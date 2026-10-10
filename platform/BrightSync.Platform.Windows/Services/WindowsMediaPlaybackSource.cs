using BrightSync.Platform;
using Serilog;
using Windows.Media.Control;

namespace BrightSync.Core.Services;

/// <summary>Detects active media playback through the Windows System Media Transport Controls.</summary>
internal sealed class WindowsMediaPlaybackSource : IMediaPlaybackSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private bool _mediaManagerInitialized;
    private readonly object _mediaLock = new();

    public bool IsMediaPlaying()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return false;

        lock (_mediaLock)
        {
            if (!_mediaManagerInitialized)
            {
                _mediaManagerInitialized = true;
                Task.Run(async () =>
                {
                    try
                    {
                        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        lock (_mediaLock)
                        {
                            _mediaManager = manager;
                        }

                        Log.Information("GlobalSystemMediaTransportControlsSessionManager initialized successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to request GlobalSystemMediaTransportControlsSessionManager");
                    }
                });
            }
        }

        GlobalSystemMediaTransportControlsSessionManager? managerToUse;
        lock (_mediaLock)
        {
            managerToUse = _mediaManager;
        }

        if (managerToUse == null)
            return false;

        try
        {
            var sessions = managerToUse.GetSessions();
            if (sessions != null)
            {
                foreach (var session in sessions)
                {
                    var playbackInfo = session.GetPlaybackInfo();
                    if (playbackInfo != null &&
                        playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        Log.Debug("Active media session is playing.");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to query active media session playback status");
        }

        return false;
    }
}
