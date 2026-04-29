# WpfInspector

WPF アプリケーションの実行状態とシンボル情報を確認するためのデスクトップ解析ツールです。  
実行中プロセスの選択、PE/CodeView ベースの PDB 候補確認、Portable PDB の解析を行えます。

## スコープ

- 実行中プロセスの一覧表示と詳細確認
- 対象プロセスのロード済みモジュール列挙
- モジュールから PDB 候補パスの解決
- Portable PDB の文書・メソッド・シーケンスポイント解析
- xUnit によるコアロジックの検証

## 安全境界

この実装では、任意プロセスへの DLL インジェクションは扱いません。  
代わりに、安全なアタッチ前提の解析基盤と PDB/PE 情報の確認に対象を絞っています。

## 構成

- `src/WpfInspector.App`: WPF フロントエンド
- `src/WpfInspector.Core`: 解析ロジック
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

