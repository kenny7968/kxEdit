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

### 8.1 Task 1 — EOL 正規化を `CsvWriter` へ集約(挙動不変)

`CsvCellEditor.Commit` が持っていた `\r\n` / `\r` → `\n` の置換を `CsvWriter.NormalizeEols`
へ移し、`Commit` はそれを呼ぶ形へ寄せた。§4.3 が要求する「規則の持ち主を 1 つにする」の実体。

**Step 2 で観測した赤**(逐語。リポジトリ絶対パスのみ `no-local-paths` フックの規約に従い
`<repo>` へ置換):

```
<repo>\tests\kxEdit.Core.Tests\Csv\CsvWriterTests.cs(54,78): error CS0117: 'CsvWriter' に 'NormalizeEols' の定義がありません [<repo>\tests\kxEdit.Core.Tests\kxEdit.Core.Tests.csproj]
```

同文が 5 箇所(新規テスト 5 本すべて)。計画の予測どおり。

**集約が実効を持つことの確認**: `src/` 全体を `Replace("\r` で grep し、他の EOL 正規化サイトが
**0 件**であることを確認した。比較側が別規則を持つ余地は残っていない。

### 8.2 Task 1 — 計画からの逸脱

| 逸脱 | 理由 |
|------|------|
| テストメソッド名を計画の PascalCase(`NormalizeEols_Crlf_BecomesLf` 等)から既存様式へ(`Crlf_is_normalized_to_lf` 等) | `CsvWriterTests.cs` の既存は `Plain_value_is_unchanged` / `Lf_is_quoted` =「先頭のみ大文字 + snake」。計画の名前だけが浮く |
| `CsvCellEditor.cs` へ `using kxEdit.Core.Csv;` を足さなかった | 既に 1 行目にあった(計画の「確認すること」を充足) |
| `ArgumentNullException` を無修飾で書いた | 同ファイルの既存は `System.StringComparison` と修飾するが、`Directory.Build.props` の `ImplicitUsings=enable` で無修飾が有効。IDE0001 等の警告も出ない |
| CSharpier が `Crlf_is_normalized_to_lf` の式本体を 2 行へ折った | pre-commit フックの整形。再 stage して同一 commit に収めた |

### 8.3 Task 1 レビュー Important-1 — 「どの変異を殺すか」の記述が偽だった

`Lf_only_value_is_not_changed_by_normalize` に「過剰置換=CRLF を 2 個の LF にする変異を殺す」
と書いていたが**偽**。入力 `"a\nb\n"` は CR を 1 文字も含まないので、CR の扱いを変える変異は
**すべて**この fixture 上で恒等になる。

**結論(3 変異はすべて殺される)は正しく、根拠(どのテストが殺すか)だけが誤っていた。**
[[rationale-not-just-conclusion]] の類型が本ブランチでも出た形。

訂正にあたり、**変異を実際に注入して**殺したテストを実測した(実装者による再実測。
レビュー担当は独立に 6 変異 × 7 ケースの総当たりで同じ表を得ている):

| 変異 | 実測で落ちたテスト |
|------|--------------------|
| 置換順序の入替(`\r→\n` を先に) | `Crlf_...` / `Mixed_...`(2 失敗) |
| `Replace("\r\n","\n")` を削る | `Crlf_...` / `Mixed_...`(2 失敗) |
| `Replace("\r","\n")` を削る | `Lone_cr_...` / `Mixed_...`(2 失敗) |
| 過剰置換(CRLF→LF 2 個) | `Crlf_...` / `Mixed_...`(2 失敗) |
| 丸ごと no-op(`return value;`) | `Crlf_...` / `Lone_cr_...` / `Mixed_...`(3 失敗) |
| 末尾 LF を落とす(`TrimEnd('\n')`) | **`Lf_only_...` のみ**(1 失敗) |
| `ArgumentNullException.ThrowIfNull` を削る | **`NormalizeEols_throws_on_null_value` のみ**(1 失敗) |

`Lf_only_...` の存在価値は「このファイルで末尾に改行を持つ唯一の fixture」であって
過剰置換の網ではない。コメントはこの実測どおりに書き直した。

**同じ型を訂正作業中に自分で再発させた**(fixup `0ca02be`)。書き直したコメントに
「(順序入替・crlf 削除・過剰置換・no-op の)いずれも CRLF が LF 2 個へ化けるため落ちる」と
書いたが、**no-op では CRLF は CRLF のまま残る**ので理由節が偽。列挙した変異の数も
数え間違えていた。**「殺される」という結論だけを検算して理由節を検算しないと、訂正のたびに
同じ穴が空く。** 理由節も 1 つずつ変異を当てて確かめること。

### 8.4 Task 1 レビュー Important-2 — 呼出側(`Commit`)に網が 1 本も無かった

計画 Step 6 の観点「`Commit` の置換が挙動不変か(既存テストで足りているか)」の答えは **No** だった。
既存の F2 テストは確定値が `"NEW"` / `"SHOULD_NOT_APPLY"` で **CR を 1 文字も含まない**ため、
`string text = _box.Text;`(正規化を丸ごと落とす変異)が**全テスト緑のまま生存**していた。

しかもこれは到達不能な枝ではない。**Alt+Enter がセル内改行として `"\r\n"` を TextBox へ
挿入する**(`CsvCellEditor.cs:79`)ので、CR を含む確定値は実運用の主経路である。

`BeginEdit_ThenCommit_NormalizesCrlfInCellValue_BeforeSerializing`(L3)を追加し、
変異を注入して赤を実測した:

```
Assert.Equal() Failure: Strings differ
Expected: ""x\ny",a2,a3\nb1,b2,b3\nc1,c2,c3"
Actual:   ""x\r\ny",a2,a3\nb1,b2,b3\nc1,c2,c3"
```

同じ実行で既存の `BeginEdit_ThenCommit_Replaces...` は**緑のまま**であり、
「既存テストがこの変異を素通しする」というレビューの指摘も同時に実証された。

**教訓**: 規則を関数へ括り出したとき、網は**関数側だけでなく呼出側にも**要る。
`NormalizeEols` 側の 6 本は「規則が正しいこと」しか守らず、
「呼出側がその規則を使い続けること」は 1 ビットも守らない。
リファクタで「移しただけ」の箇所こそ、移した先の網が移す前の網を代替していないか確かめる。

### 8.5 Task 1 — §4.3 の根拠は実読で真と確認された

§4.3 が引く「`CsvParser` は引用符内の CR / LF を literal のまま `Value` へ積む」は、
レビュー担当の実読で真と確認された。`inQuotes` ブロックが `"` 以外の文字を無条件に
`sb.Append(c)` する構造(「引用符内のカンマ・改行も literal」コメントの直上)で、
CR / LF に特別扱いが無い。**§4.3 は想定ではなく実測として扱ってよい。**

### 8.6 Task 1 コード品質レビュー I-2 —— 連続改行の網が無く「畳み込み」変異が生存していた

**最も重い指摘。** 当時の L1 8 ケース(`"a\r\nb"` / `"a\rb"` / `"a\r\nb\rc\nd"` / `"a\nb\n"` /
`""` / `"abc"` / `"a,b\"c"` / `null`)は**隣接した改行を 1 つも含まず**、次の変異が全緑で生存していた:

```csharp
return Regex.Replace(value, "(\r\n|\r|\n)+", "\n"); // 改行の連続を 1 個へ畳む
```

**そしてこれは無害な変異ではない。** `NormalizeEols` は比較専用ではなく、
`CsvCellEditor.Commit` が**本文へ書く値そのもの**を作る。Alt+Enter を 2 回押して作った
確定値 `"x\r\n\r\ny"` は正しくは `"x\n\ny"`、変異下では `"x\ny"` ——
**ユーザーが入れた空行が黙って消える。** §8.4 で足した L3 の網も fixture が単発改行
(`"x\r\ny"`)だったため、これを捕まえられていなかった。

**単発改行だけの fixture は「改行ごとに 1 個」と「連続を 1 個へ畳む」を区別できない。**
CLAUDE.md §4-B の「partial-selection の fixture は prefix / suffix を除外できる形にする
(全選択と区別する)」と**同型の穴**である。

対応: L1 に隣接改行の Theory 3 ケースを追加し、L3 の fixture を `"x\r\n\r\ny"` へ差し替えた
(元の kill 対象を殺す力は保ったまま畳み込みも殺す)。L3 で観測した赤:

```
Assert.Equal() Failure: Strings differ
Expected: ""x\n\ny",a2,a3\nb1,b2,b3\nc1,c2,c3"
Actual:   ""x\ny",a2,a3\nb1,b2,b3\nc1,c2,c3"
```

### 8.7 Task 1 コード品質レビュー I-1 / M-4 —— kill 主張の再発(3 回目)と、手順化

コメントの「(順序入替・crlf 削除・過剰置換・**丸ごと no-op** の)いずれも本テストと
`Mixed_...` の **2 本**が落ちる」は、no-op に対して**偽**(no-op は **3 本**落ちる)。

**決定的なのは、同じブランチの一次資料 2 つが既に「3 本」と書いていたこと**である ——
§8.3 の変異表と、訂正 commit `0ca02be` のコミットメッセージ本文。
**`0ca02be` は本文には正しい実測を書きながら、コメント側に誤りを残した**
= 同じ commit の中で記述が食い違っていた。「結論は正しいが理由節が偽」の型の **3 回目**。

**手順として明記する: 理由節を書く前に「変異 × ケース」の表を作り、表から書き写す。**
前 2 回はいずれも表を作らずに理由節を書いて外している。今回作り直した実測表
(✗ = そのテストが落ちる):

| テスト(fixture) | (a) 順序入替 | (b) crlf 削除 | (c) cr 削除 | (d) 過剰置換 | (e) no-op | (f) 畳み込み |
|---|---|---|---|---|---|---|
| `Crlf_` `"a\r\nb"` | ✗ | ✗ | – | ✗ | ✗ | – |
| `Lone_cr_` `"a\rb"` | – | – | ✗ | – | ✗ | – |
| `Mixed_` `"a\r\nb\rc\nd"` | ✗ | ✗ | ✗ | ✗ | ✗ | – |
| `Adjacent_` `"a\r\n\r\rb"` | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| `Adjacent_` `"a\n\rb"` | – | – | ✗ | – | ✗ | ✗ |
| `Adjacent_` `"\r\n\r\n"` | ✗ | ✗ | – | ✗ | ✗ | ✗ |
| `Lf_only_` `"a\nb\n"` | – | – | – | – | – | – |
| 素通し 3 ケース + `null` | – | – | – | – | – | – |
| **Core の失敗テスト数** | **4** | **4** | **4** | **4** | **6** | **3** |
| L3 `"x\r\n\r\ny"` | ✗ | ✗ | – | ✗ | ✗ | ✗ |

別枠の 2 変異: 末尾 LF 落とし(`TrimEnd('\n')`)= `Lf_only_` と `Adjacent_("\r\n\r\n")` の
**2 本**が落ちる / null ガード削除 = null テストのみ **1 本**。

この表から分かったこと 2 つ(どちらも**表を作らなければ気付けなかった**):

- `Mixed_` は (a)〜(e) の 5 種を単独で殺すが **(f) は殺せない**。「最強の 1 本」ではない。
- **fixture を足したことで既存コメントの別の主張も偽になった。** `Lf_only_` の
  「単独で殺すのは末尾の改行を落とす変異のみ」は、`"\r\n\r\n"` 追加後は TrimEnd がそちらでも
  落ちるので「単独で」が成立しない。**網を足したら、既存の kill 主張を再測定すること。**

M-4 も同時に対応: 「CRLF が LF 1 個へ畳まれない変異はここで落ちる」は全称命題として
書かれていたが、実際は**この fixture 上に限られる**。射程を 1 語で締めた。

### 8.8 Task 1 コード品質レビュー I-3 —— XML doc が比較の当事者を取り違えていた

`CsvWriter.NormalizeEols` の doc は「F2 確定値と**パース結果の値を比較する側**は…」と書いていたが、
Task 3 が比較するのは**開始時のパース結果と確定時のパース結果**であり、
**F2 確定値はどことも比較されない**(揃えた上で直列化されて書かれるだけ)。
§4.3 の原文は「比較の前に**両辺**を正規化する。これは `Commit` が確定値に対して既に行っている
正規化と**同一の規則である**」と正しく書き分けているのに、doc への圧縮でその区別が潰れていた。

この doc は共通規則の**唯一の契約文**で、Task 3 の実装者が最初に読む場所である。読んだとおりに
実装すると「`NormalizeEols(text)` と `target.Value` を比べる」という誤った guard になりかねない。
用途を (1) 直列化前に揃える / (2) 2 つのパース結果の両辺を揃える の 2 つへ分けて書き直し、
あわせて **§8.6 で判明した「連続は畳まない」契約**も doc に明記した。

### 8.9 §4.4 の「改行 1:1 対応」には既に L2 の実測がある

§4.4 は「これは論証であって実測ではない」と結んでいるが、**一番効く前提については
既に committed な L2 の網がある**とレビューで指摘され、実物を開いて確認した(真)。

`tests/kxEdit.Editor.Tests/EditorControlConvertEolsTests.cs`:

- `ConvertEols_Utf8_MixedEols_AllConvertedToTarget` が `"a\r\nb\rc\nd\r\ne"` →
  `"a\r\nb\r\nc\r\nd\r\ne"` を固定。**単独 CR が 1 改行として CRLF 1 個へ写る**
  (2 個に増えない・隣と併合しない)ことが押さえられている。
- `ConvertEols_TrailingLoneCr_ToLf_DrainedByPendingCr` が `"abc\r"` → `"abc\n"` を固定し、
  末尾孤立 CR の drain 経路も押さえている。

**ただし §4.4 の全体が実測になったわけではない。** これらが押さえるのは `ConvertEols` 側の
改行 1:1 対応だけで、§4.4 が併せて主張する `CsvParser` 側(引用符内の CR / LF が行区切りに
ならない・引用符外で CRLF / CR / LF がいずれも改行 1 個)は別の網が要る。
**§4.4 の本文は策定時スナップショットとして書き換えない**(CLAUDE.md §8)。

### 8.10 Task 1 コード品質レビュー —— 受容 / 却下した指摘

- **受容(② PR description に記載)**: `CsvWriter` が「直列化」と「EOL 正規化規則」の
  2 責務を持つのはやや窮屈(SRP)。呼出 2 か所の現状では分割の costs が上回ると判断。
- **受容(②)**: `EscapeField` に null ガードが無い非対称。**直すと挙動変更**になり
  Task 1 の「挙動不変」に反するため触らない。
- **却下(③)**: `NormalizeEolsToLf` への改名。呼出は 2 か所で、I-3 の doc 修正により
  「LF へ」は契約文に明示された。
- **却下(③)**: cref の粒度が不揃い。Core から Editor / App を参照できない以上
  `<c>` にするのは正しく、害がない。
- **却下(③)**: §8.1 の「5 本」が時点依存。§8 は**実施記録**であり Step 2 時点の記述として正しい。

### 8.11 Task 2 — 確定時に `(row, col)` から解決し直す(座標の持ち越しを消す)

`onCommit` のクロージャから `start` / `length` を消し、確定時の `doc.ParseCsv()` →
`GetField(row, col)` で書込先を解決し直した。§4 の芯(陳腐化しうる値をそもそも持ち越さない)の実体。

**Step 3 で観測した赤**(逐語):

```
失敗 kxEdit.App.Tests.CsvControllerTests.Commit_AfterEolConversion_WritesEditedCell_NotStaleOffsets
   Assert.Equal() Failure: Strings differ
                       ↓ (pos 10)
Expected: "a1,a2\r\nNEW,b2\r\nc1,c2"
Actual:   "a1,a2\r\nNEW",b2\r\nc1,c2"
                       ↑ (pos 10)

失敗 kxEdit.App.Tests.CsvControllerTests.Commit_AfterEolConversion_WritesShiftedCell_NotStaleOffsets
   Assert.Equal() Failure: Strings differ
                            ↓ (pos 13)
Expected: "a1,"p\r\nq"\r\nb1,NEW\r\nc1,c2"
Actual:   "a1,"p\r\nq"\r\nb1NEW2\r\nc1,c2"
                            ↑ (pos 13)
```

**計画の想定と完全一致**(T1 = 陳腐化した `length` が 1 足りず閉じ引用符が残る / T2 = 陳腐化した
`start` が 1 手前を指し区切りカンマと `b` を食う)。fixture のオフセットも手計算を数え直したうえで
一致し、期待値の修正は要らなかった。

### 8.12 Task 2 — §2.2 / §3 の前提(Ctrl+S 経路)が実測になった

`ConvertEols` は **`ReadOnly=true`(= CSV モードそのもの)のまま `true` を返し、本文を差し替えた**。
根拠は 2 つとも上の赤に含まれている:

- `Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf))` が通っている(fast-path の `false` ではない)。
- Actual の中でセル内改行が `\r\n` になっている(`"a1,a2\r\nNEW",b2...` / `"p\r\nq"`)= 実際に
  バッファが差し替わった。

`EditorControl.ConvertEols` の冒頭ガードは `if (_buffer is null) return false;` **だけ**で、
ReadOnly 判定は無い。§3 の表が「Undo / Redo は `ReadOnly` ガードで no-op」と書き分けた一方で
Ctrl+S だけが到達するとした理由が、コードの非対称として実物で確かめられた。

### 8.13 Task 2 — この欠陥に既存の網は 1 本も無かった(変異表)

書込先の 2 引数を 1 つずつ陳腐化させた総当たりを**実際に注入して**測った(注入後は毎回 revert し、
`git diff -- src/` が意図した差分だけに戻ることを確認):

| 変異 | T1 `..._WritesEditedCell_...` | T2 `..._WritesShiftedCell_...` | App 全体の失敗数 |
|------|---|---|---|
| (a) `start` / `length` を両方持ち越す(= **修正前の実装そのもの**) | ✗ | ✗ | **2 / 724** |
| (b) `start` は解決し直すが `length` を持ち越す | ✗ | – | 1 |
| (c) `length` は解決し直すが `start` を持ち越す | – | ✗ | 1 |

分かったこと 2 つ:

- **(a) を注入しても既存 722 本は全緑**。M-25 に既存の網は 1 本も無かった。
- **(b) と (c) が別々のテストを落とすので、T1 / T2 は互いに冗長でない。** T2 の編集セルは
  自分自身に改行を持たない = `ConvertEols` で長さが変わらないため、(b) は T2 の fixture 上で
  恒等になる。計画が T1 / T2 を分けた意図(「オフセットずれのみのケースを分離する」)が
  実測で裏付けられた形。

この表はテスト側のコメントへ書き写した(CLAUDE.md §4 / §8.7 の手順「理由節を書く前に
変異 × ケースの表を作り、表から書き写す」)。

### 8.14 Task 2 — 計画の欠陥 2 点(`5fa12c3` で計画側を訂正済み)

| # | 欠陥 | 実測 |
|---|------|------|
| A | Step 1 の `MutateBodyWhileEditing` ヘルパーを Task 2 に置くと**ビルドが壊れる** | Task 2 では 1 度も使わないため `error S1144: Remove the unused private method 'MutateBodyWhileEditing'`(`-warnaserror`)。実際に使う Task 3 へ移した。`using kxEdit.Editor;` も同様(Task 2 の範囲は `var` で足り、単独で足すと未使用 using になる) |
| D | Step 3 の T2 想定 Actual の脱字 | `b1NEW\r\n` ではなく `b1NEW2\r\n` が正(陳腐化した `start` が区切りカンマと `b` を食い、元の `2` が残る) |

そのほかの逸脱:

- テストの挿入位置は計画本文の「`BeginEdit_ThenCancel_...` の直後」ではなく F2 節の末尾
  (`BeginEdit_ThenCommit_NormalizesCrlfInCellValue_...` の直後)にした。計画本文は Task 1 で
  テストが 1 本増えた分だけ古い。
- `BeginEdit` 冒頭の既存コメント「開始時点のセル span(**直列化対象**)を確定」は本修正で偽になる
  (`f` は直列化対象ではなくなった)ので書き直した。**ただしその書き直し自体に射程の誤りがあり、
  §8.15 で再訂正した。**
- CSharpier が 2 ファイルを整形(コメント表の桁揃え等)。pre-commit フック内で再 stage され
  同一 commit に収まった。整形後に再ビルド + 再テストして緑を確認済み。

### 8.15 Task 2 レビュー I-1 — 「結論は正しいが射程が偽」の 4 回目

§8.14 で書き直した `BeginEdit` 冒頭のコメントに

> 使い道は「オーバーレイの配置座標(`f.Start`)と初期値(`f.Value`)」**だけ**で、
> どちらも `Begin` が同期的に読む。

と書いたが、`f` の読取箇所は **3 か所**あり列挙が 1 つ足りなかった(実物を開いて確認した):

| # | 場所 | 読む値 |
|---|------|--------|
| 1 | `CsvController.cs` の `ed.EnsureVisibleCharRange(f.Start, f.Length)` | `f.Start` / **`f.Length`** |
| 2 | `CsvCellEditor.Begin` の `ed.PointFromCharOffset(field.Start)` | `f.Start` |
| 3 | `CsvCellEditor.Begin` の `Text = field.Value` | `f.Value` |

落としていた **`f.Length` は M-25 が問題にしている 2 つの陳腐化値の片方そのもの**で、
「どこにも使われていない」と読んだ人が 1 を見て混乱する。加えて「どちらも `Begin` が読む」は
2 / 3 については真だが **1 は `Begin` の外(`BeginEdit` 自身)**であり、ここも偽だった。

**結論(確定時の書込先へは持ち越さない)は正しく、射程だけが偽。** §8.3 / §8.7 と同じ型の
**4 回目**である。3 か所を列挙し「いずれも `_editor.Begin` が戻るまでに同期的に読み切られる」へ
書き直した(fixup)。あわせて `CsvCellEditor` が `CsvField` をフィールドへ保存しないこと
(持つのは `_box` / `_closing` / `_refocus` / `_onCommit` / `_onCancel` の 5 つだけ)も
実読で確認してからコメントに書いた。

### 8.16 Task 2 レビュー M-2 — `Ok=false` 時の挙動が変わった(意図的・安全側)

CLAUDE.md §2「意図的な挙動変更は文書化する」の対象。

- **修正前**: パース結果を見ずに**まず陳腐化した座標で書き**、そのあと `csv2.Ok == false` なら
  `ParseError` を発声していた(= 壊れた本文が残る)。
- **修正後**: `csvNow.Ok == false` なら `target` が `null` になり、**1 文字も書かずに**
  `ParseError` を発声する。

安全側であり §4 の設計どおりだが、**現行の配線ではこの差は到達不能**である。F2 編集中に本文を
変える経路は §3 の表のとおり `ConvertEols` 1 本だけで、`ConvertEols` は CR / LF 以外の
バイトに触れない = 引用符の対応も行列構造も変えないため、`Ok` が `true` から `false` へ
転じることがない。到達経路が生まれるのは将来配線が増えたときである。

**ただしこの到達不能性は §4.4 の論証を引いたものであって、Task 2 では実測していない**
(`Ok=false` を踏む網は Task 2 に無い。§8.17-1 の fixture がその網の候補)。
「網がある」と読める書き方をしないこと。

### 8.17 Task 2 — Task 3 への申し送り(実装はしない・記録のみ)

1. **`csvNow.Ok` は Task 3 の「値 + 形」guard でも守られない**(レビュー I-2)。弁別する
   fixture の構成はレビュー担当が提示した ——「開始本文 `a1,a2\nb1,b2` の (0,0) で F2 開始 →
   末尾へ `\n"x` を追記」。**実物で確認した条件**(`CsvParser.cs:189`。レビューの `:188` は 1 行ずれ):

   ```csharp
   if (ok && (pos > fieldStart || row.Count > 0))
   ```

   直前の `if (inQuotes) ok = false;`(`:184`)で未終端引用符は `ok=false` になり、上の
   `if (ok && ...)` が**末尾の不完全レコードだけを rows へ混ぜない**。**条件式そのものは真と確認した。**
   ただし「`Rows.Count=2` / `Rows[0].Count=2` / `GetField(0,0)="a1"` がすべて開始時と一致する」
   という帰結は**コード読解による論証であって Task 2 では実測していない**(既存の L1
   `Unterminated_quote_is_not_ok` は `Ok=false` しか assert していない)。**Task 3 で fixture を
   書いて実測すること。**
2. **早期 return はセル強調を復元しない**(レビュー M-1)。`ConvertEols` は本文差し替えの直後に

   ```csharp
   _cellHighlight = null; // 変換前オフセット由来のセル強調は無効化(EOL 変換で位置がずれる)
   ```

   を実行する(`EditorControl.cs:592`。**実物で確認して真**)。Task 2 の早期 return は
   `target is null` = 強調すべきセルが存在しない場合なので対処不要だが、**Task 3 では同じ
   return が「値・形が不一致(= セルは存在する)」の受け皿になる**。晴眼・弱視ユーザーは
   CLAUDE.md §2 で第一級なので、強調を失ったまま放置しない手当てを Task 3 で検討すること。
3. **T1 の「第 2 の役割」がコメントから落ちている。** 計画の fixture コメントにあった
   「正規化を省いた同一性検証を殺せる fixture」という役割を、Task 2 のコメントには書いていない
   (Task 2 時点では同一性検証が存在しないので**先食いしない**判断)。ただし Task 3 Step 4 は
   「T1 が緑のままであること」を正規化の網として当てにしているので、**Task 3 でこの役割を
   コメントへ書き戻すこと**。書き戻さないと将来 `MixedEolCsv` が編集されたときに網が黙って消える。
4. **`ParseError` の暫定利用は Task 3 の完了が前提。** `target is null` の到達条件のうち
   「行 / 列が減って `GetField` が `null`」のケースでは「CSVとして解析できません」は**文言が偽**であり、
   かつどちらの条件でもユーザーの入力が黙って捨てられることを発声が伝えない。
   **Task 2 単独ではマージできない。**

### 8.18 Task 3 — 同一性の検証と専用発声(Step 2 の赤)

開始時の `Value`(EOL 正規化済)・`Rows.Count`・`Rows[row].Count` をスカラーで捕捉し、確定時の
パース結果と突き合わせて、崩れていれば本文へ 1 文字も触れずに `CommitTargetChanged` を発声する
形にした。§8.17-4 の暫定(`ParseError` で受ける)は解消。

まずビルドが `error CS0117: 'CsvAnnounceFormatter' に 'CommitTargetChanged' の定義がありません` を
**6 箇所**(新規テスト 6 本すべて)で出して失敗。定数だけ足して再実行した赤(逐語):

| テスト | Actual |
|---|---|
| `Commit_WhenCellAtRowColBecameAnotherCell` | `Expected: "a1,a2,a3\nb1,ZZ,b3\nc1,c2,c3"` / `Actual: "a1,a2,a3\nb1,NEW,b3\nc1,c2,c3"` |
| `Commit_WhenCellAtRowColDisappeared` | `Expected: "本文が変わったため確定できません"` / `Actual: "CSVとして解析できません"` |
| `Commit_WhenRowCountChanged_AndValueCoincides` | `Expected: "X,Y\nX,Y"` / `Actual: "X,Y\nNEW,Y"` |
| `Commit_WhenColumnCountChanged_AndValueCoincides` | `Expected: "p,q\nX,X,X"` / `Actual: "p,q\nX,NEW,X"` |
| `Commit_WhenBodyBecameUnparsable` | `Expected: "本文が変わったため確定できません"` / `Actual: "CSVとして解析できません"` |
| `Commit_WhenRejected_RestoresCellHighlight...` | `Expected: "a1,a2\r\nZZ,b2\r\nc1,c2"` / `Actual: "a1,a2\r\nNEW,b2\r\nc1,c2"` |

**計画の想定と全件一致。** 前提固定の `Assert.Equal(..., afterMutation)` は 6 本とも先に通ったので、
計画のオフセット(12/2・17/9・0/4・4/0)は数え直した結果も正しく、期待値の修正は要らなかった。

### 8.19 Task 3 — §8.17-1 の論証が実測へ昇格した

`CsvParserTests` に `Unterminated_trailing_record_sets_not_ok_but_leaves_preceding_rows_intact` を
足し、`"a1,a2\nb1,b2\n\"x"` を実測(初回から PASS):

- `Ok` = **false** / `Rows.Count` = **2** / `Rows[0].Count` = **2** / `GetField(0,0)` = **`(Start=0, Length=2, Value="a1")`**

**§8.17-1 が「コード読解による論証であって実測していない」と留保した帰結は真だった。** 値も形も
開始時と全一致するので「値 + 形」の guard は全部素通りし、`csvNow.Ok` だけが書込を止める。
L3 側でも `csvNow.Ok` 判定を落とす変異を殺すのが `Commit_WhenBodyBecameUnparsable` 1 本だけである
ことを実測した(§8.24 の m1 列)。**§8.17-1 は回収済み。**

### 8.20 Task 3 — M-1 の網は「2 手」でしか作れない(§8.17-2 の回収)

拒否枝でセル強調を復元する手当て(§8.17-2)を入れ、網も張った。fixture の設計で分かったこと:

- `ConvertEols` は `_cellHighlight = null` を実行する(実測: `Assert.Null(CellHighlight(...))` が通る)。
- **しかし `ConvertEols` 単独では拒否枝に入れない。** 行列構造も正規化後 `Value` も変えないので
  同一性検証が必ず通り、受理枝へ行く(T1 / T2 が緑であることがその実測)。
- したがって「強調が消えている」と「拒否される」を**同時に作るには 2 手要る**:
  1 手目 `ConvertEols(Crlf)` で強調を落とし、2 手目 `MutateBodyWhileEditing` で編集セルの値だけを
  差し替える。fixture は混在 EOL の `"a1,a2\nb1,b2\r\nc1,c2"`。
- 観測は `EditorControlConvertEolsTests` の `CellHighlight` と同じ流儀のリフレクション。
  `System.Windows.Forms.SelectionRange` と名前が衝突したので別名 using で入れた(計画に無い実物都合)。

### 8.21 Task 3 — 計画の T4 kill 主張が偽だった / T4 の網に穴があった

**(a) 計画の kill 主張が偽。** 計画は `Commit_WhenCellAtRowColDisappeared`(T4)の kill 対象を
「`GetField` の null 判定削除(`NullReferenceException`)」としていたが、**実測では T4 はこの変異を
殺さない**。行が消えると `csvNow.Rows.Count != startRowCount` が先に true になり `target.Value` へ
到達しないためである。この変異を殺すのは `Commit_WhenBodyBecameUnparsable` **だけ**だった。
「結論(null 判定は要る)は正しいが理由節(どのテストが守るか)が偽」の型。

**(b) T4 の網に穴があった。** 当初 T4 は `Said[^1]` だけを見ていたため、強調復元の前置ガード
`if (target is not null)` だけを削る変異が**全緑で生存**した(セル消失時に `ApplyCell` が
「移動できません」を先に喋る = 余計な発声が 1 本増えるが、末尾だけ見る assert は素通しする)。
T4 を `Assert.Equal(new[]{ CommitTargetChanged }, Said)` の全件固定へ強化して塞いだ。

### 8.22 Task 3 レビュー I-1 — 「結論は正しいが理由節が偽」の 5 回目

拒否枝に書いた M-1 のコメント

> 到達経路である `ConvertEols` は本文差し替えの直後に `_cellHighlight` を捨てるため、
> ここで戻さないと晴眼・弱視ユーザーが現在セルを見失ったまま残る

は 2 点とも偽だった:

1. **`ConvertEols` はこの拒否枝の到達経路ではない**(§8.20 のとおり必ず受理枝へ行く。T1 / T2 が
   緑であることが実測)。
2. **受理枝は末尾の `ApplyCell(ed, csv2, row, col, announce: true)` が強調を張り直す**
   (実読で確認。`ApplyCell` は `HighlightCharRange` を呼ぶ)。したがって Ctrl+S 経路では
   戻さなくても見失わない。**強調が失われたまま残るのは到達不能な拒否枝だけ。**

決定的なのは、**同じ commit のテスト側コメントが正反対を明記していた**こと ——
「`ConvertEols` だけでは行列構造も値も変わらず拒否枝に入らないので、…2 手で同時に作る」。
§8.7 が記録した「同じ commit の中で記述が食い違っていた」型の**再発(5 回目)**である。
さらにこの書き方は「拒否枝が実運用で到達する」という**逆向きの誤読**を生み、テスト側の
「実運用から到達できない」宣言と衝突していた。

**結論(拒否枝でも強調を戻す)は正しいので、理由節だけを書き直した**(fixup)。
**なお元 commit のメッセージ本文にも同じ誤りが残っている**(`fix(app): 確定先が別セルに…` の
「到達経路の ConvertEols が `_cellHighlight` を捨てるため、戻さないと…見失う」)。
commit は書き換えない規範なので、**訂正の場所はここ**である。

### 8.23 Task 3 レビュー I-2 / I-3 — 「網を塞いだつもりが兄弟変異に開いていた」

§8.21(b) で m11 を塞いだ直後に、**同じ型の変異が 2 つ残っていた**。どちらも実測で確認した。

**I-2: 拒否枝の `ApplyCell` を `announce: true` にする変異が 730 本全緑で生存。** 全件固定してある
唯一のテスト(T4)は `target is null` で `ApplyCell` を**呼ばない**枝なのでこの変異に対して恒等、
残り 5 本は `Said[^1]` しか見ないため先頭に 1 本増えても素通しする。生存する変異体は無害ではなく、
**拒否したのにセルの新しい値を読み上げてから「確定できません」と言う**(傘設計書 B5)。
`Commit_WhenCellAtRowColBecameAnotherCell` を全件固定へ強化して塞いだ。観測した赤:

```
Assert.Equal() Failure: Collections differ
Expected: string[]     ["本文が変わったため確定できません。入力は破棄しました"]
Actual:   List<string> ["ZZ 2行2列", "本文が変わったため確定できません。入力は破棄しました"]
```

**教訓**: 「余計な発声が先頭に 1 本増える」型の変異は、`Said[^1]` を見る assert では**原理的に**
捕まらない。1 本を全件固定にしても、その 1 本が通らない枝の兄弟変異は残る。

**I-3: `startColCount` を `csv.Rows[0].Count`(見出し行の幅)から取る変異も 730 本全緑で生存。**
理由は **fixture が全部長方形**だったから(`Grid3x3` 3/3/3・`"p,q\nX,Y\nX,Y"` 2/2/2・
`"p,q\nX,X"` 2/2・`"a1,a2\nb1,b2"` 2/2・`MixedEolGrid` 2/2/2)。CLAUDE.md §4-B
「fixture は区別できる形にする」(partial-selection と同型)の未適用箇所。

**ここでレビューの推奨(既存 fixture を非長方形へ差し替える)はそのまま採らなかった。** 実測:

| 変異 | 長方形 `"p,q\nX,X"` | 非長方形 `"p,q,r\nX,X"` |
|------|---------------------|-------------------------|
| 捕捉側 `csv.Rows[0].Count` | – (恒等) | ✗ |
| 検査側 `csvNow.Rows[0].Count` | ✗ | – (拒否側へ倒れる) |

**1 つの fixture では両方殺せない。** 捕捉側を殺すには開始時に `Rows[0].Count != Rows[row].Count` が
要り、検査側を殺すには変異後に `Rows[0].Count == startColCount` が要るが、両立しない。
差し替えていたら検査側の網が黙って消えていた。**既存の長方形テストを残し、非長方形の 1 本
(`Commit_WhenColumnCountChangedInJaggedRow_...`)を追加**した。

### 8.24 Task 3 — 変異 14 種 × テスト 7 本(fixup 後の最終実測)

1 変異ずつ注入 → App 全件実行 → revert(毎回 `git diff -- src/` が意図した差分だけに戻ることを確認)。
ビルド判定は `grep -E " error [A-Z]+[0-9]+"`(Sonar の `error S###` を見落とさないため)。

| テスト \ 変異 | m1 | m2 | m3 | m4 | m6 | m7 | m8 | m9 | m10 | m11 | m12 | m13 | m14 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `...BecameAnotherCell` | – | – | – | ✗ | – | ✗ | – | ✗ | – | – | **✗** | – | – |
| `...Disappeared` | – | – | – | – | – | – | – | ✗ | ✗ | **✗** | – | – | – |
| `...RowCountChanged` | – | **✗** | – | – | – | ✗ | – | ✗ | – | – | – | – | – |
| `...ColumnCountChanged` | – | – | ✗ | – | – | ✗ | – | ✗ | – | – | – | – | **✗** |
| `...ColumnCountChangedInJaggedRow` | – | – | ✗ | – | – | ✗ | – | ✗ | – | – | – | **✗** | – |
| `...BodyBecameUnparsable` | **✗** | – | – | – | **✗** | – | – | ✗ | ✗ | – | – | – | – |
| `...RestoresCellHighlight...` | – | – | – | ✗ | – | ✗ | **✗** | ✗ | – | – | – | – | – |
| **App 失敗数(731 本中)** | 1 | 1 | 2 | 2 | 1 | 5 | 1 | 7 | 2 | 1 | 1 | 1 | 1 |

変異の内訳: m1 `csvNow.Ok` 判定削除 / m2 行数比較削除 / m3 列数比較削除 / m4 値比較削除 /
m6 `target is null` を条件から削除 / m7 同一性検証を丸ごと削除(= Task 2 の状態) /
m8 拒否枝の強調復元削除 / m9 発声を `ParseError` へ戻す / m10 `target is null` だけ `ParseError` /
m11 強調復元の前置ガードだけ削除 / m12 `announce: true` 化 / m13 捕捉側 `Rows[0]` / m14 検査側 `Rows[0]`。

**別枠 m5(同一性検証から EOL 正規化を外す)**: 落ちるのは Task 2 の T1
`Commit_AfterEolConversion_WritesEditedCell_NotStaleOffsets` **1 本だけ**(App 731 中 1 失敗)。観測した赤:

```
Assert.Equal() Failure: Strings differ
Expected: "a1,a2\r\nNEW,b2\r\nc1,c2"
Actual:   "a1,a2\r\n"x\r\ny",b2\r\nc1,c2"
```

= 確定が拒否されてセルが編集前のまま残る。**§8.17-3 の申し送り(T1 の第 2 の役割)は、この実測を
確認してから `MixedEolCsv` のコメントへ書き戻した。回収済み。**

**注入できなかった変異**(アナライザが `error` 相当で先に殺す): 比較 1 本だけを消して
`startXxx` を残す → `S1481` / `target is null` を `false` や `csvNow.Rows.Count < 0` へ置換 →
`S1125`・`CS8602`・`RCS1215`・`S3981`。

### 8.25 Task 3 レビュー Minor 群と申し送り

対応した Minor(すべて fixup):

| # | 指摘 | 対応 |
|---|------|------|
| M-1 | 「短絡順序を入れ替えるな」が src に無い | src へ追記。**ただし計画の書き方は不正確だった** —— 行数比較を先頭へ動かす並べ替えは**安全**(`row < startRowCount == Rows.Count` が成り立つ)。危険なのは `Rows[row].Count` を行数比較より前へ出すことだけ。正確に書いた |
| M-2 | 文言が「入力が捨てられた」を伝えない | `"本文が変わったため確定できません。入力は破棄しました"` へ伸ばした。既存 14 定数との部分文字列関係を全ペア機械検査し、**新たな包含は 0**(既存の `ParseError` ⊂ `OpenParseFailed` 1 組のみで、これは本件以前からある) |
| M-3 | 到達不能の根拠が断定形 | 「T1 / T2 の 2 fixture 分だけが実測。一般には §4.4 の論証(§8.9 の留保つき)」を明記 |
| M-4 | 到達不能宣言から最後のテストまで 180 行 | `...RestoresCellHighlight...` に「1 手目は実運用で到達するが、拒否枝そのものは到達不能」の射程を追記 |
| M-5 | `...BecameAnotherCell` が単独 kill を持たない | I-2 の対応(m12)で解消 |
| M-6 | 「上の 4 本」が曖昧 | 「guard の `Ok` 以外の 3 節(行数・列数・値)」へ具体化 |

**申し送り(PR description へ)**: 拒否枝の `ApplyCell` は `doc.State.CsvRow` / `CsvCol` を
**書き戻す**(`ApplyCell` の実装)。現行配線では F2 編集中に State が動かない(§4.1: ナビは
`TryContext` 冒頭の `_editor.IsEditing` で撥ねられる)ので開始時 `(row, col)` の書き戻しは冪等だが、
**将来 F2 編集中に State が動く配線が増えると、拒否のたびに論理カーソルを開始位置へ巻き戻す
潜在結合**になる。強調の復元だけが要るなら `HighlightCharRange` + `EnsureVisibleCharRange` に
限定する形へ切り出す余地がある(本ブランチでは `ApplyCell` の既存経路に乗せる判断)。

### 8.26 Task 4 — §6 の暫定 3 項目から確定 7 項目へ

`docs/plans/2026-09-01-csv-f2-commit-staleness-l5-checklist.md` を起こした。§6 は策定時の
暫定案(3 項目)で、実装で状況が変わったため次のように確定した。**§6 の本文は策定時
スナップショットとして書き換えない**(CLAUDE.md §8)。

| §6 の暫定 | 確定後 |
|---|---|
| 1. 混在 EOL で F2 → Ctrl+S → Enter | **項目 1**。実装で T1 / T2 を分けた理由(編集セル自身が改行を持つ / 後ろの行のセル。§8.13)に合わせ **1-A / 1-B の 2 ケース**へ割った |
| 2. Ctrl+S → 開き直して一致 | **項目 2**。ディスクのバイト検算を足した(L3 は `SnapshotText` しか見ない) |
| 3. `CommitTargetChanged` の発声(「実機では確認できない見込み。判断は実装時に確定する」) | **項目 6 = 未確認・到達経路なし**。§8.27 に判断の根拠 |
| (無し) | **項目 3** 取消側(F2 → Ctrl+S → Esc)。実装計画 Task 4 Step 1-3 |
| (無し) | **項目 4** Alt+Enter のセル内改行(§8.6 が「ユーザーの空行が黙って消える」と判定した箇所の実機面) |
| (無し) | **項目 5** セル強調の目視(§8.20 / §8.22 の M-1 が扱う観測面。CLAUDE.md §2) |
| (無し) | **項目 7** 退行 = 通常の F2 → Enter が**誤って拒否されない**こと |

**項目 7 を足したのが Task 4 の実質的な収穫である。** §8.24 の変異表は「拒否すべきときに拒否するか」
を 13 変異で覆っているが、**「拒否してはいけないときに拒否しないか」の実機面はどこにも無かった**。
guard が誤っていれば通常編集がそのまま失敗する = 実運用で最も痛い失敗様式なので、L5 の項目にした。

### 8.27 Task 4 — §6 項目 3 の判断: `CommitTargetChanged` は実運用から到達できない

**判断: 到達手段は見つからない。チェックリストには「未確認(到達経路なし)」と書き、判定欄を置かない。**

§3 の表を実コードで当て直して確かめた(F2 編集中 = オーバーレイ TextBox がフォーカスを持つ状態)。

- `Undo` / `Redo` / `Cut` / `Paste` は冒頭が `if (_buffer is null || ReadOnly) return;` で、
  CSV モードは `ReadOnly=true`(`CsvController.TryEnterMode`)なので **no-op**。
- `ReplaceCharRange` / `ReplaceCharRangeExact` にも同じ `ReadOnly` ガードがあり、
  置換・禁則整形は CSV モードで前段からも拒否される。
- 本文を変えうる残りのメニューショートカット(Ctrl+O / Ctrl+Shift+S / Ctrl+F / Ctrl+H /
  Ctrl+Shift+F / Ctrl+G / 設定)は**すべてダイアログでフォーカスを奪う** → `TextBox.LostFocus`
  → `CancelEdit` = 確定そのものが起きない。
- Ctrl+N / Ctrl+Tab / Ctrl+1..9 / Ctrl+W は `BeforeActiveChange` / `CloseActiveTab` の
  `AbortEdit` でコールバックを呼ばずに破棄される。
- `ProcessCmdKey` が `IsEditing` を見ずに横取りする F3 / Shift+F3 / Insert / Ctrl+Alt+P は
  **本文に触らない**。
- バックアップタイマー(`BackupCoordinator`)はディスクへ書くだけでバッファに触らない。

**残るのは Ctrl+S → `ConvertEols` の 1 本だけで、それは §4.4 の理由で必ず受理枝へ行く。**

**ただし「到達不能」は本ブランチの実測範囲を超える主張である。** `ConvertEols` が行列構造を
変えないことは T1 / T2 の 2 fixture 分だけが実測で、一般には §4.4 の論証(§8.9 の留保つき)。
上の列挙も前置ガードの列挙であって不変条件ではない(監査 §9 V-7)。
**チェックリストにこの射程をそのまま書いた。**

#### 8.27.1 §3 の表に無かった 2 本目の本文変更経路(結論は変わらない)

Task 4 の追跡で、**F2 編集中に本文を変える経路が Ctrl+S 系にもう 1 本あった**ことが分かった。
`FileController.WriteToPath` の catch 節が呼ぶ `UndoEolConversion`(保存が失敗したとき)である。
これも `ReadOnly` ガードを持たない(`EditorControl.UndoEolConversion` の冒頭は
`if (!conversionRecorded || _buffer is null) return false;` だけ)ので、
**CSV モードのまま本文を変換前へ戻す**。

差引きの本文は F2 開始時と同一になるので同一性検証は通り、**受理枝へ行く = 結論は変わらない**。
§3 の表(策定時スナップショット)は書き換えないが、**「Ctrl+S 1 本」は正確には
「Ctrl+S の成功経路と失敗経路の 2 本」である**ことをここに記録する。
なお `2026-08-28-eol-detection-and-undo-l5-checklist.md` の項目 ⑦ が
まさにこの失敗経路を CSV モードで踏む項目なので、L5 の束ね直しで兼ねられる
(チェックリスト §5 に記載)。

### 8.28 Task 4 — 「空行が消えたか」は**発声からは判らない**(§8.6 の観測面の穴)

§8.6 は「Alt+Enter を 2 回押して作った確定値の空行が畳み込み変異で黙って消える」を L1 / L3 で
塞いだ。Task 4 で L5 の期待発声を `CsvAnnounceFormatter` の実物から引いたところ、
**その差は発声には現れない**ことが分かった。

- `CsvAnnounceFormatter.Cell` は `SanitizeForDisplay.ContainsSanitizableControlChar` で分岐し、
  **CR / LF は `UnicodeCategory.Control` なのでこの分岐に入る**。値は
  `SanitizeForDisplay.OneLine(value, 60)` で 1 行化される。
- `OneLine` は制御文字を空白 1 個へ置換し、**連続空白を 1 個へ畳む**。
  したがって `x\ny` も `x\n\ny` も **どちらも `x y`** になる。
- つまり確定後の発声は両方とも `制御文字を含みます: x y 2行2列` で**完全に同一**である。

**帰結 2 つ**:

1. L5 のチェックリストで項目 4 の合否は**目視とバイト**で決めることにした
   (発声を合否条件にすると、空行が消えていても PASS になる)。
2. [[net-absence-claims-are-also-verifiable]] が定める観測面の順序
   「本文 → 選択 → 発声文言 → キャレット」のうち、**このケースでは第 2 の観測面(発声文言)が
   原理的に盲である**。SR ユーザーはセル内の空行の有無を耳で確かめる手段を持たない
   —— 本ブランチ以前からの挙動だが、**申し送り候補**としてチェックリストに記録した。

### 8.29 Task 4 — 自動テストが 1 行も触れていない実機経路 3 本

チェックリストの §0.2 に書いた根拠をここにも残す(「網がある」と読み違えないため)。

| 未検証の経路 | 実物 | なぜ L3 で届かないか |
|---|---|---|
| **Ctrl+S が F2 オーバーレイを閉じずに `ConvertEols` へ届くこと** | `MainForm.ProcessCmdKey` の `!_csv.IsEditing` → `base.ProcessCmdKey` → メニューショートカット | L3 は `doc.Editor.ConvertEols(...)` を**直接**呼ぶ(テスト本体のコメントに明記)。`MainForm` も `FileController` も通らない。**このブランチの前提そのものが未実測** |
| Alt+Enter のセル内改行挿入 | `CsvCellEditor.OnKeyDown` の `Keys.Return && e.Alt` 分岐(`"\r\n"` 挿入 + キャレット +2) | L3 は `TextBox.Text` を直接代入するので `OnKeyDown` を一度も通らない |
| `Text = field.Value`(LF 単独)→ `Commit` での `Text` 読み戻し | `CsvCellEditor.Begin` / `Commit` | L3 は `Begin` が入れた初期値を**必ず上書きしてから** `Commit` する。ネイティブ EDIT コントロールが LF 単独をどう保持するかは実機でしか判らない |

いずれもチェックリストの項目 1 / 4 / 7 で観測する。

### 8.30 Task 4 — §8 通読での点検(訂正記述の追加。既存節は書き換えない)

§8.1〜§8.25 を通して読み、次の 2 点を記録する。**既存節の本文は書き換えていない**
(§8 は実施記録であり、書き換えは履歴の改竄になる)。

1. **§8.7 の本文と同節の表が食い違って読める。** 本文は「no-op は **3 本**落ちる」と書き、
   同じ節の表は「(e) no-op = **6**」としている。これは矛盾ではなく**時点差**である ——
   本文の「3 本」は §8.3 の測定(`Adjacent_` 3 ケースを足す前)を引いた数、
   表の「6」は §8.6 で `Adjacent_` を足した後の再測定値。**§8.7 自身が
   「網を足したら、既存の kill 主張を再測定すること」と書いている、その再測定の結果が表**である。
   §8.3 の変異表も同じ理由で `Adjacent_` 追加前の値であり、**現在の網に対する正しい数は
   §8.7 の表のほう**である。
2. **§8.16 は Task 2 時点の記録であり、Task 3 で上書きされている。** §8.16 は
   `csvNow.Ok == false` のとき「`ParseError` を発声する」と書くが、これは §8.17-4 が
   「暫定・Task 2 単独ではマージできない」と留保した状態で、§8.18 の Task 3 で
   `CommitTargetChanged` へ差し替わっている。§8.10 が確認したとおり
   **§8 の各節は時点依存の記述として正しい**が、通読する人が §8.16 だけを引くと現行実装を
   誤って説明することになるので、ここで指しておく。

そのほか、§8.22 が訂正した元 commit のメッセージ本文の誤り(「到達経路である `ConvertEols` が
`_cellHighlight` を捨てるため、戻さないと見失う」)については、**訂正が §8.22 に書かれていることを
確認した**。対象 commit を特定できるよう hash を補っておく: **`8aea45d`**
(`fix(app): 確定先が別セルになっていたら書かずに知らせる(M-25)`)。
同 commit のメッセージ本文には「変異 10 種 × テスト 6 本」ともあるが、これも fixup `e67976c` 後の
最終形(**変異 14 種 × テスト 7 本** = §8.24)より古い。**commit は書き換えない規範なので、
両方とも訂正の場所は §8 である。**

### 8.31 Task 4 レビュー — チェックリストが「修正前でも全行 PASS する」形だった(Critical-1)

`20aea6b` の L5 チェックリストは別エージェントレビューで **❌「このままでは実機検証に使えない」**
と判定された。裏取りされた部分(fixture 作成コマンド・期待発声の逐語・§8.27 の到達不能判断・
§8.28 / §8.27.1 の新事実)は独立に再現されて真だったが、**ゲート仕様として決定的な欠陥があった**。

**欠陥**: 項目 1 に「**Ctrl+S が `ConvertEols` まで届いた**」を測る手段が無かった。
Ctrl+S が握り潰されればバッファは混在 EOL のままで、**開始時の座標と確定時に解決し直した座標が
一致する** —— つまり本文も発声も期待どおりになり、**修正前の実装でも全行 PASS する**。
§0.2 が「Ctrl+S が届くことは一度も実測されていない」を項目の存在理由として宣言しておきながら、
その 1 ビットを測る手順が無かった。**「網が無い」の親戚 = 「区別できない網がある」** である。

**対応(fixup)**: 前提 A〜D の 4 行を項目 1 へ追加した。

| 前提 | 何を見るか | 導出 |
|---|---|---|
| A | ダイアログが出ていない | 出たら `CancelEdit` 済み(§1.0) |
| B | **セル強調が消えている** | `_cellHighlight = null` は `ConvertEols` の fast-path(`EditorControl.cs:501-505` の `return false`)**より後**(`:592`)にしかない。**強調の消失 ⇔ 非 fast-path で本文が差し替わった**。ウィンドウを切り替えずに済む**唯一のアプリ内観測面** |
| C | `eol-watch.log` に `len=43 CRLF=4 LoneLF=0` が増える | 下記の自力計算 |
| D | ディスクの 2 行目が**編集前**のまま | `WriteToPath` は `ConvertEols` 済みバッファを書くが、F2 の確定はまだ起きていない |

**C の期待値は自分で導き直した**(レビュアーの数値を鵜呑みにしない)。fixture は
`id,memo,tail<CRLF>` `1,"x<LF>y",t1<CRLF>` `2,m2,t2<CRLF>` `3,m3,t3` = **42 バイト
(CRLF 3 / lone LF 1)**。`LineEndingDetector.Detect` は多数決なので `State.LineEnding` は必ず
CRLF(`FileController.cs:359` が検出結果を入れる)。編集中 Ctrl+S で `ConvertEols(Crlf)` が
lone LF を CRLF へ広げ、`TextFileService.Save` がそのバッファを書くので
**43 バイト / CRLF 4 / lone LF 0**、かつ memo セルは編集前の `x`⏎`y`。
**レビュアーの CRLF=4 / LoneLF=0 は真**だった。

**B の観測が要る理由**は Task 4 の追跡で判った制約である: **F2 編集中にターミナルへ切り替えると
その瞬間に `CancelEdit` が走ってオーバーレイが消える**。したがって「編集中のディスク状態」は
**先に仕掛けたポーリングループでしか観測できない**(チェックリスト §2-5 にスクリプトを置いた)。

### 8.32 Task 4 レビュー Important-1 — §8.27.1 の理由節が偽だった(6 回目)

§8.27.1 は保存失敗経路(`UndoEolConversion`)を新事実として記録したが、その最後の 1 行
「`2026-08-28-...-l5-checklist.md` の項目 ⑦ と**兼ねられる**」は**偽**である。実読で確かめた:

1. `FileController.cs:912` の `UndoEolConversion` が本文を巻き戻す。
2. **その直後 `:928` の `_prompt.Error` がモーダル `MessageBox.Show` を出す**
   (`MessageBoxUserPrompt.Error`)。
3. モーダルはフォーカスを奪う → `CsvCellEditor.OnLostFocus`(`:99`)→ **`CancelEdit` が走り
   オーバーレイが消える**。

したがって項目 3 の手順「Ctrl+S → `ZZ` と打つ → Esc」は**打てない**うえ、本文も巻き戻るので
「本文が差し替わった状態で Esc する」という項目 3 の前提そのものが消える。**兼ねると vacuous。**

**さらに §8.27.1 の理由節そのものが不正確だった。** 「差引きの本文は開始時と同一になるので
同一性検証は通り、受理枝へ行く」と書いたが、この経路では**確定コールバックが一度も呼ばれない**
(先に `CancelEdit` が走る)ので、同一性検証は**走りもしない**。
**結論(`CommitTargetChanged` に到達しない)は正しく、理由節が偽** ——
§8.3 / §8.7 / §8.15 / §8.21(a) / §8.22 と同じ型の **6 回目**である。

決定的なのは、**その反証材料が §8.27.1 自身の中にあった**こと。同じ節が
「開く / 名前を付けて保存 / … はダイアログがフォーカスを奪う → `CancelEdit`」と書いておきながら、
**保存失敗のダイアログにだけその規則を当てなかった。** 「同じ commit の中で記述が食い違う」
(§8.7 / §8.22 が記録した型)の再発。

**手順として足す: 新しい経路を『到達不能』側へ分類したら、その経路が出すダイアログにも
`CancelEdit` の規則を当てること。** 経路の列挙だけでなく、**各経路の副作用を最後まで追う**。

**訂正**: 保存失敗経路の正しい説明は「本文は巻き戻り、かつ `_prompt.Error` の
`CancelEdit` によって**確定自体が起きない**」である。`ConvertEols` に到達する**前**に短絡する枝
(重複タブ `:483` / 符号化劣化の事前確認 / リモート到達性 `TryInspectSaveTarget`)も同様で、
**Ctrl+S の枝は「成功 1 本」ではなく「成功 1 本 + 短絡 3 種 + 書込失敗 1 本」**である。
チェックリスト §1.0 と項目 6 の表をこの形に直した。

### 8.33 Task 4 レビュー Important-2 / Important-3 — 手順が主張を踏まない(7 回目)

**Important-2(7 回目)**: 項目 7 の手順 3(本文を変えずに no-op 確定)に
「**m5(同一性検証から EOL 正規化を外す変異)を実機で踏む形**」と書いたが**偽**。
本文を変えないので `startValue` と `target.Value` は**同じ生文字列**になり
(`doc.ParseCsv()` はスナップショット参照が同一なら同一インスタンスを返す)、
**正規化を外しても一致して受理される**。§8.24 の別枠が「m5 を殺すのは T1 の 1 本だけ」と
実測しており、T1 に対応する実機シナリオは**項目 1-A** である。
**結論(正規化は要る)は正しく、どの手順が守るかが偽** = §8.21(a) と同型。
m5 の記述を項目 1-A の「もう 1 つの FAIL 様式」へ移し、項目 7 には
**「ここは m5 を踏まない」と明示**した(偽の安全宣言にしないため)。

**Important-3**: §8.29 が「自動テストが 1 行も触れていない実機経路」の 3 本目に挙げた
`Text = field.Value`(**LF 単独**)→ `Commit` での読み戻しが、**チェックリストの手順どおりに
操作すると踏めなかった**。項目 7 の手順 1 が「`mix1b.csv` の続きでよい」としていたが、
項目 1-B で編集中 Ctrl+S を 1 回押しているので**セル内改行は既に CRLF に統一済み**
(§1.2 が自分で書いている制約に、自分の手順が違反していた)。
**一度も Ctrl+S していない `mix7.csv` を追加**して手順 1 を差し替えた。

### 8.34 Task 4 レビュー Minor — 修正前の挙動は fixture ごとに計算し直す必要があった

| # | 指摘 | 対応 |
|---|---|---|
| M-1 | 「拒否枝の文言は L3 **6 本**」が古い(現在 **7 本**) | 7 本へ。**§8.7 の教訓「網を足したら既存の kill 主張を再測定する」の再発**。同じ文書の §8.24 が「テスト 7 本」と書いているのに、要約側が古い数を持っていた |
| M-2 | 項目 5 の「**3 表示行**のセル」が誤り | `"x`⏎`y"` は **2 表示行**(3 行になるのは項目 4 の `"x`⏎⏎`y"`)。項目 7 では正しく「2 表示行」と書いており**同じ文書の中で食い違っていた** |
| M-3 | 「LF と表示された場合も検証は成立する」が偽 | 多数決(CRLF 3 : LF 1)なので**必ず CRLF**。LF と出るのは fixture が壊れている場合だけで、そのとき 1-A は `length` の陳腐化を殺せない。「**壊れているので作り直す**」へ |
| M-4 | 1-B の「修正前の挙動」が L3 fixture の症状だった | **`mix1b.csv` で計算し直した**(下記) |
| M-5 | 「PR #54 と fixture を共有できる」が不成立 | #54 の ①②⑤⑥ は fixture が全部 `.txt` で CSV モードに入れない。「操作の**種類**が同じなので段取りだけまとめる」へ |
| M-6 | 「F3 / Insert 等は**素通りする**」が語として逆 | `MainForm.cs:732-753` の `switch` は `IsEditing` を見ずに**横取りして `true` を返す**(TextBox に渡らない)。結論(本文に触らない)は同じ |
| M-7 | Ctrl+S の枝が 2 本ではなく実質 5 通り | §8.32 の訂正に含めた |
| M-8 | 「解析に成功してモードへ入る」が自動進入前提 | `AppSettings.CsvAutoModeOnOpen` は初期化子を持たない = **既定 false**。既定設定では手動でモードへ入る |
| M-9 | 項目 7 の「テンポ」行が B5 の M-8 と重複 | **② 受容**。B5 は別ブランチで、§5 が既に「順序を分ける」と書いている |

**M-4 の再計算(自分でバイト位置を数え直した)**: `mix1b.csv` の編集対象 3 行 1 列 は
変換前 `Start=26 / Length=1`、変換後 `Start=27 / Length=1`。修正前は 26 を持ち越すが、
変換後の 26 は **2 行目を終える CRLF の LF 側**である。`ReplaceCharRange` の `SnapAndClamp` は
**mid-CRLF を前方(CR 側)へスナップする**(`TextBoundary.SnapToLogicalCharStart` の
`IsCrlfEndingAt` 分岐 → `pos - 1`)ので、実際の書込範囲は `[25, 27)` = **行区切りの CRLF 2 文字
まるごと**になり、**2 行目と 3 行目が連結して列数が 3 → 5 になる**。

L3 の T2 が記録した症状(`b1NEW2` = 区切りカンマを食う)とは**別**である。
**「修正前の挙動(比較用)」は fixture ごとに計算し直さなければならない** ——
L3 の赤をそのまま転記すると、実施者は出ないはずの症状を探すことになる。

**Task 4 の総括**: docs だけの変更でも「結論は正しいが理由節/射程が偽」が **2 件**(§8.32 / §8.33)
出た。本ブランチ通算 **7 回**。**列挙・要約・転記の 3 つが、この型の温床である。**

### 8.35 Task 4 — 「Ctrl+S 直後は無音」を観測行として入れた(判断)

レビュアーの提案どおり、項目 1-A へ**合否ではない観測行**として入れた。理由:

- `FileController` に `IAnnouncer` / `_announcer` の参照が **0 件**であることを自分で `grep` して
  確認した(§3.1 の主張を独立に検証)。**F2 編集中の Ctrl+S は成功しても無発声**である。
- §7 の申し送り「F2 編集中の Ctrl+S は**編集前の内容**をディスクへ書く」を v0.2 スコープ外へ
  送った以上、**SR ユーザーには「保存が起きたこと」も「編集前が保存されたこと」も伝わらない**。
  これは実機でしか集まらない材料で、次のブランチで文言を足すかどうかの判断に直接効く。
- 項目 3 の「Esc は無発声」と**同じ扱い**にすることで、チェックリスト内で一貫する。
- **合否にはしない**。本ブランチが変えた挙動ではないため(§3.1 の判断は据え置き)。
