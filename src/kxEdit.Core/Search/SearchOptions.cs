namespace kxEdit.Core.Search;

/// <summary>
/// 検索の照合条件。置換文字列は含めない（照合に専念し grep でも再利用するため、
/// 置換文字列は置換メソッドへ都度渡す）。
/// </summary>
public sealed record SearchOptions(
    string Pattern,
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false
);
