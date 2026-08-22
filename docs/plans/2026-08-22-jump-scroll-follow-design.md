# ジャンプ/選択の追従スクロールを回復する(A-3 / M-32 / A-12)設計書

作成日: 2026-08-22 / ブランチ: `feature/jump-scroll-follow` / 起点 main = `35d8eb9`

対象は [`docs/plans/2026-08-22-v0.2-release-bug-audit.md`](./2026-08-22-v0.2-release-bug-audit.md) の
**A-3**(優先度 1)・**A-12**(優先度 2)・**M-32**(将来対応記録)。監査書 §8-3
「A-3 + A-12 + M-32(ジャンプ/選択のスクロール追従)は Editor 1 箇所の修正で束ねられる」に従う。

本書は策定時スナップショット(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行う。

## 1. 問題

### 1.1 A-3 — 検索ジャンプ・Ctrl+G・grep ジャンプで画面がスクロールしない [実機]

308 行の説明書を開き Ctrl+G → 250 と入力すると、ステータスバーは「行 250, 桁 1」に変わるが
ビューポートは 1 行目のまま動かない。検索(F3 / 検索ダイアログ)と grep 結果からのジャンプも同様。

晴眼・弱視ユーザーが初日に気づく水準の退行(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
NVDA 稼働中は UIA `ScrollIntoView`(PR #29)経由で追従しうるため、L5 実機 SR 検証では露見しにくい。

**機構**: `EditorControl` のキャレット/選択 setter が追従スクロールを呼ばない。

```
SetCaretCharOffset      → _caretCtrl.SetTo → PositionCaret() → Invalidate()
SetSelectionCharRange   → _caretCtrl.SetSelection → PositionCaret() → Invalidate()
```

一方で **キーボード経路と編集経路は明示的に追従している**。

- `InputRouter.cs:149, 493, 508, 534` — 矢印/Home/End/マウスドラッグの直後に `BringCaretIntoView()`
- `EditorControl.AfterEdit()`(`EditorControl.cs:1162`)— 編集後に `BringCaretIntoView()`

つまり **ジャンプ系だけが契約から漏れている**。旧 `ScintillaHost.SelectCharRange` は
`SCI_SCROLLCARET` を発行しており、P6 の自作エディタ移行でこの契約が落ちた。

App 側で `EnsureVisibleCharRange` を明示的に呼んでいるのは `CsvController.cs:254, 363` だけで、
以下の 6 経路は呼んでいない。

| 呼び出し元 | API | 用途 |
|---|---|---|
| `SearchController.cs:232` | `SelectCharRange` | 検索ヒットへジャンプ |
| `SearchController.cs:308` | `SelectCharRange` | 置換して次を検索 |
| `MainForm.cs:958` | `GoToLine` | Ctrl+G 行へ移動 |
| `MainForm.cs:919` | `SelectCharRange` | grep 結果からのジャンプ |
| `KinsokuFormatController.cs:74` | `SelectCharRange(0, 0)` | 全文整形後に先頭へ |
| `KinsokuFormatController.cs:76` | `SelectCharRange(start, len)` | 部分整形後に変化箇所へ |

### 1.2 M-32 — UIA `Select()` がキャレットを可視域へスクロールしない

`UiaTextHostAdapter.cs:323-334` の `IUiaTextHost.SetSelection` は、`BeginInvoke` で UI スレッドへ
マーシャリングしたうえで `_host.SetSelectionCharRange(start, end)` を呼ぶ。したがって
**A-3 と同一の根**であり、setter を直せば同時に解消する。

SR が範囲を選択したのに画面が動かないと、晴眼者が SR の読み上げ位置を目で追えない。

### 1.3 A-12 — UIA `GetBoundingRectangles` が `_scrollX` を引かない

`UiaTextHostAdapter.ComputeBoundingRectangles`(`:613-618`)は `ComputeCaretPointForUia` の
戻り X をそのままスクリーン座標へ足しており、水平スクロール量を減算していない。

折り返し OFF で右へスクロールした状態では、NVDA のフォーカスハイライト/キャレット矩形が
実描画より `ScrollX` px だけ右にずれる。弱視ユーザーに可視の不具合。

**往復非対称**である点が根拠になる。同じ座標系を扱う他の 3 経路は全て減算している。

| 経路 | `_scrollX` の扱い |
|---|---|
| 描画 `EditorControl.Paint.cs:99` | 引いている |
| `PointFromCharOffset`(`Caret.cs`)| 引いている(`x - _scrollX`) |
| 逆変換 `OffsetFromClientPoint` | 引いている |
| `ComputeBoundingRectangles` | **引いていない** ← ここだけ |

## 2. 方針 — 契約の言い直し

Editor には絶対位置/相対位置の 4 つの setter があり、追従スクロールの責務が不揃いになっている。
これを次の規約で揃える。

> **キャレット/選択の絶対位置を外から指定する API は、キャレットを可視域に入れる。
> アンカー相対で動かす API は、呼び出し側がスクロールを判断する。**

| API | 追従を足すか | 根拠 |
|---|---|---|
| `SetCaretCharOffset` | **足す** | GoToLine・セッション復元・CSV モード復帰・矢印キーの無修飾分岐が通る |
| `SetSelectionCharRange` | **足す** | 検索 / grep / 整形のジャンプ・UIA `Select()`(= M-32)が通る |
| `SetSelectionAnchored` | 足さない | Ctrl+A(`SelectAll`)と マウスドラッグが通る。Ctrl+A の非スクロールは Task 6 レビュー I-1 で意図的に決定済み |
| `MoveCaretWithSelection` | 足さない | shift+移動。呼び出し側の `InputRouter` が直後に明示的に呼んでいる |

App 側の各ジャンプ地点に `EnsureVisibleCharRange` を足す案(案 B)と、App 互換エイリアス
(`SelectCharRange` / `GoToLine`)だけに足す案(案 C)も検討したが、次の理由で採らない。

- 案 B: M-32(UIA `Select()`)が直らず別途対応が要る。さらに **「呼び忘れると壊れる」構造がそのまま残る**
  — A-3 はまさにその構造で発生した退行である。
- 案 C: 旧 `ScintillaHost` 契約の復元としては筋が良いが、UIA `Select()` は
  `SetSelectionCharRange` を直接呼ぶため M-32 が直らない。

### 2.1 挿入位置と順序

```csharp
public void SetCaretCharOffset(int offset)
{
    if (IsComposing) CancelCompositionAndDefault();
    if (_buffer is null) return;
    int snapped = SnapAndClamp(offset);
    if (_caretCtrl.Caret == snapped && _caretCtrl.Anchor == snapped)
        return;                     // ← 早期 return は温存
    _caretCtrl.SetTo(snapped, _buffer.Current);
    PositionCaret();
    BringCaretIntoView();           // ← 追加
    Invalidate();
    if (RaiseUiaSelectionEvents) _uia.RaiseSelectionChanged();
    UpdateUI?.Invoke(this, EventArgs.Empty);
}
```

- **早期 return の後に置く**。UIA は無変化の `SetSelection` を高頻度で投げてくるため、
  前に置くと `BringCaretIntoView` の水平分岐が毎回 `ComputeCaretPoint` を走らせる。
  `ScrollCharRangeIntoView` が無変化呼び出しの早期 return を設けたのと同じ理由
  (`Caret.cs` の同メソッド remarks に経緯あり)。
- **`PositionCaret()` と `Invalidate()` の間に置く**。`AfterEdit()` の
  「`PositionCaret` → `BringCaretIntoView` → `Invalidate`」と順序を揃える。
  `BringCaretIntoView` は `TopLine`/`ScrollX` setter 経由でしか `PositionCaret` を呼ばないため、
  可視域内で完結するケースのために先出しの `PositionCaret` が要る(`AfterEdit` の remarks と同じ論理)。

**受容する副作用**: 早期 return の後に置くため、「キャレットが既にその位置にあるが画面だけ
スクロールで離れている」ケース(現在行を Ctrl+G で指定・同じヒットを再検索)ではスクロールしない。
実害は小さいと判断して受容する。

`SetSelectionCharRange` も同じ形で追加する(`BringCaretIntoView` は `_caretCtrl.Caret` を見るが、
このメソッドは `Caret = Max(start, end)` にマップするので**範囲末尾**が可視化される
= `EnsureVisibleCharRange` の仕様と一致する)。

### 2.2 A-12 の修正

`ComputeBoundingRectangles` で X 座標から `_host.ScrollX` を引く。

```csharp
int sx = _host.ScrollX;
...
var (x1, y1, visible) = _host.ComputeCaretPointForUia(pos);
var (x2, _, _)        = _host.ComputeCaretPointForUia(rangeEnd);
if (visible)
{
    double w = Math.Max(1, x2 - x1);
    rects.Add(csx + x1 - sx);   // ← 減算
    ...
}
```

幅 `w` は差分なので `ScrollX` の影響を受けない(両端から同じ量を引くため)。

**スレッド面**: `IUiaTextHost.GetBoundingRectangles` は `IsHandleCreated` ガード →
`InvokeRequired` 判定 → `Invoke` の順で **必ず UI スレッド上で `ComputeBoundingRectangles` を
実行する**(`:569-586`)。`_host.ScrollX` の読みも UI スレッド上なので、a11y 鉄則
(RPC スレッドからエディタ内部に触らない)に追加の設計は要らない。

**水平方向に画面外の範囲**は X が負またはクライアント幅超になるが、これは実描画位置の
正しい報告であり、クリップはクライアント側の責務とする(既存の `visible` は垂直方向の判定のみ)。

## 3. 波及の全列挙

`SetCaretCharOffset` / `SetSelectionCharRange` を通る経路を網羅する。

| 呼び出し経路 | 変化 |
|---|---|
| `InputRouter.cs:147, 489`(矢印/Home/End の無修飾分岐)| 直後の `BringCaretIntoView()` と二重呼び。2 回目は可視のため no-op。`InputRouter` 側は**残す**(同じ if/else の shift 分岐は `MoveCaretWithSelection` を通り、setter 側の追従が無いため) |
| `InputRouter.cs:288`(Ctrl+A)/ `:531`(マウスドラッグ)| **不変**(`SetSelectionAnchored` 経路) |
| `EditorControl.SelectAll()` | **不変**(同上) |
| `MainForm.GoToLine`(`:958`)| ★ A-3 が直る |
| `MainForm.OpenAndSelect`(`:919`・grep ジャンプ)| ★ A-3 が直る |
| `SearchController.cs:232`(検索)/ `:308`(置換して次へ)| ★ A-3 が直る |
| `KinsokuFormatController.cs:74`(全文整形→先頭)/ `:76`(部分整形→変化箇所)| ★ A-3 が直る。整形直後は `ReplaceCharRange` → `AfterEdit` で既に追従済みだが、その後のキャレット再配置にも追従が付く=意図どおり |
| `CsvController.ExitMode`(`:113`・`MoveCaretCharOffset`)| CSV モードを抜けたとき最終セル位置へ追従(改善) |
| `FileController` セッション復元(`:760` / `:824` / `:849`・`SetCaretByLineColumn`)| 復元キャレットが可視域に入る(改善)。**要確認**: 非アクティブタブは TabControl がページを表示するまでハンドル未生成で `ClientSize` が暫定値のため、`TopLine` が最適値にならない可能性がある。現状は常に `TopLine=0`(キャレットが下方にあれば必ず不可視)なので**悪化はしない**が、実装時に App.Tests で確認する |
| UIA `Select()` → `UiaTextHostAdapter.SetSelection` → `SetSelectionCharRange` | ★ M-32 が直る |

## 4. 折り返し ON の制約(本ブランチでは直さない)

`BringCaretIntoView` の垂直判定は `TopLine` = **論理行**である(`Caret.cs` の `VisibleRowCount`
は折り返し ON でも視覚行数を論理行数と見なす近似)。したがって折り返し ON では、

- ビューポートより背の高い段落へジャンプすると、段落**先頭**までは寄るがキャレット自体は
  画面外に残ることがある

= 監査書 **A-6** の制約がそのまま残る。A-6 の根治には `TopLine` を視覚行単位にする設計判断が要り、
A-5(折り返し ON の ↑ が効かない)・E-1(折り返し ON の ↓ で NVDA が「ブランク」)と絡む。
監査書 §8-4 の判断どおり**別テーマ**として送る。

本ブランチは「折り返し OFF では完全に直る/折り返し ON では A-6 の範囲で部分的に直る」を
到達点とし、PR description に明記する。

## 5. テスト設計

### 5.1 既存テストの vacuous 化への対処(本件の要注意点)

`CaretScrollTests` の次の 3 本は「`SetCaretCharOffset` で caret を置く →
`BringCaretIntoView()` を呼ぶ → `TopLine` を検証」という順で書かれている。
setter 側が先にスクロールしてしまうため、**`BringCaretIntoView` の実装を壊しても緑のまま**になる。

| テスト | 現在の形 |
|---|---|
| `BringCaretIntoView_ScrollsDown_WhenCaretBelowVisible` | `TopLine=0` → `SetCaretCharOffset(末尾行)` → `BringCaretIntoView()` |
| `BringCaretIntoView_ScrollsUp_WhenCaretAboveVisible` | `TopLine=5` → `SetCaretCharOffset(0)` → `BringCaretIntoView()` |
| `BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar` | 同上(**Task 7 レビュー I-1 の回帰テスト**) |

→ **「先に caret を置く → 後から `TopLine` をずらす → `BringCaretIntoView()`」** の順に組み替えて
網を維持する。組み替え後も元の assertion(上端/下端への張り付き・paintHeight ベースの
`visibleRows`)はそのまま使える。

vacuous 化しないことを確認済みの既存テスト(caret が可視域内にあり setter がスクロールを
起こさないため):

- `BringCaretIntoView_NoOp_WhenCaretAlreadyVisible`
- `EnsureVisibleCharRange_PreservesCaretAndAnchor` / `_RestoresSystemCaretPosition`
- `UiaScrollIntoViewTests.ScrollCharRangeIntoView_PreservesCaretAndAnchor`
- `FileControllerTests.Save_WriteFailure_FastPath_PreservesCaretAndScroll`(`SetSelectionAnchored` 経路)

### 5.2 新規テスト

**Editor.Tests**

| 検証 | 内容 |
|---|---|
| `SetCaretCharOffset` の追従 | 可視域外の行へ移動 → `TopLine` が追従する |
| `SetSelectionCharRange` の追従 | 可視域外の範囲を選択 → 範囲**末尾**が可視域に入る |
| `GoToLine` の追従 | 監査書が名指しした `EditorControlCompatApiTests.GoToLine_*` に `TopLine` の assertion を足す |
| `SelectCharRange` の追従 | App 互換エイリアス経由でも追従する |
| **Ctrl+A 契約の固定** | `SetSelectionAnchored` / `SelectAll` は `TopLine` を動かさない(非既定位置 `TopLine != 0` から検証を始める=レビュー標準「no-change のテストは非既定位置から」) |
| UIA `Select()` の追従 | `IUiaTextHost.SetSelection` 経由で `TopLine` が追従する(M-32) |
| A-12 | hscroll 表示中に `ScrollX > 0` を設定 → `GetBoundingRectangles` の X が `ScrollX` 分だけ左に寄る。fixture は `CaretScrollTests.BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar`(長文行 + 狭いウィンドウ)を流用 |

**App.Tests**

| 検証 | 内容 |
|---|---|
| 検索ジャンプ | 可視域外のヒットへ `FindNext` → `TopLine` 追従 |
| grep ジャンプ | `OpenAndSelect` → `TopLine` 追従 |
| セッション復元 | 復元後のキャレットが可視域に入る(§3 の「要確認」の回収) |

### 5.3 ミューテーション検証(最終品質パスのスポットチェック)

| 変異 | 期待 |
|---|---|
| `SetCaretCharOffset` の `BringCaretIntoView()` を削除 | GoToLine / 復元系が赤 |
| `SetSelectionCharRange` の `BringCaretIntoView()` を削除 | 検索 / grep / UIA `Select()` が赤 |
| `SetSelectionAnchored` に `BringCaretIntoView()` を**追加** | Ctrl+A 非スクロールのテストが赤 |
| A-12 の `- sx` を削除 | 矩形テストが赤 |
| 早期 return を削除(`if (...) return;` を外す)| 既存の no-change テストが赤(挿入位置の意図の固定) |

`--filter` で絞るとミューテーションの結論を誤る(PR #43 の教訓)ため、変異ごとに
プロジェクト単位でテストを走らせる。

## 6. 品質ゲートと L5

- `tools/pre-merge-check.ps1` EXIT 0(CLAUDE.md §6)。0 warning 維持。
- **L5 必須**。SR 経路(UIA `Select()` = `kxEdit.Accessibility` / `EditorControl` の UIA 部)に
  触れる(CLAUDE.md §5)。監査書 §5 の「PR #36〜#39 分をまとめて 1 回」に相乗りする。
- L5 の確認項目(暫定):
  1. 折り返し OFF で Ctrl+G → 遠い行 → 画面がスクロールし、NVDA が移動先の行を読む
  2. 検索 / grep ジャンプで同上
  3. NVDA のレビューカーソル移動で**画面が飛ばない**(`ScrollIntoView` の「既に可視なら動かさない」原則が保たれている)
  4. 折り返し OFF で右へスクロールした状態で、NVDA のフォーカスハイライト矩形が実描画と一致する(A-12)
- `tools/sr-regression.ps1` をマージ前に手動実行(UIA 応答の検証まで。L5 の代替にはならない)。

## 7. 申し送り

- **A-6**(折り返し ON の視覚行スクロール)は本ブランチで直さない。§4 のとおり
  A-5 / E-1 と合わせて「折り返し ON の垂直移動」テーマで扱う。
- §3 の「セッション復元時、非アクティブタブのハンドル未生成で `TopLine` が最適値にならない」は
  実装時に確認する。悪化が確認された場合は `BringCaretIntoView` にハンドル未生成ガードを
  入れるのではなく(既存の編集経路の挙動を変えるため)、復元側でタブ表示後に再追従させる。
- `InputRouter` の `BringCaretIntoView()` は無修飾分岐に対して冗長になるが**残す**(§3)。
  将来 shift 系も setter へ寄せる場合は Ctrl+A の非スクロール契約を壊さないこと。
