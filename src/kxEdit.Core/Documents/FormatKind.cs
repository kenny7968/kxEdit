namespace kxEdit.Core.Documents;

/// <summary>文書情報ダイアログの「形式」項目のカテゴリ。拡張子から Builder が判定する。</summary>
public enum FormatKind
{
    /// <summary>.txt</summary>
    Text,

    /// <summary>.csv</summary>
    Csv,

    /// <summary>.md</summary>
    Markdown,

    /// <summary>その他の拡張子 or 拡張子なし</summary>
    Other,

    /// <summary>Path=null(未保存)</summary>
    Unsaved,
}
