# B3: CSV F2 セル確定の座標陳腐化(M-25) 設計書

策定日: 2026-09-01 / ベース: main `fd9205b`(PR #61 = B2 マージ後)

傘設計書は `docs/plans/2026-08-31-v0.2-remaining-work-design.md`(B3)。監査の一次資料は
`docs/plans/2026-08-22-v0.2-release-bug-audit.md` §6 の M-25。本書は**策定時スナップショット**
(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行い、後日書き換えない。

## 1. 対象

監査 M-25:

> CSV F2 編集中に Ctrl+S(セル内 LF を含む混在 EOL)→ `ConvertEols` 差替え後に古い
> `start/length` でコミットし別位置を書換 —— `App/CsvController.cs:250-267`

傘設計書 §3 が**データ破壊級**に再分類した 2 件のうちの 1 件(もう 1 件 M-12 は B4)。

## 2. 現状の機構(実コードで確認)

`CsvController.BeginEdit` は F2 開始時点のセルスパンをクロージャへ捕捉し、確定時にそのまま使う。

```csharp
// CsvController.cs:244-251
var f = csv.GetField(row, col);
...
int start = f.Start,
    length = f.Length;
...
// CsvController.cs:262-272
onCommit: text =>
{
    string serialized = CsvWriter.EscapeField(text);
    bool wasRo = ed.ReadOnly;
    ed.ReadOnly = false;
    ed.ReplaceCharRange(start, length, serialized);   // ← 開始時の座標
    ed.ReadOnly = wasRo;
    var csv2 = doc.ParseCsv();
    if (csv2.Ok)
        ApplyCell(ed, csv2, row, col, announce: true);
    ...
}
```

`start`/`length` は**開始時のスナップショット上の座標**である。開始から確定までの間に本文が
差し替わると、この 2 つの整数だけが古い世界を指したまま残る。`row`/`col` と `doc` は
クロージャ内で再解決されている(`doc.ParseCsv()` は `Document.cs:40-48` でスナップショット
参照が変われば必ず再パースする)ため、**陳腐化するのは座標だけ**である。

### 2.1 何が座標をずらすか

`FileController.SaveDocument` は書き込み前に `doc.Editor.ConvertEols(doc.Editor.EolMode)`
(`FileController.cs:868`)を呼ぶ。EOL がすでに統一済みなら `EditorControl.cs:501-505` の
fast-path で `EolMode` だけ更新して抜けるが、**混在していればバッファを丸ごと差し替える**。
LF → CRLF なら変換点以降の char オフセットが 1 ずつ後ろへずれる。

### 2.2 なぜ Ctrl+S が F2 編集中に届くのか

`MainForm.ProcessCmdKey` の CSV 素キー横取りは `!_csv.IsEditing`(`MainForm.cs:722`)で
自分を無効化し、オーバーレイ TextBox に通常編集させる。その結果 Ctrl+S は
`base.ProcessCmdKey` → メニューショートカット(`MainForm.cs:796` = `_file.Save()`)へ
**素通りする**。メニューショートカットの処理はフォーカスを奪わないので、オーバーレイは
開いたまま残る。

## 3. 到達経路の実測 —— なぜ「Ctrl+S だけ」なのか

前置ガードの列挙は原理的に漏れる(監査 §9 V-7)。それでも**現状の到達面がどこまで狭いかは
事実として書いておく**。修正の置き場所を決める根拠になるからである。

| 経路 | 本文を変えるか | F2 編集中の扱い | 出典 |
|------|---------------|----------------|------|
| **Ctrl+S(上書き保存)** | **変える**(`ConvertEols`) | **素通り・フォーカスも奪わない** | `MainForm.cs:722, 796` / `FileController.cs:868` |
| Ctrl+Shift+S(名前を付けて保存) | 変える | `SaveFileDialog` がフォーカスを奪う → `OnLostFocus` → `CancelEdit` | `CsvCellEditor.cs:99-103` |
| 置換 / 一括置換 | 変える | CSV モードで拒否・発声 | `SearchController.cs:275, 505` |
| 禁則整形 | 変える | CSV モードで拒否・発声 | `KinsokuFormatController.cs:40-42` |
| Undo / Redo | 変える | `ReadOnly` ガードで no-op(CSV モードは `ReadOnly=true`) | `EditorControl.cs:1680, 1795` |
| バックアップ(hot exit) | 変えない(ディスクへ書くだけ) | — | — |
| メニューを**クリック**して保存 | 変える | クリックでフォーカスが移る → `CancelEdit` | `CsvCellEditor.cs:99-103` |

**到達するのは Ctrl+S のショートカット経路 1 本**である。ただしこの表は現在の配線の写像で
あって不変条件ではない —— メニュー項目が 1 つ増えるだけで崩れる。したがって**修正は
「Ctrl+S を塞ぐ」側ではなく「確定が座標を持ち越さない」側に置く**。

### 3.1 保存は無発声である(虚偽発声には当たらない)

`FileController` は `IAnnouncer` を一切持たない(`grep` で 0 件)。F2 編集中の Ctrl+S は
**編集前の内容をディスクへ書くが、成功の発声はしない**。修正後は「ディスク = 編集前 /
バッファ = 編集後 / `Modified` = true」となり、タブの状態表示と一致する。
したがって B5「実際と違うことを言わない」の対象ではなく、**Ctrl+S 自体を塞ぐ変更は入れない**
(§7 の申し送りに残す)。

## 4. 設計 —— 確定時に解決し直す

**`onCommit` のクロージャから座標を消す。** 確定時のパースから `(row, col)` で解決し直し、
解決先が「F2 を開いたセルと同じ」ことを検証してから書く。ガードを足すのではなく、
**陳腐化しうる値をそもそも持ち越さない**構造にするのが芯である。

```csharp
onCommit: text =>
{
    // 確定時点のスナップショットで解決し直す。ParseCsv はスナップショット参照が
    // 同一なら開始時と同じインスタンスを返すので、変化が無ければ追加コストは無い。
    var csvNow = doc.ParseCsv();
    var target = csvNow.Ok ? csvNow.GetField(row, col) : null;
    if (target is null || !SameCell(target.Value, f.Value))
    {
        _announcer.Say(CsvAnnounceFormatter.CommitTargetChanged);
        return;                                  // 書かない
    }
    string serialized = CsvWriter.EscapeField(text);
    bool wasRo = ed.ReadOnly;
    ed.ReadOnly = false;
    ed.ReplaceCharRange(target.Start, target.Length, serialized);
    ed.ReadOnly = wasRo;
    var csv2 = doc.ParseCsv();
    ...(以降は現行と同じ)
}
```

### 4.1 `(row, col)` を真実源にしてよい根拠

CSV モードの現在セルは `DocumentState.CsvRow` / `CsvCol` が真実源であり
(`CsvController.cs` クラス doc)、**F2 編集中はこれを動かす経路が閉じている**:
ナビ系はすべて `TryContext` を通り、その冒頭が `if (_editor.IsEditing) return false;`
(`CsvController.cs:295-296`)で撥ねる。`ProcessCmdKey` の素キー横取りも `!_csv.IsEditing`
で無効。したがって確定時の `(row, col)` は F2 を開始したときの `(row, col)` と等しい。

### 4.2 なぜ「セル同一性の検証」を足すのか

`(row, col)` が同じでも、本文が変わっていれば**その座標が指すセルが別物になっている**ことが
ありうる(行が消える・列が増える等)。そこへ書けば陳腐化した座標へ書くのと同じ
データ破壊になる。`(row, col)` は「どのセルを編集していたか」を一意に決めないので、
**開始時に読み取ったセル値との一致**を同一性の代用とする。

一致しなければ書かずに発声する。ここは「安全側に倒して編集を捨てる」枝であり、
§3 の表にあるとおり現行の配線では到達経路が無い —— 将来配線が増えたときの受け皿である。

### 4.3 EOL 正規化が必須である理由

`CsvParser` は**引用符内の CR / LF を literal として `Value` に積む**
(`CsvParser.cs:117-124` のコメント「引用符内のカンマ・改行も literal」)。したがって
セル内改行を持つセルでは `ConvertEols` が `Value` 自体を書き換える(`x\ny` → `x\r\ny`)。
素の文字列比較では**本件がまさに直そうとしているシナリオで不一致になる**。

比較の前に両辺を `\r\n` / `\r` → `\n` へ正規化する。これは `CsvCellEditor.Commit`
(`CsvCellEditor.cs:112`)が確定値に対して既に行っている正規化と同一の規則である。
**規則の持ち主を 1 か所にする**ため、`CsvWriter` に正規化を公開し、`CsvCellEditor.Commit`
もそれを呼ぶ形へ寄せる(挙動不変)。

### 4.4 `ConvertEols` は CSV の行列構造を変えない(論証・実測は実装時)

- 引用符内の CR / LF は literal として `Value` に入るだけで、行区切りにはならない。
- 引用符外では `\r\n`(`lb=2`)・`\r` 単独・`\n` 単独のいずれも**改行 1 個**として扱われる
  (`CsvParser.cs:148-164`)。
- `ConvertEols` は CR / LF 以外のバイトに触れない(`EditorControl.cs:487-494` の target は
  ASCII の CR / LF のみ)。

以上から `ConvertEols` の前後で行数・各行の列数・各セルの正規化後 `Value` は不変であり、
§4 の同一性検証は Ctrl+S 経路で**必ず一致する**はずである。**これは論証であって実測ではない**。
実装時にテストで固定する(§5)。

### 4.5 採らなかった案

- **一律で確定を中止する**(スナップショットが変わっていたら内容を問わず書かない)。実装は
  最小でヒューリスティックがゼロだが、Ctrl+S → Enter という自然な操作でユーザーの入力が
  失われる。**ユーザー判断で不採用**(2026-09-01)。
- **F2 編集中の Ctrl+S を拒否する**。§3 のとおり前置ガードの列挙になり、かつ保存を
  塞ぐのは別の挙動変更である。§7 の申し送りへ回す。
- **B2 の `ReplaceCharRangeExact`(事後条件 seam)を使う**。傘設計書 §4.1 のとおり、
  M-25 は書込自体は「言われたところ」へ正しく落ちるため、この seam は要求どおりの値を返し
  **陳腐化を検出できない**。CSV の呼び出しは `ReplaceCharRange` のままとする
  (境界に乗る範囲しか渡さない = PR #56 §6 の巻き込み契約に触れない)。

## 5. テスト(L3 = kxEdit.App.Tests)

`tests/kxEdit.App.Tests/CsvControllerTests.cs` には既に F2 経路のフルワイヤ seam がある
(`GetCellEditor` / `GetOverlayBox` のリフレクションで `BeginEdit` → `Commit` を駆動・
同ファイル `:598-670`)。ここに足す。

| # | シナリオ | 期待 |
|---|---------|------|
| T1 | セル内 LF を含む混在 EOL で F2 開始 → `ConvertEols(Crlf)` → `Commit` | **編集したセルだけ**が新しい値になり、前後のセル・区切り・引用符が無傷 |
| T2 | 先行行に LF がありセル自身は改行を持たない状態で同上 | 同上(オフセットずれのみのケースを T1 と分離する) |
| T3 | F2 開始 → `(row,col)` が別セルを指すよう本文を差し替え → `Commit` | 本文が**変わらない**・`CommitTargetChanged` を発声 |
| T4 | F2 開始 → `(row,col)` が消えるよう行を削る → `Commit` | 同上 |
| T5 | 本文を変えずに `Commit`(既存 `BeginEdit_ThenCommit_...`) | **現行どおり**(挙動不変の確認) |
| T6 | `CsvWriter` の EOL 正規化(L1) | `\r\n` / `\r` / 混在 → `\n`・既に `\n` のみは恒等 |

T1 / T2 は **修正前の src で赤になること**を実装時に確認する(欠陥の存在証明。
`large-line-wrap-perf` で確立した手順)。T3 / T4 は現行の配線では到達経路が無い枝を
テストからだけ踏む —— この事実をテストのコメントに明記する(到達不能を「網がある」と
言い換えないため)。

### 5.1 ミューテーション検証

**実施しない。** CLAUDE.md §4-A の「有効」列(カーソル移動・選択範囲算出・UNDO/REDO・
検索置換エンジン・Lexer)のいずれにも当たらず、本件は App 層の F2 確定配線である。

## 6. L5(実機 SR 検証)

**必要**(傘設計書 §4.2)。`IAnnouncer.Say` の発声を 1 つ足すため。チェックリストは
`docs/plans/2026-09-01-csv-f2-commit-staleness-l5-checklist.md` として起こし、
傘設計書 §7.1 の「新規」欄へ合流させる。項目は最低限:

1. 混在 EOL の CSV でセル F2 → Ctrl+S → Enter。**編集したセルの値が読み上げられ**、
   前後のセルが壊れていないこと(セル移動で確認)。
2. 上記のあと Ctrl+S → もう一度開き直して内容が一致すること。
3. `CommitTargetChanged` の発声が NVDA で読まれること(到達経路が無いため、
   **L5 では確認できない**見込み。判断は実装時に確定する)。

## 7. 申し送り

- **F2 編集中の Ctrl+S は「編集前の内容」をディスクへ書く。** 本件の修正後もそれは変わらない
  (バッファ側だけが編集後になり `Modified=true` が立つ)。塞ぐ / 先に確定してから保存する /
  現状維持のいずれにするかは挙動変更の判断を伴うため v0.2 のスコープ外とする。
- `CsvWriter.EscapeField` はセル内改行を常に `\n` で書く。文書の `EolMode` が CRLF でも
  セル内だけ LF になるが、これは**本件以前からの挙動**であり本ブランチでは変えない。

## 8. 実施記録

(実装時に追記する)
