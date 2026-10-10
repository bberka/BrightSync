using Serilog;
using Tmds.DBus.Protocol;

namespace BrightSync.Platform.Linux.DBus;

/// <summary>Writes the body of a method call after the header has been emitted.</summary>
internal delegate void WriteBody(ref MessageWriter writer);

/// <summary>Reads a typed value from a reply or signal body.</summary>
internal delegate T ReadBody<out T>(Reader reader);

/// <summary>
/// Thin synchronous facade over Tmds.DBus.Protocol for the handful of calls BrightSync makes.
/// Every call is bounded by a timeout and failures return a default value, because a missing
/// service (no logind, no power-profiles-daemon, no MPRIS player) is a normal condition.
/// </summary>
internal sealed class DBusClient(DBusConnection connection, string label)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    public static DBusClient System { get; } = new(DBusConnection.System, "system");
    public static DBusClient Session { get; } = new(DBusConnection.Session, "session");

    public DBusConnection Connection => connection;

    public bool TryCall(string destination, string path, string iface, string member)
        => TryCall(destination, path, iface, member, null, null);

    public bool TryCall(string destination, string path, string iface, string member, string? signature, WriteBody? body)
    {
        try
        {
            var message = BuildCall(destination, path, iface, member, signature, body);
            connection.CallMethodAsync(message).WaitAsync(CallTimeout).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "D-Bus {Bus} call {Interface}.{Member} on {Destination} failed", label, iface, member, destination);
            return false;
        }
    }

    public bool TryCall<T>(
        string destination,
        string path,
        string iface,
        string member,
        string? signature,
        WriteBody? body,
        ReadBody<T> read,
        out T? result)
    {
        result = default;
        try
        {
            var message = BuildCall(destination, path, iface, member, signature, body);
            var state = read;
            result = connection
                .CallMethodAsync(message, static (Message m, object? s) => ((ReadBody<T>)s!)(m.GetBodyReader()), state)
                .WaitAsync(CallTimeout)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "D-Bus {Bus} call {Interface}.{Member} on {Destination} failed", label, iface, member, destination);
            return false;
        }
    }

    /// <summary>Reads a string-valued property via <c>org.freedesktop.DBus.Properties.Get</c>.</summary>
    public string? GetStringProperty(string destination, string path, string iface, string property)
        => TryCall(
            destination,
            path,
            "org.freedesktop.DBus.Properties",
            "Get",
            "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(iface);
                w.WriteString(property);
            },
            static reader => reader.ReadVariantValue().GetString(),
            out string? value)
            ? value
            : null;

    public string[] ListNames()
        => TryCall(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus",
            "ListNames",
            null,
            null,
            static reader => reader.ReadArrayOfString(),
            out string[]? names) && names is not null
            ? names
            : [];

    /// <summary>Names of services that can be started on demand, such as power-profiles-daemon.</summary>
    public string[] ListActivatableNames()
        => TryCall(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus",
            "ListActivatableNames",
            null,
            null,
            static reader => reader.ReadArrayOfString(),
            out string[]? names) && names is not null
            ? names
            : [];

    /// <summary>Subscribes to a signal and keeps it alive until the connection closes.</summary>
    public void Subscribe(
        string sender,
        string? path,
        string iface,
        string member,
        Action onSignal)
    {
        _ = SubscribeAsync(sender, path, iface, member, onSignal);
    }

    /// <summary>Subscribes to a boolean-argument signal.</summary>
    public void SubscribeBool(
        string sender,
        string? path,
        string iface,
        string member,
        Action<bool> onSignal)
    {
        _ = SubscribeBoolAsync(sender, path, iface, member, onSignal);
    }

    private async Task SubscribeAsync(string sender, string? path, string iface, string member, Action onSignal)
    {
        try
        {
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = string.IsNullOrEmpty(sender) ? null : sender,
                Interface = iface,
                Member = member,
                Path = path
            };
            await connection.AddMatchAsync(
                rule,
                notification =>
                {
                    if (notification.Exception is null)
                        onSignal();
                },
                emitOnCapturedContext: false,
                ObserverFlags.None,
                state: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "D-Bus {Bus} subscription {Interface}.{Member} failed", label, iface, member);
        }
    }

    private async Task SubscribeBoolAsync(string sender, string? path, string iface, string member, Action<bool> onSignal)
    {
        try
        {
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = string.IsNullOrEmpty(sender) ? null : sender,
                Interface = iface,
                Member = member,
                Path = path
            };
            await connection.AddMatchAsync(
                rule,
                static (Message m, object? _) => m.GetBodyReader().ReadBool(),
                notification =>
                {
                    if (notification.Exception is null && notification.HasValue)
                        onSignal(notification.Value);
                },
                emitOnCapturedContext: false,
                ObserverFlags.None,
                state: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "D-Bus {Bus} subscription {Interface}.{Member} failed", label, iface, member);
        }
    }

    private MessageBuffer BuildCall(
        string destination,
        string path,
        string iface,
        string member,
        string? signature,
        WriteBody? body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
