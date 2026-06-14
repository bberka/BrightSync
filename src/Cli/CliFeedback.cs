using System.Runtime.InteropServices;

namespace BrightSync.Cli;

public sealed class CliFeedback
{
    private bool _consoleAttached;

    public void AttachToParentConsole()
    {
        if (_consoleAttached)
            return;

        _consoleAttached = AttachConsole(AttachParentProcess);
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

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
}
