namespace WpfInspector.Core.Models;

public sealed record ProcessSummary(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string? ExecutablePath,
    bool IsResponding,
    bool IsCurrentProcess = false);
