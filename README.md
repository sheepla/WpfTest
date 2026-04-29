# WpfInspector

WPF アプリケーションの実行状態、選択中プロセスの PDB、UI ツリーを確認するためのデスクトップ解析ツールです。  
実行中プロセスの選択、PE/CodeView ベースの PDB 候補確認、Portable PDB の解析、外部プロセスへの DLL インジェクションまたは自己プロセス直接走査による UI 走査を行えます。

## スコープ

- 実行中プロセスの一覧表示と詳細確認
- 対象プロセスのロード済みモジュール列挙
- `WpfInspector` 自身を選んだ自己プロセス検証
- 選択中 WPF のメインモジュールから PDB 候補パスの解決
- Portable PDB の文書・メソッド・シーケンスポイント解析
- 検査ペイロード注入によるウィンドウ/ビジュアルツリー走査
- xUnit によるコアロジックの検証

## 動作フロー

プロセス一覧から WPF を選択すると、メインモジュールの PDB 候補が自動入力されます。  
そのまま `PDB解析とUI走査` を実行すると、先にランタイム UI 走査を行って使われているアセンブリ候補を収集し、その結果を使って対象 PDB を選んで解析します。`WpfInspector` 自身を選んだ場合は DLL 注入ではなく直接走査します。

## 構成

- `src/WpfInspector.App`: WPF フロントエンド
- `src/WpfInspector.Core`: 解析ロジック
- `src/WpfInspector.Injector`: DLL 注入処理
- `src/WpfInspector.InspectionPayload`: 注入先で動作する WPF 走査ペイロード
- `tests/WpfInspector.App.Tests`: xUnit テスト
- `docs/specification.md`: 仕様
- `docs/roadmap.md`: ロードマップ

## 実行

```powershell
dotnet build WpfInspector.sln
dotnet run --project src/WpfInspector.App/WpfInspector.App.csproj
```

## テスト

```powershell
dotnet test WpfInspector.sln
```
