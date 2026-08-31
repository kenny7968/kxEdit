# grep ジャンプの行ベース再解決 設計書(A-18)

- 日付: 2026-08-31
- 出自: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §4 の **A-18**
- 対象: `src/kxEdit.Core/Search/GrepJumpResolver.cs`(新規) / `src/kxEdit.Core/Search/GrepTypes.cs` /
  `src/kxEdit.App/MainForm.cs` / `src/kxEdit.Editor/EditorControl.Caret.cs`

本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録のみ追記する。

## 1. 問題

grep のヒットは**ディスクのバイト列を復号した文字列**の上で算出される。エディタが選択するのは
**タブが保持するバッファ**の上の位置である。`MainForm.OpenAndSelect`(`MainForm.cs:1066`)は
この 2 つを同一空間とみなし、`GrepHit.AbsoluteOffset` をそのまま `SelectCharRange` に渡している。

```csharp
// MainForm.cs:195(呼び出し側)
OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength)
```

2 つの空間が一致するのは「タブを新規に開き、かつ両者の復号結果が同一」のときだけである。
ずれた場合、`OpenAndSelect` は着地点から `CurrentLine + 1` を算出して発声するため、
**別の行を、正しい行であるかのように読み上げる**。SR ユーザーには誤りが検出できない。

### 1.1 空間が食い違う経路(3 つ)

| # | 経路 | 機構 |
|---|---|---|
| 1 | **未保存編集のあるタブ** | `TryOpenOrActivate` は既存タブを再読込しない(二重編集の上書き事故防止・Q4)。ヒット位置より前の編集の増減分だけ丸ごとずれる |
| 2 | **文字コード判定窓の違い** | エディタは先頭 64KB を prefix にして判定(`TextFileService.cs:156` `LoadAsBufferAuto`)、grep は**全バイト**を渡す(`GrepService.cs:99`)。判定が割れると復号結果そのものが別物になる |
| 3 | **grep 実行後のディスク側外部変更** | 未オープンのファイルは新しい内容で開かれるが、ヒットは古い内容基準 |

いずれも「ディスク基準オフセットをバッファ空間に持ち込む」という同一の欠陥の現れである。
経路 1 が最も踏みやすい(grep → 結果一覧を開いたまま編集 → 別のヒットへジャンプ)。

### 1.2 doc コメントが偽の不変条件を宣言している

`MainForm.cs:1063-1064` は次のように書いている。

> offset は grep が算出した UTF-16 文字位置で、同じ復号経路(TextFileService)を通るため
> エディタのスナップショットと同一空間に揃う。

「同じ復号経路を通る」は事実だが、「同一空間に揃う」は**バッファが未編集かつ判定が一致する場合に
限った条件付きの主張**であり、無条件の不変条件として書かれている。A-18 はこの誤った前提の帰結で
あり、コメントの訂正も修正の一部とする。

## 2. 方針

**`AbsoluteOffset` をジャンプに使うのをやめ、`GrepHit` が既に持っている
`LineNumber` / `LineText` / `MatchStartInLine` を live バッファへ照合して着地点を決める。**

行番号と行内容という「内容に基づく錨」に切り替えることで、§1.1 の 3 経路を**由来を区別せず一括で**
扱える。dirty フラグを見る分岐も、判定窓を揃える工事も要らない。

行区切りの規約が grep と一致していることは確認済み:

| | 規約 |
|---|---|
| grep(`GrepService.CollectLineHits`) | `\r\n` / `\n` / 単独 `\r` で分割・末尾改行は空の最終行を作らない |
| エディタ(`TextChunk` の `Breaks`) | 「LF 数 + LF が直後に続かない CR 数」 |

同一テキストに対して両者の行番号は一致する。

### 2.1 採らなかった案

- **入口で編集中バッファを grep する**(開いているタブはディスクでなくバッファを検索)。
  「未保存の編集も grep で見つかる」という機能改善が付くが、Core の `GrepService` に seam を足し、
  UI スレッド所有のバッファをバックグラウンド走査する問題を抱える。さらに **grep 実行後の編集には
  無力**なので結局出口の再解決が要る。機能追加として別テーマに切り離す。
- **陳腐化を検知してジャンプを拒否**(dirty なら「保存してから」と警告)。嘘の発声は消えるが、
  SR ユーザーが最もよく使う導線が実質死ぬ。経路 2 も残る。

## 3. 設計

### 3.1 `GrepJumpResolver`(Core・純関数)

`src/kxEdit.Core/Search/GrepJumpResolver.cs` を新設する。

```csharp
public enum GrepJumpKind { Exact, Nearby, Stale }

public sealed record GrepJumpTarget(GrepJumpKind Kind, int Line, int Offset, int Length);

public static class GrepJumpResolver
{
    internal const int NearbyLineWindow = 1000;

    public static GrepJumpTarget Resolve(GrepHit hit, TextSnapshot snap);
}
```

解決手順:

1. `line = Clamp(hit.LineNumber - 1, 0, snap.LineCount - 1)`。
   その行のテキストが `hit.LineText` と**序数一致**するなら `Exact`。
   `Offset = snap.GetLineStart(line) + hit.MatchStartInLine`、`Length = hit.MatchLength`。
2. 不一致なら `line` を中心に **±`NearbyLineWindow` 行**を**近い順**(`line-1`, `line+1`,
   `line-2`, `line+2`, …)に走査し、`hit.LineText` と一致する行を探す。見つかれば `Nearby`
   (オフセットの算出は 1. と同じ)。近い順に見るので、同一内容の行が複数あるときは
   **元の行番号に最も近いもの**が選ばれる。
3. 見つからない場合、**または `hit.LineText` が空の場合**は `Stale`。
   `Offset = snap.GetLineStart(line)`、`Length = 0`(選択せずキャレットのみ)。

**`LineText` が空なら近傍走査しない**のは、照合材料がゼロで任意の空行に着地しうるためである。
黙って無関係な空行へ飛んで正常であるかのように発声するより、`Stale` として明示するほうが誠実。

**走査コストの抑制**: 各行で文字列を実体化する前に
`snap.GetLineEnd(i, includeBreak: false) - snap.GetLineStart(i)` と `hit.LineText.Length` を
比較し、長さが違う行は `GetText` を呼ばずに捨てる。

**`AbsoluteOffset` は `Resolve` の中で読まない。** 引数として `GrepHit` を受けるが参照しないことを
テストで固定する(§5)。将来「ディスク基準オフセットを選択に使う」実装が戻る道を、doc コメント
ではなく網で塞ぐ。

`GrepHit.AbsoluteOffset` フィールド自体は残す(ディスク基準の値としては正しく、`GrepService` の
テストが pin している)。ただし `GrepTypes.cs` の doc コメントの
「エディタの string index・`SelectCharRange` と同一空間」という記述は §1.2 と同種の偽の宣言なので
訂正する。

### 3.2 `MainForm.OpenAndSelect`

シグネチャを **`OpenAndSelect(GrepHit hit)`** に変更する。裸のオフセットを渡せる入口を無くし、
呼び出し側が誤った空間の値を渡すことを型で防ぐ(現行の呼び出しは `MainForm.cs:195` の 1 箇所と
`MainFormSmokeTests` のみ)。

```
doc = _file.TryOpenOrActivate(hit.FilePath, suppressAutoCsv: true)
if doc is null: return
t = GrepJumpResolver.Resolve(hit, doc.Editor.CurrentBuffer.Current)
doc.Editor.SelectCharRange(t.Offset, t.Length)
doc.Editor.BringCaretIntoView()        // §3.3
doc.FocusTarget.Focus()
Say(t.Kind == Stale
    ? $"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目 内容が変わっています"
    : $"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目")
```

発声の行番号は **`t.Line` ではなく着地後の `doc.Editor.CurrentLine` から読み戻す**(現行の流儀を
維持)。resolver の意図値を発声すると、`SelectCharRange` 側のクランプやスナップの不具合が
発声に現れなくなる。「発声文言は第 2 の観測面」という既存の教訓に従う。

`CurrentBuffer` は non-null 保証(`SetSource` 前も静的空 `TextBuffer`)なので、
`SearchController` と同じ `ed.CurrentBuffer.Current` の流儀で読む。

### 3.3 巻き込み: 同一ヒットへの再ジャンプでスクロールが追従しない

`SetSelectionCharRange` は `Anchor`/`Caret` が無変化なら早期 return する
(`EditorControl.Caret.cs:202`)。この早期 return 自体は UIA の高頻度な無変化 `Select()` を守るため
必要だが(A-3 で確認済み)、ジャンプ導線では次の順序で退行になる。

1. grep 結果一覧でヒット A をアクティベート → その行が見える
2. エディタでマウスホイールを回してスクロール退避(`TopLine` だけ動きキャレットは不動)
3. 同じヒット A をもう一度アクティベート → `Anchor`/`Caret` が一致 → **早期 return で
   `BringCaretIntoView` に到達せず、退避したままになる**

A-3 で `GoToLine` に明示 `BringCaretIntoView` を足したのと**同型**であり、そのとき
`SelectCharRange` 経路には入れていなかった取りこぼしである。「ジャンプは移動先を必ず見せる操作」と
いう A-3 の原則をこの導線にも適用し、`OpenAndSelect` 側で明示的に `BringCaretIntoView()` を呼ぶ。

CLAUDE.md §2 に従い、A-18 本体とは別症状の**意図的なスコープ追加**として本書に記録する。
弱視ユーザーに可視の退行であり(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)、修正対象の
メソッドそのものに同居しているため同時に直すのが妥当と判断した。

## 4. スコープ外(明示)

- **§1.1 経路 2(判定窓の違い)の統一は行わない。** 本設計では復号が割れたケースは `LineText` が
  一致せず `Stale` に倒れる。**嘘の発声は止まるが、正しく飛べるようにはならない。**
  先頭 64KB 判定は監査 §6 の **M-16 で受容済みのトレードオフ**であり、これを覆すかどうかは
  別途の判断を要する。
- **grep が未保存の編集内容を検索対象にすること**は §2.1 のとおり別テーマ。
- `GrepHit.AbsoluteOffset` の削除は行わない(§3.1)。
- `Nearby` で同一内容の行が複数ある場合に「元とは違う行」へ着地しうることは**受容する**。
  行番号最近傍を選ぶこと、および着地行を発声で必ず読み上げることで、ユーザーが検知できる。

## 5. 検証

### 5.1 L1 — `kxEdit.Core.Tests` / `GrepJumpResolverTests`

| # | 内容 |
|---|---|
| 1 | `Exact`: 該当行が一致 → `Offset == GetLineStart(line) + MatchStartInLine` |
| 2 | `Nearby` 上方向(grep 後に行が削られた)/ 下方向(行が挿入された)の両方 |
| 3 | 同一内容の行が複数 → **元の行番号に最も近い行**を選ぶ(ヒットを先頭行に置かない fixture) |
| 4 | 窓外(`NearbyLineWindow` を超えるずれ)→ `Stale` |
| 5 | `LineText` が空 → 近傍に空行があっても `Stale`(近傍走査しないことの網) |
| 6 | `LineNumber` が `LineCount` 超 → clamp して `Stale`・`Offset` は行頭 |
| 7 | CRLF / LF / 単独 CR 混在の fixture で grep の行番号と一致すること |
| 8 | **`AbsoluteOffset` に故意の異常値を入れても結果が不変**(不使用の網) |
| 9 | `Stale` の `Length == 0`(選択しない) |

fixture 設計は CLAUDE.md §4-B に従う。特に #3 は非既定位置(先頭行・末尾行以外)から検証し、
#5 は「近傍に空行が実在する」陽性対照を置いて、走査しないことが `Stale` の原因だと弁別する。

### 5.2 L3 — `kxEdit.App.Tests` / `MainFormSmokeTests`

| # | 内容 |
|---|---|
| 1 | 開いているタブの**先頭に行を挿入**(未保存)→ ジャンプ → 正しい行を選択し、**発声も正しい行番号** |
| 2 | 同上で `AbsoluteOffset` をそのまま使った場合に着地する行と**異なる**ことを陽性対照で示す |
| 3 | `Stale` → 発声に「内容が変わっています」が含まれ、選択長 0 |
| 4 | 同一ヒットへの再ジャンプでスクロールが追従する(§3.3) |
| 5 | 既存の `suppressAutoCsv` 配線が維持されること(シグネチャ変更に伴う移植) |

### 5.3 ミューテーション検証

`GrepJumpResolver` は CLAUDE.md §4-A の**有効領域**(「テキスト選択範囲の算出」)に該当するため、
スポットチェックを実施する。最低限、次の変異が落ちること:

- `LineNumber - 1` → `LineNumber`(off-by-one)
- 近傍走査の一致条件を反転 / 窓境界を `<=` ↔ `<`
- `Stale` の `Length` を `MatchLength` にする
- `LineText` 空ガードの除去

### 5.4 L5 — 実機 SR 検証(必須)

発声文言に触れるため CLAUDE.md §5 により**必須**。別途 L5 チェックリストを起こす。

1. grep → 未保存編集のあるタブへジャンプ → NVDA が**正しい行**を読むこと
2. `Stale` ケースで「内容が変わっています」が読まれること
3. 同一ヒット再ジャンプ後にキャレット行が画面内に戻ること(弱視観点・目視)

### 5.5 品質ゲート

`tools/pre-merge-check.ps1` を EXIT 0 で通す(CLAUDE.md §6)。

## 6. 申し送り

- **判定窓の統一(M-16)**: エディタの先頭 64KB 判定と grep の全文判定が割れるケースは本設計では
  `Stale` に倒れるだけで解決しない。M-16 を再評価するときに合わせて扱う。
- **grep の入口をバッファ基準にする案**(§2.1)は機能追加として未着手。着手するなら
  「未保存の編集も grep で見つかる」がユーザー価値の主。
- **`Nearby` の窓 1000 行**は根拠のある実測値ではなく、UI スレッド上の走査を有界にするための
  設計値。実使用で「窓外 Stale」が頻発するようなら再検討する。
