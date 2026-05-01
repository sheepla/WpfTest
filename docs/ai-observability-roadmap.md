# WPF AI 観測ロードマップ

## 目的

AI から WPF アプリの複雑な内部動作を確認できるようにし、単なる UI スナップショット確認ではなく、

- 今どうなっているか
- なぜそうなっているか
- どのコードに繋がるか
- 操作後に何が順番に起きたか

をデバッガなしで説明できる状態を目指す。

## 目標の定義

「完全な観測」は厳密には不可能である。`HwndHost`、WebView2、D3D、IME、別 HWND の popup、OS 入力経路、GPU 合成まで含めると、WPF 単体の収集では閉じない。

このため現実的な完了条件は以下とする。

- managed WPF の 95% 以上について、AI が「現在状態」「状態の原因」「関連コード」「状態遷移」を説明できる
- WPF 管轄外の領域は未対応ではなく「別 adapter が必要な境界」として識別できる

## 現状

現行実装では主に以下が可能である。

- 実行中プロセスの列挙と WPF 判定
- 注入または自己走査による UI ツリー取得
- 簡単な UI 操作
- Portable PDB 解析

一方で、観測は主に単発 `GET_TREE` スナップショットに依存している。

### 現在の制約

- 各ノードで取得している情報は `TypeName`、`Name`、`AutomationId`、`Text`、`DataContextTypeName`、`IsVisible`、`IsEnabled`、`Width`、`Height` 程度に留まる
- visual tree 中心であり、logical tree、popup、virtualization、resource 解決、binding 状態、command 状態、event 流れが見えない
- AI から使える操作は `click`、`type_text`、`get_text`、`get_ui_state` 程度で粗い
- 時系列トレースがなく、単一時点の状態しか分からない
- UI ノードと PDB / XAML / ViewModel コードの対応付けがない

## 方針

- まず操作より観測を強化する
- 次に時系列とコード相関を追加する
- 最後に WPF 外縁の interop 領域へ adapter を広げる
- 既存の自己走査 / 注入走査 / PDB 解析フローを壊さない

## フェーズ別ロードマップ

### Phase 1: 観測モデル再定義

目的:
- UI 表示用 DTO を診断用 DTO に拡張する

実装項目:
- `WpfInspectionResult` と `WpfElementInfo` に schema version を追加する
- 各ノードに stable id を追加する
- visual path、logical path、window 情報、dispatcher thread 情報を追加する
- parent chain、templated parent、owner window を表現できるようにする

完了条件:
- AI が `Name` ではなく stable id で要素を再指定できる
- 同名要素が複数あっても識別できる

### Phase 2: ツリー完全性の向上

目的:
- visual tree の欠落を減らす

実装項目:
- logical tree 収集を追加する
- `Popup`、`ContextMenu`、`ToolTip`、`AdornerLayer` を明示的に収集する
- `ItemsControl` の item と container の対応を保持する
- virtualization 状態と未実体化 item 数を収集する
- `MaxDepth` / `MaxChildren` の固定打ち切りを pagination に置き換える

完了条件:
- 画面上に見えている主要 UI 要素が tree から欠落しない
- virtualized list の観測漏れが説明可能になる

### Phase 3: DependencyProperty 観測

目的:
- 状態の原因を説明できるようにする

実装項目:
- 主要 DP の実効値を収集する
- `BaseValueSource` と local/style/template/inherited の由来を収集する
- 少なくとも以下を対象にする
  - `Visibility`
  - `IsEnabled`
  - `IsChecked`
  - `IsSelected`
  - `IsFocused`
  - `IsKeyboardFocusWithin`
  - `Text`
  - `SelectedItem`
  - `ItemsSource`
  - `DataContext`
  - `Style`
  - `Template`
  - `Foreground`
  - `Background`
  - `ActualWidth`
  - `ActualHeight`

完了条件:
- AI が「なぜ disabled / hidden / selected なのか」を説明できる

### Phase 4: Binding / Validation / Resource 診断

目的:
- WPF 特有の故障点を直接観測する

実装項目:
- `BindingExpression` 情報を取得する
- binding path、source type、status、fallback、target null value、converter 情報を返す
- `Validation.Errors` とエラー要約を返す
- local resource、merged dictionaries、theme resource、dynamic/static resource 解決元を返す

完了条件:
- AI が「なぜ値が出ないか」「なぜ binding が失敗しているか」を説明できる

### Phase 5: Command / Event / Interaction 診断

目的:
- 入力と動作のつながりを説明可能にする

実装項目:
- `ICommand`、parameter、target、`CanExecute` を観測対象にする
- `CommandBinding` の解決先を追跡できるようにする
- routed event のログ収集機構を追加する
- 現在の直接 `ClickEvent` 発火や `Text` 直書き操作を、実入力に近い経路へ段階的に切り替える

完了条件:
- AI が「押しても動かない理由」を command/event レベルで説明できる

### Phase 6: 時系列トレース

目的:
- 単発スナップショットではなく状態遷移を追えるようにする

実装項目:
- trace session を追加する
- property changed、collection changed、command reevaluation を収集する
- dispatcher operation、layout/render、binding trace、dispatcher exception を収集する
- snapshot diff 機能を追加する

追加 MCP ツール候補:
- `start_trace`
- `stop_trace`
- `get_trace`
- `diff_snapshots`
- `wait_for_condition`
- `wait_for_idle`

完了条件:
- AI が「操作後に何が順番に起きたか」を時系列で説明できる

### Phase 7: コード相関

目的:
- UI 状態とコードを結び付ける

実装項目:
- UI ノードと XAML / BAML / PDB を相関させる
- event handler、ViewModel property、command 実装候補をノードに紐付ける
- テンプレート要素でも template source と生成元を追えるようにする

完了条件:
- AI が「この UI はどのコードに由来するか」をファイル単位で返せる

### Phase 8: MCP ツール面の拡張

目的:
- AI が観測と操作を細かく組み合わせられるようにする

追加ツール候補:
- `get_element_details`
- `get_binding_details`
- `get_command_state`
- `select_item`
- `expand`
- `collapse`
- `focus`
- `scroll_into_view`
- `invoke_command`
- `list_popups`
- `start_trace`
- `stop_trace`
- `get_trace`
- `diff_snapshots`

完了条件:
- AI が単なる button click に依存せず、状態確認と細粒度操作を組み合わせて調査できる

### Phase 9: WPF 外縁の interop 対応

目的:
- WPF 以外の重要領域を見失わないようにする

実装項目:
- `HwndHost`、WebView2、Win32 child window を opaque node ではなく adapter 付き node として扱う
- IME、別 HWND popup、GPU surface は境界情報を収集する
- WPF 管轄外を明示するメタデータを導入する

完了条件:
- AI が「ここから先は WPF ではない」と判断できる

### Phase 10: フィクスチャと回帰テスト

目的:
- 観測機能の退行を防ぐ

実装項目:
- 病理ケース用の fixture app を用意する
- 少なくとも以下を含める
  - binding failure
  - validation
  - multi-binding
  - priority binding
  - popup / context menu
  - virtualized list
  - async command
  - dispatcher busy / timeout
  - resource override
  - control template
  - HwndHost
- snapshot / trace / code 相関の自動テストを追加する

完了条件:
- 主要観測機能ごとに再現ケースと自動テストが存在する

## 優先順位

実装順は以下を推奨する。

1. Phase 1: 観測モデル再定義
2. Phase 2: ツリー完全性の向上
3. Phase 3: DependencyProperty 観測
4. Phase 4: Binding / Validation / Resource 診断
5. Phase 5: Command / Event / Interaction 診断
6. Phase 6: 時系列トレース
7. Phase 7: コード相関
8. Phase 8: MCP ツール面の拡張
9. Phase 9: WPF 外縁の interop 対応
10. Phase 10: フィクスチャと回帰テスト

## マイルストーン

### Milestone A: 状態説明可能

- Phase 1-4 完了
- AI が単一時点の UI について「何が起きているか」と「なぜそうなっているか」を説明できる

### Milestone B: 挙動説明可能

- Phase 5-6 完了
- AI が操作後の変化を時系列で説明できる

### Milestone C: 実装修正可能

- Phase 7-8 完了
- AI が関連コード候補を辿り、観測結果に基づく具体的修正案を返せる

### Milestone D: 高カバレッジ観測

- Phase 9-10 完了
- managed WPF の大半について、AI が観測境界を誤認せずに調査できる

## 完了の定義

以下を満たした時点を、このロードマップの完了とする。

- AI が任意の主要 control について、現在値、値の由来、関連 binding、関連 command、関連コード位置を返せる
- AI が snapshot だけでなく trace から状態遷移を説明できる
- popup、virtualization、template 内部、validation、async UI 更新を大きく落とさない
- WPF 外領域は「別 adapter 管轄」として明示できる

## 補足

- まず操作を増やすより、観測モデルと binding / command / trace を増やす方が効果が大きい
- 特に初期投資として重要なのは Phase 1-4 であり、ここがないと AI は UI を見ても原因を説明できない
