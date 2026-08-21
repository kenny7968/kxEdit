using System.Text;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 現在開いているドキュメントの状態。v0.1 は単一だが、将来 DocumentManager で
/// タブ毎に持てるよう独立クラスにしておく（design M2 への布石）。
/// </summary>
public sealed class DocumentState
{
    public string? Path { get; set; } // 未保存なら null
    public int UntitledNumber { get; set; } // 無題タブの連番（Path 未確定時のみ表示に使う）
    public Encoding Encoding { get; set; } = EncodingCatalog.Get(65001);
    public bool HasBom { get; set; }
    public LineEnding LineEnding { get; set; } = LineEnding.Crlf;
    public bool CsvMode { get; set; } // CSV モード（タブ毎・既定 false）

    // CSV モード中の論理カーソル位置（0始まり）。モード中はここが真実源で、Scintilla の
    // システムキャレットは動かさない（SR の自動読み上げ二重発火を防ぐため）。モード ON 時に
    // その時点のキャレット位置から初期導出し、以降のセル移動でここだけを更新する。
    public int CsvRow { get; set; }
    public int CsvCol { get; set; }

    public string DisplayName =>
        Path is not null ? System.IO.Path.GetFileName(Path)
        : UntitledNumber > 0 ? $"無題 {UntitledNumber}"
        : "無題";
}
