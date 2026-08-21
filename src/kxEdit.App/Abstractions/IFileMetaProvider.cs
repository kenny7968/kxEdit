using kxEdit.Core.Documents;

namespace kxEdit.App;

/// <summary>
/// path から <see cref="FileMeta"/>(作成日時/更新日時/サイズ)を取り出す seam。
/// 実 File I/O を Core とテストから分離するために用意する(App.Tests では stub 差し替え)。
/// 取得できない場合は null を返す契約=呼び出し側は「未保存」と「取得失敗」を区別しない
/// (Formatter がどちらも「-」に落とす)。
/// </summary>
public interface IFileMetaProvider
{
    /// <summary>path のファイル属性。path が null / ファイルが存在しない / 取得に失敗したら null。</summary>
    FileMeta? TryGet(string? path);
}
