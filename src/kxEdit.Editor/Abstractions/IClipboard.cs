// IClipboard.cs
// A-13(監査 2026-08-22 / 設計 2026-08-29 §4): System.Windows.Forms.Clipboard は静的クラスで、
// 失敗経路(他プロセスがクリップボードを保持中の ExternalException)をテストから作れない。
// EditorControl が叩く 3 操作だけを切り出した seam(IImeContext と同じ形)。
// 本番実装 = WinClipboard。テスト実装 = FakeClipboard。
namespace kxEdit.Editor.Abstractions;

/// <summary>
/// <see cref="System.Windows.Forms.Clipboard"/> の UnicodeText 操作 seam。
/// </summary>
/// <remarks>
/// 実装は<b>例外を握らない</b>(捕捉は呼び出し側=<c>EditorControl</c> の責務)。
/// 実装がリトライを行わないことも契約の一部:
/// <see cref="System.Windows.Forms.Clipboard.SetText(string, System.Windows.Forms.TextDataFormat)"/>
/// は内部で 10 回 × 100ms リトライ済みで、その上での失敗はユーザーに伝えるのが正しい(設計 §10)。
/// 全メソッドが STA 必須(<c>EditorControl</c> は WinForms UI スレッド専用契約なので常に満たされる)。
/// </remarks>
public interface IClipboard
{
    /// <summary>UnicodeText 形式のデータがあるか。</summary>
    bool ContainsUnicodeText();

    /// <summary>UnicodeText を読む。無ければ空文字列。</summary>
    string GetUnicodeText();

    /// <summary>UnicodeText を書く。</summary>
    void SetUnicodeText(string text);
}
