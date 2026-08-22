# 「名前を付けて保存」の保存先確定を健全化する(A-7 / A-4 / A-19)設計書

- 日付: 2026-08-23
- 対象: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` の **A-7 / A-4 / A-19**(いずれも優先度 1)
- ブランチ: `feature/saveas-target-validation`
- ベース: main `b2c5ad8`
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録のみ追記する。

## 1. 目的

`FileController.SaveAsDocument` が **ダイアログの戻り値を検証せずそのまま保存先として使う** ことに
起因する 4 症状(3 ID)を根治する。

| ID | 症状 | 害 |
|----|------|-----|
| A-7 (a) | テキストボックスに既存ファイル名を打って OK → **無確認で上書き** | データ喪失 |
| A-7 (b) | 他タブが開いているパスへ保存 → 同一ファイルを 2 タブが編集 → 片方の Ctrl+S でもう片方の内容が消える | データ喪失 |
| A-19 | 相対パス(`memo.txt`)が未正規化のまま `State.Path` に残り、保存先が CWD 依存。hot exit 復元で無言の無題化 | 保存先の喪失 |
| A-4 | **ネットワーク共有へ新規ファイルを保存できない**(「ネットワークパスに到達できません」) | 機能不全 |

監査 §8 の「A-4 / A-7 / A-19 は FileController に閉じる」という束ね方に従う。
A-1 / A-8 は `BackupCoordinator` 側のテーマで、A-1 は [PR #42](https://github.com/kenny7968/kxEdit/pull/42)
で処理済み・A-8 は本書の対象外。

## 2. 3 件を 1 ブランチにする理由(機構上の依存)

別々のバグに見えるが、**保存先パスを確定する 1 本の手続き**の穴で、修正が互いに依存する。

- **A-19 → A-4**: `RemotePathDetector.IsRemote` は相対パスに `false` を返す
  (`RemotePathDetector.cs:36-38` で `Path.GetPathRoot` が空文字 → early return)。
  正規化を先に置かないと、マップドネットワークドライブ上の CWD で相対パスを打ったとき
  リモート判定が漏れ、A-4 の到達性判定自体が動かない。
- **A-19 → A-7 (a)**: 存在確認は `File.Exists` に依存する。相対パスのままだと CWD 基準で
  当たり判定が動き、確認の有無が起動時のカレントディレクトリで変わる。
- **A-4 ←→ A-7 (a)**: 両方とも「保存先に対する I/O」であり、**同じ 5 秒プローブの中で
  同時に答えるべき**(§3)。別々に実装すると遠隔共有で 5 秒 + 60 秒になる。

## 3. 中核設計 — 到達性と存在を 1 回の境界付き I/O で得る

### 3.1 罠: 素の `File.Exists` は 60 秒凍結を再導入する

A-7 (a) の「修正の当たり」は監査では `File.Exists なら上書き確認` と書かれている。
これを素で足すと、**PR #42 の脆弱性レビュー H-1 で踏んだ罠の再導入**になる:
切断済み SMB 共有に対する `File.Exists` は UI スレッドで 60 秒ブロックする
(HIGH-6 / `FileMetaProvider` が既に踏み、`IReachabilityProbe` を導入して塞いだ経路)。

保存先に対するどんな I/O も、`IReachabilityProbe` の境界付きタスクを通す。

### 3.2 `IReachabilityProbe` にメソッドを 1 本追加する

既存 `ProbeWithTimeout`(= `File.Exists` 意味論)は **Load 経路** と `FileMetaProvider` /
`FileTimestampProvider` が使っているため**不変**とし、保存先専用のメソッドを足す。

```csharp
/// <summary>保存先の到達性と既存有無。タイムアウト時は (false, false)。</summary>
public readonly record struct SaveTargetProbe(bool Reachable, bool FileExists);

public interface IReachabilityProbe
{
    bool ProbeWithTimeout(string path, TimeSpan timeout);              // 既存(Load 用)= 不変

    /// <summary>保存先を 1 回の境界付き I/O で調べる。Reachable = FileExists || 親フォルダーが存在。</summary>
    SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout);
}
```

本番実装(`FileReachabilityProbe`):

```csharp
public SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout)
{
    var task = Task.Run(() =>
    {
        try
        {
            bool fileExists = File.Exists(path);
            string? dir = Path.GetDirectoryName(path);
            // dir が null/空 = ルート自体("C:\")を指す入力。ファイルとしては保存できないので
            // 到達不能側に落とす(親フォルダーが無い=書き込み先が確定しない)。
            bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
            return new SaveTargetProbe(fileExists || dirExists, fileExists);
        }
        catch
        {
            return new SaveTargetProbe(false, false);
        }
    });
    return task.Wait(timeout) ? task.Result : new SaveTargetProbe(false, false);
}
```

- `Reachable = FileExists || dirExists` が **A-4 の修正**。現状は `File.Exists` の結果を
  そのまま到達性として返すため(`FileReachabilityProbe.cs:20`)、存在しない新規パスは
  到達可能でも常に false になっていた。
- `FileExists` が **A-7 (a) の入力**。I/O は 1 回のタスクの中で完結するので、
  遠隔共有でも待ちは 5 秒 1 回に収まる。

### 3.3 ローカルパスも同じ経路を通す

現行 `TryProbeReachability` は `RemotePathDetector.IsRemote` でプローブをゲートしている。
新しい保存先判定は**ゲートせず常にプローブを通す**。理由:

- A-7 (a) はローカル・リモートの別なく存在確認を要する。分岐を作ると
  「ローカルは実ファイル・リモートは Fake」で網が二重になり、片側が抜ける。
- ローカルパスの `Task.Run` + `Wait` は即完了する(実測は実装時に確認)。

ただし**エラー文言だけは `IsRemote` で分ける**。到達不能の理由がリモートとローカルで違うため:

| 判定 | 文言 |
|------|------|
| `IsRemote(path)` かつ `!Reachable` | `ネットワークパスに到達できません: {path}`(既存文言を踏襲) |
| それ以外で `!Reachable` | `保存先のフォルダーが見つかりません: {path}` |

パスは外部入力(SR ユーザーの直入力 / grep 由来)なので、既存規約どおり
`SanitizeForDisplay.OneLine(path, 200)` を通してから prompt に載せる(CSV-L-5)。

## 4. `SaveAsDocument` をループにする

### 4.1 制御フロー

```
seed = SaveAsRequest(doc.State.Path, doc.State.Encoding.CodePage, doc.State.HasBom, doc.State.LineEnding)
while (true):
    picked = _fileDialogs.PickSaveAs(_owner, seed)
    if picked is null: return false                                  # キャンセル = 唯一の途中出口
    seed = seed with { Path = picked.Path, CodePage = ..., HasBom = ..., LineEnding = ... }

    (1) IsNullOrWhiteSpace(picked.Path)      -> Warn("ファイル名を指定してください。")        -> continue
    (2) TryNormalize(picked.Path, out full)  -> 失敗なら Warn("パスが正しくありません: …")     -> continue   # A-19
        seed = seed with { Path = full }
    (3) _docs.FindByPath(full) が doc 以外   -> Error("別のタブで開いています…")               -> continue   # A-7 (b)
    (4) probe = ProbeSaveTargetWithTimeout(full, 5s)
        !probe.Reachable                     -> Error(§3.3 の文言)                             -> continue   # A-4
        probe.FileExists && !OkCancel(上書き) -> continue                                                     # A-7 (a)
    (5) 文字コード劣化警告 いいえ            -> continue

    State を更新 -> WriteToPath(doc, full) -> 失敗ならロールバックして return false
    doc.State.Path = full; UpdateLabel; RegisterRecent(full); return true
```

出口は「キャンセル」と「`WriteToPath` の結果」だけで、**すべての `continue` は
`PickSaveAs`(ユーザー操作)を挟む**。ユーザー操作なしに回るループは存在しない。

### 4.2 順序の根拠

安いローカル判定 → I/O を伴う判定 → 内容に関する警告、の順に置く。
(3) の重複タブ照合を (4) のプローブより前に置くのは、**重複タブは保存を許さない**ため
到達性を調べる意味がないから(遠隔共有で無駄な 5 秒を待たせない)。

### 4.3 A-19 の正規化

```csharp
private static bool TryNormalize(string input, out string full)
{
    try { full = Path.GetFullPath(input); return true; }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                               or PathTooLongException or System.Security.SecurityException)
    { full = string.Empty; return false; }
}
```

`Path.GetFullPath` は null 文字・無効文字・長大パスで投げる。SR ユーザーの直入力が
そのまま届く面なので、例外は握って「入力し直し」に落とす(未捕捉例外ダイアログにしない)。

`PathKey.For` も内部で `GetFullPath` するが、あちらは**失敗時に空文字へ落として
dedup キーを 1 件に集約する**契約(CSV-L-8)で、こちらは**ユーザーに直させる**のが目的。
契約が違うので流用しない。

`full` は `WriteToPath` / `State.Path` / `RegisterRecent` のすべてに使う。
現状 `doc.State.Path = picked.Path`(`FileController.cs:347`)が未正規化を残していた点が A-19 の実体。

### 4.4 重複タブの照合

```csharp
var other = _docs.FindByPath(full);
if (other is not null && !ReferenceEquals(other, doc)) { Error(...); continue; }
```

`FindByPath` は `PathKey`(`GetFullPath` + `ToLowerInvariant`)で照合するので、
大文字小文字違い・区切り文字違いも同一と判定される。
**自分自身の除外が必須**(自分のパスへの上書き保存は正当な操作)。

### 4.5 意図的な挙動変更(CLAUDE.md §2 に基づく明示)

| 変更 | 従来 | 新 | 理由 |
|------|------|----|------|
| 空白パスの警告後 | SaveAs 全体を中止 | ダイアログを再表示 | 打ち直しを強いない |
| 文字コード劣化警告のキャンセル後 | SaveAs 全体を中止 | ダイアログを再表示 | 文字コードのコンボボックスは**そのダイアログの中**にある。中止して開き直させるほうが不自然 |
| `参照` の `SaveFileDialog` | `OverwritePrompt` 既定 ON | `OverwritePrompt = false` | 確認点を §4.1 (4) の 1 箇所に集約。A-7 の訴えは「経路によって確認が出たり出なかったりする非対称」そのもの |
| `State.Path` | ダイアログの生入力 | 正規化済み絶対パス | A-19 |

いずれも「**ダイアログの中で選んだ値への警告なら、そのダイアログへ戻す**」という 1 つの原則の帰結。

## 5. `WriteToPath` 側(A-4 の実体)

`WriteToPath` 冒頭の `TryProbeReachability(path)`(`FileController.cs:439-445` 付近)を
保存先意味論へ切り替える。**A-4 が現に発火しているのはここ**で、
`SaveAsDocument` の事前判定だけを直しても新規ファイルは書けない。

```csharp
// WriteToPath 冒頭
if (!TryProbeSaveTarget(path, out _))   // Reachable のみ見る。FileExists は使わない
    return false;
```

`LoadInto` の `TryProbeReachability` は**不変**(読む側は存在しないと意味がない)。

SaveAs 経路ではプローブが 2 回走る(§4.1 (4) と `WriteToPath` 冒頭)。
`WriteToPath` は **Ctrl+S が直接呼ぶ**入口でもあるため自己完結を崩さない、という判断で受容する。
1 回目が通った直後の 2 回目なので実質即答で、共有が 2 回の間に落ちた場合のみ追加で 5 秒待つ。
`skipProbe` のようなバイパス引数は導入しない(将来の呼出が誤って素通りできる seam を作らない)。

## 6. 非目標(YAGNI)

- **既存タブを閉じてから保存する / 既存タブへ統合する**(A-7 (b) の代替案)は採らない。
  他タブの未保存内容の扱いという別テーマを抱え込む。エラーで中止が最も安全。
- **保存先がフォルダーだった場合の専用エラー**(`Directory.Exists(full)` が true)は入れない。
  `WriteToPath` の既存 catch がエラーを出す。§9 の申し送りにする。
- **A-1 / A-8**(`BackupCoordinator` 側)、**A-10**(上書き保存経路のエンコード警告)、
  **A-11**(`ConvertEols` の Undo 全消去)は含めない。別テーマ。
- 外部プロセスによる更新検知(保存時の mtime 比較・M-18)は含めない。

## 7. テスト設計

### 7.1 層

- **L1(Core)**: 変更なし。`RemotePathDetector` / `PathKey` は触らない。
- **L3(App)**: `FileControllerTests` に追加。`FakeFileDialogService` / `FakePrompt` /
  `FakeReachabilityProbe` の既存シームで全ケースを閉じられる。

### 7.2 `FakeFileDialogService` の拡張(ループの暴走を構造で防ぐ)

ループテストでは `PickSaveAs` が複数回呼ばれる。現状の fake は単一値を返し続けるため、
prompt が拒否し続けるテストは**無限ループになる**。

```csharp
public Queue<SaveAsResult?> SaveAsQueue { get; } = new();
public int PickSaveAsCount { get; private set; }

public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
{
    PickSaveAsCount++;
    SaveAsRequests.Add(current);
    if (SaveAsQueue.Count > 0) return SaveAsQueue.Dequeue();
    return PickSaveAsCount == 1 ? SaveAs : null;   // 単一値は 1 回だけ。以降はキャンセル扱い
}
```

**キュー枯渇 = キャンセル**にすることで、網の書き間違いが無限ループではなく
「PickSaveAsCount が想定と違う」という失敗として出る。
既存テスト(`Dialogs.SaveAs = ...` で 1 回だけ呼ぶ)は挙動不変。

`SaveAsRequests` は既存のまま**再表示時の seed を検証する観測点**として使う。

### 7.3 追加テスト(L3・12 本前後)

| # | 名前(仮) | 検証 |
|---|-----------|------|
| 1 | `SaveAs_ExistingFile_AsksOverwriteConfirmation` | 実ファイルを置いて SaveAs → `OkCancel` が Log に出る |
| 2 | `SaveAs_OverwriteDeclined_KeepsFileAndReopensDialog` | 「いいえ」→ ファイル内容不変・`PickSaveAsCount == 2`・2 回目の seed が入力値 |
| 3 | `SaveAs_NewFile_DoesNotAskOverwrite` | `OkCancel` が Log に**出ない** |
| 4 | `SaveAs_PathOpenInAnotherTab_ShowsErrorAndReopens` | Error + 書かれない + 再表示 |
| 5 | `SaveAs_OwnPath_IsNotTreatedAsDuplicate` | 自タブのパスへの保存は通る |
| 6 | `SaveAs_RelativePath_StoresAbsolutePath` | `State.Path` が絶対・実ファイルが解決先に作られる |
| 7 | `SaveAs_UnnormalizablePath_WarnsAndReopens` | 正規化失敗 → Warn + 書かれない + 再表示 |
| 8 | `SaveAs_NewFileOnRemotePath_Succeeds` | probe が `(Reachable: true, FileExists: false)` → 保存が通る(**A-4 の回帰**) |
| 9 | `SaveAs_UnreachableTarget_ShowsErrorAndReopens` | `(false, false)` → Error + 再表示 |
| 10 | `SaveAs_BlankPath_WarnsAndReopens` | 従来 Warn + 中止 → Warn + 再表示 |
| 11 | `SaveAs_Cancelled_WritesNothing` | `PickSaveAsCount == 1`・書かれない |
| 12 | `SaveAs_ProbesSaveTargetWithFiveSecondTimeout` | timeout の pin(既存 `LastTimeout` 観測点と対称) |
| 13 | `SaveAs_EncodingWarningDeclined_ReopensDialog` | 文字コード警告キャンセル → 再表示 |

### 7.4 網の穴を作らないための注意(執筆時に固定する)

過去 3 ブランチのレビューで繰り返し出た形をそのまま踏む。

- **#5(自タブ除外)は非既定状態から始める**。無題タブ(`State.Path == null`)から
  始めると `FindByPath` は常に null を返すので、「null が返った」と「自分が返った」が
  区別できず、`!ReferenceEquals(other, doc)` を落とす変異が生存する。
  **パス確定済みの doc + 別パスの他タブ**という fixture にする。
- **#3(新規ファイル)は「確認が出ないこと」を Log で検証する**。
  `FakePrompt.OkCancelResult` の既定は `true` なので、保存が成功したことだけでは
  確認の有無を区別できない(vacuous になる)。
- **#2 / #4 / #7 / #9 / #10 / #13 は再表示を `PickSaveAsCount` で検証する**。
  Warn/Error が出たことだけを見ると、`continue` を `return false` に変える変異が生存する。
- **#8 は `FakeReachabilityProbe` に `SaveTargetResult` を持たせる**。
  既存の `Result`(bool)を流用すると `Reachable` と `FileExists` が同値に縛られ、
  A-4 の本質(到達可能かつ非存在)を表現できない。

### 7.5 ミューテーション検証(最終品質パスのスポットチェック)

| # | 変異 | kill を期待するテスト |
|---|------|----------------------|
| 1 | `!ReferenceEquals(other, doc)` をガードから除去 | #5 |
| 2 | `probe.FileExists` を `false` に固定 | #1 / #2 |
| 3 | `!probe.Reachable` を `false` に固定 | #9 |
| 4 | `Reachable = FileExists \|\| dirExists` → `Reachable = FileExists` | #8(A-4 の再導入) |
| 5 | `TryNormalize` の結果を捨てて `picked.Path` を使う | #6 |
| 6 | 各 `continue` を `return false` へ | #2 / #4 / #7 / #9 / #10 / #13 |

## 8. L5(実機 SR 検証)

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)には**触れない**。
しかし**テキストボックス直入力は SR ユーザーの主経路**(A-7 の再現 (a) がそう書いている)であり、
ダイアログ再表示というフォーカス遷移が新しく入る。CLAUDE.md §5「迷ったら必要に倒す」に従い実施する。

`docs/plans/2026-08-23-saveas-target-validation-l5-checklist.md` に以下を用意する:

1. 既存ファイル名を直入力 → OK → 上書き確認が NVDA で読まれる
2. 「いいえ」→ SaveAs ダイアログが再表示され、**フォーカスがファイル名テキストボックスにあり**、
   入力していた値が残っていることが読まれる
3. 他タブで開いているファイル名 → エラーが読まれ、再表示される
4. `参照` ボタン経由で既存ファイルを選ぶ → `SaveFileDialog` では確認が出ず、
   OK 後に **1 回だけ**確認が出る(二重確認がない)

## 9. 申し送り

- **S-1**: 保存先がフォルダーだったとき(`Directory.Exists(full)`)の専用エラーは入れていない。
  `WriteToPath` の `UnauthorizedAccessException` 経路で分かりにくいエラーになる。
  プローブは既に `Directory.Exists` を呼んでいるので、必要になれば `SaveTargetProbe` に
  `IsDirectory` を足すだけで足りる。
- **S-2**: 上書き保存(Ctrl+S)経路には重複タブ検知を入れていない。
  A-7 (b) を本書の (3) で塞げば同一パス 2 タブは作られないので、現状は到達不能。
  将来タブのドラッグ並べ替えや外部からのパス変更を入れるなら再検討する。
- **S-3**: `FileMetaProvider` / `FileTimestampProvider` は既存 `ProbeWithTimeout` のままで、
  保存先意味論とは無関係(読み取り側)。統合はしない。
- **S-4**: 監査 §8 が同テーマとした **A-8**(hot exit の確認なしクローズがバックアップ書込失敗を
  待たない)は未着手。本書とは別ブランチ。
