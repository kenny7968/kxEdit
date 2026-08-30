# 単発置換が「現ヒット」と「許容範囲」を取り違える 設計書(A-14 / T-3)

- 日付: 2026-08-29
- 出自: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §4 の **A-14** と **T-3**
  (T-3 は PR #38 設計書 `2026-08-06-search-selection-scope-staleness-design.md` §6 の申し送りでもある)
- 対象: `src/kxEdit.App/SearchController.cs` / `src/kxEdit.Editor/EditorControl.cs` /
  `src/kxEdit.Core/Text/TextBoundary.cs`

本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録のみ追記する。

## 1. 問題

`SearchController.ReplaceOne`(単発の「置換」)は、置換に必要な 2 つの情報を
**どちらも保持せず、その場で再導出**している。

| 必要な情報 | 現状 | 帰結 |
|---|---|---|
| いま置換すべきヒット | `ed.GetSelectionCharRange()` から再導出 | CRLF / サロゲートのスナップで実ヒットとずれ、**別の出現を置換**する(A-14) |
| 置換してよい範囲 | 参照しない | 「選択範囲のみ」ON でも**範囲外を置換**して成功発声する(T-3) |

`Find` は本物のヒット `MatchSpan` を持っているのに、選択へ書き込んだ時点でその情報を捨てている。
`_lastHit` は保持しているが、照合は `selStart == h.Start && selEnd == h.End` という
**スナップ前の値との比較**なので、スナップが起きるケースでは必ず外れる。

### 1.1 A-14 の機構(コード読解で確定)

`EditorControl.SelectCharRange` → `SetSelectionCharRange` → `CaretController.SetSelection` は
両端に `SnapAndClamp` = `TextBoundary.SnapToLogicalCharStart` を通す。この関数は
**CRLF の LF 側 / サロゲートの low 側を指す位置を 1 つ後退させる**(PR #26 のキャレット atomic 化)。

文書 `abc\r\ndef`(CRLF)に対し正規表現 `\n` を検索した場合:

| 段 | 値 |
|---|---|
| ヒット | `MatchSpan(4, 1)`(LF のみ) |
| `SelectCharRange(4, 1)` 後の選択 | `[3, 5)` = `"\r\n"`(先頭が 4→3 へ後退) |
| `ReplaceOne` が作る `span` | `MatchSpan(3, 2)` |
| `searcher.ReplacementAt(snap, span, …)` | `\n` は `"\r\n"` に一致しないので **null** |
| 落ちる分岐 | 「現ヒット未選択」→ `FindNext(snap, selEnd=5)` → **次の出現を置換** |

`\r` は逆側で壊れる。ヒット `MatchSpan(3, 1)` に対し、始端 3 はそのまま・終端 4 が 3 へ後退して
選択が **`[3, 3)` = ゼロ幅**になる。`Find` の前進条件 `selEnd == h.End`(3 == 4)が成立せず、
`from = selEnd = 3` で同じ位置を再び見つけるため **F3 が前進しない**。

### 1.2 `ReplaceAll` は壊れていない

`ReplaceAll` は `searcher.ReplaceInRange` で断片を組み、`ed.ReplaceCharRange(0, CharLength, fragment)`
と**範囲全体を丸ごと差し替える**。両端 0 / `CharLength` はスナップが恒等なので巻き込みが起きず、
`\n` → `X` は `abc\rXdef` という正しい結果を出す。**壊れているのは単発置換だけ**であり、
本件は「単発を一括に揃える」修正になる。

なお `abc\rXdef` の単独 CR は改行として正しく数えられる
(`TextChunk` の `BreaksTo` 規約 = 「LF 数 + LF が直後に続かない CR 数」)。

### 1.3 T-3 の機構

`ReplaceOne` には `d.InSelection` も `_selectionScope` も一切現れない。PR #38 は
`ReplaceAll` にだけ世代付きスコープ検証を入れ、`ReplaceOne` を明示的に申し送りにした。

## 2. 方針

### 2.1 現ヒットを世代付きで保持する(A-14 の主修正)

`_lastHit` を `MatchSpan?` から次へ拡張する。

```csharp
private (WeakReference<TextSnapshot> Snap, MatchSpan Hit, int SelStart, int SelEnd)? _lastHit;
```

- `SelStart` / `SelEnd` は **`SelectCharRange` の直後に `GetSelectionCharRange()` を読み戻した値**。
  スナップ規則を App 層に複製せず、Editor が実際に適用した結果を記録する。将来スナップ規則が
  変わっても(書記素クラスタ導入等)App 側は追随不要になる。
- 「現ヒットが生きている」の判定は 2 条件の論理積:
  1. `_lastHit.Snap` が現在のスナップショットと参照同一(= 文書が編集されていない)
  2. 現在の選択が `(SelStart, SelEnd)` と一致(= ユーザーが選択を動かしていない)
- 生きているときだけ `Hit` を「現ヒット」として使う。生きていなければ従来どおり選択から再導出する
  (ユーザーが手で選び直した場合の経路をそのまま残す)。

弱参照にする理由は `_selectionScope` と同じ(PR #38 §5.1)。判定は変わらない —— 捕捉元が
生きていれば必ず現在のスナップショットと同一なので回収されえず、回収済みなら「生きていない」
= 安全側に倒れる。開き直し・復元・タブクローズで旧ピース木をピン留めしない。

適用箇所:

| 箇所 | 変更 |
|---|---|
| `Find`(forward) | 前進起点を `h.Start + Max(1, h.Length)` にする条件を、生きている現ヒットの有無で判定する |
| `Find`(backward) | `before` を、生きている現ヒットがあれば `h.Start`、なければ従来どおり `selStart` にする |
| `ReplaceOne` | 生きている現ヒットがあればそれを置換対象にする(`ReplacementAt` の往復に頼らない) |

`Find`(backward)で `h.Start` を使うのは、スナップで `selStart` が `h.Start` より小さくなった
とき(CRLF の LF ヒット)に `[selStart, h.Start)` 内のヒットを取りこぼさないため。スナップが
起きないケースでは `h.Start == selStart` なので**挙動不変**。

### 2.2 CRLF 内部のヒットを「巻き込みを戻して」置換する(A-14 の副修正)

`EditorControl.ReplaceCharRange` も両端に `SnapAndClamp` を掛けるため、`(4, 1)`(LF のみ)を
渡すと `[3, 5)` = CRLF 全体が消える。**現ヒットを正しく特定できても、置換 API がそれを表現できない。**

Editor に厳密置換 API を 1 本足す。

```csharp
/// [start, start+length) だけを厳密に置換する。両端が CRLF / サロゲートの内側を指していても、
/// 外側へ巻き込んだ文字は復元して書き戻す(= ReplaceInRange 経由の一括置換と同じ結果になる)。
public void ReplaceCharRangeExact(int start, int length, string replacement)
```

実装は「外側へ広げて、はみ出し分を前後に足し戻し、既存 `ReplaceCharRange` へ委譲」する。

```
s0 = Clamp(start), e0 = Clamp(start + length)          // 既存と同じ long 経由のオーバーフロー対策
s  = TextBoundary.SnapToLogicalCharStart(snap, s0)     // 外側へ(後退)
e  = TextBoundary.SnapToLogicalCharEnd(snap, e0)       // 外側へ(前進・新設)
text = snap.GetText(s, s0 - s) + replacement + snap.GetText(e0, e - e0)
ReplaceCharRange(s, e - s, text)
```

委譲先の再スナップは**恒等であることが証明できる**(`s` / `e` は既に論理文字境界にある)ので、
編集の副作用(`AfterEdit` / キャレット規約 / Undo 単位 / UIA イベント)は 1 箇所に保たれる。

`abc\r\ndef` の検算:

| 入力 | `s0, e0` | `s, e` | `text` | 結果 |
|---|---|---|---|---|
| `\n` ヒット `(4,1)` → `X` | 4, 5 | 3, 5 | `"\r" + "X"` | `abc\rXdef` |
| `\r` ヒット `(3,1)` → `X` | 3, 4 | 3, 5 | `"X" + "\n"` | `abcX\ndef` |

どちらも §1.2 の `ReplaceAll` と同じ結果になる。

**既存 `ReplaceCharRange` の契約は変えない。** 現契約(両端スナップ)に乗っている呼び出し側が
`CsvController.cs:267` と `KinsokuFormatController.cs:71` にあり、どちらも行/セル境界の
広い範囲を置換するため巻き込みは起きないが、契約変更の影響確認は本件の目的外。

#### 新設: `TextBoundary.SnapToLogicalCharEnd`

`SnapToLogicalCharStart` の対(外側へ前進する版)。`TextBoundary` は
「境界規則の唯一の定義」を置く場所であり、前進 / 後退 × サロゲート / CRLF の述語が既にそろっている。

```csharp
public static int SnapToLogicalCharEnd(TextSnapshot snap, int pos)
    // pos が論理文字の途中(CRLF の LF 側 / サロゲートの low 側)なら pos + 1、それ以外は pos。
    // 両端は [0, CharLength] にクランプ。
```

### 2.3 置換操作を選択範囲に閉じる(T-3)

**「選択範囲のみ」のスコープは置換操作(`ReplaceAll` / `ReplaceOne`)にだけ効かせる。**
`UpdateCount` と F3 は全文のままとする(§6 の申し送り)。

`ReplaceAll` が持つスコープ検証(null 判定 + 弱参照の世代比較 + 文言)を private ヘルパーへ
括り出し、`ReplaceOne` も同一実装・同一文言を使う。

```csharp
// 戻り値: スコープが使えるなら (Start, End)、使えないなら null(発声は呼び出し側が済ませる)
private (int Start, int End)? TryResolveScope(TextSnapshot snap);
```

`ReplaceOne` の追加規約:

- 探索起点を `Math.Max(選択の始端, scope.Start)` にクランプする。これによりスコープより前の
  ヒットは自動的に飛ばされ、以降は `hit.Start >= scope.Start` が保証されるので、包含判定は
  **`hit.End <= scope.End` の 1 条件**で足りる。
  **起点に選択の終端ではなく始端を使う**(スコープなしの経路は従来どおり終端=挙動不変)。
  終端だと、範囲を選んで「選択範囲のみ」を ON にした直後は選択がスコープと一致するため
  起点がスコープの外に出てしまい、範囲内に未置換のヒットがあるのに 1 回目の「置換」が空振りする。
  始端なら「トグル直後はスコープ先頭から / スコープより前のキャレットは繰り上げ /
  スコープより後ろのキャレットは前方=包含判定で弾かれる」が一様に成り立つ。
- 生きている現ヒットがスコープ外なら、現ヒット扱いをやめて上の探索経路へ落とす。
- 見つかったヒットが `hit.End > scope.End` なら置換せず「これ以上見つかりません」。
- 置換後は `scope.End += repl.Length - hit.Length` で伸縮させ、**新しいスナップショットで捕捉し直す**
  (`ReplaceAll` と同じ復帰処理。これが無いと 2 回目の置換が「陳腐化」で拒否される)。
- 置換後の次ヒット探索にも同じスコープ制約を掛ける。

発声文言(すべて `ReplaceAll` の既存文言を再利用し、新規文言は追加しない):

| 状況 | 文言 |
|---|---|
| `InSelection` ON でスコープ未捕捉 | 「選択範囲がありません」 |
| `InSelection` ON でスコープが陳腐化 | 「選択範囲が変わりました。選択し直してください」 |
| スコープ内に次のヒットが無い | 「これ以上見つかりません」 |
| 置換したが次が無い | 「置換しました。これ以上見つかりません」 |
| 置換して次へ | 「置換しました。{Total} 件中 {Ordinal} 件目」(`Locate` は従来どおり全文基準) |

## 3. 触らないもの

- **`UpdateCount`** — スコープを参照せず全文を数える現挙動を維持する(§2.3 の決定)。
- **`Find` / `FindNext` / `FindPrev` のスコープ制約** — 同上。前進起点の判定だけを直す(§2.1)。
- **`ReplaceCharRange` の契約** — §2.2 のとおり据え置き、新 API を並べる。
- **`ReplaceAll` の置換経路** — `ReplaceInRange` + 範囲丸ごと差し替えは既に正しい(§1.2)。
  スコープ検証のヘルパー抽出による**挙動不変のリファクタのみ**行う。
- **`SnapshotSearcher` / `TextSearcher` などの Core 照合** — 本件はヒットの特定と適用の問題で、
  照合結果そのものは正しい。M-29(範囲内照合の起点)は別件として §6 に残す。
- **`CsvController` / `KinsokuFormatController`** — `ReplaceCharRange` の呼び出し側は据え置き。

## 4. テスト

### L3 — `tests/kxEdit.App.Tests/SearchControllerTests.cs`

テストホストは実 `DocumentManager` + 実 `EditorControl` を STA で使うので、CRLF スナップは
**本物が走る**。`Editor.Text` セッターは `TextBuffer.FromString` で正規化しないため、CRLF fixture を
そのまま置ける。fixture は前後に非ヒット部を持たせ、全選択と部分選択を弁別できる形にする(§4-B)。

| # | 内容 | 意図 |
|---|---|---|
| 1 | CRLF 文書で `\n` を検索 → 単発置換 → **その位置**が置換され、次の出現は無傷 | A-14 の回帰(修正前に赤であることを実測で確認する) |
| 2 | 同上で結果本文が `ReplaceAll` の結果と一致する | §1.2 の「単発を一括に揃える」を固定 |
| 3 | CRLF 文書で `\r` を検索 → F3 が次の CR へ前進する | A-14 の裏側(ゼロ幅選択) |
| 4 | `\r` の単発置換が LF を巻き込まない | §2.2 の巻き込み復元 |
| 5 | 「選択範囲のみ」ON で、範囲外のヒットを単発置換しない(本文不変 + 「これ以上見つかりません」) | T-3 の回帰 |
| 6 | 同 ON で範囲内は従来どおり置換でき、範囲末で止まる | 過剰無効化の網 |
| 7 | 同 ON で連続 2 回の単発置換が 2 回目も通る(スコープ伸縮) | PR #38 §5.1 と同じ復帰経路 |
| 8 | 同 ON でスコープ陳腐化 → 置換せず「選択範囲が変わりました。選択し直してください」 | `ReplaceAll` との文言・判定の一致 |
| 9 | 現ヒットが生きていないとき(ユーザーが選択を動かした)は従来経路 | 挙動不変の網 |

### L2 — `tests/kxEdit.Editor.Tests`

| # | 内容 |
|---|---|
| 10 | `ReplaceCharRangeExact` が CRLF の LF のみを置換し CR を残す |
| 11 | 同、CR のみを置換し LF を残す |
| 12 | 同、サロゲートペアの low 側だけを指した場合に high を復元する |
| 13 | 論理文字境界に乗った範囲では `ReplaceCharRange` と同結果(委譲の恒等性) |
| 14 | 範囲外・負長・オーバーフロー引数のクランプが `ReplaceCharRange` と同じ |

### L1 — `tests/kxEdit.Core.Tests`

| # | 内容 |
|---|---|
| 15 | `TextBoundary.SnapToLogicalCharEnd` の 2×2(CRLF / サロゲート × 内側 / 境界上)+ 両端クランプ |

### ミューテーション検証

CLAUDE.md §4-A の「有効」に該当する(テキスト選択範囲の算出・置換エンジンのコアロジック)。
最終品質パスのスポットチェックとして次を確認する。

- `SnapToLogicalCharEnd` の `pos + 1` を `pos` 固定 → #4 / #11 / #15 が赤
- 包含判定 `hit.End <= scope.End` を `true` 固定 → #5 が赤 / `false` 固定 → #6 が赤
- 現ヒット生存判定の 2 条件を片方ずつ `true` 固定 → #1(スナップショット側)/ #9(選択側)が赤
- 起点クランプ `Math.Max(起点, scope.Start)` を素の起点へ戻す → #6 が赤

## 5. 保持量

`_lastHit` が `TextSnapshot` への弱参照を 1 本増やす。強参照は増えないため保持量は不変。
`_selectionScope` と合わせて弱参照 2 本になる。

## 6. 申し送り

- **`UpdateCount` と F3 が「選択範囲のみ」を見ない非一貫。** 「12 件」と表示されたまま単発置換は
  範囲末で止まる。データは壊れないが、SR ユーザーには件数と実際の到達範囲の食い違いが分かりにくい。
  検索セッション全体をスコープに閉じる案(VSCode 相当)は退行面が広いため本ブランチでは扱わない。
- **M-29**(監査 §6): 「選択範囲のみ」の照合が文書全体基準のため、選択直前から始まるヒットが
  選択内の文字を食い、`aaa` の `[1,3)` を `aa` で検索すると 0 件になる。本件と同じ subsystem だが
  原因は Core の `TextSearcher` 側で、修正はスコープ内での再照合を要する。
- **M-25**(監査 §6): CSV F2 編集中の `ReplaceCharRange` が古い `start/length` でコミットする件。
  `ReplaceCharRangeExact` を足す本件で `ReplaceCharRange` の呼び出し側を再点検したが、
  M-25 は「範囲がずれている」問題であって巻き込みの問題ではないため本件では直らない。
- **`ReplaceCharRange` の巻き込み契約そのもの。** 巻き込みを望む呼び出し側は現時点で存在しない
  (2 箇所とも境界に乗る範囲を渡している)。将来、契約を厳密側へ一本化してよいかを検討する。

## 7. L5 判定

**必要**。`ReplaceOne` から `IAnnouncer.Say` 経由の発声が新たに 2 種類出るようになり
(「選択範囲がありません」/「選択範囲が変わりました。選択し直してください」)、
CLAUDE.md §5 の「App の Speech 系に触れる変更」に該当する。また置換位置そのものが変わるため、
UIA の選択変更が SR にどう伝わるかを実機で確認する必要がある。

実機確認項目は実装後に `2026-08-29-replace-one-hit-and-scope-l5-checklist.md` へ起こす。

## 8. 工程

CLAUDE.md §3 の簡略化基準には**該当しない**(3 プロジェクトに跨り、新 API を 2 本足す)。
通常工程で進める。

1. Task 1: `TextBoundary.SnapToLogicalCharEnd` + L1 テスト
2. Task 2: `EditorControl.ReplaceCharRangeExact` + L2 テスト
   — 後続タスクが依存する新 API のため、CLAUDE.md §3-4 の前倒し**コード品質レビュー**を行う
3. Task 3: `_lastHit` の世代付き化と `Find` / `ReplaceOne` の現ヒット判定 + L3 テスト(#1〜#4, #9)
4. Task 4: スコープ検証ヘルパー抽出と `ReplaceOne` のスコープ制約 + L3 テスト(#5〜#8)
5. 最終ブランチレビュー 2 パス(コード品質 / 脆弱性)を**別エージェントで独立に**起動
6. 品質ゲート `tools/pre-merge-check.ps1` EXIT 0 → PR

外部入力のパース・パス操作・プロセス起動・WebView いずれにも触れないため、
タスク時の前倒し脆弱性レビューは行わない(最終 2 パスの脆弱性パスは実施する)。

---

## 9. 実装時の追記(実施記録)

本節は**実施記録**(CLAUDE.md §8 が認める追記)。§1〜§8 の策定内容は書き換えていない。

### 9.1 サロゲートペアを割るヒットは原理的に救出できない(Task 2)

§2.2 の検算表は CRLF しか扱っていなかったが、サロゲートペアには CRLF と**非対称な制約**がある。

ピース木は本文を **UTF-8 で保持する**(`TextBuffer.FromString` / `AppendBuffer.Append` がいずれも
`Encoding.UTF8.GetBytes`。後者の doc に「孤立サロゲートは既定で U+FFFD 置換」と明記済み)。
したがって**孤立サロゲートを含む文字列は文書に書き込めない**。

- 文書 `a😀b` の low サロゲート `(2,1)` を `X` へ置換すると、巻き込みを復元しても
  `a\uFFFDXb` になる。high サロゲートが単独で残せないため。
- これは `ReplaceCharRangeExact` 固有の欠陥ではなく**保存層の契約**であり、
  一括置換(`ReplaceInRange` + 範囲丸ごと差し替え)も同じ結果を出す。
- .NET の正規表現 `.` は UTF-16 code unit 単位で照合するため、孤立サロゲートに単独ヒットしうる。
  つまり到達可能な経路である。

計画に書いた期待値 `a\uD83DXb` は**到達不能**だった(実装エージェントが実測で反証)。
[[plan-code-is-not-ground-truth]] の実例がまた 1 件増えた。

### 9.2 ゼロ幅挿入だけは「広げない」を選んだ(Task 2 の仕様レビュー指摘)

全数プローブで、**新 API が既存 API より結果を悪化させる入力**が見つかった。

| 入力(文書 `a😀b`) | 既存 `ReplaceCharRange` | 当初の `ReplaceCharRangeExact` |
|---|---|---|
| `(2, 0, "X")` ゼロ幅挿入 | `aX😀b`(絵文字無傷) | `a\uFFFDX\uFFFDb`(絵文字が破壊) |

ゼロ幅でも両端を外側へ広げるため `[1,3)` になり、孤立サロゲート 2 個を書き戻していた。
**既存 API なら 1 文字も壊さない入力で、新 API が新たにデータ破壊を持ち込んでいた。**

判断: **ゼロ幅(`s0 == e0`)は広げない。** 巻き込み復元は「論理文字の内側の文字を
*置換する*」ために要るものであって、挿入には分割すべき文字が無い。既存 API と同じく
境界へスナップして挿入すれば論理文字は 1 つも壊れない。

トレードオフ: **ゼロ幅マッチに限り、単発置換と一括置換の結果が食い違う**
(一括側は広げた断片を組むので U+FFFD 化する)。§1.2 の「単発を一括に揃える」という
本設計の柱をゼロ幅だけ意図的に外している。無警告のデータ破壊を消すほうを採った
(判定基準で「ユーザーデータの喪失・破壊」が最上位のため)。§6 の申し送りに
「一括置換側の同じ破壊」を追加する。

### 9.3 §6 申し送りへの追加

- **一括置換(`ReplaceInRange`)もゼロ幅マッチ / サロゲート割りで U+FFFD 化する。**
  §9.2 で単発置換だけを直したので、次はこちらを揃える(または両方で拒否する)判断が要る。
- **`ReplaceCharRangeExact` の `ReadOnly` 入口ガードは単独ではテストで固定できない**
  (委譲先が同じガードを持つため等価変異になる)。テスト側のコメントに
  「固定しているのは観測可能な契約であって入口ガードの存在ではない」と明記してある。

### 9.4 計画の `ReplaceOne` は新しい退行を作るところだった(Task 3)

§2.1 は `ReplaceOne` を「生きている現ヒットがあればそれを置換、無ければ従来どおり
`FindNext(selEnd)`」の 2 分岐で書いていた。**これは実装すると退行になる。**

変更前のコードは、選択から作った span を `ReplacementAt` に通していた。つまり
**Find を経由せず手で語を選んで「置換」を押す**操作が、その分岐で動いていた。2 分岐に
畳むとこの経路が消え、**選択の次の出現が置換される** —— A-14 とまったく同じ症状の別経路になる。

実装エージェントが実測で発見した(`Expected: "X abc" / Actual: "abc X"`)。最終形は 3 分岐:

1. 生きている現ヒット(`LiveHit`)があり現 searcher で置換可能なら、それを置換する
2. 選択そのものがヒットなら、選択を置換する(**旧挙動の保存**)
3. どちらでもなければ `FindNext` で次を検索して即置換(G-3 / VSCode 準拠)

この 3 分岐は束ねられない。span の出所(`hit` / 選択 / `next0`)も失敗時の意味
(前 2 つはフォールスルー、3 つ目は発声して return)も違う。

### 9.5 網の欠落を 2 回、レビューが掘り当てた(Task 3 / Task 4)

どちらも「テストは全部緑」なのに、**変更した条件式そのものに網が無かった**箇所。

- **第 1 分岐の `&& ReplacementAt(...)` ガード**(Task 3 品質レビュー)。到達経路は
  「Find した後に検索語だけ変えて『置換』を押す」。`ResolveSearcher` は照合条件の変化で
  searcher を作り直すが `_lastHit` はクリアされないので、`LiveHit` が生きたまま
  `ReplacementAt` だけが外れる。ガードを外す変異は 62 本すべて緑のまま生存した。
  変異下の実測値は `"X def"` —— 選択中の `abc` は新パターン `def` の**ヒットですらない**のに、
  新パターンの置換文字列で無言に潰される。
- **第 1 分岐の `WithinScope` ガード**(Task 4 セルフレビュー)。計画のテスト 8 件は
  すべて `_lastHit` が立たない経路(手動選択・キャレット移動)しか通らず、第 1 分岐に
  到達していなかった。ゼロ幅ヒット(`(?=abc)`)なら選択が幅ゼロになり第 2 分岐が
  `selEnd > selStart` で短絡するので、第 1 分岐だけを弁別できる。

**教訓**: 分岐に条件を足したら、**その分岐に到達するテストが実在するか**を変異で確かめる。
「テストが全部緑」は「その条件に網がある」を意味しない。3 分岐に同じガードを足す設計では、
**ガードごとに固有の killer が居ること**まで確認する(片方を外して落ちるテストが
もう片方を外したときと同じなら、どちらかの網が足りていない)。

### 9.6 「網が書けない」の申告が 1 件、反証された(Task 3)

`FindNext(snap2, afterRepl)` を `span.Start + repl.Length` に戻す変異について、実装エージェントが
「差分窓が 1 code unit しかないので読める App レベルのテストが書けない」と申告した。
レビュアーが実際に書いて反証した。

fixture: 本文 `"a\r\nb"` / パターン `(?<=\r)(?=\n)|(?<=X)` / 置換 `"X"` / 正規表現 ON。
本文も選択も両者で完全に一致するが、**通知が「2 件中 1 件目」対「2 件中 2 件目」で割れる**。
差の実体は「次のヒットを 1 件飛ばす」で、直している A-14 と同じクラスの実害だった。

**教訓**: 「観測できない」の根拠が「主要な出力(本文・選択)が同じだから」であるときは、
ほぼ確実に過小申告。このアプリは `IAnnouncer.Say` の文言(件数・序数)という**第 2 の観測面**
を持つ。SR 対応アプリなので発声文言は一級の観測対象であり、位置ずれ系の変異はたいてい
ここに出る。**網を探す順序: 本文 → 選択 → 発声文言 → キャレット。**

### 9.7 §6 申し送りへの追加(Task 4)

- **起点クランプが前方のみ。** `Math.Max(selStart, scope.Start)` なので、キャレット / 選択が
  スコープより**後方**にあると、スコープ内に未置換ヒットが残っていても
  「これ以上見つかりません」で止まり、以降何度押しても置換できない
  (`ReplaceOne_InSelection_CaretAfterScope_DoesNotReplaceOutsideScope` が固定している挙動)。
  仕様どおりだが、§6 の「件数表示と実際の到達範囲が食い違う」と同種の非一貫。
  スコープ内で折り返すか、範囲外である旨を発声するかは次のブランチで判断する。
- **スコープ外のヒットが選択されている状態のフィードバックが弱い。** 「これ以上見つかりません」
  と発声するが、ユーザーの目 / SR には「いま選択されている語」がある。「なぜ置換されないのか」が
  伝わらない可能性がある。本ブランチは「新規発声文言を追加しない」制約で実装したので、
  L5 の項目 ⑤ を主観判定として立て、判断材料を集める。
