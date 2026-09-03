using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// <see cref="IFileTimestampProvider"/> の本番実装。呼び出し元は A-1 の起動時復元(陳腐化検出)と
/// M-18 の外部変更検知(開く・保存・ウィンドウ復帰・タブ切替の各チェック)。どちらの経路でも、
/// どんな入力でも例外を上位へ伝播させない(1 件の異常で全タブの復元を巻き添えにしない
/// = FileController.RestoreFromBackup のフォールバック方針と同じ。M-18 側は null を
/// 「判定しない」(<see cref="ExternalChangeOutcome.Skipped"/>)として扱い、聞かない)。
/// 2 本の公開メソッドは同じ核(<see cref="GetCore"/>)を通り、違いは到達不能記憶を参照するかどうかだけ:
/// <see cref="GetLastWriteTimeUtc"/>(復帰・タブ切替の検知と A-1 の復元)は参照し、
/// <see cref="ProbeLastWriteTimeUtc"/>(開く・保存・保存直前の確認 = 基準を捕捉する経路)は素通りする。
/// </summary>
/// <remarks>
/// 脆弱性レビュー H-1: リモートパス(UNC / マップドネットワークドライブ)は
/// <see cref="IReachabilityProbe"/> の 5 秒プローブを前置する。設計時は
/// 「<c>OriginalPathValidator.Check</c> が既に同期 I/O で触れた後のパスだけを見るので
/// 新しい凍結クラスは作らない」と考えていたが、<b>これは UNC で成立しない</b>:
/// 同 validator は <c>isUnc</c> のとき reparse 検査(唯一の I/O)をスキップするため、
/// UNC では本クラスの <see cref="File.Exists"/> が(プローブ無しでは)復元経路で最初の同期 I/O になる。
/// 切断済みリモートでは SMB タイムアウト(約 60 秒)まで UI スレッドが返らず、
/// 起動時にタブ数ぶん直列で発生する(HIGH-6 / CSV-M-1 / FileMetaProvider と同じ罠)。
/// M-18 の各経路でも本クラスが同じ核で呼ばれるため、切断済み共有での待ちは同じく
/// 1 回 5 秒に収まる。ただし呼び出しが起動時の 1 回から常時の繰り返しへ変わったので、
/// 到達不能の記憶には TTL を付けた(<see cref="DefaultUnreachableTtl"/>)。記憶を参照するのは
/// <see cref="GetLastWriteTimeUtc"/> だけで、基準を捕捉する経路は素通りする(<see cref="_unreachableUntil"/>)。
/// </remarks>
public sealed class FileTimestampProvider : IFileTimestampProvider
{
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約(FileController.TryProbeFileExists・
    /// FileMetaProvider と対称)。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>到達不能ルートを憶えておく長さ(M-18・設計 2026-09-03 §3.8)。
    /// A-1 の起動時復元だけならプロセス寿命でよかったが、M-18 がウィンドウ復帰・タブ切替の
    /// たびに呼ぶため (1) 永久に憶えると一度落ちた共有の文書は再起動まで検知が黙って止まり、
    /// (2) 憶えないと Alt+Tab のたびに 5 秒止まる。60 秒で「最悪 1 分に 1 回 5 秒」。</summary>
    private static readonly TimeSpan DefaultUnreachableTtl = TimeSpan.FromSeconds(60);

    private readonly IReachabilityProbe _probe;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _unreachableTtl;

    /// <summary>到達不能と判明したリモートルート(<c>\\server\share</c> / <c>Z:\</c>)→ 記憶の期限。
    /// 参照するのは <see cref="GetLastWriteTimeUtc"/>(復帰・タブ切替の検知と A-1 の起動時復元)だけ。
    /// 起動時復元は同じ共有上の文書を何件も含みうるため、これを憶えないと
    /// 「5 秒 × レコード数」が積み上がる(レビュー H-1 の増幅点)。
    /// 記録の効果は「その根の判定をあきらめる = null を返す」。A-1(復元)では null = 従来どおり復元
    /// なので安全側にしか倒れず、復帰・タブ切替の検知では null = Skipped(聞かない)で基準を汚さない。
    /// 基準を捕捉する経路(開く・保存・保存直前の確認 = <see cref="ProbeLastWriteTimeUtc"/>)は記憶を
    /// 素通りする: 使うと、共有が落ちて記憶 → 復旧 → 60 秒以内に開く/保存、で基準が null になり、
    /// その文書の検知(復帰時の確認も保存直前の確認も)が次の基準捕捉まで黙って止まる
    /// (最終脆弱性レビュー V-1)。記憶を書くのは両方(到達不能と判った事実は経路によらない)。</summary>
    private readonly Dictionary<string, DateTimeOffset> _unreachableUntil = new(
        StringComparer.OrdinalIgnoreCase
    );

    public FileTimestampProvider(
        IReachabilityProbe? probe = null,
        TimeProvider? clock = null,
        TimeSpan? unreachableTtl = null
    )
    {
        _probe = probe ?? new FileReachabilityProbe();
        _clock = clock ?? TimeProvider.System;
        _unreachableTtl = unreachableTtl ?? DefaultUnreachableTtl;
    }

    public DateTime? GetLastWriteTimeUtc(string path) => GetCore(path, useMemo: true);

    public DateTime? ProbeLastWriteTimeUtc(string path) => GetCore(path, useMemo: false);

    /// <summary>2 本の公開メソッドの共通の核。<paramref name="useMemo"/> は到達不能記憶を参照するか
    /// (書くのは両方)。ローカルパスは <paramref name="useMemo"/> によらずプローブしない。</summary>
    private DateTime? GetCore(string path, bool useMemo)
    {
        try
        {
            if (RemotePathDetector.IsRemote(path))
            {
                string root = RootKey(path);
                DateTimeOffset now = _clock.GetUtcNow();
                if (useMemo && _unreachableUntil.TryGetValue(root, out var until) && now < until)
                    return null;
                // 脆弱性レビュー L-1(2026-09-03): 「到達不能」と「到達できるが不在」を区別する。
                // ProbeFileExistsWithTimeout は File.Exists 意味論で両者を区別しないため、到達可能な
                // 共有上の一時的な不在(別ツールの delete→recreate・rename 保存の途中)でルート全体を
                // 到達不能として記憶し、以後その共有の全文書で検知が黙って止まっていた。
                // 保存先用のプローブは (Reachable, FileExists) を分けて返すので読む側でもこれを使う。
                // ただし Reachable は「ファイルが在る、または親フォルダーが在る」(FileReachabilityProbe の
                // 定義)なので、記憶しないのは「ファイルは無いが親フォルダーは在る」場合だけ。
                // 親フォルダーごと消えた/改名された場合は到達不能として TTL の間記憶される残余がある。
                var probe = _probe.ProbeSaveTargetWithTimeout(path, ProbeTimeout);
                if (!probe.Reachable)
                {
                    _unreachableUntil[root] = now + _unreachableTtl;
                    return null;
                }
                // 復旧を確認した根の記憶をここで捨てる。期限切れの記録は到達不能なら上の分岐で上書きされるが、
                // 復旧したときは上書きされないので明示的に消す。これで**この根については**壁時計が逆行しても
                // 期限切れの記録が復活しない。期限切れ後に一度も再照会されていない根は until を持ったまま残るので、
                // 一般命題としては閉じていない(設計 2026-09-03 §11.5「壁時計の逆行」)。
                _unreachableUntil.Remove(root);
                if (!probe.FileExists)
                    return null; // 不在は記憶しない(次の問い合わせで再び見る)
            }

            // 不在時の File.GetLastWriteTimeUtc は 1601-01-01 を返す(例外を投げない)。
            // そのまま返すと「非常に古いディスク」に見えて判定が黙って歪むため明示的に弾く。
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or System.Security.SecurityException
            )
        {
            return null;
        }
    }

    /// <summary>到達不能の記録単位。<see cref="Path.GetPathRoot"/> は UNC なら
    /// <c>\\server\share</c>、マップドドライブなら <c>Z:\</c> を返す。取れなければ
    /// パス全体をキーにする(記録が効かないだけで判定の正しさは変わらない)。</summary>
    private static string RootKey(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? path : root;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
