// InputRouter.cs
// Phase 3 (Task 3c) で EditorControl.Input.cs から抽出した入力ディスパッチャ。
// state を持たない pure dispatcher: keymap Dictionary と MouseEventKind ディスパッチのみを担う。
//
// 各ハンドラは <see cref="InputContext"/> と KeyEventArgs / MouseEventArgs を受け取り、
// ホスト側 (EditorControl) の public / internal API を叩く。移設した case body / mouse handler body は
// 元の switch 分岐からロジック中身を bit-perfect に保つ(挙動不変)。
using yEdit.Core.Editing;
using yEdit.Core.Text;

namespace yEdit.Editor;

/// <summary>Task 3c: InputRouter がマウス入力を dispatch する種別。</summary>
internal enum MouseEventKind
{
    Down,
    Move,
    Up,
    DoubleClick,
}

/// <summary>
/// keymap dispatcher(state なし)。EditorControl.Input.cs の OnKeyDown 分岐(22 個)と
/// OnMouse{Down,Move,Up,DoubleClick} を委譲先とする。
/// </summary>
internal sealed class InputRouter
{
    private readonly EditorControl _host;
    private readonly CaretController _caret;

    // CA1859: 呼び出しごとの TryGetValue が interface dispatch を通さず直接呼べるよう
    // concrete Dictionary<> を保持する(input routing の hot path)。
    private readonly Dictionary<Keys, Func<InputContext, KeyEventArgs, bool>> _keyMap;

    public InputRouter(EditorControl host, CaretController caret)
    {
        _host = host;
        _caret = caret;
        _keyMap = BuildKeyMap();
    }

    /// <summary>
    /// KeyDown 経路: <see cref="KeyEventArgs.KeyCode"/> 単位で dispatch。
    /// ハンドラが true を返した場合のみ <c>e.Handled=true</c> を設定
    /// (元 OnKeyDown の case 単位で <c>e.Handled=true; return;</c> を打っていた挙動と等価)。
    /// 未マップ / <c>_buffer</c> 未セット / ハンドラが unhandled を返す場合は no-op(base の伝搬に委ねる)。
    /// </summary>
    /// <remarks>
    /// 元 OnKeyDown は <c>base.OnKeyDown(e)</c> → <c>if (_buffer is null) return;</c> → switch の順で、
    /// switch 内の each case 単位で <c>e.Handled=true</c> のみ立てて <c>SuppressKeyPress</c> は
    /// 設定していない。本 Router も <c>SuppressKeyPress</c> は設定しない(挙動不変)。
    /// </remarks>
    public void RouteKey(KeyEventArgs e)
    {
        if (_host.Buffer is null)
            return;
        if (!_keyMap.TryGetValue(e.KeyCode, out var handler))
            return;
        var ctx = new InputContext(_host, _caret);
        if (handler(ctx, e))
            e.Handled = true;
    }

    /// <summary>マウス経路: <paramref name="kind"/> に応じて MouseXxx ハンドラへ dispatch。</summary>
    /// <remarks>
    /// OnMouseWheel は精度改善版(_wheelAccum 累積 + HandledMouseEventArgs)で経路が異なるため
    /// EditorControl.Input.cs にとどまり、Router 経由ではない。
    /// </remarks>
    public void RouteMouse(MouseEventKind kind, MouseEventArgs e)
    {
        var ctx = new InputContext(_host, _caret);
        switch (kind)
        {
            case MouseEventKind.Down:
                HandleMouseDown(ctx, e);
                break;
            case MouseEventKind.Move:
                HandleMouseMove(ctx, e);
                break;
            case MouseEventKind.Up:
                HandleMouseUp(ctx, e);
                break;
            case MouseEventKind.DoubleClick:
                HandleMouseDoubleClick(ctx, e);
                break;
        }
    }

    // ===== keymap =====

    /// <summary>
    /// 移設した keymap 表(元 OnKeyDown の 22 分岐)。KeyCode 単位で 1 エントリ登録し、
    /// 修飾 (Ctrl/Shift) と <see cref="EditorControl.ReadOnly"/> の判定は各ハンドラ内で
    /// 元 case の <c>when</c> ガードと等価に行う。
    /// </summary>
    private static Dictionary<Keys, Func<InputContext, KeyEventArgs, bool>> BuildKeyMap()
    {
        return new Dictionary<Keys, Func<InputContext, KeyEventArgs, bool>>
        {
            [Keys.Left] = HandleLeft,
            [Keys.Right] = HandleRight,
            [Keys.Home] = HandleHome,
            [Keys.End] = HandleEnd,
            [Keys.Up] = HandleUp,
            [Keys.Down] = HandleDown,
            [Keys.PageUp] = HandlePageUp,
            [Keys.PageDown] = HandlePageDown,
            [Keys.A] = HandleA,
            [Keys.X] = HandleX,
            [Keys.C] = HandleC,
            [Keys.V] = HandleV,
            [Keys.Z] = HandleZ,
            [Keys.Y] = HandleY,
            [Keys.Insert] = HandleInsert,
            [Keys.Delete] = HandleDelete,
            [Keys.Back] = HandleBack,
            [Keys.Enter] = HandleEnter,
            [Keys.Tab] = HandleTab,
        };
    }

    // ===== nav shared tail =====

    /// <summary>
    /// 移動系キーで target が確定した後の共通後処理(元 OnKeyDown 末尾の
    /// <c>if (target is int t2)</c> ブロックと等価)。
    /// - <paramref name="resetDesired"/> true で <see cref="CaretController.DesiredXpx"/> を -1 リセット
    ///   (水平方向・Home/End 用=Left/Right/Home/End)。
    /// - Shift 押下時は <see cref="EditorControl.MoveCaretWithSelection"/>(アンカー保持=選択拡張)、
    ///   無修飾時は <see cref="EditorControl.SetCaretCharOffset"/>(選択解除)。
    /// - 純キャレット移動でも <see cref="Core.Buffers.TextBuffer.BreakUndoCoalescing"/> を呼ぶ
    ///   (Scintilla 互換)。<see cref="EditorControl.BringCaretIntoView"/> で追従スクロール。
    /// </summary>
    private static void ApplyNavMove(
        InputContext ctx,
        KeyEventArgs e,
        int target,
        bool resetDesired
    )
    {
        if (resetDesired)
            ctx.Caret.DesiredXpx = -1;
        bool shift = (e.Modifiers & Keys.Shift) != 0;
        if (shift)
            ctx.Host.MoveCaretWithSelection(target);
        else
            ctx.Host.SetCaretCharOffset(target);
        ctx.Host.Buffer!.BreakUndoCoalescing();
        ctx.Host.BringCaretIntoView();
    }

    // ===== keyboard handlers (nav) =====

    private static bool HandleLeft(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        // 2026-08-04: 走査上限つき。空白ゼロの単一クラス長大行(ascii 500K)では
        // 上限なしの PrevWordStart が UI スレッドを約 1.4 秒占有する
        // (docs/plans/2026-08-03-uia-word-unit-design.md §2.4)。上限に当たると
        // キャレットは単語 run の途中で止まる = 意図的な仕様変更。
        int target = ctrl
            ? WordBoundary.PrevWordStart(snap, ctx.Caret.Caret, WordBoundary.DefaultMaxScan)
            : NavigationCommands.MoveLeftChar(snap, ctx.Caret.Caret);
        ApplyNavMove(ctx, e, target, resetDesired: true);
        return true;
    }

    private static bool HandleRight(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        // 2026-08-04: 走査上限つき。理由は HandleLeft と同じ(空白ゼロの単一クラス長大行で
        // 上限なしの NextWordStart が UI スレッドを占有する)。cap は SR の読み上げスパン
        // (UiaTextHostAdapter)とダブルクリック単語選択と必ず同じ値を使うこと。
        int target = ctrl
            ? WordBoundary.NextWordStart(snap, ctx.Caret.Caret, WordBoundary.DefaultMaxScan)
            : NavigationCommands.MoveRightChar(snap, ctx.Caret.Caret);
        ApplyNavMove(ctx, e, target, resetDesired: true);
        return true;
    }

    private static bool HandleHome(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        // P8-1a: 折り返し ON では視覚行(折り返し行)の先頭へ(NVDA が視覚行先頭から読むよう App 層挙動を統一)
        int target = ctrl
            ? 0
            : NavigationCommands.MoveHomeSmart(
                snap,
                ctx.Caret.Caret,
                ctx.Host.WrapColumns,
                ctx.Host.Metrics
            );
        ApplyNavMove(ctx, e, target, resetDesired: true);
        return true;
    }

    private static bool HandleEnd(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        int target = ctrl ? snap.CharLength : NavigationCommands.MoveEnd(snap, ctx.Caret.Caret);
        ApplyNavMove(ctx, e, target, resetDesired: true);
        return true;
    }

    private static bool HandleUp(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        var (t, d) = VerticalNavigation.MoveUp(
            snap,
            ctx.Caret.Caret,
            ctx.Caret.DesiredXpx,
            ctx.Host.WrapColumns,
            ctx.Host.Metrics
        );
        ctx.Caret.DesiredXpx = d;
        ApplyNavMove(ctx, e, t, resetDesired: false);
        return true;
    }

    private static bool HandleDown(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        var (t, d) = VerticalNavigation.MoveDown(
            snap,
            ctx.Caret.Caret,
            ctx.Caret.DesiredXpx,
            ctx.Host.WrapColumns,
            ctx.Host.Metrics
        );
        ctx.Caret.DesiredXpx = d;
        ApplyNavMove(ctx, e, t, resetDesired: false);
        return true;
    }

    private static bool HandlePageUp(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        int rows = Math.Max(
            1,
            ctx.Host.ClientSize.Height / Math.Max(1, ctx.Host.Metrics.LineHeightPx)
        );
        var (t, d) = VerticalNavigation.PageUp(
            snap,
            ctx.Caret.Caret,
            ctx.Caret.DesiredXpx,
            ctx.Host.WrapColumns,
            rows,
            ctx.Host.Metrics
        );
        ctx.Caret.DesiredXpx = d;
        ApplyNavMove(ctx, e, t, resetDesired: false);
        return true;
    }

    private static bool HandlePageDown(InputContext ctx, KeyEventArgs e)
    {
        var snap = ctx.Host.Buffer!.Current;
        int rows = Math.Max(
            1,
            ctx.Host.ClientSize.Height / Math.Max(1, ctx.Host.Metrics.LineHeightPx)
        );
        var (t, d) = VerticalNavigation.PageDown(
            snap,
            ctx.Caret.Caret,
            ctx.Caret.DesiredXpx,
            ctx.Host.WrapColumns,
            rows,
            ctx.Host.Metrics
        );
        ctx.Caret.DesiredXpx = d;
        ApplyNavMove(ctx, e, t, resetDesired: false);
        return true;
    }

    // ===== keyboard handlers (Ctrl+letter・SelectAll/Clipboard/Undo/Redo) =====

    private static bool HandleA(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.A when ctrl → ctrl 無しは no-match(fallthrough で unhandled)
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl)
            return false;
        var snap = ctx.Host.Buffer!.Current;
        ctx.Host.SetSelectionAnchored(0, snap.CharLength);
        // 全選択=文書頭〜末尾のジャンプ相当なので、水平移動と同様に desired X をリセット。
        // これを忘れると次の Up/Down が「Ctrl+A 前の古い列」を目指す(Task 6 レビュー S-1)。
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.Buffer.BreakUndoCoalescing();
        return true;
    }

    private static bool HandleX(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.X when ctrl && !ReadOnly
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl || ctx.Host.ReadOnly)
            return false;
        ctx.Host.Cut();
        return true;
    }

    private static bool HandleC(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.C when ctrl (ReadOnly でも動く=Notepad と同挙動)
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl)
            return false;
        ctx.Host.Copy();
        return true;
    }

    private static bool HandleV(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.V when ctrl && !ReadOnly
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl || ctx.Host.ReadOnly)
            return false;
        ctx.Host.Paste();
        return true;
    }

    private static bool HandleZ(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.Z when ctrl && !ReadOnly
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl || ctx.Host.ReadOnly)
            return false;
        ctx.Host.Undo();
        return true;
    }

    private static bool HandleY(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.Y when ctrl && !ReadOnly
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        if (!ctrl || ctx.Host.ReadOnly)
            return false;
        ctx.Host.Redo();
        return true;
    }

    // ===== keyboard handlers (Insert/Delete + レガシー Copy/Paste/Cut ショートカット) =====

    /// <summary>
    /// Insert キー。元 switch の 3 段カスケード(case Keys.Insert when ctrl → case Keys.Insert
    /// when shift && !ReadOnly → case Keys.Insert)を等価に再現する。
    /// - Ctrl+Insert: Copy(ReadOnly でも動く=Notepad と同挙動)
    /// - Shift+Insert: Paste(ReadOnly で no-op=fallback へ落ちる)
    /// - bare Insert: 修飾なしのときのみ Overtype トグル(Alt+Insert 等は toggle しないが
    ///   <c>e.Handled=true</c> は常に立てる=元 case Keys.Insert: 経路の挙動)
    /// </summary>
    private static bool HandleInsert(InputContext ctx, KeyEventArgs e)
    {
        bool ctrl = (e.Modifiers & Keys.Control) != 0;
        bool shift = (e.Modifiers & Keys.Shift) != 0;
        if (ctrl)
        {
            // Ctrl+Insert = Copy(元 switch 上位 case)
            ctx.Host.Copy();
            return true;
        }
        if (shift && !ctx.Host.ReadOnly)
        {
            // Shift+Insert = Paste(元 switch 上位 case)
            ctx.Host.Paste();
            return true;
        }
        // 残り(bare Insert / Shift+Insert@ReadOnly / Alt+Insert 等)=元 case Keys.Insert:。
        // 修飾なしのみ Overtype トグル(if 条件は body 側)。Handled=true は常に立てる。
        if (!ctrl && !shift)
            ctx.Host.Overtype = !ctx.Host.Overtype;
        return true;
    }

    /// <summary>
    /// Delete キー。元 switch の 2 段カスケード(case Keys.Delete when shift && !ReadOnly →
    /// case Keys.Delete when !ReadOnly)を等価に再現する。ReadOnly なら unhandled(base 伝搬)。
    /// </summary>
    private static bool HandleDelete(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: ReadOnly=true のとき Shift+Delete も bare Delete も case マッチせず fallthrough
        if (ctx.Host.ReadOnly)
            return false;
        bool shift = (e.Modifiers & Keys.Shift) != 0;
        if (shift)
        {
            // Shift+Delete = Cut(元 switch 上位 case・Windows 3.x 互換のレガシーショートカット)
            ctx.Host.Cut();
            return true;
        }
        // 前方削除(元 case Keys.Delete when !ReadOnly)
        var buffer = ctx.Host.Buffer!;
        var snap = buffer.Current;
        var (s, en) = ctx.Host.GetSelectionCharRange();
        if (s != en)
        {
            buffer.Replace(s, en - s, "");
            ctx.Caret.SetTo(s, buffer.Current);
        }
        else if (ctx.Caret.Caret < snap.CharLength)
        {
            // MoveRightChar はサロゲートペアを 1 文字として右寄せする(caret+2 になる)。
            int next = NavigationCommands.MoveRightChar(snap, ctx.Caret.Caret);
            buffer.Delete(ctx.Caret.Caret, next - ctx.Caret.Caret);
        }
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.AfterEdit();
        return true;
    }

    // ===== keyboard handlers (editing: Back/Enter/Tab) =====

    private static bool HandleBack(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.Back when !ReadOnly
        if (ctx.Host.ReadOnly)
            return false;
        var buffer = ctx.Host.Buffer!;
        var snap = buffer.Current;
        var (s, en) = ctx.Host.GetSelectionCharRange();
        if (s != en)
        {
            buffer.Replace(s, en - s, "");
            ctx.Caret.SetTo(s, buffer.Current);
        }
        else if (ctx.Caret.Caret > 0)
        {
            // MoveLeftChar はサロゲートペアを 1 文字として左寄せする(caret-2 になる)。
            // switch 冒頭で捕獲した snap を使う(UI スレッド専用契約により Delete case と等価)。
            int start = NavigationCommands.MoveLeftChar(snap, ctx.Caret.Caret);
            buffer.Delete(start, ctx.Caret.Caret - start);
            ctx.Caret.SetTo(start, buffer.Current);
        }
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.AfterEdit();
        return true;
    }

    private static bool HandleEnter(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.Enter when !ReadOnly
        if (ctx.Host.ReadOnly)
            return false;
        string eol = ctx.Host.EolMode.ToEolString(); // "\r\n" / "\n" / "\r"
        var buffer = ctx.Host.Buffer!;
        var (s, en) = ctx.Host.GetSelectionCharRange();
        buffer.Replace(s, en - s, eol);
        ctx.Caret.SetTo(s + eol.Length, buffer.Current);
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.AfterEdit();
        return true;
    }

    private static bool HandleTab(InputContext ctx, KeyEventArgs e)
    {
        // 元 switch: case Keys.Tab when !ReadOnly
        // TabsToSpaces / TabWidth 対応は P6 送り(YAGNI・Task 9 は素の \t 挿入のみ)。
        if (ctx.Host.ReadOnly)
            return false;
        var buffer = ctx.Host.Buffer!;
        var (s, en) = ctx.Host.GetSelectionCharRange();
        buffer.Replace(s, en - s, "\t");
        ctx.Caret.SetTo(s + 1, buffer.Current);
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.AfterEdit();
        return true;
    }

    // ===== mouse handlers =====

    /// <summary>
    /// マウス左ボタン Down(元 OnMouseDown 本体・base.OnMouseDown / IsComposing チェックは
    /// EditorControl 側のラッパで先行実施済み)。
    /// </summary>
    private static void HandleMouseDown(InputContext ctx, MouseEventArgs e)
    {
        if (ctx.Host.Buffer is null || e.Button != MouseButtons.Left)
            return;
        ctx.Host.Focus();
        int target = ctx.Host.OffsetFromClientPoint(e.X, e.Y);
        bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
        if (shift)
            ctx.Host.MoveCaretWithSelection(target);
        else
            ctx.Host.SetCaretCharOffset(target);
        ctx.Host.MouseDragging = true;
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.Buffer.BreakUndoCoalescing();
        ctx.Host.BringCaretIntoView();
    }

    /// <summary>マウス移動(元 OnMouseMove 本体)。ドラッグ選択専用。</summary>
    private static void HandleMouseMove(InputContext ctx, MouseEventArgs e)
    {
        if (!ctx.Host.MouseDragging || ctx.Host.Buffer is null)
            return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            ctx.Host.MouseDragging = false;
            return;
        }
        int target = ctx.Host.OffsetFromClientPoint(e.X, e.Y);
        ctx.Host.MoveCaretWithSelection(target);
        ctx.Host.BringCaretIntoView();
    }

    /// <summary>マウス左ボタン Up(元 OnMouseUp 本体)。フラグ落としのみ。</summary>
    private static void HandleMouseUp(InputContext ctx, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        ctx.Host.MouseDragging = false;
    }

    /// <summary>マウス左ボタンダブルクリック(元 OnMouseDoubleClick 本体)。単語選択。</summary>
    private static void HandleMouseDoubleClick(InputContext ctx, MouseEventArgs e)
    {
        if (ctx.Host.Buffer is null || e.Button != MouseButtons.Left)
            return;
        int target = ctx.Host.OffsetFromClientPoint(e.X, e.Y);
        var snap = ctx.Host.Buffer.Current;
        // 2026-08-04: 走査上限つき。Ctrl+←→ / SR の読み上げスパン(UiaTextHostAdapter)と
        // 同じ cap を使う。ここだけ広げたくなるが(唯一データが欠ける経路なので)、分けると
        // 同じ位置で「見える選択」と「聞くスパン」が食い違い、F-3 の再導入になる。
        int start = WordBoundary.WordStart(snap, target, WordBoundary.DefaultMaxScan);
        int end = WordBoundary.WordEnd(snap, target, WordBoundary.DefaultMaxScan);
        ctx.Host.SetSelectionAnchored(start, end);
        ctx.Caret.DesiredXpx = -1;
        ctx.Host.Buffer.BreakUndoCoalescing();
        ctx.Host.BringCaretIntoView();
    }
}
