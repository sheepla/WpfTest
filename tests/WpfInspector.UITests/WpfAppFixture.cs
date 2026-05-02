using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows.Automation;

namespace WpfInspector.UITests;

/// <summary>
/// WpfInspector.App を起動し、Windows UI Automation で操作するテストフィクスチャ。
/// </summary>
public sealed class WpfAppFixture : IAsyncLifetime
{
    private Process? _process;
    private readonly List<Process> _additionalProcesses = [];

    private static readonly TimeSpan AppStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UiReadyTimeout = TimeSpan.FromSeconds(20);

    public async Task InitializeAsync()
    {
        var appExe = FindAppExe();

        _process = Process.Start(new ProcessStartInfo(appExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appExe)
        }) ?? throw new InvalidOperationException("プロセスの起動に失敗しました。");

        await WaitForMainWindowAsync(_process, AppStartTimeout);
        await WaitForConditionAsync(
            () => TryGetDataGridItemCount("ProcessesGrid"),
            count => count > 0,
            UiReadyTimeout,
            "ProcessesGrid の初期化を待機中にタイムアウトしました。");
        await WaitForConditionAsync(
            () => TryGetDataGridItemCount("ModulesGrid"),
            count => count > 0,
            UiReadyTimeout,
            "ModulesGrid の初期化を待機中にタイムアウトしました。");
    }

    public Task DisposeAsync() => CleanupAsync();

    public string GetWindowTitle() =>
        GetMainWindow().Current.Name;

    public int GetDataGridItemCount(string automationId)
    {
        var count = TryGetDataGridItemCount(automationId);
        if (count < 0)
        {
            throw new InvalidOperationException($"{automationId} が見つかりません。");
        }

        return count;
    }

    public string GetText(string automationId)
    {
        var element = FindDescendantByAutomationId(automationId)
            ?? throw new InvalidOperationException($"{automationId} が見つかりません。");
        return element.Current.Name;
    }

    public string FindTextContaining(string fragment)
    {
        var text = TryFindTextContaining(fragment);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"'{fragment}' を含むテキストが見つかりません。");
        }

        return text;
    }

    public void ClickButton(string automationIdOrName)
    {
        var button = FindButton(automationIdOrName)
            ?? throw new InvalidOperationException($"{automationIdOrName} ボタンが見つかりません。");

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"{automationIdOrName} ボタンは InvokePattern をサポートしていません。");
        }

        ((InvokePattern)pattern).Invoke();
    }

    public void SetText(string automationId, string value)
    {
        var element = FindDescendantByAutomationId(automationId)
            ?? throw new InvalidOperationException($"{automationId} が見つかりません。");

        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"{automationId} は ValuePattern をサポートしていません。");
        }

        ((ValuePattern)pattern).SetValue(value);
    }

    public async Task<Process> LaunchAdditionalInstanceAsync()
    {
        var appExe = FindAppExe();
        var process = Process.Start(new ProcessStartInfo(appExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appExe)
        }) ?? throw new InvalidOperationException("追加プロセスの起動に失敗しました。");

        await WaitForMainWindowAsync(process, AppStartTimeout);
        _additionalProcesses.Add(process);
        return process;
    }

    public async Task SelectProcessByPidAsync(int processId)
    {
        ClickButton("RefreshButton");
        SetText("ProcessFilterTextBox", processId.ToString(CultureInfo.InvariantCulture));

        await WaitForConditionAsync(
            () => TryFindProcessRowByPid(processId) is not null,
            found => found,
            UiReadyTimeout,
            $"PID {processId} のプロセス行が見つかりません。");

        if (!TrySelectProcessByPid(processId))
        {
            throw new InvalidOperationException($"PID {processId} のプロセス行を選択できませんでした。");
        }

        await WaitForConditionAsync(
            () => TryGetText("SelectedProcessHeadlineText"),
            text => text.Contains($"PID {processId}", StringComparison.Ordinal),
            UiReadyTimeout,
            $"PID {processId} が選択状態になりませんでした。現在値: '{TryGetText("SelectedProcessHeadlineText")}'");
    }

    public async Task<string> WaitForStatusAsync(TimeSpan timeout, params string[] expectedFragments)
    {
        return await WaitForConditionAsync(
            () => TryGetText("StatusMessageText"),
            text => !string.IsNullOrWhiteSpace(text) && expectedFragments.Any(fragment => text.Contains(fragment, StringComparison.Ordinal)),
            timeout,
            $"期待したステータスに遷移しませんでした。現在値: '{TryGetText("StatusMessageText")}'");
    }

    public async Task<string> WaitForTextContainingAsync(TimeSpan timeout, string fragment)
    {
        return await WaitForConditionAsync(
            () => TryFindTextContaining(fragment),
            text => !string.IsNullOrWhiteSpace(text),
            timeout,
            $"'{fragment}' を含むテキストが見つかりませんでした。");
    }

    public async Task<int> WaitForTreeItemCountAsync(TimeSpan timeout, int minimumCount)
    {
        return await WaitForConditionAsync(
            TryGetTreeItemCount,
            count => count >= minimumCount,
            timeout,
            $"TreeView の項目数が {minimumCount} 件以上になりませんでした。現在値: {TryGetTreeItemCount()}");
    }

    private static string FindAppExe()
    {
        var here = Path.GetDirectoryName(typeof(WpfAppFixture).Assembly.Location)!;
        var configurations = GetPreferredConfigurations(here);
        var candidates = configurations
            .Select(configuration => FindInParents(
                here,
                Path.Combine("src", "WpfInspector.App", "bin", configuration, "net9.0-windows", "WpfInspector.App.exe")))
            .Concat(
            [
                Path.Combine(here, "WpfInspector.App.exe")
            ])
            .ToArray();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("WpfInspector.App.exe が見つかりません。先に 'dotnet build' を実行してください。");
    }

    private static IReadOnlyList<string> GetPreferredConfigurations(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configuration = segments.FirstOrDefault(segment =>
            string.Equals(segment, "Debug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "Release", StringComparison.OrdinalIgnoreCase));

        if (string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            return ["Release", "Debug"];
        }

        return ["Debug", "Release"];
    }

    private static string? FindInParents(string start, string relative)
    {
        var dir = start;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent;
        }

        return null;
    }

    private static async Task WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("WPF アプリのメインウィンドウが表示されませんでした。");
    }

    private async Task<T> WaitForConditionAsync<T>(
        Func<T> valueFactory,
        Func<T, bool> predicate,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = valueFactory();
            if (predicate(value))
            {
                return value;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private int TryGetDataGridItemCount(string automationId)
    {
        try
        {
            var grid = FindDescendantByAutomationId(automationId);
            if (grid is null)
            {
                return -1;
            }

            var items = grid.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            return items.Count;
        }
        catch
        {
            return -1;
        }
    }

    private int TryGetTreeItemCount()
    {
        try
        {
            var treeItems = GetMainWindow().FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            return treeItems.Count;
        }
        catch
        {
            return -1;
        }
    }

    private AutomationElement? TryFindProcessRowByPid(int processId)
    {
        try
        {
            var grid = FindDescendantByAutomationId("ProcessesGrid");
            if (grid is null)
            {
                return null;
            }

            var rows = grid.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            var processIdText = processId.ToString(CultureInfo.InvariantCulture);

            foreach (AutomationElement row in rows)
            {
                if (ElementContainsText(row, processIdText))
                {
                    return row;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySelectProcessByPid(int processId)
    {
        var row = TryFindProcessRowByPid(processId);
        if (row is null)
        {
            return false;
        }

        if (row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            return true;
        }

        return false;
    }

    private string TryGetText(string automationId)
    {
        try
        {
            return GetText(automationId);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string TryFindTextContaining(string fragment)
    {
        try
        {
            var texts = GetMainWindow().FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

            foreach (AutomationElement text in texts)
            {
                if (!string.IsNullOrWhiteSpace(text.Current.Name)
                    && text.Current.Name.Contains(fragment, StringComparison.Ordinal))
                {
                    return text.Current.Name;
                }
            }
        }
        catch
        {
            // テキスト探索失敗時は空を返す。
        }

        return string.Empty;
    }

    private AutomationElement GetMainWindow()
    {
        if (_process is null)
        {
            throw new InvalidOperationException("WpfInspector.App が起動していません。");
        }

        _process.Refresh();
        if (_process.MainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("メインウィンドウのハンドルを取得できません。");
        }

        return AutomationElement.FromHandle(_process.MainWindowHandle);
    }

    private AutomationElement? FindDescendantByAutomationId(string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return GetMainWindow().FindFirst(TreeScope.Descendants, condition);
    }

    private AutomationElement? FindButton(string automationIdOrName)
    {
        var controlType = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, automationIdOrName);
        var byName = new PropertyCondition(AutomationElement.NameProperty, automationIdOrName);
        return GetMainWindow().FindFirst(TreeScope.Descendants, new AndCondition(controlType, new OrCondition(byId, byName)));
    }

    private static bool ElementContainsText(AutomationElement root, string text)
    {
        if (!string.IsNullOrWhiteSpace(root.Current.Name)
            && root.Current.Name.Contains(text, StringComparison.Ordinal))
        {
            return true;
        }

        var textNodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement node in textNodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Current.Name)
                && node.Current.Name.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task CleanupAsync()
    {
        foreach (var process in _additionalProcesses)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }
        _additionalProcesses.Clear();

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        _process = null;
    }
}
