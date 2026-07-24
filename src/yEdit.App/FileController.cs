using System.Text;
using yEdit.Core.Backup;
using yEdit.Core.Buffers;
using yEdit.Core.IO;
using yEdit.Core.Settings;
using yEdit.Core.Text;

namespace yEdit.App;

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
    private int _untitledSeq; // 無題タブの連番（新規作成毎に増加・セッション内で再利用しない）

    // Task 4: 復元経路(RestoreSession)専用の内部 seam。LoadInto の catch 内 _prompt.Error を一時抑止し、
    // 失敗パスを failedPaths に集約する(単発ダイアログを避けまとめて 1 個で通知するため)。
    // 復元経路以外(開く/最近/grep/開き直し)からは触れない=これらは既存の per-file ダイアログを維持する。
    private bool _suppressLoadErrorPrompt;

    // Task 5 review I-2: 復元経路(RestoreSession)で RegisterRecent を抑止するための seam。
    // 復元は「ユーザーが開いた」相当ではないため RecentFiles を汚さない=起動前の順序を保つ。
    private bool _suppressRegisterRecent;

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
        IReachabilityProbe reachabilityProbe
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
    /// </summary>
    public Document? TryOpenOrActivate(string path, bool suppressAutoCsv = false)
    {
        var existing = _docs.FindByPath(path);
        if (existing is not null)
        {
            _docs.Activate(existing);
            // Task 10 review I-2: 復元経路(_suppressRegisterRecent=true)では fast-path でも
            // RegisterRecent を抑止する(重複パスの LastSession 復元で RecentFiles が汚染されるのを防ぐ)。
            if (!_suppressRegisterRecent)
                RegisterRecent(path);
            return existing;
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
            loaded = LoadInto(doc, path, forcedCodePage: null);
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
            return doc;
        }
        _docs.TryClose(doc, _ => true); // 読込失敗→作りかけタブを破棄
        if (prev is not null)
            _docs.Activate(prev); // 直前のアクティブへ戻す
        return null;
    }

    /// <summary>アクティブタブを指定の文字コードで開き直す。Path 未確定なら案内表示して中止。</summary>
    public void ReopenWithEncoding()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;
        if (doc.State.Path is null)
        {
            _prompt.Info("ファイルを開いてから実行してください。", "yEdit");
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
            // 即エラー(60 秒 UI 凍結を回避)。ポリシーは Save 側と共有=TryProbeReachability。
            if (!TryProbeReachability(path))
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
                        "yEdit: restore-replacement-char-detected: {0}",
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

    /// <summary>指定ドキュメントを保存。Path 未確定なら SaveAs にフォールバック。</summary>
    public bool SaveDocument(Document doc) =>
        doc.State.Path is null ? SaveAsDocument(doc) : WriteToPath(doc, doc.State.Path);

    /// <summary>指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。</summary>
    private bool SaveAsDocument(Document doc)
    {
        var picked = _fileDialogs.PickSaveAs(
            _owner,
            new SaveAsRequest(
                doc.State.Path,
                doc.State.Encoding.CodePage,
                doc.State.HasBom,
                doc.State.LineEnding
            )
        );
        if (picked is null)
            return false;
        if (string.IsNullOrWhiteSpace(picked.Path))
        {
            _prompt.Warn("ファイル名を指定してください。", "エラー");
            return false;
        }

        var newEncoding = EncodingCatalog.Get(picked.CodePage);

        // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告して続行/中止を選ばせる。
        // Load 経路の HadReplacementChar 警告と対称。UTF-8(65001) は BMP+astral 全表現可でスキップ。
        if (
            picked.CodePage != 65001
            && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding)
            && !_prompt.OkCancel(
                "選択した文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。続行しますか?",
                "文字コードの警告"
            )
        )
        {
            return false;
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

        if (!WriteToPath(doc, picked.Path))
        {
            doc.State.Encoding = oldEncoding;
            doc.State.LineEnding = oldLineEnding;
            doc.State.HasBom = oldHasBom;
            return false;
        }
        doc.State.Path = picked.Path;
        DocumentManager.UpdateLabel(doc);
        _metaChanged();
        RegisterRecent(picked.Path); // 保存先も最近のファイルへ
        return true;
    }

    /// <summary>指定エンコードでバッファ全文が損失なく符号化できるかを事前判定する(SaveAs のダウングレード警告用)。</summary>
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
    /// 改行を State.LineEnding に正規化してから本文を取得し、原子的に保存する。
    /// 例外は _prompt.Error で通知し false を返す。
    /// </summary>
    /// <remarks>
    /// CSV-M-2(2026-07-20): 冒頭に <see cref="TryProbeReachability"/> を追加し、UNC / マップドネットワーク
    /// ドライブは 5 秒プローブで到達不能なら以下のロールバック導線を発火する前に return false する
    /// (Load 側 HIGH-6 と対称=Save でも 60 秒 UI 凍結を回避)。
    ///
    /// Batch A Task 1(2026-07-15): WriteToPath 失敗時に ConvertEols で書き換わった本文と保存点(Modified)
    /// をロールバックする。ConvertEols(非 fast-path)は <c>ReplaceSource(builder.Build())</c> で新規
    /// TextBuffer に差し替えるため、失敗しても本文の EOL が新値に変わったまま/新規バッファは fresh
    /// (Modified=false)で保存点が破壊されたままになる 2 重の静音喪失導線があった。
    /// 旧 <see cref="TextBuffer"/> 参照を保存前に握っておき、失敗時に <see cref="EditorControl.SetOrReplaceSource"/>
    /// で参照だけを戻せば <see cref="TextBuffer"/> 内部の <c>_savedRoot</c>/<c>_current</c> は
    /// ConvertEols で不変=Modified も本文も一括で復元される(旧バッファの内部状態は
    /// ReplaceSource で置換されただけで書き換わっていない)。
    /// </remarks>
    private bool WriteToPath(Document doc, string path)
    {
        // CSV-M-2: リモートパス(UNC / マップドネットワークドライブ)は 5 秒プローブで到達不能なら
        // 即エラー(HIGH-6 の LoadInto 側と対称)。snapshotBefore を握る前・ConvertEols 副作用を
        // 起こす前に短絡することで、プローブ失敗時に「本文の EOL が書き換わる」「新規バッファに
        // 差し替わる」を発生させない。ポリシーは Load 側と共有=TryProbeReachability。
        if (!TryProbeReachability(path))
            return false;

        // ConvertEols 前のバッファ参照を保持(失敗時ロールバック用=バグ 1+2 対策)。
        // 旧 TextBuffer は不変=このハンドルを保持している限り、内部の _savedRoot/_current は
        // ConvertEols/ReplaceSource で書き換わらない。成功パスでは使わない。
        var snapshotBefore = doc.Editor.CurrentBuffer;
        try
        {
            ApplyEol(doc);
            bool wasReadOnly = doc.Editor.ReadOnly;
            if (wasReadOnly)
                doc.Editor.ReadOnly = false;
            try
            {
                doc.Editor.ConvertEols(doc.Editor.EolMode);
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
        catch (Exception ex)
            when (ex
                    is System.IO.IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException
                        or NotSupportedException
            )
        {
            // バグ 1+2 修正: ConvertEols が非 fast-path で新規 TextBuffer に差し替えている場合は
            // 旧バッファ参照へ戻す(fast-path 済み=同一参照ならキャレット/スクロールリセットを避けて no-op)。
            if (!ReferenceEquals(doc.Editor.CurrentBuffer, snapshotBefore))
            {
                doc.Editor.SetOrReplaceSource(snapshotBefore);
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
        var r = _prompt.YesNoCancel($"{doc.State.DisplayName} の変更を保存しますか？", "yEdit");
        return r switch
        {
            DialogResult.Yes => SaveDocument(doc),
            DialogResult.No => true,
            _ => false,
        };
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
            var status = OriginalPathValidator.Check(rec.OriginalPath!, out var normalized);
            if (status == PathValidation.Ok)
            {
                safePath = normalized;
            }
            else
            {
                useUntitled = true;
                // CSV-L-5: OriginalPath は攻撃者 JSON 由来 (RLO/改行 で拡張子偽装や複数行注入)。
                // path 部分のみ OneLine で無害化し、案内文と "\n\n元パス:" の改行区切りは保持する。
                // 統合復元 Task 5: silent 経路(WithLoadErrorPromptSuppressed 実行中=RestoreSession の
                // extras 等)ではダイアログを出さない。ダイアログ経路(OfferRestoreOnStartup からの
                // 呼出)は非抑止スコープ=挙動不変。
                if (!_suppressLoadErrorPrompt)
                {
                    _prompt.Warn(
                        $"バックアップの元パスが無効なため、無題タブとして復元します。"
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
        yEdit.Core.Session.SessionLayout? layout,
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
                            "yEdit: restore-record-failed: {0}",
                            yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
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
                if (extrasOpened >= yEdit.Core.Session.SessionLayoutStore.MaxTabs)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "yEdit: restore-extras-capped ({0} -> {1})",
                        extras.Count,
                        yEdit.Core.Session.SessionLayoutStore.MaxTabs
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
                        "yEdit: restore-extra-failed: {0}",
                        yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
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
        yEdit.Core.Session.SessionLayoutRecord rec,
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
                "yEdit: restore-path-only-demote: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path ?? bk.Id, 200)
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
                    "yEdit: dirty-backup-missing, demoting to disk reopen: {0}",
                    yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200)
                );
            bool existedBefore = _docs.FindByPath(rec.Path) is not null;
            var opened = TryOpenOrActivate(rec.Path);
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
            if (pathOnlyBk is not null && !existedBefore)
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
                "yEdit: untitled-backup-missing, skipping record (untitled-{0})",
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
        yEdit.Core.Session.SessionLayoutRecord rec,
        BackupRecord bk
    )
    {
        var doc = _docs.CreateNew();
        var status = OriginalPathValidator.Check(rec.Path!, out var normalized);
        if (status == PathValidation.Ok)
        {
            doc.State.Path = normalized;
            doc.State.UntitledNumber = 0;
        }
        else
        {
            doc.State.Path = null;
            doc.State.UntitledNumber = ++_untitledSeq;
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: restore-invalid-path-fallback-to-untitled: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200)
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
        yEdit.Core.Session.SessionLayoutRecord rec,
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
    private Document RestoreUntitledFrame(yEdit.Core.Session.SessionLayoutRecord rec)
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
                    "yEdit: extra-path-only-untitled-skipped: {0}",
                    yEdit.Core.Text.SanitizeForDisplay.OneLine(bk.Id, 200)
                );
                return null;
            }
            if (
                OriginalPathValidator.Check(bk.OriginalPath, out var normalized)
                != PathValidation.Ok
            )
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: extra-path-only-invalid-path-skipped: {0}",
                    yEdit.Core.Text.SanitizeForDisplay.OneLine(bk.OriginalPath, 200)
                );
                return null;
            }
            bool existedBefore = _docs.FindByPath(normalized) is not null;
            var opened = TryOpenOrActivate(normalized);
            if (opened is null)
            {
                failedPaths.Add(bk.OriginalPath);
                // open 失敗時はレコード残置を受容(希少の二乗・30 日 sweep が回収)。
                return null;
            }
            // 最終品質パス I-1: path-only 消費レコードは adopt→clean 検出→次 tick 削除で
            // 残置させない(レイアウト外 Id は次回以降も毎起動 extras としてゾンビ復活するため)。
            // fast-path activate(既存タブ)には adopt しない=Id 上書きで既存 adopt を壊さない。
            if (!existedBefore)
                adoptRestored?.Invoke(opened, bk);
            return opened;
        }
        var doc = RestoreFromBackup(bk); // 既存経路(HIGH-2 検証・dirty 復元・無題連番)
        adoptRestored?.Invoke(doc, bk);
        return doc;
    }

    /// <summary>
    /// action の実行中は復元経路専用の抑止フラグをまとめて ON にする:
    /// (a) LoadInto/TryProbeReachability の catch 内 _prompt.Error を抑止(失敗パスを集約通知するため)
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
    /// HIGH-6 + CSV-M-1/M-2: リモートパス(UNC / マップドネットワークドライブ)は 5 秒プローブで
    /// 到達不能なら即 <c>_prompt.Error</c> を出して false を返す。ローカルは true を返して通常経路へ。
    /// LoadInto / WriteToPath 双方から共有し「Save と Load で同じ到達性ポリシー」を 1 箇所で表現する。
    /// 判定は <see cref="RemotePathDetector.IsRemote(string)"/>(UNC + DriveInfo.DriveType==Network)。
    /// ローカル固定/リムーバブルは判定を skip(挙動不変)、リモート正常経路は reachable=true で通過(挙動不変)。
    /// </summary>
    private bool TryProbeReachability(string path)
    {
        if (
            RemotePathDetector.IsRemote(path)
            && !_reachabilityProbe.ProbeWithTimeout(path, TimeSpan.FromSeconds(5))
        )
        {
            // CSV-L-5: path は grep/最近のファイル/BackupRecord 由来で攻撃者制御が届き得るため、
            // SanitizeForDisplay.OneLine で RLO/改行/長大 path を無害化してから prompt に載せる。
            // Task 4: 復元経路(WithLoadErrorPromptSuppressed 実行中)は per-file ダイアログを抑止し、
            // 呼出元で失敗パスを集約通知させる(戻り値 false は伝播)。
            if (!_suppressLoadErrorPrompt)
            {
                _prompt.Error(
                    $"ネットワークパスに到達できません: {SanitizeForDisplay.OneLine(path, 200)}",
                    "エラー"
                );
            }
            return false;
        }
        return true;
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
                "yEdit: BackupRecord CodePage {0} is not supported; falling back to UTF-8.",
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
            "yEdit: BackupRecord LineEndingId {0} is out of range; falling back to CRLF.",
            id
        );
        return LineEnding.Crlf;
    }
}
