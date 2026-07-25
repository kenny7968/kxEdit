namespace yEdit.Core.Documents;

/// <summary>
/// ファイル属性(作成/更新/サイズ)の値型。App 側 FileMetaProvider が構築し Builder に注入する
/// (Core は File I/O に触れない=依存方向の分離)。
/// 本型を包む <c>FileMeta?</c> の null は「未保存」または「取得失敗」を等しく意味する
/// (Formatter は区別せず「-」表示)。
/// </summary>
public readonly record struct FileMeta(DateTime CreationTime, DateTime LastWriteTime, long Length);
