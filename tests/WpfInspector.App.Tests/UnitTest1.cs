using WpfInspector.Core.Models;
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
    public void PeMetadataService_reads_architecture_from_current_assembly()
    {
        var assemblyPath = typeof(UnitTest1).Assembly.Location;
        var service = new PeMetadataService();
        var result = service.TryReadDebugInfo(assemblyPath);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Architecture));
        Assert.True(result.Architecture is "x64" or "x86");
    }

    [Fact]
    public void ProcessInspectionService_finds_current_process()
    {
        var service = new ProcessInspectionService(new PeMetadataService());
        var processes = service.GetProcesses();
        Assert.Contains(processes, process => process.ProcessId == Environment.ProcessId && process.IsCurrentProcess);

        var snapshot = service.InspectProcess(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.Summary.ProcessId);
        Assert.True(snapshot.Summary.IsCurrentProcess);
        Assert.True(snapshot.Modules.Count > 0);
    }

    [Fact]
    public void ProcessInspectionService_adds_architecture_note_for_current_process()
    {
        var service = new ProcessInspectionService(new PeMetadataService());
        var snapshot = service.InspectProcess(Environment.ProcessId);

        Assert.Contains(snapshot.Notes, note => note.StartsWith("アーキテクチャ:"));
    }

    [Fact]
    public void ProcessInspectionSnapshot_IsWpfProcess_false_when_no_wpf_modules()
    {
        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "test", string.Empty, null, true),
            MainModulePath: null,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [],
            Notes: []);

        Assert.False(snapshot.IsWpfProcess);
    }

    [Fact]
    public void ProcessInspectionSnapshot_IsWpfProcess_true_when_presentation_framework_present()
    {
        var module = new ProcessModuleInfo(
            ModuleName: "PresentationFramework.dll",
            ModulePath: @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\PresentationFramework.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "wpfapp", string.Empty, null, true),
            MainModulePath: null,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [module],
            Notes: []);

        Assert.True(snapshot.IsWpfProcess);
    }

    [Fact]
    public void ProcessInspectionSnapshot_IsWpfProcess_is_case_insensitive()
    {
        var module = new ProcessModuleInfo(
            ModuleName: "presentationframework.dll",
            ModulePath: @"C:\fake\presentationframework.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "test", string.Empty, null, true),
            MainModulePath: null,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [module],
            Notes: []);

        Assert.True(snapshot.IsWpfProcess);
    }

    [Fact]
    public void ProcessInspectionSnapshot_IsWpfProcess_true_when_presentation_framework_ni_present()
    {
        var module = new ProcessModuleInfo(
            ModuleName: "PresentationFramework.ni.dll",
            ModulePath: @"C:\Windows\assembly\NativeImages_v4.0.30319_64\PresentationFramework.ni.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "wpfapp", string.Empty, null, true),
            MainModulePath: null,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [module],
            Notes: []);

        Assert.True(snapshot.IsWpfProcess);
    }

    [Fact]
    public void ProcessInspectionSnapshot_IsWpfProcess_true_when_presentation_core_present()
    {
        var module = new ProcessModuleInfo(
            ModuleName: "PresentationCore.dll",
            ModulePath: @"C:\Program Files\dotnet\shared\PresentationCore.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "wpfapp", string.Empty, null, true),
            MainModulePath: null,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [module],
            Notes: []);

        Assert.True(snapshot.IsWpfProcess);
    }

    [Fact]
    public void ProcessInspectionSnapshot_MainModule_returns_module_matching_main_path()
    {
        var mainModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.exe",
            ModulePath: @"C:\apps\SampleApp.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);
        var supportModule = new ProcessModuleInfo(
            ModuleName: "PresentationFramework.dll",
            ModulePath: @"C:\windows\PresentationFramework.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: null);

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "SampleApp", string.Empty, mainModule.ModulePath, true),
            MainModulePath: mainModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [supportModule, mainModule],
            Notes: []);

        Assert.Equal(mainModule, snapshot.MainModule);
    }

    [Fact]
    public void ProcessInspectionSnapshot_HasMainModuleSuggestedPdb_true_when_main_module_pdb_exists()
    {
        var mainModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.exe",
            ModulePath: @"C:\apps\SampleApp.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: new PeSymbolInfo(
                OriginalPdbPath: @"C:\src\SampleApp.pdb",
                SuggestedPdbPath: @"C:\apps\SampleApp.pdb",
                SuggestedPdbExists: true,
                Signature: null,
                Age: null,
                HasEmbeddedPortablePdb: false,
                SymbolState: "ローカル PDB 検出",
                Architecture: "x64"));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "SampleApp", string.Empty, mainModule.ModulePath, true),
            MainModulePath: mainModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [mainModule],
            Notes: []);

        Assert.True(snapshot.HasMainModuleSuggestedPdb);
        Assert.Equal(@"C:\apps\SampleApp.pdb", snapshot.MainModuleSuggestedPdbPath);
    }

    [Fact]
    public void ProcessInspectionSnapshot_HasMainModuleSuggestedPdb_false_when_only_non_main_module_has_pdb()
    {
        var mainModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.exe",
            ModulePath: @"C:\apps\SampleApp.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: new PeSymbolInfo(
                OriginalPdbPath: null,
                SuggestedPdbPath: null,
                SuggestedPdbExists: false,
                Signature: null,
                Age: null,
                HasEmbeddedPortablePdb: false,
                SymbolState: "シンボル参照なし",
                Architecture: "x64"));
        var dependencyModule = new ProcessModuleInfo(
            ModuleName: "Dependency.dll",
            ModulePath: @"C:\apps\Dependency.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: new PeSymbolInfo(
                OriginalPdbPath: @"C:\src\Dependency.pdb",
                SuggestedPdbPath: @"C:\apps\Dependency.pdb",
                SuggestedPdbExists: true,
                Signature: null,
                Age: null,
                HasEmbeddedPortablePdb: false,
                SymbolState: "ローカル PDB 検出",
                Architecture: "x64"));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "SampleApp", string.Empty, mainModule.ModulePath, true),
            MainModulePath: mainModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [mainModule, dependencyModule],
            Notes: []);

        Assert.False(snapshot.HasMainModuleSuggestedPdb);
        Assert.Null(snapshot.MainModuleSuggestedPdbPath);
    }

    [Fact]
    public void ProcessInspectionSnapshot_ResolvePreferredWpfAnalysisModule_prefers_runtime_application_module_with_pdb()
    {
        var mainModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.exe",
            ModulePath: @"C:\apps\SampleApp.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\SampleApp.pdb", suggestedPdbExists: false));
        var uiModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.Views.dll",
            ModulePath: @"C:\apps\SampleApp.Views.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\SampleApp.Views.pdb", suggestedPdbExists: true));
        var frameworkModule = new ProcessModuleInfo(
            ModuleName: "PresentationFramework.dll",
            ModulePath: @"C:\Program Files\dotnet\shared\PresentationFramework.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\Program Files\dotnet\shared\PresentationFramework.pdb", suggestedPdbExists: true));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "SampleApp", string.Empty, mainModule.ModulePath, true),
            MainModulePath: mainModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [mainModule, uiModule, frameworkModule],
            Notes: []);

        var inspectionResult = new WpfInspectionResult(
            ProcessId: 1,
            WpfVersion: "9.0.0",
            DispatcherState: "Running",
            RuntimeAssemblies:
            [
                new WpfRuntimeAssemblyInfo(
                    AssemblyName: "PresentationFramework",
                    AssemblyPath: frameworkModule.ModulePath,
                    AssemblyVersion: "9.0.0",
                    RepresentativeTypeName: "System.Windows.Window",
                    DiscoverySource: "Window",
                    IsDynamic: false,
                    IsInAppDirectory: false),
                new WpfRuntimeAssemblyInfo(
                    AssemblyName: "SampleApp.Views",
                    AssemblyPath: uiModule.ModulePath,
                    AssemblyVersion: "1.0.0",
                    RepresentativeTypeName: "SampleApp.Views.MainWindow",
                    DiscoverySource: "Visual",
                    IsDynamic: false,
                    IsInAppDirectory: true)
            ],
            Windows: []);

        var resolvedModule = snapshot.ResolvePreferredWpfAnalysisModule(inspectionResult);

        Assert.Equal(uiModule, resolvedModule);
    }

    [Fact]
    public void ProcessInspectionSnapshot_ResolvePreferredWpfAnalysisModule_falls_back_to_main_module_when_runtime_candidates_have_no_pdb()
    {
        var mainModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.exe",
            ModulePath: @"C:\apps\SampleApp.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\SampleApp.pdb", suggestedPdbExists: true));
        var dataModule = new ProcessModuleInfo(
            ModuleName: "SampleApp.Dynamic.dll",
            ModulePath: @"C:\apps\SampleApp.Dynamic.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\SampleApp.Dynamic.pdb", suggestedPdbExists: false));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "SampleApp", string.Empty, mainModule.ModulePath, true),
            MainModulePath: mainModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [mainModule, dataModule],
            Notes: []);

        var inspectionResult = new WpfInspectionResult(
            ProcessId: 1,
            WpfVersion: "9.0.0",
            DispatcherState: "Running",
            RuntimeAssemblies:
            [
                new WpfRuntimeAssemblyInfo(
                    AssemblyName: "SampleApp.Dynamic",
                    AssemblyPath: dataModule.ModulePath,
                    AssemblyVersion: "1.0.0",
                    RepresentativeTypeName: "SampleApp.Dynamic.SomeViewModel",
                    DiscoverySource: "DataContext",
                    IsDynamic: false,
                    IsInAppDirectory: true)
            ],
            Windows: []);

        var resolvedModule = snapshot.ResolvePreferredWpfAnalysisModule(inspectionResult);

        Assert.Equal(mainModule, resolvedModule);
    }

    [Fact]
    public void ProcessInspectionSnapshot_ResolvePreferredInitialAnalysisModule_prefers_sibling_dll_when_main_module_is_apphost()
    {
        var appHostModule = new ProcessModuleInfo(
            ModuleName: "WpfInspector.App.exe",
            ModulePath: @"C:\apps\WpfInspector.App.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: new PeSymbolInfo(
                OriginalPdbPath: @"C:\Program Files\dotnet\sdk\9.0.300\apphost.pdb",
                SuggestedPdbPath: @"C:\apps\apphost.pdb",
                SuggestedPdbExists: false,
                Signature: null,
                Age: null,
                HasEmbeddedPortablePdb: false,
                SymbolState: "PDB 参照あり",
                Architecture: "x64"));
        var managedModule = new ProcessModuleInfo(
            ModuleName: "WpfInspector.App.dll",
            ModulePath: @"C:\apps\WpfInspector.App.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\WpfInspector.App.pdb", suggestedPdbExists: true));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "WpfInspector.App", string.Empty, appHostModule.ModulePath, true, true),
            MainModulePath: appHostModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [appHostModule, managedModule],
            Notes: []);

        var resolvedModule = snapshot.ResolvePreferredInitialAnalysisModule();

        Assert.Equal(managedModule, resolvedModule);
    }

    [Fact]
    public void ProcessInspectionSnapshot_ResolvePreferredWpfAnalysisModule_falls_back_to_preferred_initial_module_when_runtime_candidates_missing()
    {
        var appHostModule = new ProcessModuleInfo(
            ModuleName: "WpfInspector.App.exe",
            ModulePath: @"C:\apps\WpfInspector.App.exe",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: new PeSymbolInfo(
                OriginalPdbPath: @"C:\Program Files\dotnet\sdk\9.0.300\apphost.pdb",
                SuggestedPdbPath: @"C:\apps\apphost.pdb",
                SuggestedPdbExists: false,
                Signature: null,
                Age: null,
                HasEmbeddedPortablePdb: false,
                SymbolState: "PDB 参照あり",
                Architecture: "x64"));
        var managedModule = new ProcessModuleInfo(
            ModuleName: "WpfInspector.App.dll",
            ModulePath: @"C:\apps\WpfInspector.App.dll",
            BaseAddress: 0,
            ModuleMemorySize: 0,
            SymbolInfo: CreateSymbolInfo(@"C:\apps\WpfInspector.App.pdb", suggestedPdbExists: true));

        var snapshot = new ProcessInspectionSnapshot(
            Summary: new ProcessSummary(1, "WpfInspector.App", string.Empty, appHostModule.ModulePath, true, true),
            MainModulePath: appHostModule.ModulePath,
            StartTime: null,
            WorkingSetBytes: 0,
            Modules: [appHostModule, managedModule],
            Notes: []);

        var resolvedModule = snapshot.ResolvePreferredWpfAnalysisModule(
            new WpfInspectionResult(
                ProcessId: 1,
                WpfVersion: "9.0.0",
                DispatcherState: "Running",
                RuntimeAssemblies: [],
                Windows: []));

        Assert.Equal(managedModule, resolvedModule);
    }

    private static PeSymbolInfo CreateSymbolInfo(string suggestedPdbPath, bool suggestedPdbExists) =>
        new(
            OriginalPdbPath: suggestedPdbPath,
            SuggestedPdbPath: suggestedPdbPath,
            SuggestedPdbExists: suggestedPdbExists,
            Signature: null,
            Age: null,
            HasEmbeddedPortablePdb: false,
            SymbolState: suggestedPdbExists ? "ローカル PDB 検出" : "PDB 参照あり",
            Architecture: "x64");
}
