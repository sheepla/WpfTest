using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public interface IWpfInjectionService
{
    Task<WpfInspectionResult> InspectAsync(int processId);
}
