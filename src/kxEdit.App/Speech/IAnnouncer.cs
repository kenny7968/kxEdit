namespace kxEdit.App.Speech;

/// <summary>
/// SR への能動通知。底部/ステータス Label を視覚表示しつつ、UIA 通知で発声する。
/// 空メッセージは「視覚クリアのみ・発声なし」（表示と読み上げを1呼び出しで扱えるようにする契約）。
/// 実体は UiaAnnouncer（利用側が通知先 Label を渡して直接生成する）。
/// </summary>
public interface IAnnouncer
{
    void Say(string message);
}
