// CaretController.cs
// Phase 3 (Task 3b) で EditorControl から抽出したキャレット/選択/desired X の state holder。
// 選択範囲は [Math.Min(_anchor, _caret), Math.Max(_anchor, _caret)]。
// - _anchor == _caret: 選択なし(単純キャレット位置)
// - _anchor <  _caret: 右方向に伸びた選択(キャレットが末尾)
// - _anchor >  _caret: 左方向に伸びた選択(キャレットが先頭・shift+←/Home で作られる)
//
// 責務: state 操作 (SnapAndClamp + 選択セマンティクス) のみ。
// Invalidate / PositionCaret / AfterEdit / UIA イベント発火 等の副作用は EditorControl 側に残す。
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;

namespace kxEdit.Editor;

internal sealed class CaretController
{
    // キャレット/選択の内部状態(P3 Task 2 でアンカー概念を導入=_selStart/_selEnd から _anchor に置換)。
    private int _caret;
    private int _anchor;

    /// <summary>キャレット位置(UTF-16 文字オフセット)。</summary>
    public int Caret => _caret;

    /// <summary>選択アンカー(UTF-16 文字オフセット)。<c>_anchor == _caret</c> なら選択なし。</summary>
    public int Anchor => _anchor;

    /// <summary>
    /// P3 Task 6: 上下移動(Up/Down/PageUp/PageDown)で保持する desired X(px)。
    /// -1 = 未計算=次回の垂直移動時に現在キャレット位置から新規計算する(慣例値)。
    /// Left/Right/Home/End など水平方向の移動が起きたらリセット=次の垂直移動で再計算される。
    /// Task 8 以降の編集経路(挿入/削除等)でも同様に -1 リセットする(§0-6 の一貫性)。
    /// </summary>
    public int DesiredXpx { get; set; } = -1;

    /// <summary>現在の選択範囲(UTF-16 文字オフセット・Start &lt;= End で返す)。</summary>
    public (int Start, int End) Selection => (Math.Min(_caret, _anchor), Math.Max(_caret, _anchor));

    /// <summary>選択があるか(<c>_anchor != _caret</c>)。</summary>
    public bool HasSelection => _caret != _anchor;

    /// <summary>
    /// キャレットとアンカーを同じ位置に設定する(単純キャレット移動=選択解除)。
    /// サロゲート中間位置は前方(high)スナップ・範囲外は [0, CharLength] にクランプ。
    /// </summary>
    public void SetTo(int pos, TextSnapshot snap)
    {
        int c = SnapAndClamp(pos, snap);
        _caret = c;
        _anchor = c;
    }

    /// <summary>
    /// キャレットを移動する。<paramref name="extend"/>=true でアンカーを保持(shift+移動系)、
    /// false でアンカーを caret に揃える(選択解除)。サロゲート/クランプ規約は <see cref="SnapAndClamp"/>。
    /// </summary>
    public void MoveTo(int newPos, bool extend, TextSnapshot snap)
    {
        int c = SnapAndClamp(newPos, snap);
        _caret = c;
        if (!extend)
            _anchor = c;
    }

    /// <summary>
    /// アンカーとキャレットを個別指定して選択範囲を設定する(非対称版)。
    /// <paramref name="anchor"/> &gt; <paramref name="caret"/> のときはキャレットが Min=選択先頭
    /// (=shift+左方向の選択)。両端は <see cref="SnapAndClamp"/>。
    /// </summary>
    public void SetSelection(int anchor, int caret, TextSnapshot snap)
    {
        _anchor = SnapAndClamp(anchor, snap);
        _caret = SnapAndClamp(caret, snap);
    }

    /// <summary>選択をクリアする(<c>_anchor = _caret</c>)。キャレット位置は保持。</summary>
    public void ClearSelection() => _anchor = _caret;

    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方へスナップする。CharLength 位置(=EOF)はキャレットが立てる境界なので許可。
    /// キャレット/選択のすべての位置設定入り口が本メソッドを通るため、ここで一度スナップすれば
    /// mid-surrogate / mid-CRLF は不変条件として守られる。
    /// </summary>
    /// <remarks>2026-07-31: 判定の実体は <see cref="TextBoundary.SnapToLogicalCharStart"/> へ移した
    /// (同じ規則が Core / UIA / ここの 3 箇所に散っていたため)。</remarks>
    public static int SnapAndClamp(int offset, TextSnapshot snap) =>
        TextBoundary.SnapToLogicalCharStart(snap, offset);
}
