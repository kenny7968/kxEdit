namespace kxEdit.Core.Settings;

/// <summary>
/// settings.json の読込結果(設計 2026-09-02 §5.2)。旧実装は catch-all で
/// この 4 状態を 1 つに潰しており、破損しても無言で既定値へ戻っていた。
/// </summary>
public enum SettingsLoadStatus
{
    /// <summary>読めて、解釈できた。</summary>
    Ok,

    /// <summary>ファイルが無い(初回起動)。通知しない。</summary>
    Missing,

    /// <summary>読めたが JSON として解釈できない(内容が "null" の場合を含む)。通知し、退避する。</summary>
    Corrupt,

    /// <summary>I/O で読めない(ロック・権限)。通知し、<b>起動時には退避しない</b>
    /// —— 中身が正常なファイルを改名してしまうため。
    /// <b>ただし退避しないのは起動時だけ</b>(B5・仕様レビュー I-2): 最初の設定保存の直前には
    /// <c>.bak</c> へ退避する(<see cref="SettingsStore.TryQuarantineUnreadable"/>)——
    /// そこまで来れば中身はどのみち上書きで失われるため。</summary>
    Unreadable,
}
