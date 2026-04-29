using System.Diagnostics;
using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public sealed class ProcessInspectionService : IProcessInspectionService
{
    private readonly IPeMetadataService _peMetadataService;

    public ProcessInspectionService(IPeMetadataService peMetadataService)
    {
        _peMetadataService = peMetadataService;
    }

    public IReadOnlyList<ProcessSummary> GetProcesses()
    {
        var summaries = new List<ProcessSummary>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                summaries.Add(new ProcessSummary(
                    ProcessId: SafeGet(() => process.Id, -1),
                    ProcessName: SafeGet(() => process.ProcessName, "<unknown>"),
                    WindowTitle: SafeGet(() => process.MainWindowTitle, string.Empty),
                    ExecutablePath: SafeGetNullable(() => process.MainModule?.FileName),
                    IsResponding: SafeGet(() => process.Responding, false),
                    IsCurrentProcess: SafeGet(() => process.Id, -1) == Environment.ProcessId));
            }
        }

        return summaries
            .Where(summary => summary.ProcessId >= 0)
            .OrderBy(summary => summary.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.ProcessId)
            .ToList();
    }

    public ProcessInspectionSnapshot InspectProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var notes = new List<string>();

        var summary = new ProcessSummary(
            ProcessId: SafeGet(() => process.Id, processId),
            ProcessName: SafeGet(() => process.ProcessName, "<unknown>"),
            WindowTitle: SafeGet(() => process.MainWindowTitle, string.Empty),
            ExecutablePath: SafeGetNullable(() => process.MainModule?.FileName),
            IsResponding: SafeGet(() => process.Responding, false),
            IsCurrentProcess: SafeGet(() => process.Id, processId) == Environment.ProcessId);

        var mainModulePath = SafeGetNullable(() => process.MainModule?.FileName);
        if (string.IsNullOrWhiteSpace(mainModulePath))
        {
            notes.Add("実行ファイルパスを取得できませんでした。権限不足または対象プロセス終了の可能性があります。");
        }

        var modules = ReadModules(process, notes);

        AppendDiagnosticNotes(modules, mainModulePath, notes);

        return new ProcessInspectionSnapshot(
            Summary: summary,
            MainModulePath: mainModulePath,
            StartTime: SafeGetNullable(() => process.StartTime),
            WorkingSetBytes: SafeGet(() => process.WorkingSet64, 0L),
            Modules: modules,
            Notes: notes);
    }

    private static void AppendDiagnosticNotes(IReadOnlyList<ProcessModuleInfo> modules, string? mainModulePath, List<string> notes)
    {
        var wpfRuntimeModule = WpfModuleDetection.FindRuntimeModule(modules);

        if (wpfRuntimeModule is not null)
        {
            var version = SafeGetNullable(() =>
                FileVersionInfo.GetVersionInfo(wpfRuntimeModule.ModulePath).ProductVersion);
            notes.Add(version is not null
                ? $"WPF プロセス: {wpfRuntimeModule.ModuleName} {version}"
                : $"WPF プロセス: {wpfRuntimeModule.ModuleName}");
        }

        var mainModuleInfo = modules.FirstOrDefault(m =>
            string.Equals(m.ModulePath, mainModulePath, StringComparison.OrdinalIgnoreCase));
        if (mainModuleInfo?.SymbolInfo?.Architecture is { } arch)
        {
            notes.Add($"アーキテクチャ: {arch}");
        }
    }

    private IReadOnlyList<ProcessModuleInfo> ReadModules(Process process, List<string> notes)
    {
        var modules = new List<ProcessModuleInfo>();

        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var modulePath = SafeGetNullable(() => module.FileName);
                if (string.IsNullOrWhiteSpace(modulePath))
                {
                    continue;
                }

                modules.Add(new ProcessModuleInfo(
                    ModuleName: SafeGet(() => module.ModuleName, Path.GetFileName(modulePath)),
                    ModulePath: modulePath,
                    BaseAddress: SafeGet(() => module.BaseAddress.ToInt64(), 0L),
                    ModuleMemorySize: SafeGet(() => module.ModuleMemorySize, 0),
                    SymbolInfo: _peMetadataService.TryReadDebugInfo(modulePath)));
            }
        }
        catch
        {
            notes.Add("モジュール列挙に失敗しました。管理者権限が必要なプロセスの可能性があります。");
        }

        return modules
            .OrderBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static T? SafeGetNullable<T>(Func<T?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }
}
