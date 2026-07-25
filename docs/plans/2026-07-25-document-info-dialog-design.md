# 文書情報ダイアログ 設計書

- **作成日**: 2026-07-25
- **対象**: `src/yEdit.Core`(新規 `DocumentInfo/` 名前空間)、`src/yEdit.App`(新規 Dialog/Controller・MainForm 変更)、`src/yEdit.Core/Reading/PositionFormatter.cs`(シグネチャ縮小)
- **区分**: 新機能追加 + 既存 API の縮小変更(Ctrl+Alt+P 出力)
- **前提**: [[crlf-atomic-caret]] マージ後の main。CRLF=1 論理文字方針は本件でも継続。F-3(サロゲート=1)は「Ctrl+Alt+P では UTF-16 code unit 2 のまま」「文書情報ダイアログでは 1 として数える」の別基準として棲み分ける

## 0. スコープと決定事項サマリ

**目的**: アクティブタブの文書メタ情報を一覧できるダイアログを追加する。SR ユーザーはダイアログを開くだけで文書全体の把握ができ、晴眼・弱視ユーザーもレイアウトで即読み可能。

**採用方針**:
- Core に純ロジック(`DocumentInfoBuilder` / `DocumentInfoFormatter` / `DocumentInfo` record)、App に薄い Dialog + Controller の 2 層分離(既存 `KinsokuFormatController` / `PositionFormatter` と同型)
- ダイアログ本体は単一の `Multiline / ReadOnly TextBox` に全項目を行区切り表示(SR は TextBox にフォーカスすると内容全体を通し読み、以降は行・文字単位のキャレット移動で読める)
- Ctrl+Alt+P の位置照会からは **「文字数 M」も「選択 K 文字」も削除**(文字数の重複情報を排し、詳細は文書情報ダイアログへ集約)

**変更後の姿(ユーザー視点)**:
- [ファイル] メニュー内、[タブを閉じる] の 1 つ上に「文書情報(&I)」項目が出現。ショートカット無し(Alt→F→I で到達)
- 選択でモーダルダイアログが開き、以下 9〜10 項目が縦に並ぶ:
  ```
  ファイル名: aaa
  形式: テキスト(.txt)
  保存ディレクトリ: d:\hogehoge
  文字数: 1,234
  文字コード: UTF-8 (BOM付き)
  改行コード: CRLF
  ファイルサイズ: 2,048 バイト
  作成日時: 2026-07-25 10:30:15
  更新日時: 2026-07-25 12:45:00
  ```
  CSV モード時は末尾に `CSV: 100 行 × 5 列` が追加される。
- Enter または Esc で閉じ、閉じた後は編集領域にフォーカスが戻る
- Ctrl+Alt+P の発話は `行 L / 全 N、桁 C(、上書き)` に短縮される

**非対象(scope out)**:
- 単語数・段落数・可読性スコア等、言語依存の集計(YAGNI)
- 履歴的な yEdit 管理項目(セッション内編集開始時刻など)
- ダイアログ内のクリップボードコピーボタン(TextBox 上での Ctrl+A → Ctrl+C は標準動作として利用可)
- Ctrl+Alt+P のショートカット再割り当て
- ダイアログ表示中のリアルタイム更新(モーダル・開いた瞬間のスナップショット固定)

## 1. アーキテクチャ

依存方向: Core は File I/O に触れない。ファイル属性(作成日時・更新日時・サイズ)は App 側で構築し、Builder に注入する純関数モデル。

### 追加ファイル

| ファイル | 責務 |
|---|---|
| `src/yEdit.Core/DocumentInfo/DocumentInfo.cs` | イミュータブル record(表示用データ) |
| `src/yEdit.Core/DocumentInfo/FormatKind.cs` | enum(Text/Csv/Markdown/Other/Unsaved) |
| `src/yEdit.Core/DocumentInfo/DocumentInfoBuilder.cs` | 純関数 `Build(state, snapshot, fileMeta, csvDoc)` |
| `src/yEdit.Core/DocumentInfo/DocumentInfoFormatter.cs` | 純関数 `Format(DocumentInfo)` → `string` |
| `src/yEdit.Core/DocumentInfo/FileMeta.cs` | ファイル属性の値型(CreationTime/LastWriteTime/Length) |
| `src/yEdit.App/DocumentInfoDialog.cs` | 薄い Form(TextBox + [閉じる]) |
| `src/yEdit.App/DocumentInfoController.cs` | 起動導線(DocumentManager から Active を取り、Build→Format→Show) |
| `src/yEdit.App/FileMetaProvider.cs` | `path → FileMeta?` の I/O 分離ヘルパ(try-catch を包む) |

### 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/yEdit.App/MainForm.cs` | メニュー配線 + Controller 生成 + `AnnouncePosition` 縮小 |
| `src/yEdit.Core/Reading/PositionFormatter.cs` | 引数 `totalChars` / `selectionLength` を削除(breaking) |
| `tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs` | 追従 |

### 依存グラフ

```
MainForm ─┬─> DocumentInfoController ─> DocumentInfoDialog ── (WinForms)
          │        │
          │        ├─> FileMetaProvider ── File.* I/O
          │        │
          │        ├─> DocumentInfoBuilder ─┐
          │        │                         ├─> DocumentInfo (record)
          │        └─> DocumentInfoFormatter ┘
          │
          └─> PositionFormatter.Format(line, totalLines, column, overtype)  ★引数縮小
```

## 2. データ型

### DocumentInfo record

```csharp
namespace yEdit.Core.DocumentInfo;

public sealed record DocumentInfo(
    string DisplayName,          // "aaa" or "無題 1"(拡張子なしファイル名)
    FormatKind Format,
    string? Extension,           // ".txt"(小文字化済) or null(未保存・拡張子なし)
    string? Directory,           // "d:\hogehoge" or null(未保存)
    int CharacterCount,          // Rune 数 - Rune.IsWhiteSpace 除外
    string EncodingLabel,        // "UTF-8 (BOM付き)" 等(整形済)
    LineEnding LineEnding,       // Core.Text.LineEnding
    DateTime? CreationTime,      // null=未保存 or 属性取得失敗
    DateTime? LastWriteTime,     // 同上
    long? FileSizeBytes,         // 同上
    (int Rows, int Cols)? Csv    // null=CSVモードでない
);

public enum FormatKind { Text, Csv, Markdown, Other, Unsaved }
```

### FileMeta 値型

```csharp
public readonly record struct FileMeta(
    DateTime CreationTime,
    DateTime LastWriteTime,
    long Length
);
```

`null` なファイル属性は「未保存」または「取得失敗」を等しく意味する(Formatter 側では区別せず「-」表示)。

## 3. Format 判定ロジック(Builder 内)

拡張子は `Path.GetExtension(path).ToLowerInvariant()` で正規化してから switch(ユーザー Q6 で確認済)。

```csharp
static (FormatKind, string?) DecideFormat(string? path)
{
    if (path is null) return (FormatKind.Unsaved, null);
    string ext = Path.GetExtension(path).ToLowerInvariant();
    return ext switch
    {
        ".txt" => (FormatKind.Text, ".txt"),
        ".csv" => (FormatKind.Csv,  ".csv"),
        ".md"  => (FormatKind.Markdown, ".md"),
        ""     => (FormatKind.Other, null),           // 拡張子なし
        _      => (FormatKind.Other, ext),
    };
}
```

`DisplayName` は保存済なら `Path.GetFileNameWithoutExtension(path)`、未保存なら `state.DisplayName`(既存の「無題 N」ロジックを流用)。

## 4. 文字数カウント(核心ロジック)

`TextSnapshot.CreateReader()`(全文 string 非実体化 API・既存)+ `Rune.DecodeFromUtf16` で peak O(chunk) を維持。100MB 文書でも起動時に string を一括アロケーションしない。

### 仕様

- サロゲートペア = 1 文字(Rune 単位で数える)
- Unicode White_Space に該当する Rune は除外:
  - 半角スペース(U+0020)
  - タブ(U+0009)
  - CR(U+000D) / LF(U+000A) / VT / FF
  - 全角スペース(U+3000)
  - その他 Unicode White_Space(NBSP 等も一律除外)
- 不正 UTF-16 シーケンス(未対の high/low サロゲート等)はカウント対象外(skip)

### 実装スケッチ

```csharp
namespace yEdit.Core.DocumentInfo;

public static class CharacterCounter
{
    public static int CountVisible(TextSnapshot snap)
    {
        using var reader = snap.CreateReader();
        int count = 0;
        Span<char> buf = stackalloc char[2];
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            buf[0] = (char)ch;
            int len = 1;
            if (char.IsHighSurrogate(buf[0]))
            {
                int ch2 = reader.Read();
                if (ch2 < 0) break;                    // 未対 high → 破棄
                buf[1] = (char)ch2;
                len = 2;
            }
            var status = Rune.DecodeFromUtf16(buf[..len], out Rune rune, out _);
            if (status == OperationStatus.Done && !Rune.IsWhiteSpace(rune))
                count++;
        }
        return count;
    }
}
```

**設計判断**: Ctrl+Alt+P 側の位置照会が CRLF=1 論理文字(サロゲート=2)を採る一方、本ダイアログは「人間に自然な文字数」を優先し **CRLF=1(空白として除外)/サロゲート=1(Rune)** とする。両者は異なる文脈の指標として棲み分ける(Ctrl+Alt+P は編集位置指標・本ダイアログは文書全体の内容量指標)。この不一致は意図的で、README / CLAUDE.md への追記は不要(コード内コメントに理由を明記する)。

## 5. Formatter 出力仕様

- 数値: `ToString("N0", CultureInfo.InvariantCulture)`(三桁カンマ区切り)
- 日時: `yyyy-MM-dd HH:mm:ss` を `CultureInfo.InvariantCulture` で整形(ローカル時刻)
- 改行: `\r\n`(WinForms TextBox が期待する形式)
- 各項目間の順序は本書 §0 サンプルの通り固定
- 未保存等で null な項目は値部分を `-` に置換(項目ラベルは残す)

### 通常例(既存 .txt 保存済)

```
ファイル名: aaa
形式: テキスト(.txt)
保存ディレクトリ: d:\hogehoge
文字数: 1,234
文字コード: UTF-8 (BOM付き)
改行コード: CRLF
ファイルサイズ: 2,048 バイト
作成日時: 2026-07-25 10:30:15
更新日時: 2026-07-25 12:45:00
```

### 未保存例

```
ファイル名: 無題 1
形式: -
保存ディレクトリ: -
文字数: 0
文字コード: UTF-8
改行コード: CRLF
ファイルサイズ: -
作成日時: -
更新日時: -
```

### 拡張子なし既存ファイル(例: `d:\repo\README`)

```
ファイル名: README
形式: その他(拡張子なし)
保存ディレクトリ: d:\repo
...
```

### 未知拡張子(例: `foo.ini`)

```
...
形式: その他(.ini)
...
```

### CSV モード時(末尾 1 行追加)

```
...
更新日時: 2026-07-25 12:45:00
CSV: 100 行 × 5 列
```

### EncodingLabel の整形規則

- BOM 付き: `"{Encoding.WebName の慣用表記} (BOM付き)"` — 例: `UTF-8 (BOM付き)`、`UTF-16 LE (BOM付き)`
- BOM 無し: 表記のみ — 例: `UTF-8`、`Shift_JIS`、`EUC-JP`
- 表記マップは既存 `EncodingCatalog` の CodePage → 表示名を再利用(重複させない)。無い場合は Formatter 内に小さい switch を持つ(ケース列挙は Core.Tests で網羅)。

## 6. Dialog UI 詳細(GoToLineDialog 相当)

```csharp
namespace yEdit.App;

public sealed class DocumentInfoDialog : Form
{
    public DocumentInfoDialog(string text)
    {
        Text = "文書情報";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 280);          // AutoSize は使わない(TextBox 内容量に依存させたくない)

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = text,
            TabStop = true,
        };
        // 初期フォーカスは TextBox(SR がすぐ内容を読める)

        var closeBtn = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Dock = DockStyle.Right,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = new Padding(6),
        };
        buttonPanel.Controls.Add(closeBtn);

        Controls.Add(textBox);
        Controls.Add(buttonPanel);
        AcceptButton = closeBtn;   // Enter で閉じる
        CancelButton = closeBtn;   // Esc で閉じる
    }
}
```

**SR 動作**: フォーカスが TextBox に入ると SR は Multiline の初期読み上げで内容全体を通し読みし、以降はキャレット移動で行単位/文字単位に読める。yEdit 側から `IAnnouncer.Say` を呼ばない(Windows/SR の標準動作に委ねる)。

**フォント**: 既定を使用(等幅にしない)。プロポーショナルでも「ラベル: 値」構造は SR にとって透過。

## 7. Controller と結線

### DocumentInfoController

```csharp
namespace yEdit.App;

public sealed class DocumentInfoController
{
    private readonly DocumentManager _docs;
    private readonly IFileMetaProvider _meta;

    public DocumentInfoController(DocumentManager docs, IFileMetaProvider? meta = null)
    {
        _docs = docs;
        _meta = meta ?? FileMetaProvider.Instance;    // seam(テスト差し替え可)
    }

    public void Show(IWin32Window owner)
    {
        var doc = _docs.Active;
        if (doc is null) return;
        var info = DocumentInfoBuilder.Build(
            doc.State,
            doc.Editor.CurrentBuffer.Current,
            _meta.TryGet(doc.State.Path),
            doc.State.CsvMode ? doc.ParseCsv() : null
        );
        string text = DocumentInfoFormatter.Format(info);
        using var dlg = new DocumentInfoDialog(text);
        dlg.ShowDialog(owner);
        doc.FocusTarget.Focus();  // 閉じたら編集領域へ戻す
    }
}

public interface IFileMetaProvider
{
    FileMeta? TryGet(string? path);
}

public sealed class FileMetaProvider : IFileMetaProvider
{
    public static readonly FileMetaProvider Instance = new();
    public FileMeta? TryGet(string? path)
    {
        if (path is null) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return new FileMeta(fi.CreationTime, fi.LastWriteTime, fi.Length);
        }
        catch { return null; }  // ネットワーク遮断・権限拒否等はサイレントに「-」に落ちる
    }
}
```

### MainForm.BuildFileMenu(順序変更のみ)

```csharp
// 現状: ... 名前を付けて保存 / Separator / タブを閉じる(&W) / 終了(&X)
// 変更後:
//        ... 名前を付けて保存
//        ToolStripSeparator
//        文書情報(&I)         ← 新規追加
//        タブを閉じる(&W)
//        終了(&X)
```

Ctor で `_docInfo = new DocumentInfoController(_docs);` を追加。

### MainForm.AnnouncePosition 変更

```csharp
// 変更前
private void AnnouncePosition()
{
    var ed = _docs.Active?.Editor;
    if (ed is null) return;
    int line = ed.CurrentLine + 1;
    int totalLines = ed.LineCount;
    int column = ed.GetColumn(ed.CurrentPosition) + 1;
    var (s, e) = ed.GetSelectionCharRange();
    var snap = ed.CurrentBuffer.Current;
    int totalLogical = snap.CharLength - snap.CountCrlfPairs(0, snap.CharLength);
    int selLogical = (e - s) - snap.CountCrlfPairs(s, e);
    _announcer.Say(PositionFormatter.Format(line, totalLines, column, totalLogical, selLogical, ed.Overtype));
}

// 変更後
private void AnnouncePosition()
{
    var ed = _docs.Active?.Editor;
    if (ed is null) return;
    int line = ed.CurrentLine + 1;
    int totalLines = ed.LineCount;
    int column = ed.GetColumn(ed.CurrentPosition) + 1;
    _announcer.Say(PositionFormatter.Format(line, totalLines, column, ed.Overtype));
}
```

`snap.CountCrlfPairs` は他呼び出し無し(Grep で確認)なので、Ctrl+Alt+P からの削除で本メソッドは呼び出しゼロになる。ただし将来他の場所から使われる可能性を残し、`TextSnapshot.CountCrlfPairs` API 自体は残す(削除は §Task 9 のスコープ外)。

### PositionFormatter シグネチャ

```csharp
// 変更前
public static string Format(int line, int totalLines, int column,
                             int totalChars, int selectionLength, bool overtype = false)

// 変更後
public static string Format(int line, int totalLines, int column, bool overtype = false)

// 戻り値例:
// - 通常   : "行 5 / 全 100、桁 3"
// - 上書き : "行 5 / 全 100、桁 3、上書き"
```

XML doc コメントも「文字数 M・選択 K 文字」の記述を削除する。

## 8. エラーハンドリング

- **File I/O 失敗**: `FileMetaProvider.TryGet` で try-catch し `null` を返す。Formatter が `-` に描画。MessageBox は出さない(致命度低)
- **不正 encoding**: `EncodingCatalog` に無い CodePage は Formatter 内 switch のフォールバック(`Encoding.WebName` そのまま or `"Code Page {n}"`)
- **不正 CSV パース**: `ParseCsv` が例外を投げた場合は Controller で try-catch し、CSV 行を「CSV: -」に落とすか非表示にする(§Task 3 で単体テスト時に方針確定)
- **ダイアログ表示中の Active 変更**: モーダルなのでダイアログを閉じるまで他 UI は操作不可 → 発生しない

## 9. §3 開発フロー適用

- **区分**: 中規模変更(Core 抽象追加 + App Dialog 追加 + 既存 API シグネチャ変更 + Ctrl+Alt+P 経路変更)。§3 の簡略化基準(数十行/単一ファイル)は該当しない
- **タスク毎レビュー**: 実施(仕様レビュー)
- **前倒し脆弱性レビュー**: **不要**。File I/O は既存 `DocumentState.Path` の参照利用のみで新規外部入力なし。パスに対する `FileInfo` 構築は既存の File 操作(FileController 等)と同経路
- **前倒し品質レビュー**: **不要**。`KinsokuFormatController` / `PositionFormatter` と同型パターン
- **最終ブランチ 2 パスレビュー**: 実施(品質パス + 脆弱性パス・独立エージェント)。ミューテーション検証は境界の効いた `CountVisibleCharacters` テストと `DocumentInfoBuilder` の Format 判定テストで実施

## 10. テスト戦略(§5 の 5 層)

### L1 Core.Tests(主戦場)

- `tests/yEdit.Core.Tests/DocumentInfo/CharacterCounterTests.cs`
  - 空文書 → 0
  - 全 CR / 全 LF / 全 CRLF → 0
  - 半角スペースのみ → 0
  - タブのみ → 0
  - 全角スペースのみ → 0
  - "abc" → 3
  - "a b\tc" → 3
  - "行1\r\n行2" → 4
  - サロゲートペア単発("𩸽") → 1
  - サロゲート + 空白混在("𩸽 𩸽") → 2
  - 不正 high サロゲート単独 → 0
- `tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoBuilderTests.cs`
  - 未保存(Path=null) → Format=Unsaved / Extension=null / Directory=null / 日時系=null
  - `.txt` 保存済 → Format=Text / Extension=".txt"
  - `.TXT`(大文字)→ Format=Text / Extension=".txt"(正規化)
  - `.md` / `.MD` → Format=Markdown
  - `.csv` → Format=Csv
  - `.ini` → Format=Other / Extension=".ini"
  - 拡張子なし(`README`)→ Format=Other / Extension=null
  - CsvMode=true → Csv フィールドが (Rows, Cols) を持つ
  - CsvMode=false → Csv フィールド=null
  - FileMeta 注入時 → CreationTime/LastWriteTime/FileSizeBytes が反映
  - FileMeta=null 注入時 → 日時系すべて null
- `tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoFormatterTests.cs`
  - 通常ケース(§5 サンプル文字列固定)
  - 未保存ケース(§5 サンプル文字列固定)
  - 拡張子なしケース
  - 未知拡張子(`.ini`)ケース
  - CSV モード時の追加行
  - 大きな数値の三桁区切り(`1,234,567 バイト`)
  - Encoding + BOM 有無の切替
- `tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs`
  - 既存テストのシグネチャ変更に追従(`totalChars` / `selectionLength` の分岐テストは削除)
  - 通常: `行 5 / 全 100、桁 3`
  - 上書き: `行 5 / 全 100、桁 3、上書き`

### L3 App.Tests

- `tests/yEdit.App.Tests/DocumentInfoControllerTests.cs`
  - `_docs.Active == null` のときガードで即 return(ダイアログを開かない)
  - `IFileMetaProvider` を stub 差し替え、Builder に FileMeta が伝播することを検証(Dialog は Show せず Builder/Formatter の呼び出しをアサートする smoke)
- `tests/yEdit.App.Tests/MainFormSmokeTests.cs`
  - [ファイル] メニュー内に「文書情報(&I)」項目が存在し、[タブを閉じる] より上に配置されていることを検証

### L2 Editor.Tests

- 変更なし(SR 経路に触れないため)

### L4 性能

- 100MB CSV / テキストで文書情報ダイアログの起動時間をスポットチェック(自動化不要・手動)
- 期待: 数秒以内。`CreateReader` + Rune 反復で string 一括アロケーション回避済

### L5 実機 SR

- **原則不要**。UIA プロバイダ / Announcer 経路に変更なし
- **推奨**(軽度): Ctrl+Alt+P の発話内容変化(「文字数」「選択」が消える)を NVDA で 1 分ドライブ確認

## 11. タスク分割(実装計画で詳細化)

1. **Core**: `DocumentInfo` record + `FormatKind` enum + `FileMeta` 値型(型定義のみ)
2. **Core**: `CharacterCounter.CountVisible` 実装 + 単体テスト(境界網羅・ミューテーション適用対象)
3. **Core**: `DocumentInfoBuilder.Build` 実装 + 単体テスト(Format 判定の網羅)
4. **Core**: `DocumentInfoFormatter.Format` 実装 + 単体テスト(文字列固定)
5. **App**: `IFileMetaProvider` + `FileMetaProvider` 実装
6. **App**: `DocumentInfoDialog` 実装(GoToLineDialog パターン踏襲)
7. **App**: `DocumentInfoController` 実装 + 単体テスト(seam 経由の smoke)
8. **App**: `MainForm` メニュー配線 + Ctor 変更 + `MainFormSmokeTests` 追従
9. **Core+App**: `PositionFormatter` 引数縮小 + `MainForm.AnnouncePosition` 縮小 + `PositionFormatterTests` 追従
10. **最終ブランチ 2 パスレビュー**: 品質パス(ミューテーション検証スポットチェック含む)+ 脆弱性パス(独立エージェント)

タスクの粒度は「1 タスク = 1 実装 + 1 レビュー」を基本とし、Task 1〜4 は Core 層のみで単体テスト完結、Task 5〜8 は App 層、Task 9 は既存経路の縮小(退行防止テスト付き)。

## 12. 参照・関連メモリ

- [[crlf-atomic-caret]] — CRLF=1 論理文字方針の起点。本件は同方針を継続しつつ、文書情報ダイアログ側は F-3(サロゲート=1)を採用して棲み分け
- [[test-strategy]] — 5 層テスト戦略
- [[claude-md-process-doc]] — §3 開発フロー
- 既存の `KinsokuFormatController` / `PositionFormatter` — Core 純ロジック + App 薄い層の同型パターン参考
