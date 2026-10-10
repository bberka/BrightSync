using BrightSync.Core.Colors;
using BrightSync.Platform;

namespace BrightSync.Core.Monitors;

internal sealed class WindowsDisplaySettings : IDisplaySettingsService
{
    public IReadOnlyList<int> GetSupportedRefreshRates(string deviceName)
        => DisplaySettingsService.GetSupportedRefreshRates(deviceName);

    public int GetCurrentRefreshRate(string deviceName)
        => DisplaySettingsService.GetCurrentRefreshRate(deviceName);

    public bool SetRefreshRate(string deviceName, int refreshRate)
        => DisplaySettingsService.SetRefreshRate(deviceName, refreshRate);
}

internal sealed class WindowsColorProfiles : IColorProfileService
{
    public IReadOnlyList<string> GetInstalledColorProfiles()
        => ColorProfileManager.GetInstalledColorProfiles();

    public string GetActiveColorProfile(string deviceName)
        => ColorProfileManager.GetActiveColorProfile(deviceName);

    public bool SetActiveColorProfile(string deviceName, string profileName)
        => ColorProfileManager.SetActiveColorProfile(deviceName, profileName);
}
