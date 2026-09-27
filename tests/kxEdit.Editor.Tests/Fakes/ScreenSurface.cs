using System.Drawing;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// オラクル用の「画面」(フェーズ 9b)。<see cref="IPaintSurface.TryScroll"/> で画素を実際に動かし、
/// 露出した帯は古い画素のまま残す(実画面の ScrollWindowEx と同じ = 描き直さなければ古い絵が残る)。
/// <see cref="Pending"/> は「この操作で無効化が起きた」(= 保留中の無効領域がある)を表し、テストが立てる。
/// </summary>
internal sealed class ScreenSurface(int width, int height) : IPaintSurface
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int[] Pixels { get; set; } = new int[width * height];
    public bool Pending { get; set; }
    public int Scrolls { get; private set; }

    public bool CanScroll(EditorControl editor) => !Pending;

    public bool TryScroll(
        EditorControl editor,
        int dx,
        int dy,
        Rectangle area,
        out Rectangle uncovered
    )
    {
        var copy = (int[])Pixels.Clone();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int sx = x - dx,
                    sy = y - dy;
                if (area.Contains(sx, sy))
                    Pixels[y * Width + x] = copy[sy * Width + sx];
            }
        }
        uncovered =
            dy < 0 ? Rectangle.FromLTRB(area.Left, area.Bottom + dy, area.Right, area.Bottom)
            : dy > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Top + dy)
            : dx < 0 ? Rectangle.FromLTRB(area.Right + dx, area.Top, area.Right, area.Bottom)
            : Rectangle.FromLTRB(area.Left, area.Top, area.Left + dx, area.Bottom);
        Scrolls++;
        return true;
    }
}
