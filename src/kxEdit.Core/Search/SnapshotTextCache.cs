using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>
/// スナップショット 1 本ぶんの全文 string のキャッシュ(1 枠)。
/// 照合条件が変わって <see cref="SnapshotSearcher"/> を作り直しても、同じ文書なら全文化をやり直さないために、
/// searcher の外(<c>SearchController</c>)が所有して注入する(2026-09-25 フェーズ 5 P-5(a))。
/// </summary>
/// <remarks>
/// <para>
/// <b>参照同一性で判定する</b>。<see cref="TextSnapshot"/> は不変(構築時のルート参照を包むだけ)で、
/// <see cref="TextBuffer.Current"/> は編集・Undo・Redo のときだけ差し替わるフィールド返しなので、
/// 参照同一性が「文書が変わっていない」の正当な signal になる。
/// 参照同一性を同種の signal に使う idiom は <see cref="TextBuffer.Modified"/> が既に採用している
/// (あちらが比べるのはスナップショットではなくピース木のルート参照)。
/// </para>
/// <para>
/// 誤りは<b>安全な側にしか倒れない</b>: 内容が同じでもインスタンスが別なら
/// (Undo で同じルートへ戻った直後など)材質化をやり直すだけで、古い本文を返すことはない。
/// 逆向き=「同じインスタンスなのに内容が違う」は <see cref="TextSnapshot"/> が不変である限り起こらない。
/// 保持するのは常に最大 1 本で、スナップショットが変われば古い文字列は参照が切れる。
/// </para>
/// <para>
/// <b>public なのは型と ctor だけ</b>。kxEdit.App が所有して渡すためで(Core の InternalsVisibleTo に
/// kxEdit.App は含まれない)、中身を読む <see cref="TextOf"/> は internal。
/// </para>
/// <para>
/// <b>スレッドセーフではない</b>。所有者と、これを注入したすべての searcher は同じスレッドから使うこと。
/// <b>寿命は所有者の責任</b>: 最後に材質化した <see cref="TextSnapshot"/> と全文を強参照で持つ
/// (= 背後のピース木・バイト配列ごとピン留めする)。文書の切替・クローズ・検索の終了で参照を捨てること
/// (<c>SearchController.DropSearcher</c> がその実装)。
/// </para>
/// </remarks>
public sealed class SnapshotTextCache
{
    private TextSnapshot? _snapshot;
    private string _text = string.Empty;

    /// <summary>
    /// テスト観測用: 実際に材質化した回数。キャッシュが効いていることを assert 化する seam。
    /// <b>消さないこと</b>: <c>SnapshotTextCacheTests.Holds_at_most_one_snapshot</c> が「保持は最大 1 本」を
    /// 検証する唯一の手段であり、結果値からは辞書実装(多スロット)と区別できない。
    /// </summary>
    internal int MaterializeCountForTest { get; private set; }

    /// <summary>snap の全文。直前と同じスナップショットなら前回の結果を返す。</summary>
    internal string TextOf(TextSnapshot snap)
    {
        if (ReferenceEquals(_snapshot, snap))
            return _text;
        // 代入順は text が先・snapshot が後(入れ替えないこと)。逆順だと GetText が
        // 例外を投げたときに _snapshot だけ新しくなり、次回の参照同一性ヒットで
        // 古い本文を新しいスナップショットのものとして返す stale の窓が開く。
        _text = snap.GetText(0, snap.CharLength);
        _snapshot = snap;
        MaterializeCountForTest++;
        return _text;
    }
}
