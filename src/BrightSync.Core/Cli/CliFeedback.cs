using BrightSync.Platform;

namespace BrightSync.Cli;

public sealed class CliFeedback
{
    private bool _consoleAttached;

    public void AttachToParentConsole()
    {
        if (_consoleAttached)
            return;

        _consoleAttached = PlatformServices.Current.Shell.TryAttachParentConsole();
    }

    public void WriteInfo(string message)
    {
        if (!_consoleAttached)
            return;

        Console.Out.WriteLine(message);
    }

    public void WriteError(string message)
    {
        if (!_consoleAttached)
            return;

        Console.Error.WriteLine(message);
    }
}
