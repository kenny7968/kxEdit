using kxEdit.Core.Settings;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// App 層の設定オブジェクト（<see cref="AppSettings"/>）を <see cref="EditorControl.ApplyAppearance"/> へ受け渡す薄い Adapter。
/// フォント・配色テーマ・キャレット幅・行番号表示・空白可視化・現在行強調・表示折り返しの反映は EditorControl 側に集約されており、
/// 本クラスは呼び出しの一本化のみを担う。
/// </summary>
public static class EditorAppearance
{
    public static void Apply(EditorControl ed, AppSettings settings) =>
        ed.ApplyAppearance(settings);
}
