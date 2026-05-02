namespace WpfInspector.UITests;

/// <summary>
/// WpfInspector.App 自身を起動し、実 UI に対してボタン操作と表示検証を行う。
/// </summary>
[Collection("WpfApp")]
public sealed class MainWindowTests : IAsyncLifetime
{
    private readonly WpfAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync()    => _fixture.DisposeAsync();

    [Fact]
    public async Task Window_title_is_WpfInspector()
    {
        var title = _fixture.GetWindowTitle();
        Assert.Equal("WpfInspector", title);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessesGrid_has_rows_after_startup()
    {
        var count = _fixture.GetDataGridItemCount("ProcessesGrid");
        Assert.True(count > 0, $"ProcessesGrid の行数が 0 です: {count}");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ModulesGrid_has_rows_after_process_selected()
    {
        var count = _fixture.GetDataGridItemCount("ModulesGrid");
        Assert.True(count > 0, $"ModulesGrid の行数が 0 です: {count}");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Self_process_is_selected_after_startup()
    {
        var headline = _fixture.GetText("SelectedProcessHeadlineText");
        Assert.Contains("WpfInspector自身", headline, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Self_process_initial_pdb_summary_prefers_managed_module_over_apphost()
    {
        var summary = _fixture.FindTextContaining("対象 PDB:");
        Assert.DoesNotContain("apphost.pdb", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WpfInspector.App", summary, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Analyze_button_runs_against_self_process()
    {
        var status = await AnalyzeSelfAsync();

        Assert.Contains("UI 走査", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_button_populates_runtime_assembly_summary_for_self_process()
    {
        await AnalyzeSelfAsync();

        var summary = await _fixture.WaitForTextContainingAsync(TimeSpan.FromSeconds(10), "[Window]");
        Assert.Contains("WpfInspector.App", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_button_populates_window_summary_and_tree_for_self_process()
    {
        await AnalyzeSelfAsync();

        var windowSummary = await _fixture.WaitForTextContainingAsync(TimeSpan.FromSeconds(10), "WpfInspector |");
        Assert.Contains("Normal", windowSummary, StringComparison.Ordinal);

        var treeItemCount = await _fixture.WaitForTreeItemCountAsync(TimeSpan.FromSeconds(10), 1);
        Assert.True(treeItemCount > 0, $"TreeView の項目数が 0 です: {treeItemCount}");
    }

    [Fact]
    public async Task Analyze_button_runs_against_secondary_process_via_injection()
    {
        var target = await _fixture.LaunchAdditionalInstanceAsync();

        await _fixture.SelectProcessByPidAsync(target.Id);

        var headline = _fixture.GetText("SelectedProcessHeadlineText");
        Assert.Contains($"PID {target.Id}", headline, StringComparison.Ordinal);
        Assert.DoesNotContain("WpfInspector自身", headline, StringComparison.Ordinal);

        _fixture.ClickButton("AnalyzeSelectedWpfButton");

        var status = await _fixture.WaitForStatusAsync(
            TimeSpan.FromSeconds(45),
            "UI 走査と PDB 解析が完了しました",
            "UI 走査は完了しましたが");

        Assert.Contains("UI 走査", status, StringComparison.Ordinal);
    }

    private async Task<string> AnalyzeSelfAsync()
    {
        _fixture.ClickButton("AnalyzeSelectedWpfButton");

        return await _fixture.WaitForStatusAsync(
            TimeSpan.FromSeconds(30),
            "UI 走査と PDB 解析が完了しました",
            "UI 走査は完了しましたが");
    }
}
