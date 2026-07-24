namespace yEdit.Core.Backup;

/// <summary>
/// BackupRecord.Id が正規の GUID N 形式(32 桁 hex・区切りなし)かを検証する。
/// ここを通さない Id は、%APPDATA%\yEdit\backups 配下に攻撃者が植えた不正 JSON の
/// 可能性がある(パストラバーサル入口を BackupStore.Write / Delete で塞ぐための白リスト)。
/// BK-L-8 (2026-07-20 v0.11): 大文字/mixed case は攻撃者 JSON 由来を疑い拒否する
/// (BackupStore は Guid.NewGuid().ToString("N") 経由で必ず lowercase を生成するので、
/// 大文字が混じった Id は「外部から差し込まれた」痕跡。NTFS の case-insensitive path と
/// id 文字列比較の齟齬による衝突リスクも同時に潰す)。
/// Task 1 レビュー (2026-07-23): Guid.TryParseExact は Unicode 空白を Trim してから
/// パースするため、空白パディング付き Id(" &lt;32hex&gt;" 等)の混入を Length=32 固定で拒否する
/// (N 形式は厳密 32 文字=自プロセス生成 Id への回帰ゼロ)。
/// </summary>
public static class BackupIdValidator
{
    public static bool IsValid(string? id) =>
        id is { Length: 32 }
        && Guid.TryParseExact(id, "N", out _)
        && id.Equals(id.ToLowerInvariant(), StringComparison.Ordinal);
}
