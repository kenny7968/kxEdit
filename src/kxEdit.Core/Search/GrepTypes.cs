namespace kxEdit.Core.Search;

/// <summary>
/// grep の 1 ヒット（＝1 マッチ行）。1 行に複数マッチがあっても行頭の最初のマッチを 1 件として持つ。
/// オフセットはいずれも UTF-16 文字位置。
/// </summary>
/// <remarks>
/// <b>A-18(2026-08-31)</b>: 旧 doc は「エディタの string index・SelectCharRange と同一空間」と
/// 書いていたが、これは<b>偽の不変条件</b>だった。<see cref="AbsoluteOffset"/> は
/// <b>ディスク上のバイト列を復号した空間</b>の値で、未保存編集のあるタブのバッファと
/// <b>一致する保証がない</b>(ヒットより後ろだけを編集した場合など、たまたま一致することはある。
/// エディタと grep で文字コード判定の窓も違う)。ジャンプ先の解決には
/// <c>GrepJumpResolver.Resolve</c> を使い、<see cref="AbsoluteOffset"/> を選択位置へ流用しないこと。
/// <para>
/// <b>producer への要求(load-bearing・belt ではない)</b>:
/// <see cref="MatchStartInLine"/> / <see cref="MatchLength"/> は <see cref="LineText"/> の内側に
/// 収まること(<c>MatchStartInLine + MatchLength &lt;= LineText.Length</c>)。
/// <c>GrepJumpResolver.Land</c> はこれを前提に<b>行内クランプを省いて</b>いるので、破ると選択末尾が
/// 次行へ食み出し、<c>SetSelectionCharRange</c> が <c>Caret = Max(start, end)</c> にマップする結果
/// <c>CurrentLine</c> が選択末尾の行を返し、<b>着地行と違う行番号を発声する</b>(= A-18 の再発)。
/// 設計書 §2.1 / §6 の「grep の入口をバッファ基準にする」案は<b>2 つ目の producer</b> を作る変更に
/// なるため、そのときはこの制約を満たすこと。
/// </para>
/// </remarks>
public sealed record GrepHit(
    string FilePath, // 絶対パス
    int LineNumber, // 1 始まり
    int Column, // 1 始まり（行内 UTF-16 桁・最初のマッチ）
    string LineText, // 行内容（EOL 除外・表示用 / A-18 の照合キー）
    int MatchStartInLine, // 行内 UTF-16 オフセット（0 始まり）
    int MatchLength, // マッチ長（UTF-16）
    int AbsoluteOffset
); // ファイル先頭からの UTF-16 オフセット（ディスク基準・ジャンプには使わない=A-18）

/// <summary>grep の要求。FilePatterns は ";"/"," 区切りの glob（空＝全ファイル）。</summary>
public sealed record GrepRequest(
    string Folder,
    string FilePatterns,
    bool Recursive,
    SearchOptions Options
);

/// <summary>読めなかった/対象外になったファイル・ディレクトリの記録（握り潰さず一覧化）。</summary>
public sealed record GrepError(string Path, string Message);

/// <summary>走査進捗（一定間隔で通知）。CurrentFile は最後に走査したファイル。</summary>
public sealed record GrepProgress(int FilesScanned, int HitCount, string? CurrentFile);

/// <summary>
/// grep の結果一式。Cancelled=true は協調キャンセルで途中打ち切り（Hits は途中までの部分結果）。
/// FilesMatched は 1 件以上ヒットしたファイル数。
/// </summary>
public sealed record GrepOutcome(
    IReadOnlyList<GrepHit> Hits,
    int FilesScanned,
    int FilesMatched,
    IReadOnlyList<GrepError> Errors,
    bool Cancelled
);
