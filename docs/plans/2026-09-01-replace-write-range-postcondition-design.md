# 置換の書込範囲を事後条件で保証する 設計書(B2)

策定日: 2026-09-01 / ベース: main `06721e5`(PR #60 = B1 マージ後)

傘設計書 `2026-08-31-v0.2-remaining-work-design.md` §4 の **B2** に対応する。
本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行う。

出所は PR #56 設計書(`2026-08-29-replace-one-hit-and-scope-design.md`)§6 / §9.3 / §9.10 の
申し送りと、監査 §6 の M-29。**4 件を 1 ブランチで扱う**。1 件ずつでは分割損のほうが大きく、
かつ 4 件のうち 3 件が「置換が実際に触る範囲」という同じ軸の上にある。

## 1. 問題

### 1.1 ゼロ幅ヒットがスコープ外へ書き込む(PR #56 §9.10)

「選択範囲のみ」ON の単発置換で、**ユーザーが選択していない位置を書き換えたうえ成功発声する**。

`SearchController.WithinScope`(`:575`)は**生の UTF-16 span** で包含を判定する。一方
`EditorControl.ReplaceCharRangeExact`(`:1279`)はゼロ幅(純挿入)のとき挿入点を
`TextBoundary.SnapToLogicalCharStart` で**論理文字の境界まで後退**させる。
`scope.Start` が論理文字の内側にあると、判定は通るのに書込がスコープの外へ落ちる。

実測再現(PR #56 で確認済み):

| 手順 | 状態 |
|------|------|
| `"X\rYZ"` の `[2,4)` を選択して「選択範囲のみ」ON | scope `[2,4)` |
| `Y` → `\n` を置換 | 本文 `"X\r\nZ"` / scope `[2,4)`・**位置 2 は CRLF の内側** |
| `(?<=\r)` で `Q` を置換 | ヒット `[2,2)` は `WithinScope` を通る → 挿入点が 1 へ後退 → **`"XQ\r\nZ"`**。位置 1 はスコープ外 |

トリガは「スコープ内の置換がスコープ端に CRLF を作る」+「ゼロ幅正規表現」の同時成立に限られる。

**前置ガードの列挙では塞げない**(監査 §9 V-7 の教訓と同型)。後退が起きる条件を App 側で
数え上げるのは `ReplaceCharRangeExact` の規則の複製であり、規則が変われば黙って腐る。

### 1.2 一括置換がゼロ幅・サロゲート割りで U+FFFD 化する(PR #56 §9.3)

PR #56 §9.2 は、単発置換の**ゼロ幅は外側へ広げない**と決めた。`"a😀b"` の `(2, 0, "X")` を
広げると孤立サロゲート 2 個を書き戻すことになり、ピース木が UTF-8 で保持する以上
`AppendBuffer.Append` の既定フォールバックで U+FFFD へ潰れるためである。

一括置換(`TextSearcher.ReplaceInRange`)には同じ手当てが入っていない。範囲全体の断片を
`Regex` の結果から組むため、**ゼロ幅マッチが論理文字の内側に立つとそこへ素直に挿入する**。

| 文書 | パターン | 単発置換 | 一括置換(現状) |
|------|---------|---------|---------------|
| `"a😀b"` | `(?<=\uD83D)` → `X` | `"aX😀b"` | **`"a�X�b"`**(絵文字が破壊) |
| `"a\r\nb"` | `(?<=\r)` → `X` | `"aX\r\nb"` | `"a\rX\nb"`(CRLF が 2 個の改行へ分裂) |

前者は**無警告のデータ破壊**、後者は CRLF を 1 論理文字として扱う本エディタの不変条件
(PR #26)の破れ。どちらも「N 件置換しました」と成功発声する。

なお**非ゼロ幅でサロゲートペアを割るヒット**(`.` が孤立 high サロゲートに単独ヒットする等)は
PR #56 §9.1 のとおり**保存層の制約**であり、単発・一括のどちらも U+FFFD になる。
これは本件の対象外(既に一致しており、この層では直せない)。

### 1.3 `ReadOnly` の no-op でスコープが伸縮し成功発声する(PR #56 §9.10)

`ReplaceCharRangeExact` は `ReadOnly` のとき何も書かずに戻るが、`ReplaceOne` はそれを見ずに
`_selectionScope` を更新し置換成功として発声する。`snap2 == snap` なので**世代チェックを通る
不正なスコープ**が残る。

**到達性は実質なし**。`ReadOnly=true` は CSV モードと保存中の一時解除だけで、前者は
`IsCsvModeActive`(`:275` / `:440`)が先に弾き、後者に `ReplaceOne` が割り込む経路がない。
それでも直すのは、1 行のガードで「呼び出し側が委譲先の no-op を見ていない」構造そのものが
消えるため。

### 1.4 M-29 — 「選択範囲のみ」の一括置換がスコープ内で再照合しない

`TextSearcher.ReplaceInRange`(`:162-167`)は `_regex.Matches(text)` で**文書全体**を照合し、
`m.Index < s` を捨てるだけである。スコープ直前から始まるヒットがスコープ内の文字を食うと、
**そのスコープ内に本当にあるヒットが検出されない**。

`"aaa"` の `[1,3)` を `aa` で一括置換 → 全文ヒットは `[0,2)` のみ → `m.Index < 1` で捨てられ **0 件**。

**単発置換だけは既に当たる**。`FindNext` は `_regex.Match(text, from)` で `from` に**再アンカー**
するため `[1,3)` を見つける。つまり現状は**単発と一括が食い違っている**。
`LiteralWindowSearchStrategy.ReplaceInRange` も `FindNext` 起点なので当たる。
食い違っているのは材質化経路(閾値以下=通常の全文書)だけであり、
`MaterializedSearchStrategy` は SnapshotSearcher の doc が「**意味論の「正」**」と宣言している
経路である。

## 2. 方針

### 2.1 「実際に書く範囲」を問う照会 seam を足す(1.1)

`EditorControl` に、**書かずに実書込範囲だけを返す**照会 API を足す。

```csharp
/// <summary>ReplaceCharRangeExact が同じ引数で呼ばれたとき、
/// 本文の内容が変わりうる文字範囲を、何も書かずに返す。</summary>
public (int Start, int End) GetExactChangeRange(int start, int length)
```

`ReplaceOne` / `ReplaceAll` は**書く直前**にこれを呼び、スコープ包含を検査して外れるなら
**書かずに拒否する**。

```csharp
if (scope is { } sc)
{
    var w = ed.GetExactChangeRange(span.Start, span.Length);
    if (w.Start < sc.Start || w.End > sc.End)
    {
        Announce("選択範囲の外に及ぶため置換できません");
        return;
    }
}
```

**なぜ照会なのか。** 「事後条件で検査する」の要点は*結果を問う*ことであって*時点*ではない。
書いてから Undo で巻き戻す案は履歴を汚し Undo 粒度に依存する。許容範囲を
`ReplaceCharRangeExact` に渡すガード付きオーバーロード案は「呼び出し側が忘れられない」利点が
あるが、**Editor 層が検索スコープという App の概念を知る**ことになり署名も重くなる。
照会 API なら Editor は「何を書くか」だけを答え、方針判断は App に残る。

**規則の二重実装を作らないこと**が本 seam の唯一の要求である。範囲計算を private ヘルパー
`ExactRangeParts(snap, start, length) → (S0, E0, S, E)` へ括り出し、
`ReplaceCharRangeExact` と `GetExactChangeRange` の**両方がそれを呼ぶ**。
`ReplaceCharRangeExact` の挙動は 1 bit も変えない(括り出し前後で同一式)。

**検査対象は「内容が変わりうる範囲」** とする。`ReplaceCharRange` へ渡す「広げた範囲」ではない。

巻き込み復元は**長さ保存**なので、広げた分の prefix / suffix は原則そのまま書き戻され、
スコープ外の内容は変わらない。例外は**復元する半身が孤立サロゲートになる場合**だけで、
このとき UTF-8 往復で U+FFFD へ潰れる(PR #56 §9.1)。CRLF を割ったときの `\r` / `\n` は無傷で戻る。

| ケース | 変わりうる範囲 | 判定 |
|--------|---------------|------|
| 非ゼロ幅・広がりなし | `[s0,e0)` | 通す |
| 非ゼロ幅・端で CRLF を割る | `[s0,e0)`(復元は無傷) | 通す |
| 非ゼロ幅・端でサロゲートペアを割る | 広げた `[s,e)` | **拒否** |
| ゼロ幅で `scope.Start` より前へ後退 | `[at,at)` | **拒否** |

```csharp
bool prefixCorrupts = s < s0 && char.IsLowSurrogate(snap.GetChar(s0));
bool suffixCorrupts = e > e0 && char.IsLowSurrogate(snap.GetChar(e0));
```

`s < s0` になる後退要因は「`s0` が low サロゲート」か「`s0` が LF で直前が CR」の 2 つしかないので、
`s0` の文字が low サロゲートかで弁別できる(終端側も同じ)。`s < s0` は `s0 < CharLength` を
含意する(`SnapToLogicalCharStart` は `pos >= CharLength` を動かさない)ので `GetChar(s0)` は安全。

**CRLF を通す判断は必須であり、選択の余地がない。** 策定中に当初案「広げた範囲で一律に拒否」を
既存テストへ当てて**反証した**: PR #56 §9.9 が main 既存バグとして根治した「スコープ端が
CRLF の内側にある一括置換」は**成功しなければならない**
(`SearchControllerTests.cs:1063` `ReplaceAll_InSelection_ScopeEndInsideCrlf_DoesNotDuplicateCr` /
`:1093` `..._ScopeStartInsideCrlf_DoesNotDeleteOutsideCr` が固定済み)。一律拒否はこの 2 件を
赤にし、PR #56 の修正を打ち消す。**「安全側だから厳しくしておく」が既存の修正を潰す実例**であり、
[[rationale-not-just-conclusion]] の型どおり結論ではなく根拠を当てて初めて出た。

`ReplaceAll` にも同じ検査を同じ文言で入れる。`SearchController` は
「`ReplaceOne` と片方だけ通る非一貫を作らない」を既存の設計原則として持っている(`:293` / `:460`)。

**ただし `ReplaceAll` 側の拒否分岐に到達できるかは未確定**。スコープ端が
サロゲートペアの内側に来るには、再捕捉した断片が孤立 high サロゲートで終わる必要があり、
そのような断片は組めない可能性がある(策定時の机上検討では見つからなかった)。
**到達 fixture の探索を実装タスクの明示ステップに置き、見つからなければガードを入れない**
(網の張れない分岐を残さない)。「書けない」の宣言も検証対象である
= [[net-absence-claims-are-also-verifiable]]。

**この検査が入ることで、`ReplaceOne` のスコープ再捕捉の既知の穴も閉じる。**
現在 `:393` は `grown = (prev.Start, prev.End + repl.Length - span.Length)` と置き、
その根拠コメント(`:378-391`)自身が「ゼロ幅では始端が後退するのでこの式は成り立たない」と
書いている。検査を通ったヒットは `S >= scope.Start` が保証されるので、式はそのまま正しくなる。
**コメントを「既知の穴」から「検査で保証された前提」へ書き換える**(記述だけを直して
根拠を確かめないのは [[rationale-not-just-conclusion]] の失敗型)。

### 2.2 一括置換を単発に揃える(1.2)

`TextSearcher.ReplaceInRange` が**ゼロ幅マッチの挿入点を論理文字の境界まで後退させる**
(`ReplaceCharRangeExact` と同じ規則)。非ゼロ幅は現状どおり触らない。

```csharp
int at = m.Length == 0 ? TextBoundary.SnapToLogicalCharStart(text, m.Index) : m.Index;
if (at < pos)          // 範囲始端より前 / 出力済み位置より前へは書けない
{
    scan = m.Index + 1;  // 件数にも数えない
    continue;
}
sb.Append(text, pos, at - pos);
sb.Append(Expand(m, replacement));
pos = at + m.Length;
count++;
```

`at < pos` の 1 本で 2 つの不正を弾く。`pos` は `s`(範囲始端)で初期化されるので
**範囲外への書込**を弾き、走査が進んだ後は**既に出力した位置より前への挿入**を弾く
(`\r|(?<=\r)` のように「`\r` を消費したあと同じ位置にゼロ幅が立つ」パターンで起きる)。
スキップしたマッチを `count` に数えないので `"N 件置換しました"` は嘘にならない。

**後退は元テキスト上で判定する**。単発置換も編集前スナップショットに対して
`SnapToLogicalCharStart` を掛けるので、規則の適用面が一致する。

これで §1.2 の表の 2 行はどちらも単発と同じ結果になる。非ゼロ幅のサロゲート割りは
両者とも U+FFFD のまま=**元から一致している**ので触らない(PR #56 §9.1)。

### 2.3 `ReadOnly` 早期 return(1.3)

`ReplaceOne` / `ReplaceAll` の `IsCsvModeActive` チェックの直後に `if (ed.ReadOnly) return;`
を置く。**発声はしない**。App には「読み取り専用」を告げる既存文言が無く、到達する経路も
無いため、新文言を足しても L5 で確認できる操作が作れない。

### 2.4 スコープ内で再アンカーして走査する(1.4)

`TextSearcher.ReplaceInRange` の走査を `_regex.Matches(text)` + `m.Index < s` の切り捨てから、
`_regex.Match(text, scan)` の**再アンカー**へ変える(`FindNext` と同じ形)。

```csharp
int scan = s;
while (scan <= end)
{
    var m = _regex.Match(text, scan);
    if (!m.Success || m.Index + m.Length > end)
        break;
    // …2.2 の後退処理…
    scan = m.Index + Math.Max(1, m.Length);
}
```

**`Match(text, startat)` は入力を切らない**。`\b`・先読み・後読みは**全文文脈のまま**評価され、
`startat` は走査開始位置だけを動かす。substring を切って照合する案は `\b` の意味が変わる
(`"aaa"` の `[1,3)` を `\baa` で照合すると、substring 化した場合だけ 1 件になる)。
この違いは**弁別できる fixture がある**ので網で固定する(§4)。

**全文置換(`s == 0`)では挙動不変**。`Matches` の非重複・左端優先の歩進規則
(ゼロ幅は +1、非ゼロ幅は +Length)を上のループがそのまま再現しているため。
これは主張であって自明ではないので、対照テストで固定する。

`RegexPerLineSearchStrategy` は `_inner` へ委譲するので**自動的に追随する**(ただし行内 substring
を渡す設計なので、M-29 は行単位でしか直らない=元からの 壊れる契約)。
`LiteralWindowSearchStrategy` は既に `FindNext` 起点で M-29 を持たず、リテラルパターンは
ゼロ幅になりえない(空パターンは `TextSearcher` の ctor が弾く)ので**変更なし**。

### 2.5 `TextBoundary` に span 版 `SnapToLogicalCharStart` を足す(2.2 の前提)

`TextSearcher` は `string` を扱い `TextSnapshot` を持たない。規則を `TextSearcher` に
インライン展開すると、`TextBoundary` の class doc が明示的に禁じている
「述語を登録簿から外す」ことになる(規則が変わったときテストは赤くならない)。

したがって `TextBoundary` に `ReadOnlySpan<char>` 版を足す。既存の span 版
(`CodePointLengthAt` / `SnapToCodePointStart`)と同じ形で、述語も span 版を対にして置く。

```csharp
public static int SnapToLogicalCharStart(ReadOnlySpan<char> text, int pos)
private static bool IsSurrogatePairEndingAt(ReadOnlySpan<char> text, int pos, char charAtPos)
private static bool IsCrlfEndingAt(ReadOnlySpan<char> text, int pos, char charAtPos)
```

**class doc の書き換えが必須**。現在の class doc は
「`ReadOnlySpan<char>` を受ける版は Layout / 描画が扱う**行内テキスト(改行を含まない=
CRLF 概念が不要)**を対象とする」と宣言している。この文はこの追加で偽になるので、
span 版の適用範囲を「行内テキスト」から「`TextSnapshot` を持たない呼び出し側」へ広げる。
**doc を直さずに API だけ足すと、次に読む人が誤った不変条件を信じる。**

snapshot 版と span 版が**同値**であること(同じ本文・同じ pos で同じ答え)は全数で固定する。

## 3. 触らないもの

- **`Count` / `Locate` / `FindPrev` のスコープ非対応。** 「12 件」と出たまま置換はスコープ内で
  止まる非一貫は残る。傘設計書 §5 が「検索セッション全体をスコープに閉じるか」の仕様判断を
  伴うとして v0.2 対象外と決めている。
- **起点クランプが前方のみ**(PR #56 §9.7)。同じく仕様判断を伴う。
- **非ゼロ幅サロゲート割りの U+FFFD 化**(PR #56 §9.1)。保存層の制約で、単発・一括とも同結果。
- **`ReplaceOne` の最悪ケース regex 時間 2 倍**(PR #56 §9.10)。傘設計書 §5 で対象外。
- **`ReplaceCharRangeExact` の巻き込み契約そのもの**(PR #56 §6)。厳密側への一本化は別件。
- **`LiteralWindowSearchStrategy`**。§2.4 の理由により変更なし。

## 4. テスト

### L1 — `tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs`

- span 版 `SnapToLogicalCharStart` の**全数**: アルファベット `{a, CR, LF, high, low}` の長さ 4 以下の
  全文字列 × `pos ∈ [-2, len+2]`。出力が常に論理文字境界・`[0, Length]` 内・単調・throw なし。
- **snapshot 版との同値**を同じ全数で照合(規則が 2 実装に分かれたことによる drift の網)。

### L1 — `tests/kxEdit.Core.Tests/Search/TextSearcherTests.cs`

| # | 固定する挙動 | fixture |
|---|-------------|---------|
| 1 | M-29: スコープ内で再照合する | `"aaa"` `[1,3)` / `aa` → 1 件・断片 `"X"` |
| 2 | 再アンカーは全文文脈を切らない | `"aaa"` `[1,3)` / `\baa` → **0 件**(substring 化なら 1 件) |
| 3 | 全文置換は `Matches` 版と同一 | 文書 × パターンの対照表(ゼロ幅・重なり・末尾ヒットを含む) |
| 4 | ゼロ幅は CRLF を割らない | `"a\r\nb"` / `(?<=\r)` → `"aX\r\nb"` |
| 5 | ゼロ幅はサロゲートペアを割らない | `"a😀b"` / `(?<=\uD83D)` → `"aX😀b"`(U+FFFD ゼロ) |
| 6 | 範囲始端より前へ後退するゼロ幅はスキップし**件数に数えない** | 端が CRLF 内側の範囲 |
| 7 | 出力済み位置より前へ後退するゼロ幅はスキップ | `"a\r\nb"` / `\r|(?<=\r)` |
| 8 | 範囲またぎ・範囲外は従来どおり対象外 | 既存テストの維持 |

**#2 と #6 は「非既定位置から始める」**(CLAUDE.md §4-B)。#2 は substring 化案と結果が割れる
唯一の形、#6 は範囲始端が論理文字の内側でなければ発火しない。

### L1 — `SnapshotSearcherTests` / `MaterializedSearchStrategyTests`

材質化経路の委譲で同じ結果になること。`RegexPerLineSearchStrategy` が M-29 修正を
**行内でだけ**引き継ぐこと(閾値注入で経路を選ぶ既存の流儀に従う)。

### L2 — `tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs`

- **全数プローブ**(PR #56 §9.8 と同じ形。文書 8 種 × `start` / `length` の境界値 ×
  置換文字列 4 種)で、`GetExactChangeRange` が返す範囲の**外側の本文が、実際に
  `ReplaceCharRangeExact` を呼んだ後も 1 文字も変わっていない**ことを実測で照合する。
  **これが本 seam の契約そのもの**であり、`ReplaceCharRange` へ渡す範囲との一致ではない。
- 3 形の弁別を個別に固定する(全数だけだと規則の取り違えが埋もれる):
  CRLF を割る非ゼロ幅 → `[s0,e0)` を返す / サロゲートを割る非ゼロ幅 → 広げた `[s,e)` を返す /
  ゼロ幅 → 後退した `[at,at)` を返す。
- `ReplaceCharRangeExact` の**挙動不変**: 括り出し前の既存テストが 1 件も落ちないこと。
- `ReadOnly` / `_buffer is null` のとき `GetExactChangeRange` が空範囲を返すこと。

### L3 — `tests/kxEdit.App.Tests/SearchControllerTests.cs`

| # | 固定する挙動 |
|---|-------------|
| 1 | §1.1 の再現手順で**本文が 1 文字も変わらない**・新文言を発声する |
| 2 | 同じ手順でスコープが更新されない(次の操作が拒否されない) |
| 3 | 端が境界に乗る通常のスコープでは従来どおり置換できる(偽陽性の網) |
| 4 | **スコープ端が CRLF の内側でも置換できる**(既存 `:1063` / `:1093` の 2 件が green のまま) |
| 5 | `ReplaceAll` でも同じ検査・同じ文言 |
| 6 | `ReadOnly=true` で `ReplaceOne` が本文・スコープ・発声のいずれも動かさない |
| 7 | M-29: 「選択範囲のみ」の `ReplaceAll` が `"aaa"` `[1,3)` `aa` を 1 件置換する |

**#3 は partial-selection の fixture 要件**(CLAUDE.md §4-B)を満たすこと=スコープの前後に
除外されるべき prefix / suffix を置き、全選択と区別できるようにする。

**#4 は新規テストを書かない**。既存 2 件が落ちないことが要件であり、同じ主張の重複を足さない
(落ちたら §2.1 の判定規則が壊れたということ)。

### ミューテーション検証

CLAUDE.md §4-A の**有効域**(置換エンジンのコアロジック)に該当する。スポットチェック:

- `TextSearcher.ReplaceInRange`: `at < pos` → `at <= pos` / 条件削除、`Math.Max(1, m.Length)` → `m.Length`
- `TextBoundary.SnapToLogicalCharStart(span)`: `pos - 1` → `pos`、述語の `||` を片側ずつ削除
- `ExactRangeParts`: `SnapToLogicalCharStart` ↔ `SnapToLogicalCharEnd` の入替え
- `SearchController` の包含判定: `w.Start < sc.Start` / `w.End > sc.End` を**1 行ずつ**変異させる
  (OR ガードは条件ごとに変異させる= [[backup-savepoint-sync]] の教訓)

[[mutation-harness-exit-code-trap]] のとおり、ビルド失敗の判定は
`grep -E " error [A-Z]+[0-9]+"` を使う(`grep "error CS"` は Sonar の `error S###` を見落とす)。
**対の関数を片方へ退化させる変異は S4144 で必ず落ちる**ので、`SnapToLogicalCharStart` ↔
`SnapToLogicalCharEnd` の入替えはこの罠に当たらない形(引数の入替えでなく式の書換え)で行う。

## 5. L5 判定

**必要**(傘設計書 §4.2 の判定どおり)。

- 新規発声文言が 1 種増える(「選択範囲の外に及ぶため置換できません」)。
- M-29 修正で「選択範囲のみ」の一括置換の**件数が変わる**=発声内容が変わる。
- 拒否時は書き込まないので UIA の選択変更は飛ばない。**飛ばないことが正しい**ことを実機で確認する。

実機確認項目は実装後に `2026-09-01-replace-write-range-postcondition-l5-checklist.md` へ起こし、
傘設計書 §7 の統合台帳へ載せる。

## 6. 申し送り

- **サロゲート割りの拒否は保守的**。PR #56 §9.1 の例外(`replacement` が対の相手で
  始まる / 終わり、復元される半身と繋がって正しいペアになる場合)では実際には壊れないが、
  本設計は判定せず拒否する。救えるのはこの 1 形だけで、規則を 1 つ増やすに見合わない。
- **`LiteralWindowSearchStrategy` はゼロ幅後退を持たない。** リテラルパターンがゼロ幅に
  なりえないことに依存している。将来リテラル経路が空パターンを受けるようになったら破れる。
- **`RegexPerLineSearchStrategy` の M-29 は行単位まで。** 行を跨ぐスコープの先頭行で、
  行頭より前から始まるヒットは依然として拾えない。閾値超(>32M chars)+ 正規表現でのみ到達。
- **`Count` / `Locate` / `FindPrev` は依然スコープ非対応**(§3)。件数表示と実際の到達範囲の
  食い違いは残る。傘設計書 §5 の同項目と併せて次リリースで判断する。

## 7. 工程

CLAUDE.md §3 の簡略化基準には**該当しない**(Core / Editor / App の 3 プロジェクトに跨り、
新 API を 2 本足す)。通常工程で進める。

1. **Task 1**: `TextBoundary` の span 版 `SnapToLogicalCharStart` + 述語 + class doc 更新 + L1 全数
   — 後続タスクが依存する共通規則の追加なので、CLAUDE.md §3-4 の前倒し**コード品質レビュー**を行う
2. **Task 2**: `TextSearcher.ReplaceInRange` の再アンカー化 + ゼロ幅後退 + L1(#1〜#8)
   — 外部入力(正規表現)のパース結果で書込範囲が決まる中核なので、前倒し**脆弱性レビュー**を行う
   (傘設計書 §8 が B2 を V-7 の教訓の直接該当と指定している)
3. **Task 3**: `ExactRangeParts` 括り出し + `GetExactChangeRange` + L2
   — 新 seam のため前倒し**コード品質レビュー**を行う
4. **Task 4**: `SearchController` の包含検査・`ReadOnly` ガード・再捕捉コメントの訂正 + L3
5. **Task 5**: L5 チェックリスト起こし
6. 最終ブランチレビュー 2 パス(コード品質 / 脆弱性)を**別エージェントで独立に**起動
7. 品質ゲート `tools/pre-merge-check.ps1` EXIT 0 → PR

---

## 8. 実装時の追記(実施記録)

本節は**実施記録**(CLAUDE.md §8 が認める追記)。§1〜§7 の策定内容は書き換えていない。

タスクごとに **実装 → 仕様レビュー → (前倒し指定があれば)コード品質 / 脆弱性レビュー** を
別エージェントで回した。以下は**計画が実測で反証された点**と**次回に効く罠**の記録である。

### 8.1 計画の期待値・判定則が反証された点

計画は冒頭で「計画に書いたコードと期待値は検証すべき仮説であって正解ではない」と宣言し、
挙動不変の主張は変更前 src で green を、バグ修正は変更前 src で red を先に確認する手順を
必須ステップに置いた。**その手順が実際に 6 件の誤りを捕まえた。**

| # | 計画の記述 | 実測 | 出所 |
|---|-----------|------|------|
| 1 | span 版の doc「同値テストがあるので**片方だけ直すと必ず赤くなる**」 | **偽**。2 本の網は射程が違い相補的(下表 8.2) | Task 1 仕様レビュー |
| 2 | Task 2 の対照群 `("ab", "b*", true, "-", "-a-b-", 3)` | 断片は **`"-a--"`**。index 1 の実マッチが `'b'` を消費する | Task 2 Step 1 |
| 3 | Task 2 Step 2「**6 件すべてが赤になること**。赤にならない fixture は網として無効」 | **判定則が誤り**。5 件のみ赤。`_ReAnchorDoesNotCutInputContext` は**差分ガード**であり before / after 両方 green が正しい姿(下記 8.3) | Task 2 実装 → 仕様レビューが支持 |
| 4 | Task 2 の変異 #5(`Match(text, scan)` → `Match(text, s)`)は「赤」 | **ハング**。`s` 固定だと同じマッチを無限に返し、スキップ経路の `scan = m.Index + 1` が毎回同値になる | Task 2 Step 6 |
| 5 | 対照群のコメント「全文置換(`s == 0`)は旧実装と**1 文字も変わらない**」 | **射程の誤り**。示せたのは*再アンカー単独*の不変性で、ゼロ幅後退と合成した全文置換は変わる(実測 960 行中 70 行 = 7.3%)。**反例は同じファイルの 60 行下**(`_ZeroWidthInsideCrlf_RetreatsToBoundary` は `s == 0`) | Task 2 仕様レビュー |
| 6 | 対照テストのコメント「substring 化を弁別する**唯一の形**」 | **偽**。`(?<=a)aa` が逆向きに同じ 2 実装を弁別する。ただし**現に存在する網としては 1 本だけ**は事実(全 `ReplaceInRange` テストを両実装で突き合わせて確認済み) | Task 2 仕様レビュー |

**#1 / #5 / #6 はいずれも「同じ事実を実態より強く書く」型**であり、本リポジトリで反復している
失敗である。#3 は計画が与えた**判定則そのもの**が誤っていた例で、実装エージェントの反論が正しかった。

計画の内部不整合も 1 件あった。冒頭と Task 2 Step 1 は逸脱を「設計書 §9 へ書き残す」と指示するが、
Task 6 Step 1 は「§8 を足す」と書いており、設計書に §9 は存在しない。**本節が §8 である。**

### 8.2 網の穴を 2 件掘り当てた(どちらも「網がある」という宣言のほうが誤っていた)

**(a) span 版と snapshot 版の同値テストは、孤立サロゲートに原理的に届かない。**

`TextBuffer` は本文を UTF-8 で保持するため、材質化後の本文に孤立 low サロゲートは 1 つも現れない
(実測 0 件)。したがって次の非対称が生じる。

| 変異 | 同値テスト | 事後条件テスト |
|------|-----------|---------------|
| `endsSurrogatePair` から `IsHighSurrogate(text[pos-1])` を落とす | **素通り**(不一致 0) | 検出(違反 22) |
| `endsCrlf` から `c == '\n'` を落とす | 検出(不一致 344) | **素通り**(違反 0。戻り先が CR になり「内側を指さない」を満たしてしまう) |

**2 本は相補的で、どちらか一方では穴が開く。** この事実を span 版の remarks へ実測値つきで書いた。

**(b) 事後条件テストが iff の半分しか主張していなかった。**

`char.IsLowSurrogate(c)` → `char.IsSurrogate(c)` の変異が **Core.Tests 1348 本を全通過**した。
事後条件が「戻ってよいときにしか戻らない」を主張していないため、**過剰スナップ系の変異が族ごと
素通り**していた(`SnapToLogicalCharStart("\uD83D\uD83D", 1)` が 1 でなく 0 を返す)。
`got != clamped` なら `mustMove` が真、という残り半分を足して塞いだ。

**この穴は、既存 span 族に `CodePointLengthAt_Span_HighSurrogateFollowedByNonLow_IsOne` という
high サロゲート隣接の名前付きテストが既にあり、新族だけがその対応物を欠いていたことが直接の原因**
だった。族を揃える、という単純な観点で防げた。

### 8.3 欠陥網と差分ガードは別物である(計画の判定則の誤り)

「変更前の src で赤にならないテストは、欠陥に到達していない=網として無効」は
**欠陥網(defect net)には正しいが、差分ガード(characterization guard)には誤り**である。

`_ReAnchorDoesNotCutInputContext`(`"aaa"` の `[1,3)` を `\baa` で 0 件)は、旧実装も
`Matches(text)` で入力を切らないため before / after 両方で green になる。このテストが殺すのは
**「M-29 を substring 化で直す」という誤った代替実装**であり、before で赤にならないのが正しい。

M-29 の「正しい substring 実装」(インデックスを `s` 分戻すもの)は `_ReMatchesInsideRange_...` を
通してしまうため、**substring 化を弁別する網は現状この 1 本だけ**である(全 `ReplaceInRange`
テストを両実装で突き合わせて確認。他はすべて `s == 0` か、両実装で同じ答えになる形)。

### 8.4 次回に効く罠(ツールチェーン)

既知の [[mutation-harness-exit-code-trap]] に加えて、本ブランチで新たに 4 件踏んだ。

1. **CSharpier(pre-commit フック)の整形がビルドを壊すことがある。** 波括弧なしの入れ子 `foreach`
   を同じ深さへ畳んだ結果 Sonar `S3973` に触れ、**commit された時点で `-warnaserror` が落ちる**
   状態になった。**「commit 前に green」は「commit 後に green」を意味しない。**
   commit の後に必ず再ビルドすること。
2. **ミューテーション変異が Sonar でビルド不能になる。** 両分岐が同値 → `S3923` /
   未使用ローカルが残る → `S1481` / オーバーロードが隣接していない → `S4136`。
   意味を保った別の形へ置き換えて実施する。
3. **`Copy-Item` はタイムスタンプを保持するため、変異を戻した src が出力 DLL より古くなり
   MSBuild の増分ビルドが再コンパイルをスキップする。**「BUILD OK」のまま古い DLL を叩く。
   `grep -E " error [A-Z]+[0-9]+"` でビルド成否を判定しても**この抜け道は検出できない**。
   変異戻しに `Copy-Item` を使うなら `LastWriteTime` の更新を併用すること。
4. **オーバーロードを足すと既存の裸 `cref` が曖昧になる。** ただし本リポジトリは
   `GenerateDocumentationFile` を設定していないため **CS0419 は通常ビルドでは出ない**
   (`-warnaserror` でも落ちない)。実装時の申告「未対応ならビルドが落ちる」は**偽**で、
   正しい根拠は「オーバーロード追加で参照先が一意でなくなったから」。
   なお本ブランチ以前から同種の CS0419 が 4 件存在する
   (`TextSnapshot.cs` / `TextFileService.cs` / `EditorControl.cs` / `UiaTextHostAdapter.cs`)。

### 8.5 Task 1 で計画から広げた範囲

- **`TextBoundary` に span 版述語 2 本を新設**し、`SnapToCodePointStart` と新 span 版の両方を
  述語呼びへ変えた(挙動不変)。計画には無い。理由は、span 版を足したことで
  サロゲート終端規則の手書きコピーが 2 → **3 箇所**へ増え、class doc が宣言する
  「規則を直す場所は境界述語と span 版だけ」という**登録簿の完全性が実際に一段弱まった**ため。
  class doc が挙げる例外の根拠(「`TextSnapshot` ではなく indexer で読むため共有できない」)は
  **snapshot ↔ span 間にしか当てはまらず、span ↔ span は共有可能**だった。
  宣言を弱めるのではなく、述語を括って宣言を真にする方向を採った。
  **残る例外は `CodePointLengthAt(ReadOnlySpan<char>, int)` の前進判定 1 箇所**で、
  class doc にそう明記してある(span 版で前進側を触るコードがもう 1 つ増えたら述語へ括ること)。
- **`CaretController.cs:84` の `cref` 1 行**(Files 外)。今回のオーバーロード追加が作った不整合。
- **全数生成器の被覆をピン留めする `[Fact]` を追加**。生成器を `len <= 1` へ退化させても
  48 本が全 green のままだったため(被覆が静かに失われても「全数で固定してある」と主張し続ける)。

### 8.6 Task 2 の脆弱性レビューが Critical を 1 件掘り当てた(無限ループ)

**再アンカー化した `ReplaceInRange` は、ユーザーが入力した正規表現で無限ループしていた。**
100% CPU の純粋スピンで、**例外もタイムアウトも起きない**完全な UI 凍結である。
変更前の実装は同じ入力で正常終了するので、**本ブランチが持ち込んだ退行**だった。

**根因は停止性証明の前提が偽だったこと。** 実装は
「`Match(text, scan)` は `m.Index >= scan` を保証するので `scan` は厳密増加する」を根拠にしていたが、
**.NET 9.0.8 でこれは成立しない**(3 者が独立に再現):

```
new Regex("(?:b(?!a)+?)*", CultureInvariant | IgnoreCase).Match("abbbb", 4)
  → Index=3 Length=2      // Index < startat
```

`m.Index < scan` になると `scan = m.Index + 1` も `scan = m.Index + Math.Max(1, m.Length)` も
`scan` を減らす / 据え置くため、`while (scan <= end)` が終わらない。
**個々の `Match` は 0ms で返るので `RegexMatchTimeoutException` は出ず**、例外が無いので
`CrashHandler` にも到達せず、メッセージポンプ駆動の自動バックアップも止まる
(強制終了 → 直前 tick 以降の未保存編集が失われる)。

到達は「本文 `abbbb` を開く → 正規表現 ON で `(?:b(?!a)+?)*` → すべて置換」の 3 手順。
**リテラルモードは影響なし**(`Regex.Escape` が量指定子もゼロ幅構成も作らない)。

**修正は事後条件に倒した(案 B)。**

```csharp
scan = Math.Max(scan + 1, m.Index + 1);                      // スキップ経路
scan = Math.Max(scan + 1, m.Index + Math.Max(1, m.Length));  // 出力経路
```

前置ガード案(`m.Index < scan` なら `break`)も同じ出力になることが実測されたが、
そちらは**「エンジンが異常を返す条件」の特徴づけに依存する**。レビュー担当自身が
「起きる条件の完全な特徴づけはできなかった」と報告しており、未知に賭ける形になる。
**案 B は `scan_new >= scan + 1` がエンジンの返り値と無関係に成立する**ので、反復回数
`≤ end - s + 1` が無条件に従う。**本ブランチが典拠に置く V-7 そのものの適用**である。

実測(実装者の全数コーパス。アルファベット `{a,b,c,\r,\n}` の長さ ≤4 全 781 文字列 ×
18 パターン × 全 `(s,e)` = 194,238 ケース):

| | 件数 |
|---|---|
| 修正前が停止しない | **192** |
| 案 B が停止しない | **0** |
| 修正前が停止したケースで案 B と出力が違う | **0**(非病的入力では `Math.Max` は恒等) |

### 8.7 この Critical から得た規範

1. **停止性は「呼び出し先の契約」ではなく「自分が書いた変数の事後条件」で示す。**
   今回の証明は**論理的には正しく、前提だけが偽**だった。`Math.Max` による強制はまさに
   前提を要らなくする形であり、同種のループを書くときの既定形にすべきである。
2. **「網羅的に見えるが実は狭い」パターン集合を根拠に安全を宣言しない。**
   脆弱性レビューは固定パターン 30 種 × 全 startat の 2,460 ケースで違反を **0 件**と報告し、
   **その「問題なし」が偽**だった。反例はランダム生成した正規表現を投げて初めて出た。
   [[net-absence-claims-are-also-verifiable]] の新しい形である。
3. **計画・レビューが与える「期待値」も仮説。** 修正指示に添えた 3 件の期待出力のうち
   **1 件が実測と食い違った**(`"abbbb"` 全文は案 B で `"XabXXbX"` count=4、
   変更前実装は `"XabXXXX"` count=5)。**病的入力で変更前と一致することは要求ではない** ——
   要求は (a) 停止すること (b) 断片が範囲の中身と置換だけであること。実装者が実測を正として
   期待値を書き換えた判断が正しい。
4. **PowerShell 5.1 の `[regex]` はこの種の検証に使えない**(.NET Framework 上で動くため)。
   `net9.0` のコンソールプロジェクトを scratchpad に立てて確かめること。
5. **ハングを固定するテストは `Task.Wait` / `Task.Result` で書けない**(xUnit アナライザ
   `xUnit1031` が禁止し `-warnaserror` でビルド不能)。`Task.WhenAny(t, Task.Delay(...))` +
   `ReferenceEquals` で書く。

### 8.8 脆弱性レビューが確認した「変更前から存在する問題」(本ブランチの退行ではない)

再監査の出発点として記録する。**いずれも本ブランチでは直さない。**

1. **`RegexPerLineSearchStrategy.ReplaceInRange` が範囲外の文字を断片に混ぜる**(`:211` 付近の
   `emit` 計算)。範囲始端が CRLF の内側にあると範囲より前の `\r` を断片へ出力する
   (`"a\r\n"` の `[2,2)` → 断片 `"\r"`)。**変更前と HEAD で違反 3,750 件が同数**なので無関係。
   閾値超(>32M chars)+ 正規表現 + スコープ端が CRLF 内側、が要る。
2. **置換文字列が `$` + Int32.MaxValue 超の数値を含むと `RegexParseException` が漏れる**
   (例 `$2147483648`)。`SearchController` は `RegexMatchTimeoutException` しか捕まえないため
   `CrashHandler` 経路。他の `$` 異常形(`${nope}` `${` `$-1` 等)は literal 扱いで安全。
3. **.NET の正規表現エンジン自体が、ゼロ幅構成への量指定子で `OverflowException` を投げたり
   20 秒以上ハングしたりする。** `Count` / `FindNext` / `Matches` すべてに等しく効き実装に依らない。
   `SearchController` は `OverflowException` を捕まえないので同じくクラッシュ経路。
4. **`start + length` の int オーバーフローは throw しないが範囲を黙って潰す**
   (`start=5, length=int.MaxValue` → `end=5`)。`EditorControl.ReplaceCharRangeExact` は
   `long` 経由で避けており**流儀が不揃い**。現状の呼び出し側では実害なし。
5. 非ゼロ幅マッチがサロゲートペアの片割れに当たると U+FFFD になる件は既知のトレードオフ。
   **本ブランチはこの件数を約 75% 削減しており(3,579 → 822)悪化はゼロ。**

### 8.9 脆弱性レビューが潰した範囲(再監査の省力化のため)

| 探針 | 規模 | 結果 |
|------|------|------|
| 算術安全性 | 10 文書 × 30 パターン × `start` / `length` の境界値(`int.MinValue` / `int.MaxValue` 含む)× 置換 4 種 = **174,240** | 変更前後とも throw 0 |
| 断片の事後条件(全数) | 長さ ≤4 の全文字列 1,555 本 × 25 パターン × 全 `(s,e)` × 3 置換 = **655,050** | `$&` 恒等違反 0 / 件数不一致 0 / 例外 0 |
| 変更前後の差分(全数) | 同コーパス × 2 置換 = **1,091,750** | 「新が孤立サロゲートを作り旧は作らない」**0 件**(逆は 2,192 件 = 改善)/ 非ゼロ幅パターンでの件数減 **0 件** |
| 性能・タイムアウト | 1M 文字の密ヒット / 2M 文字の末尾範囲 / 20 万ゼロ幅 / 破滅的バックトラッキング | 新は全ケースで旧以下(0.01〜1.00 倍)。末尾範囲は 243ms → 2ms。**1 秒 matchTimeout は旧も新もマッチ毎にリセット**= UI 凍結の上限は不変 |
| 窓渡し経路 | `SnapshotSearcher` 全体 **161,865** + 行窓 **10,742** | 行窓が論理文字を割る例 **0**(窓に LF が入らない)。閾値超の恒等違反は変更前と同数 |
| `RightToLeft` 到達性 | `(?r)` `(?-r)` `(?R)` ほか | すべて `IsValid=False` = ユーザーパターンからは有効化できない |
| ランダム fuzz + watchdog | 3,000 ケース・5 秒 watchdog | **HANG 6 件を検出** → §8.6 の Critical |
