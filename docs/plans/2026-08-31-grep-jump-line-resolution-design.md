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

#### 実施記録(2026-08-31)

**総括: 20 変異 / KILLED 16 / SURVIVED 4 / ビルド失敗による無効 0。**

変異は 1 つずつ投入し、都度 `git diff --quiet` の EXIT 0 で復帰を確認した。ビルド成否は
`dotnet build kxEdit.sln -c Release -warnaserror` の**終了コード**で判定している
(`grep "error CS"` は Sonar の `error S###` / Roslynator の `error RCS####` を見落として
古い DLL を叩く罠があるため使わない)。今回は 20 変異すべてがビルドを通り、
「対の関数を片方へ退化させると S4144 で落ちる」型の無効変異も発生しなかった。

| # | 変異 | 結果 | 失敗数 (Core / App) |
|---|------|------|------|
| 1 | `LineNumber - 1` → `LineNumber` | KILLED | 12 / 1 |
| 2 | `d <= NearbyLineWindow` → `d <` | KILLED | 1 / 0 |
| 3 | 近傍走査の up / down 探索順を入れ替え | KILLED | 1 / 0 |
| 4 | `LineText` 空ガードを削除 | KILLED | 1 / 0 |
| 5 | 空ガードを `Exact` 判定より前へ移動 | KILLED | 1 / 0 |
| 6 | `Stale` の `Length` を `0` → `MatchLength` | KILLED | 3 / 1 |
| 7 | `Land` の `+ MatchStartInLine` を削除 | KILLED | 7 / 2 |
| 8 | `LineEquals` の長さ篩いを無効化 | **SURVIVED(正)** | 0 / 0 |
| 9 | `Ordinal` → `OrdinalIgnoreCase` | KILLED | 1 / 0 |
| 10 | `Land` の着地行を 1 行上へずらす | KILLED | 13 / 2 |
| 11a | クランプ**上限**を撤去(`Math.Max` 化) | KILLED(例外) | 2 / 0 |
| 11b | クランプ**下限**を撤去(`Math.Min` 化) | SURVIVED | 0 / 0 |
| 16 | 早期終了条件 `&&` → `\|\|` | KILLED | 3 / 0 |
| 17 | 早期終了 `break` を削除 | **SURVIVED(正)** | 0 / 0 |
| 12 | `OpenAndSelect` を A-18 旧実装へ差し戻し | KILLED | 0 / 4 |
| 13 | 発声を `CurrentLine + 1` → `t.Line + 1` | SURVIVED | 0 / 0 |
| 14a | `Stale` 条件を反転 | KILLED | 0 / 2 |
| 14b | `Stale` 条件を常に真 | KILLED | 0 / 1 |
| 14c | `Stale` 条件を常に偽 | KILLED | 0 / 1 |
| 15 | `BringCaretIntoView()` を削除 | KILLED | 0 / 1 |

**この修正の中核が網で守られていること**: #12(`SelectCharRange(t.BufferOffset, t.Length)` を
`SelectCharRange(hit.AbsoluteOffset, hit.MatchLength)` へ戻す=A-18 そのものへの退行)は
`OpenAndSelect_*` **5 件中 4 件**で赤化する。選択レンジ・着地行・スクロール・発声文言の
4 面から独立に捕まるので、この退行が静かに戻る余地はない。

**生存 4 件の判断**(いずれも網を足さない):

- **#8(長さ篩いの無効化)= 等価変異。生存が正しい。** `length != text.Length` なら序数比較も
  必ず false になるので、篩いは純粋な最適化であって意味論ではない。ここが赤化するなら
  篩いが意味論に漏れているという**実装の欠陥**だったが、実測は緑=漏れていないことの証明。
  網を張ると最適化を意味論に格上げし、将来の性能改善を不当に縛る。
- **#17(早期 `break` の削除)= 等価変異。生存が正しい。** 両端に達したあとの反復は原理的に
  一致しえないので、これも純粋な終了最適化。#8 と同型。
- **#13(発声を `t.Line + 1` にする)= 構成不能。** `SelectCharRange` が位置を動かすのは
  (a) `[0, CharLength]` へのクランプ (b) サロゲートペア中間位置の前方スナップ の 2 つだけで、
  resolver の出力は行内に有界だから (a) は発火せず、(b) も同一行内で最大 1 文字動くだけ。
  つまり `t.Line != CurrentLine` を作る fixture が存在しない。書けるのは常に緑の無意味な網だけ。
  §3.2 の「着地後の `CurrentLine` から読み戻す」判断は、将来 `SelectCharRange` が変わったときの
  保険として維持する(いま検証面を持てるものではない)。
- **#11b(クランプ下限の撤去)= 到達不能 belt。** 唯一の生成元 `GrepService.CollectLineHits` は
  `lineNumber` を `++` してから emit するので `LineNumber >= 1` が常に成立する。
  網で固定することもできたが、`Land` の「到達不能な belt は書かない」判断と食い違うため、
  テストではなく `Resolve` の doc へ**上限は到達可能 / 下限は到達不能**という非対称を書いた。

**計画の表に無かった変異を追加した**: #10(着地行の取り違え)・#11b(クランプ下限)・
#16(早期終了 `&&` → `||`)・#17(早期終了の削除)・#14b / #14c(`Stale` 条件の常真 / 常偽)。
とくに #16 は典型的なタイポでありながら計画に無く、実測で 3 件が赤化した(片側だけ端に
達した時点で探索を打ち切ってしまい `Nearby` が `Stale` に化ける)。#10 は `origin` が `Land` の
スコープ外なので、意図(着地行の取り違え)を保ったまま「1 行上へのオフセット」として成立させた。

**App 側の網が Core より薄いのは責務分離として正しい形**: resolver 内部の変異
(#2〜#5・#9・#16)は App 側では 1 件も赤化しない。これは欠陥ではなく、純関数である Core が
厚い網を持ち、App 層は「resolver へ正しく配線されているか」(#12・#14・#15)だけを見る
という設計どおりの姿。App 側で resolver の内部仕様を二重に固定しにいかないこと。

**この検証から拾った申し送り 3 件**は commit `ef68e29` で回収済み(行末ゼロ幅ヒットの網追加・
belt の単一網である旨の明記・クランプ非対称の明記)。

### 5.4 L5 — 実機 SR 検証(必須)

発声文言に触れるため CLAUDE.md §5 により**必須**。別途 L5 チェックリストを起こす。

1. grep → 未保存編集のあるタブへジャンプ → NVDA が**正しい行**を読むこと
2. `Stale` ケースで「内容が変わっています」が読まれること
3. 同一ヒット再ジャンプ後にキャレット行が画面内に戻ること(弱視観点・目視)

### 5.5 品質ゲート

`tools/pre-merge-check.ps1` を EXIT 0 で通す(CLAUDE.md §6)。

### 5.6 L2 — `kxEdit.Editor.Tests` / `CaretScrollTests`(実装時に追記)

**追記位置について**: §5.1 = L1 / §5.2 = L3 は策定時スナップショット(CLAUDE.md §8)なので
番号を振り直さず末尾へ足す。策定時は L1 / L3 の 2 層構成のつもりだったが、
**実際の本ブランチは L1 + L2 + L3 の 3 層**である(Task 4 コード品質パス I-3)。

| # | 内容 |
|---|---|
| 1 | `SetSelectionCharRange_NoChange_DoesNotScroll`: `Anchor`/`Caret` 無変化なら早期 return し、`BringCaretIntoView` へ到達しない(=`TopLine` が動かない)こと |

Task 3 統合レビュー S-1 で追加した。役割は 2 つある。

1. **陽性対照** — L3 の `OpenAndSelect_SameHitTwice_ScrollsBackIntoView` が緑なのは
   §3.3 の belt(`OpenAndSelect` が明示的に呼ぶ `BringCaretIntoView`)のおかげであって
   setter が追従しているからではない、という因果を構造的に固定する。
2. **検知可能化** — 将来この早期 return が撤去されると、L3 側は belt を消しても緑のままになり
   (=静かに網でなくなる)、誰も気づけない。本テストがその変更を赤で知らせる。

setter 側の契約(Editor 層)と belt の効き(App 層)を**別の層で**固定するのが正しい分担。

## 6. 申し送り

- **判定窓の統一(M-16)**: エディタの先頭 64KB 判定と grep の全文判定が割れるケースは本設計では
  `Stale` に倒れるだけで解決しない。M-16 を再評価するときに合わせて扱う。
- **grep の入口をバッファ基準にする案**(§2.1)は機能追加として未着手。着手するなら
  「未保存の編集も grep で見つかる」がユーザー価値の主。
- **`Nearby` の窓 1000 行**は根拠のある実測値ではなく、UI スレッド上の走査を有界にするための
  設計値。実使用で「窓外 Stale」が頻発するようなら再検討する。

  **実施記録(2026-08-31・Task 1 コード品質レビュー)**: 上の「実測値ではない」に実測を与えた。

  計測条件: スクラッチパッドの Release コンソール(リポジトリ外)から `kxEdit.Core.dll` を
  直接参照し、`Resolve` を 5〜200 回回した平均。**アプリの実 UI スレッドではないので
  絶対値はそのまま鵜呑みにできないが、桁は信頼できる**。

  | fixture | Kind | 1 回あたり |
  |---|---|---|
  | Exact(20k / 200k 行) | Exact | 0.007〜0.024 ms |
  | Nearby d=500(200k 行 CRLF) | Nearby | 16.6 ms |
  | 20k 行 CRLF・同一長で不一致(窓フル走査) | Stale | 34.5 ms |
  | 20k 行 CRLF・長さ違いで不一致(前フィルタ有効) | Stale | 17.4 ms |
  | 200k 行 CRLF・同一長で不一致 | Stale | 36.3 ms |
  | 200k 行 CRLF・1 文字挿入 2,000 回で断片化 | Stale | 58.7 ms |
  | 6,000 行 × 200 桁・同一長で不一致 | Stale | 71.2 ms(最悪) |

  結論:
  - **`Exact` は無視できる**(実運用の大半はこれ)。
  - コストが出るのは `Stale` と遠い `Nearby` だけ。
  - 最悪 ~70 ms で、**ファイルサイズにはほぼ非依存**(固定 2,000 回の走査が支配する)。
  - `GrepResultsWindow.cs:33`(`DoubleClick`)と `:103-104`(`Keys.Enter when _list.Focused`)の
    とおり `OnActivate` は **Enter とダブルクリックでのみ**発火し、リスト選択の矢印移動では
    走らない。よって **1 ジャンプにつき 1 回・最悪 ~70 ms** で実用上の問題にならない。

  **独立再計測(実装担当・同条件)**: 上表を鵜呑みにせず別に測り直した結果、
  Exact 0.019 / 0.086 ms・Nearby d=500 26.6 ms・20k 同一長 27.2 ms・20k 長さ違い 15.4 ms・
  200k 同一長 33.2 ms・200k 断片化 65.4 ms と**同じ桁・同じ形**を再現した。
  ただし「6,000 行 × 200 桁」だけは 26.7 ms で 71.2 ms を再現しなかった(fixture の
  細部の差と思われる)。最悪値は再計測では断片化ケースの 65.4 ms。
  **いずれの測り方でも「最悪 ~70 ms・1 ジャンプ 1 回」という結論は変わらない**ため、
  窓 1000 行は据え置く。

- **`GrepJumpTarget.Offset` は実装時に `BufferOffset` へ改名した**(Task 1 コード品質レビュー
  I-3)。§3.2 の擬似コード中の `t.Offset` は `t.BufferOffset` と読み替えること。
  `hit.AbsoluteOffset`(ディスク空間)と並べたときに空間の違いが目に入るようにするための改名。

  **追記(Task 4 コード品質パス M-3)**: 同じ理由で **`GrepJumpTarget.Line` も `BufferLine` へ
  改名した**。§3.1 のシグネチャ中の `Line`・§3.2 の `t.Line` はいずれも `BufferLine` と
  読み替えること。`GrepHit.LineNumber`(**1 始まり・ディスク空間**)と
  `GrepJumpTarget.BufferLine`(**0 始まり・バッファ空間**)は基数も空間も違うのに名前が
  似ていた。`BufferOffset` と対称にすることで、§5.3 の変異 #1(`LineNumber - 1` →
  `LineNumber`)が誘発する off-by-one が名前の段階で読める。

- **CSV モードのタブへ grep ジャンプすると `CsvRow` / `CsvCol` がキャレットと desync する**
  (Task 3 統合レビュー S-3)。**本ブランチ以前からの挙動**で、A-18 の修正が作ったものではない。
  `OpenAndSelect` の `suppressAutoCsv: true` は**新規オープン時の自動 CSV 遷移を抑えるだけ**で、
  既に CSV モードになっているタブへ飛ぶ経路は塞いでいない。飛んだ先で `SelectCharRange` が
  キャレットを動かしても `CsvController` のセル状態は追従しないため、以後のセル移動が
  ずれた位置から始まる。回収するなら「CSV モードのタブへのジャンプはセル座標へ翻訳する」か
  「CSV モードを抜ける」かの設計判断が要る。

- **`GrepJumpResolver.Resolve` は Core の public API だが、`GrepHit` の producer 契約
  (`MatchStartInLine + MatchLength <= LineText.Length`)を型では強制していない**
  (Task 2 コード品質レビュー I-1・Task 3 統合レビュー M-3)。契約は `GrepTypes.cs` の
  remarks に書いて回収したが、破ると `Land` の `GetLineStart(line) + MatchStartInLine` が
  **int 同士の加算で溢れうる**(`SelectCharRange` の long ガードが見る前に負値が確定する)。
  現在の唯一の生成元 `GrepService` は構造的にこれを守るため production 到達不能。
  record の primary constructor に検証を足すのは Core のホットパスに条件分岐を入れることに
  なるため見送った。§2.1 の「grep の入口をバッファ基準にする案」に着手するときは、
  2 つ目の producer がこの契約を守ることを必ず確認すること。

  **追記(Task 4 脆弱性パス F-2)**: 同じ producer 契約に **`LineText` は非 null** を足した。
  `Resolve` の `ArgumentNullException.ThrowIfNull` は `hit` / `snap` にしか掛かっておらず、
  `LineEquals` が `text.Length` を無条件に読むため、破ると **UI スレッドの未処理例外(NRE)**
  になる(脆弱性パスが実測)。`GrepService` の `LineText` は必ず `text.Substring` 由来なので
  到達不能で、`ThrowIfNull` の追加はしていない(上と同じ理由・`Land` の
  「到達不能な belt は書かない」判断と整合)。

### 6.1 最終ブランチレビュー 2 パス由来の追記(2026-08-31)

コード品質パス / 脆弱性パスとも **「マージ可」・Critical / blocking ゼロ**。指摘の大半は
doc の「強さと置き場所」であり、src の挙動を変えたのは `BufferLine` への改名 1 件のみ。
以下は既存記述を書き換えずに足す**訂正記録・精密化・申し送り**である(CLAUDE.md §8)。

- **§1 の一致条件を精密化する**(コード品質パス I-2)。§1 の 2 箇所が食い違っている。
  - §1 `:21`「2 つの空間が一致するのは**「タブを新規に開き、かつ両者の復号結果が同一」の
    ときだけ**である」
  - §1 `:43-45`「『同一空間に揃う』は**バッファが未編集かつ判定が一致する**場合に限った
    条件付きの主張」

  **正しいのは後者**。`:21` は狭すぎる。「タブを新規に開き」は**必要条件ではなく**、
  既に開いているタブでもバッファが未編集なら揃う。さらに**ヒットより後ろだけを編集した
  場合も揃う**(ヒット位置より前の増減がゼロなのでオフセットが動かない)。
  正確な一致条件は「**ヒット位置より前のバッファ内容が、ディスクの復号結果と同一**」であって
  「新規に開いたか」ではない。ただし §1.2 の主旨(**揃うことを前提にできない**)は変わらない。
  §1 は策定時スナップショットなので書き換えず、ここに記録する。

- **折り返し ON のスクロール退避に自動テストが無い**(Task 3 レビュー S-2 の申し送り・
  コード品質パス I-3 で設計書へ移設)。`_topSegment != 0` を残す唯一の経路が折り返し ON での
  退避だが、L2 の `SetSelectionCharRange_NoChange_DoesNotScroll`(§5.6)も L3 の
  `OpenAndSelect_SameHitTwice_ScrollsBackIntoView` も**折り返し OFF** で書かれている。
  現状は L5 チェックリスト項目 3 の「折り返し ON」行が人手で担保しているだけ。
  **将来タスクとして回収する**(L5 チェックリストは §5 の表に PASS を書いたら役目を終える
  一過性の文書なので、そこにしか無い記録は消える=CLAUDE.md §8)。

- **上の性能実測(「実施記録」と「独立再計測」)は、結論は正しいが根拠 2 点が偽だった**
  (脆弱性パス F-1)。**「結論ではなく根拠を検証する」に該当**するので訂正記録として残す。

  | 既存の記述 | 実測による訂正 |
  |---|---|
  | 「**`Exact` は無視できる**」(0.007〜0.024 ms) | **幅の狭い行でしか成り立たない**。`LineEquals` は比較のためだけに `GetText` で行全体を実体化するので、ミニファイ JSON / 1 行ログのような **32M 文字 1 行では `Exact` でも 34 ms・183 MB** |
  | 「最悪 ~70 ms で、**ファイルサイズにはほぼ非依存**(固定 2,000 回の走査が支配する)」 | **偽**。2,000 回が有界化しているのは**木降下だけ**。`GetText` の実体化は窓内で長さ一致した各行の**幅の総和**(≒文書サイズ・grep の 512MB 上限まで)に比例する |

  **追加実測**(スクラッチパッドの Release コンソール・`kxEdit.Core.dll` 直参照・上と同方式):

  | fixture | Kind | ms/call | alloc/call |
  |---|---|---|---|
  | 上の baseline: 20k 行 × 20 桁 CRLF | Stale | 36 | 0.5 MB |
  | 2,600 行 × 2,000 桁 | Stale | 32 | 23 MB |
  | 2,600 行 × 25,000 桁(≈64MB = grep の上限) | Stale | 56 | 286 MB |
  | 2,600 行 × 40,000 桁 | Stale | 72 | 458 MB |
  | 64 行 × 1,000,000 桁(64MB ファイル) | Stale | 100(連続)/ 51(単発 cold) | 366 MB |
  | **1 行 × 32,000,000 桁** | **Exact** | **34** | **183 MB** |
  | 64MB 文書・長さ違い(前フィルタ有効) | Stale | 20 | 0 MB |

  1 行あたりのアロケーションは fixture により **0.5 MB 〜 458 MB** と約 900 倍振れる
  (42,500 文字以上の行は LOH 行き)。

  **結論は維持される**: 1 アクティベートにつき **1 回**・単発 51〜100 ms・一過性 366 MB で、
  **ハングではない**。`doc.FocusTarget.Focus()` がリストからフォーカスを外すので、Enter の
  オートリピートで連射されることもない(実測確認済み)。保持もループもなし。
  **よって窓 1000 行は据え置く。**

  **到達可能性**: 攻撃者に限らず**通常ユーザーも到達可能**。ただし新規に開いた直後は `Exact` で
  即決着するため、高コスト経路に入るには「バッファが grep 時と**内容は違うが長さは同じ**」が
  要る(開いているタブで同長の全置換をしてから古いヒットへ飛ぶ / §1.1 経路 2 の 64KB 判定割れ)。

  **申し送り**: 窓を広げる・近傍走査を強化するなら、**実体化しない比較 seam**
  (`GetText` を経ずに行内容を突き合わせる)が必要になる。

- **サロゲートで終わる選択が 1 単位縮退する**(脆弱性パス Info)。
  `EditorControl.SetSelectionCharRange` は**始端にも終端にも** `SnapToLogicalCharStart`
  (`SnapAndClamp`)を掛けるため、`a.`(`.` が UTF-16 1 単位にマッチ)のような正規表現の
  ヒットが絵文字の high サロゲートで終わると、選択が `[6,8)` ではなく `[6,7)` に**縮んで
  その絵文字が選択から落ちる**。`TextBoundary.SnapToLogicalCharEnd` の remarks が
  まさにこの縮退(始端 Start・終端 End を掛けるべき理由)を警告している。
  **本ブランチの変更ではない**——旧 `AbsoluteOffset` 経路でも同じで、
  **行番号の発声は正しいままなので A-18 の再発ではない**。回収するなら
  「終端は `SnapToLogicalCharEnd` を使う」という `SetSelectionCharRange` 側の設計判断になり、
  grep ジャンプに閉じないので別テーマとして扱う。
