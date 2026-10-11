namespace BrightSync.Core.Monitors;

internal sealed record MonitorDetectionInfo(
    string ManufacturerName,
    string ModelName,
    string FriendlyName,
    string ConnectionType,
    bool IsInternal,
    HdrDisplayInfo HdrInfo,
    string StableIdentity,
    string DetectionBackend,
    string DetectionDetails);
