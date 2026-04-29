using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public interface IPeMetadataService
{
    PeSymbolInfo? TryReadDebugInfo(string modulePath);
}

