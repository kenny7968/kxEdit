# B5「実際と違うことを言わない」実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** kxEdit が「起きていないことを起きたと言う」「起きたことを何も言わない」3 箇所
(M-22 / M-20 / M-8)を潰し、B4 の申し送り(`Unreadable` のまま終了すると案内した当のファイルが
上書きされる)を回収する。

**Architecture:** 発声・通知の**判断**を純関数かテスト可能な internal seam へ出し、UI 側は
薄い配線に留める(`SettingsStartup.Prepare` が `Program.Main` に対して採ったのと同じ形)。
M-20 は「復旧」を判定するために `IBackupWriter` へ成功の観測面を追加し、
`BackupCoordinator` 側で遷移だけを発声する状態機械にする。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit / CSharpier / Husky.Net

**設計書:** `docs/plans/2026-09-02-truthful-notifications-design.md`(承認済み)

---

## 読む前に

### この計画のコードは「検証すべき案」であって正解ではない

行番号・シグニチャ・周辺コードは 2026-09-02 の main `0faa480` で実際に読んで書いているが、
**分岐構造・文言・テストの期待値は仮説**である。実装時に実コードと食い違ったら、
計画ではなく実コードを正とし、**食い違った事実を実施記録へ書くこと**。

### 網を足す前に必ず「変更前の src で赤になる」ことを確認する

本計画のテストはすべて「修正前は赤・修正後は緑」で弁別できるよう組んである。
先にテストを書き、**変更前の src に対して走らせて赤を見てから**実装する。
緑のまま通ったら、その網は欠陥を捕まえていない(PR #35 で確立した手順)。

### ミューテーション検証は行わない

CLAUDE.md §4-A の**禁止**領域(GUI・イベントハンドリング・ファイル入出力)に該当する。
設計書 §7 の判断。

### 共通の検証コマンド

```
dotnet build kxEdit.sln -c Release --no-incremental -warnaserror
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release
dotnet csharpier check <変更したファイル>
```

`-warnaserror` が既定で効いているため **0 warning を維持**すること(CLAUDE.md §6)。
pre-commit フック(CSharpier 整形 + ローカルパス検出)を `--no-verify` で飛ばさない。

---

## Task 1: M-8 — 窓外 Raise が保留 trailing を取り消さない

**Files:**
- Modify: `src/kxEdit.App/Speech/UiaAnnouncer.cs:85-89`
- Test: `tests/kxEdit.App.Tests/AnnouncerTests.cs`(`:170` の直下へ追加)

### Step 1: 失敗するテストを書く

`AnnouncerTests.Say_Empty_CancelsPendingTrailingMessage`(`:170`)の直後に置く。
既存の `RecordingAnnouncer`(`:18`)と `FakeTimeProvider` をそのまま使う。

```csharp
    /// <summary>M-8: 窓外の即時 Raise は「今このメッセージが最新である」という宣言なので、
    /// armed 済みの trailing(それより古い pending)を取り消さなければならない。
    /// 取り消さないと、直後に trailing が発火して<b>1 つ前のメッセージが最後に読まれる</b>
    /// (CSV で → を押しっぱなしにしたとき、着地セルの 1 つ手前が最後に発声される)。
    /// 空文字列分岐(<c>UiaAnnouncer.cs:53-65</c>)が既に同じ危険をコメントで名指しして
    /// pending を潰しており、本テストはその対称化を固定する。</summary>
    [Fact]
    public void Say_OutsideWindow_CancelsPendingTrailingMessage() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("a"); // T=0: 窓外 → 即 Raise。_lastSaidUtc=0
            clock.Advance(TimeSpan.FromMilliseconds(20));
            announcer.Say("b"); // T=20: 窓内 → pending="b"、trailing を T=70 へ armed
            clock.Advance(TimeSpan.FromMilliseconds(40));
            announcer.Say("c"); // T=60: 窓外(60-0=60 ≧ 50)→ 即 Raise。pending は潰されるべき
            // T=70 の trailing 発火時刻を大きく越えて進める。潰せていれば何も起きない。
            clock.Advance(TimeSpan.FromMilliseconds(100));
            // 修正前は ["a", "c", "b"] になる = 最後に読まれるのが 1 つ前の "b"。
            Assert.Equal(new[] { "a", "c" }, announcer.Spoken);
        });
```

### Step 2: 変更前の src で走らせて赤を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release --filter "FullyQualifiedName~Say_OutsideWindow_CancelsPendingTrailingMessage"
```

期待: **失敗**し、`["a", "c", "b"]` が実際に得られること。
**得られた実際の系列を実施記録へ書くこと** —— 系列が違うなら §3.1 の再現手順が間違っている。

### Step 3: 最小の実装

`UiaAnnouncer.cs:85-89` を差し替える。lock 内で pending / timer を潰し、Raise は lock 外のまま。

```csharp
            // 窓外: 即 Raise。lock 内で timestamp を更新し、Raise 自体は lock 外で行う
            // (RaiseAutomationNotification の I/O を lock で長時間握らないため)。
            //
            // M-8: armed 済みの trailing も同時に潰す。窓外の即時 Raise は「今このメッセージが
            // 最新である」という宣言であり、それより古い pending を後から鳴らす理由が無い。
            // 潰さないと T=0 Say("a") / T=20 Say("b")(pending) / T=60 Say("c")(窓外・即 Raise)
            // の後、T=70 の trailing が "b" を Raise して<b>1 つ前が最後に読まれる</b>。
            // 空文字列分岐(:53-65)が同じ危険を名指しして既に潰しており、ここはその対称化。
            _pendingMessage = null;
            _trailingTimer?.Dispose();
            _trailingTimer = null;
            _lastSaidUtc = now;
        }
        Raise(message);
```

### Step 4: 緑を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release --filter "FullyQualifiedName~AnnouncerTests"
```

期待: `AnnouncerTests` 全件 PASS(**既存 10 数本の退行が無いこと**が本当の関門。
とくに `Say_ThrottlesRaise_When50msWithinPrevious` / `Say_TrailingMessage_IsLastReceived_NotFirstThrottled` /
`Say_ThirdCall_AfterTrailingFires_RaisesImmediately`)。

### Step 5: `_lastSaidUtc` 更新の網が残っていることを確かめる

設計書 §3.3 の宿題。pending を潰す 3 行を足したついでに `_lastSaidUtc = now;` を消しても
Task 1 の新テストは緑のままである(T=70 に何も起きないため)。**手で一時的に消して**、
既存の `Say_ThrottlesRaise_When50msWithinPrevious`(`:106`)か
`Say_ThirdCall_AfterTrailingFires_RaisesImmediately`(`:265`)が赤くなることを確認し、
**赤くなった test 名を実施記録へ書く**。どちらも緑なら、その面には網が無いので 1 本足す。
確認後、消した行は必ず戻すこと。

### Step 6: commit

```bash
git add src/kxEdit.App/Speech/UiaAnnouncer.cs tests/kxEdit.App.Tests/AnnouncerTests.cs
git commit -m "fix(app): 窓外の即時 Raise が保留 trailing を取り消すようにする (M-8)"
```

### Step 7: 仕様レビュー

別エージェントで「実装とテストが設計書 §3 のとおりか」を確認する(CLAUDE.md §3-4)。
指摘は 3 択(fixup / 受容を記載 / 理由付き却下)で明示する。

---

## Task 2: M-22 — 設定保存失敗でも成功発声

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`:1058-1066` の `SaveSettingsSafe` / `:1113-1119` の `OpenSettings` / 通知用フィールド)
- Test: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

### 設計の要点(先に読む)

1. **「設定を適用できませんでした」は逆向きの嘘。** ダイアログ OK の時点で外観適用と
   `_backup.UpdateSettings` は済んでおり、走っているアプリには効いている。落ちたのは永続化だけ。
2. **`AtomicReplaceFailedException` は実 I/O では作れない。** `AtomicFile.Write` は
   「差替に失敗し**かつ原本が失われ**かつ復旧リネームも失敗」でしか投げない
   (`AtomicFile.cs:44-56` のクラス xmldoc)。したがって**その分岐は純関数を直接叩いて検証する**。
   通常の失敗(読み取り専用ファイル)は実 I/O で作れるので、そちらで配線を検証する。
3. **握り潰すラッパ `SaveSettingsSafe()` は残す。** 呼出は 3 箇所あり、
   `FileController._saveSettings`(`MainForm.cs:207` → `FileController.cs:1575`)は
   `Action` 型で戻り値を持てない。

### Step 1: 純関数の失敗テストを書く

`MainFormSmokeTests.cs` の末尾へ追加。まず**判断だけ**を固定する。

```csharp
    // ===== M-22 (B5): 設定保存の失敗を実態どおりに伝える =====

    /// <summary>M-22: 保存が成功したときだけ現行の成功文言を出す。</summary>
    [Fact]
    public void SettingsSaveOutcome_reports_plain_success_when_nothing_failed()
    {
        var (speech, dialog) = MainForm.SettingsSaveOutcomeForTest(null);
        Assert.Equal("設定を適用しました", speech);
        Assert.Null(dialog);
    }

    /// <summary>M-22: 通常の保存失敗では、<b>適用は済んでいる</b>ことと
    /// <b>永続化だけが落ちた</b>ことの両方を言う。「適用できませんでした」は逆向きの嘘になる。
    /// 案内すべきパスが無いのでダイアログは出さない。</summary>
    [Fact]
    public void SettingsSaveOutcome_says_applied_but_not_saved_for_an_ordinary_failure()
    {
        var (speech, dialog) = MainForm.SettingsSaveOutcomeForTest(
            new UnauthorizedAccessException("denied")
        );
        Assert.Contains("適用しました", speech);
        Assert.Contains("保存できませんでした", speech);
        Assert.Null(dialog);
    }

    /// <summary>M-22: 差替失敗で tmp が<b>実在する</b>ときは、その場所と後始末をダイアログで案内する。
    /// 発声(1 行のステータスラベル)には長いパスを載せられないための二段構え
    /// (<c>FileController.cs:956-976</c> が M-12 で採った形と同型)。</summary>
    [Fact]
    public void SettingsSaveOutcome_points_at_the_preserved_temp_when_it_exists()
    {
        using var tmp = new TempDir();
        string preserved = Path.Combine(tmp.Path, "settings.json.abc.tmp");
        File.WriteAllText(preserved, "{}");
        var (speech, dialog) = MainForm.SettingsSaveOutcomeForTest(
            new AtomicReplaceFailedException(
                Path.Combine(tmp.Path, "settings.json"),
                preserved,
                new IOException("replace"),
                new IOException("recover")
            )
        );
        Assert.Contains("適用しました", speech);
        Assert.NotNull(dialog);
        Assert.Contains(preserved, dialog);
        Assert.Contains("削除", dialog);
    }

    /// <summary>M-22: tmp まで失われていたら、<b>実在しない退避先を案内しない</b>。
    /// 弁別は <c>File.Exists</c> 一本(例外の型で分けると原理的に漏れる。監査 §9 V-7)。</summary>
    [Fact]
    public void SettingsSaveOutcome_does_not_point_at_a_temp_that_is_gone()
    {
        using var tmp = new TempDir();
        string missing = Path.Combine(tmp.Path, "never-created.tmp");
        var (_, dialog) = MainForm.SettingsSaveOutcomeForTest(
            new AtomicReplaceFailedException(
                Path.Combine(tmp.Path, "settings.json"),
                missing,
                new IOException("replace"),
                new IOException("recover")
            )
        );
        Assert.NotNull(dialog);
        Assert.DoesNotContain(missing, dialog);
    }
```

> `TempDir` の API(`tmp.Path` か別名か)は `tests/kxEdit.App.Tests/TempDir.cs` を読んで合わせること。
> `using` の要否(`System.IO` / `kxEdit.Core.IO`)は `GlobalUsings.cs` を確認する。

### Step 2: 配線の失敗テストを書く

純関数だけでは「`OpenSettings` が実際にそれを使っているか」が観測できない
(`Program.cs:71-76` が同じ罠を名指ししている)。**実 MainForm を作り、読み取り専用の
`settings.json` に対して適用経路を叩いて、発声ラベルを読む。**

```csharp
    /// <summary>M-22 の配線: 設定の適用経路が、保存の成否を見て発声を選んでいること。
    /// 純関数だけを固定すると「<c>OpenSettings</c> が実際にそれを呼んでいるか」が観測できず、
    /// 配線が黙って切れても緑のままになる(<c>Program.cs:71-76</c> が名指ししている罠)。
    /// <c>SettingsDialog</c> はモーダルでテストから開けないため、ダイアログを閉じた後の
    /// 処理を <c>ApplySettingsForTest</c> として切り出し、そこを実経路として叩く。</summary>
    [Fact]
    public void Applying_settings_announces_the_failure_when_the_file_cannot_be_written() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = new MainForm(
                new AppSettings(),
                tmp.SettingsPath,
                tmp.BackupDir,
                tmp.LayoutPath
            );
            File.WriteAllText(tmp.SettingsPath, "{}");
            File.SetAttributes(tmp.SettingsPath, FileAttributes.ReadOnly);
            try
            {
                form.ApplySettingsForTest(new AppSettings());
                Assert.Contains("保存できませんでした", form.LastAnnouncementForTest);
            }
            finally
            {
                File.SetAttributes(tmp.SettingsPath, FileAttributes.Normal);
            }
        });

    /// <summary>M-22: 保存できたときは現行の成功文言のまま(退行の証人)。</summary>
    [Fact]
    public void Applying_settings_announces_plain_success_when_the_file_is_writable() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = new MainForm(
                new AppSettings(),
                tmp.SettingsPath,
                tmp.BackupDir,
                tmp.LayoutPath
            );
            form.ApplySettingsForTest(new AppSettings());
            Assert.Equal("設定を適用しました", form.LastAnnouncementForTest);
        });
```

> `TempDir` が `SettingsPath` / `BackupDir` / `LayoutPath` を持つかは
> `MainFormSmokeTests.cs:1149` 付近(`Program.CreateMainForm(tmp.SettingsPath, tmp.BackupDir, tmp.LayoutPath)`)で
> 使われている実物を確認して合わせること。

### Step 3: 走らせて赤を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsSaveOutcome|FullyQualifiedName~Applying_settings"
```

期待: **コンパイルエラー**(`SettingsSaveOutcomeForTest` / `ApplySettingsForTest` が未定義)。
実装後に `Applying_settings_announces_the_failure...` が「読み取り専用でも成功文言が出る」で
落ちる状態を一度作れるとなお良いが、必須ではない。

**読み取り専用属性で `SettingsStore.Save` が実際に投げるかを先に確かめること。**
`AtomicFile.Write` は tmp を作って `File.Replace` する経路なので、読み取り専用の**差替先**で
落ちるはずだが、**未実測**である。落ちないなら別の失敗注入(ディレクトリを読み取り専用にする /
`settings.json` と同名のディレクトリを置く)へ替え、替えた理由を実施記録へ書く。

### Step 4: 実装

**(a) `SaveSettingsSafe` を割る**(`MainForm.cs:1058-1066` を差し替え):

```csharp
    /// <summary>設定を永続化し、失敗した例外を返す(成功なら null)。
    /// M-22(B5): 握り潰しをここで止め、<b>伝えるかどうかは呼び出し側に決めさせる</b>。
    /// 3 つの呼出のうち発声を伴うのは <see cref="ApplySettings"/> だけで、
    /// 残る 2 つ(<c>OnFormClosing</c> / <c>FileController</c> の最近ファイル更新)は
    /// 設計 §8 の判断により現行どおり握る。</summary>
    private Exception? TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settingsPath, _settings);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>設定を永続化する(保存失敗は致命でないため握る)。
    /// <see cref="FileController"/> へ <c>Action</c> として渡るため戻り値を持てない経路と、
    /// 終了時の保存が使う。失敗を<b>伝える</b>のは <see cref="ApplySettings"/> だけ。</summary>
    private void SaveSettingsSafe() => _ = TrySaveSettings();
```

**(b) `OpenSettings` を割る**(`MainForm.cs:1113-1119`):

```csharp
    /// <summary>設定ダイアログを開き、OK なら <see cref="ApplySettings"/> へ渡す。
    /// <b>ダイアログを開くこと以外はここに置かない</b> —— <c>ShowDialog</c> はモーダルで
    /// 自動テストから叩けないため、判断をここへ残すと配線が黙って切れても緑のままになる
    /// (<c>Program.CreateMainForm</c> を <c>Main</c> から切り出したのと同じ理由)。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        ApplySettings(dlg.Result); // Result は取得のたびに組み立てるため一度だけ読む
    }

    /// <summary>設定ダイアログ OK 後の反映本体。全タブへ外観適用＋バックアップ設定の即時反映＋
    /// 永続化を行い、<b>永続化の成否を見て</b>発声を選ぶ(M-22)。
    /// 発声時点で外観適用と <c>UpdateSettings</c> は済んでいる = 走っているアプリには効いている。
    /// したがって失敗時も「適用しました」は残し、落ちた永続化の方を足す。</summary>
    private void ApplySettings(AppSettings result)
    {
        _settings = result;
        foreach (var doc in _docs.Documents)
            EditorAppearance.Apply(doc.Editor, _settings);
        _backup.UpdateSettings(
            _settings.BackupEnabled,
            _settings.BackupIntervalSeconds,
            _settings.RestoreOpenFilesOnStartup
        );
        var (speech, dialogBody) = SettingsSaveOutcome(TrySaveSettings());
        _announcer.Say(speech);
        if (dialogBody is not null)
            ShowSettingsSaveFailedDialog(dialogBody);
    }

    internal void ApplySettingsForTest(AppSettings result) => ApplySettings(result);
```

**(c) 判断の純関数**(`SettingsSaveOutcome`)。`FileController.cs:956-976` の語順の教訓
(SR は線形に読むので、長いパスより前に「何が起きたか」と「次に何をすればよいか」を置く)を
そのまま適用する:

```csharp
    /// <summary>M-22: 設定保存の結果から「発声する 1 行」と「出すならダイアログ本文」を決める。
    /// <para>
    /// <b>失敗時も「適用しました」を残す。</b> 呼出時点で外観適用と <c>UpdateSettings</c> は
    /// 済んでおり、走っているアプリには設定が効いている。「適用できませんでした」は
    /// <b>逆向きの嘘</b>になる。欠けているのは「次回起動時には元に戻る」という帰結の方。
    /// </para>
    /// <para>
    /// ダイアログを出すのは <see cref="AtomicReplaceFailedException"/> のときだけ。通常の失敗は
    /// tmp が残らず案内すべきパスが無いので発声で完結する。tmp が残る場合は
    /// <b>%APPDATA%\kxEdit\ 直下に恒久残留し、中身は最近使ったファイルの一覧(パス)を含む</b>
    /// (B4 が実測。<c>SettingsStore.Save</c> の xmldoc)ため、場所と後始末を届ける必要がある。
    /// 1 行のステータスラベルに長いパスは載らないので二段にする。
    /// </para>
    /// <para>
    /// 実在確認は <c>File.Exists</c> 一本。復旧リネームが「tmp まで失われていた」で落ちた場合も
    /// 同じ例外型になるため、例外の型で分けると原理的に漏れる(監査 §9 V-7)。
    /// </para></summary>
    private static (string Speech, string? DialogBody) SettingsSaveOutcome(Exception? error)
    {
        if (error is null)
            return ("設定を適用しました", null);

        const string Speech =
            "設定を適用しましたが、保存できませんでした。次回起動時は元の設定に戻ります";

        if (error is not AtomicReplaceFailedException replaceFailed)
            return (Speech, null);

        // 原本パスは丸めてよい(80)。ユーザーが今まさに保存しようとした先で既知であり、
        // 退避先のフォルダーは tmp パス側に完全な形で載る。tmp パスは kxEdit がその場で
        // 作った乱数入りで他所から知る手段が無いので<b>切り詰めない</b>
        // (FileController.cs:938-949 が確立した非対称)。無害化(OneLine)は外さない。
        string target = SanitizeForDisplay.OneLine(replaceFailed.TargetPath, 80);
        string body = System.IO.File.Exists(replaceFailed.PreservedTempPath)
            ? $"設定を保存できませんでした: 保存先 '{target}' が失われました。"
                + "設定は今の kxEdit には適用されていますが、次回起動時は元に戻ります。"
                + "必要な項目は設定し直してください。"
                + $"\n\n書き込んだ内容は次の場所に残してあります。不要になったら削除してください:\n  "
                + SanitizeForDisplay.OneLine(replaceFailed.PreservedTempPath)
            : $"設定を保存できませんでした: 保存先 '{target}' が失われ、書き込んだ内容も残せませんでした。"
                + "設定は今の kxEdit には適用されていますが、次回起動時は元に戻ります。"
                + "必要な項目は設定し直してください。";
        return (Speech, body);
    }

    internal static (string Speech, string? DialogBody) SettingsSaveOutcomeForTest(
        Exception? error
    ) => SettingsSaveOutcome(error);
```

**(d) ダイアログ**。`ShowSettingsStartupWarning`(`MainForm.cs:486`)の直下へ、同じ形で置く:

```csharp
    /// <summary>M-22(B5): 設定保存の差替失敗を伝える。<b>文言は組み立て済みで渡ってくる</b>
    /// (<see cref="SettingsSaveOutcome"/>)—— パスの無害化も「切り詰めない」判断もそちらの担当。
    /// <b>本文をログ・クリップボード・例外へ流さないこと</b>(%APPDATA% 配下のパスを含む。
    /// B4 Task 8 の申し送り 2 と同じ制約)。</summary>
    private void ShowSettingsSaveFailedDialog(string body) =>
        MessageBox.Show(
            this,
            body,
            "設定を保存できませんでした",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
```

> **注意**: `SettingsDialog` のモーダル直後なのでここで MessageBox を出しても編集は止まらない。
> ただし **Task 2 のテストは `ApplySettingsForTest` を通常失敗経路でしか叩かない**(ダイアログ
> 分岐に入らない)ので、テストが MessageBox でブロックすることはない。もし将来
> `AtomicReplaceFailedException` を配線経路で叩く網を足すなら、`_suppressRestoreDialogsForTest`
> と同型の抑止フラグを先に用意すること。

### Step 5: 緑を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release
```

期待: 全件 PASS。**`OpenSettings` を割ったことで既存のスモークテストが落ちていないこと**が関門。

### Step 6: commit

```bash
git add src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "fix(app): 設定保存の失敗を実態どおりに伝える (M-22)"
```

### Step 7: 仕様レビュー

---

## Task 3: M-20 (1/2) — 書込成功の観測面を足す

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IBackupWriter.cs:15-18`
- Modify: `src/kxEdit.App/SerialBackupWriter.cs:43-54`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs:60-68`
- Test: `tests/kxEdit.App.Tests/SerialBackupWriterTests.cs`

> **このタスクは CLAUDE.md §3-4 の前倒し「コード品質レビュー」対象。**
> `OnWriteSucceeded` は後続タスク(Task 4)が乗る新しい seam であるため、
> Task 3 の完了時点で仕様レビューとは別にコード品質レビューを行う。

### なぜ成功の観測面が要るか(設計書 §5.2)

失敗が来なくなったことだけでは復旧を判定できない。失敗が来ない理由には
「書込が成功した」だけでなく「そもそも書込を投入していない」(dirty でない・署名一致で
`BackupAction.None`)が含まれる。後者を復旧と読むと、**バックアップが一度も書けていないのに
「再開しました」と言う** —— B5 が潰そうとしている虚偽発声を B5 自身が新設することになる。

### Step 1: 失敗するテストを書く

`SerialBackupWriterTests.cs` の既存の失敗通知テストの隣に置く
(既存テスト名を先に読んで、命名と待ち合わせの作法を合わせること —— `SerialBackupWriter` は
背景スレッドなので、既存テストが `WaitForPendingJobs` かイベントで待っているはず)。

```csharp
    /// <summary>M-20(B5): 書込が成功したら Id を通知する。
    /// <c>OnWriteFailed</c> の対であり、Coordinator が「復旧した」を判定する唯一の観測面。
    /// これが無いと「失敗が来ない」を復旧と読むしかなく、<b>書込を一度も投入していない</b>
    /// 場合と区別できない(= 虚偽の復旧発声になる)。</summary>
    [Fact]
    public void Write_reports_the_id_when_the_write_succeeds()
    {
        using var tmp = new TempDir();
        using var writer = new SerialBackupWriter(tmp.Path);
        var succeeded = new List<string>();
        writer.OnWriteSucceeded = id => succeeded.Add(id);
        writer.Write(SampleRecord("id-1"));
        Assert.True(writer.WaitForPendingJobs(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "id-1" }, succeeded);
    }

    /// <summary>M-20: 失敗したときは成功を通知しない(両方鳴ると遷移判定が壊れる)。</summary>
    [Fact]
    public void Write_does_not_report_success_when_the_write_fails()
    {
        using var tmp = new TempDir();
        // 書込先を「同名のディレクトリ」で塞ぐ等、BackupStore.Write が確実に投げる状態を作る。
        // 既存テストに失敗注入の前例があればそれに倣うこと。
        using var writer = new SerialBackupWriter(<書けない dir>);
        var succeeded = new List<string>();
        var failed = new List<string>();
        writer.OnWriteSucceeded = id => succeeded.Add(id);
        writer.OnWriteFailed = id => failed.Add(id);
        writer.Write(SampleRecord("id-1"));
        Assert.True(writer.WaitForPendingJobs(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "id-1" }, failed);
        Assert.Empty(succeeded);
    }
```

> `SampleRecord` / `TempDir` の既存ヘルパー名は `SerialBackupWriterTests.cs` を読んで合わせること。
> 失敗注入の作り方も既存テストに前例があるはずなので、**新しい作り方を発明する前に探す**。

### Step 2: 走らせて赤を確認する

期待: **コンパイルエラー**(`OnWriteSucceeded` が未定義)。

### Step 3: 実装

**(a) `IBackupWriter.cs`** — `OnWriteFailed` の直下へ:

```csharp
    /// <summary>M-20(B5): 書込<b>成功</b>を UI スレッド側に通知するフック(<see cref="OnWriteFailed"/> の対)。
    /// Coordinator が「バックアップが復旧した」を判定する唯一の観測面である ——
    /// 失敗が来なくなったことだけでは、書込が成功したのか<b>そもそも投入していないのか</b>
    /// (dirty でない・署名一致)を区別できず、後者を復旧と読むと虚偽の発声になる。
    /// null なら何もしない(= 本フックを配線しない実装・テストの挙動は不変)。</summary>
    Action<string>? OnWriteSucceeded { get; set; }
```

**(b) `SerialBackupWriter.cs`** — プロパティを足し、`Write` の try 末尾で発火:

```csharp
    /// <inheritdoc/>
    public Action<string>? OnWriteSucceeded { get; set; }
```

```csharp
    public void Write(BackupRecord record) =>
        Enqueue(() =>
        {
            try
            {
                BackupStore.Write(_dir, record);
                // M-20: 成功も通知する。Invoke がここ(try の中)にあると、フック自身が投げた
                // 場合に下の catch が拾って「書込が失敗した」と誤って報告してしまう。
                // したがって<b>この行の位置と、フック側が投げない契約</b>が対になる。
                // Run() の外側 catch が背景スレッドを守るので、投げてもプロセスは落ちない。
            }
            catch
            {
                OnWriteFailed?.Invoke(record.Id);
                return;
            }
            OnWriteSucceeded?.Invoke(record.Id);
        });
```

> **実装時に判断すること**: 上は `try` を抜けてから Invoke する形にしてある(成功通知が
> 失敗通知に化けないため)。`return` を足すと既存の制御フローが変わるので、
> **既存の `SerialBackupWriterTests` が全緑のままであること**を必ず確認する。

**(c) `FakeBackupWriter.cs`** — 実物の挙動を写す:

```csharp
    public Action<string>? OnWriteSucceeded { get; set; }

    public void Write(BackupRecord record)
    {
        Writes.Add(record);
        Store[record.Id] = record;
        // M-20: 実物と同じく成功を通知する。Fake は同期実行なので、Coordinator から見ると
        // 「Reconcile の中で投入した書込の成功が、その Reconcile 内で届く」= 実物より早い。
        // 遷移判定は次の Reconcile 冒頭の drain で読むので、この差は結論を変えない。
        OnWriteSucceeded?.Invoke(record.Id);
    }
```

### Step 4: 緑を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release
```

期待: 全件 PASS。**`FakeBackupWriter.Write` が成功通知を出すようになったことで
既存の `BackupCoordinatorTests` が落ちないこと**が関門(現時点では誰も購読していないので
落ちないはずだが、落ちたらその事実が Task 4 の設計に効く)。

### Step 5: commit

```bash
git add src/kxEdit.App/Abstractions/IBackupWriter.cs src/kxEdit.App/SerialBackupWriter.cs tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs tests/kxEdit.App.Tests/SerialBackupWriterTests.cs
git commit -m "feat(app): バックアップ書込の成功を通知する seam を足す (M-20 準備)"
```

### Step 6: 仕様レビュー + **コード品質レビュー**(別々のエージェントで)

---

## Task 4: M-20 (2/2) — 遷移だけを発声する

**Files:**
- Modify: `src/kxEdit.App/BackupCoordinator.cs`(`:73` 付近のフィールド / `:164-166` の配線 / `:498-503` の drain)
- Modify: `src/kxEdit.App/MainForm.cs:229-242` の直後(フックの配線)
- Test: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs`

### 設計の要点

- **キューは足さない。** 成功は id を要さないので、`_layoutWriteFailed`(`:166`)と同じ
  `Interlocked.Exchange` の 0/1 フラグで受ける。設計書 §11 の第 1 申し送り
  (`_succeeded` の無制限成長をどうするか)は**この選択で解消する** —— 決めた根拠として
  実施記録へ書くこと。
- **同一 pass に失敗と成功が両方あれば失敗が勝つ。** 1 文書だけ書けている状態を
  「復旧」と呼ばないため。
- **`WaitForFinalFlush`(`:772`)の `_failed.IsEmpty` には触らない。** あそこが意図的に
  dequeue しない理由は本変更と独立で、drain の位置も既存のまま 1 箇所に閉じる。

### Step 1: 失敗するテストを書く

`BackupCoordinatorTests.cs` へ追加。既存テストの Coordinator 組み立てヘルパーを流用すること。

```csharp
    // ===== M-20 (B5): バックアップ書込の健全性を遷移で 1 回だけ伝える =====

    /// <summary>M-20: 最初の書込失敗で 1 回だけ「失敗」を報告する。
    /// 現状はこの経路が存在せず、ユーザーはバックアップに守られていると信じたまま編集を続ける。</summary>
    [Fact]
    public void Reports_unhealthy_once_when_a_background_write_fails() { /* 下記の骨子 */ }

    /// <summary>M-20: 失敗が続く間は鳴り続けない(遷移でのみ報告する)。</summary>
    [Fact]
    public void Does_not_repeat_the_unhealthy_report_while_writes_keep_failing() { }

    /// <summary>M-20: 書込が実際に成功したら 1 回だけ「復旧」を報告する。</summary>
    [Fact]
    public void Reports_healthy_again_after_a_write_succeeds() { }

    /// <summary>M-20 の中核: <b>書込を投入していない</b>だけの tick を復旧と読まない。
    /// dirty でない(= BackupPlanner が None を返す)状態で tick を回しても、
    /// 失敗は届かないが成功も届かない。ここで「再開しました」と言うのは虚偽発声であり、
    /// B5 が潰そうとしている欠陥を B5 自身が新設することになる。</summary>
    [Fact]
    public void Does_not_report_recovery_when_no_write_was_attempted() { }

    /// <summary>M-20: 同一 drain に失敗と成功が両方あれば失敗が勝つ
    /// (1 文書だけ書けている状態を「復旧」と呼ばない)。</summary>
    [Fact]
    public void Failure_wins_over_success_within_the_same_drain() { }
```

各テストの骨子:

1. `FakeBackupWriter` + Coordinator を作り、`coordinator.OnBackupHealthChanged = h => reports.Add(h);`
2. dirty 文書を用意して `Reconcile` を 1 回回す(書込が投入される)
3. 失敗を注入するときは既存作法どおり `writer.OnWriteFailed!.Invoke(<書いた record の Id>)` を直接叩く
4. 再度 `Reconcile` を回して drain させる
5. `reports` の系列を assert する(`Assert.Equal(new[] { false }, reports)` など)

> **`Does_not_report_recovery_when_no_write_was_attempted` は本タスクで最も重要な網。**
> ここが緑にならない実装は M-20 を直す代わりに新しい虚偽発声を作っている。
> 具体的には「失敗を注入 → clean 化(または署名一致)させて Reconcile を 2 回回す」で、
> `reports` が `[false]` のままであることを固定する。

### Step 2: 走らせて赤を確認する

期待: **コンパイルエラー**(`OnBackupHealthChanged` が未定義)。

### Step 3: 実装

**(a) フィールド**(`BackupCoordinator.cs:73` の `_failed` 宣言の直下):

```csharp
    // M-20(B5): 背景書込が成功したか(0/1)。id は要らないので _layoutWriteFailed と同じ
    // Interlocked フラグで受ける = キューを作らない(無制限成長の心配が構造的に消える)。
    private int _writeSucceeded;

    // M-20: バックアップ書込が健全か。遷移したときだけ OnBackupHealthChanged を撃つ。
    // 初期 true = 最初の失敗でも報告される。
    private bool _backupHealthy = true;

    /// <summary>M-20(B5): バックアップ書込の健全性が<b>遷移した</b>ときだけ呼ばれる
    /// (true=復旧 / false=失敗)。UI スレッドから呼ばれる(<see cref="Reconcile"/> の呼出元は
    /// <c>Timer.Tick</c> と <c>ActiveDocumentChanged</c> のみ)。
    /// <para>発声手段そのものは注入しない —— <see cref="BackupCoordinator"/> は
    /// <c>_map</c> を非スレッドセーフな Dictionary で持つ UI スレッド専有クラスであり、
    /// <c>IAnnouncer</c> を知る必要が無い。<see cref="IBackupWriter.OnWriteFailed"/> と
    /// 同じ Action プロパティの idiom に揃えてある。</para></summary>
    public Action<bool>? OnBackupHealthChanged { get; set; }
```

**(b) 配線**(`CreateWriter`・`:164-166` の隣):

```csharp
        w.OnWriteFailed = OnBackgroundWriteFailed;
        // M-20: 成功は id を使わないのでフラグだけ立てる(_layoutWriteFailed と同型)。
        w.OnWriteSucceeded = _ => Interlocked.Exchange(ref _writeSucceeded, 1);
```

**(c) drain と遷移判定**(`ReconcileContent` 冒頭・`:498-503` を差し替え):

```csharp
        // 背景書込が失敗した文書を強制再書込対象にする(楽観更新で欠落・陳腐化しないように)。
        bool anyFailed = false;
        while (_failed.TryDequeue(out var failedId))
        {
            anyFailed = true;
            foreach (var v in _map.Values)
                if (v.Id == failedId)
                    v.ForceWrite = true;
        }
        // M-20: 成功フラグは<b>健全なときも必ず読み捨てる</b>。残すと、後で失敗して
        // unhealthy になった次の drain が、失敗より前に届いていた古い成功で復旧と判定する。
        bool anySucceeded = Interlocked.Exchange(ref _writeSucceeded, 0) == 1;
        ReportBackupHealth(anyFailed, anySucceeded);
```

```csharp
    /// <summary>M-20(B5): 書込の健全性が遷移したときだけ報告する。
    /// <para><b>同一 pass に失敗と成功が両方あれば失敗が勝つ。</b> 複数文書のうち 1 つだけ
    /// 書けている状態を「復旧」と呼ばないため。</para>
    /// <para><b>成功の観測が必須である理由</b>: 「失敗が来ない」だけでは、書込が成功したのか
    /// <b>そもそも投入していない</b>のか(dirty でない・署名一致で <c>BackupAction.None</c>)を
    /// 区別できない。後者を復旧と読むと、一度も書けていないのに「再開しました」と言うことになる。</para>
    /// <para><c>_enabled == false</c>(レイアウトのみモード)では <see cref="ReconcileMapMaintenance"/>
    /// 側へ分岐して本メソッドが走らない = 報告も起きない。<b>バックアップを書いていないのだから
    /// 正しい</b>。</para></summary>
    private void ReportBackupHealth(bool anyFailed, bool anySucceeded)
    {
        if (anyFailed && _backupHealthy)
        {
            _backupHealthy = false;
            OnBackupHealthChanged?.Invoke(false);
        }
        else if (anySucceeded && !_backupHealthy)
        {
            _backupHealthy = true;
            OnBackupHealthChanged?.Invoke(true);
        }
    }
```

**(d) MainForm の配線**(`MainForm.cs:242` の `_backup = new BackupCoordinator(...)` 直後):

```csharp
        // M-20(B5): バックアップ書込の健全性が遷移したときだけ知らせる。既定 tick は 300 秒なので、
        // 一過性の失敗では「失敗」「復旧」の 2 回鳴りうる —— その 5 分間はバックアップが実際に
        // 効いていなかったので、黙る側ではなく言う側へ倒す(設計 §5.5 (b) で受容)。
        _backup.OnBackupHealthChanged = healthy =>
            _announcer.Say(
                healthy
                    ? "バックアップの保存を再開しました"
                    : "バックアップを保存できません。編集中の内容は復元できない可能性があります"
            );
```

### Step 4: 緑を確認する

```
dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj -c Release
```

### Step 5: 配線の網を確かめる

`MainForm` 側の 1 行(`_backup.OnBackupHealthChanged = ...`)は、上のテストでは**観測されない**
(Coordinator を直接叩いているため)。落としても全緑になる。
**一時的にこの行を消して全テストを走らせ、緑のままであることを確認する** ——
緑なら、`MainFormSmokeTests` に「実 MainForm で `LastAnnouncementForTest` を読む」網を 1 本足すか、
`IlCallees` で構造的に固定するかを決め、**決めた内容と根拠を実施記録へ書く**。
確認後、消した行は必ず戻すこと。

### Step 6: commit

```bash
git add src/kxEdit.App/BackupCoordinator.cs src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "feat(app): バックアップ書込の失敗と復旧を遷移で 1 回だけ知らせる (M-20)"
```

### Step 7: 仕様レビュー

---

## Task 5: B4 申し送り — `Unreadable` は上書きの直前に退避する

**Files:**
- Modify: `src/kxEdit.Core/Settings/SettingsStore.cs`(`TryQuarantineCorrupt` の隣)
- Modify: `src/kxEdit.App/SettingsStartup.cs:51-54`(戻り値)/ `:107-130`(文言)
- Modify: `src/kxEdit.App/Program.cs:90-98`(`CreateMainForm`)
- Modify: `src/kxEdit.App/MainForm.cs`(ctor・`TrySaveSettings`)
- Test: `tests/kxEdit.App.Tests/SettingsStartupTests.cs` / `tests/kxEdit.Core.Tests/`(`SettingsStoreTests`)

### なぜ「起動時にコピー」ではないか(設計書 §6.2)

`Unreadable` は `File.Exists` が true で `File.ReadAllText` が投げた状態
(`SettingsStore.cs:79-95`)。`File.Copy` は同じ読み取りを行うので、`ReadAllText` を落とした
事由はコピーも落とす。**「コピーしました」と言えるケースがほぼ残らない。**

### この belt が実際に効く場面(実装時に確かめること)

`File.Move` は `GENERIC_READ` を必要とせず、対象への `DELETE` と親ディレクトリへの書込で足りる
(rename は `SetFileInformationByHandle(FileRenameInfo)` 相当)。したがって:

| `Unreadable` の事由 | `File.Copy` | `File.Move`(本 belt) | `AtomicFile.Write` の差替 |
|---|---|---|---|
| ファイル単位の DENY-READ ACL | ✗ | **○(効く)** | 成功しうる = 原本が消える |
| 他プロセスの排他ロック(継続中) | ✗ | ✗ | ✗(保存も落ちる) |
| 一過性のロック(セッション中に解放) | ✗(起動時は掴まれている) | **○(効く)** | 成功 = 原本が消える |
| I/O エラー・不正パス | ✗ | ✗ | ✗ |

**上の「○」は推論であって実測ではない。** Task 5 の実装時に、DENY-READ ACL を張ったファイルに
対して `File.Move` が通ることを手で確かめ、**結果を実施記録へ書く**(通らなければ belt は
一過性ロックのケースにしか効かないので、その事実を設計書の受容記録へ残す)。
自動テスト化は `Category=LocalOnly` でも脆いので行わない —— L5 チェックリスト(Task 6)へ回す。

### Step 1: 失敗するテストを書く

**(a) Core 側**(`SettingsStoreTests`):

```csharp
    /// <summary>B5: 読み取れなかった設定を上書きする直前の退避。<c>.bad</c>(破損)とは
    /// 意味が違うので別名(<c>.bak</c>)にする —— 前者は「壊れた内容」、後者は
    /// 「読めなかっただけで中身は正常かもしれない以前の設定」。</summary>
    [Fact]
    public void TryQuarantineUnreadable_renames_the_original_aside()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "settings.json");
        File.WriteAllText(path, "original");
        Assert.True(SettingsStore.TryQuarantineUnreadable(path, out string aside));
        Assert.Equal(path + ".bak", aside);
        Assert.False(File.Exists(path));
        Assert.Equal("original", File.ReadAllText(aside));
    }

    /// <summary>退避の失敗で起動・保存を落とさない(Corrupt 側と同じ catch-all 契約)。</summary>
    [Fact]
    public void TryQuarantineUnreadable_returns_false_when_the_original_is_gone()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "never-created.json");
        Assert.False(SettingsStore.TryQuarantineUnreadable(path, out _));
    }
```

**(b) App 側**(`SettingsStartupTests` + `MainFormSmokeTests`):

```csharp
    /// <summary>B4 申し送りの回収: Unreadable セッションの<b>最初の保存</b>で原本を退避する。
    /// 起動時の警告は「先にコピーしてください」と案内するが、ユーザーが対処する前に
    /// OnFormClosing / RegisterRecent が上書きしてしまう(B4 設計書 §10.15 の申し送り)。</summary>
    [Fact]
    public void Saving_settings_quarantines_the_unreadable_original_once() { /* 骨子は下記 */ }

    /// <summary>Ok / Missing / Corrupt のセッションでは退避しない。</summary>
    [Theory]
    public void Saving_settings_does_not_quarantine_for_other_statuses() { }

    /// <summary>退避に失敗しても保存は進む(B4 §5.5 の「保存を止めない」を維持する)。
    /// 止めると「設定を適用しました」が虚偽になり、M-22 が潰した欠陥を新設する。</summary>
    [Fact]
    public void Saving_settings_proceeds_even_when_the_quarantine_fails() { }
```

骨子: `SettingsStartup.Prepare` に seam(`quarantineOverrideForTest` と同型)を足し、
`MainForm` へ「退避が保留中か」を渡してから `ApplySettingsForTest` を 2 回叩き、
**退避 seam の呼出が 1 回だけ**であることを固定する。

### Step 2: 走らせて赤を確認する

期待: コンパイルエラー。

### Step 3: 実装

**(a) `SettingsStore.cs`** — `TryQuarantineCorrupt` の直下。**共通の private ヘルパーに割る**
(2 つの公開名を残すのは、それぞれの呼出が 1 箇所であるという構造的な主張を保つため。
`TryQuarantineCorrupt` の xmldoc が「Corrupt のときだけ改名するのは呼出位置で保たれている」と
書いているので、suffix 引数で 1 本にまとめるとその主張が崩れる):

```csharp
    /// <summary>B5: 読み取れなかった設定を、上書きする<b>直前</b>に <c>.bak</c> へ退避する。
    /// <para>
    /// <b><see cref="TryQuarantineCorrupt"/> とは呼ぶ時点が違う。</b> あちらは起動時に
    /// 「壊れている」と判った内容を退避する。こちらは<b>中身が正常かもしれない</b>ファイルを
    /// 扱うため起動時には改名できない(一過性のロックなら次回起動で普通に読めたはずのものを
    /// 壊すことになる。B4 設計 §5.2 が退避を却下した理由)。上書きの直前なら中身はどのみち
    /// 失われるので、退避は<b>厳密に増える側</b>にしか働かない。
    /// </para>
    /// <para>
    /// <c>File.Move</c> は <c>GENERIC_READ</c> を要さないため、<b>読み取りを拒否する ACL で
    /// <c>Unreadable</c> になったファイルでも成功しうる</b> —— そこが
    /// <c>File.Copy</c>(読み取りが要る)を採らなかった理由である。
    /// </para>
    /// <para><c>.bak</c> は掃除しない。<c>%APPDATA%\kxEdit\</c> 直下でどの sweeper の視野にも
    /// 入らないが、<b>消すのはユーザーの判断</b>という <c>.bad</c> の方針を踏襲する(B4 §9)。</para>
    /// </summary>
    public static bool TryQuarantineUnreadable(string path, out string quarantinePath) =>
        TryRenameAside(path, ".bak", out quarantinePath);
```

`TryQuarantineCorrupt` の本体も同じ private ヘルパーへ寄せる(`.bad` を渡す)。
**この付け替えで既存の `SettingsStoreTests` が全緑のままであることを確認すること。**

**(b) `SettingsStartup.Prepare`** — 戻り値に「退避が要るか」を足す。
既存が 2-tuple なので 3-tuple へ広げる(呼出は `Program.CreateMainForm` の 1 箇所だけ):

```csharp
    internal static (
        AppSettings Settings,
        string? Warning,
        bool QuarantineBeforeFirstSave
    ) Prepare(...)
```

`Unreadable` 分岐だけ `QuarantineBeforeFirstSave: true` を返し、他は `false`。

**(c) `Unreadable` の文言**(`SettingsStartup.cs:118-125` 付近)。
**退避の成功を約束しない** —— リネームは失敗しうる。即時の行動指針(コピー)は残す:

```csharp
                return (
                    settings,
                    "設定ファイルを読み取れなかったため、既定の設定で起動しました。\n\n"
                        + RewriteReason
                        + "このまま使うと、読み取れなかったファイルは既定の設定で上書きされます。"
                        + "上書きの前に元のファイルを '.bak' を付けた名前へ退避しますが、"
                        + "退避できないこともあります。"
                        + "以前の設定を確実に残したい場合は、先に次のファイルをコピーしてください:\n  "
                        + SanitizeForDisplay.OneLine(path),
                    QuarantineBeforeFirstSave: true
                );
```

> **既存の `SettingsStartupTests` はこの文言を assert している可能性が高い。**
> 落ちたテストの期待値を更新するのは正しいが、**「何を assert していたか」を読んでから**直すこと
> (文言そのものではなく「原本パスが載っていること」を見ているなら、変更は無害のはず)。

**(d) `Program.CreateMainForm` と `MainForm` ctor** — フラグを通す。
`MainForm` の internal ctor に `bool quarantineSettingsBeforeFirstSave = false` を足す
(**public ctor には足さない** —— `MainForm.cs:162-165` の警告どおり、位置指定呼出が
黙って束縛先を変える)。

**(e) `MainForm.TrySaveSettings`** — 最初の保存の直前に 1 回だけ退避:

```csharp
    private Exception? TrySaveSettings()
    {
        // B5(B4 申し送りの回収): 読み取れなかった設定を上書きする直前に退避する。
        // 起動時の警告は「先にコピーしてください」と案内するが、ユーザーが対処する前に
        // OnFormClosing / RegisterRecent が上書きしてしまう。
        // フラグはここで先に落とす = 退避が失敗しても<b>再試行しない</b>(毎回の保存で
        // 失敗し続けるリネームを試みても得るものが無い)。
        if (_quarantineSettingsBeforeFirstSave)
        {
            _quarantineSettingsBeforeFirstSave = false;
            // 退避の失敗で保存を止めない(B4 §5.5)。止めると「設定を適用しました」が
            // 虚偽になり、M-22 で潰した欠陥をここで新設することになる。
            _ = SettingsStore.TryQuarantineUnreadable(_settingsPath, out _);
        }
        try { ... }
    }
```

> **テスト seam をどう入れるかは実装時に決める。** `SettingsStartupTests` の
> `quarantineOverrideForTest` と同型(`Func<string, bool>?` を internal ctor で受ける)が素直だが、
> ctor の引数がさらに増える。**実ファイルで検証できる**(`TempDir` に本物の
> `settings.json` を置き、`ApplySettingsForTest` を叩いて `.bak` の実在を見る)なら seam は不要。
> **不要で済むならその方がよい** —— seam を足すと「実経路が本当に通ったか」の観測が 1 段遠くなる。

### Step 4: 緑を確認する

```
dotnet build kxEdit.sln -c Release --no-incremental -warnaserror
dotnet test kxEdit.sln -c Release
```

### Step 5: DENY-READ ACL の実測

上の表の「○」を手で確かめる。PowerShell(管理者不要)で:

```powershell
$p = "$env:TEMP\b5-acl-probe.json"
Set-Content -Path $p -Value '{}' -Encoding utf8
icacls $p /deny "$env:USERNAME:(R)"
# ReadAllText が落ちること / File.Move が通ることを確認する
```

**結果を実施記録へ書く。** 通らなければ設計書 §6.3 の効き目は「一過性ロックのみ」に縮むので、
その訂正も記録する(CLAUDE.md §8: 設計書は書き換えず、実施記録へ追記)。

### Step 6: commit

```bash
git add src/kxEdit.Core/Settings/SettingsStore.cs src/kxEdit.App/SettingsStartup.cs src/kxEdit.App/Program.cs src/kxEdit.App/MainForm.cs tests/
git commit -m "fix(core,app): 読み取れなかった設定を上書きの直前に退避する (B4 申し送り)"
```

### Step 7: 仕様レビュー

---

## Task 6: L5 チェックリスト・最終レビュー・品質ゲート

**Files:**
- Create: `docs/plans/2026-09-02-truthful-notifications-l5-checklist.md`
- Modify: `docs/plans/2026-09-02-truthful-notifications-design.md`(実施記録節を追記)

### Step 1: L5 チェックリストを起こす

設計書 §9 の 5 項目を、既存の `*-l5-checklist.md`(14 本ある)と同じ書式で起こす。
**手順・期待発声・PASS/FAIL 欄**を含めること。項目:

1. **M-8**: CSV モードで → を押しっぱなしにして離し、最後に読まれるセルがキャレット位置と一致する
2. **M-22**: `settings.json` を読み取り専用にして設定 OK → NVDA スピーチビューアーで逐語確認
3. **M-20**: バックアップ先を書込不可にして tick を待つ → 失敗発声 → 書込可へ戻す → 復旧発声
   → **さらに 1 tick 待って再度鳴らないこと**
4. **Task 5**: 読み取れない `settings.json` で起動 → 警告確認 → 設定保存 → `.bak` の実在確認
   (**DENY-READ ACL 版**を含める。Task 5 Step 5 の実測がここで裏取りされる)
5. (観測のみ・合否判定しない)M-7: 文書先頭 Backspace / 末尾 Delete で NVDA が何か言うか

`tools/sr-regression.ps1` は UIA 応答までしか見ないため**代替にならない**旨を明記する。

### Step 2: 最終ブランチレビュー(2 パス)

CLAUDE.md §3-5。**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。

- **コード品質パス**(ミューテーション検証のスポットチェックは §7 の判断により**行わない**。
  代わりに「網が本当に新しいか」= 変更前 src で赤になることの確認を重点にする)
- **脆弱性パス**(設計書 §10 の判定では前倒し不要としたが、最終パスは必ず行う。
  焦点はパスの表示・無害化と、`.bak` / tmp の残留がもたらす情報露出)

指摘は 3 択(fixup commit / PR description に記載して受容 / 理由付き却下)で明示し、
**元 commit を書き換えず別 fixup commit で積む**。

### Step 3: 実施記録を設計書へ追記

設計書 §11 の宿題を回収した記録を書く:

- `_succeeded` の扱い(Task 4 で `Interlocked` フラグを選んだ根拠)
- Task 1 Step 5 / Task 4 Step 5 で判明した「網の有無」
- Task 5 Step 5 の ACL 実測結果
- 計画のコードと実コードが食い違った箇所

### Step 4: 品質ゲート

```
pwsh tools/pre-merge-check.ps1
```

**EXIT 0** を確認する(CLAUDE.md §6)。0 warning 維持。

### Step 5: L5 を実施する

**ユーザーへ実機 SR 検証を依頼する。** L5 が最終ゲート(CLAUDE.md §5)。
a11y 関連変更なので `tools/sr-regression.ps1` も手動実行する(L5 の代替にはならない)。

### Step 6: PR

```bash
git push -u origin feature/truthful-notifications
gh pr create --base main
```

PR description は日本語で、目的・レビュー経緯・申し送り(受容した指摘を含む)を記載する。

---

## 申し送り(実装中に決めること)

| 項目 | どこで決めるか |
|------|---------------|
| `SerialBackupWriter.Write` の成功通知を try の内と外どちらに置くか | Task 3 Step 3。外に置く案を示したが、既存テストの緑を確認して確定する |
| `MainForm` のフック配線 1 行に網を張るか | Task 4 Step 5。落として緑なら網を足すか `IlCallees` で固定するかを決める |
| Task 5 のテスト seam の要否 | Task 5 Step 3 (e)。実ファイルで検証できるなら seam を足さない |
| `File.Move` が DENY-READ ACL を越えるか | Task 5 Step 5 で実測。越えなければ設計書 §6.3 の効き目を実施記録で訂正 |
| M-7 を次リリースの項目として起こすか | Task 6 Step 1 の L5 項目 5 の観測結果次第 |
