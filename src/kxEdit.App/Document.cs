using kxEdit.Core.Buffers;
using kxEdit.Core.Csv;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// 開いている1ドキュメント。独立した編集コントロール・タブ面・メタ状態を束ねる。
/// 変更フラグは Editor.Modified（TextBuffer のセーブポイント）を唯一の真実とする。
/// </summary>
public sealed class Document
{
    public EditorControl Editor { get; }
    public TabPage Page { get; }
    public DocumentState State { get; } = new();

    /// <summary>「編集領域」へフォーカスを戻すときの正しい行き先。P6 以降は常に Editor
    /// (P5 まで CSV モード中は CsvFocusSink へ退避していたが、UIA v2 単一経路への統一に合わせ Editor 固定・
    /// P7 で CsvFocusSink 自体を完全撤去=§0-8 猶予を解消)。</summary>
    public Control FocusTarget => Editor;

    public Document(EditorControl editor, TabPage page)
    {
        Editor = editor;
        Page = page;
    }

    /// <summary>タブに表示するラベル（変更マーク＋ファイル名）。位置はタイトルバーと揃える。</summary>
    public string TabLabel => (Editor.Modified ? "* " : "") + State.DisplayName;

    // ---- CSV パースのメモ化（文書単位） ----
    // TextSnapshot 自体の参照同一性でメモ化。TextBuffer.Current は edit 毎に new 置換されるため、
    // snapshot 参照比較で自動失効する。全文字列化(SnapshotText)を経由しないため 1GB CSV でも peak
    // O(chunk)。Root には触らず public TextSnapshot のみで実現（InternalsVisibleTo 拡張不要）。
    private TextSnapshot? _csvCachedSnap;
    private CsvDocument? _csvCachedDoc;

    /// <summary>TextSnapshot の参照同一性でメモ化した CSV パース。編集でスナップショットが
    /// 差し替わると自動失効する。キャッシュは文書と同寿命（タブを閉じれば解放）。</summary>
    public CsvDocument ParseCsv()
    {
        var snap = Editor.CurrentBuffer.Current;
        if (!ReferenceEquals(snap, _csvCachedSnap))
        {
            _csvCachedDoc = CsvParser.Parse(snap);
            _csvCachedSnap = snap;
        }
        return _csvCachedDoc!;
    }

    /// <summary>CSV パースキャッシュを明示解放する（CSVモード解除・開き直し時のメモリ解放）。</summary>
    public void ClearCsvCache()
    {
        _csvCachedSnap = null;
        _csvCachedDoc = null;
    }
}
