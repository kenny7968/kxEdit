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

### 3.3 リモートゲートは維持する(ローカルは直接 I/O)

現行 `TryProbeReachability` は `RemotePathDetector.IsRemote` でプローブをゲートしている。
**このゲートは維持する**。保存先の検査はこう分岐する:

```csharp
/// <summary>保存先の既存有無を得る。到達不能(リモートのみ)なら false。</summary>
private bool TryInspectSaveTarget(string path, out bool exists)
{
    if (RemotePathDetector.IsRemote(path))
    {
        var probe = _reachabilityProbe.ProbeSaveTargetWithTimeout(path, TimeSpan.FromSeconds(5));
        exists = probe.FileExists;
        return probe.Reachable;
    }
    exists = File.Exists(path);   // ローカルは SMB 凍結の懸念がない
    return true;                  // 従来もローカルの到達性は検査していない = 挙動不変
}
```

**ゲートを外してはいけない理由**(策定時に「常にプローブを通す」と書いたのは誤り。
実装計画の執筆中に発見した):

- ローカルパスには**現状そもそも到達性検査が存在しない**。ゲートを外すと
  「存在しないフォルダー配下への保存」がプローブ段階で弾かれ、
  既存テスト `SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath`
  (`WriteToPath` 失敗時に Encoding/BOM/EOL をロールバックする=データ破損防止の要)が
  **`WriteToPath` に到達しなくなる**。挙動不変の原則(CLAUDE.md §2)に反する。
- A-4 の症状は「**ネットワーク共有**へ新規保存できない」であり、ローカル新規保存は
  今も正常に動く。ローカル側に検査を足すのは修正ではなく scope creep。

分岐が 2 本になるが、どちらも網を張れる。UNC 判定は純粋な文字列述語
(`UncPathDetector.IsUnc` = 先頭 `\\`)なので、`\\server\share\a.txt` を渡せば
実ネットワークなしで**リモート枝を Fake プローブで駆動できる**。ローカル枝は
既存テストと同じ実一時ファイルで駆動する。

到達不能の文言は既存を踏襲する(リモート枝でしか発火しないため分岐不要):

```
ネットワークパスに到達できません: {SanitizeForDisplay.OneLine(path, 200)}
```

パスは外部入力(SR ユーザーの直入力)なので、既存規約どおり
`SanitizeForDisplay.OneLine` を通してから prompt に載せる(CSV-L-5)。

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
    (4) !TryInspectSaveTarget(full, out exists) -> Error(§3.3 の文言)                          -> continue   # A-4
        exists && !OkCancel(上書き)             -> continue                                                   # A-7 (a)
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
`SaveAsDocument` の事前判定だけを直しても新規ファイルは書けない
(Ctrl+S でパス未確定 → SaveAs にフォールバックする経路も同じ `WriteToPath` を通る)。

```csharp
// WriteToPath 冒頭。IsRemote ゲートは §3.3 と同じ理由で維持する。
if (!TryInspectSaveTarget(path, out _))   // Reachable のみ見る。exists は使わない
    return false;
```

`LoadInto` の `TryProbeReachability` は**不変**(読む側は存在しないと意味がない)。
ローカルパスでは `TryInspectSaveTarget` は常に true を返すので、
「存在しないフォルダー配下への保存」は従来どおり `TextFileService.Save` の
`DirectoryNotFoundException` として `WriteToPath` の catch に落ち、
ロールバック導線(既存テスト `SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath`)が保たれる。

SaveAs 経路ではリモートパスでプローブが 2 回走る(§4.1 (4) と `WriteToPath` 冒頭)。
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
- **L3(App)**: 2 ファイル。
  - `FileReachabilityProbeTests`(新設): **本番プローブの意味論**を実一時ディレクトリで固定する。
    監査が「`FakeReachabilityProbe` で固定値を返すため実 Probe の意味論は未検証」と
    名指しした穴(A-4 の既存テスト欄)をここで塞ぐ。`Reachable = FileExists || dirExists`
    の `||` を kill できるのはこのファイルだけ。
  - `FileControllerTests`(追記): 配線・制御フロー。`FakeFileDialogService` / `FakePrompt` /
    `FakeReachabilityProbe` の既存シームで閉じる。

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
| 8 | `SaveAs_NewFileOnUncPath_PassesReachabilityGate` | `\\server\share\a.txt` + probe `(Reachable: true, FileExists: false)` → 到達性エラーが**出ない**(書込自体は実ネットワーク不在で失敗するので、失敗理由が「保存できませんでした」であることを assert する)。**A-4 の回帰** |
| 9 | `SaveAs_UnreachableUncPath_ShowsErrorAndReopens` | `(false, false)` → 到達性 Error + 再表示 |
| 10 | `SaveAs_BlankPath_WarnsAndReopens` | 従来 Warn + 中止 → Warn + 再表示 |
| 11 | `SaveAs_Cancelled_WritesNothing` | `PickSaveAsCount == 1`・書かれない |
| 12 | `SaveAs_UncPath_ProbesWithFiveSecondTimeout` | timeout の pin(既存 `LastTimeout` 観測点と対称) |
| 13 | `SaveAs_EncodingWarningDeclined_ReopensDialog` | 文字コード警告キャンセル → 再表示 |
| 14 | `SaveAs_LocalNewFile_DoesNotProbe` | ローカル枝は `IsRemote` ゲートで素通り(`Probe.CallCount == 0`)= §3.3 の挙動不変を固定 |

`FileReachabilityProbeTests`(本番プローブ・実一時ディレクトリ):

| # | 名前(仮) | 検証 |
|---|-----------|------|
| P1 | `ProbeSaveTarget_ExistingFile_ReachableAndExists` | `(true, true)` |
| P2 | `ProbeSaveTarget_NewNameInExistingDir_ReachableAndNotExists` | `(true, false)`(**A-4 の核**) |
| P3 | `ProbeSaveTarget_UnderMissingDir_NotReachable` | `(false, false)` |
| P4 | `ProbeSaveTarget_ZeroTimeout_ReturnsUnreachable` | タイムアウト時の既定値 |

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
| 2 | `TryInspectSaveTarget` の `exists` を `false` に固定 | #1 / #2 |
| 3 | `TryInspectSaveTarget` の戻り値を `true` に固定 | #9 |
| 4 | `Reachable = FileExists \|\| dirExists` → `Reachable = FileExists` | **P2**(A-4 の再導入)。#8 は Fake 経由なので kill できない |
| 5 | `TryNormalize` の結果を捨てて `picked.Path` を使う | #6 |
| 6 | 各 `continue` を `return false` へ | #2 / #4 / #7 / #9 / #10 / #13 |
| 7 | `TryInspectSaveTarget` の `IsRemote` ゲートを外して常にプローブ | #14 |

## 8. L5(実機 SR 検証)

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)には**触れない**。
しかし**テキストボックス直入力は SR ユーザーの主経路**(A-7 の再現 (a) がそう書いている)であり、
ダイアログ再表示というフォーカス遷移が新しく入る。CLAUDE.md §5「迷ったら必要に倒す」に従い実施する。

`docs/plans/2026-08-23-saveas-target-validation-l5-checklist.md` に以下を用意する:

1. 既存ファイル名を直入力 → OK → 上書き確認が NVDA で読まれる。
   **あわせて、開いた直後のフォーカスが「キャンセル」側にあることを確認する**(S-12)。
   `defaultCancel` が seam に渡っていることは L3 で pin したが、
   **MessageBox が実際にどのボタンにフォーカスを置くかは実機でしか見えない**。
   ここが OK 側だと、SR の主経路(打つ → Enter → 確認が開く → 反射的な 2 回目の Enter)で
   確認が無力化される。
   参考: 起票時に「Windows 純正の上書き確認は『いいえ』が既定」と書いたが**未検証**。
   ついでに `main` か Notepad で純正の挙動も確認できると、S-12 の記述を事実に直せる。
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
- **S-5**(Task 2 のレビューで追加・2026-08-23): `TryInspectSaveTarget` のローカル枝は素の
  `System.IO.File.Exists` を打つ。`RemotePathDetector.IsRemote` は UNC とマップドネットワーク
  ドライブを見るが、**固定ドライブ上のジャンクション/シンボリックリンクがネットワーク先を指す場合**
  (`mklink /D C:\link \\server\share`)は検出しない。この穴自体は読み取り側
  (`TryProbeFileExists` / `FileMetaProvider` / `FileTimestampProvider`)にも既存で、Task 2 は
  穴を作っていない。Task 2 時点では**直後に必ず同じパスへ書きに行く**ので上限は既存凍結の
  約 2 倍にとどまり、新規の凍結クラスではない。
  **ただし Task 7(上書き確認)が入ると性質が変わる**: 確認で「いいえ」を選ぶと書き込みが
  発生しない=対応する凍結が無いため「書き込みと同じ待ちだから相殺」という論拠が崩れ、
  さらにループなので N 回繰り返せる。**Task 7 で明示的に再評価すること。**
  低コストな案として「ローカル枝も `ProbeSaveTargetWithTimeout` を通し、`Reachable` は無視して
  `exists` だけ採る」がある(制御フローも文言も不変のまま 5 秒上限になる)が、採ると Task 7 の
  上書き確認テストが「実ファイルがディスクに在る → 確認が出る」から「Fake が在ると言った →
  確認が出る」に変わり、**本ブランチで最も重要な網が弱まる**。このトレードオフを Task 7 で判断する。
  → **判断済み(Task 7): (a) 現状維持。理由と、上の見立てのうち訂正した 2 点は §10.6。**
- **S-6**(Task 5 の脆弱性レビューで追加・2026-08-23): `SanitizeForDisplay` の適用漏れが 3 箇所ある。
  `FileController.ConfirmDiscardIfDirty` の「{DisplayName} の変更を保存しますか?」・
  `DocumentState.DisplayName`(タブラベル)・`MainForm.RebuildRecentMenu`(`&` のエスケープのみ)。
  **Task 5 が開けた穴ではない**(本タスクが足した文言は `SanitizeForDisplay.OneLine` を通っている)。
  RLO 入りの名前が実測でタブラベルと最近のファイルにそのまま載ることを確認済み。
  自分で打った名前が自分に見えるだけなのでスプーフィング価値は低いが、**`RestoreFromBackup` 経由
  (攻撃者 JSON)の `State.Path` も同じ面に載る**ので、そちらは実入力起源。CSV-L-5 の
  「新規 File I/O / 表示面を足すときの定番チェック」の棚卸し対象。
- **S-9**(Task 6 で判明・2026-08-23。**Task 6 の仕様レビューで見立てを訂正**): **A-7 (b) の修正は
  重複タブが「生まれる」経路を塞ぐが、「既にある」状態は塞がない。** `SaveDocument` →
  `WriteToPath`(Ctrl+S)は `FindByPath` を一切参照しないので、2 タブが同じパスを持っていれば
  無警告の上書きは従来どおり起きる。
  **当初「修正前ビルドからのアップグレード限定」と書いたのは誤りだった。** extras の重複排除は
  **バックアップ Id のみ**で行われパスでは行われないため(`FileController.cs:781-783`)、
  未消費の dirty バックアップの `OriginalPath` が復元済みパスと一致すれば**その場で**
  2 つ目のタブが開く。発生源はコード自身のコメント(`:779`)が挙げている
  「クラッシュ直前に開いたタブ・他インスタンス遺物・旧『あとで』孤児」。**現行フリートの経路**。
  **さらに Task 6b のレビューで判明: 既定構成(復元 OFF)でも衝突は起きる。**
  `BackupCoordinator.OfferRestoreOnStartup` は checked レコードを**パス dedup なしで全件**
  復元するので、同じ `OriginalPath` を持つ孤児バックアップが 2 件あれば衝突タブが 2 枚できる。
  上の extras の記述は ON 経路しか挙げておらず狭かった。
  さらに **Task 6 は非対称を作った**: `FindByPath` は生成順で最初の一致を返すので、衝突した
  2 タブのうち**新しい方は自分自身のパスへの SaveAs が誤検知で塞がれる**一方、Ctrl+S は
  無防備なまま他方を破壊する。**安全な操作が塞がれ、破壊的な操作が通る。**
  → **Task 6b として本ブランチで Ctrl+S 側にも同じ検査を入れる**(置き場所は `SaveDocument` で
  `BackupCoordinator` ではないので §6 の非目標には当たらない)。**Task 6b 完了後の到達点**:
  `State.Path` へ書ける経路は `WriteToPath` 一本で、その呼出元 2 つ(`SaveDocument` /
  `SaveAsDocument`)は両方ガード済み = **破壊側は全経路で閉じた**。Task 6 が作った非対称も解消。
  構成ごとの残余は次のとおり(Task 6b のレビューが実測):

  | 構成 | 衝突タブの発生 | 終了時の挙動 | 自己増殖 |
  |------|----------------|--------------|----------|
  | **既定(復元 OFF)** | `OfferRestoreOnStartup` の全件復元で起きる | 確認ループが走りガード発火 → 終了中止。「いいえ」で明示的破棄は可能 = **無断喪失なし** | **しない**(`OnFormClosed` の `Shutdown(keepForRestore: false)` が当セッションのバックアップを全削除) |
  | **ON(オプトイン)** | `BuildLayout` がパス dedup なしで 1 タブ 1 レコード書く | 静かな hot exit で何も尋ねない | **する**(毎起動で衝突が忠実に再生される) |

  ON 構成では Task 6b は「無断破壊」を「**若い方のタブが永久に保存できない**
  (相手タブを閉じるか別名で保存するまで)」へ変換する。害としては明確な改善だが
  **新しい行き止まり状態**なので PR に明記する。
  **復元側の dedup(`:678` / `:922`)だけは follow-up に残す** — こちらは「2 つ目のタブの
  未保存内容をどう扱うか(統合 / 破棄 / 両方を無題で残す)」という §6 が正当に除外している別テーマ。
  修正前レイアウトが修正済みビルドの初回起動で衝突タブに復元される点は、v0.2 の既知事項として記載する。
- **S-10**(Task 6b で判明・2026-08-23。**仕様レビューで範囲を拡大**): **クローズ/終了が
  黙って中止される。現場は 2 つある。**
  (1) `MainForm.OnFormClosing`: 未保存確認で「はい」→ `SaveDocument` が false →
  `keepClosing=false` → `e.Cancel = true`。追加の通知は無い。
  (2) `MainForm.cs:1022` `CloseActiveTab`: `TryClose` が false のとき**裸の `return;`**。
  Ctrl+W だとエラーの後タブが黙って残る。
  どちらもエラー自体は `_prompt.Error` の MessageBox として読み上げられるが、内容は
  「**保存**が失敗した」であって「**終了/クローズを取りやめた**」ではない。
  **この挙動は Task 6b が作ったものではない** — `SaveDocument` が false を返す既存経路
  (UNC 到達不能・書込失敗など)でも同じ。ただし Task 6b は (a) **既定構成で踏める**新しい
  トリガを増やし、(b) UNC 障害と違い**自然に解消しない**、という 2 点で従来トリガより悪い。
  修正には `MainForm` のクローズループに触れる必要があり Task 6b の範囲外。
- **S-11**(同上): **重複タブの Ctrl+S に「別名で保存しますか?」の逃げ道を用意する案。**
  現在は硬いエラーで中止する。設計書 §6 の「エラーで中止が最も安全」に従った判断で、
  「このまま上書き」を許す OkCancel にすべきではない(害が**画面に出ていない他方のタブ**に及ぶうえ、
  クローズ連打中のユーザーはダイアログを流し読みする)。ただし
  `OkCancel("別の名前で保存しますか?") → SaveAsDocument(doc)` なら話が別で、
  **成功すれば Modified が下りてクローズも進む**のでクローズフローが正しく解決する。
  クローズループのキャンセル意味論と絡むため Task 6b では実装せず記録に留める。
- **S-12**(Task 7 で判明・2026-08-23。**Task 7 のレビューで根拠を差し替え、方針も ① 修正へ変更**):
  **上書き確認の既定ボタンが OK 側だった。** `IUserPrompt.OkCancel` は
  `MessageBox.Show(..., MessageBoxButtons.OKCancel, ...)` の 4 引数オーバーロードで、
  WinForms の既定は Button1 = OK。

  **起票時に「Windows 純正の `SaveFileDialog.OverwritePrompt` は『いいえ』が既定なので、
  A-7 (a) の合流で安全側の既定を失った」と書いたが、これは未検証の主張だった。**
  レビューは GUI 実行なしに検証できず、証拠はむしろ逆を指す(.NET の
  `FileDialog.PromptFileOverwrite` は `MessageBoxButtons.YesNo` で既定 `Button1` = **Yes**)。
  本ブランチでは「既定値を実物で確認せず意図から推論して外した」失敗が既に 2 回あるため
  (§10.2 と Task 6b の `RestoreOpenFilesOnStartup`)、事実として残さない。
  **実機確認は L5 の項目に落とす。**

  **根拠は純正との比較を持ち出さなくても成立する。** `SaveAsDialog.cs` は
  `AcceptButton = ok` を設定しているので、SR の主経路は
  「ファイル名を打つ → **Enter**(OK 発火)→ MessageBox が OK フォーカスで開く →
  **反射的な 2 回目の Enter でファイルが消える**」。読み上げが遅いときに Enter を連打するのは
  SR ユーザーの実際の振る舞いであり、**A-7 (a) が追加した確認が、主経路の打鍵パターン
  そのもので無力化される**。Windows の既定が何であれ成立する。

  → **Task 7 の fixup で ① 修正**。`IUserPrompt.OkCancel` に
  `bool defaultCancel = false` を足し、上書き確認だけ `true` を渡す。呼出元は
  `FileController` の 2 箇所のみ(レビューが全 grep 済み)なので波及ゼロ。
  **これは網の面でも利得がある**: `OverwritePrompt = false` の行は `SaveAsDialog` が Form で
  テスト参照ゼロのため原理的に kill 不能(§10.9)だが、`defaultCancel` は `FakePrompt` に
  記録できるので**安全側の既定が L3 の assertion になる**。L5 のみの保証を網のある保証へ変える。

  併記した文言の不整合(「上書きしますか?」に対してボタンが OK / キャンセルで
  はい / いいえではない)は**別の Minor として残す**。`IUserPrompt` に `YesNo` を足す変更は
  安全側既定の修正に見合わない。なお既定を Cancel にすると、質問を聞いた直後に
  フォーカスされたボタンとして「キャンセル」が読まれるので、不整合は緩和される方向に働く。
- **S-8**(Task 5 の fixup で発生・2026-08-23): V-1 の修正 (b) で `WriteToPath` の catch フィルタに
  `ArgumentException` を足したが、これは `ArgumentNullException` と `ArgumentOutOfRangeException` も
  一緒に握る。フィルタのコメントは「**想定内の入出力エラーのみ握る。NullReference 等のロジックバグは
  伝播させる**」と宣言しており、その方針をわずかに侵している。`LoadInto` は同じトレードオフを
  受容済み(悪意/破損 JSON 由来の path を吸収するため)なので対称ではある。
  **より根本的な修正は `AtomicFile.cs:62` の `Path.GetDirectoryName(Path.GetFullPath(path))!` にある** —
  この null 免除は嘘で、親が取れないパスに対して `ArgumentNullException` を生む。`AtomicFile` 側で
  「親が確定しないパスは保存先にできない」を `IOException` 系で明示的に弾けば、呼出側のフィルタを
  広げずに済む。ただし `AtomicFile` は Core でバックアップライターも使うため影響範囲の確認が要る。
  **最終ブランチレビューで判断する。**
- **S-7**(同上): `IsNullOrWhiteSpace` は `​`(ZWSP)/ `﻿` / `⠀`(点字空白)/ `᠎` を
  空白と見なさないので、これらだけのファイル名が保存できてしまう。実ファイルは作られるので
  喪失は無いが、**タブ名が空に見えてファイルを見つけられない**(SR では特に厄介)。
  空白判定の意味論を広げると全角空白等で別の議論を呼ぶため A-19 の範囲外とした。
- **S-13**(Task 8 で判明・2026-08-23): **上書き承諾が周回をまたがないこと(§10.11)に
  自動テストの網が無い。** `FakePrompt.OkCancelResult` は**単一の bool** で、すべての
  `OkCancel` 呼出に同じ答を返す。したがって「**上書きは承諾したが、文字コード劣化警告は
  キャンセルする**」という状況をテストで表現できず、再問いの有無を assert できない。
  **最後のコードタスクで共有テストシームを導入する**ことになり、CLAUDE.md §3 の
  「後続タスクが依存する新しい抽象・seam を導入する → コード品質レビュー」に該当するため、
  本ブランチでは足さず記録に留める。**現状の唯一の網は L5 ⑤ の後半**
  (`2026-08-23-saveas-target-validation-l5-checklist.md`)。次にこの面へ触れるときに回収する。

  **回収時の形**(Task 8 のレビューが提案・起票時の caption キー案から差し替え):
  `Dictionary<string, bool>` で caption を引くより、**`Queue<bool> OkCancelQueue`
  (空なら従来の `OkCancelResult` にフォールバック)** のほうが安く安全。加算的で既存テストを
  1 行も触らず、`FakeFileDialogService` が既に持つ `SaveAsQueue` / `SaveAs` の対と**同じ idiom**
  なので新しいパターンを持ち込まない(= §3 のトリガー自体が弱まる)。
  `[true, false]` を積めば「上書き承諾 → 劣化キャンセル」が表現でき、`OkCancelCalls` が
  `[上書きの確認, 文字コードの警告, 上書きの確認]` になることで再問いを pin できる。

  **なお、この面の網の価値はもともと低い**(同レビューの指摘): 再問いは「コード」ではなく
  **「状態の不在」**である。承諾を保持するフィールドも変数も存在しないので、退行させるには
  誰かが意図的にキャッシュ機構を**追加**するしかなく、それは独自のレビューを伴う大きな編集になる。
  事故で壊れうる挙動ではない。

- **S-14**(Task 8 の fixup 中に付随発見・2026-08-23。**本ブランチ固有ではなくリポジトリ横断**):
  **XML doc の `cref` が一切検証されていない。** `GenerateDocumentationFile` が
  `Directory.Build.props` にも csproj にも `.editorconfig` にも設定されていないため、
  **CS1574(解決できない cref)が永久に出ない**。
  `-p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false` で建てると
  **既存の壊れた cref が 12 件**出る(Accessibility / App / Core / Editor と
  `MainFormSmokeTests.cs`。うち 1 件は `FileController.cs` の `SetOrReplaceSource`)。
  このコードベースは `<see cref="..."/>` による相互参照を多用する書き方なので、
  **参照先を消しても改名しても誰も気づかない = コメントが静かに腐り続ける**。
  対処は「プロパティ 1 つ + CS1591(未文書化メンバー)の抑止」で済むが、
  ソリューション全体のビルドプロパティ変更なので本ブランチでは扱わない。
  なお本ブランチが**追加した** cref は 12 件のリストに入っていない(確認済み)。

## 10. 実施記録

本節は**実施記録の追記**(CLAUDE.md §8 が認める)。§1〜§8 は策定時のまま。
§9 の申し送りリストだけは実装中に S-5〜S-13 を追記している(策定時は S-1〜S-4 のみ)。

### 10.0 §7.3 の P4 は書いていない(忘れたのではない)

§7.3 のテスト表にある **P4 `ProbeSaveTarget_ZeroTimeout_ReturnsUnreachable` は実装していない。**
`task.Wait(TimeSpan.Zero)` は「タスクが先に完了する」レースを持ち、確実に false にできないため
(フレーキーなテストを増やすより書かない、という判断)。

タイムアウト経路そのものは**別の形で被覆済み**で、無被覆のまま残ってはいない。
`RunSaveTargetProbe` / `RunFileExistsProbe` に `work` を注入し、完了しない
`TaskCompletionSource` で止めた `work` を渡すことで、両側のフェイルセーフ値を決定的に pin した。
経緯は §10.3。

### 10.1 seam の名前が変わった(Task 1 のコード品質レビュー)

§3.2 のコードスケッチは策定時のまま残してあるので、読むときは次の対応で読み替えること。

| 策定時 | 実装 | 変更理由 |
|--------|------|----------|
| `SaveTargetProbe` | `SaveTargetProbeResult` | 「probe が probe を返す」形になっていた。同フォルダーの先例は全部 `Result` / `Outcome` 接尾(`SaveAsResult` / `RestoreOutcome` / `CellPickResult`) |
| `IReachabilityProbe.ProbeWithTimeout` | `ProbeFileExistsWithTimeout` | **A-4 の機構は「到達性の名前で存在確認を実装したメソッドを、書き込み側が名前を信じて再利用した」こと**。正しい名前のメソッドを足すだけでは罠が立ったまま残る(doc は一度しか読まれず、名前は毎回読まれる) |
| `FileController.TryProbeReachability` | `TryProbeFileExists` | 同じ理由。1 段上の私有ヘルパにも同型の問題があり、`TryInspectSaveTarget` と並ぶと対比が紛らわしい |

あわせて `TryProbeFileExists` の doc から「`LoadInto` / `WriteToPath` 双方から共有し『Save と Load で
同じ到達性ポリシー』を 1 箇所で表現する」という一文を削除した。**これは次の読者に読み取り側の述語を
書き込み側と再共有するよう勧める文面で、A-4 の発生源そのものだった。**

実装は §3.2 に無い internal seam を 2 本持つ(`WaitBounded<T>` / `RunSaveTargetProbe` /
`RunFileExistsProbe`)。タイムアウト経路のフェイルセーフ値を決定的にテストするためで、経緯は 10.3。

### 10.2 §3.3 が挙げたゲート維持の根拠は誤りだった(Task 2 の実測)

§3.3 は「リモートゲートを外すと、既存テスト `SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath`
が `WriteToPath` に到達しなくなる」と書いた。**これは二重に誤っている。**

1. テストは `FakeReachabilityProbe` を注入し、その既定 `SaveTargetResult` は `Reachable: true`。
   したがって**ゲートを外してもこのテストは緑のまま**で、変異を kill しない(実測で確認)。
2. **実プローブを使った場合でもロールバック導線は失われない。** `SaveAsDocument` の
   Encoding / HasBom / LineEnding / Path のロールバックは `WriteToPath` の **false 戻り値**で駆動される。
   ゲートを外して実プローブが `Reachable=false` を返しても `WriteToPath` は false を返すので
   ロールバックはそのまま発火する。しかも短絡は `ApplyEol` / `ConvertEols` より手前なので、
   本文側の巻き戻し対象すら発生しない。実際に壊れるのは assertion 1 行だけ。

**ゲート維持という判断は変えない。真の理由は次のとおり:**

- **決定的な理由 = 誤ったエラーメッセージ。** `ReportUnreachable` の文言は「ネットワークパスに
  到達できません」の 1 種類しかない。ゲートを外すと、ローカルの存在しないフォルダー配下への保存に
  対してこの文言が出る。**SR ユーザーが直入力でタイプミスした典型ケースがこれ**で、現行の
  `DirectoryNotFoundException` 由来の「保存できませんでした: 指定されたパスが見つかりません」より
  明確に劣化する。文言を分岐させれば直るが、それは §6 が YAGNI として除外した範囲。
- 副次的理由: 挙動変更であること(CLAUDE.md §2)、Ctrl+S ごとに `Task.Run` + `Wait` が 1 回増えること。

**このゲートを実際に守っているテストは `Save_SkipsProbe_ForLocalPath` と
`SaveAs_LocalNewFile_DoesNotProbe` の 2 本。** 実装計画 Task 2 Step 5 の「(ロールバックテストが)
赤ならリモートゲートを外してしまっている」という安全確認は**機能しない検査**なので、
その手順に従わないこと。

**手法上の教訓**: Fake を注入するテストは「本番の実装が持つ性質」を証人にできない。
設計書に「この変更はこのテストが守る」と書くときは、そのテストが**実際に何を注入しているか**まで
遡って確かめる。

### 10.3 抽象化が網を弱めた(Task 1 のレビュー往復)

「タイムアウト時のフェイルセーフ値が無被覆」という指摘に応えて、境界付き待ちの判断を
`WaitBounded<T>` に集約した。**集約は達成したが、片方の定数が以前より壊しやすくなった。**

- 集約前 `task.Wait(timeout) && task.Result` … フェイルセーフが `&&` の短絡に**構造的に埋め込まれて**
  いた。`||` に変異させるとタイムアウト済みタスクの `task.Result` を読んで**ブロックする**ので、
  静かには壊せない。
- 集約後 `WaitBounded(task, timeout, false)` … 定数が 1 トークンの引数になり、`true` に書き換えても
  **コンパイルが通り・ハングもせず・全緑**になる(実測で生存)。帰結はタイムアウトを
  「ファイルは在る」と読んで実 read へ進むこと = HIGH-6 の 60 秒凍結の再導入。

最終的に `RunSaveTargetProbe` / `RunFileExistsProbe`(`work` を注入できる internal seam)を足し、
完了しない `TaskCompletionSource` で止めた `work` を渡すことで、両側のフェイルセーフ値を
決定的に pin した。

**教訓: 「重複を消す」リファクタは、消した重複が網の役目を兼ねていないかを変異で確かめてから入れる。**

### 10.3b アナライザが網になった副産物(Task 6)

Task 5 は `seed = seed with { Path = full };` を将来のために残したが、その時点では以降の全経路が
`return` するため**証明可能な dead store** で、`-warnaserror` を通すために `#pragma warning disable S1854`
を置いた。Task 6 が重複タブ分岐の `continue` をその直後に入れて assignment が live になったので
抑止を削除した。

結果として **S1854 が Task 6 のガードのコンパイル時ガードになった**(レビューが独立に 2 回実測):

- ガードを丸ごと削除する変異 → `error S1854` でビルド不能
- `continue` を `return false` に変える変異 → 同じ行で同じエラー

**ガードの存在と、その `continue` であることの両方**が、テストとは独立にコンパイラで固定されている。
抑止を残していればこの信号は永久に隠れていた。**「将来のために残す行」に抑止を置くときは、
抑止が消える条件を計画に書いて回収する**(本件は計画 Task 6 に明記して回収した)。

なお副作用として、抑止を消すと CSharpier が `seed = seed with { Path = full };` を 3 行の
初期化子へ整形する(CSharpier はプリプロセッサ指令に隣接するコードを整形しない)。
tool 由来の差分であって挙動変更ではない。

### 10.4 受容した無被覆(known-unkillable)

最終品質パスがミューテーションスコアを誤読しないよう記録する。

| 箇所 | 変異 | 状態 |
|------|------|------|
| `FileReachabilityProbe` の `!string.IsNullOrEmpty(dir) &&` | ガードごと削除 | **等価変異**。`Directory.Exists(null)` も `("")` も false を返すので観測可能な差が出ない。ガードが守る挙動(ルートは保存先でない)自体は `DriveRoot` が pin 済み |
| 両プローブの `catch` 節の フェイルセーフ | 戻り値の変更 | **原理的に到達不能**。.NET Core 以降の `File.Exists` / `Directory.Exists` はマネージドコードから構成できるどんな入力でも投げない。「書き忘れ」ではなく「書けない」 |
| public プローブメソッドから seam への `timeout` の受け渡し | 受け取った値を捨てて固定値にする | **原理的に pin 不能**(実 I/O の所要時間を制御できない)。上流半分は Task 2 の 5 秒 pin テストが守る |

`!IsNullOrEmpty(dir) && Directory.Exists(dir)` に対する変異は 3 種あり、**それぞれ別のテストが kill する**
(台帳に書くときは「どの変異か」を式ごと書くこと。「ガードの変異」では特定できない):

| 変異 | kill するテスト |
|------|-----------------|
| 否定反転 `!IsNullOrEmpty(dir)` → `IsNullOrEmpty(dir)` | `NewNameInExistingDir` / `ExistingDirectory_ReportedAsNewFile_CurrentBehavior` |
| `&&` → `\|\|` | `UnderMissingDir` |
| `GetDirectoryName(path) ?? path` | `DriveRoot` |
| ガードごと削除 | 生存(上記のとおり等価) |

### 10.5 ミューテーションを当てる際の注意(実測で判明)

- **`dotnet test --no-build` はビルド失敗後に古いバイナリを走らせて緑を報告する。** 本ブランチだけで
  捏造された「変異が生存」が 3 回発生した。`ビルドに成功しました` を確認してから走らせること。
- **計画が例示した形の変異は `-warnaserror` でビルドが通らない**ものがある
  (`WriteToPath` の直接差し替え = S1144 / ゲートを `if (true)` にする = CS0162 /
  `path.Length >= 0` = RCS1215・S3981)。挙動等価な別形で当てること。
- 変異で変数が未使用になるとビルドが落ち、上記 1 点目と組み合わさって偽の「生存」を作る。

### 10.6 S-5 の判断: (a) 現状維持(Task 7)

`TryInspectSaveTarget` のローカル枝は素の `System.IO.File.Exists` のまま残す。
根拠は 3 つで、うち 2 つは S-5 起票時の見立てを**実装を読んだうえで訂正**するもの。

1. **S-5 が想定した「相殺されない凍結」は、実際にはほぼ発生しない。** 上書き確認へ到達する
   条件は `exists == true`、すなわち `File.Exists` が **true を返しきった**ことである。
   60 秒級の凍結は切断済み SMB のセッションタイムアウトで、**false で終わる**ので確認は出ず、
   したがって「いいえ」も選べない。Task 7 が現に増やす露出は
   「SaveAsDocument 段の 1 回」= `WriteToPath` 冒頭で既に走っていた同じ呼出の 2 倍化であって、
   新しい凍結クラスではない(ループでの反復も、`continue` が毎回 `PickSaveAs` を挟む以上
   ユーザー操作 1 回につき 1 回)。
2. **(b) はフェイルセーフの向きを反転させる。** `RunSaveTargetProbe` のタイムアウト時の戻り値は
   `(Reachable: false, FileExists: false)` で、これが安全なのは **`Reachable` を先に見て短絡する**
   から(= `SaveTargetProbeResult` の契約)。(b) は「`Reachable` を無視して `exists` だけ採る」ので、
   タイムアウトが「ファイルは存在しない」= **上書き確認をスキップする**と読まれる。
   まれな凍結を、本ブランチが直そうとしている当の欠陥(無確認の上書き)と交換することになる。
3. **網が弱まる。** (b) にすると A-7 (a) の全テストが「実ファイルがディスクに在る」ではなく
   「Fake がそう申告した」を入力にする。加えて `SaveAs_LocalNewFile_DoesNotProbe`
   (`SaveTargetCallCount == 0`)が成立しなくなる = §3.3 のリモートゲートを守る 2 本のうち 1 本を
   失う。

残存リスク(固定ドライブ上のジャンクション/シンボリックリンクがネットワーク先を指す場合に
`File.Exists` が上限なくブロックしうる)は **PR description に記載して受容**する。
根治するなら `RemotePathDetector` 側で reparse point を解決する話であり、読み取り側
(`TryProbeFileExists` / `FileMetaProvider` / `FileTimestampProvider`)にも同じ穴があるので、
本ブランチの範囲ではなく横断テーマとして扱う。

### 10.7 SaveAs のリモートプローブは 2 回(Task 7 で実測)

計画 Task 7 の Step 5 は `SaveAs_UncPath_ProbesSaveTargetWithFiveSecondTimeout` へ
`Assert.Equal(1, host.Probe.SaveTargetCallCount)` を足すよう指示していたが、**実測は 2**。
§5 が明記しているとおり SaveAs 経路では事前判定(§4.1 (4))と `WriteToPath` 冒頭の
自己完結ガードで 1 回ずつ走る(`WriteToPath` は Ctrl+S が直接入る入口でもあるため自己完結を
崩さない、という §5 の判断の帰結)。テストは 2 で pin し、「1 に直さないこと」を doc に書いた。
1 往復であることの pin は `PickSaveAsCount == 1` と「OkCancel が Log に出ない」で担保する
(回数だけでは、確認が出て既定 `OkCancelResult = true` で続行した場合と区別できない)。

### 10.8 追加の意図的な挙動変更(Task 7・§4.5 の表に無いもの)

**自分自身のパスへ「名前を付けて保存」すると、上書き確認が出るようになった。**
§4.5 の表は策定時のまま残すので、ここに記録する(CLAUDE.md §2)。

レビューの判定は**妥当**。`参照` 経由の `SaveFileDialog` は `OverwritePrompt` が既定 true で、
native prompt は「今そのタブで開いているファイル」を除外しないため、**従来から自分のパスでも
確認していた**。したがってこれは新しい負担ではなく、A-7 (a) が訴えている非対称の解消そのもの。
自分のパスへの SaveAs は通常「文字コードや改行を変えるため」に行うので、確認が効く場面でもある。
Ctrl+S は従来どおり確認しない(`WriteToPath` は `exists` を捨てる)。

### 10.9 原理的に pin できない production 行(Task 7)

`SaveAsDialog.cs` の `OverwritePrompt = false` は**自動テストで kill できない**。
`SaveAsDialog` は `Form` で、`tests/` からの参照が**ゼロ**(レビューが全 grep で確認)。
この行を消しても全ソリューション 2173 件が緑のままになる。**唯一の網は L5 ④**
(参照経由で既存ファイルを選び、確認が 1 回だけ出ることを実機で確認する)。

**L5 を実施するまで、この行は無保護であると認識すること。** CSharpier や整理で消えても
自動テストは何も言わない。§10.4 の known-unkillable と同じ枠だが、あちらが「防御コードで
到達不能」なのに対し、こちらは「production の挙動なのにテスト層が届かない」点で質が違う。

### 10.10 アナライザが網になった 2 例目(Task 7)

§10.3b の S1854 に続き、**S1006 が「インターフェースと実装の既定引数値のずれ」の網**になっている
ことが判明した(S-12 の `defaultCancel` に変異を当てて発見)。既定値を片方だけ書き換える変異は
`error S1006: Use the default parameter value defined in the overridden method` でビルドできない。

`IUserPrompt` / `MessageBoxUserPrompt` / `FakePrompt` の 3 箇所を揃えて変異させる形が必要で、
逆に言えば**実装が interface の既定値から黙って逸れることはコンパイラが許さない**。
既定引数を持つ seam を足すときに覚えておく価値がある。

**手法上の注意**: 変異がビルドできないとき、`--no-build` は前回の変異のバイナリで走って
**古い結果を報告する**(Task 7 で実際に発生)。「ビルド失敗 → 前の結果」は
「変異が生存した」と見分けがつかない。§10.5 の再掲になるが、ビルド出力を先に見ること。

### 10.11 追加の意図的な挙動変更(Task 8)— 上書き承諾は周回をまたがない

**上書き確認を承諾した後に文字コード劣化警告をキャンセルすると、次の周回で上書き確認が
もう一度出る。** 実装中に判明した意図的な挙動変更なので記録する(CLAUDE.md §2)。
§4.5 の表は策定時のまま残すので、§10.8 と並べてここに置く。

**そうした理由**: 承諾は「**その周回で選ばれた保存先**」に対する答である。再表示ではパスを
打ち替えられるので、承諾を持ち越すと前の周回の「`a.txt` を上書きしてよい」が、
打ち替え後の `b.txt` の上書きを黙って許すことになる。**A-7 の非対称(主経路だけが無確認)を
別方向から再導入する**のと同じで、本ブランチが直そうとしている当の欠陥に戻る。

**採らなかった安価な代替(記録のみ)**: 上書き確認を**文字コード検査の後ろ**へ移す。
そうすれば劣化警告をキャンセルした周回はそもそも上書き確認に到達しないので、再問い自体が
起きない。実装しなかったが、再問いが煩わしいという声が出たときの最も安い選択肢である。
採るときは判定順を変えることになるので、§4.2 の根拠(安いローカル判定 → I/O を伴う判定 →
内容に関する警告)を読み直すこと。

### 10.12 Task 8 の網は 1 本の assert だけが固有(Task 8)

`SaveAs_EncodingWarningDeclined_ReopensDialog` が持つ 4 つの assert のうち、
**Task 8 が入れた `continue` を pin しているのは `PickSaveAsCount == 2` の 1 本だけ**である。
`continue` を Task 8 以前の `return false` へ戻す変異を当てると、
**落ちるのはこのテスト 1 本だけで、他は 1 件も落ちない**(実測)。

残る 3 つ(`文字コードの警告` が Log に出る / ファイルが書かれない / `State.Encoding` が
65001 のまま)は `SaveAs_LossyEncoding_CancelKeepsStateAndWritesNothing` と**重複**しており、
この変異では死なない。テストを整理する人が「重複だから」と `PickSaveAsCount` ごと削ると
**Task 8 は無保護になる**ので、消さないこと。

### 10.13 再問いの挙動には自動テストの網が無い(Task 8 → 申し送り S-13)

§10.11 の再問い(承諾 → 劣化警告キャンセル → 次の周回で上書き確認が再度出る)は
**自動テストで表現できない**。`FakePrompt.OkCancelResult` は単一の bool で、
`OkCancel` の呼出すべてに同じ答を返すため、「上書きは OK・文字コード警告はキャンセル」という
**答を呼出ごとに変える**状況が作れない。

caption をキーにした応答テーブルを `FakePrompt` に足せば表現できる(6 行程度)。
足さなかったのは、**最後のコードタスクで共有テストシームを導入する**判断になり、
CLAUDE.md §3 の前倒しコード品質レビューを要するため。**申し送り S-13** として記録した。
**現時点の唯一の網は L5 ⑤ の後半**(実機で承諾 → 劣化警告キャンセル → 再問いを目視する)。

### 10.14 停止性 — 完成したループについての再掲(Task 8)

`continue` が全部で **6 箇所**になったので、Task 5 時点の停止性の議論をここで確定させる。

| # | `continue` する分岐 | 導入 |
|---|---------------------|------|
| 1 | 空白パス | Task 5 |
| 2 | 正規化失敗 / 親フォルダーが取れない(V-1) | Task 5 |
| 3 | 重複タブ(A-7 (b)) | Task 6 |
| 4 | 到達不能(A-4) | Task 5 |
| 5 | 上書き確認の辞退(A-7 (a)) | Task 7 |
| 6 | 文字コード劣化警告の辞退 | Task 8 |

- **6 箇所すべてが `while (true)` の先頭に戻り、その最初の文は `PickSaveAs` である。**
  分岐を足すときはこの性質を壊さないこと(`continue` の前に別のループを挟まない)。
- **本番では、モーダルダイアログの OK / キャンセルを人が押さない限り 1 周も回らない。**
  ユーザー操作を挟まずに回る経路は存在しない。
- **テストでの停止保証は `FakeFileDialogService.SaveAsQueue` の枯渇が `null`
  (= キャンセル)を返すこと、ただ 1 つである。** Task 8 で劣化警告が `continue` になった今、
  「同じ入力 → 警告 → キャンセル → 再表示」は自力では終わらない。
  → **`SaveAsQueue` に「最後の値を繰り返す」モードを絶対に足さないこと。**
  足した瞬間に、網の書き間違いが「`PickSaveAsCount` が想定と違う」という失敗ではなく
  **CI を固める無限ループ**になる(§7.2 がこの fake を設計した理由そのもの)。
