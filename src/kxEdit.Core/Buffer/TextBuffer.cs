using System.Text;

namespace kxEdit.Core.Buffers;

/// <summary>Undo/Redo後の推奨キャレット位置。</summary>
public readonly record struct UndoResult(int CaretPos);

/// <summary>
/// UTF-8永続ピーステーブルのテキストバッファ。公開オフセットはすべてUTF-16コード単位。
/// スナップショット(Current)はルート参照コピーで O(1)・以後の編集の影響を受けない。
/// 単一スレッド(UIスレッド)からの編集を前提とする。スナップショットは不変だが、
/// 他スレッドへ渡す場合は通常の同期的ハンドオフ(lock/Invoke等)で受け渡すこと(§AppendBuffer共有ブロック)。
/// </summary>
public sealed class TextBuffer
{
    private readonly AppendBuffer _append = new();
    private readonly UndoHistory _history = new();
    private TextSnapshot _current;
    private PieceTree.Node? _savedRoot;

    // 保存点なし状態(MarkUnsaved)。参照比較だけでは fresh バッファ(生成時 _savedRoot=root。
    // 空文書なら両方 null)が常に「未変更」になるため、Modified へ OR するフラグで表す。
    private bool _noSavePoint;

    /// <summary>文書上限(§0-1: 既定 512 MB (2026-07-20 v0.11 引下げ))。テスト注入用。</summary>
    internal long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;

    internal TextBuffer(PieceTree.Node? root)
    {
        _current = new TextSnapshot(root);
        _savedRoot = root;
    }

    /// <summary>小文書・テスト用。ストリーム読込は TextBufferBuilder を使う。</summary>
    public static TextBuffer FromString(string text)
    {
        var builder = new TextBufferBuilder();
        builder.Add(Encoding.UTF8.GetBytes(text));
        return builder.Build();
    }

    public TextSnapshot Current => _current;

    /// <summary>現在ルート != 保存時ルート(参照比較。Undoで保存点に戻ると false に戻る)。
    /// <see cref="MarkUnsaved"/> 後は次の <see cref="MarkSaved"/> まで true 固定。</summary>
    public bool Modified => _noSavePoint || !ReferenceEquals(_current.Root, _savedRoot);

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>Undo。不可なら null。キャレット= Pos + RemovedLen(削除が復元された末尾)。</summary>
    public UndoResult? Undo()
    {
        var e = _history.PopUndo();
        if (e is null)
            return null;
        _current = new TextSnapshot(e.Value.RootBefore);
        return new UndoResult(e.Value.Pos + e.Value.RemovedLen);
    }

    /// <summary>Redo。不可なら null。キャレット= Pos + InsertedLen。</summary>
    public UndoResult? Redo()
    {
        var e = _history.PopRedo();
        if (e is null)
            return null;
        _current = new TextSnapshot(e.Value.RootAfter);
        return new UndoResult(e.Value.Pos + e.Value.InsertedLen);
    }

    /// <summary>キャレット移動・保存時にApp側が呼ぶ(coalescing強制分割)。</summary>
    public void BreakUndoCoalescing() => _history.BreakCoalescing();

    /// <summary>SavePoint。以後 Modified は現在ルートとの参照比較。
    /// 保存点はcoalescing境界(Undoが保存点を飛び越えて融合エントリを戻さないように)。</summary>
    public void MarkSaved()
    {
        _savedRoot = _current.Root;
        _noSavePoint = false;
        _history.BreakCoalescing();
    }

    /// <summary>保存点を破棄し、次の <see cref="MarkSaved"/> まで <see cref="Modified"/> を true に固定する。
    /// バックアップ復元のように「fresh バッファだが内容はどのファイルにも保存されていない」文書を
    /// dirty として扱うための逆操作(Undo で戻れる保存点は存在しないためフラグで表す)。</summary>
    public void MarkUnsaved() => _noSavePoint = true;

    /// <summary>EmptyUndoBuffer相当。両スタック破棄(保存点は維持)。</summary>
    public void ClearUndo() => _history.Clear();

    /// <summary>
    /// A-11(2026-08-28): Redoスタックだけを破棄する(Undoスタック・保存点・coalescing 状態は不変)。
    /// </summary>
    /// <remarks>
    /// 用途は「ユーザーが要求していない <see cref="Undo"/>」の後始末=保存失敗時の EOL 変換
    /// ロールバック(<c>EditorControl.UndoEolConversion</c>)だけである。通常の <see cref="Undo"/> は
    /// エントリを <c>_redo</c> へ積んで Ctrl+Y でやり直せるようにするが、ロールバックで積まれる
    /// エントリは「全文の EOL 変換」であり、ユーザーが一度も要求していない操作を Ctrl+Y に
    /// 差し出すことになる(やり直しメニューも有効化される)。そのため呼び出し側が明示的に捨てる。
    /// <para>
    /// 保存失敗<b>前</b>にユーザーが持っていた Redo は復元できない
    /// (<c>ReplaceAllRecordingUndo</c> の <c>Record</c> が既に <c>_redo.Clear()</c> 済み=
    /// 設計書 2026-08-28 §10.11 (4) で受容)。本メソッドは「空のまま維持する」だけである。
    /// </para>
    /// </remarks>
    public void DropRedo() => _history.ClearRedo();

    /// <summary>
    /// A-11(2026-08-28): <paramref name="rebuilt"/> が指す木へ全文を差し替え、
    /// <b>1 Undo 単位として記録する</b>。保存時のEOL一括変換(<c>EditorControl.ConvertEols</c>)で
    /// Undo/Redo履歴が全消去されるのを防ぐための経路。
    /// </summary>
    /// <param name="rebuilt">差し替え後の内容を持つスナップショット(木のルートだけを取り込む)。
    /// 別の <see cref="TextBuffer"/> が構築・編集したものでよい。</param>
    /// <returns>記録したら true。現在のルートと同一(無変化)で何もしなければ false。
    /// 呼び出し側は「Undo 1 回で差し替え前へ戻せるか」をこの戻り値で判定する(経路から推論しない)。</returns>
    /// <remarks>
    /// Undoエントリは永続木のルート参照だけを持つので、全文差し替えでもテキストの実体化や
    /// 差分計算は要らない(コストはほぼゼロ)。
    /// <para>
    /// <c>_savedRoot</c> は<b>触らない</b>: 保存点はルートの参照比較で判定されるため、
    /// Undoで変換前=保存点のルートへ戻れば <see cref="Modified"/> も自動的にfalseへ復す。
    /// </para>
    /// <para>
    /// 通常の編集と同じく<b>Redoスタックを破棄する</b>(<c>UndoHistory.Record</c> の
    /// <c>_redo.Clear()</c>。<c>Splice</c> と同じ契約)。
    /// </para>
    /// <para>
    /// <b>メモリ</b>: 差し替え<b>前</b>の木は Undo エントリ(<c>RootBefore</c>)が保持し続ける。
    /// <paramref name="rebuilt"/> が別バッファ由来だとバイト列を共有しないため、全文 EOL 変換では
    /// 文書 1 つぶんのメモリが増える(<c>UndoHistory</c> にエントリ数の上限は無い)。
    /// 「Undo で戻せる」ことは「戻し先を持っている」ことなので、これは本 API に内在するコストである
    /// (設計書 2026-08-28 §10.15)。
    /// </para>
    /// <para>
    /// 文書上限(<see cref="MaxTotalBytes"/>)はここで再判定しない。取り込む木の上限は
    /// 渡し手が担保する(<see cref="TextBufferBuilder"/> が構築時に判定し
    /// <c>DocumentTooLargeException</c> を投げる)。
    /// </para>
    /// <para>
    /// 別バッファ由来のスナップショットでよい: <c>Piece</c> は自分のチャンク参照を持ち、
    /// 読み取り経路はチャンクの出自を仮定しない(編集中バッファのスナップショットでも
    /// <c>AppendBuffer</c> の「公開済み範囲は以後不変」不変条件が効く)。
    /// </para>
    /// </remarks>
    public bool ReplaceAllRecordingUndo(TextSnapshot rebuilt)
    {
        ArgumentNullException.ThrowIfNull(rebuilt);
        var rootBefore = _current.Root;
        var newRoot = rebuilt.Root;
        // 参照比較で無変化なら履歴を汚さない(Splice の早期returnと同じ契約)。内容比較はしない:
        // 全文の突合はO(n)で、呼び出し側(ConvertEols)が変換不要を先に判定できるため。
        // 自分自身のスナップショットを渡した場合もここで抜ける。
        if (ReferenceEquals(rootBefore, newRoot))
            return false;

        int removed = _current.CharLength;
        int inserted = rebuilt.CharLength;
        _current = new TextSnapshot(newRoot);

        // 全文差し替えは単一Undo単位。EOL変換が作る通常形(removed>0 かつ inserted>0)は
        // Record の融合判定(pureInsert / pureDelete)に構造的に掛からないので、次の2つの
        // ガードが実際に効くのは退化形だけである。どちらも公開API契約のための防御で、
        // ConvertEols 経路からは発生しない(EOL変換は本文を空にも非空にもしない):
        //  ・前置 BreakCoalescing = 全文→空(純削除形・removed≤2)が直前の1〜2文字削除へ
        //    逆方向融合(Backspace継続扱い)するのを止める。pureDelete側の判定は
        //    insertHasBreak を見ないので、この向きはフラグでは止められない。
        //    早期returnの後に置く: 無変化パスで _open を倒すと「履歴を汚さない」契約が崩れる。
        //  ・insertHasBreak: true = 空文書→1〜2文字(純挿入形)の直後のタイプがこのエントリへ
        //    融合するのを止める(Record 末尾の _open = coalescable を false にする)。
        // 純削除の退化形では Record 後に _open = true が残るが、差し替え後は空文書で後続の
        // 融合可能な編集が来ない(削除は Splice の早期return、挿入は prev.RemovedLen == 0 の
        // 条件で弾かれる)ため観測できない。
        _history.BreakCoalescing();
        _history.Record(
            rootBefore,
            newRoot,
            pos: 0,
            removedLen: removed,
            insertedLen: inserted,
            insertHasBreak: true
        );
        return true;
    }

    public void Insert(int pos, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateRange(pos, 0);
        Splice(pos, 0, text);
    }

    public void Delete(int pos, int length)
    {
        ValidateRange(pos, length);
        Splice(pos, length, "");
    }

    /// <summary>1 Undo単位のsplice。</summary>
    public void Replace(int pos, int length, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateRange(pos, length);
        Splice(pos, length, text);
    }

    /// <summary>全編集の共通経路。範囲 [pos, pos+delLen) を insert で置き換える。</summary>
    private void Splice(int pos, int delLen, string insert)
    {
        if (delLen == 0 && insert.Length == 0)
            return; // 無変化

        var rootBefore = _current.Root;

        // 1)-2) 分割(サロゲートペア中間は Split 内で前方=低い方へスナップ§0-3)。
        //        実効位置は分割結果の統計から得る(追加走査なし)
        var (l, rest) = PieceTree.Split(rootBefore, pos);
        int start = PieceTree.SumOf(l).CharLen;
        var (mid, r) = PieceTree.Split(rest, pos + delLen - start);
        int removed = PieceTree.SumOf(mid).CharLen;
        if (removed == 0 && insert.Length == 0)
            return; // スナップの結果無変化(ルート参照も不変)

        // 文書上限ガード(§0-1)。分割は非破壊なので、ここで throw しても状態は変わらない
        long newTotalBytes =
            PieceTree.SumOf(rootBefore).ByteLen
            - PieceTree.SumOf(mid).ByteLen
            + (insert.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(insert));
        if (newTotalBytes > MaxTotalBytes)
            throw new InvalidOperationException(
                "文書サイズ上限(512 MB)を超えるため編集できません。"
            );

        // 3) 挿入テキストを追記バッファへ
        var newPieces = _append.Append(insert);

        // 4) 隣接マージ: 同一チャンクかつバイト連続なら1ピース化(連続タイピングでピース数が伸びない)
        PieceTree.Node? left = l;
        if (newPieces.Count > 0 && left is not null)
        {
            var (remaining, last) = PieceTree.SplitLast(left);
            var first = newPieces[0];
            if (
                ReferenceEquals(last.Chunk, first.Chunk)
                && last.ByteStart + last.ByteLen == first.ByteStart
            )
            {
                newPieces[0] = new Piece(
                    last.Chunk,
                    last.ByteStart,
                    last.ByteLen + first.ByteLen,
                    PieceStats.Combine(last.Stats, first.Stats)
                );
                left = remaining;
            }
            else
                left = PieceTree.Join(remaining, last, null);
        }
        // 右側マージ: 現設計では既存ピースが新規追記の直後に位置することはないため通常発火しない
        // (左右対称性の保険として保持。発火しても正しさはモノイド結合で保証される)
        PieceTree.Node? right = r;
        if (newPieces.Count > 0 && right is not null)
        {
            var (first, remaining) = PieceTree.SplitFirst(right);
            var lastNew = newPieces[^1];
            if (
                ReferenceEquals(lastNew.Chunk, first.Chunk)
                && lastNew.ByteStart + lastNew.ByteLen == first.ByteStart
            )
            {
                newPieces[^1] = new Piece(
                    first.Chunk,
                    lastNew.ByteStart,
                    lastNew.ByteLen + first.ByteLen,
                    PieceStats.Combine(lastNew.Stats, first.Stats)
                );
                right = remaining;
            }
            else
                right = PieceTree.Join(null, first, remaining);
        }

        // 5) 再結合+Undoログ記録
        var joined = left;
        foreach (var p in newPieces)
            joined = PieceTree.Join(joined, p, null);
        var newRoot = PieceTree.Join2(joined, right);
        _current = new TextSnapshot(newRoot);
        // 挿入のUTF-16長: 孤立サロゲート→U+FFFD 置換は1単位→1単位なので insert.Length と一致
        _history.Record(
            rootBefore,
            newRoot,
            start,
            removed,
            insert.Length,
            insert.Contains('\n') || insert.Contains('\r')
        );
    }

    private void ValidateRange(int pos, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pos);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((long)pos + length > _current.CharLength)
            throw new ArgumentOutOfRangeException(
                nameof(pos),
                pos,
                "範囲が文書末尾を超えています。"
            );
    }
}
