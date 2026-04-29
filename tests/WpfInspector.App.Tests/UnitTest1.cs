using WpfInspector.Core.Services;

namespace WpfInspector.App.Tests;

public sealed class UnitTest1
{
    [Fact]
    public void PortablePdbAnalysisService_reads_current_test_pdb()
    {
        var assemblyPath = typeof(UnitTest1).Assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        Assert.True(File.Exists(pdbPath));

        var service = new PortablePdbAnalysisService();
        var result = service.Analyze(pdbPath!, assemblyPath);

        Assert.Equal("Portable PDB", result.Format);
        Assert.NotEmpty(result.Documents);
        Assert.True(result.MethodCount > 0);
        Assert.True(result.VisibleSequencePointCount > 0);
    }

    [Fact]
    public void PeMetadataService_reads_debug_directory_from_current_assembly()
    {
        var assemblyPath = typeof(UnitTest1).Assembly.Location;
        var service = new PeMetadataService();
        var result = service.TryReadDebugInfo(assemblyPath);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.SymbolState));
    }

    [Fact]
    public void ProcessInspectionService_finds_current_process()
    {
        var service = new ProcessInspectionService(new PeMetadataService());
        var processes = service.GetProcesses();
        Assert.Contains(processes, process => process.ProcessId == Environment.ProcessId);

        var snapshot = service.InspectProcess(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.Summary.ProcessId);
        Assert.True(snapshot.Modules.Count > 0);
    }
}
