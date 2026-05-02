using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace WpfInspector.App.ViewModels;

public sealed class FixtureLabViewModel : ObservableObject
{
    private const int DispatcherTimeoutThresholdMs = 400;

    private readonly AsyncDelegateCommand _runAsyncCommand;
    private readonly AsyncDelegateCommand _simulateDispatcherBusyCommand;

    private ResourcePalette _activePalette = ResourcePalette.Ocean;
    private string _asyncCommandStatus = "未実行";
    private double _asyncCommandProgress;
    private string _dispatcherBusyStatus = "未実行";
    private string _dispatcherLagSummary = "未計測";
    private string _dispatcherTimeoutSummary = "未発生";
    private bool _isAsyncCommandRunning;
    private bool _isContextMenuOpen;
    private bool _isPopupOpen = true;
    private bool _isTemplateActive;
    private DateTimeOffset _lastPriorityRefresh = DateTimeOffset.Now;
    private int _priorityVersion = 1;
    private FixtureVirtualItem? _selectedVirtualizedItem;
    private string _validatedToken = "WPF-42";

    public FixtureLabViewModel()
    {
        VirtualizedItems = new ObservableCollection<FixtureVirtualItem>(
            Enumerable
                .Range(1, 250)
                .Select(index => new FixtureVirtualItem(
                    index,
                    $"Item {index:000}",
                    index % 5 == 0 ? "recycled candidate" : "realized on demand")));

        _selectedVirtualizedItem = VirtualizedItems.FirstOrDefault();

        ToggleContextMenuCommand = new DelegateCommand(() => IsContextMenuOpen = !IsContextMenuOpen);
        SelectVirtualizedTailCommand = new DelegateCommand(SelectVirtualizedTail);
        CycleResourcePaletteCommand = new DelegateCommand(CycleResourcePalette);
        RefreshPriorityBindingCommand = new DelegateCommand(RefreshPriorityBinding);

        _runAsyncCommand = new AsyncDelegateCommand(RunAsyncCommandCoreAsync);
        _simulateDispatcherBusyCommand = new AsyncDelegateCommand(SimulateDispatcherBusyCoreAsync);
    }

    public event EventHandler<ResourcePaletteChangedEventArgs>? ResourcePaletteChanged;

    public string MultiBindingFirstName => "Ada";

    public string MultiBindingLastName => "Lovelace";

    public string ValidatedToken
    {
        get => _validatedToken;
        set
        {
            if (!SetProperty(ref _validatedToken, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ValidationSummary));
        }
    }

    public string ValidationSummary =>
        $"受理済みトークン: {ValidatedToken} | 入力規則: 大文字英数字と '-' を 4 文字以上";

    public string PriorityFallbackValue => $"fallback headline v{_priorityVersion}";

    public string SlowPriorityValue
    {
        get
        {
            var version = _priorityVersion;
            Thread.Sleep(900);
            return $"async headline v{version} ({DateTime.Now:HH:mm:ss})";
        }
    }

    public string PriorityBindingSummary =>
        $"優先順位: slow source -> fallback | 最終再評価: {_lastPriorityRefresh:HH:mm:ss} | version {_priorityVersion}";

    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set
        {
            if (!SetProperty(ref _isPopupOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PopupSummary));
        }
    }

    public bool IsContextMenuOpen
    {
        get => _isContextMenuOpen;
        set
        {
            if (!SetProperty(ref _isContextMenuOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PopupSummary));
        }
    }

    public string PopupSummary =>
        $"Popup: {(IsPopupOpen ? "open" : "closed")} | ContextMenu: {(IsContextMenuOpen ? "open" : "closed")}";

    public ObservableCollection<FixtureVirtualItem> VirtualizedItems { get; }

    public FixtureVirtualItem? SelectedVirtualizedItem
    {
        get => _selectedVirtualizedItem;
        set
        {
            if (!SetProperty(ref _selectedVirtualizedItem, value))
            {
                return;
            }

            OnPropertyChanged(nameof(VirtualizedSelectionSummary));
        }
    }

    public string VirtualizedSelectionSummary =>
        SelectedVirtualizedItem is null
            ? "250 件の仮想化候補を持つリストです。"
            : $"{SelectedVirtualizedItem.Label} を選択中 | {SelectedVirtualizedItem.State}";

    public bool IsAsyncCommandRunning
    {
        get => _isAsyncCommandRunning;
        private set
        {
            if (!SetProperty(ref _isAsyncCommandRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AsyncCommandSummary));
        }
    }

    public string AsyncCommandStatus
    {
        get => _asyncCommandStatus;
        private set
        {
            if (!SetProperty(ref _asyncCommandStatus, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AsyncCommandSummary));
        }
    }

    public double AsyncCommandProgress
    {
        get => _asyncCommandProgress;
        private set
        {
            if (!SetProperty(ref _asyncCommandProgress, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AsyncCommandSummary));
        }
    }

    public string AsyncCommandSummary =>
        $"{AsyncCommandStatus} | progress {AsyncCommandProgress:N0}% | running={IsAsyncCommandRunning.ToString().ToLowerInvariant()}";

    public string DispatcherBusyStatus
    {
        get => _dispatcherBusyStatus;
        private set => SetProperty(ref _dispatcherBusyStatus, value);
    }

    public string DispatcherLagSummary
    {
        get => _dispatcherLagSummary;
        private set => SetProperty(ref _dispatcherLagSummary, value);
    }

    public string DispatcherTimeoutSummary
    {
        get => _dispatcherTimeoutSummary;
        private set => SetProperty(ref _dispatcherTimeoutSummary, value);
    }

    public ResourcePalette ActivePalette => _activePalette;

    public string ResourcePaletteSummary =>
        $"現在の配色: {GetPaletteLabel(_activePalette)} | DynamicResource は差し替えに追従し、内側スコープはローカル override を優先します。";

    public bool IsTemplateActive
    {
        get => _isTemplateActive;
        set
        {
            if (!SetProperty(ref _isTemplateActive, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TemplateSummary));
        }
    }

    public string TemplateSummary =>
        IsTemplateActive
            ? "Template trigger active: accent badge and alternate chrome are visible."
            : "Template trigger idle: the control shows its default chrome.";

    public string HwndHostCaption => "Opaque Win32 child HWND";

    public string HwndHostSummary =>
        "HwndHost は WPF 管轄外の Win32 子ウィンドウ境界を露出します。将来の観測では adapter 境界として扱う前提です。";

    public DelegateCommand ToggleContextMenuCommand { get; }

    public DelegateCommand SelectVirtualizedTailCommand { get; }

    public DelegateCommand CycleResourcePaletteCommand { get; }

    public DelegateCommand RefreshPriorityBindingCommand { get; }

    public AsyncDelegateCommand RunAsyncCommand => _runAsyncCommand;

    public AsyncDelegateCommand SimulateDispatcherBusyCommand => _simulateDispatcherBusyCommand;

    private void SelectVirtualizedTail()
    {
        if (VirtualizedItems.Count > 0)
        {
            SelectedVirtualizedItem = VirtualizedItems[^1];
        }
    }

    private void CycleResourcePalette()
    {
        _activePalette = _activePalette switch
        {
            ResourcePalette.Ocean => ResourcePalette.Alert,
            ResourcePalette.Alert => ResourcePalette.Forest,
            _ => ResourcePalette.Ocean
        };

        OnPropertyChanged(nameof(ActivePalette));
        OnPropertyChanged(nameof(ResourcePaletteSummary));
        ResourcePaletteChanged?.Invoke(this, new ResourcePaletteChangedEventArgs(_activePalette));
    }

    private void RefreshPriorityBinding()
    {
        _priorityVersion++;
        _lastPriorityRefresh = DateTimeOffset.Now;
        OnPropertyChanged(nameof(PriorityFallbackValue));
        OnPropertyChanged(nameof(SlowPriorityValue));
        OnPropertyChanged(nameof(PriorityBindingSummary));
    }

    private async Task RunAsyncCommandCoreAsync()
    {
        IsAsyncCommandRunning = true;
        AsyncCommandProgress = 0;
        AsyncCommandStatus = "非同期処理を開始しました";

        try
        {
            for (var step = 1; step <= 4; step++)
            {
                await Task.Delay(250);
                AsyncCommandProgress = step * 25;
                AsyncCommandStatus = $"step {step}/4";
            }

            AsyncCommandStatus = $"完了 {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsAsyncCommandRunning = false;
        }
    }

    private async Task SimulateDispatcherBusyCoreAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        DispatcherBusyStatus = "ApplicationIdle probe を発行して UI スレッドをブロックします";
        DispatcherLagSummary = "未完了";
        DispatcherTimeoutSummary = $"wait_for_idle 閾値 {DispatcherTimeoutThresholdMs} ms を監視中";

        await Task.Yield();

        var stopwatch = Stopwatch.StartNew();
        var idleProbe = dispatcher.InvokeAsync(() => stopwatch.ElapsedMilliseconds, DispatcherPriority.ApplicationIdle);

        Thread.Sleep(1200);

        var timeoutTask = Task.Delay(DispatcherTimeoutThresholdMs);
        var firstCompleted = await Task.WhenAny(idleProbe.Task, timeoutTask);
        var lag = await idleProbe.Task;

        DispatcherLagSummary = $"ApplicationIdle probe observed after {lag} ms";
        DispatcherTimeoutSummary = firstCompleted == timeoutTask
            ? $"wait_for_idle exceeded {DispatcherTimeoutThresholdMs} ms before idle was reached."
            : $"wait_for_idle completed within {DispatcherTimeoutThresholdMs} ms.";
        DispatcherBusyStatus = $"最後の UI ブロック完了: {lag} ms at {DateTime.Now:HH:mm:ss}";
    }

    private static string GetPaletteLabel(ResourcePalette palette) => palette switch
    {
        ResourcePalette.Ocean => "Ocean",
        ResourcePalette.Alert => "Alert",
        ResourcePalette.Forest => "Forest",
        _ => palette.ToString()
    };
}

public sealed record FixtureVirtualItem(
    int Index,
    string Label,
    string State);

public sealed class ResourcePaletteChangedEventArgs : EventArgs
{
    public ResourcePaletteChangedEventArgs(ResourcePalette palette)
    {
        Palette = palette;
    }

    public ResourcePalette Palette { get; }
}

public enum ResourcePalette
{
    Ocean,
    Alert,
    Forest
}
