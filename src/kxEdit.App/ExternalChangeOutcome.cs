namespace kxEdit.App;

/// <summary>
/// M-18(設計 2026-09-03 §3.4): <see cref="FileController.CheckExternalChange"/> の結果。
/// ゼロ値を <see cref="Skipped"/> に置く(初期化漏れが「読み直した」に転ばないように。
/// <c>PathNormalizeStatus.TimedOut</c> と同じ流儀)。
/// </summary>
public enum ExternalChangeOutcome
{
    /// <summary>判定しなかった(無題・観測値なし・ディスク側取得失敗・再入中)。</summary>
    Skipped,

    /// <summary>ディスクの更新時刻が観測値と一致、または「読み直さない」と答えた値のまま
    /// (= 聞くべき変更が無い)。呼出側は何もしない。</summary>
    NoChange,

    /// <summary>変更あり → 読み直した。呼出側は発声と CSV モードの復帰を行う。</summary>
    Reloaded,

    /// <summary>変更あり → 読み直さなかった(ディスクの値を <see cref="DocumentState.AcknowledgedWriteTimeUtc"/> に憶えた
    /// = 次の変更まで聞かない。観測値=本文の基準は不変で、保存直前の確認は生きたまま)。</summary>
    Kept,

    /// <summary>変更あり → 読み直そうとして失敗(<c>LoadInto</c> がエラーを出した)。観測値は不変。
    /// 観測値も憶えた値も動かさないので、エラーダイアログを閉じた直後の <c>OnActivated</c> で同じ確認が
    /// 再び出る(一過性のロックならそれが再試行になる。「いいえ」で止まる)。</summary>
    ReloadFailed,
}
