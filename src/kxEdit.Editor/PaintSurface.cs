// PaintSurface.cs
// 2026-09-27 性能改善フェーズ 9b(設計書 §14.2・計画 §0.2): スクロールで既存の画素を移す seam。
// 製品は Win32PaintSurface(ScrollWindowEx)。テストは画面のビットマップを動かす偽物に差し替える
// (画面外の HostForm には WM_PAINT が来ず、保留中の無効領域も消えないため)。
namespace kxEdit.Editor;

/// <summary>画面の画素を移す先。UI スレッド専用。</summary>
internal interface IPaintSurface
{
    /// <summary>
    /// 画素を移してよいか。移した画素が「画面の絵の入力」(<c>_lastPaintedInputs</c>)から描いた絵であることが前提
    /// (計画 §0.3 の不変条件 3)なので、保留中の無効領域があるときは false にする。
    /// </summary>
    bool CanScroll(EditorControl editor);

    /// <summary>
    /// <paramref name="area"/> の画素を (<paramref name="dx"/>, <paramref name="dy"/>) だけ移す。失敗したら false。
    /// <paramref name="uncovered"/> は、移した結果として描き直しが要る領域の外接矩形(空のこともある)。
    /// </summary>
    bool TryScroll(EditorControl editor, int dx, int dy, Rectangle area, out Rectangle uncovered);
}

/// <summary><see cref="IPaintSurface"/> の製品実装(ScrollWindowEx)。</summary>
internal sealed class Win32PaintSurface : IPaintSurface
{
    public static readonly Win32PaintSurface Instance = new();

    private Win32PaintSurface() { }

    /// <remarks>
    /// false にする条件: ハンドルがない・非表示(画面外の窓・最小化前の組み立て中など)・保留中の無効領域がある・
    /// 兄弟のコントロールがエディタに重なる(CSV のセル編集 TextBox など。重なった部分の画素を運ぶと、
    /// 他のコントロールの絵がエディタに混ざるおそれがある)・スクロールバー以外の子がある。
    /// いずれも全面の再描画に落とすだけなので、条件は保守的でよい。
    /// </remarks>
    public bool CanScroll(EditorControl editor)
    {
        if (!editor.IsHandleCreated || !editor.Visible)
            return false;
        if (NativeMethods.GetUpdateRect(editor.Handle, 0, false))
            return false;
        if (editor.Parent is { } parent)
        {
            foreach (Control sibling in parent.Controls)
            {
                if (
                    !ReferenceEquals(sibling, editor)
                    && sibling.Visible
                    && sibling.Bounds.IntersectsWith(editor.Bounds)
                )
                    return false;
            }
        }
        foreach (Control child in editor.Controls)
        {
            if (child is not ScrollBar && child.Visible)
                return false;
        }
        return true;
    }

    public bool TryScroll(
        EditorControl editor,
        int dx,
        int dy,
        Rectangle area,
        out Rectangle uncovered
    )
    {
        var rc = new NativeMethods.RECT
        {
            left = area.Left,
            top = area.Top,
            right = area.Right,
            bottom = area.Bottom,
        };
        var clip = rc;
        // flags 0: 子ウィンドウ(スクロールバー)は動かさず、無効化もしない(呼び出し側が uncovered を無効化する)。
        int result = NativeMethods.ScrollWindowEx(
            editor.Handle,
            dx,
            dy,
            ref rc,
            ref clip,
            0,
            out var upd,
            0
        );
        uncovered = Rectangle.FromLTRB(upd.left, upd.top, upd.right, upd.bottom);
        return result != 0;
    }
}
