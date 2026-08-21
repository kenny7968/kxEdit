namespace kxEdit.Core.Search;

/// <summary>
/// grep の 1 ヒット（＝1 マッチ行）。1 行に複数マッチがあっても行頭の最初のマッチを 1 件として持つ。
/// オフセットは UTF-16 文字位置（エディタの string index・SelectCharRange と同一空間）。
/// </summary>
public sealed record GrepHit(
    string FilePath, // 絶対パス
    int LineNumber, // 1 始まり
    int Column, // 1 始まり（行内 UTF-16 桁・最初のマッチ）
    string LineText, // 行内容（EOL 除外・表示用）
    int MatchStartInLine, // 行内 UTF-16 オフセット（0 始まり）
    int MatchLength, // マッチ長（UTF-16）
    int AbsoluteOffset
); // ファイル先頭からの UTF-16 オフセット（ジャンプ用）

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
