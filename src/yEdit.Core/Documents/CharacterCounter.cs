using System.Buffers;
using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Documents;

/// <summary>
/// 文書全体の「可視文字数」を数える純関数。
/// - サロゲートペア = 1 文字(Rune 単位)
/// - Rune.IsWhiteSpace(Unicode White_Space)に該当する文字を除外
///   = 半角スペース(U+0020) / タブ(U+0009) / CR(U+000D) / LF(U+000A) / 全角スペース(U+3000) / NBSP 等
/// - 不正な UTF-16 シーケンス(未対 high/low サロゲート等)はスキップ
///   (バッファ層が UTF-8 バックエンドで U+FFFD へ正規化するため TextSnapshot 経由では
///    到達しない防御。CharacterCounterTests がその正規化ごと固定している)
///
/// 設計判断: 位置照会(Ctrl+Alt+P)側の CRLF=1 論理文字(サロゲート=2)とは異なる基準を採る。
/// 本メソッドは「人間に自然な文字数」= CRLF は空白として除外・サロゲート=1(Rune)を優先する。
/// 両者は異なる文脈の指標として意図的に棲み分ける(設計 2026-07-25 §4 参照)。
///
/// 走査は <see cref="TextSnapshot.CreateReader"/> 経由で行い、全文 string を実体化しない
/// (peak O(piece))。低頻度パス(ダイアログを開いた瞬間のみ)なので O(N) 走査を許容する。
/// </summary>
public static class CharacterCounter
{
    public static int CountVisible(TextSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        using var reader = snap.CreateReader();
        int count = 0;
        Span<char> buf = stackalloc char[2];
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            buf[0] = (char)ch;
            int len = 1;
            if (char.IsHighSurrogate(buf[0]))
            {
                int ch2 = reader.Read();
                if (ch2 < 0)
                    break; // 未対 high のまま EOF → 破棄
                buf[1] = (char)ch2;
                len = 2;
            }
            var status = Rune.DecodeFromUtf16(buf[..len], out Rune rune, out _);
            if (status == OperationStatus.Done && !Rune.IsWhiteSpace(rune))
                count++;
        }
        return count;
    }
}
