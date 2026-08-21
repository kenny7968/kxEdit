namespace kxEdit.Core.Backup;

/// <summary>
/// 本文の変化検出用の 64bit 署名（FNV-1a ＋ 長さ）。プロセス間で安定（GetHashCode のような
/// 乱択化が無い）。tick 抑止（無変化なら書かない）に使う。暗号学的強度は不要だが、32bit より
/// 衝突を大幅に減らし、クラッシュ直前の最終編集を取り逃すリスクを下げる。
/// </summary>
public static class ContentSignature
{
    public static long Of(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (char c in s)
        {
            h ^= c;
            h *= prime;
        }
        h ^= (ulong)s.Length; // 同ハッシュ・異長を分離
        h *= prime;
        return unchecked((long)h);
    }
}
