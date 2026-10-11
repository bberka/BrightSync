namespace BrightSync.Cli;

public enum CliExitCode
{
    Success = 0,
    InvalidArguments = 1,
    ResidentCommandFailed = 2,
    ResidentAppRequired = 3,
    ManualCommandBlockedByAutoBrightness = 4,
    TransportFailure = 5
}
