using kxEdit.Core.Text;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileDialogService"/> のテスト用フェイク。返す値を事前登録する(null=キャンセル)。
/// PickSaveAs へ渡った現在値(SaveAsRequest)を記録し、ダイアログ初期値の配線を検証できるようにする。
/// </summary>
public sealed class FakeFileDialogService : IFileDialogService
{
    public string? OpenPath { get; set; }

    /// <summary>単一値の応答(従来 API)。**1 回目の呼出でだけ**返し、以降はキャンセル扱い。</summary>
    public SaveAsResult? SaveAs { get; set; }

    /// <summary>
    /// 複数回の応答(ダイアログ再表示のテスト用)。先頭から 1 件ずつ払い出す。
    /// **枯渇したらキャンセル(null)**にすることで、網の書き間違いが無限ループではなく
    /// 「PickSaveAsCount が想定と違う」という失敗として出る。
    /// </summary>
    public Queue<SaveAsResult?> SaveAsQueue { get; } = new();

    public int? EncodingCodePage { get; set; }

    public List<SaveAsRequest> SaveAsRequests { get; } = new();
    public int PickSaveAsCount => SaveAsRequests.Count;
    public int PickOpenCount;
    public int PickEncodingCount;

    public string? PickOpenPath(IWin32Window owner)
    {
        PickOpenCount++;
        return OpenPath;
    }

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        SaveAsRequests.Add(current); // 再表示時の初期値(seed)を検証する観測点
        if (SaveAsQueue.Count > 0)
            return SaveAsQueue.Dequeue();
        return SaveAsRequests.Count == 1 ? SaveAs : null;
    }

    public int? PickEncoding(IWin32Window owner, int currentCodePage)
    {
        PickEncodingCount++;
        return EncodingCodePage;
    }
}
