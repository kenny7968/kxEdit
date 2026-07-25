using yEdit.Core.Documents;

namespace yEdit.App;

/// <summary>
/// [ファイル] &gt; 文書情報 の起動導線。DocumentManager からアクティブ文書を取り、
/// Core の純関数(Builder → Formatter)に流して文字列を作り、ダイアログを表示する
/// (KinsokuFormatController と同型の薄い App 層)。
/// テスト観点: ShowDialog はモーダルで自動テストできないため、文字列生成を
/// <see cref="BuildText"/> として切り出しスモークの対象にする。
/// </summary>
public sealed class DocumentInfoController
{
    private readonly DocumentManager _docs;
    private readonly IFileMetaProvider _meta;

    public DocumentInfoController(DocumentManager docs, IFileMetaProvider? meta = null)
    {
        ArgumentNullException.ThrowIfNull(docs);
        _docs = docs;
        _meta = meta ?? FileMetaProvider.Instance; // seam(テストで差し替え可)
    }

    /// <summary>アクティブ文書から DocumentInfo を組み立て、Formatter で文字列にして返す。
    /// アクティブ文書が無ければ null(=ダイアログを開かない)。</summary>
    public string? BuildText()
    {
        var doc = _docs.Active;
        if (doc is null)
            return null;
        var info = DocumentInfoBuilder.Build(
            path: doc.State.Path,
            untitledNumber: doc.State.UntitledNumber,
            snapshot: doc.Editor.CurrentBuffer.Current,
            encoding: doc.State.Encoding,
            hasBom: doc.State.HasBom,
            lineEnding: doc.State.LineEnding,
            fileMeta: _meta.TryGet(doc.State.Path),
            // CSV モードは CsvController.TryEnterMode が csv.Ok を確認してからでないと入れないため、
            // ここでのパースは常に成功する(ParseCsv は文書単位のメモ化済み=再パースしない)。
            csv: doc.State.CsvMode ? doc.ParseCsv() : null
        );
        return DocumentInfoFormatter.Format(info);
    }

    /// <summary>ダイアログを表示する。アクティブ文書が無ければ何もしない。
    /// 閉じたら編集領域へフォーカスを戻す(操作継続性)。</summary>
    public void Show(IWin32Window owner)
    {
        string? text = BuildText();
        if (text is null)
            return;
        using var dlg = new DocumentInfoDialog(text);
        dlg.ShowDialog(owner);
        _docs.Active?.FocusTarget.Focus();
    }
}
