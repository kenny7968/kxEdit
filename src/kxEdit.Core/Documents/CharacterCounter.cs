using System.Buffers;
using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Documents;

/// <summary>
/// 文書全体の「可視文字数」を数える純関数。
/// - サロゲートペア = 1 文字(Rune 単位)
/// - Rune.IsWhiteSpace(Unicode White_Space)に該当する文字を除外
///   = 半角スペース(U+0020) / タブ(U+0009) / CR(U+000D) / LF(U+000A) / 全角スペース(U+3000) / NBSP 等
/// - 不正な UTF-16 シーケンス(未対 high/low サロゲート等)は、その 1 文字だけをスキップする
///   (後続の正常な文字は巻き添えにしない)。バッファ層が UTF-8 バックエンドで U+FFFD へ
///   正規化するため TextSnapshot 経由では到達しない防御。CharacterCounterTests が
///   その正規化ごと固定している
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
            // ペアが成立するときだけ次の 1 文字を消費する。Read() で無条件に食うと、
            // high サロゲートの直後にある正常な文字まで巻き添えで捨ててしまう。
            // Peek() は EOF で -1 を返し、(char)(-1)=U+FFFF は low サロゲートではないので
            // 明示の EOF 分岐は不要。
            int next = reader.Peek();
            if (char.IsHighSurrogate(buf[0]) && next >= 0 && char.IsLowSurrogate((char)next))
            {
                buf[1] = (char)reader.Read();
                len = 2;
            }
            var status = Rune.DecodeFromUtf16(buf[..len], out Rune rune, out _);
            if (status == OperationStatus.Done && !Rune.IsWhiteSpace(rune))
                count++;
        }
        return count;
    }
}
