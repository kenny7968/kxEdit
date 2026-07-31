# 文字アクセス seam の集約と高速化(A+B+C)設計書

策定日: 2026-07-31 / 対象ブランチ: `feature/char-access-seam`

## 1. 背景・目的

「エディターコントロールの文字コードの扱いにリファクタが必要な箇所があるか」の調査
(2026-07-31)で見つかった 3 件を回収する。

調査ではまず「文字コード」を 2 通りに読み分けた。

- **エンコーディング(UTF-8 / Shift_JIS / EUC-JP)** — `EditorControl` は `Encoding` 型を
  一切持たない。判定・復号・保存は `Core/Text/{EncodingDetector,EncodingCatalog,TextFileService}`、
  状態保持は `App/DocumentState`、UI は `EncodingPickDialog` / `SaveAsDialog` と層が割れている。
  **本設計はここに一切触れない。**
- **文字コード単位(UTF-16 code unit / コードポイント / CRLF)** — ここに問題が集中していた。

回収対象:

- **A**: `UiaTextHostAdapter.NextChar/PrevChar` が `NavigationCommands.MoveRightChar/MoveLeftChar`
  の論理的な完全重複になっている。
- **B**: サロゲートペア判定の自前実装が src 配下 14 ファイル・21 箇所に散っている。
- **C**: その土台である `TextSnapshot.GetChar` が 1 文字あたり 128 B のアロケーションと
  最大 64 KB のバイト走査を伴う。

**本作業は挙動不変のリファクタである。** ただし `UiaTextHostAdapter`(SR 経路)に触れるため
CLAUDE.md §5 の L5 実機 SR 検証は必須とする(「迷ったら必要に倒す」)。

## 2. 現状調査結果(2026-07-31)

### 2.1 A: UIA 側の歩進は Core の論理的な完全重複

| 実装 | 位置 |
|------|------|
| `NavigationCommands.MoveRightChar` | `src/yEdit.Core/Editing/NavigationCommands.cs:39` |
| `UiaTextHostAdapter.NextChar` | `src/yEdit.Editor/UiaTextHostAdapter.cs:335` |
| `NavigationCommands.MoveLeftChar` | `src/yEdit.Core/Editing/NavigationCommands.cs:21` |
| `UiaTextHostAdapter.PrevChar` | `src/yEdit.Editor/UiaTextHostAdapter.cs:356` |

サロゲート判定も CRLF 判定も論理的に等価である。`MoveLeftChar` の `prev > 0` と
`PrevChar` の `o - 2 >= 0` は `prev = o - 1` より同値。差は先頭の clamp / null チェックのみ。

これが美観の問題でない証拠は `docs/plans/2026-07-24-crlf-atomic-caret-design.md:119-120` に残る。
「明示対応が必要」の節に `NavigationCommands` と `UiaTextHostAdapter` が**別項目として並んでおり**、
CRLF atomic 化のとき同じ規則を 2 箇所へ手で入れたことが分かる。次に規則を変えるとき
(同設計書 §10 F-3「サロゲート=1 化」など)も 2 箇所必要で、片方を落とすと
**キーボード操作と SR 読みだけが食い違う**。自動テストで最も気づきにくい壊れ方である。

### 2.2 B: サロゲート判定が 14 ファイル・21 箇所

`char.IsHighSurrogate(c) && i + 1 < len && char.IsLowSurrogate(...)` が各所で手書きされている。

TextSnapshot 上を歩く系(本設計の対象):

`CaretController.cs:92` / `UiaTextHostAdapter.cs:345,365,555,576` /
`WordBoundary.cs:138,151` / `EditorControl.Input.cs:313` /
`NavigationCommands.cs:28,45`

`ReadOnlySpan<char>` 上を歩く系(本設計の対象):

`PixelMapper.cs:24,60` / `LineLayout.cs:50` / `MonoCharMetrics.cs:22` / `FrameBuilder.cs:355`

対象外(流儀が異なる): `KinsokuFormatter.cs:180,218`(`ConvertToUtf32` 併用) /
`SanitizeForDisplay.cs:84`(Rune ベース) / `CharacterCounter.cs:42`(Rune ベース) /
`GrepResultsWindow.cs:74`

### 2.3 C: `GetChar` のコスト(実測)

`src/yEdit.Core/Buffer/TextSnapshot.cs:45` の実装は `GetText(pos, 1)[0]`。1 回で
StringBuilder と string を作り、内部の `TextChunk.CharToByte` が**格子(既定 64 KB・
`TextChunk.cs:24`)からの線形バイト走査を 2 回**行う。コストは「直近格子点からの距離」に比例する。

scratchpad の使い捨てコンソール(Release・`yEdit.Core` を ProjectReference)で実測:

```
GetChar 単発        pos=0 で 438 ns → 3.8M 文字 ASCII の格子セル末尾で 171 µs
                    alloc は位置によらず常に 128 B/call
連続 200 文字を 1 文字ずつ   34.9 ms(garbage 25.6 KB)
同じ 200 文字を GetText 1 回  0.18 ms                     ← 約 196 倍差

実操作 WordBoundary.PrevWordStart(Ctrl+←)1 回
  ASCII    10,000 文字 : 0.337 ms
  ASCII    50,000 文字 : 0.847 ms
  ASCII   200,000 文字 : 0.995 ms
  ASCII 1,000,000 文字 : 1.044 ms
  同等処理を「GetText で前後 200 文字の窓を取ってローカル走査」に置換 : 0.095 ms
```

効いてくる呼び出し元は `CaretController.SnapAndClamp`(全キャレット設定入口)・
`NavigationCommands`・`WordBoundary`・`UiaTextHostAdapter.WordBoundary_WordStart/WordEnd`
(SR の単語単位読み)。← キー 1 回で `GetChar` が 6 回程度走る。

`SnapshotSearcher.IsWordBoundaryMatch`(`:513`)も呼ぶが、こちらは `IsLarge` 閾値により
32M 文字超の窓経路のみ = 通常利用では効かない。

**すでに認識されていた形跡**: 同じ `TextSnapshot` 内の `IsLfAt`(`:109`)が
「バイト1点照会・stringデコードなし」と明記して手書きされている = 1 箇所だけコストを
回避した痕跡。抽象が足りていない。

### 2.4 `AppendBuffer` は格子表が空であることに依存している(C の制約)

`src/yEdit.Core/Buffer/AppendBuffer.cs:20` は 64 KB ブロックを `new TextChunk(_block)` で
包んだ**後も**同じ配列へ書き込み続ける(`Write` の `Array.Copy`)。これが成立しているのは、
格子幅 = ブロック長 = 64 KB のとき `TextChunk` の格子構築ループが 1 度も回らず、格子表が
`[(0,0,0)]` だけになるからである(`AppendBuffer.cs:9` のコメントが明示)。

**格子を細かくすると未書込のゼロ領域で累積 (CharOff, BreaksTo) がキャッシュされ、
後から書いた文字の char↔byte 対応が静かに壊れる。** C-1 の最大の落とし穴。

## 3. スコープ

**対象**: A / B(TextSnapshot 系 10 箇所 + span 系 5 箇所)/ C。

**対象外**:

- `KinsokuFormatter` / `SanitizeForDisplay` / `CharacterCounter` / `GrepResultsWindow`
  (Rune ベース等、流儀が異なる。統一するとかえって各所の意図が読みにくくなる)
- エンコーディング層(§1 のとおり分離済み)
- D: UIA の単語境界が Core `WordBoundary` と別ロジックである件(§8 F-3)
- E: 「文字」の数え方が 3 通り並存している件(§8 F-4)

**不変条件**: 挙動不変。`GetChar` の例外契約(範囲外で `ArgumentOutOfRangeException`)も維持。

## 4. 設計

### 4.1 A+B: `TextBoundary` の新設

新設ファイル: `src/yEdit.Core/Text/TextBoundary.cs`

```csharp
public static class TextBoundary
{
    // コードポイント単位(サロゲートのみ atomic)
    static int CodePointLengthAt(TextSnapshot s, int pos);   // 1 or 2
    static int NextCodePoint(TextSnapshot s, int pos);
    static int PrevCodePoint(TextSnapshot s, int pos);

    // 論理文字単位(サロゲート + CRLF atomic)
    static int NextLogicalChar(TextSnapshot s, int pos);
    static int PrevLogicalChar(TextSnapshot s, int pos);
    static int SnapToLogicalCharStart(TextSnapshot s, int pos);

    // span 版(Layout / 描画。入力に改行を含まない前提のため CRLF 概念なし)
    static int CodePointLengthAt(ReadOnlySpan<char> text, int i);
    static int SnapToCodePointStart(ReadOnlySpan<char> text, int i);
}
```

**置き場を `yEdit.Core.Text` にする理由**: `Core.Editing` と `Core.Layout` の双方から
参照される葉である必要がある。現状 Editing → Layout の依存が既にあり(`NavigationCommands`
が `LineLayout` / `VisualSegments` / `ICharMetrics` を使う)、Layout → Editing を足すと
向きが濁る。`Core.Text` は既に `Core.Buffers` へ依存しており(`TextFileService`)、
文字の意味論を扱うクラス(`EastAsianWidth`)も同居している。

**2 つの規則を意図的に分ける点が設計の要**: キャレット / UIA 系は CRLF atomic、
`WordBoundary` の内部歩進は CRLF 非対応(CR と LF を別々に LineBreak クラスとして
数える前提)。ここを 1 本に統一すると `WordBoundary` の挙動が変わる。
`CodePoint` 系と `LogicalChar` 系を別名の API に分けて名前レベルで取り違えを防ぐ。

呼び出し元の付け替え:

| 呼び出し元 | 変更後 |
|---|---|
| `NavigationCommands.MoveLeft/RightChar` | `Prev/NextLogicalChar` の薄いラッパ |
| `UiaTextHostAdapter.NextChar/PrevChar` | clamp + `Next/PrevLogicalChar`(**A の重複解消**) |
| `CaretController.SnapAndClamp` | clamp + `SnapToLogicalCharStart` |
| `WordBoundary.MoveLeftCp/MoveRightCp` | `Prev/NextCodePoint` に置換 |
| `UiaTextHostAdapter.WordBoundary_WordStart/WordEnd` | ループ内の歩進を `Prev/NextCodePoint` に |
| `EditorControl.Input` 上書きモード | `overwriteLen = CodePointLengthAt(snap, caret)` |
| `PixelMapper.OffsetToPx` | `SnapToCodePointStart(span, i)` |
| `PixelMapper.PxToOffset` / `LineLayout.Wrap` / `MonoCharMetrics.MeasureRun` / `FrameBuilder.EmitWhitespaceGlyphs` | `CodePointLengthAt(span, i)` |

### 4.2 C-1: 格子幅の細分化

`TextChunk` コンストラクタの既定 `gridBytes` を 64 KB → **4 KB**(暫定値・実装時に Bench で確定)。

呼び出し元 3 箇所の扱い:

| 呼び出し元 | 扱い | 理由 |
|---|---|---|
| `AppendBuffer` ctor `new TextChunk(_block)` | **`gridBytes: BlockBytes` を明示** | §2.4 の変異前提を守る |
| `AppendBuffer` 大挿入 `new TextChunk(bytes)` | 既定のまま | 生成後不変の専用配列 |
| `TextBufferBuilder.AddChunk` | 既定のまま | Sanitize 済みの不変配列 |

`AppendBuffer` 側には理由をコメントで固定する。

メモリ増は 4 MB チャンクあたり 1024 エントリ × 12 B = 12 KB、512 MB 文書で 1.5 MB(0.3%)。
格子構築の総走査量は幅によらず O(n) なので読み込み時間は変わらない。

### 4.3 C-2: `GetChar` の単一マッピング + アロケーション除去

`GetText(pos, 1)[0]` をやめ、`IsLfAt` と同じ木降下でピースを特定 →
`CharToByte(byteStart, byteLen, posInPiece, out int actual)` を**1 回だけ**呼ぶ →
そのバイト位置から UTF-8 を直接デコードする。

サロゲート中間位置の再現が肝で、`actual` の値で 2 分岐する:

- `actual == posInPiece` … コードポイント先頭。4 バイト列なら **high サロゲート**を、
  それ以外なら BMP の文字そのものを返す。
- `actual == posInPiece - 1` … 4 バイトコードポイントの後半。**low サロゲート**を返す。

現行 `GetSubstring` の挙動(開始が中間なら低い方へスナップ、終端が中間ならコードポイントを
丸ごと含めてから `Substring`)を、1 回のマッピングで直接導く形に置き換える。

**依存する前提**(いずれも既存の `Encoding.UTF8.GetString` 経路が既に依存している):

- ピース境界はコードポイント境界(`TextBufferBuilder` / `AppendBuffer` / `PieceTree.Split`
  がいずれも境界へスナップする)= コードポイントがピースを跨がない
- バッファは `Utf8Sanitizer` 済みで不正 UTF-8 を含まない

副次的に `TextSnapshot.IsLfAt`(private)は新 `GetChar(pos) == '\n'` と等価かつ同コストに
なるため、重複として畳める(任意。やるなら同じ commit に含める)。

### 4.4 見積りと DoD

| 段階 | Ctrl+← 相当(1M 文字 ASCII) |
|---|---|
| 現状 | 1.044 ms |
| C-1 のみ(4 KB) | 約 0.065 ms |
| C-2 のみ | 約 0.5 ms |
| **C-1 + C-2** | **約 0.033 ms** |

**DoD = 1M 文字 ASCII で `WordBoundary.PrevWordStart` が 0.05 ms 未満。**
4 KB で未達なら 2 KB へ下げる。

## 5. テスト計画

### L1(Core・自動)

- 新規 `TextBoundaryTests` — サロゲート / CRLF / 孤立 CR / 孤立 LF / BMP / 空文書 /
  先頭 / 末尾。span 版も同様
- **`GetChar` 新旧差分テスト(C の中核)** — `GetText(pos, 1)[0]` が公開 API として残るので
  これを参照実装にできる。ASCII・CJK(3 バイト)・絵文字(4 バイト)・CRLF/LF/CR 混在の
  fixture について**全位置**で一致を確認
- **編集後の文書でも同じ差分テストを回す** — ピース分割後・`AppendBuffer` 経由でタイプした
  文書を対象にする。§2.4 の回帰を捕まえる唯一の実質的な網
- 既存 `TextSnapshotTests` / `NavigationCommandsTests` / `WordBoundaryTests` /
  `PixelMapperTests` / `LineLayoutTests` 全緑

### L2(Editor・自動)

- 既存 `CaretControllerSnapAndClampTests` / `CaretControllerContractTests` /
  `UiaTextHostAdapterCrlfTests` / `UiaTextHostAdapterTests` / `KeyboardNavigationTests` 全緑
- Adapter の clamp 境界(負値 / `CharLength` 超 / snapshot null)テストを追加。
  A の委譲で「同じ関数を呼ぶ」ことは構造的に保証されるが、**Adapter 側に残る前処理の
  等価性は別途押さえる必要がある**

### L3(App・自動)

既存全緑。

### L4(性能ゲート・手動)

`tests/yEdit.Core.Bench` に `GetChar` 単発(格子セル先頭 / 中央 / 末尾)と
`WordBoundary.PrevWordStart`(1M 文字 ASCII / CJK)の測定を追加。§4.4 の DoD で判定。

### L5(実機 SR 検証・手動)

**必須。** `UiaTextHostAdapter` の歩進は SR の文字 / 単語単位読みの経路そのもの。
`tools/sr-regression.ps1` も併せて実行する(UIA 応答の検証まで。実発声は L5 でのみ確認)。

### ミューテーション検証(最終品質パスのスポットチェック)

- `TextBoundary` のサロゲート判定の `&&` → `||`
- CRLF 判定の CR / LF 入れ替え
- `CodePointLengthAt` の 2 → 1
- `GetChar` の `actual == posInPiece - 1` 分岐潰し

それぞれ対象テストが赤になることを確認してから復元する。

## 6. タスク分割

| # | 内容 | 前倒しレビュー |
|---|---|---|
| 1 | `TextBoundary` 新設 + 単体テスト | **コード品質**(後続が依存する新抽象) |
| 2 | Bench 追加(現状値 = 基準線を記録) | — |
| 3 | C-2 `GetChar` 再実装 + 新旧差分テスト | **脆弱性**(手書き UTF-8 デコード) |
| 4 | C-1 格子細分化 + `AppendBuffer` 明示 + 編集後差分テスト + DoD 判定 | — |
| 5 | A: Adapter → Core 委譲 + clamp 境界テスト | — |
| 6 | B: 残り呼び出し元の付け替え(snapshot 系 / span 系) | — |

Task 3 に脆弱性レビューを置くのは、`GetChar` がファイル由来バイト列を手書きでデコードする
形になるため。`Utf8Sanitizer` 済みという前提はあるが、CLAUDE.md §3 の
「該当判定に迷ったら前倒しに倒す」に従う。

## 7. リスクと緩和

- **リスク R2(最大)**: 格子細分化が `AppendBuffer` の「`TextChunk` 生成後に同じ配列へ
  書き続ける」前提を壊す
  - 緩和: `gridBytes: BlockBytes` の明示指定 + 理由コメント + 編集後差分テスト(§5 L1)
- **リスク R1**: `GetChar` 再実装のサロゲート中間 / ピース境界の取りこぼし
  - 緩和: 全位置差分テスト(編集後文書を含む)
- **リスク R3**: `CodePoint` 系と `LogicalChar` 系の取り違えで `WordBoundary` の挙動が変わる
  - 緩和: API 名で分離 + 既存 `WordBoundaryTests`
- **リスク R4**: 手書き UTF-8 デコードの不正入力耐性
  - 緩和: Sanitizer 前提を実装コメントに明記 + Task 3 の脆弱性レビュー前倒し
- **リスク R5**: メモリ増
  - 緩和: 実測して本設計書 §9 へ追記する

## 8. 申し送り(follow-up)

- **F-1**: `CharCursor`(piece / byte offset を保持して O(1) 歩進する struct)は
  §4.4 の DoD 未達のときのみ検討。`TextBoundary` の内部実装を差し替えるだけで移行できる
- **F-2**: `SnapshotReader` がピースを丸ごと string 化する(4 MB ピース → 8 MB UTF-16 常駐)。
  `CharacterCounter` / `ConvertEols` が経由する。本設計のスコープ外
- **F-3**: D — UIA の単語境界が Core `WordBoundary` と別ロジック
  (`UiaTextHostAdapter.cs:542` のコメントが「Core WordBoundary に直接メンバがないため
  素朴実装する」と明記)。SR の単語読みと Ctrl+←→ がずれる。計画書 §5-5 で v1 許容と
  されているが、直すなら挙動変更 = L5 必須の別案件
- **F-4**: E — 「文字」の数え方が 3 通り並存する。桁 / 位置照会は CRLF=1・サロゲート=2、
  文書情報の文字数は CRLF 除外・サロゲート=1(Rune)、バッファ / UIA オフセットは
  CRLF=2・サロゲート=2。`docs/plans/2026-07-25-document-info-dialog-design.md` §4 で
  意図的な棲み分けと明記済みでバグではないが、定義が一箇所にまとまっていない。
  `docs/plans/2026-07-24-crlf-atomic-caret-design.md` §10 F-3(サロゲート=1 化)と
  合わせて回収する
- **F-5(実装後に追加・最終ブランチレビュー 2 パスの指摘由来)**: 文字単位ループ族が
  長大トークンで長時間ブロックする。**本作業は約 12 倍改善したが解消はしていない。**
  - 対象: `NavigationCommands.MoveHomeSmart`(先頭空白の連続長)/
    `WordBoundary.NextWordStart` / `PrevWordStart`(空白・改行なしの連続長)/
    `UiaTextHostAdapter.WordBoundary_WordStart` / `WordBoundary_WordEnd`
  - 実測(2M 文字・Release・本ブランチ HEAD): `MoveHomeSmart` 3,547 ms /
    `NextWordStart` 6,295 ms / `PrevWordStart` 6,124 ms(通常文書 2M は 0.011 ms)
  - **`WordBoundary` 系は UIA RPC スレッドからも到達する**
    (`TextRangeProviderV2.cs:60-61, 303, 312`)。NVDA の単語単位読みは日常操作なので、
    該当ファイルを開いているだけで SR 側が固まる。`MoveHomeSmart` にはこの経路が無い
  - 到達条件: 空白も改行も含まない長大トークン(単一行 base64 / DNA 配列 / 1 行 JSON 等)。
    300KB 程度で約 1 秒、3MB で約 10 秒
  - 実害は UI / a11y のフリーズのみ(データ破壊・情報漏洩・権限昇格なし)
  - 同族: 折り返し ON × 改行なし巨大 1 行で `UiaTextHostAdapter.TryFindVisualSegmentCore` と
    `MoveHomeSmart`(wrap overload)が論理行全体を `GetText` で string 化する(OOM)
  - 緩和候補は F-1 の `CharCursor`
- **F-6(実装後に追加・同上)**: 高速化が `AppendBuffer` の**現ブロック内テキストには効かない**。
  同クラスの共有チャンクは安全性のため格子表を先頭 1 エントリに固定しており(§2.4)、
  `CharToByte` の `CumAt(byteStart)` がブロック先頭からの線形走査になる=
  **1 回の `GetChar` あたり最大 64KB 走査という着手前の姿がこの領域だけ残る**。
  `TextSnapshot.GetChar` の `pos == 0` fast path はピース内オフセット 0 しか救わない。
  最悪ケースは「64KB 近くまで埋まったブロックで新しく打った段落を Ctrl+← でなぞる」。
  **ベンチ `--characcess` は `TextBuffer.FromString`(builder チャンク)だけを測っており
  この経路に一度も触れていない**=§9 の性能数値は読み込み済み文書に対するもの。
  根治には F-1 の `CharCursor` か `Piece` への chunk 内 char オフセット保持が要り、
  ピース木の不変条件に触れるため別案件

## 9. 実施記録

実施日: 2026-07-31 / ブランチ `feature/char-access-seam`(実装 commit 18 本)

### 9.1 格子幅の確定値

**4KB で確定**(2KB / 1KB へは下げなかった)。掃引後の中央値が閾値 0.05 ms に対して
約 4 倍の余裕を持ったため。参考値は §9.2 の表。

### 9.2 Bench `--characcess` の前後実測

**⚠️ 途中で測定方法を是正している。** 当初のベンチは単一位置(`mid = CharLength / 2`)で
測っており、4KB 格子では `500000 mod 4096 = 288`(セルの先頭 7% = 最良に近い位置)、
64KB 格子では `500000 mod 65536 = 41,248`(セルの 63% = ほぼ平均位置)と
**前後で比較位置が非対称**だった。仕様レビューが検出し、格子セル全域を掃く形へ是正した
(決定的乱択 256 位置 × 各 3 回の最小値・中央値でゲート・最悪値を併記)。

同一機・同一手法・格子幅のみ変更した最終実測:

| 格子幅 | ASCII 1M 中央値 | 最悪 | CJK 1M 中央値 | 最悪 | 判定 |
|---|---|---|---|---|---|
| 64KB(着手前) | 0.155 ms | 0.870 ms | 0.247 ms | 1.102 ms | FAIL |
| **4KB(採用)** | **0.013 ms** | 0.050 ms | 0.013 ms | 0.068 ms | **PASS** |
| 2KB | 0.006 ms | 0.029 ms | 0.006 ms | 0.028 ms | PASS |
| 1KB | 0.003 ms | 0.014 ms | 0.003 ms | 0.013 ms | PASS |

**同条件での改善は約 12 倍**(0.155 → 0.013 ms)。是正前に報告された「31 倍」は
測定位置の非対称による過大評価であり、**採用しない**。

`GetChar` 単発の位置依存(格子点 499,712 基準・4KB 格子):

| 格子点からの距離 | ns/回 |
|---|---|
| +0 B | 133 |
| +1,024 B | 695 |
| +2,048 B | 1,409 |
| +4,095 B | 2,742 |

距離あたり約 1.85 ns で線形。**格子幅がそのままコストであること**が可視化されている。

**アロケーションは 128 B/call → 0 B**(4 条件で実測)。

**適用範囲**: 上記は読み込み済み文書(`TextBufferBuilder` 由来チャンク)に対する数字。
編集中領域(`AppendBuffer` の現ブロック)には効かない — §8 F-6 参照。

### 9.3 メモリ増の実測(リスク R5 の回収)

4MB チャンク 1 個あたり、格子表 **896 B → 12,416 B**(+11,520 B = チャンクの 0.275%)。
理論値(1024 エントリ × 3 × 4 B = 12,288 B + 配列ヘッダ 24 B × 3 + 本体 56 B = 12,416 B)と一致。
**512MB 文書換算で +1.41 MB**。§4.2 の見積り(1.5MB / 0.3%)どおり。

構築時間の増加は **+1.7〜2.5%**(4MB チャンクで実測・ウォームアップ 60 回 + 交互サンプリング
n=80)。§4.2 のコメント案「読み込み時間は変わらない」の方が実態に近く、
実装中に一度 +20% と報告されたが**再現しなかった**(ウォームアップ 3 回・順序固定による
順序効果。素朴な計測では +78% まで振れる)。ソースコメントは実測値へ修正済み。

### 9.4 計画側の誤りとその是正(重要)

実装中に**設計書 / 実装計画の記述の誤りが 6 件**見つかった。いずれも実装者または
レビュアが実測で検出したもので、記録として残す。

1. **§5 の「編集後の文書でも同じ差分テストを回す = R2 の回帰を捕まえる唯一の実質的な網」は
   誤り。** `GetChar` と `GetText` は同じ `TextChunk.CharToByte` を通るため、格子が壊れると
   **両者が同じだけ壊れて一致する**。等価テストでは原理的に検出できない。
   参照を元文字列(ground truth)に取る形へ是正した。
   加えて「唯一」も誤りで、`FuzzTests.Random_edits_match_naive_model` が
   ctor 側の破れを検出していた(繰上げ側は無防備だったため新テストの価値は変わらない)。
2. **DoD の測定位置が偏っていた**(§9.2)。
3. **Task 3 の fixture に 2 バイト UTF-8 列が 1 つも含まれていなかった**
   (ASCII + CJK 3 バイト + 絵文字 4 バイトのみ)。2 バイト分岐が丸ごと未検証で、
   初回掃引で 11 変異が生存した。境界 fixture を追加して解消。
4. **実装計画 Step 5 のミューテーション用コマンドが `--filter` でクラス限定していた**ため、
   「既存テストは守れていない」と誤結論しかけた。
5. **§4.1 の棚卸し「14 ファイル・21 箇所」から `ImeCompositionState.SnapCursorPos` が
   漏れていた**(実数 15 ファイル・22 箇所)。実装者が発見し回収。
6. **計画が指定したミューテーターがアナライザに弾かれるものが複数あった**
   (`true` 置換 → S1125/S1172 / 分岐削除 → S4144 / `? 2 : 1` → `? 1 : 1` → S3923 /
   `out` 変数を定数化 → S1481)。削除・条件置換・境界ずらしの 3 系統へ差し替えた。

### 9.5 §4.1 の主張の訂正 —「統一すると `WordBoundary` の挙動が変わる」は現状テスト不能

`CodePoint` 系と `LogicalChar` 系を分ける根拠として §4.1 は
「統一すると Ctrl+←→ の単語境界が変わる」と断定したが、**現状の実装では観測差が出ない**。

`WordBoundary.ClassOf` が `'\r'` と `'\n'` を**同一の `CharClass.LineBreak`** に写すため、
すべてのループ述語が両者を同一に扱い、`LogicalChar` が飛ばす CRLF 中間位置は判定を
一度も変えない。網羅探索(長さ 5 以下の**全 16,807 文字列 × 全キャレット位置**)で
**差分ゼロ**を確認した。

**系を分けた判断自体は正しい**(旧実装に忠実)。差が出るのは `ClassOf` が CR と LF を
別クラスとして扱うようになったときで、そのとき**テストは赤くならない**。
コード側の `<remarks>` を「予防的措置であり、なぜ今はテストで守れないのか」まで書く形へ
是正した(`25f964b`)。**本設計書 §4.1 は CLAUDE.md §8 によりスナップショットとして
書き換えていない**ため、本節が訂正記録となる。

### 9.6 レビュー経緯と指摘の処理

タスクごとに仕様レビュー、Task 1 にコード品質レビュー、Task 3 に脆弱性レビューを前倒しで実施
(CLAUDE.md §3 の前倒し例外)。最終ブランチレビューは 2 パスを別エージェント・
隔離 worktree で実施。**すべて別エージェント**。

主要な指摘と処理:

| 指摘 | 処理 |
|---|---|
| Task 1: 境界述語がクラス内で 7〜10 箇所に重複 | ① 4 述語へ単一化(読み回数は不変または改善) |
| Task 1: span 版 2 箇所の変異が生存 | ① テスト追加(受容判断が span 版には当てはまらなかった) |
| Task 1: 範囲外契約が 3 家族で非対称・未文書 | ① クラス doc に表を追加 + `Assert.Throws` で固定 |
| Task 1: CRLF 後退規則の LF 判定が未被覆(**到達可能**) | ① テスト追加 |
| Task 1: 前進側 CRLF が「登録簿」の外にあった | ① `IsCrlfStartingAt` を抽出し 2×2 対称に。**抽出した瞬間にもう 1 件の到達可能な穴が露出**(`"a\nb"` で → キーが LF を巻き込む) |
| Task 2: ゲートが判定行ゼロで空振り PASS する | ① 判定行の存在をゲート条件に追加 |
| Task 3: `AppendBuffer` ブロック境界のスナップが無被覆 | ① テスト追加(両レビュアが独立に発見) |
| Task 3: `CharLen == 1` ピースで 61 倍の性能回帰 | ① `pos == 0` fast path を追加(0.32〜0.52 µs/call へ) |
| Task 3: `Debug.Assert` を継続バイトに置く案が機能しない | ① 先頭バイトの表明へ。**assert 自身の引数評価が先に例外を投げるため発火しなかった** |
| Task 4: DoD 測定位置の偏り | ① ベンチを掃引化(§9.2) |
| Task 4: 構築時間 +20% が再現しない | ① コメントを実測値(+2〜3%)へ |
| Task 6: 「統一するな」が実証不能 | ① `<remarks>` を予防的措置として書き直し(§9.5) |
| 最終: クラス doc の登録簿が不完全 | ① 箇所数の正確化 + 除外 4 ファイルの列挙 + 適用範囲の明示 |
| 最終: `IsLfAt` を畳まない理由が技術的に成立しない | ① 理由を書き換え(方針は維持) |
| 最終: `AppendBuffer` 現ブロックに高速化が効かない | ② §8 F-6 |
| 最終: 文字単位ループ族の DoS 申し送りが実態より狭い | ② §8 F-5 |

却下したもの: `SnapToLogicalCharStart` の改名 / `ThrowIfNull` の二重呼び出し /
`pos` と `i` のパラメータ名不統一(座標系が違うため名前の差に情報がある)。

### 9.7 テスト数の推移

Core 997 → **1026** / Editor 306 → **312** / App 444(変化なし)。

### 9.8 L5 実機 SR 検証

(ユーザー実施後に追記)
