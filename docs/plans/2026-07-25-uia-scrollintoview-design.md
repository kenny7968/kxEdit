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

## 8. 申し送り(follow-up)

実装後に追記する。
