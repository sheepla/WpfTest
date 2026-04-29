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
    private readonly IWpfInjectionService _wpfInjectionService;

    private bool _isBusy;
    private string _statusMessage = "準備完了";
    private string _processFilter = string.Empty;
    private string _pdbPath = string.Empty;
    private string _assemblyPath = string.Empty;
    private ProcessSummary? _selectedProcess;
    private ProcessInspectionSnapshot? _selectedProcessSnapshot;
    private ProcessModuleInfo? _selectedModule;
    private PdbAnalysisResult? _pdbAnalysisResult;
    private WpfInspectionResult? _wpfInspectionResult;
    private WpfWindowInfo? _selectedInspectedWindow;

    public MainViewModel(
        IProcessInspectionService processInspectionService,
        IPdbAnalysisService pdbAnalysisService,
        IWpfInjectionService wpfInjectionService)
    {
        _processInspectionService = processInspectionService;
        _pdbAnalysisService = pdbAnalysisService;
        _wpfInjectionService = wpfInjectionService;
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcess;
    }

    public ObservableCollection<ProcessSummary> Processes { get; } = new();
    public ObservableCollection<ProcessModuleInfo> Modules { get; } = new();
    public ObservableCollection<PdbDocumentInfo> Documents { get; } = new();
    public ObservableCollection<PdbMethodInfo> Methods { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<WpfWindowInfo> InspectedWindows { get; } = new();

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

    public bool IsWpfProcess => SelectedProcessSnapshot?.IsWpfProcess == true;

    public bool CanAnalyzeSelectedWpf => IsWpfProcess && !IsBusy;

    public WpfInspectionResult? WpfInspectionResult
    {
        get => _wpfInspectionResult;
        private set
        {
            if (!SetProperty(ref _wpfInspectionResult, value)) return;
            OnPropertyChanged(nameof(WpfInspectionSummary));
            OnPropertyChanged(nameof(HasInspectionResult));
            OnPropertyChanged(nameof(UiResultSummary));
        }
    }

    public WpfWindowInfo? SelectedInspectedWindow
    {
        get => _selectedInspectedWindow;
        set
        {
            if (!SetProperty(ref _selectedInspectedWindow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedInspectedWindow));
            OnPropertyChanged(nameof(SelectedInspectedWindowSummary));
            OnPropertyChanged(nameof(SelectedInspectedWindowRoots));
        }
    }

    public bool HasInspectionResult => _wpfInspectionResult is not null;

    public bool HasSelectedInspectedWindow => _selectedInspectedWindow is not null;

    public string WpfInspectionSummary
    {
        get
        {
            if (_wpfInspectionResult is not null)
            {
                return $"WPF {_wpfInspectionResult.WpfVersion} | ウィンドウ: {_wpfInspectionResult.Windows.Count} 件 | Dispatcher: {_wpfInspectionResult.DispatcherState}";
            }

            if (SelectedProcessSnapshot is null)
            {
                return "WPF プロセスを選択すると一括解析を実行できます。";
            }

            if (!SelectedProcessSnapshot.IsWpfProcess)
            {
                return "WPF プロセスを選択してください。";
            }

            if (SelectedProcessSnapshot.Summary.IsCurrentProcess)
            {
                return WpfInspectionResult is null
                    ? "WpfInspector 自身を直接走査して、ランタイムの UI アセンブリ候補を確定できます。"
                    : "自己プロセスの走査結果を使って、実際に使われているアセンブリを優先して PDB を解析しています。";
            }

            return WpfInspectionResult is null
                ? "選択した WPF を走査して、ランタイムの UI アセンブリ候補を確定できます。"
                : "走査結果を使って、実際に使われているアセンブリを優先して PDB を解析しています。";
        }
    }

    public string SelectedProcessHeadline =>
        SelectedProcessSnapshot is null
            ? "プロセスを選択してください"
            : SelectedProcessSnapshot.Summary.IsCurrentProcess
                ? $"{SelectedProcessSnapshot.Summary.ProcessName} (PID {SelectedProcessSnapshot.Summary.ProcessId}) | WpfInspector自身"
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
            : BuildSelectedProcessNotes();

    public string SelectedProcessPdbSummary
    {
        get
        {
            if (SelectedProcessSnapshot is null)
            {
                return "WPF プロセスを選択するとメインモジュール PDB を確認します。";
            }

            if (!SelectedProcessSnapshot.IsWpfProcess)
            {
                return "選択中プロセスは WPF ではありません。";
            }

            var resolvedRuntimeModule = GetResolvedAnalysisModule();
            var preferredRuntimeAssembly = GetPreferredRuntimeAssembly();
            if (preferredRuntimeAssembly is not null)
            {
                if (resolvedRuntimeModule?.SymbolInfo?.SuggestedPdbExists == true
                    && !string.IsNullOrWhiteSpace(resolvedRuntimeModule.SymbolInfo.SuggestedPdbPath))
                {
                    return $"走査後の解析対象: {resolvedRuntimeModule.ModuleName} ({preferredRuntimeAssembly.RepresentativeTypeName}) -> {resolvedRuntimeModule.SymbolInfo.SuggestedPdbPath}";
                }

                if (!string.IsNullOrWhiteSpace(resolvedRuntimeModule?.SymbolInfo?.SuggestedPdbPath))
                {
                    return $"走査後の解析対象: {resolvedRuntimeModule.ModuleName} ({preferredRuntimeAssembly.RepresentativeTypeName}) -> {resolvedRuntimeModule.SymbolInfo.SuggestedPdbPath} (未検出)";
                }

                return $"走査後の解析対象: {preferredRuntimeAssembly.AssemblyName} ({preferredRuntimeAssembly.RepresentativeTypeName})";
            }

            var initialAnalysisModule = SelectedProcessSnapshot.ResolvePreferredInitialAnalysisModule();
            if (initialAnalysisModule is null)
            {
                return "解析対象モジュール情報を取得できませんでした。";
            }

            var symbolInfo = initialAnalysisModule.SymbolInfo;
            if (symbolInfo is null)
            {
                return $"{initialAnalysisModule.ModuleName} のシンボル情報を取得できませんでした。";
            }

            if (symbolInfo.SuggestedPdbExists && !string.IsNullOrWhiteSpace(symbolInfo.SuggestedPdbPath))
            {
                return $"対象 PDB: {symbolInfo.SuggestedPdbPath}";
            }

            if (!string.IsNullOrWhiteSpace(symbolInfo.SuggestedPdbPath))
            {
                return $"対象 PDB 候補: {symbolInfo.SuggestedPdbPath} (未検出)";
            }

            return $"{initialAnalysisModule.ModuleName} に利用可能な PDB 候補はありません。";
        }
    }

    public string SelectedInspectedWindowSummary
    {
        get
        {
            if (_selectedInspectedWindow is null)
            {
                return HasInspectionResult
                    ? "ウィンドウを選択するとルート要素から UI ツリーを表示します。"
                    : "UI 走査を実行すると検出したウィンドウをここに表示します。";
            }

            return $"{_selectedInspectedWindow.Title} | {_selectedInspectedWindow.WindowState} | {_selectedInspectedWindow.Width:N0} x {_selectedInspectedWindow.Height:N0}";
        }
    }

    public IReadOnlyList<WpfElementInfo> SelectedInspectedWindowRoots =>
        _selectedInspectedWindow?.RootElement is { } rootElement
            ? [rootElement]
            : [];

    public string RuntimeAssemblySummary
    {
        get
        {
            if (WpfInspectionResult is null)
            {
                return "走査後に、ビジュアルツリーと DataContext から実際に使われているアセンブリを収集します。";
            }

            if (WpfInspectionResult.RuntimeAssemblies.Count == 0)
            {
                return "ランタイムで特定できたアセンブリ候補はありません。";
            }

            return string.Join(", ", WpfInspectionResult.RuntimeAssemblies
                .Take(4)
                .Select(assembly => $"{assembly.AssemblyName} [{assembly.DiscoverySource}]"));
        }
    }

    public string PdbResultSummary =>
        PdbAnalysisResult is null
            ? "PDB 未解析"
            : $"{PdbAnalysisResult.DocumentCount} 文書 / {PdbAnalysisResult.MethodCount} メソッド / 可視 {PdbAnalysisResult.VisibleSequencePointCount}";

    public string UiResultSummary =>
        WpfInspectionResult is null
            ? "UI 未走査"
            : $"{WpfInspectionResult.Windows.Count} ウィンドウ / Dispatcher {WpfInspectionResult.DispatcherState}";

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
                return "走査後に解決したランタイム対象の PDB を解析すると結果を表示します。";
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
                ? GetDefaultSelectedProcess()
                : Processes.FirstOrDefault(process => process.ProcessId == previousProcessId.Value) ?? GetDefaultSelectedProcess();

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
            SelectedModule = snapshot.ResolvePreferredInitialAnalysisModule() ?? snapshot.MainModule ?? Modules.FirstOrDefault();
            ApplySelectedProcessPdbCandidate(snapshot);
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

            ApplyPdbAnalysisResult(result);
            StatusMessage = $"PDB 解析完了: 文書 {result.DocumentCount} 件 / メソッド {result.MethodCount} 件";
        });
    }

    public async Task AnalyzeSelectedWpfAsync()
    {
        if (SelectedProcess is null)
        {
            StatusMessage = "プロセスを選択してください。";
            return;
        }

        if (!IsWpfProcess)
        {
            StatusMessage = "WPF プロセスを選択してください。";
            return;
        }

        if (SelectedProcessSnapshot is null)
        {
            StatusMessage = "選択中プロセスの詳細を読み込めませんでした。";
            return;
        }

        var inspectionModeMessage = SelectedProcessSnapshot.Summary.IsCurrentProcess
            ? "WpfInspector 自身を走査して、ランタイム情報から PDB 解析対象を決定しています..."
            : $"PID {SelectedProcess.ProcessId} を走査して、ランタイム情報から PDB 解析対象を決定しています...";

        await RunBusyAsync(inspectionModeMessage, async () =>
        {
            var snapshot = SelectedProcessSnapshot!;
            var inspectionResult = await _wpfInjectionService.InspectAsync(SelectedProcess.ProcessId);
            ApplyInspectionResult(inspectionResult);

            var analysisModule = snapshot.ResolvePreferredWpfAnalysisModule(inspectionResult);
            if (analysisModule is not null)
            {
                SelectedModule = analysisModule;
                AssemblyPath = analysisModule.ModulePath;
                PdbPath = analysisModule.SymbolInfo?.SuggestedPdbPath ?? string.Empty;
            }
            else
            {
                AssemblyPath = snapshot.ResolvePreferredInitialAnalysisModule()?.ModulePath ?? snapshot.MainModulePath ?? string.Empty;
                PdbPath = string.Empty;
            }

            OnPropertyChanged(nameof(SelectedProcessPdbSummary));
            OnPropertyChanged(nameof(RuntimeAssemblySummary));

            string? pdbFailure = null;
            if (analysisModule?.SymbolInfo?.SuggestedPdbExists == true
                && !string.IsNullOrWhiteSpace(analysisModule.SymbolInfo.SuggestedPdbPath))
            {
                try
                {
                    var pdbResult = await Task.Run(() =>
                        _pdbAnalysisService.Analyze(
                            analysisModule.SymbolInfo.SuggestedPdbPath,
                            string.IsNullOrWhiteSpace(analysisModule.ModulePath) ? null : analysisModule.ModulePath));
                    ApplyPdbAnalysisResult(pdbResult);
                }
                catch (Exception exception)
                {
                    pdbFailure = exception.Message;
                    ClearPdbAnalysis($"走査後に解決した PDB の解析に失敗しました: {exception.Message}");
                }
            }
            else
            {
                pdbFailure = analysisModule is null
                    ? "ランタイム候補から解析対象モジュールを特定できませんでした。"
                    : $"解析対象 {analysisModule.ModuleName} に利用可能な PDB が見つかりません。";
                ClearPdbAnalysis(pdbFailure);
            }

            StatusMessage = pdbFailure is null
                ? $"UI 走査と PDB 解析が完了しました: ウィンドウ {inspectionResult.Windows.Count} 件 / 文書 {Documents.Count} 件"
                : $"UI 走査は完了しましたが、PDB 解析は未完了です: {pdbFailure}";
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
            OnPropertyChanged(nameof(CanAnalyzeSelectedWpf));
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
            OnPropertyChanged(nameof(CanAnalyzeSelectedWpf));
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
        OnPropertyChanged(nameof(SelectedProcessPdbSummary));
        OnPropertyChanged(nameof(IsWpfProcess));
        OnPropertyChanged(nameof(CanAnalyzeSelectedWpf));
        OnPropertyChanged(nameof(RuntimeAssemblySummary));
        OnPropertyChanged(nameof(WpfInspectionSummary));
        WpfInspectionResult = null;
        InspectedWindows.Clear();
        SelectedInspectedWindow = null;
        ClearPdbAnalysis();
    }

    private void RaisePdbDetailsChanged()
    {
        OnPropertyChanged(nameof(PdbSummary));
        OnPropertyChanged(nameof(PdbWarningsText));
        OnPropertyChanged(nameof(PdbResultSummary));
    }

    private void ApplySelectedProcessPdbCandidate(ProcessInspectionSnapshot snapshot)
    {
        var preferredModule = snapshot.ResolvePreferredInitialAnalysisModule();
        AssemblyPath = preferredModule?.ModulePath ?? snapshot.MainModulePath ?? string.Empty;
        PdbPath = preferredModule?.SymbolInfo?.SuggestedPdbExists == true
            ? preferredModule.SymbolInfo.SuggestedPdbPath ?? string.Empty
            : string.Empty;
        OnPropertyChanged(nameof(SelectedProcessPdbSummary));
        OnPropertyChanged(nameof(CanAnalyzeSelectedWpf));
        OnPropertyChanged(nameof(RuntimeAssemblySummary));
    }

    private void ApplyPdbAnalysisResult(PdbAnalysisResult result)
    {
        PdbAnalysisResult = result;
        ReplaceCollection(Documents, result.Documents);
        ReplaceCollection(Methods, result.Methods);
        ReplaceCollection(Warnings, result.Warnings);
        OnPropertyChanged(nameof(PdbWarningsText));
    }

    private void ApplyInspectionResult(WpfInspectionResult result)
    {
        WpfInspectionResult = result;
        ReplaceCollection(InspectedWindows, result.Windows);
        SelectedInspectedWindow = InspectedWindows.FirstOrDefault();
        var resolvedModule = GetResolvedAnalysisModule();
        if (resolvedModule is not null)
        {
            SelectedModule = resolvedModule;
        }
        OnPropertyChanged(nameof(SelectedProcessPdbSummary));
        OnPropertyChanged(nameof(RuntimeAssemblySummary));
        OnPropertyChanged(nameof(UiResultSummary));
    }

    private void ClearPdbAnalysis(string? warning = null)
    {
        PdbAnalysisResult = null;
        Documents.Clear();
        Methods.Clear();

        Warnings.Clear();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            Warnings.Add(warning);
        }

        OnPropertyChanged(nameof(PdbWarningsText));
    }

    private ProcessModuleInfo? GetResolvedAnalysisModule() =>
        SelectedProcessSnapshot?.ResolvePreferredWpfAnalysisModule(WpfInspectionResult);

    private WpfRuntimeAssemblyInfo? GetPreferredRuntimeAssembly() =>
        WpfInspectionResult?.RuntimeAssemblies.FirstOrDefault();

    private ProcessSummary? GetDefaultSelectedProcess() =>
        Processes.FirstOrDefault(process => process.IsCurrentProcess) ?? Processes.FirstOrDefault();

    private string BuildSelectedProcessNotes()
    {
        if (SelectedProcessSnapshot is null)
        {
            return "取得可能なプロセス情報とシンボル候補を表示します。";
        }

        var notes = new List<string>();
        if (SelectedProcessSnapshot.Summary.IsCurrentProcess)
        {
            notes.Add("このプロセスは WpfInspector 自身です。UI 走査は DLL インジェクションではなく、アプリ内で直接実行します。");
        }

        if (SelectedProcessSnapshot.Notes.Count == 0)
        {
            notes.Add("追加の注意事項はありません。");
        }
        else
        {
            notes.AddRange(SelectedProcessSnapshot.Notes);
        }

        return string.Join(Environment.NewLine, notes);
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
