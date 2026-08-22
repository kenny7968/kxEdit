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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buffer is not null)
        {
            var snap = _buffer.Current;
            // Control.ClientSize は docked 子コントロールを引かないため、VScrollBar 幅・
            // HScrollBar 高さを明示的に減算。(ScrollableControl と違って Control は
            // DisplayRectangle でも同じ挙動)
            int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
            // 可視高さの定義は PaintHeightPx (EditorControl.Caret.cs) に一本化する。
            // ここで同じ式をコピーすると、UIA の GetVisibleCharRange / BringCaretIntoView /
            // ScrollCharRangeIntoView と「どこまで見えているか」の定義が食い違う。
            // ローカルへ 1 度だけ受けるのは、以降 2 箇所 (ViewportLayout.Build / FrameBuilder.Build)
            // で同一値を使うことを保証するため(式自体は移設前と同一=挙動不変)。
            int paintHeight = PaintHeightPx;
            var rows = ViewportLayout.Build(
                snap,
                _topLine,
                _topSegment,
                paintHeight,
                _wrapColumns,
                _metrics
            );
            int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;

            // 選択がある間は現在行強調 FillRect を抑止する(選択矩形と重ねると
            // ハイライトが二重になり視覚的に読みにくいため=EditorControl 層の責務)。
            bool hasSelection = _caretCtrl.HasSelection;
            int currentLineLogical =
                (_highlightCurrentLine && !hasSelection)
                    ? snap.GetLineIndexOfChar(_caretCtrl.Caret)
                    : -1;
            SelectionRange? selection = null;
            if (hasSelection)
            {
                var (selS, selE) = GetSelectionCharRange();
                selection = new SelectionRange(selS, selE);
            }

            var frame = FrameBuilder.Build(
                snap,
                rows,
                paintWidth,
                paintHeight,
                lnWidth,
                currentLineLogical,
                selection,
                _cellHighlight,
                ShowWhitespace,
                _style,
                _metrics
            );
            RenderFrame(g, frame);

            // P4 Task 9: 未確定文字列 overlay(本文 → cellHighlight → キャレット行強調 →
            // ここ → システムキャレット の順序=設計 §3-3)。IsComposing=false(未確定期間外)は
            // 呼ばない=空描画のコストゼロ。節ハイライト(反転)は Task 10・
            // IME 内キャレット位置反映は Task 11 で扱う。
            // Task 3a: 描画ロジックは ImeController.Draw に bit-perfect 移設済 (IImeOverlayHost 経由で
            // Font/Color/Metrics/ComputeCaretPoint を取得)。
            if (IsComposing)
                _imeCtrl.Draw(g);

            // P5 Task 10: 描画完了時点の Frame を UIA 座標 API 用に公開(不変参照)。
            _lastFrame = frame;
            // Task 3d: client→screen オフセットも念のため refresh(スクロールでウィンドウ位置が
            // 動かなくても、DPI 変化・親コントロール移動などで値が変わり得る)。Adapter へ委譲
            // (元 `_clientToScreenX = origin.X; _clientToScreenY = origin.Y;`)。
            _uia.RefreshClientToScreenOrigin();
        }
        // 本コントロールの描画を確定させた後に Paint イベント購読者に描かせる
        // (App 層の overlay 拡張余地を残す)。base.OnPaint は Paint イベントを発火する。
        base.OnPaint(e);
    }

    /// <summary>
    /// Frame の Ops を GDI 呼び出しに変換する。折り返し OFF 時の水平スクロール(<see cref="_scrollX"/>)は
    /// <b>全 op の X から一様に差し引く</b>形で反映する(_wrapColumns&gt;0 時は _scrollX=0 で実質シフトなし)。
    /// 先頭 op(背景全域 FillRect)も一緒にシフトされるが、OnPaint 冒頭で
    /// <c>g.Clear(BackColor)</c> が全 client 領域を BackColor で塗っており、DefaultStyle.Background
    /// と BackColor が一致している(共に White)ため、シフトで生じる右側の隙間は視覚的にクリアの
    /// BackColor と同色になり結果は同じ。行番号マージンも一緒にシフトされる(仕様=YAGNI)。
    /// </summary>
    private void RenderFrame(Graphics g, Frame frame)
    {
        foreach (var op in frame.Ops)
        {
            int x = op.X - _scrollX; // 一様シフト
            switch (op.Kind)
            {
                case PaintOpKind.FillRect:
                    using (var b = new SolidBrush(ToColor(op.Back)))
                        g.FillRectangle(b, x, op.Y, op.Width, op.Height);
                    break;
                case PaintOpKind.DrawText:
                    TextRenderer.DrawText(
                        g,
                        op.Text ?? string.Empty,
                        _font,
                        new Rectangle(x, op.Y, op.Width, op.Height),
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

    private static ViewportStyle DefaultStyle() =>
        new(
            Foreground: new PaintColor(0x000000),
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0xF0F0F0),
            SelectionBack: new PaintColor(0xADD8E6),
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
    /// 選択背景と枠色は現行 App 層と同じ固定値(P6 でテーマ拡張が入るなら再検討=Task 15 の申し送り参照)。
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
            SelectionBack: new PaintColor(0xADD8E6),
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
