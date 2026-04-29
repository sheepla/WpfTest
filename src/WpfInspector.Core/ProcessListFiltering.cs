using WpfInspector.Core.Models;

namespace WpfInspector.Core;

public static class ProcessListFiltering
{
    public static bool Matches(ProcessSummary process, string? filterText, bool includeBackgroundProcesses)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!includeBackgroundProcesses && !IsUserFacing(process))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        return Contains(process.ProcessName, filterText)
            || process.ProcessId.ToString().Contains(filterText, StringComparison.OrdinalIgnoreCase)
            || Contains(process.WindowTitle, filterText)
            || Contains(process.ExecutablePath, filterText);
    }

    public static bool IsUserFacing(ProcessSummary process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.IsCurrentProcess || !string.IsNullOrWhiteSpace(process.WindowTitle);
    }

    private static bool Contains(string? value, string filterText) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);
}
