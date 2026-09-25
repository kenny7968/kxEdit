// EditorControl.Paint.cs
// Phase 2 (Task 2e) で切り出した描画分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3 Task 3a で DrawImeOverlay を ImeController.Draw() に移設 (IImeOverlayHost seam 経由・
// 元の bit-perfect 描画コードは ImeController.cs 側に存在)。本ファイルは OnPaint / RenderFrame /
// style/color helpers を担う。
using System.Globalization;
using kxEdit.Core.Layout;
using kxEdit.Core.Settings;
using SelectionRange = kxEdit.Core.Layout.SelectionRange;

namespace kxEdit.Editor;

public sealed partial class EditorControl
{
    // OnPaint + RenderFrame + IME overlay 描画 + style/color helpers

    // 2026-09-24 性能改善フェーズ 1(P-20): WinForms の OptimizedDoubleBuffer は既定の
    // MaximumBuffer(225×96)を超える面積で、描画のたびに一時コンテキストと DIB を作って捨てる。
    // MaximumBuffer を画面サイズにした専用のコンテキストを使い回す。
    // ・プロセス全体の BufferedGraphicsManager.Current は変えない(他コントロールへの影響が読めない)。
    // ・[ThreadStatic] にするのは、BufferedGraphicsContext がスレッド安全でなく、テストは STA スレッドを
    //   並行に立てるため。製品は UI スレッド 1 本なので、タブ数によらず 1 個になる。
    // ・コンテキスト(と DIB)はスレッドの寿命まで持ち続け、Dispose しない(意図的)。
    //   使い回しが目的であり、製品では UI スレッド = プロセスの寿命と一致する。
    // ・VirtualScreen がモニタの抜き差しで広がっても、Allocate は MaximumBuffer を超える分を
    //   一時バッファに逃がすだけで、描画結果は変わらない(遅くなるだけ)。
    [ThreadStatic]
    private static BufferedGraphicsContext? t_paintBuffer;

    private static BufferedGraphicsContext PaintBuffer =>
        t_paintBuffer ??= new BufferedGraphicsContext
        {
            MaximumBuffer = SystemInformation.VirtualScreen.Size,
        };

    protected override void OnPaint(PaintEventArgs e)
    {
        // 更新矩形が空なら何もしない(Paint イベントも発火しない)。旧 WmPaint(OptimizedDoubleBuffer)は
        // 更新矩形が空なら OnPaint 自体を呼ばずに return していたので、それに揃える
        // (クライアントに面積があって更新領域だけが空の WM_PAINT = RDW_INTERNALPAINT 等で、
        // PaintBody・_lastFrame の更新・Paint イベントを走らせない)。OnPrint 経路のクリップは
        // ClientRectangle なので、クライアント面積 0 もここで返る = 面積 0 の DIB は作らない。
        var clip = e.ClipRectangle;
        if (clip.Width <= 0 || clip.Height <= 0)
            return;

        // Allocate が失敗したときはバッファなしで直接描く(TryAllocatePaintBuffer の注記を参照)。
        var buffer = TryAllocatePaintBuffer(e.Graphics, ClientRectangle);
        if (buffer is null)
        {
            PaintAndRecord(e.Graphics);
        }
        else
        {
            using (buffer)
            {
                // 旧 OptimizedDoubleBuffer と同じく、バッファ側の Graphics にも更新領域のクリップを掛ける
                // (WinForms の WmPaint はバッファの Graphics に SetClip(clip) してから描かせていた)。
                // Allocate が返す Graphics は target のクリップを引き継がない。画面への反映範囲は
                // Render の BitBlt が BeginPaint の DC のクリップで絞られるので、これが無くても画素は
                // 変わらないが、GDI+ / GDI の描画量と Graphics の状態を従来と揃えておく。
                buffer.Graphics.SetClip(clip);
                PaintAndRecord(buffer.Graphics);
                buffer.Render(e.Graphics);
            }
        }
        // 本コントロールの描画を確定させた後に Paint イベント購読者に描かせる
        // (App 層の overlay 拡張余地を残す)。base.OnPaint は Paint イベントを発火する。
        // P-20 以降、購読者の e.Graphics はバッファではなく描画先(画面の DC 等)になる。
        base.OnPaint(e);
    }

    /// <summary>
    /// バックバッファを確保する。失敗したら null を返し、呼び出し側はバッファなしで直接描く。
    /// 旧 Control.WmPaint(OptimizedDoubleBuffer)は Allocate を try で囲み、致命的でない例外と
    /// OutOfMemoryException のときバッファなしの描画に切り替えていた(GDI 資源の枯渇等)。
    /// ここで例外を外へ出すと PaintWithErrorHandling が描画失敗の印を立て、以後ずっと赤い × に
    /// なるため、同じ判定で退避する。catch するのは Allocate だけ(PaintBody / Render の例外は従来どおり外へ)。
    /// 判定(<see cref="IsCriticalForPaintFallback"/>)は System.ExceptionExtensions.IsCriticalException
    /// (System.Private.Windows.Core。9.0.20 の IL で確認)の 6 型(NullReference・StackOverflow・
    /// OutOfMemory・ThreadAbort・IndexOutOfRange・AccessViolation)から OutOfMemory を除いたもの。
    /// 旧 WmPaint の <c>!IsCritical || ex is OutOfMemoryException</c> と同値。
    /// </summary>
    private static BufferedGraphics? TryAllocatePaintBuffer(Graphics target, Rectangle bounds)
    {
        try
        {
            return PaintBuffer.Allocate(target, bounds);
        }
        catch (Exception ex) when (!IsCriticalForPaintFallback(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// バックバッファ確保の失敗を退避せず外へ出すべき(致命的)例外なら true。
    /// System.ExceptionExtensions.IsCriticalException の 6 型から OutOfMemoryException を除いたもの
    /// (旧 WmPaint の <c>!IsCritical || ex is OutOfMemoryException</c> を裏返した判定)。
    /// 表形式のテスト(EditorControlPaintCostTests)で型ごとの判定を固定するため internal。
    /// </summary>
    internal static bool IsCriticalForPaintFallback(Exception ex) =>
        ex
            is NullReferenceException
                or StackOverflowException
                or ThreadAbortException
                or IndexOutOfRangeException
                or AccessViolationException;

    /// <summary>
    /// 今の状態を集めて描き、描き終えた入力を <see cref="_lastPaintedInputs"/> に記録する(OnPaint の本体)。
    /// 描く前に記録を捨てる=描画が例外で抜けたら記録は null のまま(次の比較は必ず「変化あり」)。
    /// WM_PRINT 経由(DrawToBitmap / PrintWindow)でも記録してよい根拠は
    /// 実装計画 docs/plans/2026-09-25-perf-skip-invalidate.md §0.2。
    /// </summary>
    private void PaintAndRecord(Graphics g)
    {
        _lastPaintedInputs = null;
        var inputs = CaptureFrameInputs();
        var frame = PaintBody(g, inputs, BackColor, _imeCtrl);
        // テスト観測用(TestHook_GetLastFrame)。SetSource 前(frame が null)は従来どおり更新しない。
        if (frame is not null)
            _lastFrame = frame;
        _lastPaintedInputs = inputs;
    }

    /// <summary>
    /// 今の描画の入力が、最後に描いたフレームの入力と異なるときだけ Invalidate する(設計書 §8.2)。
    /// キャレット・選択の 4 経路(EditorControl.Caret.cs)専用。
    /// </summary>
    /// <remarks>
    /// 正しさの根拠: 画面に出ているのは <see cref="_lastPaintedInputs"/> から決定的に描いた絵である。
    /// 今の入力が同じなら、描き直しても同じ絵になる。未処理の無効領域がある場合でも、その描画は
    /// 今の入力で描かれる。スクロールのセッターは自前で無条件に Invalidate するので、この比較の外にある。
    /// 他の Invalidate(編集・IME・外観・CSV 強調・スクロール・リサイズ)は無条件のまま(変更範囲を最小にする)。
    /// </remarks>
    private void InvalidateIfFrameChanged()
    {
        var current = CaptureFrameInputs();
        if (current is not null && current.Equals(_lastPaintedInputs))
            return;
        Invalidate();
    }

    /// <summary>
    /// 本文・フォント・テーマを丸ごと差し替える経路の Invalidate。記録を捨てて、古いスナップショットや
    /// フォント・幅メモを次の描画まで握らない(描画されないタブで起きても解放される)。
    /// 捨てた後の比較は必ず「変化あり」になる(安全側)。
    /// </summary>
    private void InvalidateAndForgetPaintedFrame()
    {
        _lastPaintedInputs = null;
        Invalidate();
    }

    /// <summary>
    /// 描画が読む状態を集める唯一の場所(設計書 §8.1)。SetSource 前は null。
    /// 描画(<see cref="PaintBody"/>)は、ここで集めた値<b>だけ</b>を使う
    /// (例外は IME の未確定表示。<see cref="FrameInputs"/> の remarks を参照)。
    /// </summary>
    private FrameInputs? CaptureFrameInputs()
    {
        if (_buffer is null)
            return null;
        var snap = _buffer.Current;
        // 選択がある間は現在行強調 FillRect を抑止する(選択矩形と重ねると
        // ハイライトが二重になり視覚的に読みにくいため=EditorControl 層の責務)。
        bool hasSelection = _caretCtrl.HasSelection;
        SelectionRange? selection = null;
        if (hasSelection)
        {
            var (selS, selE) = GetSelectionCharRange();
            selection = new SelectionRange(selS, selE);
        }
        var clientSize = ClientSize;
        return new FrameInputs
        {
            Snapshot = snap,
            TopLine = _topLine,
            TopSegment = _topSegment,
            ScrollX = _scrollX,
            WrapColumns = _wrapColumns,
            ClientSize = clientSize,
            // Control.ClientSize は docked 子コントロールを引かないため、VScrollBar 幅・
            // HScrollBar 高さを明示的に減算。(ScrollableControl と違って Control は
            // DisplayRectangle でも同じ挙動)
            PaintWidth = Math.Max(0, clientSize.Width - _vscroll.Width),
            // 可視高さの定義は PaintHeightPx (EditorControl.Caret.cs) に一本化する。
            // ここで同じ式をコピーすると、UIA の GetVisibleCharRange / BringCaretIntoView /
            // ScrollCharRangeIntoView と「どこまで見えているか」の定義が食い違う。
            // FrameInputs に 1 度だけ受けるのは、以降 2 箇所 (BuildVisibleRows / FrameBuilder.Build)
            // で同一値を使うことを保証するため(式自体は移設前と同一=挙動不変)。
            PaintHeight = PaintHeightPx,
            ShowLineNumbers = _showLineNumbers,
            LineNumberWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0,
            CurrentLineLogical =
                (_highlightCurrentLine && !hasSelection)
                    ? snap.GetLineIndexOfChar(_caretCtrl.Caret)
                    : -1,
            Selection = selection,
            CellHighlight = _cellHighlight,
            ShowWhitespace = _showWhitespace,
            Style = _style,
            Metrics = _metrics,
            Font = _font,
            UnderlineFont = _underlineFontCache,
            TargetFont = _targetFontCache,
            BackColor = BackColor,
            Ime = _imeCtrl.State,
        };
    }

    /// <summary>
    /// <paramref name="inputs"/> <b>だけ</b>からフレームを組み立てて描き、描いたフレームを返す。
    /// <paramref name="inputs"/> が null(SetSource 前)なら <paramref name="emptyBackColor"/> で塗るだけで null を返す。
    /// </summary>
    /// <remarks>
    /// static にして、生の状態への出口を引数だけに限る(描画が FrameInputs の外の状態を読めないことを
    /// コンパイラで保証する)。<paramref name="ime"/> が唯一の例外で、<see cref="ImeController.Draw"/> は
    /// host 経由で生の状態を読む(<see cref="FrameInputs"/> の remarks)。
    /// <paramref name="emptyBackColor"/> は inputs が null のときだけ使う。
    /// </remarks>
    private static Frame? PaintBody(
        Graphics g,
        FrameInputs? inputs,
        Color emptyBackColor,
        ImeController ime
    )
    {
        // 2026-09-24 性能改善フェーズ 1(P-17): ControlStyles.Opaque で背景層(OnPaintBackground)を
        // 省いたため、この行が client 全面を下塗りする唯一の箇所になった。FrameBuilder の工程 1
        // (背景全域 FillRect)があっても消さない: RenderFrame の scrollX シフトで右端に生じる隙間は、
        // この塗りしか覆わない。_buffer が null(ソース未設定 = inputs が null)の間も、この行が空の
        // コントロールを BackColor(emptyBackColor)で塗る。なお右下の角は VScrollBar(高さいっぱいに
        // dock する子)が覆っており、この行の役目ではない(ctor の Dock 順の注意を参照)。
        g.Clear(inputs?.BackColor ?? emptyBackColor);
        if (inputs is null)
            return null;

        // 起点 (TopLine, TopSegment)・折り返し設定・可視高さは BuildVisibleRows に集約する
        // (2026-08-22 A-6)。GetVisibleCharRange の doc が言う「描画と同じ Build を使う」を
        // 言葉の約束ではなく呼び出しの共有にする=片側だけ起点がずれる変異が成立しなくなる。
        var frame = FrameBuilder.Build(
            inputs.Snapshot,
            BuildVisibleRows(inputs),
            inputs.PaintWidth,
            inputs.PaintHeight,
            inputs.LineNumberWidth,
            inputs.CurrentLineLogical,
            inputs.Selection,
            inputs.CellHighlight,
            inputs.ShowWhitespace,
            inputs.Style,
            inputs.Metrics
        );
        RenderFrame(g, frame, inputs.ScrollX, inputs.Font);

        // P4 Task 9: 未確定文字列 overlay(本文 → cellHighlight → キャレット行強調 →
        // ここ → システムキャレット の順序=設計 §3-3)。未確定期間外は
        // 呼ばない=空描画のコストゼロ。節ハイライト(反転)は Task 10・
        // IME 内キャレット位置反映は Task 11 で扱う。
        // Task 3a: 描画ロジックは ImeController.Draw に bit-perfect 移設済 (IImeOverlayHost 経由で
        // Font/Color/Metrics/ComputeCaretPoint を取得)。
        // 2026-09-25 フェーズ 3: ImeController.Draw は host 経由で生の状態を読む(FrameInputs の remarks)。
        if (inputs.Ime.IsActive)
            ime.Draw(g);

        return frame;
    }

    /// <summary>
    /// Frame の Ops を GDI 呼び出しに変換する。折り返し OFF 時の水平スクロール(<paramref name="scrollX"/>)は
    /// <b>全 op の X から一様に差し引く</b>形で反映する(<c>WrapColumns</c>&gt;0 時は scrollX=0 で実質シフトなし)。
    /// 先頭 op(背景全域 FillRect)も一緒にシフトされるが、PaintBody 冒頭で
    /// <c>g.Clear(inputs.BackColor)</c> が全 client 領域を塗っており、<c>Style.Background</c>
    /// と <c>inputs.BackColor</c> が一致している(ctor は共に White、ApplyAppearance は同じテーマ色から両方を設定する)ため、
    /// シフトで生じる右側の隙間は視覚的にクリアの色と同色になり結果は同じ。行番号マージンも一緒にシフトされる(仕様=YAGNI)。
    /// </summary>
    private static void RenderFrame(Graphics g, Frame frame, int scrollX, Font font)
    {
        foreach (var op in frame.Ops)
        {
            int x = op.X - scrollX; // 一様シフト
            switch (op.Kind)
            {
                case PaintOpKind.FillRect:
                    using (var b = new SolidBrush(ToColor(op.Back)))
                        g.FillRectangle(b, x, op.Y, op.Width, op.Height);
                    break;
                case PaintOpKind.DrawText:
                    // 矩形の幅は「レイアウト上この run が占める幅」(op.Width)ではなく、右端までを
                    // 与える。TextFormatFlags に NoClipping が無いため矩形幅はクリップ幅として
                    // 効き、op.Width をそのまま渡すと文字の右端が削れることがある:
                    // 選択境界で分割された本文 run の op.Width は PixelMapper.OffsetToPx の差分
                    // (行頭からの prefix 計測の引き算)であって、その run 単独を測った幅ではない。
                    // ICharMetrics.MeasureRun は非 ASCII を含む run を一括計測して加算的でないので、
                    // run 単独の実描画幅が差分を上回り得る(結合文字のように advance 0 の
                    // コードポイントだけが区間に入ると差分が 0 になり、一切描かれない)。
                    // 縦は従来どおり op.Height(行高)でクリップする=背の高いグリフが行間へ
                    // にじむ従来の挙動を変えないため。Left 揃えなので開始位置は x のまま。
                    int textClipWidth = Math.Max(op.Width, frame.ClientWidth - x);
                    TextRenderer.DrawText(
                        g,
                        op.Text ?? string.Empty,
                        font,
                        new Rectangle(x, op.Y, textClipWidth, op.Height),
                        ToColor(op.Fore),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left
                    );
                    break;
                case PaintOpKind.DrawLine:
                    using (var p = new Pen(ToColor(op.Fore)))
                        g.DrawLine(p, x, op.Y, x + op.Width, op.Y + op.Height);
                    break;
            }
        }
    }

    // Task 8 で本実装。現状はダミー: 桁数(下限 3) × '9' 幅 + 4px 余白
    private int MeasureLineNumberWidth(int lineCount)
    {
        int digits = Math.Max(3, lineCount.ToString(CultureInfo.InvariantCulture).Length);
        return _metrics.MeasureRun(new string('9', digits)) + 4;
    }

    private static Color ToColor(PaintColor c) =>
        Color.FromArgb(c.Alpha, (c.Rgb >> 16) & 0xFF, (c.Rgb >> 8) & 0xFF, c.Rgb & 0xFF);

    /// <summary>
    /// <c>_style</c> を直接読む唯一の窓口 (TestHook_* 規約)。
    /// <see cref="IImeOverlayHost"/> 経由では <see cref="ViewportStyle.SelectionFore"/> の
    /// <b>null 性が <c>?? Foreground</c> で潰れて観測できない</b>
    /// (標準テーマは SelectionFore も Foreground も黒なので seam の値が同じになる)。
    /// 「標準テーマは選択文字色を持たない = 本文 op を分割しない = 描画完全不変」という
    /// 2026-09-14 設計書 §5.2 の不変条件は Editor 層でここからしか固定できない。
    /// </summary>
    internal static ViewportStyle TestHook_ViewportStyle(EditorControl c) => c._style;

    /// <summary>
    /// テスト専用: クライアント領域の大きさのビットマップに、OnPaint と同じ経路で描く。
    /// <paramref name="record"/> が true なら「WM_PAINT で描いた」扱いで <see cref="_lastPaintedInputs"/> を
    /// 記録する(<see cref="PaintAndRecord"/>)。false なら記録せずに今の状態を描く(オラクルの正解。
    /// <see cref="_lastPaintedInputs"/> も <see cref="_lastFrame"/> も書き換えない)。
    /// 画面外の HostForm には WM_PAINT が来ないので、描画とその記録はこれで同期的に起こす。
    /// </summary>
    internal static Bitmap TestHook_PaintToBitmap(EditorControl c, bool record)
    {
        var size = c.ClientSize;
        var bmp = new Bitmap(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb
        );
        using var g = Graphics.FromImage(bmp);
        if (record)
            c.PaintAndRecord(g);
        else
            PaintBody(g, c.CaptureFrameInputs(), c.BackColor, c._imeCtrl);
        return bmp;
    }

    /// <summary>テスト専用: 最後に描いたフレームの入力を持っているか。</summary>
    internal static bool TestHook_HasLastPaintedInputs(EditorControl c) =>
        c._lastPaintedInputs is not null;

    private static ViewportStyle DefaultStyle() =>
        new(
            Foreground: new PaintColor(0x000000),
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0xF0F0F0),
            // 標準テーマ(AppearanceThemes の "default" 行)と同値。ApplyAppearance 前の暫定値。
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: null,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xD77800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );

    /// <summary>
    /// <see cref="AppearanceTheme"/> の Fore/Back RGB から派生色を算出して <see cref="ViewportStyle"/> を組む。
    /// App 層 <c>EditorAppearance</c> の <c>Blend</c> の手法(Back と Fore の線形補間)を流用しつつ、
    /// 各成分の混色比は以下の内訳:
    /// - CurrentLineBack (ratio=0.12): App 層と厳密一致(移植)
    /// - LineNumberFore (ratio=0.5) / WhitespaceGlyph (ratio=0.3): 自作コントロール独自の派生
    ///   (App 層は Scintilla の既定色を使うため直接の対応値なし)
    /// 強調 OFF 時の CurrentLineBack は Alpha=0 で「未使用」を明示。
    /// 選択色(背景・文字色)は <see cref="AppearanceTheme"/> の表から取る(2026-09-14 設計書 §5.1)。
    /// 枠色は現行 App 層と同じ固定値。
    /// </summary>
    private static ViewportStyle BuildStyle(AppearanceTheme theme, bool highlightCurrentLine)
    {
        var currentLineBack = highlightCurrentLine
            ? new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.12))
            : new PaintColor(0, 0);
        return new ViewportStyle(
            Foreground: new PaintColor(theme.ForeRgb),
            Background: new PaintColor(theme.BackRgb),
            CurrentLineBack: currentLineBack,
            SelectionBack: new PaintColor(theme.SelectionBackRgb),
            SelectionFore: theme.SelectionForeRgb is int selFore ? new PaintColor(selFore) : null,
            LineNumberFore: new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.5)),
            HighlightOutline: new PaintColor(0xD77800),
            WhitespaceGlyph: new PaintColor(BlendRgb(theme.BackRgb, theme.ForeRgb, 0.3))
        );
    }

    /// <summary>
    /// 0xRRGGBB 形式の 2 色を各チャネルで線形補間する(ratio=0 で baseRgb・1 で accentRgb)。
    /// App 層 <c>EditorAppearance.Blend</c> のロジック移植(あちらは Color 型・こちらは int 型)。
    /// </summary>
    private static int BlendRgb(int baseRgb, int accentRgb, double ratio)
    {
        int r = BlendChannel((baseRgb >> 16) & 0xFF, (accentRgb >> 16) & 0xFF, ratio);
        int g = BlendChannel((baseRgb >> 8) & 0xFF, (accentRgb >> 8) & 0xFF, ratio);
        int b = BlendChannel(baseRgb & 0xFF, accentRgb & 0xFF, ratio);
        return (r << 16) | (g << 8) | b;
        static int BlendChannel(int a, int c, double r) => (int)Math.Round(a + (c - a) * r);
    }

    /// <summary>0xRRGGBB の int から Alpha=255 の <see cref="Color"/> を組む(BackColor 設定用)。</summary>
    private static Color FromRgb(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
