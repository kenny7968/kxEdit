using System.Text;

namespace kxEdit.Core.Text;

/// <summary>
/// .NET9 で Shift_JIS(932)/EUC-JP(51932) を使うには CodePagesEncodingProvider の登録が要る。
/// 本カタログ初回利用時に一度だけ登録する。
/// </summary>
public static class EncodingCatalog
{
    // double-checked locking の慣用に従い volatile（lock 外の高速パス読みで最新値を見る）。
    private static volatile bool _registered;
    private static readonly object _sync = new();

    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        lock (_sync)
        {
            if (_registered)
                return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>コードページから Encoding を得る（必要なら provider 登録）。</summary>
    public static Encoding Get(int codePage)
    {
        EnsureRegistered();
        return codePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            _ => Encoding.GetEncoding(codePage),
        };
    }

    /// <summary>UI で扱う文字コードの選択肢（表示名・コードページ）。表示順を兼ねる。</summary>
#pragma warning disable S3218 // reason: 内側 record struct の DisplayName property は外側 EncodingCatalog.DisplayName(int) と同名だが、外側は既存 public API=改名不可
    public readonly record struct EncodingOption(int CodePage, string DisplayName);
#pragma warning restore S3218

    /// <summary>
    /// 選択・表示で使える文字コード一覧（表示順）。BOM 有無は含まない基本名。
    /// 表示名／選択肢の定義をここに一本化し、App 側（ステータス表示・開き直しダイアログ）が参照する。
    /// </summary>
    public static IReadOnlyList<EncodingOption> SelectableEncodings { get; } =
        new[]
        {
            new EncodingOption(65001, "UTF-8"),
            new EncodingOption(932, "Shift_JIS"),
            new EncodingOption(51932, "EUC-JP"),
        };

    /// <summary>SaveAs 用の選択肢。UTF-8 のみ BOM 有無で 2 エントリに展開し、
    /// 他の CodePage はそのまま(BOM 概念が意味を持たないため HasBom=false 固定)。</summary>
#pragma warning disable S3218 // reason: 内側 record struct の DisplayName property は外側 EncodingCatalog.DisplayName(int) と同名だが、外側は既存 public API=改名不可
    public readonly record struct SaveAsEncodingOption(
        int CodePage,
        bool HasBom,
        string DisplayName
    );
#pragma warning restore S3218

    /// <summary>SaveAs で使う文字コード選択肢(表示順)。
    /// 既存の <see cref="SelectableEncodings"/> は開き直し/設定/ステータス表示で使う BOM 無視の一覧。
    /// 本一覧は SaveAs 専用で BOM 有無を明示させるため別プロパティで公開する。</summary>
    public static IReadOnlyList<SaveAsEncodingOption> SaveAsSelectableEncodings { get; } =
        new[]
        {
            new SaveAsEncodingOption(65001, false, "UTF-8 (BOM なし)"),
            new SaveAsEncodingOption(65001, true, "UTF-8 (BOM)"),
            new SaveAsEncodingOption(932, false, "Shift_JIS"),
            new SaveAsEncodingOption(51932, false, "EUC-JP"),
        };

    /// <summary>
    /// コードページの基本表示名（BOM 有無は含まない）。未知のコードページは WebName を返す。
    /// "UTF-8 (BOM)" 等の BOM 表記は呼び出し側で付与する。
    /// </summary>
    public static string DisplayName(int codePage)
    {
        foreach (var opt in SelectableEncodings)
            if (opt.CodePage == codePage)
                return opt.DisplayName;
        return Get(codePage).WebName;
    }
}
