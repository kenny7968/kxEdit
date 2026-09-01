namespace kxEdit.Core.IO;

// RCS1194 (Implement exception constructors) を本型でのみ抑止する主因:
//   - RCS1194 は基底型の public ctor をすべて鏡像実装することを要求する。基底が Exception の
//     DocumentTooLargeException は 3 つの標準 ctor で足りるが、本型の基底 IOException には
//     IOException(string message, int hresult) があり、これも鏡像実装しろと言ってくる。
//   - 標準 ctor 3 種は RCS1194 の意図どおり実装済みで、欠けているのは上記 1 種のみ。
//   - 退けた案 1「public な鏡像 ctor を足す」: HResult を外から与える ctor は、この型の存在意義
//     そのものを壊す。下の xmldoc のとおり本型は「共有/ロック違反(0x80070020 / 0x80070021)と
//     一致しない HResult を持つ」ことが不変条件であり、hresult 引数はその不変条件を公開 API の
//     穴にする(IsShareOrLockViolation が真になり、原本喪失後に in-place フォールバックへ流れる)。
//   - 退けた案 2「private な鏡像 ctor を足す」: 実測では RCS1194 は消え、S1144 / CA1823 等の
//     未使用警告も出ない(=技術的には成立する)。それでも採らないのは、誰も呼ばない private ctor は
//     「なぜあるのか」がコードから読めず、将来の掃除で消されて再びビルドが割れるため。
//     抑止する理由をこのコメントとして残せる pragma の方が、意図が保存される。
#pragma warning disable RCS1194 // reason: 上記。IOException(string, int) の鏡像は型の不変条件と衝突する

/// <summary>
/// 原子的差替(File.Replace / File.Move)が失敗し、<b>かつ差替先の原本が失われた</b>ときに投げる。
/// このとき <see cref="PreservedTempPath"/> がディスク上の唯一のコピーであり、
/// <see cref="AtomicFile"/> は例外的に tmp を掃除せず残す(消すと内容が完全に失われるため)。
/// <para>
/// <b>HResult を共有/ロック違反(0x80070020 / 0x80070021)と一致させてはならない。</b>
/// 一致すると <see cref="AtomicFile.IsShareOrLockViolation"/> が真になり、呼出側
/// (<c>TextFileService.Save</c>)の in-place 上書きフォールバックへ流れる。原本が消えた後に
/// そこへ流すと、AtomicFile 側の復旧と同じ仕事を別経路で二重に行うことになり、
/// どちらが効いたのかがテストからも実機からも判らなくなる(設計 2026-09-02 §3.4)。
/// 既定の <see cref="IOException"/> の HResult をそのまま使い、
/// <c>AtomicReplaceFailedExceptionTests.Is_not_classified_as_share_or_lock_violation</c> で固定する。
/// </para>
/// </summary>
public sealed class AtomicReplaceFailedException : IOException
{
    /// <summary>失われた差替先のパス。</summary>
    public string TargetPath { get; } = string.Empty;

    /// <summary>掃除せず残した tmp のパス(= ディスク上の唯一のコピー)。</summary>
    public string PreservedTempPath { get; } = string.Empty;

    /// <summary>復旧(tmp を元の名前へリネーム)が失敗した理由。
    /// <see cref="Exception.InnerException"/> は差替そのものの失敗。</summary>
    public Exception? RecoveryError { get; }

    public AtomicReplaceFailedException(
        string targetPath,
        string preservedTempPath,
        Exception replaceError,
        Exception recoveryError
    )
        : base(
            $"保存先 '{targetPath}' が失われました。書き込んだ内容は '{preservedTempPath}' に残してあります。",
            replaceError
        )
    {
        TargetPath = targetPath;
        PreservedTempPath = preservedTempPath;
        RecoveryError = recoveryError;
    }

    // 以下は RCS1194 (Implement exception constructors) 準拠のための標準 ctor。
    // DocumentTooLargeException と同じ扱い(呼び出し実績は無いが将来の汎用パターン用)。
    // プロパティ初期化子で string.Empty を与えているのは、Nullable enable 下で
    // これらの ctor が CS8618 になるため(= null を持つ例外を作らせない)。
    public AtomicReplaceFailedException() { }

    public AtomicReplaceFailedException(string message)
        : base(message) { }

    public AtomicReplaceFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}
#pragma warning restore RCS1194
