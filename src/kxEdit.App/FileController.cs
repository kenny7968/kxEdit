using System.Text;
using kxEdit.Core.Backup;
using kxEdit.Core.Buffers;
using kxEdit.Core.IO;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// ファイル入出力の統括（新規/開く/保存/文字コード指定の開き直し/バックアップ復元/未保存確認/
/// 最近のファイル登録）。文書メタ（Path/Encoding/EOL）と本文の載せ替えはここに集約し、
/// MainForm は配線・メニュー・表示に徹する。ダイアログを出すため UI スレッド前提（他コントローラと同様）。
/// </summary>
public sealed class FileController
{
    private readonly DocumentManager _docs;
    private readonly IWin32Window _owner;
    private readonly Func<AppSettings> _settings; // 設定ダイアログ適用で参照が差し替わるため都度解決する
    private readonly Action _saveSettings; // 設定の永続化（保存失敗を致命にしない実装は呼び出し側）
    private readonly Action _recentChanged; // 「最近のファイル」メニューの再構築
    private readonly Action _metaChanged; // タイトル・ステータスの更新
    private readonly Action<Document> _openedFresh; // 開く系で新規ロード成功した直後（.csv 自動モードの判定は MainForm 側）
    private readonly IUserPrompt _prompt; // 確認・警告の注入点（テストでは FakePrompt）
    private readonly IFileDialogService _fileDialogs; // ファイル系ダイアログの注入点（テストでは FakeFileDialogService）
    private readonly IReachabilityProbe _reachabilityProbe; // HIGH-6: UNC ロードの短タイムアウトプローブ(テストでは Fake)
    private readonly IFileTimestampProvider _fileTimestamps; // A-1: 復元時の陳腐化検出(テストでは Fake)
    private int _untitledSeq; // 無題タブの連番（新規作成毎に増加・セッション内で再利用しない）

    /// <summary>A-1 第 2 層(設計 2026-08-22 §4): 直近の復元で「ディスク側がバックアップより
    /// 新しい」と判定されたパス。ON(hot exit silent)/ OFF(起動時提案)双方の復元経路が
    /// ここへ積み、MainForm.OnShown が回収して集約警告を 1 個出す。</summary>
    private readonly List<string> _staleRestoredPaths = new();

    // Task 4: 復元経路(RestoreSession)専用の内部 seam。LoadInto の catch 内 _prompt.Error を一時抑止し、
    // 失敗パスを failedPaths に集約する(単発ダイアログを避けまとめて 1 個で通知するため)。
    // 復元経路以外(開く/最近/grep/開き直し)からは触れない=これらは既存の per-file ダイアログを維持する。
    private bool _suppressLoadErrorPrompt;

    // Task 5 review I-2: 復元経路(RestoreSession)で RegisterRecent を抑止するための seam。
    // 復元は「ユーザーが開いた」相当ではないため RecentFiles を汚さない=起動前の順序を保つ。
    private bool _suppressRegisterRecent;

    /// <summary>
    /// パス正規化に張る境界(S-15)。<b>開く入口(<see cref="TryOpenOrActivate"/>)と保存入口
    /// (<see cref="NormalizeSavePath"/>)で共有する</b>ので、両者の境界が食い違うことは
    /// 構造上ありえない(doc の約束ではなく定数の共有で担保する)。
    /// <b>警告文言の「n 秒」もこの値から補間する</b>ので、ここを変えれば文言も追随する
    /// (Task 3 レビュー 脆弱-m-1: 以前は文言側が literal の二重管理で、文言だけを 30 秒に
    /// 書き換える変異が全緑で生存した)。
    /// <para>
    /// <b>到達性プローブ(<see cref="TryProbeFileExists"/> / <see cref="TryInspectSaveTarget"/>)の
    /// 5 秒はここへ寄せていない</b>(Task 4 レビュー 脆弱-m-2 の判断)。同じ数値だが<b>別の境界</b>で、
    /// 寄せると「正規化の待ちを 10 秒にしたい」がプローブの待ちまで黙って変える結合を新設する。
    /// 文言との二重管理も無い(プローブ側の文言に秒数が出ない)ため、Task 3 で潰した形とは違う。
    /// プローブ側の literal は <c>FakeReachabilityProbe.LastTimeout</c> /
    /// <c>SaveTargetLastTimeout</c> の assert が既に pin している。
    /// </para>
    /// </summary>
    private static readonly TimeSpan NormalizeTimeout = TimeSpan.FromSeconds(5);

    public FileController(
        DocumentManager docs,
        IWin32Window owner,
        Func<AppSettings> settings,
        Action saveSettings,
        Action recentChanged,
        Action metaChanged,
        Action<Document> openedFresh,
        IUserPrompt prompt,
        IFileDialogService fileDialogs,
        IReachabilityProbe reachabilityProbe,
        IFileTimestampProvider fileTimestamps
    )
    {
        _docs = docs;
        _owner = owner;
        _settings = settings;
        _saveSettings = saveSettings;
        _recentChanged = recentChanged;
        _metaChanged = metaChanged;
        _openedFresh = openedFresh;
        _prompt = prompt;
        _fileDialogs = fileDialogs;
        _reachabilityProbe = reachabilityProbe;
        _fileTimestamps = fileTimestamps;
    }

    /// <summary>A-1 第 2 層: 陳腐化が疑われるパスを取り出す(取得と同時にクリア =
    /// 同じ警告を二度出さない)。MainForm.OnShown が ON / OFF いずれの復元後にも回収する。</summary>
    public IReadOnlyList<string> TakeStaleRestoredPaths()
    {
        // レビュー M-5: 同一パスのレコードは複数ありうる(レイアウトの重複レコード・
        // レイアウト由来 + extras 由来)。重複したまま渡すと警告に同じ行が並び、
        // 表示上限 10 件の枠を重複が食い潰す。
        var result = _staleRestoredPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _staleRestoredPaths.Clear();
        return result;
    }

    /// <summary>
    /// 検証済みパスに対してのみディスクの更新時刻を見て、バックアップが陳腐化していれば記録する
    /// (設計 2026-08-22 §4.3)。
    /// </summary>
    /// <remarks>
    /// <b>検証 NG のパスへは呼ばないこと</b>: 攻撃者 JSON 由来のパスへ I/O させない(HIGH-2 の思想)。
    /// ディスク版を優先してバックアップを捨てる判断はしない。ディスクが新しい理由が
    /// 「kxEdit 自身の保存(A-1)」か「他アプリの更新」かを区別できず、捨てる実装は
    /// 新しい無言喪失経路になるため(設計 §4.2)。
    /// </remarks>
    private void NoteIfBackupStale(string validatedPath, BackupRecord bk)
    {
        if (
            BackupStaleness.IsDiskNewer(
                _fileTimestamps.GetLastWriteTimeUtc(validatedPath),
                bk.TimestampUtc,
                BackupStaleness.DefaultTolerance
            )
        )
            _staleRestoredPaths.Add(validatedPath);
    }

    /// <summary>
    /// テスト用: MainForm スモークテストが復元結果のタブ列(順序・State・Modified)を
    /// 観測するための seam。実運用経路では参照しない。
    /// </summary>
    internal IReadOnlyList<Document> DocsForTest => _docs.Documents;

    // ==================== 新規 / 開く ====================

    /// <summary>新しい無題タブを作る（既定の文字コード・改行を設定から適用）。</summary>
    public void NewFile()
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = ++_untitledSeq; // 「無題 1」「無題 2」…で区別できるように
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = (LineEnding)s.DefaultLineEnding;

        doc.Editor.Text = string.Empty;
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.SetSavePoint();
        DocumentManager.UpdateLabel(doc);
        _metaChanged();
    }

    /// <summary>「開く」ダイアログでファイルを選んで開く。</summary>
    public void OpenFileWithDialog()
    {
        var path = _fileDialogs.PickOpenPath(_owner);
        if (path is null)
            return;
        TryOpenOrActivate(path);
    }

    /// <summary>
    /// path を開く唯一の経路（「開く」「最近のファイル」「grep ジャンプ」共通）。
    /// 既に開いていればそのタブをアクティブ化（Q4：二重編集の上書き事故防止）し、
    /// 最近のファイルは先頭へ繰上げ。新規に開けなければ作りかけタブを破棄して
    /// 直前のアクティブへ戻し null を返す。
    /// <para>
    /// <b>Issue #48 / 設計書 §3.6</b>: 入口で 1 回だけ境界付き正規化する。以降
    /// (<c>FindByPath</c> / <see cref="LoadInto"/> / <see cref="RegisterRecent"/>)は
    /// 正規化済みパスを受け取る契約になる。
    /// </para>
    /// </summary>
    public Document? TryOpenOrActivate(string path, bool suppressAutoCsv = false) =>
        TryOpenOrActivateCore(path, suppressAutoCsv).Doc;

    /// <summary>
    /// <see cref="TryOpenOrActivate"/> の本体。開いた(または活性化した)文書と
    /// 「既存タブを再利用した(fast-path activate)」かどうかを<b>組で</b>返す。
    /// <para>
    /// <b>Issue #48 Task 5</b>: 復元経路には「fast-path activate した既存タブには adopt しない」
    /// という門番が 2 箇所あり(<see cref="RestoreLayoutRecord"/> / <see cref="RestoreExtraBackup"/>)、
    /// どちらも従来は<b>呼出前に</b> <c>_docs.FindByPath(引数)</c> を打って判定していた。これは
    /// 「引数と State.Path が同じ綴りである」ことに依存する形で、<see cref="DocumentManager.FindByPath"/>
    /// が区切り差を吸収しなくなった今は成り立たない(レイアウト JSON の <c>rec.Path</c> は
    /// <c>LegacySessionConverter</c> 経由で未正規化でありうる)。判定が落ちると既存タブへ adopt が走り、
    /// 既に adopt 済みのバックアップ Id を上書きして別のゾンビを作る。
    /// </para>
    /// <para>
    /// 呼出点で先に正規化して <c>FindByPath</c> と本メソッドの両方へ渡す案も採れるが、それだと
    /// 境界付き正規化が 1 レコードあたり 2 回になり、不達先ではタイムアウト待ちが倍になる。
    /// 「再利用したか」は本メソッドしか知り得ない事実なので、ここから返す。
    /// </para>
    /// <para>
    /// <b>組で返す理由(Task 5 レビュー m-3)</b>: <c>out bool</c> + メソッド先頭での
    /// <c>reusedExisting = false;</c> だと、将来「既存タブを返す」経路をもう 1 本足したときに
    /// 代入を忘れてもコンパイルが通り、門番(<c>if (!reusedExisting) adopt</c>)が黙って
    /// adopt を走らせる=既存タブが adopt 済みのバックアップ Id を上書きする。戻り値の組に
    /// すると各 return 地点で値を<b>書かざるを得ない</b>ので、「既定値のまま素通り」という
    /// 失敗の形自体が消える。公開 API(<see cref="TryOpenOrActivate"/>)の挙動は変えない。
    /// </para>
    /// </summary>
    private (Document? Doc, bool ReusedExisting) TryOpenOrActivateCore(
        string path,
        bool suppressAutoCsv
    )
    {
        // Issue #48 / 設計書 §3.6: ここが State.Path へ未正規化パスが入る唯一の入口だった
        // (保存側は SaveAsDocument が既に塞いである)。境界付きにする理由は S-15:
        // 正規化後のパスに `~` が残ると GetFullPath が GetLongPathName を呼び、不達の共有に
        // 対して約 21 秒 UI を止める。タイムアウトは保存側と同じ NormalizeTimeout を共有する。
        //
        // 第 2 項は SaveAs 側 V-1 / V-3 と同じ門番で、**正規化の後ろに置くのが load-bearing**:
        // seam の Ok は「文字列として正規化できた」以上の意味を持たない(実測 2026-08-23:
        // CON → \\.\CON・NUL → \\.\NUL・LPT1 → \\.\LPT1 がいずれも Ok で返る。
        // ディレクトリー付きでも NUL は特別扱いで <tmp>\NUL → \\.\NUL になる)。
        // 素通しにすると次の 3 つが起きる:
        //   (1) `\\.\` 綴りが State.Path / FindByPath のキー / RecentFiles へ流れうる。
        //   (2) 先頭 `\\` で RemotePathDetector がリモート判定するので、無意味な到達性プローブ
        //       (Task.Run + Wait・最悪 5 秒待ち)を 1 本余分に通る。
        //   (3) その結果「ネットワークパスに到達できません」という的外れな文言になる
        //       (= V-3 で潰したはずの症状。ルートの場合は「開けませんでした: アクセスが
        //       拒否されました」)。
        // **訂正(Task 4 レビュー 脆弱-I-4)**: ここには当初「ガードが無いと
        // File.OpenRead(@"\\.\CON") が無期限にブロックする」と書いていた。**これは誤り**で、
        // 測っていたのは OpenRead + 読み出しだった。本番の TextFileService.LoadAsBufferAuto は
        // 本文を読む前に probe.Length を打つため(TextFileService.cs:151)、デバイスパスは
        // そこで NotSupportedException になり読みに到達しない。再実測(2026-08-23):
        //   \\.\CON  OpenRead 1ms OK / +.Length 4ms NotSupported / +Read 2999ms BLOCKED /
        //            LoadAsBufferAuto 3ms NotSupported
        //   \\.\NUL  OpenRead 0ms OK / +.Length 0ms NotSupported / +Read 0ms OK /
        //            LoadAsBufferAuto 1ms NotSupported
        // つまり無境界の待ちは現行経路では発生せず、(1) は Core 側の .Length が内側の防御に
        // なっている。本ガードはその**二重防御の外側**であり、直接の実害として測れるのは
        // (2)(3) のほう。
        // 述語が弾くのはルート(C:\ / \\server\share)とデバイスパスだけで、実ファイルは
        // 拡張長 (\\?\C:\Temp\a.txt) や UNC (\\server\share\a.txt) も親フォルダーを持つ(実測)。
        var norm = _reachabilityProbe.NormalizePathWithTimeout(path, NormalizeTimeout);
        if (
            norm.Status != PathNormalizeStatus.Ok
            || string.IsNullOrEmpty(System.IO.Path.GetDirectoryName(norm.Full))
        )
        {
            // 復元経路(RestoreSession)は WithLoadErrorPromptSuppressed の中でここへ来る。
            // 無条件に出すと起動時に per-file ダイアログが増えるので、既存の失敗経路
            // (LoadInto の catch / ReportUnreachable)と同じく抑止スコープを尊重する。
            // 戻り値 null は抑止に関係なく伝播し、呼出元が failedPaths へ集約する。
            if (!_suppressLoadErrorPrompt)
            {
                // 到達不能(TimedOut)と打ち間違い(Invalid・親フォルダー無し)で文言を分ける
                // のは SaveAs 側と同じ理由(同じ文言だと、原因がネットワークなのに利用者が
                // 入力を疑い続ける)。こちらは開く経路なので保存先ではなくファイルを指す。
                // switch 式なのは可読性のため。網羅性検査が効かない事情は SaveAsDocument
                // 側の同じ分岐のコメントに書いてある。
                // 文言の「n 秒」は NormalizeTimeout から補間する(二重管理を作らない)。
                // CSV-L-5: path は外部入力(復元 JSON 由来もある)なので必ず無害化する。
                _prompt.Error(
                    norm.Status switch
                    {
                        PathNormalizeStatus.TimedOut =>
                            $"ファイルに到達できませんでした({NormalizeTimeout.TotalSeconds:0} 秒)。ネットワーク接続を確認してください: {SanitizeForDisplay.OneLine(path, 200)}",
                        _ => $"パスが正しくありません: {SanitizeForDisplay.OneLine(path, 200)}",
                    },
                    "エラー"
                );
            }
            return (null, false);
        }
        // 以降の照合・ロード・最近のファイル登録はすべて正規化済みの full を使う
        // (生の path を混ぜると設計書 §3.1 の不変条件が入口で破れる)。
        string full = norm.Full;

        var existing = _docs.FindByPath(full);
        if (existing is not null)
        {
            _docs.Activate(existing);
            // Task 10 review I-2: 復元経路(_suppressRegisterRecent=true)では fast-path でも
            // RegisterRecent を抑止する(重複パスの LastSession 復元で RecentFiles が汚染されるのを防ぐ)。
            if (!_suppressRegisterRecent)
                RegisterRecent(full);
            return (existing, true); // ここだけが「既存タブを再利用した」経路
        }

        var prev = _docs.Active; // 読込失敗時に戻る先（直前のアクティブタブ）
        var doc = _docs.CreateNew();
        // Task 5 review I-1: LoadInto は catch フィルタ外の例外(NullReference 等のロジックバグや
        // ArgumentException 追加前の残差)で throw しうる。半端に生きた doc が残ると
        // 「作りかけタブが閉じない=次の RestoreSession が initialEmpty を閉じられない」等の
        // 二次汚染につながるため、例外時も破棄→prev 復帰の後始末を保証する(挙動不変=成功/失敗
        // 経路は従来どおり)。
        bool loaded;
        try
        {
            loaded = LoadInto(doc, full, forcedCodePage: null);
        }
        catch
        {
            _docs.TryClose(doc, _ => true);
            if (prev is not null)
                _docs.Activate(prev);
            throw;
        }
        if (loaded)
        {
            // 開く系（開く/最近）のみ .csv 自動モードの対象。grep ジャンプは選択＋エディタフォーカスを
            // 機能させるため suppressAutoCsv=true で抑止する（設計 2026-07-04）。
            if (!suppressAutoCsv)
                _openedFresh(doc);
            return (doc, false); // 新しく作ったタブ=再利用ではない
        }
        _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
        if (prev is not null)
            _docs.Activate(prev); // 直前のアクティブへ戻す
        return (null, false);
    }

    /// <summary>アクティブタブを指定の文字コードで開き直す。Path 未確定なら案内表示して中止。</summary>
    public void ReopenWithEncoding()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;
        if (doc.State.Path is null)
        {
            _prompt.Info("ファイルを開いてから実行してください。", "kxEdit");
            return;
        }
        if (!ConfirmDiscardIfDirty(doc))
            return;
        int? picked = _fileDialogs.PickEncoding(_owner, doc.State.Encoding.CodePage);
        if (picked is null)
            return;
        if (!LoadInto(doc, doc.State.Path, forcedCodePage: picked))
            return;
        _openedFresh(doc); // 開き直しも .csv 自動モードの対象（設計 2026-07-04）
        // CSVモード中の開き直しでは、ダイアログ閉塞時に WinForms がシンクへフォーカスを復元するため明示的に戻す。
        // 自動 CSV モードに入った場合は FocusTarget=シンク、入らなければエディタへ向く。
        doc.FocusTarget.Focus();
    }

    /// <summary>
    /// ファイルを読み込み、本文・文字コード・改行を対象タブへ反映する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// 成否を返す（失敗は _prompt.Error で通知・握り潰さない）。
    /// </summary>
    private bool LoadInto(Document doc, string path, int? forcedCodePage)
    {
        try
        {
            // HIGH-6 + CSV-M-1: UNC / マップドネットワークドライブは 5 秒プローブで到達不能なら
            // 即エラー(60 秒 UI 凍結を回避)。読む側は「既存ファイルがあるか」が知りたいので
            // File.Exists 意味論のプローブでよい(書く側=TryInspectSaveTarget とは意味論が違う: A-4)。
            if (!TryProbeFileExists(path))
                return false;

            // P6 Task 10: Stream I/O 経路で TextBuffer に直接読み込む(1GB 級 UTF-8 の OOM 回避)。
            // 従来の TextFileService.Load(=byte 全読み + string 全文化)は選択肢から外し、
            // LoadAsBufferAuto で prefix 検出 → LoadAsBuffer(chunk stream) → LineEnding 検出。
            var loaded = TextFileService.LoadAsBufferAuto(path, forcedCodePage);
            doc.State.Path = path;
            doc.State.Encoding = loaded.Encoding;
            doc.State.HasBom = loaded.HasBom;
            doc.State.LineEnding = loaded.LineEnding;

            // CSV モードは自動判定しない（メニューから手動で有効化する）。
            // 既存タブへロードし直す場合に備え、読取専用とハイライトを解除しておく。
            // ReplaceSource は ReadOnly 時 no-op ではないが、旧 CSV モード状態を確実に解除するため事前に落とす。
            doc.State.CsvMode = false;
            doc.ClearCsvCache(); // 旧本文のパース結果を持ち越さない（開き直し経路のメモリ解放）
            doc.Editor.ReadOnly = false;
            doc.Editor.RaiseUiaSelectionEvents = true; // モードON時に落とした UIA 抑止をここで確実に戻す（開き直し経路）
            doc.Editor.ClearHighlight();

            // P6 Task 10: TextBuffer をそのまま差し込む(string 全文化を回避)。
            // 新規タブ(SetSource 前)/開き直し(_buffer あり)の両方を SetOrReplaceSource が振り分け。
            doc.Editor.SetOrReplaceSource(loaded.Buffer);
            ApplyEol(doc);
            doc.Editor.EmptyUndoBuffer();
            doc.Editor.SetSavePoint();

            DocumentManager.UpdateLabel(doc);
            _metaChanged();

            if (loaded.HadReplacementChar)
            {
                // 統合復元 Task 5 fixup: 復元経路(WithLoadErrorPromptSuppressed 実行中)は per-file
                // ダイアログを出さない(設計 2026-07-23 統合 §3.3 の silent 原則)。U+FFFD は本文に
                // 見える形で残る=silent data loss ではないため trace で診断可能性のみ維持する。
                // 通常の「開く」「開き直し」経路は挙動不変。
                if (!_suppressLoadErrorPrompt)
                {
                    _prompt.Warn(
                        "このファイルには現在の文字コードで表せない文字（置換文字）が含まれています。"
                            + "別の文字コードで開き直してください。",
                        "文字コードの警告"
                    );
                }
                else
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "kxEdit: restore-replacement-char-detected: {0}",
                        SanitizeForDisplay.OneLine(path, 200)
                    );
                }
            }
            // Task 5 review I-2: 復元経路(RestoreSession)では「ユーザーが開いた」相当ではないため
            // RecentFiles を汚さない=起動前の順序を保つ。通常経路(開く/最近/開き直し)は従来どおり登録。
            if (!_suppressRegisterRecent)
                RegisterRecent(path); // 開けたファイルを最近のファイルへ
            return true;
        }
        // Task 5 review I-1: ArgumentException を握る=悪意/破損した settings.json 由来の path
        // (null 文字入り・無効文字)で File.OpenRead が投げるのを吸収し、復元経路の全体 abort を防ぐ。
        catch (Exception ex)
            when (ex
                    is System.IO.IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException
                        or NotSupportedException
                        or ArgumentException
                        or DocumentTooLargeException
            )
        {
            // 想定内の入出力エラーのみ握る。NullReference 等のロジックバグは伝播させる。
            // CSV-L-5: ex.Message は攻撃者制御下ではないがファイル名 (path) を含みうるため、
            // 二次的スプーフィング防止として SanitizeForDisplay.OneLine で無害化する。
            // Task 4: 復元経路(WithLoadErrorPromptSuppressed 実行中)は per-file ダイアログを抑止し、
            // 呼出元で失敗パスを集約通知させる(戻り値 false は伝播)。
            if (!_suppressLoadErrorPrompt)
            {
                _prompt.Error(
                    $"開けませんでした: {SanitizeForDisplay.OneLine(ex.Message, 200)}",
                    "エラー"
                );
            }
            return false;
        }
    }

    // ==================== 保存 ====================

    /// <summary>アクティブタブを上書き保存する（メニュー経由）。</summary>
    public bool Save()
    {
        var doc = _docs.Active;
        return doc is not null && SaveDocument(doc);
    }

    /// <summary>アクティブタブを名前を付けて保存する（メニュー経由）。</summary>
    public bool SaveAs()
    {
        var doc = _docs.Active;
        return doc is not null && SaveAsDocument(doc);
    }

    /// <summary>
    /// 指定ドキュメントを保存。Path 未確定なら SaveAs にフォールバック。
    /// A-7 (b) 残余(Task 6b・2026-08-23): 同一パスを 2 タブが持っている状態での上書き保存を止める。
    /// A-10(2026-08-28): 符号化で表せない文字がある場合は保存前に確認する(SaveAs と対称)。
    /// </summary>
    public bool SaveDocument(Document doc)
    {
        if (doc.State.Path is null)
            return SaveAsDocument(doc);

        // A-7 (b) 残余: SaveAsDocument のガード(Task 6)は重複タブが「生まれる」経路しか塞がない。
        // 「既にある」状態は復元 extras の dedup がバックアップ Id のみで照合するため現行フリートでも
        // 発生し(クラッシュ直前に開いたタブ・他インスタンス遺物・旧「あとで」孤児)、そこでは
        // Ctrl+S が無警告でもう一方のタブの内容をディスクから消す。
        // 述語は Task 6 と同じ(FindByPath = PathKey 照合 + 自タブ除外)。FindByPath は生成順で
        // 最初の一致を返すので、先に生まれたタブが保存権を持ち、後から生まれた方が止まる。
        // 置き場所: WriteToPath ではなく Ctrl+S の入口。WriteToPath は低レベルの書き込み
        // プリミティブで、UI ポリシーを置くと層が濁る(現在の呼出元は本メソッドと SaveAsDocument
        // の 2 つだけで、SaveAsDocument 側は自前の同等ガードを既に通している)。
        // 位置も load-bearing: WriteToPath 冒頭の到達性プローブ(TryInspectSaveTarget)より前に
        // 置く。重複は保存させないので到達性を調べる意味がなく、遠隔共有で無駄な 5 秒を待たせない。
        var other = _docs.FindByPath(doc.State.Path);
        if (other is not null && !ReferenceEquals(other, doc))
        {
            // 文言は Task 6(SaveAs)と別: ここは呼び出し元のタブ自身もそのパスを持っているので
            // 「そのタブで保存してください」は成立しない。読み上げで取り違えないよう、
            // 冒頭が Task 6 の文言の部分文字列にならない形にしてある(テストは冒頭で弁別する)。
            // 提示する逃げ道は 2 つとも実際に効く: (a) 別名で保存すれば衝突しない・
            // (b) もう一方のタブを閉じれば FindByPath の一致が自分だけになりこの保存が通る。
            // 「このタブを破棄」は内容を捨てる案内になるので出口としては挙げない。
            // CSV-L-5: path は外部入力(復元 BackupRecord 由来もある)なので SanitizeForDisplay で無害化。
            _prompt.Error(
                "別のタブが同じファイルを開いています。このまま上書きすると、もう一方のタブの内容が失われます。"
                    + "「名前を付けて保存」で別の名前を指定するか、もう一方のタブを閉じてから保存してください: "
                    + SanitizeForDisplay.OneLine(doc.State.Path, 200),
                "エラー"
            );
            return false;
        }

        // A-10: 上書き保存経路にも符号化劣化の事前確認を置く(SaveAs の C-2 追補 I-2 と対称)。
        // 従来は CanEncodeBuffer の呼出元が SaveAsDocument だけで、Ctrl+S は
        // TextFileService.Save の EncoderFallback.ReplacementFallback に素通りしていた
        // = 表せない文字が無警告で '?' になる。読込側の U+FFFD と違い、置換はディスク上でしか
        // 起きずバッファは元の文字を保持するので、画面にも文字数にも SR の読み上げにも痕跡が出ない。
        //
        // 位置は 3 点とも load-bearing:
        // (1) 重複タブガードより後 = 重複時はそもそも保存させないので全走査を無駄打ちしない。
        // (2) WriteToPath より前 = ApplyEol / ConvertEols の副作用を起こす前に短絡する。
        //     ConvertEols が触るのは CR / LF だけで、CR / LF は 932 / 51932 のどちらでも
        //     表現可能なので、判定を前に出しても答えは変わらない。ただしこの理由だけでは
        //     不十分で、第 2 の理由に依存している(M-5): ConvertEols は**総文字数を変える**ため
        //     §8.1 の 8192 チャンク境界の位置が動き、CanEncodeBuffer は一般には位置非依存でない。
        //     不変が成り立つのは、上のガードで残る 932 / 51932 では astral 文字が境界のどこに
        //     来ても符号化不能だから。将来 CodePage ガードをゆるめると、この不変性は
        //     CR / LF の議論だけでは導けなくなる(設計書 §8.3 の予告と同じ条件)。
        //     ついでに WriteToPath 冒頭の TryInspectSaveTarget(リモートは 5 秒プローブ)よりも
        //     前になる。これは望ましい副次効果で、到達不能なリモート保存先へ Ctrl+S したとき、
        //     5 秒待たされてから諦めるのではなく、その前に劣化の確認で中止できる。
        // (3) State.Path is null 分岐より後 = 無題タブは SaveAsDocument が自前の警告を持つので
        //     二重に出ない。
        //
        // CodePage != 65001 のガードも load-bearing。当初は性能だけの理由(UTF-8 は BMP + astral を
        // 全表現できるので走査が常に無駄)と考えていたが、Task 3 のセルフチェックで**挙動**も
        // 担っていることが実測で判明した: CanEncodeBuffer は 8192 文字のチャンクごとに独立して
        // GetByteCount を呼ぶため、サロゲートペアが境界で割れると各チャンクからは孤立サロゲートに
        // しか見えず ExceptionFallback が飛ぶ = **正当な UTF-8 文書でも false を返す**。
        // このガードを外すと、8192 の境界に絵文字が来た UTF-8 文書の Ctrl+S が毎回警告を出す。
        // (CanEncodeBuffer 自体のチャンク境界バグは SaveAs 側にも既存。非 UTF-8 では
        //  astral を含む時点で実際に劣化するので誤警告にならず、実害は UTF-8 経路だけ。)
        //
        // 文言は SaveAs 版と共有しない。SaveAs は「選択した文字コード」(いまダイアログで選んだ値)、
        // ここは「現在の文字コード」(文書が持っている値)で主語が違う。逃げ道ボタンは出さず
        // (Ctrl+S が文書の文字コードを変える挙動変更を避ける)、文中で SaveAs へ誘導する。
        // 問いを末尾に置くのは既存 2 件と同じ形: SR は本文を頭から読む。
        //
        // defaultCancel: true は S-12 と対称。既定が OK 側だと、Ctrl+S 直後の Enter や
        // 閉じる確認「はい」からの連鎖で、確認を足したこと自体が無力化される。
        if (
            doc.State.Encoding.CodePage != 65001
            && !CanEncodeBuffer(doc.Editor.CurrentBuffer, doc.State.Encoding)
            && !_prompt.OkCancel(
                "現在の文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。"
                    + "元の文字を残すには「名前を付けて保存」で UTF-8 を選んでください。続行しますか?",
                "文字コードの警告",
                defaultCancel: true
            )
        )
        {
            // SaveAs と違い戻る先のダイアログが無いので中止する(SaveAs は continue で再表示)。
            return false;
        }

        return WriteToPath(doc, doc.State.Path);
    }

    /// <summary>
    /// 指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。
    /// A-7 / A-4 / A-19(2026-08-23): 保存先を確定するまでダイアログを繰り返し表示する。
    /// 「ダイアログの中で選んだ値への警告なら、そのダイアログへ戻す」= 打ち直しを強いない
    /// (SR ユーザーの主経路はテキストボックス直入力なので、中止して開き直させる代償が大きい)。
    /// ループの出口は 3 つ: (1) キャンセル(PickSaveAs が null)・(2) 保存成功・
    /// (3) <see cref="WriteToPath"/> の失敗。(3) は**再表示せずに false を返す**(保存されない)。
    /// 例: 存在しないフォルダー配下のパスを打つと PickSaveAs は 1 回しか出ない(実測)。
    /// 書込失敗はダイアログの中で選んだ値の問題とは限らない(権限・ディスク・共有の消失)ので
    /// 再表示の対象外にしてある — 範囲の線引きとして受容し、申し送り S-16 に記録した。
    /// すべての continue は PickSaveAs(ユーザー操作)を挟む。
    /// </summary>
    private bool SaveAsDocument(Document doc)
    {
        var seed = new SaveAsRequest(
            doc.State.Path,
            doc.State.Encoding.CodePage,
            doc.State.HasBom,
            doc.State.LineEnding
        );

        while (true)
        {
            var picked = _fileDialogs.PickSaveAs(_owner, seed);
            if (picked is null)
                return false;
            // 入力を次回の初期値として保つ。
            seed = new SaveAsRequest(
                picked.Path,
                picked.CodePage,
                picked.HasBom,
                picked.LineEnding
            );

            if (string.IsNullOrWhiteSpace(picked.Path))
            {
                _prompt.Warn("ファイル名を指定してください。", "エラー");
                continue;
            }

            // V-1: 正規化できても「親フォルダーが取れない」入力は書き込み先が確定しないので弾く。
            // 該当するのはドライブルート(C:\ / Q:\)・予約デバイス名(CON → \\.\CON)・
            // \\?\C:\ のような拡張ルート・共有ルート(\\server\share)。
            // 通すと AtomicFile.Write の `Path.Combine(Path.GetDirectoryName(...)!, ...)` に null が
            // 渡って ArgumentNullException になる。WriteToPath の catch フィルタと合わせて
            // 二重に守る理由: フィルタだけだと ConvertEols で保存点が壊れた後に捕まるため、
            // ここで先に止めた方が本文にもキャレットにも触れない。
            // 述語(親フォルダーの有無)は FileReachabilityProbe.ProbeSaveTargetWithTimeout と同じ。
            // ガードを**正規化の後ろに残すのは load-bearing**(Issue #48): seam の Ok は
            // 「文字列として正規化できた」以上の意味を持たない(実測: CON → \\.\CON も
            // \\?\ もそのまま Ok で返る)ので、境界付きにしても V-1 の門番は要る。
            var norm = NormalizeSavePath(picked.Path);
            if (
                norm.Status != PathNormalizeStatus.Ok
                || string.IsNullOrEmpty(System.IO.Path.GetDirectoryName(norm.Full))
            )
            {
                // S-15: 到達不能(タイムアウト)と打ち間違い(Invalid)で文言を分ける。
                // 同じ文言だと、原因がネットワークなのに利用者が入力を疑い続ける。
                // 親フォルダーが取れない場合(V-1)は Ok なので既定枝=「正しくありません」に入る。
                // switch 式なのは**可読性のため**: Status とそれが出す文言の対応が一望でき、
                // 4 値目に固有の文言を足すときもアーム 1 行で済む。
                // **網羅性検査は期待できない**(Task 3 レビュー 仕様-I-1・実測で訂正):
                // `_ =>` アームがある以上、4 値目を足しても switch 式は三項と同じく黙って
                // 既定枝へ倒れる。discard を外して 3 値を全列挙しても、enum は宣言外の値を
                // 取りうるため CS8524 が出て -warnaserror で落ちる = この場所で網羅性検査を
                // 効かせる形はそもそも取れない。三項との保護効果は等価。
                // (`_ => throw` にすると 4 値目で保存経路がクラッシュするので改悪。)
                // 文言の「n 秒」は NormalizeTimeout から補間する(二重管理を作らない)。
                _prompt.Warn(
                    norm.Status switch
                    {
                        PathNormalizeStatus.TimedOut =>
                            $"保存先に到達できませんでした({NormalizeTimeout.TotalSeconds:0} 秒)。ネットワーク接続を確認してください: {SanitizeForDisplay.OneLine(picked.Path, 200)}",
                        _ =>
                            $"パスが正しくありません: {SanitizeForDisplay.OneLine(picked.Path, 200)}",
                    },
                    "エラー"
                );
                continue;
            }
            string full = norm.Full;
            // 以降の判定・保存・State 反映はすべて正規化済みの full を使う。
            // 再表示時も絶対パスを見せる(どこへ保存されるかが読み上げで分かる)。
            // 位置も load-bearing: 上の `seed = new SaveAsRequest(picked...)` は生入力を載せるので、
            // 必ずその後に上書きする。直後の重複タブ分岐が continue するため、この代入は
            // 再表示の初期値として実際に読まれる(Task 5 の S1854 局所抑止は本タスクで不要になり削除)。
            seed = seed with
            {
                Path = full,
            };

            // A-7 (b): 同一ファイルを 2 タブで編集させない。片方の Ctrl+S が
            // もう片方の内容を無警告で消す導線(hot exit レイアウトにも同一 Path が 2 件並ぶ)。
            // FindByPath は PathKey.ForNormalized(ToLowerInvariant のみ)照合なので大小の揺れは
            // 同一と見なす。区切りの揺れは吸収しないが、ここへ渡す full も比較先の State.Path も
            // 正規化済み(設計書 §3.1 の不変条件・Issue #48 Task 5)なので揺れは残らない。
            // 自分自身への上書きは正当なので除外する。
            var other = _docs.FindByPath(full);
            if (other is not null && !ReferenceEquals(other, doc))
            {
                _prompt.Error(
                    $"このファイルは別のタブで開いています。そのタブで保存してください: {SanitizeForDisplay.OneLine(full, 200)}",
                    "エラー"
                );
                continue;
            }

            // A-4 / A-7 (a): 到達性と既存有無を 1 回の境界付き I/O で得る。
            // 素の File.Exists は切断済み SMB 共有で UI を 60 秒固める(PR #42 H-1 の罠)。
            // 戻り値を**先に**見て短絡するのが契約: 到達不能のとき targetExists は
            // 未存在と到達不能を区別できず無意味(SaveTargetProbeResult のコメント)。
            if (!TryInspectSaveTarget(full, out bool targetExists))
                continue; // エラー表示は TryInspectSaveTarget の中

            // A-7 (a): 従来は「参照」ボタン内の SaveFileDialog(OverwritePrompt)だけが確認していた。
            // SR ユーザーの主経路はテキストボックス直入力なので、**主経路だけが無確認で上書き**
            // という非対称が A-7 の実体。確認点をここ 1 箇所に集約し、SaveAsDialog 側は
            // OverwritePrompt を切って二重確認を避ける。
            // 文言はパスより問いを先に置く: SR は本文を頭から読むため、最大 200 文字のパスを
            // 先頭に置くと何を聞かれているかが最後まで分からない(他の文言と同じ「文 → : パス」)。
            // defaultCancel: true が load-bearing(S-12): SaveAsDialog は AcceptButton = OK なので
            // 主経路は「ファイル名を打つ → Enter」。読み上げが遅いときの Enter 連打で
            // この MessageBox まで確定するため、既定が OK 側だと確認を足した意味が消える。
            if (
                targetExists
                && !_prompt.OkCancel(
                    $"同じ名前のファイルが既に存在します。上書きしますか? 保存先: {SanitizeForDisplay.OneLine(full, 200)}",
                    "上書きの確認",
                    defaultCancel: true
                )
            )
            {
                continue;
            }

            var newEncoding = EncodingCatalog.Get(picked.CodePage);

            // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告して続行/選び直しを選ばせる。
            // Load 経路の HadReplacementChar 警告と対称。UTF-8(65001) は BMP+astral 全表現可でスキップ。
            // 設計書 §4.5: キャンセルは中止ではなく**ダイアログへ戻す**。文字コードのコンボボックスは
            // その SaveAs ダイアログの中にあるので、中止して開き直させると打ち直しを強いることになる。
            // 副作用として、上書きを承諾した後でここをキャンセルすると次の周回で上書き確認が
            // もう一度出る。これは意図した挙動: 承諾は「今回の周回で選ばれた保存先」への答なので、
            // 保存先を選び直せる状態へ戻る以上、前の周回の承諾を持ち越してはいけない。
            // defaultCancel: true は S-12(上書き確認)と**対称**。破壊的な確認は両方とも
            // 安全側の既定に置く。当初は「文字コードは直前にユーザー自身が選んだのだから
            // 非対称でよい」としていたが、根拠が 2 つとも崩れた:
            // (a) 非対称のコストの裏付けは「既定がキャンセルだと誤爆した Enter が SaveAs 全体を
            //     中止し、打ち直しを強いる」ことだった。上の continue 化でそのコストが消えた。
            // (b) コンボの初期値は seed(= doc.State.Encoding)由来なので、Shift_JIS で読み込んだ
            //     文書に絵文字を貼れば、ユーザーが文字コードに一度も触れなくても警告条件が成立する。
            //     しかも Enter 押しっぱなしでは読み上げの前に確定するため警告は知覚されない。
            //     知覚されない警告は警告ではない(S-12 の論旨そのもの)。
            if (
                picked.CodePage != 65001
                && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding)
                && !_prompt.OkCancel(
                    "選択した文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。続行しますか?",
                    "文字コードの警告",
                    defaultCancel: true
                )
            )
            {
                continue; // 文字コードはこのダイアログで選び直せるので戻す(設計書 §4.5)
            }

            // 新エンコード/改行/BOM を State に反映してから WriteToPath へ(既存 WriteToPath は State を参照する)。
            // C-2 追補 I-1: WriteToPath 失敗時は元の Encoding/LineEnding/HasBom へロールバック
            // (State だけ更新済で Path が旧のままだと後続の Ctrl+S が元ファイルを別エンコードで
            // サイレント上書きする=データ破損)。
            var oldEncoding = doc.State.Encoding;
            var oldLineEnding = doc.State.LineEnding;
            var oldHasBom = doc.State.HasBom;
            doc.State.Encoding = newEncoding;
            doc.State.LineEnding = picked.LineEnding;
            doc.State.HasBom = picked.HasBom;

            if (!WriteToPath(doc, full))
            {
                doc.State.Encoding = oldEncoding;
                doc.State.LineEnding = oldLineEnding;
                doc.State.HasBom = oldHasBom;
                return false;
            }
            doc.State.Path = full;
            DocumentManager.UpdateLabel(doc);
            _metaChanged();
            RegisterRecent(full); // 保存先も最近のファイルへ
            return true;
        }
    }

    /// <summary>
    /// 指定エンコードでバッファ全文が損失なく符号化できるかを事前判定する
    /// (SaveAs のダウングレード警告用 + A-10 で Ctrl+S の劣化警告からも呼ぶ)。
    /// 既知の限界: 8192 文字のチャンクごとに独立して GetByteCount を呼ぶため、サロゲートペアが
    /// チャンク境界で割れると孤立サロゲートと見なして false を返す(誤検知)。呼出側は 2 箇所とも
    /// UTF-8 を対象外にしており、非 UTF-8 で astral を含む文書は実際に劣化するため実害はない。
    /// </summary>
    private static bool CanEncodeBuffer(TextBuffer buffer, Encoding encoding)
    {
        try
        {
            var probeEnc = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            );
            using var reader = buffer.Current.CreateReader();
            char[] buf = new char[8 * 1024];
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                probeEnc.GetByteCount(buf, 0, n);
            }
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// A-19: 直入力の相対パス(memo.txt)を絶対パスへ正規化する。未正規化のまま State.Path に
    /// 残すと保存先が起動時のカレントディレクトリに依存し、hot exit 復元で無言の無題化を招く。
    /// 正規化できない入力は例外ではなく <see cref="PathNormalizeStatus.Invalid"/> で戻り、
    /// 呼出側で「入力し直し」に落とす: SR ユーザーの直入力がそのまま届く面なので
    /// 未捕捉例外ダイアログにしない(例外を握る実体は seam の中にある)。
    /// (かつて PathKey.For も内部で GetFullPath していたが、あちらは失敗時に空文字へ落として
    /// dedup キーを 1 件へ集約する契約(CSV-L-8)= ユーザーに直させる本メソッドとは契約が違った。
    /// 実消費者が消えたので最終レビュー Q-I-2 でメソッドごと削除した。)
    /// <para>
    /// <b>Issue #48 (S-15)</b>: 以前ここは <see cref="System.IO.Path.GetFullPath(string)"/> を
    /// 直接呼び、remarks に「GetFullPath は <c>GetFullPathNameW</c> による名前解決のみで
    /// 実 I/O を行わないので、握り潰してはいけない実 I/O エラーを飲み込む余地はない」と
    /// 書いていた。<b>これは誤り</b>で、正規化後のパスに <c>~</c> が含まれると
    /// <c>GetLongPathName</c>(境界の無い実 FS / ネットワーク呼び出し)が走り、不達の共有に対して
    /// 約 21 秒 UI スレッドを止める(実測 21,002 ms・2026-08-23)。この誤認が S-15 を通した。
    /// 実 I/O を伴う以上、結果は例外の有無ではなく 3 状態(Ok / Invalid / TimedOut)になる。
    /// 境界は <see cref="IReachabilityProbe.NormalizePathWithTimeout"/> 側で張る。
    /// </para>
    /// <para>
    /// <c>GetFullPath</c> がどの入力でどの例外型を投げるかの実測記録・V-2 の経緯は、フィルタの
    /// 実体と離れないよう移設先 <see cref="FileReachabilityProbe.NormalizePathWithTimeout"/> の
    /// remarks へ移してある。
    /// </para>
    /// </summary>
    private PathNormalizeResult NormalizeSavePath(string input) =>
        _reachabilityProbe.NormalizePathWithTimeout(input, NormalizeTimeout);

    /// <summary>
    /// 改行を State.LineEnding に正規化してから本文を取得し、原子的に保存する。
    /// 例外は _prompt.Error で通知し false を返す。
    /// </summary>
    /// <remarks>
    /// CSV-M-2(2026-07-20): 冒頭に到達性プローブを追加し、UNC / マップドネットワーク
    /// ドライブは 5 秒プローブで到達不能なら以下のロールバック導線を発火する前に return false する
    /// (Load 側 HIGH-6 と対称=Save でも 60 秒 UI 凍結を回避)。
    /// A-4(2026-08-23): そのプローブを <see cref="TryInspectSaveTarget"/>(保存先意味論)へ切り替えた。
    /// 読み取り側の <see cref="TryProbeFileExists"/> は File.Exists 意味論のため、まだ存在しない
    /// 新規ファイルを常に到達不能と誤判定していた(= ネットワーク共有へ新規保存できない症状)。
    ///
    /// Batch A Task 1(2026-07-15): WriteToPath 失敗時に ConvertEols で書き換わった本文と保存点(Modified)
    /// をロールバックする。放置すると「保存に失敗したのに本文の EOL は書き換わったまま」という
    /// 静音喪失が残る。
    ///
    /// A-11(2026-08-28): その機構を<b>作り直した</b>。旧機構は「ConvertEols 前の
    /// <see cref="TextBuffer"/> 参照を握り、失敗時に
    /// <see cref="kxEdit.Editor.EditorControl.SetOrReplaceSource"/> で参照だけを戻す」もので、
    /// <b>ConvertEols がバッファ参照ごと差し替える</b>ことに全面的に依存していた。ConvertEols は
    /// in-place の 1 Undo 単位になったので、この前提は崩れている(<c>ReferenceEquals</c> が
    /// 常に true =ロールバックが<b>黙って no-op</b> になる)。現在は「ConvertEols が Undo 履歴へ
    /// 記録したか」を戻り値で受け取り、記録したときだけ
    /// <see cref="kxEdit.Editor.EditorControl.UndoEolConversion"/> で 1 つだけ取り消す。
    /// <para>
    /// <b>取り消し側の契約(fast-path で取り消さない理由・<c>Undo</c> を流用しない理由・
    /// キャレットを明示復元する理由)は
    /// <see cref="kxEdit.Editor.EditorControl.UndoEolConversion"/> の remarks に一本化してある。</b>
    /// ここに写すと片方だけ直したときに黙って陳腐化する
    /// (<c>EditorControl.ReplaceSource</c> の remarks が自己申告しているのと同じ失敗形。
    /// 最終レビュー I-3)。本メソッド固有の事情だけを書く:
    /// </para>
    /// <para>
    /// <b>捕捉の位置が load-bearing</b>。復元に使うキャレット / 選択は <c>ConvertEols</c> を呼ぶ
    /// <b>前</b>に、しかも <c>try</c> の<b>外</b>で捕捉する。前でなければならないのは、変換で
    /// char オフセットが動くから(変換を取り消した本文に対して正しいのは変換前の値だけ。§10.12 (2))。
    /// <c>try</c> の外なのは、<c>ConvertEols</c> 自身が投げる経路
    /// (<see cref="DocumentTooLargeException"/>)でも catch 節から参照するため。
    /// </para>
    /// </remarks>
    private bool WriteToPath(Document doc, string path)
    {
        // CSV-M-2 → A-4: リモートは 5 秒プローブ。存在確認ではなく「書き込み先が確定できるか」を見る
        // (読み取り側の TryProbeFileExists は File.Exists 意味論で、新規ファイルを常に到達不能と誤判定した)。
        // ConvertEols 副作用を起こす前に短絡することで、プローブ失敗時に「本文の EOL が書き換わる」を
        // 発生させない。exists は書込側では使わない(上書き確認は SaveAsDocument が事前に済ませる)。
        if (!TryInspectSaveTarget(path, out _))
            return false;

        // A-11: ConvertEols 前のキャレット / 選択を捕捉する(失敗時ロールバックの復元値)。
        // ConvertEols は変換後も同じ論理位置へ caret を復元するが、char オフセットは変換で動く。
        // 変換を取り消せば本文は変換前と同一になるので、ここで捕捉した「変換前のオフセット」が
        // そのまま正しい復元値になる(設計書 2026-08-28 §10.12 (2))。
        // try の外で捕捉する: ConvertEols 自身が throw する経路(DocumentTooLargeException)でも
        // catch 節から参照するため。
        int anchorBefore = doc.Editor.SelectionAnchor;
        int caretBefore = doc.Editor.CaretCharOffset;
        // ConvertEols が Undo 履歴へ記録したか。false のまま catch へ落ちたら取り消してはならない
        // (fast-path で 1 つ戻すとユーザーの直前の編集が消える=設計書 §5.3)。
        bool eolConverted = false;
        try
        {
            ApplyEol(doc);
            bool wasReadOnly = doc.Editor.ReadOnly;
            if (wasReadOnly)
                doc.Editor.ReadOnly = false;
            try
            {
                eolConverted = doc.Editor.ConvertEols(doc.Editor.EolMode);
            }
            finally
            {
                if (wasReadOnly)
                    doc.Editor.ReadOnly = true;
            }
            // P6 Task 10: TextBuffer 版 Save に切替(SnapshotText 経由の string 全文化を回避)。
            // CurrentBuffer は SetSource 前でも空 TextBuffer を返す=null チェック不要。
            TextFileService.Save(
                path,
                doc.Editor.CurrentBuffer,
                doc.State.Encoding,
                doc.State.HasBom
            );
            doc.Editor.SetSavePoint();
            DocumentManager.UpdateLabel(doc);
            _metaChanged();
            return true;
        }
        // V-1: ArgumentException を握る = Load 側(LoadInto の Task 5 review I-1)との非対称を戻す。
        // SaveAsDocument の前段ガードを通らない経路が 2 本ある: Ctrl+S(SaveDocument は
        // State.Path をそのまま WriteToPath へ渡す)と、悪意ある backup JSON 由来の復元タブ
        // (OriginalPathValidator の BlockedRoots は C:\Windows 等なので `C:\` は Ok で通る)。
        // そこから AtomicFile.Write の Path.Combine(null, ...) に届くと ArgumentNullException が
        // 素通りし、**ConvertEols 後のロールバックが発火しないまま Modified=false が残る**
        // = 保存していない本文が確認なしで閉じられる(ConfirmDiscardIfDirty は !Modified で即 true)。
        // A-11(2026-08-28): DocumentTooLargeException を追加した。ConvertEols は LF → CRLF で
        // 総バイト数を増やすため、512MB 直下の文書では TextBufferBuilder.AddChunk が投げうるのに
        // 本フィルタに一致せず**未処理で抜けていた**(main 既存の穴)。ロールバックは no-op でよい:
        // ConvertEols は木を組み終えてから初めて _buffer に触るので、この例外が飛ぶ時点では
        // 本文もキャレットも EolMode も未変更のまま=eolConverted も false のままである。
        catch (Exception ex)
            when (ex
                    is System.IO.IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException
                        or NotSupportedException
                        or ArgumentException
                        or DocumentTooLargeException
            )
        {
            // A-11: ConvertEols が記録したときだけ 1 つ取り消す(fast-path は no-op)。キャレット /
            // 選択は変換前へ明示復元し、スクロール位置は触らない(= ロールバックが動かさない)。
            doc.Editor.UndoEolConversion(eolConverted, anchorBefore, caretBefore);
            // 最終レビュー L-V3: 上限超過だけ文言を分ける。ここへ来る実経路は「文書自体は上限内だが
            // 改行コードを変換すると超える」であり(唯一の投げ手が ConvertEols)、共通文言の
            // 「文書サイズ上限を超えました」だけでは「この文書は一切保存できない」と読める。
            // 逃げ道(改行コードを LF にすればサイズが増えない)を案内する。
            // 文言は固定文字列で組む=外部入力を含まないので SanitizeForDisplay は要らない。
            if (ex is DocumentTooLargeException)
            {
                _prompt.Error(
                    "保存できませんでした: 改行コードを変換すると文書サイズ上限(512 MB)を超えます。"
                        + "「名前を付けて保存」で改行コードに LF を指定すると、サイズを増やさずに保存できる場合があります。",
                    "エラー"
                );
                return false;
            }
            // CSV-L-5: ex.Message にファイル名 (path) が混入し得るため、SanitizeForDisplay で無害化。
            _prompt.Error(
                $"保存できませんでした: {SanitizeForDisplay.OneLine(ex.Message, 200)}",
                "エラー"
            );
            return false;
        }
    }

    // ==================== 確認 / 復元 ====================

    /// <summary>
    /// 変更があれば 保存/破棄/キャンセル を問う。Yes なら保存成否、No なら true、
    /// キャンセルなら false を返す。対象ドキュメントを明示で受ける（終了時の他タブ確認用）。
    /// </summary>
    public bool ConfirmDiscardIfDirty(Document doc)
    {
        if (!doc.Editor.Modified)
            return true;
        var r = _prompt.YesNoCancel($"{doc.State.DisplayName} の変更を保存しますか？", "kxEdit");
        return r switch
        {
            DialogResult.Yes => SaveDocument(doc),
            DialogResult.No => true,
            _ => false,
        };
    }

    /// <summary>
    /// A-16 (ii): 復元先パスの検証を<b>境界付き正規化の後ろ</b>で行う。復元経路の 3 呼出点
    /// (<see cref="RestoreFromBackup"/> の <c>rec.OriginalPath</c> /
    /// <see cref="RestoreDirtyFromBackup"/> の <c>rec.Path</c> / path-only extras の
    /// <c>bk.OriginalPath</c>)が共有する。
    /// <para>
    /// <b>なぜ要るか</b>: <see cref="OriginalPathValidator.Check"/> 冒頭の
    /// <see cref="System.IO.Path.GetFullPath(string)"/> は名前解決のみで実 I/O を行わない ——
    /// <b>ただし正規化後のパスに <c>~</c> が含まれる場合だけは <c>GetLongPathName</c> を呼ぶ</b>ため、
    /// 不達の共有に対して約 21 秒 UI スレッドを止める(Issue #48 / S-15。実測 21,002 ms)。
    /// 復元は起動時に無人で走り、レコード数は攻撃者制御の JSON で増やせるので、
    /// 1 レコードあたりの待ちがそのまま「起動できない時間」になる。
    /// </para>
    /// <para>
    /// <b>3 呼出点で 1 つの関数に寄せる理由</b>: 「正規化 → 検証」の順序と
    /// 「確定しなければ検証しない」は<b>セキュリティ境界の一部</b>で、3 か所へ写経すると
    /// 片方だけ直す/片方だけ足し忘れる形の劣化が起きる。呼出側に残す判断は
    /// 「<see cref="PathValidation.Rejected"/> をどう見せるか」だけにする。
    /// </para>
    /// <para>
    /// <b>制御フローは <see cref="PathNormalizeStatus.Invalid"/> と
    /// <see cref="PathNormalizeStatus.TimedOut"/> を区別しない</b>: どちらも
    /// <see cref="PathValidation.Rejected"/> へ丸め、呼出側の<b>既存の</b>拒否経路
    /// (無題降格 / レコード skip)へそのまま合流させる = 分岐を増やさない。
    /// </para>
    /// <para>
    /// <b>ただし <paramref name="unreachable"/> だけは別に返す</b>(唯一の例外)。
    /// <see cref="RestoreFromBackup"/> のダイアログ経路で文言を出し分けるためで、根拠は
    /// 「ユーザーに見える文言であり SR で読み上げられる」こと: 不達の共有を「パスが無効」と
    /// 説明すると「バックアップが壊れた」と誤解させるが、実際はネットワークが届かないだけで、
    /// 共有が復帰すれば元パスは有効。<c>TryOpenOrActivateCore</c> / <c>SaveAsDocument</c> が
    /// 既に同じ理由で 2 文言に分けており、ここを揃えないと A-16 で直したはずの症状が
    /// <b>文言として残る</b>。
    /// <b>出し分けはダイアログ経路だけ</b>で、silent な 2 呼出点は <c>out _</c> で捨てて
    /// trace の粒度を変えない(trace は開発者向けで、キーワードを増やす価値が出し分けの
    /// 情報量に見合わない)。
    /// </para>
    /// <para>
    /// <b><c>Check</c> 自身の正規化は残す</b>(消さないこと)。
    /// <see cref="OriginalPathValidator"/> のクラス doc が「再正規化の順序が load-bearing」と
    /// 明記しており、外すと事後条件が検査した形と BlockedRoots が照合する形が食い違う。
    /// 2 度目の <c>GetFullPath</c> が走るのは 1 本目が<b>期限内に確定したときだけ</b>なので、
    /// 不達共有の約 21 秒はこの経路から消える。
    /// <b>ただし 2 度目は依然として無境界である</b> —— 「1 本目が通ったなら 2 度目は速い」とは
    /// 言い切れない(<c>GetLongPathName</c> が失敗して <c>~</c> が残ったまま <c>Ok</c> で返る形が
    /// ありうる。実測はしていない)。ここを完全に閉じるには <c>Check</c> 側を境界付きにする
    /// 必要があり、本 Task の範囲外。
    /// </para>
    /// <para>
    /// <b>前置ガード <see cref="System.IO.Path.IsPathFullyQualified(string)"/> は挙動不変のために要る</b>
    /// (<c>Check</c> 冒頭の同じガードとの<b>意図的な重複</b>)。これが無いと、相対パスの
    /// <c>OriginalPath</c>(バックアップは常に絶対パスで書かれるので、攻撃者 JSON か旧形式でしか
    /// 現れない)を前置正規化がプロセスの CWD 基準で絶対パスへ解決してしまい、以前は入口で
    /// <c>Rejected</c> = 無題降格だったものが <c>Check</c> の本体まで進む。本 Task は凍結の解消で
    /// あって検証の緩和ではないので、その窓は開けない(受理範囲を広げる変更は独立に議論する)。
    /// 副次的に、相対入力では seam を 1 本も使わずに済む。
    /// </para>
    /// </summary>
    private PathValidation CheckRestoreTarget(
        string path,
        out string normalized,
        out bool unreachable
    )
    {
        normalized = string.Empty;
        // unreachable は各 return で必ず書く(先頭で false を置かない)。先頭に既定値を置くと
        // 将来 return を 1 本足したときに「書き忘れても通る」形になり、文言が黙って
        // 「無効」側へ倒れる —— TryOpenOrActivateCore が out bool を避けて組を返している
        // のと同じ理由(当該メソッドの doc の m-3)。
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            unreachable = false; // 相対 / 打ち間違い = 到達性の話ではない
            return PathValidation.Rejected;
        }
        var norm = _reachabilityProbe.NormalizePathWithTimeout(path, NormalizeTimeout);
        if (norm.Status != PathNormalizeStatus.Ok)
        {
            unreachable = norm.Status == PathNormalizeStatus.TimedOut;
            return PathValidation.Rejected;
        }
        unreachable = false; // 正規化は確定した = 以降の Rejected は検証の結論
        return OriginalPathValidator.Check(norm.Full, out normalized);
    }

    /// <summary>バックアップ記録を新タブへ復元する。本文・メタを載せ、保存点を破棄して dirty にする。
    /// HIGH-2: OriginalPath がシステム系ルート配下(Windows/System32/ProgramFiles 等)なら
    /// 無題タブへフォールバックし、後続の Ctrl+S での任意ファイル上書き導線を遮断する。</summary>
    public Document RestoreFromBackup(BackupRecord rec)
    {
        var doc = _docs.CreateNew();

        // OriginalPath 検証: 攻撃 JSON は Untitled にフォールバックする(HIGH-2)。
        // ユーザ配下・UNC は Ok=そのまま復元先パスとして継続。
        bool useUntitled = rec.OriginalPath is null;
        string? safePath = null;
        if (!useUntitled)
        {
            // A-16 (ii): 正規化を境界付きにしてから検証する(理由は CheckRestoreTarget の doc)。
            var status = CheckRestoreTarget(
                rec.OriginalPath!,
                out var normalized,
                out bool unreachable
            );
            if (status == PathValidation.Ok)
            {
                safePath = normalized;
                NoteIfBackupStale(normalized, rec); // A-1 第 2 層(設計 2026-08-22 §4.3)
            }
            else
            {
                useUntitled = true;
                // 統合復元 Task 5: silent 経路(WithLoadErrorPromptSuppressed 実行中=RestoreSession の
                // extras 等)ではダイアログを出さない。ダイアログ経路(OfferRestoreOnStartup からの
                // 呼出)は非抑止スコープ=挙動不変。**文言の出し分けはこのガードの内側だけ**で、
                // silent 経路(RestoreDirtyFromBackup / path-only extras の trace)は
                // 既存の粒度のまま = 出し分けはユーザーに見える面に限る。
                if (!_suppressLoadErrorPrompt)
                {
                    // A-16 (ii) fixup: 到達不能(TimedOut)と打ち間違い / 検証 NG(Invalid・
                    // Rejected)で文言を分ける。**本 Task で唯一「分岐を増やさない」規律の
                    // 例外にした箇所**で、根拠は「ユーザーに見える文言であり SR で読み上げられる」
                    // こと: 不達の共有を「パスが無効」と説明すると「バックアップが壊れた」と
                    // 誤解させるが、実際はネットワークが届かないだけで共有が復帰すれば元パスは
                    // 有効。本ブランチのテーマがまさに「無言のパス喪失」なので、ここを揃えないと
                    // 直したはずの症状が文言として残る。
                    // 言い回しは TryOpenOrActivateCore / SaveAsDocument の既存文言に合わせ、
                    // 主語だけをこの画面のものにする(新しい表現を作らない)。
                    // 「n 秒」は NormalizeTimeout から補間する(二重管理を作らない)。
                    // CSV-L-5: OriginalPath は攻撃者 JSON 由来 (RLO/改行 で拡張子偽装や複数行注入)。
                    // path 部分のみ OneLine で無害化し、案内文と "\n\n元パス:" の改行区切りは保持する。
                    // 無害化と改行区切りを持つ**末尾は 1 本のまま**にして、分岐で二重管理しない。
                    _prompt.Warn(
                        (
                            unreachable
                                ? $"バックアップの元パスに到達できませんでした({NormalizeTimeout.TotalSeconds:0} 秒)。ネットワーク接続を確認してください。無題タブとして復元します。"
                                : "バックアップの元パスが無効なため、無題タブとして復元します。"
                        )
                            + $"必要に応じて「名前を付けて保存」してください。\n\n元パス: {SanitizeForDisplay.OneLine(rec.OriginalPath, 200)}",
                        "警告"
                    );
                }
            }
        }

        doc.State.Path = safePath;

        // 無題は元の連番を保ち、ダイアログ表示と復元後タブの番号を一致させる。連番カウンタは
        // 既存の最大値以上へ進め、以後の新規無題と衝突しないようにする。
        if (useUntitled)
        {
            int n = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
            if (n > _untitledSeq)
                _untitledSeq = n;
            doc.State.UntitledNumber = n;
        }
        else
        {
            doc.State.UntitledNumber = 0;
        }
        // BK-L-1 / BK-L-2: 攻撃者 JSON が範囲外 LineEndingId / 未サポート CodePage を持つ場合、
        // 素の `(LineEnding)rec.LineEndingId` は enum 範囲外のまま突き抜けて
        // ToEolString()/ToDisplayString() の `_ => "\r\n"` で silent 上書きになり、
        // 素の `EncodingCatalog.Get(rec.CodePage)` は ArgumentException / NotSupportedException を
        // MainForm まで伝播させて他タブの復元まで巻き添え喪失させる。
        // 復元は「壊れた入力でもプロセス継続」を優先し、いずれも安全値(CRLF / UTF-8)へ silent
        // フォールバックする(_prompt.Warn は出さない=ユーザは復元後 Save で確定できる)。
        doc.State.Encoding = SafeEncodingOrFallback(rec.CodePage);
        doc.State.HasBom = rec.HasBom;
        doc.State.LineEnding = SafeLineEndingOrFallback(rec.LineEndingId);

        // P6 Task 10: BackupRecord.Content は string 保存(Task 12 で Stream 化判断)なので、
        // ここでは TextBuffer.FromString でラップして SetOrReplaceSource に流す(パターン統一)。
        // 復元は fresh Document への初回差し込みなので実質 SetSource 経路になる。
        // レビュー M-5: 旧 Text setter は内部で `value ?? string.Empty` していた=null 耐性の
        // 対称性を戻す(BackupRecord の JSON 破損時に「タブ生成失敗」を避け「空タブ復元」で継続)。
        doc.Editor.SetOrReplaceSource(TextBuffer.FromString(rec.Content ?? string.Empty));
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        // 保存点を破棄して Modified=true に固定 → ユーザーが保存できる。FromString の fresh バッファは
        // 生成時に保存点を持つため「SetSavePoint を打たない」だけではクリーン扱いになり、タブ「*」なし・
        // 終了時の保存確認スキップ・次の Reconcile でバックアップ削除=復元内容のサイレント喪失につながる。
        doc.Editor.ClearSavePoint();
        DocumentManager.UpdateLabel(doc);
        _metaChanged();
        return doc;
    }

    /// <summary>
    /// hot exit 統合復元(設計 2026-07-23 統合 §3.3)。レイアウトのタブ順に復元し、レイアウト外の
    /// バックアップ(extras)を追加復元する。silent 経路=ダイアログは一切出さない(失敗パスは
    /// 集約用に返す)。per-record try/catch で「一つの悪いレコードが他を壊さない」不変を維持。
    /// adoptRestored: バックアップ由来の復元文書を Coordinator 管理下へ引き取る callback
    /// (silent 経路=BackupCoordinator.AdoptRestored。レガシー移行の合成レコードでは null=
    /// 通常の RegisterNew 経路で次 Reconcile が保護する)。
    /// </summary>
    public IReadOnlyList<string> RestoreSession(
        kxEdit.Core.Session.SessionLayout? layout,
        IReadOnlyList<BackupRecord> backups,
        Document? initialEmpty,
        Action<Document, BackupRecord>? adoptRestored
    )
    {
        var failedPaths = new List<string>();
        Document? activeDoc = null;
        int openedCount = 0;

        // Id → record(重複 Id は新しい方を採用=別 session dir の stale と競合した場合)
        var byId = new Dictionary<string, BackupRecord>(StringComparer.Ordinal);
        foreach (var b in backups)
            if (!byId.TryGetValue(b.Id, out var prev) || b.TimestampUtc > prev.TimestampUtc)
                byId[b.Id] = b;
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        WithLoadErrorPromptSuppressed(() =>
        {
            if (layout is not null)
            {
                foreach (var rec in layout.Tabs)
                {
                    try
                    {
                        var doc = RestoreLayoutRecord(
                            rec,
                            byId,
                            consumed,
                            failedPaths,
                            adoptRestored
                        );
                        if (doc is null)
                            continue;
                        openedCount++;
                        if (rec.IsActive)
                            activeDoc = doc;
                    }
                    catch (Exception ex)
                    {
                        if (rec.Path is not null)
                            failedPaths.Add(rec.Path);
                        System.Diagnostics.Trace.TraceWarning(
                            "kxEdit: restore-record-failed: {0}",
                            kxEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
                        );
                    }
                }
            }
            // extras: レイアウト外バックアップ=クラッシュ直前に開いたタブ・他インスタンス遺物・
            // 旧「あとで」孤児。安全側=拾って開く(設計 §3.3 手順 4)。
            var extras = byId
                .Values.Where(b => !consumed.Contains(b.Id))
                .OrderByDescending(b => b.TimestampUtc)
                .ToList();
            int extrasOpened = 0;
            foreach (var bk in extras)
            {
                // 攻撃 JSON の大量植え込みで起動時に無制限のタブ生成をしない
                // (session-state.json の MaxTabs 切り詰めと対称の防御。設計 §7。
                // 値の二重定義を避けるためレイアウト側と同じ定数を参照する)。
                if (extrasOpened >= kxEdit.Core.Session.SessionLayoutStore.MaxTabs)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "kxEdit: restore-extras-capped ({0} -> {1})",
                        extras.Count,
                        kxEdit.Core.Session.SessionLayoutStore.MaxTabs
                    );
                    break;
                }
                try
                {
                    var doc = RestoreExtraBackup(bk, failedPaths, adoptRestored);
                    if (doc is not null)
                    {
                        openedCount++;
                        extrasOpened++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "kxEdit: restore-extra-failed: {0}",
                        kxEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
                    );
                }
            }
        });

        if (activeDoc is not null)
            _docs.Activate(activeDoc);
        if (openedCount > 0 && initialEmpty is not null)
            _docs.TryClose(initialEmpty, _ => true); // 空無題タブは無条件破棄
        _metaChanged();
        return failedPaths;
    }

    private Document? RestoreLayoutRecord(
        kxEdit.Core.Session.SessionLayoutRecord rec,
        Dictionary<string, BackupRecord> byId,
        HashSet<string> consumed,
        List<string> failedPaths,
        Action<Document, BackupRecord>? adoptRestored
    )
    {
        BackupRecord? bk =
            rec.BackupId is not null && byId.TryGetValue(rec.BackupId, out var found)
                ? found
                : null;
        BackupRecord? pathOnlyBk = null; // E11 demote した record(open 成功後の adopt 用に保持)
        bool demotedPathOnly = false;
        if (bk is not null && bk.Content is null)
        {
            // E11: path-only(>32M)を silent で「空 dirty+実パス」に載せない(Ctrl+S 切り詰め事故の遮断)。
            // consumed に積んで extras での空 dirty 復活も防ぐ。
            System.Diagnostics.Trace.TraceWarning(
                "kxEdit: restore-path-only-demote: {0}",
                kxEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path ?? bk.Id, 200)
            );
            consumed.Add(bk.Id);
            pathOnlyBk = bk;
            bk = null;
            demotedPathOnly = true;
        }

        if (rec.Path is not null)
        {
            if (bk is not null)
            {
                var doc = RestoreDirtyFromBackup(rec, bk);
                consumed.Add(bk.Id);
                adoptRestored?.Invoke(doc, bk);
                return doc;
            }
            if (rec.BackupId is not null && !demotedPathOnly)
                // E9': dirty 参照はあるがバックアップ欠落=編集は失われている → disk 再オープンへ demote
                System.Diagnostics.Trace.TraceWarning(
                    "kxEdit: dirty-backup-missing, demoting to disk reopen: {0}",
                    kxEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200)
                );
            // Issue #48 Task 5: 「既存タブを再利用したか」は TryOpenOrActivate 自身に答えさせる。
            // ここで _docs.FindByPath(rec.Path) を打つ旧形は、rec.Path が未正規化でありうる
            // (LegacySessionConverter が旧 LastSessionSnapshot の Path を素通しで載せる)一方で
            // FindByPath が区切り差を吸収しなくなったため、同じファイルなのに「既存タブ無し」へ
            // 倒れる。判定の使い道は下の adopt 門番なので、崩れると既存タブの adopt を上書きする。
            var (opened, reusedExisting) = TryOpenOrActivateCore(rec.Path, suppressAutoCsv: false);
            if (opened is null)
            {
                failedPaths.Add(rec.Path);
                // E11 demote の open 失敗時はレコード残置を受容(希少の二乗・30 日 sweep が回収)。
                return null;
            }
            opened.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
            // 最終品質パス I-1: E11 demote で消費した path-only レコードは adopt→clean 検出→
            // 次 tick 削除で残置させない(残置すると新レイアウトが参照しない Id として次回以降
            // 毎起動 extras 復活する=silent 経路に「すべて破棄」はない)。doc は clean なので
            // 次 Reconcile(ReconcileContent の Decide / layout-only では ReconcileMapMaintenance)が
            // 既存機構で Delete する。fast-path activate(既存タブ)には adopt しない=既存タブが
            // 別バックアップで adopt 済みの場合に Id 上書きで別のゾンビを作らないため(残置受容)。
            if (pathOnlyBk is not null && !reusedExisting)
                adoptRestored?.Invoke(opened, pathOnlyBk);
            return opened;
        }

        // 無題
        if (bk is not null)
        {
            var doc = RestoreUntitledFromBackup(rec, bk);
            consumed.Add(bk.Id);
            adoptRestored?.Invoke(doc, bk);
            return doc;
        }
        if (rec.BackupId is not null)
        {
            // E4': 無題の編集内容が欠落 → 空タブを作らず skip(誤解を招く空枠を出さない)
            System.Diagnostics.Trace.TraceWarning(
                "kxEdit: untitled-backup-missing, skipping record (untitled-{0})",
                rec.UntitledNumber
            );
            return null;
        }
        return RestoreUntitledFrame(rec); // BackupId=null=「空だった無題タブ」の枠を復元
    }

    /// <summary>dirty パスあり復元(E12): パスは rec.Path 側を検証して採用し、bk.OriginalPath は
    /// 信用しない。検証 NG は無題フォールバック(HIGH-2 踏襲・silent 経路のため Warn は出さず trace)。
    /// 本文・エンコーディング・改行は BackupRecord から(SafeEncodingOrFallback / E10 と同じ防御)。</summary>
    private Document RestoreDirtyFromBackup(
        kxEdit.Core.Session.SessionLayoutRecord rec,
        BackupRecord bk
    )
    {
        var doc = _docs.CreateNew();
        // A-16 (ii): 正規化を境界付きにしてから検証する(理由は CheckRestoreTarget の doc)。
        // unreachable は捨てる: ここは silent 経路で trace しか出さないので、到達不能と
        // 検証 NG を出し分ける相手がいない(文言の出し分けはダイアログ経路だけ)。
        var status = CheckRestoreTarget(rec.Path!, out var normalized, out _);
        if (status == PathValidation.Ok)
        {
            doc.State.Path = normalized;
            doc.State.UntitledNumber = 0;
            NoteIfBackupStale(normalized, bk); // A-1 第 2 層(設計 2026-08-22 §4.3)
        }
        else
        {
            doc.State.Path = null;
            doc.State.UntitledNumber = ++_untitledSeq;
            System.Diagnostics.Trace.TraceWarning(
                "kxEdit: restore-invalid-path-fallback-to-untitled: {0}",
                kxEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200)
            );
        }
        doc.State.Encoding = SafeEncodingOrFallback(bk.CodePage);
        doc.State.HasBom = bk.HasBom;
        doc.State.LineEnding = SafeLineEndingOrFallback(bk.LineEndingId);
        doc.Editor.SetOrReplaceSource(TextBuffer.FromString(bk.Content ?? string.Empty));
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.ClearSavePoint(); // Modified=true(RestoreFromBackup と同パターン)
        doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
        DocumentManager.UpdateLabel(doc);
        return doc;
    }

    /// <summary>無題 dirty 復元。統合後は WasModified を持たず常に Modified=true で復元する
    /// (無題の本文=バックアップ存在=dirty 相当。設計 §10)。</summary>
    private Document RestoreUntitledFromBackup(
        kxEdit.Core.Session.SessionLayoutRecord rec,
        BackupRecord bk
    )
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
        if (rec.UntitledNumber > _untitledSeq)
            _untitledSeq = rec.UntitledNumber;
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = SafeLineEndingOrFallback(bk.LineEndingId);
        doc.Editor.SetOrReplaceSource(TextBuffer.FromString(bk.Content ?? string.Empty));
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        doc.Editor.ClearSavePoint();
        doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
        DocumentManager.UpdateLabel(doc);
        return doc;
    }

    /// <summary>空の無題タブの枠を復元する(BackupId=null=終了時に空だったタブ。設計 §2.1)。</summary>
    private Document RestoreUntitledFrame(kxEdit.Core.Session.SessionLayoutRecord rec)
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
        if (rec.UntitledNumber > _untitledSeq)
            _untitledSeq = rec.UntitledNumber;
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = SafeLineEndingOrFallback(rec.LineEnding);
        DocumentManager.UpdateLabel(doc);
        return doc; // fresh バッファ=Modified=false・本文なし
    }

    /// <summary>extras(レイアウト外バックアップ)の復元。Content=null(path-only)は
    /// E11 と同方針で disk 再オープン(パス正当時のみ)・無題 path-only は skip。</summary>
    private Document? RestoreExtraBackup(
        BackupRecord bk,
        List<string> failedPaths,
        Action<Document, BackupRecord>? adoptRestored
    )
    {
        if (bk.Content is null)
        {
            if (bk.OriginalPath is null)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "kxEdit: extra-path-only-untitled-skipped: {0}",
                    kxEdit.Core.Text.SanitizeForDisplay.OneLine(bk.Id, 200)
                );
                return null;
            }
            // A-16 (ii): 正規化を境界付きにしてから検証する(理由は CheckRestoreTarget の doc)。
            // 本枝だけは seam を 2 回通る(ここ + TryOpenOrActivateCore の中)。確定しなかった
            // 場合はここで打ち切るので、実際に NormalizeTimeout を待つのは高々 1 本
            // (RestoreSession_NormalizesOncePerReopenedRecord の doc に内訳がある)。
            // unreachable は捨てる(理由は RestoreDirtyFromBackup 側の同じ呼出のコメント)。
            if (CheckRestoreTarget(bk.OriginalPath, out var normalized, out _) != PathValidation.Ok)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "kxEdit: extra-path-only-invalid-path-skipped: {0}",
                    kxEdit.Core.Text.SanitizeForDisplay.OneLine(bk.OriginalPath, 200)
                );
                return null;
            }
            // Issue #48 Task 5: レイアウト側(RestoreLayoutRecord)と同じ機構で判定する。
            // ここの normalized は OriginalPathValidator.Check の出力=同じ GetFullPath 由来なので
            // 旧形(呼出前の FindByPath)でも今のところ一致するが、「2 つの正規化器の出力が
            // 綴りまで同じ」という暗黙の前提に門番を預けたままにしない。
            var (opened, reusedExisting) = TryOpenOrActivateCore(
                normalized,
                suppressAutoCsv: false
            );
            if (opened is null)
            {
                failedPaths.Add(bk.OriginalPath);
                // open 失敗時はレコード残置を受容(希少の二乗・30 日 sweep が回収)。
                return null;
            }
            // 最終品質パス I-1: path-only 消費レコードは adopt→clean 検出→次 tick 削除で
            // 残置させない(レイアウト外 Id は次回以降も毎起動 extras としてゾンビ復活するため)。
            // fast-path activate(既存タブ)には adopt しない=Id 上書きで既存 adopt を壊さない。
            if (!reusedExisting)
                adoptRestored?.Invoke(opened, bk);
            return opened;
        }
        var doc = RestoreFromBackup(bk); // 既存経路(HIGH-2 検証・dirty 復元・無題連番)
        adoptRestored?.Invoke(doc, bk);
        return doc;
    }

    /// <summary>
    /// action の実行中は復元経路専用の抑止フラグをまとめて ON にする:
    /// (a) 復元経路の per-file ダイアログを抑止(呼出元が失敗パスを集約通知するため)。対象は
    ///     LoadInto の catch 内 _prompt.Error・LoadInto の置換文字 Warn・RestoreFromBackup の
    ///     「元パスが無効」Warn・<see cref="ReportUnreachable"/> を通す**全経路**。
    ///     ReportUnreachable は catch ではなく if ガードの本体で、抑止をそこへ集約した結果
    ///     読み取り側(<see cref="TryProbeFileExists"/>)だけでなく保存側
    ///     (<see cref="TryInspectSaveTarget"/>)も含む。main では 1 本の TryProbeReachability を
    ///     読み書き両側が共有していたので、**抑止の及ぶ範囲は main と同じ**(記述だけが陳腐化していた)。
    /// (b) LoadInto の RegisterRecent を抑止(復元は「ユーザーが開いた」相当でないため RecentFiles を汚さない)
    /// Task 5 で名前は変えずスコープを (a)+(b) に拡張(既存 seam の呼び出し側=Task 4 テストも
    /// (b) の抑止動作を暗黙に受けるが、テスト対象の path はダミー=RecentFiles 検証していないため無害)。
    /// finally で必ず復元し、nested 呼び出しでも安全。
    /// </summary>
    public void WithLoadErrorPromptSuppressed(Action action)
    {
        bool prevPrompt = _suppressLoadErrorPrompt;
        bool prevRecent = _suppressRegisterRecent;
        _suppressLoadErrorPrompt = true;
        _suppressRegisterRecent = true;
        try
        {
            action();
        }
        finally
        {
            _suppressLoadErrorPrompt = prevPrompt;
            _suppressRegisterRecent = prevRecent;
        }
    }

    // ==================== 内部 ====================

    /// <summary>開いた/保存したファイルを最近のファイルへ登録し、永続化＆メニュー再構築を促す。</summary>
    private void RegisterRecent(string path)
    {
        var s = _settings();
        s.RecentFiles = RecentFilesList.Add(s.RecentFiles, path, RecentFilesList.MaxItems);
        _saveSettings();
        _recentChanged();
    }

    /// <summary>doc.State.LineEnding をそのエディタの EOL モードへ反映する。</summary>
    private static void ApplyEol(Document doc) => doc.Editor.EolMode = doc.State.LineEnding;

    /// <summary>
    /// HIGH-6 + CSV-M-1: リモートパス(UNC / マップドネットワークドライブ)は 5 秒プローブで
    /// 既存ファイルを確認できなければ即 <c>_prompt.Error</c> を出して false を返す。
    /// ローカルは true を返して通常経路へ。
    /// **読み取り側専用**(現在の呼出は <see cref="LoadInto"/> のみ)。名前に反して実体は
    /// 存在確認(File.Exists 意味論)であり、まだ存在しない新規ファイルを到達不能と誤判定するため、
    /// 書き込み側は共有できない(= A-4 の機構)。書き込み側は
    /// <see cref="TryInspectSaveTarget"/>(到達性と既存有無を分けて返す)を使う。
    /// 判定は <see cref="RemotePathDetector.IsRemote(string)"/>(UNC + DriveInfo.DriveType==Network)。
    /// ローカル固定/リムーバブルは判定を skip(挙動不変)、リモート正常経路は reachable=true で通過(挙動不変)。
    /// </summary>
    private bool TryProbeFileExists(string path)
    {
        if (
            RemotePathDetector.IsRemote(path)
            && !_reachabilityProbe.ProbeFileExistsWithTimeout(path, TimeSpan.FromSeconds(5))
        )
        {
            ReportUnreachable(path);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 保存先の既存有無を得る。到達不能なら false(エラー表示済み)。
    /// <b>false を返したとき <paramref name="exists"/> は無意味</b>(未存在と到達不能を区別できない
    /// =<see cref="SaveTargetProbeResult"/> の契約)。必ず戻り値を先に見て短絡すること。
    /// リモート(UNC / マップドネットワークドライブ)だけを 5 秒プローブに載せる。
    /// ローカルを素通りさせるのは意図的(設計書 §3.3)。ただし**設計書が挙げる根拠
    /// 「ゲートを外すとロールバック導線に届かなくなる」は誤り**(Task 2 実装時に実測):
    /// Encoding/HasBom/LineEnding/Path のロールバックは <see cref="WriteToPath"/> の戻り値 false で
    /// 駆動され、この短絡は ApplyEol / ConvertEols より手前なので本文側の巻き戻し対象すら発生しない。
    /// ゲートを外して到達不能に倒れてもロールバックはそのまま発火する。
    /// **維持する本当の理由は誤ったエラー文言**: <see cref="ReportUnreachable"/> の文言は
    /// 「ネットワークパスに到達できません」の 1 種類しかないため、ゲートを外すとローカルの
    /// 存在しないフォルダー配下への保存(SR ユーザーが直入力でタイプミスした典型ケース)にまで
    /// この文言が出る。現行の DirectoryNotFoundException 由来
    /// 「保存できませんでした: 指定されたパスが見つかりません」より明確に劣化する。
    /// 文言を分岐させれば直るが、それは設計書 §6 が YAGNI として除外した範囲。
    /// 副次的な理由: 挙動変更にあたること(CLAUDE.md §2)と、Ctrl+S のたびに Task.Run + Wait が
    /// 1 回増えること。
    /// **このゲートを守っている網は Save_SkipsProbe_ForLocalPath と
    /// SaveAs_LocalNewFile_DoesNotProbe の 2 本**(ロールバックテストは守っていない=
    /// 緑のままゲートを外せる。上記の誤った根拠を信じて「ロールバックテストが緑だから
    /// ゲートは不要」と結論しないこと)。
    /// </summary>
    /// <param name="exists">
    /// 保存が上書きになる(A-7 (a) の上書き確認の入力)。読むのは <see cref="SaveAsDocument"/> だけで、
    /// <see cref="WriteToPath"/> は捨てる(Ctrl+S の上書きは確認しない=自分が開いているファイルへの保存)。
    /// </param>
    private bool TryInspectSaveTarget(string path, out bool exists)
    {
        if (RemotePathDetector.IsRemote(path))
        {
            var probe = _reachabilityProbe.ProbeSaveTargetWithTimeout(
                path,
                TimeSpan.FromSeconds(5)
            );
            exists = probe.FileExists;
            if (!probe.Reachable)
            {
                ReportUnreachable(path);
                return false;
            }
            return true;
        }
        // ローカルは SMB 60 秒凍結の懸念がない。
        exists = System.IO.File.Exists(path);
        return true;
    }

    /// <summary>
    /// 到達不能の通知。CSV-L-5: path は外部入力(SR ユーザーの直入力・grep / BackupRecord 由来)なので
    /// SanitizeForDisplay で無害化する。復元経路(<see cref="WithLoadErrorPromptSuppressed"/> 実行中)は
    /// per-file ダイアログを抑止し、呼出元で失敗パスを集約通知させる(戻り値 false は伝播)
    /// = 2026-07-23 復元設計 Task 4。
    /// </summary>
    private void ReportUnreachable(string path)
    {
        if (_suppressLoadErrorPrompt)
            return;
        _prompt.Error(
            $"ネットワークパスに到達できません: {SanitizeForDisplay.OneLine(path, 200)}",
            "エラー"
        );
    }

    /// <summary>
    /// BK-L-2: BackupRecord.CodePage を <see cref="Encoding"/> に変換する。攻撃者 JSON の壊れた値
    /// (未サポート/範囲外)で <see cref="Encoding.GetEncoding(int)"/> が投げる
    /// <see cref="ArgumentException"/> / <see cref="NotSupportedException"/> を握って UTF-8(65001)へ
    /// silent フォールバックする。呼び出し元(RestoreFromBackup)は try/catch を持たず、これを外で
    /// 伝播させると他タブの復元まで巻き添えに壊れるため。<c>Encoding.GetEncoding(-1)</c> は
    /// <see cref="ArgumentOutOfRangeException"/> を投げるが <see cref="ArgumentException"/> の派生な
    /// のでこの catch でカバーされる。BK-L-5 対応時に <c>Trace</c> は sink 経由へ移す予定。
    /// </summary>
    private static Encoding SafeEncodingOrFallback(int codePage)
    {
        try
        {
            return EncodingCatalog.Get(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "kxEdit: BackupRecord CodePage {0} is not supported; falling back to UTF-8.",
                codePage
            );
            return EncodingCatalog.Get(65001);
        }
    }

    /// <summary>
    /// BK-L-1: BackupRecord.LineEndingId を <see cref="LineEnding"/> に変換する。素の enum キャストは
    /// 定義外の値(例 999 / -1)をそのまま通してしまい、<c>ToEolString()</c> /
    /// <c>ToDisplayString()</c> の <c>_ =&gt; "\r\n"</c> 分岐で silent CRLF 上書きになる。
    /// <see cref="Enum.IsDefined(Type,object)"/> で範囲チェックし、範囲外なら Crlf にフォールバックする。
    /// hot path でないため <c>IsDefined</c> の boxing は許容。BK-L-5 対応時に <c>Trace</c> は sink 経由へ移す予定。
    /// </summary>
    private static LineEnding SafeLineEndingOrFallback(int id)
    {
        if (Enum.IsDefined(typeof(LineEnding), id))
            return (LineEnding)id;
        System.Diagnostics.Trace.TraceWarning(
            "kxEdit: BackupRecord LineEndingId {0} is out of range; falling back to CRLF.",
            id
        );
        return LineEnding.Crlf;
    }
}
