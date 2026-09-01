using kxEdit.App.Speech;
using kxEdit.Core.Csv;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// 新CSVモード（グリッド型ナビゲーション）の配線。CSVモード中は EditorControl.ReadOnly=true で
/// 本文を編集不可にし、素キーのコマンドでセル移動・読み上げを行う。現在セルは
/// DocumentState.CsvRow/CsvCol を真実源にする。
/// 【P6 変更】: 編集エンジンが自作 EditorControl(v2 UIA 単一経路)に統一されたため、
/// P5 まで CSV モード中にフォーカスを Document.CsvSink(1×1px シンク)へ退避していた仕組みを
/// 撤去し、フォーカスは常に EditorControl(=Document.FocusTarget)へ向かう。CsvSink 自体は
/// §0-8「無効化のみで残す」に従い生成のみ残す(P7 で完全撤去)。読み上げは Announcer に一本化。
/// システムキャレットも動かさない（可視域スクロールはキャレット無移動の
/// EnsureVisibleCharRange）。RaiseUiaSelectionEvents=false は UIA 系 SR 向けの
/// 防御（EditorControl OnGotFocus の明示 SelectionChangedEvent で行を読まれるのを防ぐ）。
/// F2 は CsvCellEditor に委譲し、終了時の復帰先は FocusTarget(=Editor)。
/// </summary>
public sealed class CsvController : IDisposable
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;
    private readonly CsvCellEditor _editor = new();
    private readonly ICellPicker _cellPicker;

    public CsvController(DocumentManager docs, IAnnouncer announcer, ICellPicker cellPicker)
    {
        _docs = docs;
        _announcer = announcer;
        _cellPicker = cellPicker;
    }

    // CA1001 対応(Sub 3.4-B): _editor(CsvCellEditor) を new() で所有生成しているため
    // IDisposable を実装。_docs はコンストラクタ注入(MainForm 所有)のため破棄しない。
    // 呼び出し元は MainForm.Dispose(bool) で本 Dispose を呼び出す(Editor.Abort が冪等)。
    public void Dispose() => _editor.Dispose();

    /// <summary>F2 編集オーバーレイ表示中か（MainForm がキー横取りを抑止するのに使う）。</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>進行中の F2 編集を強制破棄する（タブ閉じ/切替時に呼ぶ・冪等）。</summary>
    public void AbortEdit() => _editor.Abort();

    /// <summary>CSVモードを手動でトグルする。ON 時は読取専用化＋現在セルを確定して読み上げ。</summary>
    public void ToggleMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing)
            return;
        if (!doc.State.CsvMode)
            TryEnterMode(doc);
        else
            ExitMode(doc);
    }

    /// <summary>
    /// CSVモードへ入る（手動トグルと .csv 自動モードの共通経路）。解析不可なら通知して false を返し、
    /// 通常モードのまま残す。読取専用化・UIA 抑止・シンク退避・初期セル確定・読み上げは従来の ON 側と同一。
    /// </summary>
    public bool TryEnterMode(Document doc)
    {
        if (doc.State.CsvMode || _editor.IsEditing)
            return false;
        var csv = doc.ParseCsv();
        if (!csv.Ok)
        {
            doc.ClearCsvCache(); // モードに入らないのに失敗パース＋旧全文を文書寿命まで抱えない
            _announcer.Say(CsvAnnounceFormatter.ParseError);
            return false; // 解析不可ならモードに入らない
        }
        doc.State.CsvMode = true;
        doc.Editor.ReadOnly = true;
        // UIA 系 SR 向け防御: モード遷移中の EditorControl OnGotFocus で
        // 明示 TextSelectionChangedEvent を出して行を読まれるのを防ぐ。
        doc.Editor.RaiseUiaSelectionEvents = false;
        if (csv.Rows.Count == 0)
        {
            doc.Editor.ClearHighlight();
            doc.FocusTarget.Focus(); // データ無しでもフォーカスは編集領域(P6=Editor)へ
            _announcer.Say(CsvAnnounceFormatter.ModeOn);
            return true;
        }
        // ON 時のみ、その時点のキャレット位置から初期セルを導出する（以降はキャレットではなく状態を真実源にする）。
        var (row, col) = csv.FindCell(doc.Editor.CaretCharOffset);
        doc.State.CsvRow = row;
        doc.State.CsvCol = col;
        ApplyCell(doc.Editor, csv, row, col, announce: false); // ハイライト＋スクロール＋シンクへフォーカス
        var f = csv.GetField(row, col);
        _announcer.Say(
            f is null
                ? CsvAnnounceFormatter.ModeOn
                : CsvAnnounceFormatter.ModeOn
                    + " "
                    + CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1)
        );
        return true;
    }

    /// <summary>CSVモードを抜けて通常編集へ戻す（既存 OFF 側の移設・無変更）。</summary>
    private void ExitMode(Document doc)
    {
        var csv = doc.ParseCsv();
        doc.State.CsvMode = false; // 先に解除（エディタ GotFocus のシンク退避ガードを外す）
        doc.Editor.ReadOnly = false;
        doc.Editor.ClearHighlight();
        // モード中に動かなかったキャレットを最終セル位置へ復帰させ、編集領域へフォーカスを返す。
        // 以降は通常編集なので、SR がフォーカス獲得で現在行を読むのは標準挙動として許容。
        if (csv.Ok && csv.Rows.Count > 0)
        {
            var f = csv.GetField(doc.State.CsvRow, doc.State.CsvCol);
            if (f is not null)
                doc.Editor.MoveCaretCharOffset(f.Start);
        }
        // キャレット復帰の後に再有効化し、復帰が同期経路で TextSelectionChangedEvent を
        // 出すケースを塞ぐ（通常編集の SR 挙動へ復帰）。SCN_UPDATEUI は次ペイントまで
        // 遅延し得るため遅延配送までは塞げない（二重読み解消は実機で要確認）。
        doc.Editor.RaiseUiaSelectionEvents = true;
        doc.Editor.Focus();
        doc.ClearCsvCache(); // 通常編集へ戻るのでパース結果を保持しない（メモリ解放）
        _announcer.Say(CsvAnnounceFormatter.ModeOff);
    }

    // ---- 移動（読み上げ付き） ----
    public void Move(Direction dir)
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col))
            return;
        var t = csv.MoveCell(row, col, dir);
        if (t is null)
        {
            _announcer.Say(EdgeMessage(dir));
            return;
        }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    public void MoveRowStart()
    {
        if (TryContext(out var ed, out var csv, out var r, out _))
            ApplyTarget(ed, csv, csv.RowStart(r));
    }

    public void MoveRowEnd()
    {
        if (TryContext(out var ed, out var csv, out var r, out _))
            ApplyTarget(ed, csv, csv.RowEnd(r));
    }

    public void MoveColumnTop()
    {
        if (TryContext(out var ed, out var csv, out _, out var c))
            ApplyTarget(ed, csv, csv.ColumnTop(c));
    }

    public void MoveColumnBottom()
    {
        if (TryContext(out var ed, out var csv, out _, out var c))
            ApplyTarget(ed, csv, csv.ColumnBottom(c));
    }

    public void MoveTopLeft()
    {
        if (TryContext(out var ed, out var csv, out _, out _))
            ApplyTarget(ed, csv, csv.TopLeft());
    }

    public void MoveBottomRight()
    {
        if (TryContext(out var ed, out var csv, out _, out _))
            ApplyTarget(ed, csv, csv.BottomRight());
    }

    /// <summary>セル指定移動(G)。「行,列」入力→範囲検証→移動。ダイアログは ICellPicker 経由(Stage 6)。</summary>
    public void GoToCell()
    {
        if (!TryContext(out var ed, out var csv, out var row, out var col))
            return;
        var result = _cellPicker.Pick(OwnerFormOf(ed), row + 1, col + 1);
        switch (result.Kind)
        {
            case CellPickKind.Canceled:
                return; // 無音(現行挙動)
            case CellPickKind.InvalidFormat:
                _announcer.Say(CsvAnnounceFormatter.BadCellFormat);
                return;
            case CellPickKind.Ok:
                var t = csv.GoTo(result.Row1 - 1, result.Col1 - 1);
                if (t is null)
                {
                    _announcer.Say(CsvAnnounceFormatter.OutOfRange);
                    return;
                }
                ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
                return;
            default:
                throw new InvalidOperationException($"Unknown CellPickKind: {result.Kind}");
        }
    }

    // ---- 読み上げのみ（移動なし） ----
    /// <summary>現在セルを読み上げる（Tab）。</summary>
    public void ReadCurrent()
    {
        if (!TryContext(out _, out var csv, out var row, out var col))
            return;
        var f = csv.GetField(row, col);
        if (f is null)
        {
            _announcer.Say(CsvAnnounceFormatter.CannotMove);
            return;
        }
        _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    /// <summary>現在列の最上段セルを読み上げる（C）。</summary>
    public void ReadColumnTop()
    {
        if (!TryContext(out _, out var csv, out _, out var col))
            return;
        var t = csv.ColumnTop(col);
        var f = t is null ? null : csv.GetField(t.Value.row, t.Value.col);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在行の左端セルを読み上げる（R）。</summary>
    public void ReadRowHead()
    {
        if (!TryContext(out _, out var csv, out var row, out _))
            return;
        var f = csv.GetField(row, 0);
        _announcer.Say(CsvAnnounceFormatter.Header(f?.Value ?? ""));
    }

    /// <summary>現在セルを F2 編集する。確定で CSV 直列化→本文反映→再ハイライト＋読み上げ。</summary>
    public void BeginEdit()
    {
        if (_editor.IsEditing)
            return;
        if (!TryContext(out var ed, out var csv, out var row, out var col))
            return;
        // 開始時点のセル。読まれるのは次の 4 か所だけで、いずれも _editor.Begin が戻るまでに
        // 同期的に読み切られる（CsvCellEditor は CsvField をフィールドへ保存しない。持つのは
        // _box / _closing / _refocus / _onCommit / _onCancel の 5 つ）:
        //   1. 直下の EnsureVisibleCharRange(f.Start, f.Length) —— これは BeginEdit 自身が読む
        //   2. CsvCellEditor.Begin の PointFromCharOffset(field.Start) —— オーバーレイの配置座標
        //   3. CsvCellEditor.Begin の TextBox.Text = field.Value —— 編集の初期値
        //   4. 直下の startValue = NormalizeEols(f.Value) —— 確定時の同一性検査に使う開始値
        // 4 だけは唯一「f 由来の値がクロージャの向こう側へ渡る」箇所だが、渡るのは
        // **正規化済みの値のコピー**(string)だけで Start / Length は渡らない。
        // 確定時の書込先へは持ち越さない（M-25: onCommit 参照）。
        // csv は TryContext がメモ化済みの現在パース（=開始時点のスナップショット）。
        var f = csv.GetField(row, col);
        if (f is null)
        {
            _announcer.Say(CsvAnnounceFormatter.CannotMove);
            return;
        }
        // オーバーレイの配置座標（PointFromCharOffset）は可視領域基準なので、
        // ナビ後にリサイズ等で当該セルが視野外へずれていた場合に備えて明示的に可視化する。
        ed.EnsureVisibleCharRange(f.Start, f.Length);

        // 確定時の同一性検査に要る値を、ここでスカラーとして取り出す。
        // CsvField f そのものをクロージャへ捕捉してはいけない —— Start / Length が構造的に
        // 残り、Task 2 で消した陳腐化の余地が f 経由で復活する。設計書 §4 の芯
        //(陳腐化しうる値を持ち越さない)は字面で守られて初めて後続の改変に耐える。
        // 正規化は開始時に 1 回だけ行う(単一セルは最大 8M chars = CsvParser.MaxFieldChars)。
        string startValue = CsvWriter.NormalizeEols(f.Value);
        int startRowCount = csv.Rows.Count;
        int startColCount = csv.Rows[row].Count;

        var doc = _docs.Active!; // TryContext 成功時は Active 非 null。タブ切替は AbortEdit が
        // 先に走るため、確定/取消コールバック時点でも同一文書が対象。
        _editor.Begin(
            ed,
            f,
            doc.FocusTarget, // P6: 復帰先は FocusTarget=Editor(旧: CsvSink)
            onCommit: text =>
            {
                // M-25(2026-09-01): 開始時の f.Start / f.Length を**持ち越さない**。F2 開始から
                // 確定までの間に本文が差し替わりうるため(到達経路 = F2 編集中の Ctrl+S →
                // FileController.SaveDocument の ConvertEols。設計書 §2.2 / §3)、確定時の
                // パースから (row, col) で解決し直す。row / col は編集中に動かない
                // (ナビは TryContext 冒頭の _editor.IsEditing で撥ねられる)。
                // ParseCsv はスナップショット参照が同じなら開始時と同一インスタンスを返すので、
                // 本文が変わっていない通常経路に追加コストは無い。
                var csvNow = doc.ParseCsv();
                var target = csvNow.Ok ? csvNow.GetField(row, col) : null;
                // (row, col) が生きていても、本文が変わっていればそこが指すセルは別物でありうる
                // (行が消える・列が増える等)。そこへ書けば座標が陳腐化しているのと同じ
                // データ破壊になるので、「同じセルらしさ」が崩れていたら書かない。
                //  - 値の一致だけでは弱い。「別セルになったが値は同じ」を素通しする
                //    (CSV では空セルや繰り返し値がありふれている)ので、形も見る。
                //  - EOL を正規化して比べるのは、ConvertEols がセル内改行を書き換えて
                //    Value 自体を変えるため(設計書 §4.3)。
                // これは同一性の**代用**であって同一性の証明ではない。行数・列数・値が
                // すべて一致する別セル(例: 2 行の入れ替え)は弁別できない。
                // 並べ替えの注意: `csvNow.Rows[row]` を **行数比較より前へ出さないこと**
                // (開始時に row < startRowCount は保証されるが、行数が減っていれば範囲外になる)。
                // 逆に `csvNow.Rows.Count != startRowCount` を先頭へ動かすのは安全
                // (それが false なら csvNow.Rows.Count == startRowCount > row が成り立つ)。
                if (
                    target is null
                    || csvNow.Rows.Count != startRowCount
                    || csvNow.Rows[row].Count != startColCount
                    || !string.Equals(
                        CsvWriter.NormalizeEols(target.Value),
                        startValue,
                        StringComparison.Ordinal
                    )
                )
                {
                    // M-1: 書かないと決めた枝でも、セルが現存しているなら強調は現在の (row, col)
                    // へ戻す。本文を差し替える経路(現状は ConvertEols)は差し替えの直後に
                    // _cellHighlight を捨てる(EditorControl.ConvertEols)ので、そうした経路から
                    // **将来この拒否枝へ入ったとき**に強調を失ったまま残さないための復元である。
                    // 現行配線では拒否枝そのものが到達不能で(ConvertEols は行列構造も正規化後の
                    // Value も変えないので必ず受理枝へ行く —— ただしこの「変えない」は T1 / T2 の
                    // 2 fixture 分だけが実測で、一般には設計書 §4.4 の**論証**である。§8.9 / §8.27 の
                    // 留保つき)、受理枝は末尾の ApplyCell が強調を張り直す。
                    // つまりこの復元が効くのは到達不能枝だけ=安全宣言に使わないこと。
                    // 到達したときに晴眼・弱視ユーザーが現在セルを見失わない側へ倒しておく
                    // (CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
                    // target is not null は csvNow.Ok == true を含意する(target の作り方から)ので
                    // ApplyCell に渡す csvNow は正常なパース結果である。target is null のときは
                    // 強調すべきセルがそもそも無いので何もしない。
                    if (target is not null)
                        ApplyCell(ed, csvNow, row, col, announce: false);
                    _announcer.Say(CsvAnnounceFormatter.CommitTargetChanged);
                    return;
                }
                string serialized = CsvWriter.EscapeField(text);
                // ReadOnly の昇降は try/finally で括る(FileController.WriteToPath の
                // ConvertEols 前後と同じ idiom)。ReplaceCharRange が throw しても
                // CSV モードが読み書き可のまま残らないようにする。
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                try
                {
                    ed.ReplaceCharRange(target.Start, target.Length, serialized);
                }
                finally
                {
                    ed.ReadOnly = wasRo;
                }
                var csv2 = doc.ParseCsv();
                if (csv2.Ok)
                    ApplyCell(ed, csv2, row, col, announce: true);
                else
                    _announcer.Say(CsvAnnounceFormatter.ParseError);
            },
            onCancel: () =>
            {
                var csv2 = doc.ParseCsv();
                if (csv2.Ok && csv2.Rows.Count > 0)
                    ApplyCell(ed, csv2, row, col, announce: false);
            }
        );
    }

    // ==================== 内部 ====================

    /// <summary>パースして現在 (row,col) を得る。CSVでない/解析不可/データ無しは読み上げて false。
    /// (row,col) は DocumentState を真実源とし、パース結果の行列数へクランプする（本文編集で
    /// 行/列が減っても範囲外を指さないように補正）。</summary>
    private bool TryContext(out EditorControl ed, out CsvDocument csv, out int row, out int col)
    {
        ed = null!;
        csv = null!;
        row = 0;
        col = 0;
        if (_editor.IsEditing)
            return false; // F2 編集中はメニュー経由のナビ/読み上げを抑止（マウス経路の保護）
        var doc = _docs.Active;
        if (doc is null || !doc.State.CsvMode)
            return false;
        ed = doc.Editor;
        csv = doc.ParseCsv();
        if (!csv.Ok)
        {
            ed.ClearHighlight();
            _announcer.Say(CsvAnnounceFormatter.ParseError);
            return false;
        }
        if (csv.Rows.Count == 0)
        {
            ed.ClearHighlight();
            _announcer.Say(CsvAnnounceFormatter.NoData);
            return false;
        }
        row = ClampRow(csv, doc.State.CsvRow);
        col = ClampCol(csv, row, doc.State.CsvCol);
        doc.State.CsvRow = row; // クランプ結果を書き戻し（次回以降の整合）
        doc.State.CsvCol = col;
        return true;
    }

    /// <summary>EditorControl の親 Form を契約集中で解決する(Batch D Task 11)。
    /// 現状の呼び出しは GoToCell のみ。呼び出し文脈では ed は必ず MainForm 上に載っている
    /// (CSV モード進入=タブ配置済み)ため FindForm() は非 null。null になれば CSV モード未進入=
    /// そもそも本メソッドに到達しないという実装契約を null-forgiving `!` ではなく明示例外で固定する
    /// (NRT 抑止の意図を型レベルで表現)。将来 BeginEdit がダイアログ経路を持てば同契約で流用可。</summary>
    private static Form OwnerFormOf(EditorControl ed) =>
        ed.FindForm()
        ?? throw new InvalidOperationException("EditorControl is not hosted in a Form");

    private static int ClampRow(CsvDocument csv, int r) =>
        r < 0 ? 0 : (r >= csv.Rows.Count ? csv.Rows.Count - 1 : r);

    private static int ClampCol(CsvDocument csv, int row, int c)
    {
        int w = csv.Rows[row].Count;
        if (w <= 0)
            return 0;
        return c < 0 ? 0 : (c >= w ? w - 1 : c);
    }

    private void ApplyTarget(EditorControl ed, CsvDocument csv, (int row, int col)? t)
    {
        if (t is null)
        {
            _announcer.Say(CsvAnnounceFormatter.CannotMove);
            return;
        }
        ApplyCell(ed, csv, t.Value.row, t.Value.col, announce: true);
    }

    /// <summary>(row,col) のセルへ ハイライト＋可視域スクロール＋DocumentState 更新＋必要なら読み上げ。
    /// システムキャレットは動かさない（SR の自動読み上げ発火を避け、Announcer 一本に集約する）。
    /// フォーカスは FocusTarget(=P6 では常に Editor)に保つ。</summary>
    private void ApplyCell(EditorControl ed, CsvDocument csv, int row, int col, bool announce)
    {
        var f = csv.GetField(row, col);
        if (f is null)
        {
            _announcer.Say(CsvAnnounceFormatter.CannotMove);
            return;
        }
        ed.HighlightCharRange(f.Start, f.Length);
        ed.EnsureVisibleCharRange(f.Start, f.Length);
        var doc = _docs.Active;
        if (doc is not null)
        {
            doc.State.CsvRow = row;
            doc.State.CsvCol = col;
            doc.FocusTarget.Focus(); // P6: FocusTarget=Editor 固定(RaiseUiaSelectionEvents=false で SR 誤読み抑止)
        }
        if (announce)
            _announcer.Say(CsvAnnounceFormatter.Cell(f.Value, row + 1, col + 1));
    }

    private static string EdgeMessage(Direction dir) =>
        dir switch
        {
            Direction.Left => CsvAnnounceFormatter.LeftEdge,
            Direction.Right => CsvAnnounceFormatter.RightEdge,
            Direction.Up => CsvAnnounceFormatter.TopEdge,
            _ => CsvAnnounceFormatter.BottomEdge,
        };
}
