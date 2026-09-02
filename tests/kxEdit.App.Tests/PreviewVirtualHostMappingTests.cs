using System.Collections.Generic;
using System.IO;

namespace kxEdit.App.Tests;

/// <summary>
/// V-2 + PR #57 申し送り: 仮想ホストのマッピング先を決める純粋ロジック。
/// <para>
/// 守る不変条件は<b>「マッピングは常に在る」</b>。未マップの状態を作ると
/// <c>https://kxedit.preview/...</c> が実 DNS 解決へ出る (監査 §9 V-2)。
/// 「baseDir が無い」「実在しないと分かっている」「登録が失敗した」のどれも、
/// 未マップではなく空フォルダーへ倒す。
/// </para>
/// <para>
/// 実在判定そのものは<b>呼び出し側の責務</b>。SetVirtualHostNameToFolderMapping は
/// 内部で実在確認をしており、不達な共有では 21 秒返らない (設計書 §13.1 の実測)。
/// だから「実在が確定したフォルダーだけを渡す」形になっている。
/// </para>
/// </summary>
public class PreviewVirtualHostMappingTests
{
    private const string Fallback = @"C:\fallback\empty-base";
    private const string BaseDir = @"C:\docs";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoBaseDir_MapsFallback(string? baseDir)
    {
        // baseDir が無い (未保存タブ等) ときも未マップにしない網。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(baseDir, baseDirExists: false, () => Fallback, calls.Add);
        Assert.Equal(new[] { Fallback }, calls);
    }

    [Fact]
    public void BaseDirNotUsable_MapsFallbackInsteadOfLeavingUnmapped()
    {
        // 実在しないと分かっているものは渡さない (渡すと例外か 21 秒ブロック)。
        // ただし未マップにもしない = V-2 の状態を作らない。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(BaseDir, baseDirExists: false, () => Fallback, calls.Add);
        Assert.Equal(new[] { Fallback }, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoBaseDir_MapsFallback_EvenIfExistsFlagIsTrue(string? baseDir)
    {
        // 2 つのガード (baseDir が空 / 実在しない) を弁別する網。
        // 呼び出し側は「空なら実在確認しない」ので実際には来ない組み合わせだが、
        // 空文字を map へ渡すと SetVirtualHostNameToFolderMapping が投げて
        // プレビューごと失敗する = 未マップより悪い。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(baseDir, baseDirExists: true, () => Fallback, calls.Add);
        Assert.Equal(new[] { Fallback }, calls);
    }

    [Fact]
    public void UsableBaseDir_IsMapped()
    {
        // 何でも fallback へ倒す退化した実装と区別する網 (相対画像が解決できる経路)。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(BaseDir, baseDirExists: true, () => Fallback, calls.Add);
        Assert.Equal(new[] { BaseDir }, calls);
    }

    [Fact]
    public void UsableBaseDir_DoesNotTouchFallback()
    {
        // フォールバック用フォルダーは実ディレクトリを作る副作用を持つので、
        // 要らないときは呼ばれないことを固定する。
        int fallbackCalls = 0;
        PreviewVirtualHostMapping.Apply(
            BaseDir,
            baseDirExists: true,
            () =>
            {
                fallbackCalls++;
                return Fallback;
            },
            _ => { }
        );
        Assert.Equal(0, fallbackCalls);
    }

    [Theory]
    // 実測した型 (設計書 §13.1)。確認と登録の間に共有が落ちる競合で出る。
    [InlineData(typeof(DirectoryNotFoundException))]
    // 未実測の想定: アクセス拒否。プレビュー自体を失敗させるより空フォルダーへ倒す。
    [InlineData(typeof(UnauthorizedAccessException))]
    public void MapFailure_FallsBackInsteadOfLeavingUnmapped(Type exceptionType)
    {
        // 登録が失敗しても未マップへ戻さない網 (1 回目 baseDir → 2 回目 fallback の順序も固定)。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(
            BaseDir,
            baseDirExists: true,
            () => Fallback,
            folder =>
            {
                calls.Add(folder);
                if (calls.Count == 1)
                    throw (Exception)Activator.CreateInstance(exceptionType)!;
            }
        );
        Assert.Equal(new[] { BaseDir, Fallback }, calls);
    }

    [Fact]
    public void FallbackFailure_Propagates()
    {
        // 2 回とも失敗したら握り潰さない (呼び出し側の catch がプレビュー失敗を出す)。
        var calls = new List<string>();
        Assert.Throws<DirectoryNotFoundException>(() =>
            PreviewVirtualHostMapping.Apply(
                BaseDir,
                baseDirExists: true,
                () => Fallback,
                folder =>
                {
                    calls.Add(folder);
                    throw new DirectoryNotFoundException("boom");
                }
            )
        );
        Assert.Equal(new[] { BaseDir, Fallback }, calls);
    }

    [Fact]
    public void UnexpectedException_IsNotSwallowed()
    {
        // 想定外の例外型までフォールバックへ倒すと、原因不明の「画像が出ない」に化ける。
        Assert.Throws<InvalidOperationException>(() =>
            PreviewVirtualHostMapping.Apply(
                BaseDir,
                baseDirExists: true,
                () => Fallback,
                _ => throw new InvalidOperationException("boom")
            )
        );
    }
}
