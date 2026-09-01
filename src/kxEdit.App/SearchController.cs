using System.Text.RegularExpressions;
using kxEdit.App.Speech;
using kxEdit.Core.Buffers;
using kxEdit.Core.Csv;
using kxEdit.Core.Search;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// 検索・置換の統括。Core 照合と EditorControl の選択/置換を仲介し、結果を
/// ダイアログのステータス＋UIA 通知で SR に伝える。対象はアクティブ文書を毎回解決する。
/// </summary>
public sealed class SearchController
{
    private readonly DocumentManager _docs;
    private readonly IWin32Window _owner;
    private readonly IAnnouncer _announcer;
    private readonly Func<FindReplaceCallbacks, IFindReplaceView> _viewFactory;
    private IFindReplaceView? _view;

    // 直前に選択したヒット。4 つ組で持つ理由:
    //   Hit             = 照合が返した生の UTF-16 範囲。置換はこれを対象にする。
    //   SelStart/SelEnd = それを SelectCharRange した「結果」を読み戻した値。
    //   Snap            = 捕捉時のスナップショット(参照同一性で文書の編集を検出する)。
    // A-14(2026-08-29): 選択は CRLF / サロゲートを 1 論理文字として扱うため
    // (TextBoundary.SnapToLogicalCharStart)、Hit と実選択は一致しないことがある。
    // 例: CRLF 文書の \n ヒット (4,1) の実選択は [3,5)、\r ヒット (3,1) の実選択は [3,3) のゼロ幅。
    // ゆえに「現ヒットが生きているか」を Hit と選択の直接比較で判定してはならない。
    // 読み戻しにしているのは、スナップ規則を App 層へ複製しないため(規則が変わっても追随する)。
    // Snap を弱参照にする理由は _selectionScope と同じ(判定は変わらず、旧ピース木をピン留めしない)。
    private (WeakReference<TextSnapshot> Snap, MatchSpan Hit, int SelStart, int SelEnd)? _lastHit;

    // 「選択範囲のみ」ON 時に捕捉した置換対象範囲。捕捉元の TextSnapshot を一緒に持つ:
    // 位置は絶対 char index なので、捕捉後に文書が編集されると同じ数値が別の中身を指す。
    // 参照同一性で世代を見て、ずれていたら使わない。TextBuffer.Modified と同系だが、
    // あちらはピース木の Root 比較、こちらはスナップショット参照比較(Root は internal で
    // App から見えない)。ゆえに Undo で捕捉時と同一内容へ戻しても「陳腐化」と扱う=安全側。
    // 保持は弱参照にする: 捕捉元が生きていれば必ず現在のスナップショットと同一なので判定は
    // 変わらず、開き直し・復元で捨てられた旧バッファのピース木をピン留めしない
    // (下の searcher と同じ責任。回収済みなら陳腐化として拒否=安全側)。
    // A-11(2026-08-28)訂正: ここには「EOL 変換(ReplaceSource)で捨てられた」も挙げていたが、
    // **もう成立しない**。ConvertEols は in-place の 1 Undo 単位になり、変換前のピース木は
    // Undo 履歴(TextBuffer._history)が強参照で保持する。弱参照にしていても回収されない
    // = この一文が謳っていた防御は EOL 変換については存在しない(設計書 2026-08-28 §10.15)。
    // 弱参照そのものは開き直し・復元・タブクローズに対して有効なので維持する。
    private (WeakReference<TextSnapshot> Snap, int Start, int End)? _selectionScope;

    // 照合条件が変わるまで searcher を使い回す。作り直すと内部の Regex が再コンパイルされ
    // (インスタンス生成の Regex は .NET の静的キャッシュに乗らない)、MaterializedSearchStrategy の
    // 材質化キャッシュも毎回捨てられる(打鍵ごとの UpdateCount で効く)。
    // 保持する側の責任: キャッシュは TextSnapshot → ピース木 → バイト配列を強参照するため、
    // 破棄トリガ(条件変化・文書切替・文書クローズ・ユーザーの検索終了)を漏らすと
    // 閉じたタブの文書がまるごと生き残る。DropSearcher を呼ぶ経路を減らさないこと。
    private SearchOptions? _searcherOptions;
    private SnapshotSearcher? _searcher;

    public SearchController(
        DocumentManager docs,
        IWin32Window owner,
        IAnnouncer announcer,
        Func<FindReplaceCallbacks, IFindReplaceView> viewFactory
    )
    {
        _docs = docs;
        _owner = owner;
        _announcer = announcer;
        _viewFactory = viewFactory;
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null; // 別文書の歩進状態を持ち越さない
            _selectionScope = null; // 別文書へ切替時は捕捉済みスコープも無効化
            DropSearcher(); // 別文書の材質化キャッシュを持ち越さない(破棄トリガ ii-a)
            if (_view?.Visible == true)
                UpdateCount(); // 表示中なら新アクティブで件数を更新
        };
        // 破棄トリガ ii-b: タブクローズ。ActiveDocumentChanged は選択タブ削除で発火が保証されず、
        // 非アクティブタブのクローズでは切替自体が起きないため、こちらが唯一の通知源。
        _docs.DocumentClosed += (_, _) => DropSearcher();
    }

    private EditorControl? ActiveEditor => _docs.Active?.Editor;

    // CSVモード中は本文が読取専用で置換が無反映になるため、置換系を抑止して誤成功通知を防ぐ。
    private bool IsCsvModeActive => _docs.Active?.State.CsvMode == true;

    public void OpenFind() => Open(replaceMode: false);

    public void OpenReplace() => Open(replaceMode: true);

    private void Open(bool replaceMode)
    {
        if (_view is null || _view.IsDisposed)
        {
            _view = _viewFactory(
                new FindReplaceCallbacks(
                    FindNext: FindNext,
                    FindPrev: FindPrev,
                    ReplaceOne: ReplaceOne,
                    ReplaceAll: ReplaceAll,
                    UpdateCount: UpdateCount,
                    InSelectionToggled: OnInSelectionToggled
                )
            );
            // 破棄トリガ iii。購読は生成の直後=この if の中に置くこと(外に出すと Ctrl+F の
            // たびに多重購読してハンドラが単調増加する)。旧ビューごと捨てるので -= は要らない。
            _view.Dismissed += (_, _) => DropSearcher();
            // ビューを作り直す=前のダイアログのセッションは終わっている(閉じるボタン経由でない
            // 破棄=owner ごとのクローズでは Dismissed が来ない)。新セッションを持ち越しゼロで始める。
            DropSearcher();
        }
        _view.SetMode(replaceMode);
        _view.ShowAndFocus(_owner); // 従来の「!Visible なら Show→Activate→FocusPattern」と同順(ビュー側に集約)
        UpdateCount();
    }

    private SearchOptions? CurrentOptions()
    {
        var d = _view;
        if (d is null || string.IsNullOrEmpty(d.Pattern))
            return null;
        return new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex);
    }

    /// <summary>照合条件に対応する searcher を返す(条件が変われば作り直す)。条件が無効なら null。
    /// <see cref="SearchOptions"/> は record なので <c>!=</c> は構造的比較になる。
    /// <para>
    /// 4 つの呼び出し側(UpdateCount / Find / ReplaceOne / ReplaceAll)は直前で
    /// <see cref="CurrentOptions"/> の null を弾いているので、実際にここが null を返すことはない。
    /// 呼び出し側の <c>is null</c> は nullable 解決のためのガードで、無効な正規表現の扱い
    /// (<see cref="SnapshotSearcher.IsValid"/> が false)と同じ側へ倒してある。
    /// </para></summary>
    private SnapshotSearcher? ResolveSearcher()
    {
        var opts = CurrentOptions();
        if (opts is null)
        {
            // 検索語を空にしたら保持中の searcher(とキャッシュ)を落とす。
            // 素の `return null;` だと _searcher に触れないため、空にしても保持が続く。
            DropSearcher();
            return null;
        }
        if (_searcher is null || _searcherOptions != opts)
        {
            _searcher = new SnapshotSearcher(opts);
            _searcherOptions = opts;
        }
        return _searcher;
    }

    /// <summary>保持中の searcher を捨てる(材質化キャッシュごと解放する)。
    /// 冪等でなければならない=Dismissed は連続発火しうる(Escape → 再表示 → また Escape)。</summary>
    private void DropSearcher()
    {
        _searcher = null;
        _searcherOptions = null;
    }

    /// <summary>テスト観測用: 現在保持中の searcher(未解決なら null)。
    /// 保持と破棄は<b>結果値からは観測できない</b>(作り直しても同じ答えを返す)ため、
    /// 破棄トリガの網はこの参照同一性でしか書けない。実運用経路では参照しない。</summary>
    internal SnapshotSearcher? SearcherForTest => _searcher;

    /// <summary>増分カウント（移動しない）。エラー/タイムアウトはステータスのみ更新（通知しない）。</summary>
    public void UpdateCount()
    {
        var d = _view;
        if (d is null)
            return;
        // 条件が無効(検索語が空)なら null。判定を CurrentOptions() で先に済ませてしまうと
        // ResolveSearcher へ入らず、保持中の searcher が落ちない(検索語を消しても
        // 文書 1 本ぶんのキャッシュが残る)ため、null 判定はここで解決結果に対して行う。
        // 条件は CurrentOptions() の null 判定と同値=ステータスをクリアする従来挙動のまま。
        var searcher = ResolveSearcher();
        if (searcher is null)
        {
            d.SetStatus("");
            return;
        }
        if (!searcher.IsValid)
        {
            d.SetStatus("正規表現が正しくありません");
            return;
        }
        // P6 Task 11: SnapshotText 経由の全文 string 化を回避し、64MB 閾値二層化(閾値超は窓/行照合)。
        // CurrentBuffer は non-null 保証(SetSource 前も静的空 TextBuffer=Task 10 M-2)。
        // ActiveEditor が null(文書なし)なら "見つかりません" 相当。
        var snap = ActiveEditor?.CurrentBuffer.Current;
        try
        {
            int n = snap is null ? 0 : searcher.Count(snap);
            d.SetStatus(n == 0 ? "見つかりません" : $"{n} 件");
        }
        catch (RegexMatchTimeoutException)
        {
            d.SetStatus("検索式が複雑すぎます");
        }
    }

    /// <summary>次を検索。ヒットして選択を移動できたら true、それ以外(未ヒット/無効式/タイムアウト)は false。</summary>
    public bool FindNext() => Find(forward: true);

    /// <summary>前を検索。ヒットして選択を移動できたら true、それ以外(未ヒット/無効式/タイムアウト)は false。</summary>
    public bool FindPrev() => Find(forward: false);

    private bool Find(bool forward)
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        if (ed is null || opts is null)
            return false;
        var searcher = ResolveSearcher();
        if (searcher is null || !searcher.IsValid)
        {
            Announce("正規表現が正しくありません");
            return false;
        }

        // P6 Task 11: 全文 string 化を避け、TextSnapshot を直接渡す(閾値超は窓/行照合に自動切替)。
        var snap = ed.CurrentBuffer.Current;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        try
        {
            var live = LiveHit(snap, selStart, selEnd);
            MatchSpan? hit;
            if (forward)
            {
                int from = live is { } h
                    ? h.Start + Math.Max(1, h.Length) // 直前ヒットの次へ（ゼロ幅でも前進）
                    : selEnd;
                hit = searcher.FindNext(snap, from);
            }
            else
            {
                // 現ヒットがあればその始端より前を探す。スナップで選択の始端がヒットより
                // 手前へ寄ることがある(CRLF の LF ヒット)ため、selStart のままだと
                // [selStart, Hit.Start) 内のヒットを取りこぼす。スナップが起きない
                // ケースでは h.Start == selStart なので挙動不変。
                // 前方と違って Math.Max(1, Length) が要らないのは契約が非対称だから:
                // FindPrev は「開始位置が before より厳密に前」(SnapshotSearcher /
                // ISnapshotSearchStrategy の doc)なので before = h.Start で現ヒットは
                // ゼロ幅でも自動的に外れる。FindNext(from) は from を含むので自力で越える。
                int before = live is { } h2 ? h2.Start : selStart;
                hit = searcher.FindPrev(snap, before);
            }

            if (hit is null)
            {
                _lastHit = null;
                Announce("これ以上見つかりません");
                return false;
            }

            SelectHit(ed, snap, hit.Value);
            var loc = searcher.Locate(snap, hit.Value);
            // 位置不明（Locate 失敗）時は空メッセージ＝ステータスのクリアのみ（発声なし）。
            Announce(loc is { } l ? $"{l.Total} 件中 {l.Ordinal} 件目" : "");
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
            return false;
        }
    }

    /// <summary>現ヒット未選択なら次を検索して即置換、選択済なら置換して次へ(VSCode 準拠)。</summary>
    public void ReplaceOne()
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        var d = _view;
        if (ed is null || opts is null || d is null)
            return;
        if (IsCsvModeActive)
        {
            Announce(CsvAnnounceFormatter.BlockedInCsvMode);
            return;
        }
        // 委譲先(ReplaceCharRangeExact)は ReadOnly のとき何も書かずに戻るが、ここから先は
        // それを見ずにスコープを更新し成功発声する。snap2 == snap なので世代チェックを通る
        // 不正なスコープが残る。
        // **包含検査より前に置くこと**: GetExactChangeCharRange は書けない状態で空範囲を返し、
        // 空範囲はどんな包含検査も通る=このガードを削ると「検査を通る → no-op → 成功発声」に
        // なる(GetExactChangeCharRange の remarks 参照)。
        // 到達経路は実質無い(CSV モードは上で弾かれ、保存中の一時解除に ReplaceOne が
        // 割り込む経路がない)が、「呼び出し側が委譲先の no-op を見ていない」構造を消す。
        // 発声しないのは、App に「読み取り専用」を告げる既存文言が無く、
        // 新文言を足しても L5 で確認できる操作が作れないため。
        if (ed.ReadOnly)
            return;
        var searcher = ResolveSearcher();
        if (searcher is null || !searcher.IsValid)
        {
            Announce("正規表現が正しくありません");
            return;
        }

        try
        {
            // P6 Task 11: 現在バッファの Snapshot を直接渡す(閾値超は窓/行照合に自動切替)。
            var snap = ed.CurrentBuffer.Current;
            var (selStart, selEnd) = ed.GetSelectionCharRange();

            // T-3: 「選択範囲のみ」ON なら置換対象をスコープ内に閉じる。
            // ReplaceAll と同じ判定・同じ文言を使う(片方だけ通る非一貫を作らない)。
            (int Start, int End)? scope = null;
            if (d.InSelection)
            {
                if (TryResolveScope(snap) is not { } resolved)
                    return; // 理由は TryResolveScope が発声済み
                scope = resolved;
            }

            // A-14: 置換対象はまず「Find が選んだヒット本体」を使う。選択から再導出した
            // MatchSpan は CRLF / サロゲートのスナップで実ヒットとずれ、ReplacementAt が
            // 外れて「次の出現」を置換していた。
            var selSpan = new MatchSpan(selStart, selEnd - selStart);
            MatchSpan span;
            string repl;
            if (
                LiveHit(snap, selStart, selEnd) is { } hit
                && WithinScope(hit, scope) // スコープ外なら照合を試みる必要がない
                && searcher.ReplacementAt(snap, hit, d.Replacement) is { } liveRepl
            )
            {
                span = hit;
                repl = liveRepl;
            }
            else if (
                selEnd > selStart
                && WithinScope(selSpan, scope)
                && searcher.ReplacementAt(snap, selSpan, d.Replacement) is { } selRepl
            )
            {
                // 現ヒットは死んでいるが選択そのものがヒット=選択を置換する(A-14 修正前の挙動)。
                // Find を経由せず手で語を選んで「置換」を押す操作を落とさないために要る。
                // ここを削って FindNext へ落とすと、選択の「次」の出現を置換する
                // = A-14 と同じ「別の出現が置換される」不具合を作り直すことになる。
                span = selSpan;
                repl = selRepl;
            }
            else
            {
                // 現ヒットも無く選択もヒットでない(まだ検索していない / キャレットだけ動いた)。
                // G-3: 次を検索してそのまま即置換する(VSCode 準拠)。
                // 前進先が無い場合は Find と同じ「これ以上見つかりません」で終了。
                // 起点: スコープなしは従来どおり選択の終端から前進する(挙動不変)。
                // スコープありは選択の始端を起点にしてスコープ先頭まで繰り上げる。
                // 「選択範囲のみ」では選択はスコープを表しているだけで「進んだ位置」ではない。
                // 終端を使うと、選択がヒットそのものでない場合(例: 段落を選んで中の語を置換)に
                // スコープ内の未置換ヒットを飛ばす。クランプ後は hit.Start >= scope.Start が
                // 保証される(FindNext は from 以上の位置しか返さない)ので、下の包含判定で
                // 実際に効くのは End 側だけ。判定自体は他の分岐と同じ WithinScope に一本化する。
                int from = scope is { } sc ? Math.Max(selStart, sc.Start) : selEnd;
                var next0 = searcher.FindNext(snap, from);
                if (next0 is null || !WithinScope(next0.Value, scope))
                {
                    Announce("これ以上見つかりません");
                    return;
                }
                var replCand = searcher.ReplacementAt(snap, next0.Value, d.Replacement);
                // ここは通常到達しない(直前の FindNext ヒットに対して同一 snap/searcher で
                // ReplacementAt が null を返すのは異常系)。防御としてユーザーへ明示する。
                if (replCand is null)
                {
                    Announce("置換できません");
                    return;
                }
                span = next0.Value;
                repl = replCand;
            }

            // 事後条件: 実際に内容が変わる範囲がスコープに収まることを、書く前に確かめる。
            // WithinScope は生の UTF-16 span しか見ないので、ゼロ幅マッチの挿入点が論理文字の
            // 境界まで後退してスコープの外へ落ちる経路を防げない。
            // 後退条件をここで数え上げるのは EditorControl の規則の複製=規則が変われば腐るので、
            // 「実際に何を変えるか」を当人へ問う(監査 §9 V-7 の教訓)。
            // 端の扱いは非対称ではない: ゼロ幅ヒットが端ちょうど(at == check.Start /
            // at == check.End)に立つのは WithinScope が既に通した形であり、そこへ書くのは
            // 「承認された位置へ承認どおり書く」=拒否しない。本検査は WithinScope の判定を
            // 厳しくするものではなく、「承認した位置と実際に書く位置がずれていないか」を
            // 見る事後条件なので、WithinScope より狭くしてはならない。
            //
            // 実測(2026-09-01 B2 Task 4): 現時点で赤くできるのは Start 側だけで、
            // `change.End > check.End` を落とす変異は App 全件 green のまま生存する。
            // 死んだ式ではなく、現在の不変条件の下では真枝へ倒れないだけ
            // (`>` → `>=` の変異は赤くなる=境界の選び方には網が張ってある):
            //   - ゼロ幅は change.End == 後退後の挿入点 ≤ span.Start ≤ check.End(WithinScope)
            //     なので終端側を超えられない=構造的に始端側専用。
            //   - 非ゼロ幅で終端が広がるのはサロゲートペアを割ったときだけで、そのとき
            //     広げ先は「ペアの終わり」=span.End 以上で最小の論理文字境界。
            //     check.End が境界に乗っていれば必ず change.End ≤ check.End になる。
            //   - check.End が境界から外れるのは CRLF の内側だけで、そこは
            //     GetExactChangeCharRange が広げない(PR #56 §9.9 の一括置換を通すため)。
            //   - スコープ端がサロゲートペアの内側に入る経路は無い。捕捉は境界へスナップし
            //     (SetSelectionCharRange)、スコープ始端より前と終端より後の内容は
            //     スコープ内の置換では変わらない。ただし**それだけでは論証が閉じない**:
            //     不変の隣接文字が孤立 low サロゲートなら、スコープ内の置換が末尾へ high を
            //     置いた瞬間にペアが成立して端がペアの内側へ落ちうる。塞いでいるのは
            //     「本文バッファは孤立サロゲートを保持できない」(UTF-8 往復で U+FFFD へ潰れる)
            //     という別の不変条件で、ReplaceCharRangeExact の remarks が根拠、
            //     網は Editor の ReplaceCharRangeExact_LowSurrogateOnly_HighHalfCollapsesTo…
            //     と Core の Unpaired_high_surrogate_is_normalized_to_replacement_char_by_buffer。
            // 片側だけ書くと「包含を見ている」という読みが嘘になり、GetExactChangeCharRange の
            // 弁別規則が増えたときに黙って素通りする。両側を残す。
            //
            // 上の到達不能は {a, \r, \n, 😀} を 3 単位まで組んだ全テキスト × 全選択 ×
            // {ReplaceOne, ReplaceAll} × 代表パターン / 置換文字列の全数プローブでも確認したが、
            // **プローブは使い捨てで commit していない**(ゲートに乗る規模ではないため)。
            // 再監査は状態数の突き合わせではなく、上の条件でプローブを組み直して行うこと。
            if (scope is { } check)
            {
                // 前提: IsComposing でないこと(GetExactChangeCharRange の remarks の契約)。
                // 照会側は IME 未確定を確定させないが、書込側の ReplaceCharRangeExact は本体前に
                // CancelCompositionAndDefault を通す。同期配送する IME ではそこで本文が動きうる
                // =「検査した世代 ≠ 書く世代」になり、本検査そのものが素通りする
                // (この Task が塞いだ穴と同型)。ReplaceOne の起動点は検索ダイアログのボタン
                // だけで、ダイアログがフォーカスを取る時点で OnLostFocus が確定させるため到達しない。
                var change = ed.GetExactChangeCharRange(span.Start, span.Length);
                if (change.Start < check.Start || change.End > check.End)
                {
                    Announce("選択範囲の外に及ぶため置換できません");
                    return;
                }
            }

            // 論理文字の内側を指すヒット(CRLF の LF だけ等)でも巻き込みを復元する(Task 2)。
            // 戻り値=置換文字列の直後の位置。span.Start + repl.Length で導出してはいけない:
            // ゼロ幅マッチは挿入点が論理文字の境界まで後退する(ReplaceCharRangeExact は
            // ゼロ幅を広げない)ので、導出値のほうが後ろにずれて 1 論理文字ぶんを飛ばす。
            // 非ゼロ幅では両者は恒等(span.Start == s + prefixLen)なので、差が出るのは
            // ゼロ幅マッチが CRLF / サロゲートの内側に立つ場合だけ。そのとき差分窓は
            // 1 code unit しかなく、置換後の本文も選択も一致してしまう(選択も境界へ
            // スナップされるため)。弁別できるのは通知=次ヒットを 1 件飛ばすことで、fixture 次第で
            // 序数がずれる場合と「これ以上見つかりません」になる場合がある。
            // ReplaceOne_ZeroWidthHitInsideCrlf_AdvancesFromTheReturnedOffset が固定している。
            int afterRepl = ed.ReplaceCharRangeExact(span.Start, span.Length, repl);
            var snap2 = ed.CurrentBuffer.Current;
            if (scope is { } prev)
            {
                // 置換後のスコープを新世代で捕捉し直す(ReplaceAll の復帰処理と同じ理由)。
                // これが無いと次の置換が世代不一致=「陳腐化」で拒否される。
                // 終端の差分が repl.Length - span.Length ちょうどなのは、ReplaceCharRangeExact の
                // 巻き込み復元が長さ保存(削った prefix / suffix をそのまま書き戻す)だから。
                // 始端を据え置ける根拠は、span ⊆ scope(WithinScope)ではなく直前の
                // GetExactChangeCharRange 検査:
                //   ここへ来るのは「実際に内容が変わる範囲」が [scope.Start, scope.End) に
                //   収まったヒットだけなので、scope.Start より前の内容は定義上変わらない。
                //   非ゼロ幅の巻き込み復元は長さ保存で接頭辞をそのまま書き戻すため
                //   change.Start == span.Start ≥ scope.Start、ゼロ幅は挿入点が論理文字の境界まで
                //   「後退」しうるが(ReplaceCharRangeExact の remarks 参照)、後退先が
                //   scope.Start より前になるヒットは上の検査で拒否済み。
                //   検査を外すと、スコープ始端が論理文字の内側にあるとき
                //   (例: "X\rYZ" の [2,4) を捕捉 → Y を \n へ置換 → 位置 2 が CRLF の内側)
                //   挿入がスコープの外へ落ち、始端据え置きが嘘になる。
                var grown = (Start: prev.Start, End: prev.End + repl.Length - span.Length);
                _selectionScope = (Weak(snap2), grown.Start, grown.End);
                scope = grown;
            }
            // 空置換（削除）のとき +1 すると置換直後の隣接ヒットを取りこぼすので、
            // 置換文字列の直後(afterRepl)からそのまま前進する。
            var next = searcher.FindNext(snap2, afterRepl);
            if (next is null || !WithinScope(next.Value, scope))
            {
                _lastHit = null;
                Announce("置換しました。これ以上見つかりません");
                return;
            }
            SelectHit(ed, snap2, next.Value); // next は snap2 上で見つけたヒット
            var loc = searcher.Locate(snap2, next.Value);
            Announce(
                loc is { } l ? $"置換しました。{l.Total} 件中 {l.Ordinal} 件目" : "置換しました"
            );
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
        }
    }

    /// <summary>「選択範囲のみ」トグル時に対象範囲を捕捉/破棄する（find 移動でクロバーされないよう保持）。</summary>
    public void OnInSelectionToggled(bool on)
    {
        if (on && ActiveEditor is { } ed)
        {
            var (s, e) = ed.GetSelectionCharRange();
            _selectionScope = e > s ? (Weak(ed.CurrentBuffer.Current), s, e) : null;
        }
        else
        {
            _selectionScope = null;
        }
        UpdateCount();
    }

    /// <summary>全文（または選択範囲のみ）を一括置換し件数を通知する。</summary>
    public void ReplaceAll()
    {
        var ed = ActiveEditor;
        var opts = CurrentOptions();
        var d = _view;
        if (ed is null || opts is null || d is null)
            return;
        if (IsCsvModeActive)
        {
            Announce(CsvAnnounceFormatter.BlockedInCsvMode);
            return;
        }
        // 理由は ReplaceOne の同じガードを参照(委譲先の no-op を見ずにスコープを
        // 更新し成功発声する構造を消す)。
        // 一方 ReplaceOne 側の GetExactChangeCharRange 包含検査は**ここには置かない**
        // (実測 / 2026-09-01 B2 Task 4)。ReplaceAll の書込は常に
        // [rangeStart, rangeStart+rangeLen)=スコープそのものに収まる:
        //   - ゼロ幅は SnapshotSearcher.ReplaceInRange 側が後退先を見て
        //     「範囲始端より前へ下がるマッチ」を件数にも数えないので(Task 2)、
        //     count == 0 →「見つかりません」で書込へ到達しない。スコープが CRLF の内側の
        //     1 点へ潰れた状態は**到達する**("\ra\n" の [1,2) を捕捉 → a を削除)が、
        //     その状態で代表パターン / 置換文字列を総当たりしても本文が変わることは無かった
        //     (同じ状態で ReplaceOne は書いてしまう=そちらには網を張ってある)。
        //   - 非ゼロ幅で範囲が外へ広がるのはスコープ端がサロゲートペアの内側にあるときだけで、
        //     その状態への到達経路は無い(理由は ReplaceOne 側の同じ検査のコメント)。
        // 到達 fixture の無いガードは変異で必ず生存する死んだ分岐になるため置かない。
        // 探索に使ったプローブは使い捨てで commit していない(条件は ReplaceOne 側のコメント)。
        if (ed.ReadOnly)
            return;
        var searcher = ResolveSearcher();
        if (searcher is null || !searcher.IsValid)
        {
            Announce("正規表現が正しくありません");
            return;
        }

        try
        {
            // P6 Task 11: 全文 string 化を避け Snapshot を直接渡す(閾値超は窓/行照合に自動切替)。
            var snap = ed.CurrentBuffer.Current;
            int rangeStart,
                rangeLen;
            if (d.InSelection)
            {
                // 判定と文言は TryResolveScope に集約する(ReplaceOne と片方だけ通る非一貫を作らない)。
                if (TryResolveScope(snap) is not { } scope)
                    return; // 理由は TryResolveScope が発声済み
                rangeStart = scope.Start;
                rangeLen = scope.End - scope.Start;
            }
            else
            {
                rangeStart = 0;
                rangeLen = snap.CharLength;
            }

            var (fragment, count) = searcher.ReplaceInRange(
                snap,
                rangeStart,
                rangeLen,
                d.Replacement
            );
            if (count == 0)
            {
                Announce("見つかりません");
                return;
            }
            // Exact でなければならない。fragment は素の範囲 [rangeStart, rangeStart+rangeLen) 用に
            // 組まれている(ReplaceInRange はスナップしない)のに、非 Exact の ReplaceCharRange は
            // 両端をスナップして範囲を「狭める」ため、端が論理文字の内側にあると断片と書込先の
            // 長さが食い違う。終端側は CR が重複して空行が増え、始端側はスコープ外の CR が
            // 黙って消える(どちらも「N 件置換しました」と成功発声する)。
            // 端が境界に乗っている通常ケースでは s == s0 / e == e0 で prefix / suffix が空になり、
            // 委譲先は ReplaceCharRange(rangeStart, rangeLen, fragment) そのもの=挙動不変。
            // 戻り値(置換文字列の直後)は使わない。ReplaceAll には次ヒット探索が無いため。
            ed.ReplaceCharRangeExact(rangeStart, rangeLen, fragment);
            if (d.InSelection)
                // 置換後の同じ領域を新世代で捕捉し直す。これが無いと「範囲を選んで語を変えながら
                // 何度か置換する」ワークフローが 2 回目で拒否される(範囲は fragment の長さぶんに
                // 伸縮する)。
                // 端が「文字の途中を指さない」とは限らない: スコープは単発置換のたびに伸縮するので、
                // 置換が端に CRLF を作れば端は論理文字の内側に入りうる(上記 2 バグの再現条件)。
                // それでも再捕捉の値は正しい。Exact 置換は巻き込んだ prefix / suffix を長さ保存で
                // 書き戻すので、実書込範囲は [rangeStart, rangeStart + fragment.Length) からずれない
                // (rangeStart より前の内容は不変・終端は差し込んだ fragment の末尾)。
                _selectionScope = (
                    Weak(ed.CurrentBuffer.Current),
                    rangeStart,
                    rangeStart + fragment.Length
                );
            // 世代チェックとの二重の保険。ここへ来る時点で必ず編集済み(count == 0 は上で
            // return)なので LiveHit は世代不一致で null になり、現状この行は冗長。
            // 「編集したら現ヒットは捨てる」を明示に残す(消しても挙動は変わらない)。
            _lastHit = null;
            Announce($"{count} 件置換しました");
        }
        catch (RegexMatchTimeoutException)
        {
            Announce("検索式が複雑すぎます");
        }
    }

    /// <summary>直前ヒットを選択して <see cref="_lastHit"/> を更新する。
    /// 選択の<b>結果</b>を読み戻すことで、CRLF / サロゲートのスナップ規則を App 層に複製しない。
    /// <para><paramref name="snap"/> は <paramref name="hit"/> を<b>見つけたときの</b>スナップショット。
    /// ここで <c>ed.CurrentBuffer.Current</c> を読み直さないのは、ヒットとその出所を必ず一組で
    /// 運ばせるため(呼び出し側は手元の snap を渡すだけ)。読み直しでも選択操作では世代が
    /// 変わらないので現状は同値だが、置換を挟む経路が増えると取り違えが起きうる。</para></summary>
    private void SelectHit(EditorControl ed, TextSnapshot snap, MatchSpan hit)
    {
        ed.SelectCharRange(hit.Start, hit.Length);
        var (s, e) = ed.GetSelectionCharRange();
        _lastHit = (Weak(snap), hit, s, e);
    }

    /// <summary>「いま画面で選ばれているヒット」を返す(無ければ null)。
    /// 文書が編集されていない(スナップショット参照が同一)かつ ユーザーが選択を動かしていない
    /// (選択が捕捉時の読み戻し値と一致)ときだけ生きている。
    /// <para><see cref="TryResolveScope"/> と違い、死んだ現ヒットは<b>黙って捨てて</b>次の分岐へ落とす
    /// (<c>_lastHit</c> の clear も発声もしない)。これは clear 漏れではなく意図的な非対称:
    /// スコープの陳腐化は「ユーザーが指定した範囲を使えない」失敗なので伝える必要があるが、
    /// 現ヒットが死ぬのは検索の歩進状態が切れただけで、ユーザーに見せる失敗ではない
    /// (次の分岐が選択 / 再検索で回復する)。後始末は次の <see cref="SelectHit"/> か
    /// 各経路の <c>_lastHit = null</c> 代入が行う。</para></summary>
    private MatchSpan? LiveHit(TextSnapshot snap, int selStart, int selEnd)
    {
        if (_lastHit is not { } h)
            return null;
        if (!h.Snap.TryGetTarget(out var captured) || !ReferenceEquals(captured, snap))
            return null;
        return selStart == h.SelStart && selEnd == h.SelEnd ? h.Hit : null;
    }

    /// <summary>「選択範囲のみ」の捕捉済みスコープを、現世代で使える形に解決する。
    /// 使えないときは理由を発声して null を返す(呼び出し側は素直に return してよい)。</summary>
    /// <remarks>
    /// 捕捉後に文書が編集されると、同じ char 位置が別の中身を指す。そのまま置換すると
    /// ユーザーが選択していない範囲を書き換えたうえ成功発声する(SR ユーザーには区別がつかない)。
    /// 世代判定は捕捉元スナップショットとの参照同一性で行う。TextSnapshot は編集のたびに
    /// 新インスタンスになり、キャレット・選択の移動では変わらない=「検索移動でクロバーされない」
    /// という捕捉方式の目的は壊れない。Undo で捕捉時と同一内容へ戻しても陳腐化と扱う=安全側。
    /// </remarks>
    private (int Start, int End)? TryResolveScope(TextSnapshot snap)
    {
        if (_selectionScope is not { } scope)
        {
            Announce("選択範囲がありません");
            return null;
        }
        if (!scope.Snap.TryGetTarget(out var captured) || !ReferenceEquals(captured, snap))
        {
            _selectionScope = null; // 旧ピース木の参照を即手放す
            Announce("選択範囲が変わりました。選択し直してください");
            return null;
        }
        return (scope.Start, scope.End);
    }

    /// <summary>ヒットがスコープに完全に収まるか(スコープなし=全文なら常に true)。</summary>
    private static bool WithinScope(MatchSpan hit, (int Start, int End)? scope) =>
        scope is not { } s || (hit.Start >= s.Start && hit.End <= s.End);

    private static WeakReference<TextSnapshot> Weak(TextSnapshot snap) => new(snap);

    /// <summary>MainForm 底部の通知 Label へ SR ライブ通知(Say 契約: 空は視覚クリアのみ・発声なし)。
    /// dialog が表示中なら dialog 内の視覚ステータスも更新して、置換結果の可視表示が
    /// dialog 内で維持される(晴眼/弱視ユーザーの UX 保持=SetStatus は発声しないので二重発声にならない)。
    /// P7/P8 申し送り: G-2 で「次を検索」後にダイアログを Hide するため、Hidden な _view を
    /// 経由せず MainForm 共有 Announcer 直結で SR 発声を成立させる。</summary>
    internal void Announce(string message)
    {
        _announcer.Say(message);
        if (_view?.Visible == true)
            _view.SetStatus(message);
    }
}
