using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Text;

/// <summary>
/// P6 Task 10: Stream I/O 経路の読込結果(<see cref="TextFileService.LoadAsBufferAuto"/> の返却型)。
/// 既存 <see cref="LoadedDocument"/> と対を成し、本文表現が <c>string</c> ではなく
/// <see cref="TextBuffer"/>(=チャンク木)である点が違い。1GB 級 UTF-8 の string 全文化を回避する。
/// </summary>
public sealed class LoadedBuffer
{
    /// <summary>読み込んだ本文(チャンク木構築済み)。</summary>
    public required TextBuffer Buffer { get; init; }

    /// <summary>
    /// 確定した文字コード。<see cref="TextFileService.LoadAsBufferAuto"/> は先頭 64KB を prefix として
    /// <see cref="EncodingDetector.Detect"/> に渡す=旧 <see cref="LoadedDocument.Encoding"/>
    /// (全 byte 判定)と検出差が出る稀なケース(先頭 ASCII で始まり途中から SJIS/EUC-JP に切替、
    /// UtfUnknown の統計判定が prefix 内では信頼度不足になる等)は、Stream I/O 化のトレードオフ
    /// として受容する。取り違えは <see cref="HadReplacementChar"/> で警告可能。
    /// </summary>
    public required Encoding Encoding { get; init; }

    /// <summary>UTF-8 BOM を持つファイルなら true(UTF-8 以外では常に false)。</summary>
    public required bool HasBom { get; init; }

    /// <summary>
    /// 検出した改行種別(本文全体の byte 走査による多数決=<see cref="LineEndingDetector.Detect(Buffers.TextSnapshot)"/>)。
    /// A-9(2026-08-28)以前は先頭 4KB の decoded prefix だけを見ていたが、1 行目が窓より長い
    /// LF / CR ファイル(ミニファイ JSON・長いヘッダ行の CSV)が改行 0 件と見なされて CRLF 既定へ倒れ、
    /// 保存時に全行が無警告で書き換わっていた。窓は設けず PieceTree を byte 走査する
    /// (string を実体化しないので 1GB 級でもピークメモリは増えない)。
    /// </summary>
    public required LineEnding LineEnding { get; init; }

    /// <summary>デコード時に U+FFFD(置換文字)が出たか(文字コード取り違えの示唆)。</summary>
    public required bool HadReplacementChar { get; init; }
}
