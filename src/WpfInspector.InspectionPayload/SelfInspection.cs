using WpfInspector.Core.Models;

namespace WpfInspector.InspectionPayload;

/// <summary>
/// WpfInspector 自身を対象にした直接 UI 走査を提供する。
/// 外部プロセス向けの注入経路を使わず、アプリ内で同じ収集ロジックを実行する。
/// </summary>
public static class SelfInspection
{
    public static WpfInspectionResult InspectCurrentProcess() =>
        Inspector.CollectResult(Environment.ProcessId);
}
