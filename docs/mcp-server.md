# WpfInspector MCP サーバー

## 概要

`WpfInspector.McpServer` は、AI エージェントが実行中の WPF プロセスを検査・操作できるようにする MCP（Model Context Protocol）サーバーです。stdio トランスポートで動作するコンソールアプリケーションとして実装され、Claude Desktop や Claude Code などの MCP 対応クライアントから直接利用できます。

主な用途:

- 実行中 WPF プロセスの一覧取得・詳細検査
- DLL 注入によるビジュアルツリー走査・UI 操作
- Portable PDB 解析

### 前提条件

- .NET 9 SDK（Windows x64）
- Windows x64 環境
- `WpfInspector.NativeBootstrap.dll` のビルド済みバイナリ（UI 操作系ツール使用時に必要）

## プロジェクト構成

```
src/WpfInspector.McpServer/
├── WpfInspector.McpServer.csproj
├── Program.cs
├── Session/
│   ├── IWpfSession.cs
│   ├── WpfSession.cs
│   └── WpfSessionManager.cs
└── Tools/
    ├── ProcessTools.cs
    ├── InjectionTools.cs
    ├── UiTools.cs
    └── PdbTools.cs
```

| ファイル | 役割 |
|---|---|
| `Program.cs` | MCP サーバーのエントリポイント。ツール登録と stdio トランスポートの初期化 |
| `Session/IWpfSession.cs` | セッション抽象。パイプ接続・コマンド送受信のインタフェース |
| `Session/WpfSession.cs` | 単一 PID に対するパイプ接続と TestHelper コマンドの実体 |
| `Session/WpfSessionManager.cs` | PID をキーとするセッション辞書の管理。McpServer 起動中はここで保持 |
| `Tools/ProcessTools.cs` | `list_processes` / `inspect_process` の実装 |
| `Tools/InjectionTools.cs` | `inject_and_connect` / `disconnect` の実装 |
| `Tools/UiTools.cs` | ビジュアルツリー取得・UI 操作ツールの実装 |
| `Tools/PdbTools.cs` | `analyze_pdb` の実装 |

## 公開 MCP ツール一覧

### プロセス情報系

#### `list_processes`

実行中プロセスの一覧を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `include_background` | bool | 任意（既定: false） | true のときバックグラウンドプロセスも含める |

戻り値（JSON 配列の例）:

```json
[
  {
    "pid": 1234,
    "name": "MyWpfApp",
    "window_title": "メインウィンドウ",
    "is_responding": true,
    "executable_path": "C:\\MyApp\\MyWpfApp.exe"
  }
]
```

#### `inspect_process`

指定 PID のプロセス詳細情報を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 検査対象のプロセス ID |

戻り値（JSON の例）:

```json
{
  "pid": 1234,
  "name": "MyWpfApp",
  "executable_path": "C:\\MyApp\\MyWpfApp.exe",
  "start_time": "2026-04-30T10:00:00",
  "working_set_mb": 128,
  "module_count": 42,
  "is_wpf_process": true,
  "suggested_pdb_path": "C:\\MyApp\\MyWpfApp.pdb",
  "notes": ["WPF: PresentationFramework 9.0.0.0", "Architecture: x64"]
}
```

### セッション管理系

#### `inject_and_connect`

対象プロセスに `NativeBootstrap.dll` を注入し、`TestHelper` との名前付きパイプ接続を確立します。UI 操作系ツールを使う前に必ず呼び出してください。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 注入対象のプロセス ID |

戻り値（JSON の例）:

```json
{
  "success": true,
  "pid": 1234,
  "message": "Session established. Pipe: wpfinspector-1234"
}
```

#### `disconnect`

指定 PID のセッションを切断します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 切断対象のプロセス ID |

戻り値（JSON の例）:

```json
{
  "success": true,
  "pid": 1234,
  "message": "Session disconnected."
}
```

### UI 操作系

UI 操作系ツールはすべて `inject_and_connect` による接続確立後に使用できます。未接続の PID に対して呼び出した場合はエラーメッセージを返します。

#### `get_visual_tree`

ビジュアルツリーを走査して結果を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |

戻り値（JSON の例）:

```json
{
  "pid": 1234,
  "windows": ["MainWindow"],
  "root_element": "Window",
  "tree": {
    "type": "Window",
    "name": "MainWindow",
    "children": [
      { "type": "Grid", "name": "", "children": [] }
    ]
  }
}
```

#### `get_window_title`

対象プロセスのウィンドウタイトルを返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |

戻り値（JSON の例）:

```json
{ "pid": 1234, "title": "メインウィンドウ" }
```

#### `get_element_text`

指定した要素名のテキスト内容を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |
| `element_name` | string | 必須 | 取得対象の要素名（`x:Name` 属性値） |

戻り値（JSON の例）:

```json
{ "pid": 1234, "element_name": "StatusLabel", "text": "準備完了" }
```

#### `click_button`

指定したボタンをクリックします。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |
| `button_name` | string | 必須 | クリック対象のボタン名（`x:Name` 属性値） |

戻り値（JSON の例）:

```json
{ "pid": 1234, "button_name": "Refresh", "success": true }
```

#### `type_text`

指定した要素にテキストを入力します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |
| `element_name` | string | 必須 | 入力対象の要素名（`x:Name` 属性値） |
| `text` | string | 必須 | 入力するテキスト |

戻り値（JSON の例）:

```json
{ "pid": 1234, "element_name": "SearchBox", "text": "hello", "success": true }
```

#### `get_data_grid_item_count`

指定した DataGrid の行数を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pid` | int | 必須 | 対象プロセス ID |
| `grid_name` | string | 必須 | DataGrid 要素名（`x:Name` 属性値） |

戻り値（JSON の例）:

```json
{ "pid": 1234, "grid_name": "ProcessesGrid", "item_count": 15 }
```

### PDB 解析系

#### `analyze_pdb`

Portable PDB を解析し、ドキュメント・メソッド・シーケンスポイントの集計を返します。

| 引数 | 型 | 必須 | 説明 |
|---|---|---|---|
| `pdb_path` | string | 必須 | 解析対象の `.pdb` ファイルパス |
| `assembly_path` | string | 任意 | メソッド名解決に使う `.dll` / `.exe` パス |

戻り値（JSON の例）:

```json
{
  "pdb_path": "C:\\MyApp\\MyWpfApp.pdb",
  "document_count": 12,
  "method_count": 84,
  "total_sequence_points": 320,
  "visible_sequence_points": 298,
  "documents": ["C:\\MyApp\\MainWindow.xaml.cs", "..."],
  "methods": [
    {
      "token": "0x06000001",
      "name": "MainWindow..ctor",
      "sequence_point_count": 5
    }
  ]
}
```

## セッション管理の仕組み

```
inject_and_connect(pid)
  → ProcessInjector.Inject() — CreateRemoteThread + LoadLibraryW
      └─ NativeBootstrap.dll が対象プロセスで DllMain を実行
           → hostfxr で WpfInspector.TestHelper をロード
             → TestHelper がパイプ wpfinspector-{pid} に接続し "READY" を送信
  → WpfSessionManager にセッションを登録
  → 以降の UI 操作ツールはこのセッション経由でコマンドを送受信
```

- セッションは McpServer プロセスのライフタイム中、`WpfSessionManager` が保持します
- McpServer を再起動した場合は、再度 `inject_and_connect` を呼び出してください
- 未接続の PID に UI 操作ツールを呼んだ場合は `{ "error": "No session for pid XXXX. Call inject_and_connect first." }` を返します
- 対象プロセスが終了すると次回コマンド送信時にパイプエラーが発生します。その場合は `disconnect` を呼んでセッションを破棄してください

## ビルドと起動

### 前提: NativeBootstrap.dll のビルド

UI 操作系ツールを使う場合は、事前に MSVC でネイティブ DLL をビルドしてください。

```powershell
# Developer PowerShell または x64 Native Tools Command Prompt で実行
msbuild src/WpfInspector.NativeBootstrap/WpfInspector.NativeBootstrap.vcxproj `
        /p:Configuration=Debug /p:Platform=x64
```

出力: `src/WpfInspector.NativeBootstrap/x64/Debug/WpfInspector.NativeBootstrap.dll`

### McpServer のビルド

```powershell
dotnet build src/WpfInspector.McpServer
```

### 発行

```powershell
dotnet publish src/WpfInspector.McpServer `
    -c Release -r win-x64 --self-contained false `
    -o publish/WpfInspector.McpServer
```

## Claude Desktop / Claude Code での設定

### Claude Desktop

`claude_desktop_config.json` に以下を追加します。

```json
{
  "mcpServers": {
    "wpf-inspector": {
      "command": "dotnet",
      "args": [
        "run",
        "--project", "C:\\Projects\\WpfTest\\src\\WpfInspector.McpServer",
        "--configuration", "Release"
      ]
    }
  }
}
```

### Claude Code

プロジェクト設定 `.claude/settings.json` に以下を追加します。発行済みバイナリを直接指定する方法が起動が速く推奨です。

```json
{
  "mcpServers": {
    "wpf-inspector": {
      "command": "C:\\Projects\\WpfTest\\publish\\WpfInspector.McpServer\\WpfInspector.McpServer.exe"
    }
  }
}
```

## 使用例

以下は AI エージェントが WPF アプリを検査・操作する典型的な操作シナリオです。

```
1. list_processes()
   → WPF プロセスを含む一覧を確認する

2. inspect_process(pid: 1234)
   → is_wpf_process, モジュール数, suggested_pdb_path を確認する

3. inject_and_connect(pid: 1234)
   → DLL を注入し、パイプ接続を確立する

4. get_visual_tree(pid: 1234)
   → UI ツリーを走査して要素名・階層を把握する

5. click_button(pid: 1234, button_name: "Refresh")
   → ボタンをクリックしてデータを更新する

6. get_data_grid_item_count(pid: 1234, grid_name: "ProcessesGrid")
   → 行数が増えていることを確認する

7. analyze_pdb(pdb_path: "C:\\MyApp\\MyWpfApp.pdb", assembly_path: "C:\\MyApp\\MyWpfApp.dll")
   → ソース構成・メソッド数・シーケンスポイントを解析する

8. disconnect(pid: 1234)
   → セッションを終了する
```

## 注意事項

- **stdout の占有**: MCP サーバーは stdio トランスポートを使用するため、stdout は JSON-RPC の通信に占有されます。デバッグログは stderr に出力してください。アプリケーションコード内で `Console.WriteLine` を使うと MCP フレームが壊れます
- **管理者権限**: 対象プロセスが管理者権限で動作している場合、DLL 注入に管理者権限が必要になることがあります。その場合は McpServer 自体を管理者権限で起動してください
- **Portable PDB のみ対応**: `analyze_pdb` は Portable PDB のみ対応しています。Classic Windows PDB（Visual Studio の `.pdb`）は非対応です
- **NativeBootstrap のビルド**: UI 操作系ツールを使うには、事前に MSVC で `WpfInspector.NativeBootstrap.dll` をビルドしておく必要があります。ビルド手順は「ビルドと起動」節を参照してください
- **セッションの再確立**: McpServer を再起動すると既存セッションはすべて失われます。再度 `inject_and_connect` を呼び出してください
