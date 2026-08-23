using kxEdit.Core.Text;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileDialogService"/> のテスト用フェイク。返す値を事前登録する(null=キャンセル)。
/// PickSaveAs へ渡った現在値(SaveAsRequest)を記録し、ダイアログ初期値の配線を検証できるようにする。
/// </summary>
public sealed class FakeFileDialogService : IFileDialogService
{
    public string? OpenPath { get; set; }

    /// <summary>
    /// 単一値の応答(従来 API)。**1 回目の呼出でだけ**返し、以降はキャンセル扱い。
    /// Task 8(2026-08-23)以降、この 1-shot 性は <see cref="SaveAsQueue"/> の枯渇と並ぶ
    /// **停止保証**でもある(劣化警告も continue するようになり、`FakePrompt.OkCancelResult` は
    /// 固定フィールドで永久に同じ答を返すため、同じ値を返し続ける Fake は無限ループになる)。
    /// **「最後の値を繰り返す」モードをここにも <see cref="SaveAsQueue"/> にも足さないこと。**
    /// </summary>
    public SaveAsResult? SaveAs { get; set; }

    /// <summary>
    /// 複数回の応答(ダイアログ再表示のテスト用)。先頭から 1 件ずつ払い出す。
    /// **枯渇したらキャンセル(null)**にすることで、網の書き間違いが無限ループではなく
    /// 「PickSaveAsCount が想定と違う」という失敗として出る。
    /// Task 8 以降これは唯一の停止保証なので、**「最後の値を繰り返す」モードを足さないこと**
    /// (<see cref="SaveAs"/> の 1-shot 性についても同じ)。
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
