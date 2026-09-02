using System.IO;

namespace kxEdit.App;

/// <summary>
/// V-2 + PR #57 申し送り: プレビューの仮想ホストマッピング先を決める純粋ロジック。
/// <para>
/// <b>不変条件: マッピングは常に在る。</b> 未マップのまま文書を出すと、本文中の相対 URL は
/// <see cref="kxEdit.Core.Text.MarkdownRenderer.PreviewBaseHref"/> 基準で絶対化済みなので
/// <c>https://kxedit.preview/...</c> が<b>実 DNS 解決</b>へ出る (監査 §9 V-2)。
/// WebView2 のドキュメントは仮想ホストについて "There is no DNS resolution for host name" と
/// 明記しており、<b>マッピングさえ張れば DNS は起きない</b>。
/// </para>
/// <para>
/// <b>実在判定は呼び出し側の責務。</b> <c>SetVirtualHostNameToFolderMapping</c> は内部で
/// 実在確認をしており、不存在なら <see cref="DirectoryNotFoundException"/>、不達な UNC では
/// <b>21 秒返らない</b> (設計書 §13.1 の実測)。しかも <c>CoreWebView2</c> は UI スレッド専有で
/// 登録を背景スレッドへ逃がせない。だから呼び出し側 (<see cref="MarkdownPreviewForm"/>) が
/// 境界付きプローブで実在を確定し、<b>確定した結果だけ</b>を <c>baseDirExists</c> で渡す。
/// </para>
/// </summary>
internal static class PreviewVirtualHostMapping
{
    /// <summary>
    /// マッピング先を決めて <paramref name="map"/> へ渡す。<paramref name="map"/> が
    /// I/O 系の例外で失敗しても<b>未マップにはしない</b>。
    /// </summary>
    /// <param name="baseDir">.md のフォルダー。未保存タブでは null。</param>
    /// <param name="baseDirExists">
    /// 呼び出し側が境界付きで確定した実在フラグ。false なら <paramref name="baseDir"/> は使わない。
    /// </param>
    /// <param name="emptyFallback">マッピング専用の空フォルダーを作って返す (必要時のみ呼ぶ)。</param>
    /// <param name="map">
    /// <c>SetVirtualHostNameToFolderMapping(PreviewVirtualHost, folder, Allow)</c> の薄いラッパ。
    /// デリゲートにしてあるのは WebView2 実体なしでテストするため。
    /// </param>
    internal static void Apply(
        string? baseDir,
        bool baseDirExists,
        Func<string> emptyFallback,
        Action<string> map
    )
    {
        ArgumentNullException.ThrowIfNull(emptyFallback);
        ArgumentNullException.ThrowIfNull(map);

        if (string.IsNullOrEmpty(baseDir) || !baseDirExists)
        {
            map(emptyFallback());
            return;
        }

        try
        {
            map(baseDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 未マップへ戻さない。ここで諦めると V-2 の状態が復活する。
            // 実在確認と登録の間に共有が落ちる競合が主な経路 (DirectoryNotFoundException)。
            System.Diagnostics.Trace.TraceWarning(
                $"プレビュー仮想ホストのマッピングに失敗したので空フォルダーへ倒す: {ex.Message} ({baseDir})"
            );
            map(emptyFallback()); // ここが失敗したら呼び出し側へ送る (握り潰さない)
        }
    }
}
