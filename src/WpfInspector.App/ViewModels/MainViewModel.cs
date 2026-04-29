using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using WpfInspector.Core.Models;
using WpfInspector.Core.Services;

namespace WpfInspector.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IProcessInspectionService _processInspectionService;
    private readonly IPdbAnalysisService _pdbAnalysisService;

    private bool _isBusy;
    private string _statusMessage = "準備完了";
    private string _processFilter = string.Empty;
    private string _pdbPath = string.Empty;
    private string _assemblyPath = string.Empty;
    private ProcessSummary? _selectedProcess;
    private ProcessInspectionSnapshot? _selectedProcessSnapshot;
    private ProcessModuleInfo? _selectedModule;
    private PdbAnalysisResult? _pdbAnalysisResult;

    public MainViewModel(IProcessInspectionService processInspectionService, IPdbAnalysisService pdbAnalysisService)
    {
        _processInspectionService = processInspectionService;
        _pdbAnalysisService = pdbAnalysisService;
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcess;
    }

    public ObservableCollection<ProcessSummary> Processes { get; } = new();

    public ObservableCollection<ProcessModuleInfo> Modules { get; } = new();

    public ObservableCollection<PdbDocumentInfo> Documents { get; } = new();

    public ObservableCollection<PdbMethodInfo> Methods { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    public ICollectionView ProcessesView { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ProcessFilter
    {
        get => _processFilter;
        set
        {
            if (!SetProperty(ref _processFilter, value))
            {
                return;
            }

            ProcessesView.Refresh();
        }
    }

    public string PdbPath
    {
        get => _pdbPath;
        set => SetProperty(ref _pdbPath, value);
    }

    public string AssemblyPath
    {
        get => _assemblyPath;
        set => SetProperty(ref _assemblyPath, value);
    }

    public ProcessSummary? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public ProcessInspectionSnapshot? SelectedProcessSnapshot
    {
        get => _selectedProcessSnapshot;
        private set
        {
            if (!SetProperty(ref _selectedProcessSnapshot, value))
            {
                return;
            }

            RaiseProcessDetailsChanged();
        }
    }

    public ProcessModuleInfo? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (!SetProperty(ref _selectedModule, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedModuleSummary));
            OnPropertyChanged(nameof(CanUseSelectedModulePdb));
        }
    }

    public PdbAnalysisResult? PdbAnalysisResult
    {
        get => _pdbAnalysisResult;
        private set
        {
            if (!SetProperty(ref _pdbAnalysisResult, value))
            {
                return;
            }

            RaisePdbDetailsChanged();
        }
    }

    public string SelectedProcessHeadline =>
        SelectedProcessSnapshot is null
            ? "プロセスを選択してください"
            : $"{SelectedProcessSnapshot.Summary.ProcessName} (PID {SelectedProcessSnapshot.Summary.ProcessId})";

    public string SelectedProcessPath =>
        SelectedProcessSnapshot?.MainModulePath ?? "実行ファイルパス未取得";

    public string SelectedProcessMetrics
    {
        get
        {
            if (SelectedProcessSnapshot is null)
            {
                return "プロセス詳細はまだ読み込まれていません。";
            }

            var startTime = SelectedProcessSnapshot.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "不明";
            var workingSet = FormatBytes(SelectedProcessSnapshot.WorkingSetBytes);
            return $"起動時刻: {startTime} | ワーキングセット: {workingSet} | モジュール数: {SelectedProcessSnapshot.Modules.Count}";
        }
    }

    public string SelectedProcessNotes =>
        SelectedProcessSnapshot is null
            ? "取得可能なプロセス情報とシンボル候補を表示します。"
            : SelectedProcessSnapshot.Notes.Count == 0
                ? "追加の注意事項はありません。"
                : string.Join(Environment.NewLine, SelectedProcessSnapshot.Notes);

    public string SelectedModuleSummary =>
        SelectedModule is null
            ? "モジュールを選択すると PDB 候補を利用できます。"
            : $"{SelectedModule.ModuleName} | {SelectedModule.SymbolInfo?.SymbolState ?? "シンボル情報なし"}";

    public bool CanUseSelectedModulePdb =>
        SelectedModule?.SymbolInfo?.SuggestedPdbExists == true;

    public string PdbSummary
    {
        get
        {
            if (PdbAnalysisResult is null)
            {
                return "Portable PDB を選択すると文書とシーケンスポイントを解析します。";
            }

            return $"形式: {PdbAnalysisResult.Format} | 文書: {PdbAnalysisResult.DocumentCount} | メソッド: {PdbAnalysisResult.MethodCount} | 可視シーケンスポイント: {PdbAnalysisResult.VisibleSequencePointCount}";
        }
    }

    public string PdbWarningsText =>
        Warnings.Count == 0
            ? "警告はありません。"
            : string.Join(Environment.NewLine, Warnings);

    public async Task RefreshProcessesAsync()
    {
        var previousProcessId = SelectedProcess?.ProcessId;
        await RunBusyAsync("プロセス一覧を更新しています...", async () =>
        {
            var processes = await Task.Run(_processInspectionService.GetProcesses);
            ReplaceCollection(Processes, processes);
            ProcessesView.Refresh();

            SelectedProcess = previousProcessId is null
                ? Processes.FirstOrDefault()
                : Processes.FirstOrDefault(process => process.ProcessId == previousProcessId.Value) ?? Processes.FirstOrDefault();

            StatusMessage = $"{Processes.Count} 件のプロセスを読み込みました。";
        });
    }

    public async Task LoadSelectedProcessAsync()
    {
        if (SelectedProcess is null)
        {
            return;
        }

        await RunBusyAsync($"PID {SelectedProcess.ProcessId} を解析しています...", async () =>
        {
            var snapshot = await Task.Run(() => _processInspectionService.InspectProcess(SelectedProcess.ProcessId));
            SelectedProcessSnapshot = snapshot;
            ReplaceCollection(Modules, snapshot.Modules);
            SelectedModule = Modules.FirstOrDefault();
            StatusMessage = $"{snapshot.Summary.ProcessName} のモジュールを {snapshot.Modules.Count} 件読み込みました。";
        });
    }

    public async Task AnalyzePdbAsync()
    {
        if (string.IsNullOrWhiteSpace(PdbPath))
        {
            StatusMessage = "PDB ファイルを指定してください。";
            return;
        }

        await RunBusyAsync("PDB を解析しています...", async () =>
        {
            var result = await Task.Run(() =>
                _pdbAnalysisService.Analyze(PdbPath, string.IsNullOrWhiteSpace(AssemblyPath) ? null : AssemblyPath));

            PdbAnalysisResult = result;
            ReplaceCollection(Documents, result.Documents);
            ReplaceCollection(Methods, result.Methods);
            ReplaceCollection(Warnings, result.Warnings);
            StatusMessage = $"PDB 解析完了: 文書 {result.DocumentCount} 件 / メソッド {result.MethodCount} 件";
        });
    }

    public bool TryUseSelectedModulePdb()
    {
        if (SelectedModule?.SymbolInfo?.SuggestedPdbExists != true || string.IsNullOrWhiteSpace(SelectedModule.SymbolInfo.SuggestedPdbPath))
        {
            StatusMessage = "選択モジュールに利用可能な PDB がありません。";
            return false;
        }

        PdbPath = SelectedModule.SymbolInfo.SuggestedPdbPath;
        AssemblyPath = SelectedModule.ModulePath;
        StatusMessage = $"{SelectedModule.ModuleName} の PDB 候補を入力しました。";
        return true;
    }

    private async Task RunBusyAsync(string statusMessage, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = statusMessage;
            await action();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool FilterProcess(object item)
    {
        if (item is not ProcessSummary process)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProcessFilter))
        {
            return true;
        }

        return process.ProcessName.Contains(ProcessFilter, StringComparison.OrdinalIgnoreCase)
            || process.ProcessId.ToString().Contains(ProcessFilter, StringComparison.OrdinalIgnoreCase)
            || process.WindowTitle.Contains(ProcessFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseProcessDetailsChanged()
    {
        OnPropertyChanged(nameof(SelectedProcessHeadline));
        OnPropertyChanged(nameof(SelectedProcessPath));
        OnPropertyChanged(nameof(SelectedProcessMetrics));
        OnPropertyChanged(nameof(SelectedProcessNotes));
    }

    private void RaisePdbDetailsChanged()
    {
        OnPropertyChanged(nameof(PdbSummary));
        OnPropertyChanged(nameof(PdbWarningsText));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static string FormatBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }
}
