using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>SaveAs ダイアログへ渡す現在値(初期選択の種)。</summary>
public sealed record SaveAsRequest(string? Path, int CodePage, bool HasBom, LineEnding LineEnding);

/// <summary>
/// SaveAs ダイアログの選択結果。Path は未検証のまま返す(空白のみの可能性あり。
/// 検証と警告は従来どおり FileController の責務=挙動不変)。
/// </summary>
public sealed record SaveAsResult(string Path, int CodePage, bool HasBom, LineEnding LineEnding);

/// <summary>
/// ファイル系ダイアログの結果だけを返す抽象(Phase 2 設計書 §2.2)。
/// 実装は既存ダイアログをラップする薄い Adapter で、キャンセルは null。
/// </summary>
public interface IFileDialogService
{
    string? PickOpenPath(IWin32Window owner);
    SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current);
    int? PickEncoding(IWin32Window owner, int currentCodePage);
}
