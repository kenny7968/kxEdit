# UIA `ScrollIntoView` 未実装解消(案 C)設計書

策定日: 2026-07-25 / 対象ブランチ: `feature/uia-scrollintoview`

## 1. 背景・目的

`docs/plans/2026-07-25-sr-legacy-cleanup-design.md` §7 案 C の申し送りを回収する。

`src/yEdit.Accessibility/TextRangeProviderV2.cs` の `ITextRangeProvider.ScrollIntoView` は
no-op であり、その根拠は「PC-Talker はテキスト歩きで読めるため省略(v1 挙動踏襲)」という
**ベンダ固有の理由**だった。PR #28(マージ `77c53a2`)でコメントは「未実装(申し送り)」へ
正直化したが、挙動そのものは未着手のまま残っている。

本作業は `ScrollIntoView` を実装し、あわせて同じ「viewport とは何か」を扱う
`TextProviderImplV2.GetVisibleRanges()` の不正確さを解消する。

**本作業は挙動を追加する。** CLAUDE.md §3 のフル工程と §5 の L5 実機 SR 検証が必須。

## 2. 現状調査結果(2026-07-25 実測)

### 2.1 UIA からのスクロール手段は `ScrollIntoView` しかない

`src/yEdit.Accessibility/TextControlProviderV2.cs` の `GetPatternProvider` は
**TextPattern のみ**を返す。`ScrollPattern` / `ScrollItemPattern` は非対応。

```csharp
public object GetPatternProvider(int patternId)
{
    if (patternId == TextPatternIdentifiers.Pattern.Id)
        return _textProvider;
    return null;
}
```

したがって `ITextRangeProvider.ScrollIntoView` が **UIA クライアントに残された唯一の
スクロール手段**である。no-op は「UIA 経由でスクロールする方法が存在しない」ことを意味する。

これは CLAUDE.md §2 の「晴眼・弱視ユーザーも第一級」に照らして実害になり得る。
UIA テキスト範囲を追従する拡大鏡・ナレーターのスキャンモードが対象読者に含まれる。

### 2.2 スクロール基盤は既に揃っている

| 既存資産 | 内容 |
|---|---|
| `EditorControl.BringCaretIntoView()` | キャレット行が `[TopLine, TopLine+visibleRows)` の外なら `TopLine` を最小調整。折り返し OFF かつ HScroll 表示中は水平も調整 |
| `EditorControl.EnsureVisibleCharRange(start, length)` | 範囲末尾を可視化。caret / anchor は try/finally で必ず復元(= 装飾スクロール)。`SetSource` 前は no-op |
| `tests/yEdit.Editor.Tests/CaretScrollTests.cs` | 上記 2 つの契約テスト |
| `EditorControl.TopLine` setter | `ClampTopLine` + 変化時のみ `VScrollBar.Value` 追従 + `PositionCaret()` + `Invalidate()` |

新規スクロールロジックはほぼ書かずに済む。

### 2.3 マーシャリングの前例

`UiaTextHostAdapter` の書き込み系 2 メンバは同一の形をしている。

```csharp
void IUiaTextHost.SetSelection(int start, int end)
{
    if (_host.IsDisposed || !_host.IsHandleCreated) return;   // P5 Task 14 (I-3)
    if (_host.InvokeRequired)
    {
        _host.BeginInvoke(new Action(() => ((IUiaTextHost)this).SetSelection(start, end)));
        return;
    }
    _host.SetSelectionCharRange(start, end);
}
```

読み取り系で UI スレッド専用状態(`_topLine` / `_metrics` / `ClientSize`)を要するものは
同期 `Invoke` を使う(`GetBoundingRectangles` / `OffsetFromScreenPoint` /
`TryFindVisualSegment`)。本作業もこの使い分けを踏襲する。

### 2.4 副次発見: `GetVisibleRanges()` が文書全体を返している

```csharp
public ITextRangeProvider[] GetVisibleRanges() =>
    new ITextRangeProvider[] { new TextRangeProviderV2(this, 0, Host.TextLength) };
```

「文書全体が可視」と申告している。一方 `GetBoundingRectangles` は画面外オフセットに対して
`visible == false` で矩形を落とすため空配列を返す。**両者は矛盾している**。

これは `ScrollIntoView` と同じ「viewport とは何か」の欠落であり、かつ検証可能性に直結する。
可視性を先に問い合わせてからスクロールする種類のクライアントは、
「全部可視」と言われた時点で `ScrollIntoView` を呼ばない。その場合 `ScrollIntoView` だけを
実装しても L5 で効果が観測できず、「実害なし」と誤結論する恐れがある。

**ユーザー判断(2026-07-25): 本ブランチのスコープに含める。**

## 3. 方針(判断済み事項)

ユーザー判断(2026-07-25):

1. **前提調査を先に回さず、仕様準拠として実装し L5 で確認する。**
   §2.1 のとおり仕様違反は確定しており、前提調査そのものが実装かトレース版を要するため。
2. **`GetVisibleRanges` もスコープに含める**(§2.4)。
3. **実装方式は案 2「整列を効かせる」**(§5.1 で比較)。

## 4. スコープ

### 4.1 IN

| ファイル | 変更 |
|---|---|
| `src/yEdit.Accessibility/IUiaTextHost.cs` | メンバ 2 個追加(`ScrollRangeIntoView` / `GetVisibleCharRange`) |
| `src/yEdit.Accessibility/TextRangeProviderV2.cs` | `ScrollIntoView` を host への純委譲に |
| `src/yEdit.Accessibility/TextProviderImplV2.cs` | `GetVisibleRanges` を host 委譲に |
| `src/yEdit.Editor/UiaTextHostAdapter.cs` | 新 2 メンバの実装(マーシャリング) |
| `src/yEdit.Editor/EditorControl.Uia.cs` | explicit 実装の薄い委譲 2 行 |
| `src/yEdit.Editor/EditorControl.Caret.cs` | `ScrollRangeIntoView` 本体 |
| `src/yEdit.Editor/EditorControl.cs` | `GetVisibleCharRange` 本体 |
| `tests/yEdit.Core.Tests/Accessibility/*.cs` | stub 5 箇所へメンバ追加 + 新規テスト |
| `tests/yEdit.Editor.Tests/*.cs` | 新規テスト |

### 4.2 OUT(理由付き)

| 対象 | 除外理由 |
|---|---|
| `ScrollPattern` / `ScrollItemPattern` の実装 | `ScrollIntoView` で目的は満たせる。パターン追加は SR 側の扱いが変わる別案件 |
| 水平方向の可視範囲反映(`GetVisibleCharRange`) | §5.2 のとおり行単位で報告する。UIA の可視範囲は垂直スクロールを主眼にしており、慣例も行単位 |
| `RangeFromChild` が空範囲を返す件 | 子要素を持たないプロバイダでは妥当。本作業と無関係 |
| CSV モード固有のスクロール制御 | `EditorControl` 共通経路で足りる。CSV 固有の逸脱が要るかは L5 で判定(§7) |

### 4.3 完了条件

1. L1 / L2 の新規テストが全緑。
2. `tools/pre-merge-check.ps1` が EXIT 0(0 warning 維持)。
3. L5 チェックリスト(§6.3)が全項目 OK。特に **②(NVDA の通し読みが文書全体を読み切る)**。

## 5. 設計

### 5.1 `ScrollIntoView` 経路

```
SR(RPC スレッド)
 └ TextRangeProviderV2.ScrollIntoView(alignToTop)
     └ _owner.Host.ScrollRangeIntoView(_start, _end, alignToTop)   ← 純委譲・判断を持たない
        └ EditorControl.Uia.cs(explicit 実装・_uia への薄い委譲)
           └ UiaTextHostAdapter.ScrollRangeIntoView
               ├ IsDisposed || !IsHandleCreated → return
               └ InvokeRequired → BeginInvoke(自身へ再入)        ← SetSelection / SetFocus と同形
                  └ [UI スレッド] EditorControl.ScrollRangeIntoView(...)
```

**判断は `EditorControl` に集約する。** `TextRangeProviderV2` は `_start` / `_end` を
そのまま渡すだけで、`alignToTop` の解釈もオフセットの妥当性判断も持たない。
`_start` / `_end` は ctor で clamp 済みだが `Move` 後にバッファが縮むと stale になり得るため、
`EditorControl` 側で `SnapAndClamp` を通す二重防御にする。
縮退範囲(`start == end`)は分岐なしでそのまま動く。

**採用案(案 2: 整列を効かせる)**

```csharp
public void ScrollRangeIntoView(int start, int end, bool alignToTop)
{
    if (_buffer is null) return;
    var snap = _buffer.Current;
    int target = SnapAndClamp(alignToTop ? start : end);
    int line = snap.GetLineIndexOfChar(target);

    int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
    int visibleRows = Math.Max(1, paintHeight / Math.Max(1, _metrics.LineHeightPx));

    // 既に可視なら垂直は動かさない(視界の揺れ防止)
    if (line < _topLine || line >= _topLine + visibleRows)
        TopLine = alignToTop ? line : line - visibleRows + 1;

    // 水平 + 保険。caret / anchor は EnsureVisibleCharRange が try/finally で復元する
    EnsureVisibleCharRange(target, 0);
}
```

- `alignToTop == true` は範囲**先頭**を、`false` は範囲**末尾**を対象にする(UIA 仕様どおり)。
- **既に可視なら垂直は動かさない。** これが案 3(常に強制整列)との差であり、
  SR がテキストを歩くたびに画面が飛ぶのを防ぐ。
- `TopLine` setter が内部で `PositionCaret()` を呼ぶが、キャレットは動かしていないため
  OS 側キャレットはスクロール後の正しい座標に再配置されるだけで副作用にならない。
- `visibleRows` は折り返し ON でも**視覚行数を論理行数と見なす**近似で、
  `BringCaretIntoView` と同じ流儀。ここで別の計算にすると 2 つの可視判定が食い違うため、
  意図的に踏襲する。

**却下した案**

| 案 | 内容 | 却下理由 |
|---|---|---|
| 案 1 | `EnsureVisibleCharRange` への薄い委譲のみ | `alignToTop == true` かつ範囲が viewport より**下**にあるとき、先頭行が最下行に来て上端整列にならない。拡大鏡・ナレーターのスキャンモードで踏みやすいケース |
| 案 3 | 呼ばれたら必ず `TopLine` を整列 | 既に可視でも画面が飛ぶ。SR がテキストを歩くだけで視界が揺れ、晴眼・弱視ユーザーに実害 |

### 5.2 `GetVisibleRanges` 経路

読み取りなので `GetBoundingRectangles` と同じく**同期 `Invoke`**。Handle 未生成なら `(0, 0)`。

```csharp
public (int Start, int End) GetVisibleCharRange()
{
    if (_buffer is null) return (0, 0);
    var snap = _buffer.Current;
    int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
    var rows = ViewportLayout.Build(snap, _topLine, paintHeight, _wrapColumns, _metrics);
    if (rows.Count == 0) return (0, 0);
    var first = rows[0];
    var last = rows[^1];
    return (first.SegmentStartChar, last.SegmentStartChar + last.SegmentLength);
}
```

`ViewportLayout.Build` は **描画(`EditorControl.Paint.cs`)と同じ関数**である。
「見えている行」の定義を二重化しないことが本実装の要点。折り返し ON では視覚行境界になる。

意図的な割り切り 2 点:

- **末尾の改行は含めない**(`LineEndNoBreakOf` と同じ流儀)。範囲内部の改行は当然含む。
- **水平スクロールで横に隠れている部分は可視に含める**(行単位で報告する)。

`TextProviderImplV2` 側:

```csharp
public ITextRangeProvider[] GetVisibleRanges()
{
    var (s, e) = Host.GetVisibleCharRange();
    return new ITextRangeProvider[] { new TextRangeProviderV2(this, s, e) };
}
```

### 5.3 リスク:`GetVisibleRanges` 変更による読み範囲の縮小

**NVDA が `GetVisibleRanges` を通し読みの範囲決定に使っていた場合、可視域に絞ることで
読み範囲が縮む恐れがある。** これは自動テストでは判定できず L5 でしか分からない
(CLAUDE.md §2 の a11y 鉄則: SR の実発声は自動テストで検証できない)。

**対策**: §5.1(A)と §5.2(B)を**別 commit に分け、B だけを revert できる形**で積む。
L5 チェックリスト §6.3 の ② を必ず実施する。

### 5.4 インターフェース変更の波及

`IUiaTextHost` に 2 メンバ増えるため、テスト側の実装 5 箇所も更新が要る。

| ファイル | 型 |
|---|---|
| `tests/yEdit.Core.Tests/Accessibility/IUiaTextHostContractStubTests.cs` | `StubHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextControlProviderV2Tests.cs` | `StubHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs` | `Host` |
| `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs` | `InMemoryHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs` | `LargeSyntheticHost` |

`InMemoryHost` は `ScrollRangeIntoView` の引数を記録できる形にする(L1 の委譲検証で使う)。

## 6. 検証

### 6.1 L1(yEdit.Core.Tests)

- `ScrollIntoView` が `(start, end, alignToTop)` をそのまま host へ渡す(`true` / `false` 両方)。
- 縮退範囲(`start == end`)でも委譲される。
- `GetVisibleRanges` が `GetVisibleCharRange` の戻り値で範囲を作る。

### 6.2 L2(yEdit.Editor.Tests)

`ScrollRangeIntoView`:

| ケース | 期待 |
|---|---|
| 対象行が可視 | `TopLine` 不変 |
| 対象行が viewport より上・`alignToTop=true` | `TopLine == line` |
| 対象行が viewport より下・`alignToTop=true` | `TopLine == line`(上端整列) |
| 対象行が viewport より下・`alignToTop=false` | `TopLine == line - visibleRows + 1` |
| 選択を張った状態 | caret / anchor 不変 |
| `SetSource` 前 | no-op(例外を投げない) |

「可視なら不変」は CLAUDE.md §4 の教訓どおり **`TopLine = 5` という非既定位置から**検証する。
既定 0 のままだと「動かなかった」と「そもそも 0 だった」を区別できない。

`GetVisibleCharRange`: `TopLine` を動かすと範囲が追従する / 空文書は `(0, 0)` /
折り返し ON では視覚行境界になる。

`UiaTextHostAdapter`: 破棄済み・Handle 未生成で no-throw かつ no-op。

### 6.3 L5(実機 SR 検証・必須)

| # | 手順 | 期待 |
|---|---|---|
| ① | NVDA で大きなファイルを開き、検索ジャンプ後にレビューカーソルを画面外へ動かす | 画面が追従する |
| ② | **NVDA の通し読み(NVDA+↓)** | **文書全体を読み切る**(§5.3 の退行チェック) |
| ③ | ナレーターのスキャンモード(Caps+↓)で末尾まで移動 | 追従する |
| ④ | 通常のキー入力で編集 | 不意のスクロールが起きない |
| ⑤ | CSV モード | 異常が出ない |
| ⑥ | (余力があれば)Windows 拡大鏡「テキストカーソルに従う」 | 追従する |

### 6.4 品質ゲート

`tools/pre-merge-check.ps1` EXIT 0 / 0 warning 維持。

`tools/sr-regression.ps1` は UIA 応答を**変更する**ため、本作業では回帰目的で意味を持つ。
ただし前ブランチ(PR #28 §8.4)のとおり `pwsh` 未インストールで `word-sim.ps1` が
既知問題により落ちる。L5 と重複する範囲でもあるため、実行可否は実装時に判断する
(実行できない場合は §8 に理由を記録する)。

### 6.5 ミューテーション検証(最終品質パスのスポットチェック)

| 変異 | 赤になるべきテスト |
|---|---|
| 可視判定 guard を常時 true に | 「可視なら `TopLine` 不変」 |
| `alignToTop ? line : line - visibleRows + 1` の三項を反転 | 上端整列 / 下端整列 |
| Adapter の `IsDisposed \|\| !IsHandleCreated` guard を除去 | 破棄済み no-throw |

## 7. 実装単位

CLAUDE.md §3 のフル工程で進める。§3「簡略化の基準」は適用しない
(複数ファイル・スレッド境界を跨ぐ挙動追加のため)。

| Task | 内容 | レビュー |
|---|---|---|
| Task 1 | §5.1 `ScrollIntoView` 経路 + L1 / L2 テスト | 仕様レビュー + **コード品質レビュー(前倒し)** |
| Task 2 | §5.2 `GetVisibleRanges` 経路 + L1 / L2 テスト | 仕様レビュー |

Task 1 は RPC → UI の新しい書き込み seam を導入し、Task 2 もその形を踏襲するため、
CLAUDE.md §3 の前倒し例外(後続タスクが依存する新しい抽象・seam の導入)に該当すると判断する。

その後、最終ブランチレビュー 2 パス(コード品質 / 脆弱性)をパスごとに別エージェントで実施する。
脆弱性パスの焦点は外部入力のパースやパス操作ではなく、**RPC 境界を跨ぐ新しい書き込み経路**
に置く: 悪意ある UIA クライアントによる高頻度呼び出しで UI スレッドが飽和しないか、
`BeginInvoke` のキューが膨張しないか。
なお同じ性質は既存の `SetSelection` / `SetFocus` にもあり、本作業で新規に生じるものではない。

## 8. 実施記録(2026-07-25 追記)

### 8.1 実装時の精密化: タスク分割を Task 1a / 1b に割った

§7 の Task 1(= §5.1 の `ScrollIntoView` 経路)を **1a(配線)/ 1b(スクロール本体)** に分けた。

C# では新しいインターフェースメンバを足した瞬間に全実装クラスがコンパイルエラーになるため、
そのまま TDD を回すと RED が「コンパイルエラー」になり、テストの検出力を確認できない。
配線を先に通してから assertion failure で RED を作る形にした。

### 8.2 実装時の精密化: インターフェース名と `EditorControl` 実処理名を分ける

§5.1 のコードスニペットは `EditorControl.ScrollRangeIntoView` と書いているが、
**実装は `EditorControl.ScrollCharRangeIntoView`** になった。

`EditorControl` は `IUiaTextHost` を explicit interface implementation で実装し、実体は
`UiaTextHostAdapter` へ委譲される。Adapter は逆に `_host`(= `EditorControl`)の
public / internal メソッドを呼び返す。両者を同名にすると `_host.Xxx(...)` の解決先が
読み手に分からなくなる(コンパイルは通るが、explicit 実装は具象型経由では見えないため
public 側に解決される)。既存コードもこの規則を守っている
(`IUiaTextHost.SetSelection` ↔ `EditorControl.SetSelectionCharRange`)。

**採用した規則**(前倒しコード品質レビュー Minor-1 で確定):

| インターフェース | `EditorControl` 側 |
|---|---|
| `IUiaTextHost.SetSelection` | `SetSelectionCharRange`(既存) |
| `IUiaTextHost.ScrollRangeIntoView` | `ScrollCharRangeIntoView` |
| `IUiaTextHost.GetVisibleRange` | `GetVisibleCharRange` |

§5.2 は当初インターフェース側を `GetVisibleCharRange` としていたが、上記の衝突を避けるため
**`GetVisibleRange` へ改めた**。`ForUia` 接尾辞(既存 `ComputeCaretPointForUia` の流儀)は
新規メンバには使わない。既存の実際の規則は「衝突するときだけ改名する」であり
(`OffsetFromClientPoint` は衝突しないので無印)、`Char` を挟む方が既存 `SetSelectionCharRange`
と揃う。`ComputeCaretPointForUia` は改名しない(スコープ外)。

`EditorControl` 側の実処理メソッドは **`internal`** とする。呼び出し元は adapter 1 箇所のみで
App 層からの参照がなく、同 seam の他メンバ(`ComputeCaretPointForUia` / `HasFocusCached` /
`OffsetFromClientPoint`)も `internal` のため。

### 8.3 §6.5 のミューテーション表の誤記

§6.5 は「Adapter の `IsDisposed || !IsHandleCreated` ガードを除去 → 破棄済み no-throw が赤」
としていたが、**策定時点では事実と異なっていた**。

`ScrollRangeIntoView_NoThrow_WhenHandleNotCreated` / `_AfterDispose` は UI スレッド上で走るため
`InvokeRequired == false` となり、**`BeginInvoke` 分岐に一度も入らない**。実測でガードを削除しても
全件 PASS(SURVIVED)だった。

さらに重要な事実として、**`Control.InvokeRequired` は Handle 未生成 / 破棄後に false を返す**。
つまりこのガードは「`BeginInvoke` の `InvalidOperationException` 防止」だけでなく、
**RPC スレッドが `ClientSize` / `_hscroll.Visible` / `PositionCaret()` を直接触るのを防ぐ**という
CLAUDE.md §2 a11y 鉄則そのものを守っている。二重に load-bearing でありながら両方とも未被覆だった。

前倒しコード品質レビュー Important-1 の対応として、`Task.Run` から呼んで UI スレッドで
`Application.DoEvents()` を回すクロススレッドテストを追加し、ガード除去で
`TopLine` が 7 → 25 に化ける(= RPC スレッドが UI 状態を書き換えた証拠)ことを確認した。
前例は `tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs` の
`Host_LineStartOf_WithWrap_CalledFromNonUiThread_MarshalsSafely`。

なお `SetSource` 自体が Handle を生成するため、「Handle 未生成 + バッファあり」の状態は
`Control.DestroyHandle` を reflection で呼んで作っている(`CaretScrollTests` が `OnKeyDown` を
reflection で叩くのと同じ流儀)。

### 8.4 前倒しコード品質レビューで判明した構造的な弱さ

§5.1 の `<remarks>` は「`visibleRows` をここだけ別計算にすると 2 つの可視判定が食い違う」を
採用理由に挙げていたが、**その一致は当初コピペでしか担保されていなかった**。
`PaintHeightPx` / `VisibleRowCount` を private accessor に括り出し、構造で担保する形に改めた。

同じ式は `EditorControl.Paint.cs`(描画経路)にも存在する。§5.2 の `GetVisibleCharRange` は
「描画と同じ定義を使う」ことが要点のため、描画側も同 accessor に寄せる。

### 8.5 却下した指摘

- **テスト stub 用の `abstract class UiaTextHostStubBase` 導入**(前倒し品質レビュー提案)。
  レビュアー自身が「3 回目以降が見えたら」と条件付きで挙げており、本作業は 2 回目。YAGNI により却下。
  3 回目のメンバ追加が視野に入った時点で再検討する。
- **`+ 1` 脱落のミューテーションが SURVIVED する件**(仕様レビュー Nit-4)。
  後段の `BringCaretIntoView` が同じ式で下端整列を再計算して補正するため観測不能。
  挙動は正しく欠陥ではないため修正しない。

### 8.6 最終ブランチレビュー 2 パスの結果

CLAUDE.md §3 工程 5 のとおり、**パスごとに独立した別エージェント**を起動した。両パスとも
**「マージ可」**、Critical / High はゼロ。指摘はすべて「正しい実装が、正しいまま保たれる保証」の話だった。

#### コード品質パス

ミューテーション **20 件**(指定表 8 + レビュアー自主追加 12)を実行。SURVIVED 3 件が指摘の実体。

| SURVIVED した変異 | なぜ問題か | 対応 |
|---|---|---|
| `rows[rows.Count - 1]` → `rows[0]` | **§5.2 の存在意義そのもの**(可視域 = viewport であって 1 行ではない)が未固定。壊れると「SR に見えている範囲が 1 行だけ」という形で出るため、§5.3 の L5 リスク判定を誤らせる | ① fixup |
| `rows.Count == 0` ガード除去 | `Size = (400, 0)` で到達可能。ガード無しだと `ArgumentOutOfRangeException` が**同期 `Invoke` の中**で発生し、Adapter が catch していない型なので RPC スレッド経由で UIA の COM 境界へ抜ける | ① fixup |
| Adapter が `(end, end, alignToTop)` を渡す | インターフェース経由で seam 全体を通すテスト 2 本が**両方とも縮退範囲** `off, off` を渡しており、範囲端点の取り違えを誰も拾えない | ① fixup |

Minor 4 件 / Nit 3 件(`ComputeCaretPoint` の `PaintHeightPx` 未寄せ・floor/ceil の呼び名・
`IUiaTextHost` summary の「のみ」・Adapter の「同形」コメント・メンバ数の残存・配置順・
`IsDisposed` 不要理由の未記載)も fixup で反映した。

#### 脆弱性パス

RPC 境界・例外安全性・情報漏洩を個別に検証。**新規経路のガード配置は既存メンバより厳格**で、
a11y 鉄則は守られている(`IsHandleCreated` を `InvokeRequired` 分岐の**外**に置いている)。

**Medium-1(① fixup)**: `ScrollCharRangeIntoView` に「何も動かす余地がない」ケースの早期リターンが
無く、無変化呼び出しでも `EnsureVisibleCharRange` の `finally` の `PositionCaret()` →
`ComputeCaretPoint` が**対象論理行を丸ごと再折り返し**していた。実測 **1,584 ms/回**
(20,000 文字の CJK 単一論理行・`WrapColumns=80`)、**22.9 MB alloc/回**(4 MB ASCII 単一行)。

**旧実装は空メソッドだったため、これは本ブランチが新規に持ち込んだコスト**である。折り返し ON の
日本語長段落は yEdit の主要ユースケースであり、SR はレビューカーソルを動かすたびに
`ScrollIntoView` を呼ぶ。Adapter は fire-and-forget なので投入速度 > 消化速度になり
invoke キューが単調増加する。早期リターンで解消した(等価性の根拠はコード `<remarks>` に記載)。

成立しないと判定された懸念(いずれも根拠付き): stale range からの例外(`SnapAndClamp` が保証)/
`_vscroll.Value` の範囲外代入(バッファ変異は必ず `UpdateVerticalScrollbar` を先に通る)/
整数オーバーフロー(`visibleRows >= 1` で発生不能)/ デッドロック(新規経路はロックを取らず、
UI スレッドから RPC を同期待ちする箇所もない)/ 情報漏洩(`(0, TextLength)` → 部分範囲 = 単調縮小)。

### 8.7 ゲート実行結果(2026-07-25)

| 項目 | 結果 |
|---|---|
| `tools/pre-merge-check.ps1` | **EXIT 0**。Core 973 / Editor 306 / App 444 全緑・0 warning・CSharpier 306 files clean |
| `tools/sr-regression.ps1` | **EXIT 0・全 PASS**。`verify-uia-editor.ps1` 5 ケース + `word-sim.ps1` 6 ケース |

**§6.4 の予想は外れた。** §6.4 は「`pwsh` 未インストール環境では `word-sim.ps1` が
`tools/README.md` の既知問題(BOMless UTF-8 の日本語コメントを Shift-JIS 誤解釈)で落ちる」と
予想し、PR #28(§8.4)は同じ理由で実行自体を見送っていた。今回 WinPS 5.1 フォールバックで
実行したところ**警告バナーは出たが 6 ケースすべて PASS** した。

既知問題は環境のロケール / コードページに依存する可能性がある。`tools/README.md` の注意書きを
書き換えるかどうかは、複数環境での再現確認が要るため本ブランチでは触らない(→ §9)。

### 8.8 プロセス上の教訓: ミューテーション検証後の `--no-build`

脆弱性パスのレビュアーが実際に踏んだ。**ミューテーション検証のあとソースを復元しても、
増分ビルドが timestamp を見てコピーを省くため `tests/*/bin/` に変異したままの DLL が残ることがある。**
その状態で `dotnet test --no-build` を回すと、**変異したバイナリを本物と誤認**する。

このときの失敗 5 件は「三項反転の挙動」と完全に一致しており、実装バグと見分けがつかなかった。
**ミューテーション検証を挟むセッションでは、復元のたびにビルド込みでテストを回すこと。**

### 8.9 L5 実機 SR 検証の結果(2026-07-26・全項目 OK)

ユーザー実施。**全項目 green。** §6.3 の 6 項目に、脆弱性パス Medium-1 の実測を受けて
追加した 1 項目(折り返し ON の日本語長段落での連続レビューカーソル移動)を加えた 7 項目。

| # | 内容 | 結果 |
|---|---|---|
| ① | NVDA・検索ジャンプ後にレビューカーソルを画面外へ | OK(追従する) |
| ② | **NVDA の通し読み(NVDA+↓)** | **OK(文書全体を読み切る)** |
| ③ | ナレーターのスキャンモードで末尾まで移動 | OK |
| ④ | 通常のキー入力で編集 | OK(不意のスクロールなし) |
| ⑤ | CSV モード | OK |
| ⑥ | **折り返し ON の日本語長段落で連続レビューカーソル移動**(§8.6 Medium-1 の検証) | OK(引っかからない) |
| ⑦ | Windows 拡大鏡「テキストカーソルに従う」 | OK |

**② が OK だったため §5.3 のリスクは顕在化せず、`GetVisibleRanges` の変更
(commit `87cf9a2`)は revert しない。** 同 commit を単独 revert 可能に保った設計判断は
結果的に使わずに済んだが、判定が付くまでの安全弁として機能した。

**⑥ が OK** であることは §8.6 Medium-1 の早期リターンが実機で効いていることを示す。
ただし §9.1(`GetVisibleRanges` の計算量)は別経路であり、本項目では検証されていない。

## 9. 申し送り(follow-up)

### 9.1 `GetVisibleRanges` の計算量(脆弱性パス Medium-2・受容)

`ViewportLayout.Build` は各論理行について `LineLayout.Wrap` で**全セグメントを作り切ってから**
可視分だけ切り捨てる。「見えているのは 30 行」でも先頭論理行が 20 万文字なら 20 万文字ぶん折り返す。

実測 **1,640 ms/回**(20,000 文字 CJK 単一行・`WrapColumns=80`)、**22.9 MB alloc/回**
(4 MB ASCII 単一行)。Adapter は**同期 `Invoke`** なので RPC スレッドもその間ブロックされる。
変更前は `(0, TextLength)` = O(1)・マーシャリングなしだった。

**受容の理由**: 同じコストは `OnPaint` が既に払っており(= yEdit のこの文書形状に対する
既存の性能特性)、新しい崖ではない。根治は `LineLayout.Wrap` を打ち切り可能にする Core 変更で、
**描画経路も同時に速くなる本命**だが設計・検証の範囲が別物になる。単一エントリキャッシュは
ブランチ終盤に無効化面(スナップショット / 折り返し / リサイズ / フォント変更)を増やすため見送った。

**回収条件**: L5 または実運用で「SR 操作時の引っかかり」が観測されたら着手する。
着手するなら `LineLayout.Wrap` の打ち切り(根治)を選ぶ。

### 9.2 既存欠陥: `GetBoundingRectangles` / `OffsetFromScreenPoint` のガード位置(脆弱性パス Low-3)

`UiaTextHostAdapter` の既存 2 メンバは `IsHandleCreated` を **`InvokeRequired` 分岐の内側**にしか
置いていない。`Control.InvokeRequired` は Handle 未生成 / 破棄後に false を返すため、
**RPC スレッドがそのまま `ComputeBoundingRectangles` を実行**する。

到達シナリオ(レビュアーが特定): SR がプロバイダ参照を掴んだままユーザーがタブを閉じる →
Handle 破棄 → SR が `GetBoundingRectangles` を呼ぶ → RPC スレッドで `ComputeCaretPointForUia` →
`GdiCharMetrics.MeasureRun` が**破棄済み `_font` で `TextRenderer.MeasureText`**。
System.Drawing が投げるので native UAF にはならず HRESULT で返るが、CLAUDE.md §2 の鉄則違反。

**本ブランチは悪化させていない**(新規 2 メンバは分岐の外側に置いて正しく塞いでいる)。
修正は `IsHandleCreated` チェックを `InvokeRequired` の外へ出す 2 行。別作業で回収する。

### 9.3 `ScrollRangeIntoView` の `BeginInvoke` に catch が無い(脆弱性パス Low-2)

ガード〜`BeginInvoke` 間で Handle が消えると RPC スレッドへ `InvalidOperationException` が飛ぶ。
`GetVisibleRange` は catch するが `ScrollRangeIntoView` はしない。既存 `SetSelection` / `SetFocus` と
同形で**本ブランチによる悪化はゼロ**、CCW が HRESULT に変換するのでクラッシュもしない。
ただし `ScrollIntoView` は「UIA に残された唯一のスクロール手段」(§2.1)なので、タブクローズと
競合したときに SR 側へエラーが露出する。**3 メンバまとめて直すのが筋**なので別作業とする。

### 9.4 `EnsureVisibleCharRange` の caret 一時退避窓(脆弱性パス Low-1)

`EnsureVisibleCharRange` は `try` 内で caret を一時的に対象位置へ移し `finally` で復元する。
一方 `IUiaTextHost.GetSelection` は `_caret.Caret` / `_caret.Anchor` を live で無同期読みするため、
窓の間に別スレッドの `GetSelection` が実際とは違うキャレットを観測し得る。

セキュリティ影響はなく、読み上げ位置の正確性の問題。**§8.6 Medium-1 の早期リターンにより、
既に可視なケース(= SR の通常操作の大半)ではこの窓自体が消えた。** 残る窓は実スクロールを
伴うときだけで、そのときは caret が動いて当然の局面である。観測されたら再検討する。

### 9.5 `GetVisibleRanges` が縮退範囲を返す場合(品質パス Nit-4)

Handle 未生成・空文書・可視行ゼロでは `(0, 0)` の縮退範囲を 1 本返す。UIA 仕様は
「可視範囲を決められないときは空配列」とも読める。§5.2 で「1 本返す」と決めた判断どおりだが、
**L5 の ② が想定外の挙動をしたとき真っ先に疑う候補**になるため記録する。

### 9.6 `BeginInvoke` 自己再入の意図的な未被覆(品質パス Nit-5)

Adapter の `BeginInvoke` が `((IUiaTextHost)this).ScrollRangeIntoView(...)` と自身へ再入するのは、
UI スレッド到達時にガードを再評価するため。この効果を殺す変異は SURVIVED する
(race テストは flaky になるため追わない判断)。**意図的に未被覆**であることを記録する。
コード側にはコメントで load-bearing である旨を明記済み。

### 9.7 `tools/README.md` の `word-sim.ps1` 既知問題(§8.7)

WinPS 5.1 での Shift-JIS 誤解釈は**本環境では再現しなかった**。ロケール / コードページ依存の
可能性がある。注意書きを書き換えるには複数環境での再現確認が要るため本ブランチでは触らない。
