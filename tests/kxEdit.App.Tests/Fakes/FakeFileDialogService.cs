using kxEdit.Core.Text;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileDialogService"/> のテスト用フェイク。返す値を事前登録する(null=キャンセル)。
/// PickSaveAs へ渡った現在値(SaveAsRequest)を記録し、ダイアログ初期値の配線を検証できるようにする。
/// </summary>
public sealed class FakeFileDialogService : IFileDialogService
{
    public string? OpenPath { get; set; }
    public SaveAsResult? SaveAs { get; set; }
    public int? EncodingCodePage { get; set; }

    public List<SaveAsRequest> SaveAsRequests { get; } = new();
    public int PickOpenCount;
    public int PickEncodingCount;

    public string? PickOpenPath(IWin32Window owner)
    {
        PickOpenCount++;
        return OpenPath;
    }

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        SaveAsRequests.Add(current);
        return SaveAs;
    }

    public int? PickEncoding(IWin32Window owner, int currentCodePage)
    {
        PickEncodingCount++;
        return EncodingCodePage;
    }
}
