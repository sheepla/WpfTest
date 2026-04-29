using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public interface IProcessInspectionService
{
    IReadOnlyList<ProcessSummary> GetProcesses();

    ProcessInspectionSnapshot InspectProcess(int processId);
}

