using System.Text;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 現在開いているドキュメントの状態。v0.1 は単一だが、将来 DocumentManager で
/// タブ毎に持てるよう独立クラスにしておく（design M2 への布石）。
/// </summary>
public sealed class DocumentState
{
    private string? _path;

    /// <summary>
    /// <see cref="Path"/> の不変条件違反を報せる <c>Debug.Assert</c> メッセージ。
    /// </summary>
    /// <remarks>
    /// 3 引数版の <c>Debug.Assert</c> を使うのは、2 引数版の message が
    /// <c>[CallerArgumentExpression]</c> 付きで明示指定が S3236 になるため
    /// (<c>WordBoundary.MaxScanContract</c> / <c>TextSnapshot.DecodeUtf16At</c> と同じ流儀)。
    /// </remarks>
    private const string PathContract =
        "State.Path は null か正規化済み絶対パスであること(Issue #48 / 設計書 §3.1)";

    /// <summary>
    /// 未保存なら null。非 null のときは<b>正規化済みの絶対パス</b>
    /// (Issue #48 / 設計書 §3.1 の不変条件)。
    /// <c>DocumentManager.FindByPath</c> と <c>RecentFilesList.Add</c> は、この不変条件に
    /// 依拠して <c>PathKey.ForNormalized</c>(ファイルシステム非依存)で比較する。
    /// ここに未正規化パスが入ると、同一ファイルの重複タブ検知(A-7 (b))がすり抜ける。
    /// </summary>
    public string? Path
    {
        get => _path;
        set
        {
            // I/O を伴わない構造チェック。IsPathFullyQualified は純粋な文字列判定で、
            // 相対パス(= A-19 の再発)を Debug ビルドで捕まえる。
            // **捕まえられるのは「絶対でない」ことだけ**で、綴りが canonical かどうか
            // (区切りの揺れ・`..` の残り・8.3 短縮名)は見ない。それを見るには
            // GetFullPath が要り、S-15 の 21 秒をここへ持ち込むことになる。
            // Release では消える(= 本番の経路を守るのは呼出側の境界付き正規化のほう)。
            System.Diagnostics.Debug.Assert(
                value is null || System.IO.Path.IsPathFullyQualified(value),
                PathContract,
                nameof(Path)
            );
            _path = value;
        }
    }

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

    /// <summary>
    /// M-18(設計 2026-09-03 §3.1): 本文がディスクと一致していた(と kxEdit が信じている)時点の
    /// ディスク側 LastWriteTimeUtc。無題・取得失敗(到達不能・権限)は null = 判定しない。
    /// 開くときは本文を読む<b>前</b>に、保存は書いた<b>後</b>に取る(§3.2)。
    /// 比較は完全一致(同じ FS が同じファイルに返す値同士なので許容差を置かない。§3.3)。
    /// </summary>
    public DateTime? LastKnownWriteTimeUtc { get; set; }

    /// <summary>
    /// M-18: 「読み直さない」と答えたときのディスク側 LastWriteTimeUtc。ディスクがこの値のままの
    /// あいだは復帰・タブ切替で聞き直さない。<b>本文の基準ではない</b> —— 保存直前の上書き確認は
    /// <see cref="LastKnownWriteTimeUtc"/> だけを見るので、「読み直さない」のあとの Ctrl+S でも
    /// 相手の変更を無言で上書きしない。本文の基準が更新される(開く・読み直す・保存する)たびに null へ戻す。
    /// </summary>
    public DateTime? AcknowledgedWriteTimeUtc { get; set; }

    public string DisplayName =>
        Path is not null ? System.IO.Path.GetFileName(Path)
        : UntitledNumber > 0 ? $"無題 {UntitledNumber}"
        : "無題";
}
