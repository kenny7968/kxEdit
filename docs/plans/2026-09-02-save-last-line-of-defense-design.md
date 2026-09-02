# B4: 保存の最終防衛線(M-12 / M-13 / M-11) 設計書

策定日: 2026-09-02
出典: `2026-08-31-v0.2-remaining-work-design.md` §4 の B4
対象 ID: M-12(唯一のコピー削除)/ M-13(rename 前の Flush なし)/ M-11(設定の非原子的保存と無音リセット)

## 1. 対象

`AtomicFile` は kxEdit の全永続化(文書保存・バックアップ・セッションレイアウト)が通る
唯一の書込点である。ここが守る約束は「**どの段階で失敗しても、ディスク上のデータを失わない**」。
本ブランチはこの約束が破れる 3 か所を塞ぐ。

| ID | 破れ方 | 場所 |
|----|--------|------|
| M-12 | 差替の部分失敗で原本が消えたとき、唯一残ったコピー(tmp)を自分で消す | `Core/IO/AtomicFile.cs:46-50` / `:88-92` |
| M-13 | rename 前にディスクへ flush していない=保存直後の電源断で不完全ファイルが残りうる | `Core/IO/AtomicFile.cs` 両 `Write` のステージング段 |
| M-11 | 設定だけ `AtomicFile` を通っておらず非原子的。さらに破損時は無言で既定値へ戻り、次の保存で確定する | `Core/Settings/SettingsStore.cs:130-135` / `:20-32` |

**対象外**: M-14(フォルダ作成権限が無い ACL で保存不能)は §5 の申し送りに残っている別項目で、
本ブランチでは触らない。M-20(`SerialBackupWriter` が書込失敗を知らせない)と M-22(設定保存失敗でも
成功発声)は B5 の担当。**本ブランチは「失敗を起こさない / 失敗しても失わない」側だけを扱い、
「失敗をどう伝えるか」は M-11 の破損通知を除いて B5 へ渡す。**

## 2. 現状の機構(実コードで確認)

両 `Write` は同じ二段構えで書かれている。

```
① tmp( "<ファイル名>.<乱数>.tmp" )へステージング書込
   失敗 → TryDelete(tmp); throw   … 原本は無傷。正しい。
② File.Exists(path) ? File.Replace(tmp, path, destinationBackupFileName: null)
                    : File.Move(tmp, path)
   失敗 → TryDelete(tmp); throw   … ここが M-12。
```

`②` の catch は**原本が無傷である場合と、原本が消えている場合を区別していない**。
`destinationBackupFileName: null` を渡しているため Windows 側のバックアップも作られない。
つまり「原本が消えていた」ケースでは、tmp が**ディスク上の唯一のコピー**であり、それを消している。

xmldoc(`:6-7`)は「どの段階で失敗しても原本には一切触れず、tmp の掃除だけ試みて例外を伝播する
(= 原本喪失の回避が目的)」と書いており、**この記述自体が ② の部分失敗を織り込んでいない**。
文言も本ブランチの修正対象とする。

### 2.1 `File.Replace` の部分失敗について(想定・未実測)

Win32 `ReplaceFile` の仕様では、`ERROR_UNABLE_TO_MOVE_REPLACEMENT` は
「置換先はもう存在せず、置換ファイルは元の名前のまま残っている」状態を意味する。
これは §2 で述べた「tmp が唯一のコピー」と一致する。

**ただし本設計はこのエラーコードに依存しない。** 監査 §9 V-7 の教訓
(前置ガードの列挙は原理的に漏れる。事後条件で検査する)に従い、**どのエラーで失敗したかではなく、
失敗後にディスクがどうなっているかで分岐する**。上記は「なぜその状態が起こりうるか」の説明であって、
判定条件ではない。ローカルでの実測はしていない(実環境で決定的に起こせないため。§6 参照)。

### 2.2 `AtomicFile` を通っていない書込

| 呼出元 | 経路 |
|--------|------|
| `Core/Text/TextFileService.cs:340` / `:375` | 文書保存(byte[] 版 / Stream 版) |
| `Core/Backup/BackupStore.cs:49` | バックアップ JSON |
| `Core/Session/SessionLayoutStore.cs:125` | セッションレイアウト |
| **`Core/Settings/SettingsStore.cs:135`** | **通っていない。`File.WriteAllText` 直書き**(M-11) |

## 3. M-12 の設計 —— 事後条件で判定し、復旧を試みてから残す

### 3.1 判定

差替の**前**に原本の有無を採る。

```
bool existedBefore = File.Exists(path);
try { ... Replace / Move ... }
catch { /* ここで existedBefore と File.Exists(path) の組で分岐する */ }
```

| `existedBefore` | 失敗後の `File.Exists(path)` | 意味 | 処置 |
|---|---|---|---|
| true | true | 原本は無傷 | tmp を消す(**従来どおり**) |
| true | false | **原本が消え、tmp が唯一のコピー** | 復旧を試み、駄目なら tmp を残す |
| false | — | 新規作成の失敗。失うものは無い | tmp を消す(**従来どおり**) |

`existedBefore == false` を分けるのが要点である。新規作成経路(`File.Move`)の失敗でも
`!File.Exists(path)` は真になるが、そこには失われた原本が無い。事後条件だけで分岐すると
**残骸を残すだけの誤検出**になる。

### 3.2 復旧

`existedBefore && !File.Exists(path)` のとき、`File.Move(tmp, path)` を 1 回だけ試みる。

- **成功** → 保存は成立している。`Write` は正常 return する(呼出側から見て成功)。
- **失敗** → tmp を**消さずに**、`AtomicReplaceFailedException`(`IOException` 派生・
  `PreservedTempPath` プロパティを持つ)を投げる。元の例外は `InnerException` に入れる。

復旧を試みるのは、その時点でユーザーが望んでいた最終状態(= tmp の内容が `path` にある)へ
一手で到達できるからである。ここで諦めて例外にすると、ユーザーは自分で tmp をリネームする
ことになる —— 復旧手順として正しいが、アプリが代行できるものを人にやらせている。

### 3.3 受容するトレードオフ —— ACL / 属性の非継承

`File.Replace` は置換先の ACL・属性・作成日時を引き継ぐが、`File.Move` は引き継がない。
復旧が成功した場合、そのファイルは**元の ACL ではなく、置かれたディレクトリの継承 ACL** を持つ。
元ファイルに個別の(より厳しい)ACL が設定されていた場合、復旧は**権限を広げる方向へ倒す**。

これを受容する。比較しているのは「権限が広がったファイルが残る」と「ファイルが消える」であり、
後者の方が回復不能だからである。**この判断は最終レビューの脆弱性パスへ明示的に回す**
(CLAUDE.md §3-4 の前倒しレビュー該当: 外部入力ではないが、セキュリティ属性の変化を伴う)。

復旧成功時に無言で return することも受容する。保存は実際に成立しており「保存しました」は
虚偽ではない。ACL 変化を毎回警告すると、実際にはほぼ起きない事象で通常の保存体験を汚す。
**xmldoc に明記して、コードを読む側には見えるようにする。**

### 3.4 in-place フォールバックへ流さない

`TextFileService.cs:342` / `:448` は `catch (IOException ex) when (AtomicFile.IsShareOrLockViolation(ex))`
で in-place 上書きへ落ちる。`AtomicReplaceFailedException` は `IOException` 派生なので、
**HResult が共有/ロック違反(0x80070020 / 0x80070021)と一致しないこと**を保証する必要がある。

一致してしまうと、原本が消えた後に `File.WriteAllBytes(path, payload)` で書き直すことになる。
結果的に書けるかもしれないが、それは §3.2 の復旧と**同じ仕事を別経路で二重にやる**設計であり、
どちらが効いたのかテストからも実機からも判らなくなる。`AtomicReplaceFailedException` は
自前の HResult(既定の `IOException` 値)を持たせ、フォールバック条件に当たらないようにする。

**これは網で固定する**: `IsShareOrLockViolation(new AtomicReplaceFailedException(...)) == false`。

## 4. M-13 の設計 —— `Flush(flushToDisk: true)`

### 4.1 変更

- **Stream 版**: `writer(fs)` の直後・`using` を抜ける前に `fs.Flush(flushToDisk: true)`。
- **byte[] 版**: `File.WriteAllBytes(tmp, payload)` を
  `new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)` +
  `Write` + `Flush(true)` に置き換える。Stream 版と同じ形へ揃う。

`FileMode.CreateNew` は既存ファイルがあると失敗する。tmp は `Path.GetRandomFileName()` 由来なので
衝突は事実上起きず、起きたなら上書きするより失敗する方が正しい(他者の書込中ファイルを潰さない)。
Stream 版が既に `CreateNew` を使っており、**両版の差を 1 つ減らす**変更でもある。

### 4.2 適用範囲

`AtomicFile` の全書込に効かせる —— 文書保存・バックアップ・セッションレイアウト・(§5 で)設定。
**電源断で最も惜しいのは、まだ保存されていない編集内容を持つバックアップ側**である。
バックアップ書込は `SerialBackupWriter` のワーカースレッド上なので UI は止まらない。

代償: 終了時の `SerialBackupWriter` の Join(15 秒。`SerialBackupWriter.cs:203`)の余裕が減る。
**実装時に大きめの文書で保存時間を実測し、実施記録へ数値を残す**(L4 の性能ゲートには載せない。
これは回帰監視ではなく、受容判断の根拠として 1 回測るもの)。

### 4.3 保証できないこと —— ディレクトリエントリ

Windows には**ディレクトリのメタデータを明示的に flush する API が .NET から無い**。
`Flush(true)`(= `FlushFileBuffers`)が保証するのは「そのファイルの中身がディスクに届いたこと」で
あって、「その後の rename がディスクに届いたこと」ではない。

したがって本修正が消すのは「**rename されたファイルの中身が不完全**」という失敗であって、
「rename 自体が失われる」失敗は残る。後者が起きた場合、原本は無傷のまま残る(= データ喪失ではなく
保存の取りこぼし)。**この限界を xmldoc に書く。** 書かないと、次に読む人が
「原子書込 + fsync だから電源断に強い」という実際より強い主張を持つ。

## 5. M-11 の設計 —— 設定の原子化と、破損の可視化

### 5.1 保存の原子化

`SettingsStore.Save` を `AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings, Options))`
へ置き換える。`Directory.CreateDirectory(dir)` は残す(`AtomicFile` はディレクトリを作らない)。

**ディスク上のバイト列は変わらない**はずである —— エスケープ(`\uXXXX`)や整形は
`JsonSerializerOptions` が決めており、書き手(`File.WriteAllText` か `SerializeToUtf8Bytes` か)は
関与しない。どちらも UTF-8・BOM なし。**この「変わらない」は実装時にテストで固定する**
(日本語を含む設定を書き、期待バイト列と一致することを見る)。想定のまま進めない。

### 5.2 読込の 4 状態

現状の `Load` は `try { ... } catch { return new AppSettings(); }`(`:20-32`)で、
**「ファイルが無い」「壊れている」「読めない」を同じ結果に潰している**。

| 状態 | 条件 | 通知 | 退避 |
|------|------|------|------|
| `Ok` | 読めて、パースできた | しない | しない |
| `Missing` | ファイルが無い | **しない**(初回起動が該当) | しない |
| `Corrupt` | 読めたが JSON として解釈できない / `null` になる | **する** | **する** |
| `Unreadable` | I/O で読めない(ロック・権限) | **する** | **しない** |

`Corrupt` に「`Deserialize` が `null` を返す」(ファイル内容が `null` の 4 文字)を含める。
現状は `?? new AppSettings()` で成功扱いだが、**設定が失われる点では破損と同じ**である。

`Unreadable` を退避しないのは、**中身が正常なファイルを改名してしまうから**である。
読めなかった理由は AV や同期ソフトの一時的なロックであることが多く、その場合ファイルは無傷である。

### 5.3 シグネチャ

```csharp
public static AppSettings Load(string path, out SettingsLoadStatus status)
```

**status を落とせるオーバーロードは残さない。** 残すと、将来の呼出側が黙って信号を捨てられる
状態が復活する —— CLAUDE.md §6 / Issue #48 の教訓「網に見えるがゲート上は無効」と同型の、
**嘘の安全宣言**である。既存のテスト 23 か所は `out _` になる。呼び出しごとに
「ここでは status を見ない」が明示的に書かれることになり、レビューで見える。

`Save` のシグネチャは変えない。

### 5.4 退避と通知の配線

- **退避**: `Corrupt` のとき、`Program.Main` が `SettingsStore.QuarantineCorrupt(path)` を呼ぶ。
  `settings.json` → `settings.json.bad`(`File.Move(overwrite: true)`。最新の破損コピーだけ残る)。
  **`Load` に副作用を持たせない** —— `Load` は判定して返すだけにし、ディスクを書き換える操作は
  呼出側に明示的に書かせる。退避自体が失敗しても起動は続行する(戻り値で成否を返す)。
- **通知**: `Program.Main` が結果を `MainForm` の ctor へ渡し、`MainForm.OnShown`(`:261`)が
  `MessageBox` を 1 個出す。既存の `ShowStaleBackupWarning`(`:424`)と同じ形にする
  —— パスは `SanitizeForDisplay.OneLine`、`MessageBoxIcon.Warning`、テスト用の抑止 seam
  (`_suppressRestoreDialogsForTest` と同じ方式)を持たせる。

`OnShown` を回収点にするのは、**`Program.Main` の時点では通知手段が無い**からである
(`SettingsStore.Load` は `MainForm` 生成より前・`Program.cs:20`)。既に
「復元後の集約警告 1 個」という同じ形が `OnShown` にあり、新しい機構を足さずに済む。

文言は 3 系統:

| 状態 | 文言の骨子 |
|------|-----------|
| `Corrupt`(退避成功) | 設定ファイルが壊れていたため既定値で起動した。壊れたファイルは `<path>` に退避した |
| `Corrupt`(退避失敗) | 設定ファイルが壊れていたため既定値で起動した。**設定を変更すると上書きされる** |
| `Unreadable` | 設定ファイルを読めなかったため既定値で起動した。**設定を変更すると上書きされる** |

### 5.5 `Unreadable` でも設定の保存は止めない

止める案を検討して**採らない**。止めると、ユーザーが設定を変更したとき
`MainForm.cs:1046-1047` が「設定を適用しました」と発声しながら永続化しない状態になる。
これは B5 が潰そうとしている虚偽発声(M-22)を、**B4 が別の場所に新設する**ことになる。

代わりに §5.4 の文言で「設定を変更すると上書きされる」と先に伝える。起動時 1 回・
ユーザーが操作する前に伝わるので、判断の材料としては足りている。

## 6. テスト

### 6.1 M-12 —— 差替段の seam

`File.Replace` の部分失敗は実環境で決定的に起こせない。差替段を `internal static` の
デリゲート seam にして、テストから「**例外を投げ、かつ原本を消す**」偽装を注入する。
`kxEdit.Core` は `InternalsVisibleTo` で `kxEdit.Core.Tests` に開いている(`kxEdit.Core.csproj:12`)。

固定する分岐(byte[] 版 / Stream 版の**両方**で):

| # | 状況 | 期待 |
|---|------|------|
| 1 | 原本あり・差替失敗・原本が消える・復旧成功 | `Write` は正常 return。`path` に新内容がある。tmp は残らない |
| 2 | 原本あり・差替失敗・原本が消える・復旧も失敗 | `AtomicReplaceFailedException`。**tmp が残っている**。`PreservedTempPath` がその tmp を指す |
| 3 | 原本あり・差替失敗・原本は残る | 従来どおり例外。**tmp は消える**。原本は不変 |
| 4 | 原本なし(新規)・`Move` 失敗 | 従来どおり例外。**tmp は消える** |
| 5 | `IsShareOrLockViolation(AtomicReplaceFailedException)` | `false`(§3.4) |

**#3 と #4 は既存の実失敗経路で押さえられており、seam 無しでも回る**:
保存先の ReadOnly 属性 → `UnauthorizedAccessException`(`FileControllerTests.cs:1620` ほか)、
同名ディレクトリ → `File.Move` 失敗(`SerialBackupWriterTests.cs:231`)。
**seam が実物とずれていないことの担保**として、この 2 経路の既存テストが緑のままであることを見る。

seam は `internal` に留め、production からは既定実装しか通らない
(**seam 自体が production の分岐を増やさない**ことを確認する)。

### 6.2 M-13 —— flush

`Flush(true)` が実際にディスクへ届いたことは自動テストでは検証できない(電源を落とせない)。
固定できるのは次の 2 点に留まる。**これを「fsync の網がある」と書かないこと。**

- byte[] 版の書込が `CreateNew` になった(既存 tmp があると失敗する)
- 保存後の内容・原子性の既存テストが全て緑のまま(挙動不変)

「flush を呼んでいること」を網で固定したい場合、それはモック検証であり
`Flush(true)` を `Flush()` に退化させる変異しか殺せない。**§4.3 の限界と併せて、
実施記録に「ここは網が張れない」と書く**([[net-absence-claims-are-also-verifiable]] の逆側:
張れる網を張れないと言うのは嘘だが、張れない網を張ったと言うのも嘘である)。

### 6.3 M-11

- `Load` の 4 状態: 各状態を作って `status` を確認。`Corrupt` は「壊れた JSON」と
  「`null` の 4 文字」の 2 本(後者が現状バグの本体)。`Unreadable` は
  `FileShare.None` で開いたまま `Load` する。
- `Missing` で通知しない(初回起動が警告を出さない)ことを固定。**既定値との区別**のため、
  非既定の設定値で始めるのではなく「ファイルが無い」状態そのものを作る
  (CLAUDE.md §4-B の no-change テストの原則)。
- 退避: `Corrupt` → `settings.json.bad` に**元の中身が残っている**こと。
  既に `.bad` がある状態で 2 回目の破損 → 上書きされること。
- `Save` のバイト列不変(§5.1)。
- 配線: `MainForm.OnShown` が通知に到達した回数を数える seam(`StaleBackupWarningCountForTest`
  と同じ方式)。`MessageBox` は blocking で観測できないため到達数だけ見る。

### 6.4 ミューテーション検証

CLAUDE.md §4-A により、本ブランチは**原則対象外**(ファイル I/O 処理は禁止側に明記されている)。
ただし §3.1 の判定表は `existedBefore` と `File.Exists(path)` の**組**で分岐しており、
片方を落とす変異(`existedBefore` を無視する / 事後の `File.Exists` を無視する)は
データ喪失に直結する。**この 2 変異だけスポットで確認する**(§4-A の「厳密な挙動を保証する
必要がある場合」に該当・ユーザー規範の例外条件)。それ以外へは広げない。

## 7. L5(実機 SR 検証)

**必要**(傘設計書 §4.2 の判定どおり。M-11 の通知は `MessageBox` = SR が読む面を新設する)。

| # | 手順 | 観測 |
|---|------|------|
| 1 | `settings.json` を壊して起動 | 破損ダイアログが読まれる。退避先パスが**読める形**で発声される(`SanitizeForDisplay` 後) |
| 2 | 同上・`.bad` 退避が失敗する状態(`.bad` を読み取り専用等)で起動 | 文言が「上書きされる」側に変わる |
| 3 | `settings.json` をロックして起動 | `Unreadable` の文言が読まれる。退避は起きない |
| 4 | 設定変更 → 再起動 | 設定が維持される(原子化の回帰) |
| 5 | 大きい文書の Ctrl+S | 体感で待たされない(§4.2 の fsync 副作用) |

**M-12 の復旧経路は L5 項目にしない。** 実機で `File.Replace` の部分失敗を決定的に起こせず、
「起こらなかった」と「直っている」を区別できないため。§6.1 の seam で担保する。

チェックリストは `docs/plans/2026-09-02-save-last-line-of-defense-l5-checklist.md` に起こす。
**修正前でも全行 PASS する形になっていないことを、チェックリスト作成時に検算する**
(PR #62 の最終レビュー Critical-1 で踏んだ形)。

## 8. 採らなかった案

| 案 | 却下理由 |
|----|---------|
| M-12: `destinationBackupFileName` に実ファイル名を渡し、Windows のロールバックに委ねる | 守るのは「古い内容」で、失うのは「新しい内容」。成功時にバックアップ削除が要り、その削除の失敗が新しい残骸源になる。§3 の復旧と併用もできるが、機構が 2 つになる分だけ「どちらが効いたか」が判らなくなる |
| M-12: 復旧を試みず tmp を残すだけ | ユーザーに手作業のリネームを要求する。アプリが一手で代行できる |
| M-12: エラーコード(`ERROR_UNABLE_TO_MOVE_REPLACEMENT`)で分岐 | 監査 §9 V-7。前置の列挙は漏れる。事後条件なら未知のエラーでも効く |
| M-13: 文書保存のみ flush | 電源断で最も惜しいのは未保存分を持つバックアップ側。守る対象が逆 |
| M-11: `Load` のオーバーロードを残す | status を黙って捨てられる呼出が復活する(§5.3) |
| M-11: `Unreadable` のとき設定保存を止める | 「設定を適用しました」が虚偽になる。B5 が潰す欠陥を B4 が新設する(§5.5) |
| M-11: 破損時に前回の正常な設定へロールバックする | 正常だった版を持っていない。持つには世代管理が要り、B4 の射程を超える |

## 9. 申し送り

- **M-14**(フォルダ作成権限が無い ACL で保存不能)は本ブランチで触らない。§3 の復旧は
  `File.Move` を使うため、tmp を作れない M-14 の状況では**そもそも到達しない**。
  傘設計書 §5 の割り当てのまま残す。
- **§3.3 の ACL 非継承は脆弱性パスへ回す。** 判断は本設計で確定しているが、
  「復旧が権限を広げうる」事実を最終レビューが独立に評価すること。
- **§4.2 の実測値**(fsync 前後の保存時間)を実施記録へ残すこと。数値が想定外に悪ければ、
  適用範囲(§4.2)の再判断材料になる。
- `settings.json.bad` は**掃除しない**(ユーザーが手で消す)。自動削除を足すと、
  「壊れた設定を後から見る」という退避の目的を自分で潰す。
- **【Task 7 の仕様レビューで判明・B4 射程外】`{"FontSize":1e400}` が `Ok` かつ
  `FontSize = +∞` で通る。** `Normalize` のガード `if (s.FontSize <= 0f)` が Infinity を
  素通しするため、破損 settings.json から起動時のフォント生成が失敗しうる。`Normalize` は
  本ブランチで無変更なので**既存バグ**。B4 は「失敗しても失わない」側だけを扱うので触らず、
  将来タスクとして回収すること(詳細は §10.14)。

## 10. 実施記録

### 10.1 Task 1 — RCS1194 が基底 `IOException` の hresult ctor まで要求した

**計画のコードは `-warnaserror` を通らなかった。** 実装計画 Task 1 のソースをそのまま置くと
`AtomicReplaceFailedException.cs(17,21): error RCS1194: Implement exception constructors` で停止する。

原因: RCS1194 は**基底型の public ctor をすべて鏡像実装する**ことを要求する。計画が前例に挙げた
`DocumentTooLargeException` は基底が `Exception`(public ctor は 3 種)なので標準 ctor 3 種で足りて
いたが、本型の基底 `IOException` には **`IOException(string message, int hresult)`** があり、
これも鏡像実装しろと言ってくる。前例の当てはめが基底型の違いを見落としていた。

**採った解決**: 理由コメント付きの `#pragma warning disable RCS1194`(class 宣言のみを囲む最小スコープ)。
リポジトリ既存の単一箇所抑止パターン(`// reason:` 付き pragma・src 内に 5 例)に倣った。
`.editorconfig` のリポジトリ全体設定は触っていない。

**退けた案 1 — public な鏡像 ctor**: HResult を外から与える ctor は §3.4 の不変条件
(「共有/ロック違反と一致しない HResult を持つ」)を**公開 API の穴**にする。
`IsShareOrLockViolation` が真になれば、原本喪失後に in-place フォールバックへ流れる。

**退けた案 2 — private な鏡像 ctor**: 実測では RCS1194 は消え、S1144 / CA1823 等の未使用警告も
出ない(技術的には成立する)。それでも採らないのは、誰も呼ばない private ctor は「なぜあるのか」が
コードから読めず、将来の掃除で消されて再びビルドが割れるため。抑止理由をコメントとして残せる
pragma の方が意図が保存される。

#### 実測で確定した事実(想定ではない・Task 3 の前提そのもの)

一時プローブで測った値。`{既定 IOException} / {inner=0x80070020 で作った outer} / {パラメータレス ctor} / {inner}`:

```
80131620/80131620/80131620/80070020
```

- `IOException` 既定の HResult は **`0x80131620`**(COR_E_IO)であり、共有/ロック違反
  (`0x80070020` / `0x80070021`)と**一致しない**。
- **inner の HResult は outer へ伝播しない。** 共有違反を inner に持たせて
  `AtomicReplaceFailedException` を作っても、outer の HResult は既定値のままだった。
  Task 3 で `File.Replace` が投げた共有違反例外を inner に包んでも、
  `IsShareOrLockViolation(outer)` は false のままでよい、という前提はこれで裏が取れている。

#### 仕様レビューで塞いだ網の穴 2 件(いずれも変異を当てて生存を実測した)

1. **テスト #1 の fixture が既定状態から始まっていた。** `replaceError` が素の
   `new IOException(...)` = HResult 既定値 `0x80131620` で、outer の既定値と同じだった。
   そのため主 ctor に `HResult = replaceError.HResult;`(「元のエラーコードを保存しよう」という
   善意の変異)を足しても **3 PASS のまま生存**した。本番で `File.Replace` が投げるのは共有違反が
   最も多く、§3.4 が恐れている当の経路である。fixture を `HResult = 0x80070020` から始める形へ
   直したうえで同じ変異を当て直し、`Assert.False() Failure / Expected: False / Actual: True` で
   殺せることを確認した。CLAUDE.md §4-B「no-change テストは非既定状態から始める」と同型の欠陥。
2. **テスト #2 の Message assertion が targetPath 側を見ていなかった。** tmp パスは targetPath を
   部分文字列として含む(`C:\dir\doc.txt` + `.abc.tmp`)ため、`Assert.Contains(tmpPath, ex.Message)`
   だけでは補間から `'{targetPath}'` を丸ごと削る変異を**素通し**した(3 PASS のまま生存)。
   引用符で閉じた形(`'C:\dir\doc.txt'`)の assertion を足して、
   `Assert.Contains() Failure: Sub-string not found` で殺せることを確認した。

#### 申し送り(Task 3 / Task 4 で確認する)

`AtomicReplaceFailedException` は `IOException` 派生なので、App 層の広い `catch (IOException)` が
これを握り潰し、`PreservedTempPath` をユーザーに見せないまま「保存に失敗しました」で終わる経路が
ありうる。tmp を残す意味が消えるので、Task 3 / Task 4 で catch 経路を確認すること。

### 10.2 Task 2 — seam へのミューテーション検証を例外的に実施した判断と、生存した 3 変異

#### なぜ §4-A の禁止を外したか

CLAUDE.md §4-A はファイル I/O 処理へのミューテーション検証を**禁止**している。Task 2 は
`AtomicFile` = まさにファイル I/O であり、原則どおりなら対象外である。それでも実施したのは、
**ユーザー規範の例外条件「厳密な挙動を保証する必要がある場合」に当たると判断した**ため。
根拠は「I/O だから」ではなく、**この seam が Task 3 のデータ喪失修正の土台**であることにある。

- Task 3 の復旧ロジックは `CommitStaged` の 1 か所にだけ入る。2 つある `Write` の**片方が
  静かに seam から外れても、修正が効かないまま全テストが緑になる**。緑は「直った」の証拠に
  ならなくなる。
- 本番の主保存経路 `TextFileService.Save(string, TextBuffer, …)` が使うのは **Stream 版**の
  ほうであり、外れて困る側がまさにそこだった(下記 M-2)。
- §6.4 は同じ例外条件を `§3.1 の判定表の 2 変異`へ既に適用している。Task 2 の seam は
  その判定表を載せる土台なので、同じ理屈が及ぶ範囲と判断した。

なお本記録は「§4-A を破った」ではなく「例外条件に当てた」という判断の記録である。
**適用範囲は差替段の集約と seam の後始末に限る**。`AtomicFile` の他の部分(ステージング書込・
`TryDelete`・`IsShareOrLockViolation`)へは広げていない。

#### 生存していた 3 変異(すべて仕様レビューの指摘・実測)

3 件のうち **M-1 / M-2 の 2 件は「タスク本文が明文で挙げた制約そのものが無網」**だった。実装
報告では「catch は catch-all のまま」「両 `Write` が `CommitStaged` を通る」を確認済みと書いたが、
確認したのは**現在のコードがそうなっていること**であって、**それが変えられたときに気付ける網**では
なかった。この 2 つは別のことである。

| # | 変異 | 修正前 | 修正後 |
|---|------|--------|--------|
| M-1 | `CommitStaged` の `catch` → `catch (IOException)` | Core 1384 / App 734 **全 PASS**・0 warning | Core 1387 中 **2 失敗** |
| M-2 | Stream 版 `Write` の `CommitStaged(tmp, path)` を変更前のインラインへ戻す | Core 1384 / App 734 **全 PASS**・0 warning | Core 1387 中 **1 失敗** |
| M-3 | `CommitOverrideScope.Dispose()` の `SetCommitOverride(_previous)` → `SetCommitOverride(null)` | Core 1384 **全 PASS**・0 warning | Core 1387 中 **1 失敗** |

M-3 は `-warnaserror` でも 0 warning で通ることを確認済み(アナライザが殺しているのではなく、
本当に網が無かった)。

**M-1 が非等価である実証**: 保存先が ReadOnly 属性のとき `File.Replace` は
`UnauthorizedAccessException`(= `IOException` ではない)を投げる。狭めた実装ではこれが catch を
素通りし、**残骸 tmp が 1 個残る**(既定実装は 0 個)。`FileControllerTests` の ReadOnly 系は
「原本不変」と「Modified 復元」しか見ておらず、tmp 残骸を見ていないため素通しになっていた。

塞いだあとに実際に赤になった出力:

```
# M-1
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Commit_failure_with_non_io_exception_still_cleans_tmp
   Assert.Empty() Failure: Collection was not empty
失敗 kxEdit.Core.Tests.IO.AtomicFileStreamWriteTests.Write_Stream_CommitFailureWithNonIoException_StillCleansTmp
   Assert.Empty() Failure: Collection was not empty
失敗! -失敗: 2、合格: 1385、合計: 1387

# M-2
失敗 kxEdit.Core.Tests.IO.AtomicFileStreamWriteTests.Write_Stream_CommitFailureWithNonIoException_StillCleansTmp
   Assert.Throws() Failure: No exception was thrown
失敗! -失敗: 1、合格: 1386、合計: 1387

# M-3
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Commit_override_scopes_restore_in_lifo_order
   Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 0
失敗! -失敗: 1、合格: 1386、合計: 1387
```

M-2 の網は Stream 側だけを赤にし、byte[] 側は緑のまま = 網が経路を弁別できている。

> 上の変異名・テスト名・出力は**当時のもの**(証跡なので書き換えない)。その後 §10.4 の M-1 で
> 改名しており、現在の名前は `CommitOverrideScope` → `ReplaceStepOverrideScope` /
> `SetCommitOverride` → `SetReplaceStepOverride` /
> `Commit_failure_with_non_io_exception_still_cleans_tmp` →
> `Replace_step_failure_with_non_io_exception_still_cleans_tmp` /
> `Commit_override_scopes_restore_in_lifo_order` →
> `Replace_step_override_scopes_restore_in_lifo_order` /
> `Write_Stream_CommitFailureWithNonIoException_StillCleansTmp` →
> `Write_Stream_ReplaceStepFailureWithNonIoException_StillCleansTmp`。

#### Task 3 で壊さないための注意

M-1 / M-2 の網はフックに**差替先を消させない**形にしてある。Task 3 で tmp 保持が入っても
保持されるのは「原本が消えた」枝だけなので、この 2 本は `Assert.Empty`(tmp 0 個)のまま
生き残る。**Task 3 でこの 2 本を書き換える必要が出たら、それは復旧の分岐条件が
「原本が消えたか」以外に広がった合図**なので、設計 §3.1 の判定表を先に見直すこと。

#### 実装計画からの逸脱(受容済み)

計画どおりの `Dispose() => t_commitOverride = _previous;` は
`error S2696`(インスタンスメソッドから static フィールドを更新するな)でビルドが割れ、
復帰用 static メソッドを外側へ足すと今度は `error S3398`(入れ子型からのみ使われるので中へ移せ)に
なる。張る側と戻す側の両方が通る `SetReplaceStepOverride` 1 つへ集約して両方を解消した
(= フィールドへの書込口が 1 か所になる)。レビューで計画どおりの形へ一時的に戻して両エラーの
再現を実測したうえで**受容**と結論した。Task 1 の RCS1194 に続き、計画のコードがアナライザを
通らなかったのは 2 件目。

### 10.3 Task 2 — M-12 の保証が及ぶ範囲(コード品質レビュー I-3・受容)

`CommitStaged` は `AtomicFile.Write` の 4 呼出者すべての通り道だが、**M-12 の「tmp を残して例外で
伝える」が実際にユーザーへ届くのは文書保存経路だけ**である。設計 §3.2 と Task 3 の xmldoc は
そのままだと「全永続化経路でデータ喪失を防いだ」と読めてしまうので、範囲をここに明記する。

| 呼出者 | 例外はユーザーへ届くか |
|--------|------------------------|
| `TextFileService.Save`(文書保存) | **届く**。これが M-12 の対象 |
| `BackupStore.Write`(`BackupStore.cs:49`) | **届かない**。`SerialBackupWriter.cs:47-53` が `catch { OnWriteFailed?.Invoke(...); }` で握り潰す |
| `SessionLayoutStore.Save`(`SessionLayoutStore.cs:125`) | **届かない**。同じワーカーの `catch` で握り潰す(`SerialBackupWriter.cs:94`) |

さらにバックアップ側は、残した tmp を**次回起動の `BackupStore.SweepTempFiles`(`BackupStore.cs:426`)
が `*.tmp` を無差別削除して回収する**(`:151` / `:257` から呼ばれる)。つまり M-12 が tmp を残しても
バックアップ経路では「静かに消える」。

**コード変更はしない**(本ブランチの射程外)。握り潰しの解消は B5 の M-20 が担当する。
**Task 6(設定の原子化)でも同じ判断が要る**——`SettingsStore` の保存が握り潰し側か通知側かを
確認してから、M-12 の保証範囲に含めるかを決めること。

### 10.4 Task 2 — コード品質レビューの反映(I-1 / I-2 / M-1 / M-2)

**I-1 理由節が構造的に偽だった**: 「テストからのみ置換できる」と断言していたが、
`kxEdit.Core.csproj:13,16` は production アセンブリ `kxEdit.Editor` と `kxEdit.Core.Bench` へも
internal を可視化しており、強制ではない。`#if DEBUG` で構造的に封じる案は
`tools/pre-merge-check.ps1:33-43` が Release でもテストを走らせるため**使えない**と結論済み。
文言を「production コードからは呼んでいない(強制ではなく現在の観測)」へ直した。
実装報告では同じことを懸念として自分で挙げていたのに、コードのコメントは断言のままだった
——「結論は正しいが理由節が偽」の再発。

**I-2 「張ったのに不発」を検出できなかった(最重要)**: フックはスレッド親和なので、張った
スレッドと `Write` が走るスレッドがずれると**黙って既定実装が走る**。事後状態は既定実装が
成功したときとまったく同じになるため、事後状態だけを見るテストは不発に気付けない。
Task 3 の主テスト `Bytes_recovers_when_replace_loses_the_original` は事後状態しか見ないので、
**復旧ロジックを一度も通さずに緑**になりうる。不発は空論ではなく、`BackupStore.Write` /
`SessionLayoutStore.Save` は `SerialBackupWriter.cs:39` の専用ワーカースレッドで走る。

対策として seam 自身に発火回数を持たせた(`ReplaceStepOverrideScope.Invocations`)。
`OverrideReplaceStepForTest` の戻り値型を具体型にして `Assert.Equal(1, scope.Invocations)` を
書けるようにし、**既存 4 本すべてをこの形へ寄せた**(手書きカウンタは廃止)。
投げるフックも発火として数えるため、記録はフック呼出の**前**に行う。

網が実際に不発を捕まえることは実測で確認した。`Replace_step_override_applies_only_inside_scope`
の `Write` を一時的にワーカースレッドで走らせると
`Assert.Equal() Failure / Expected: 1 / Actual: 0` で赤になる。さらに恒久テスト
`Replace_step_override_does_not_fire_on_another_thread` を追加し、**事後状態(内容・tmp 残骸)は
フックが効いた場合と区別が付かないのに `Invocations` だけが 0 になる**ことを固定した。
**Task 3 / Task 4 でフックを張るテストは必ず `Invocations` を assert すること。**

**M-1 名前が段差を伝えていなかった**: 内側の `Commit` は実体が `File.Replace` / `File.Move` の
1 手しかないのに広く聞こえ、`OverrideCommitForTest` は「コミットを丸ごと差し替えた」と読める。
実際にはフックが投げても外側の復旧が走って `Write` が成功 return し得る(M-12 の復旧成功枝)ため、
期待と逆の assertion を書きやすい。呼出が 4 か所しかない今のうちに改名した:
`Commit` → `RunReplaceStep` / `OverrideCommitForTest` → `OverrideReplaceStepForTest` /
`t_commitOverride` → `t_replaceStepOverride` / `CommitOverrideScope` → `ReplaceStepOverrideScope`。
外側の `CommitStaged`(= 失敗時ポリシー全体)は据え置き。

**改名後に 3 変異を当て直した**(網の形が変わったため)。M-1 / M-2 は同じ出力で再度死ぬ。
M-3 は `Dispose()` の引数を `null` にする形が **`error S4487`(unread private field `_previous`)で
コンパイルできなくなった**ため、読み取りを残す等価な変異
(`new ReplaceStepOverrideScope(hook, t_replaceStepOverride)` → `(hook, null)` = previous を
そもそも捕まえない)へ差し替えて実測し、`Expected: 1 / Actual: 0` で死ぬことを確認した。
なお最初にこの非コンパイル変異を当てたとき、ビルド失敗に気付かず**古い DLL のテスト結果を
読みかけた**——`grep "error CS"` 系の罠(既知)。以後ビルドの終了コードで実行を止めている。

**M-2 コード内の「Task N」は不安定な参照**: どの計画の Task か書かれておらず、
`Task 2 では不変` は Task 3 が入った瞬間に偽になる。リポジトリの他のコメントに倣い
`M-12` / 「設計 2026-09-02 §3」「同 §10.2」へ寄せた。

**M-3 は却下**(フック第 3 引数 `destExists` が全テストで捨てられている件)。既定実装に必要で、
`CommitStaged` から引き渡す判断(TOCTOU 回避)は正しいため現状維持。

### 10.5 Task 3 — M-12 本体(復旧と tmp 保持)の実測

#### 計画のコードは今回そのまま通った(Task 1 / Task 2 との違い)

Task 1(RCS1194)・Task 2(S2696 + S3398)は計画のコードがアナライザで割れたが、**Task 3 は
`CommitStaged` の差替コードも 4 本のテストも無修正でビルドが通った**(`-warnaserror` / 0 warning)。
懸念していた `catch (Exception replaceError)` に Sonar は何も言わなかった。`src` 配下に
`catch (Exception ex)` が 35 か所あり(`BackupCoordinator` / `FileController` ほか)、
S2221 は本リポジトリのアナライザ構成では発火していない。

**catch の範囲が従来と同一であることの確認**: 従来の裸 `catch` と `catch (Exception ex)` は、
アセンブリが `[assembly: RuntimeCompatibility(WrapNonExceptionThrows = false)]` を持たない限り
等価(非 CLS 例外は `RuntimeWrappedException` に包まれて `Exception` で捕まる)。
リポジトリ全体を grep して `RuntimeCompatibility` / `WrapNonExceptionThrows` の指定が
**1 件も無い**ことを確認した(= 既定の `true`)。実挙動としても、非 IOException を投げる
Task 2 の網 2 本(`Replace_step_failure_with_non_io_exception_still_cleans_tmp` /
`Write_Stream_ReplaceStepFailureWithNonIoException_StillCleansTmp`)が緑のままである。

#### Step 2 —— 修正前の実測(赤 6 / 緑 4)

計画は「4 本中 2 本が赤・2 本は修正前から PASS」と書いていたが、実際に書いたのは
byte[] / Stream 各 4 本 + 実経路 2 本の**計 10 本**で、内訳は赤 6 / 緑 4 だった。

```
失敗 …AtomicFileRecoveryTests.Bytes_recovers_when_replace_loses_the_original
   System.IO.IOException : simulated partial replace failure: destroyed '…' (destExists=True); only copy is '…'
失敗 …AtomicFileRecoveryTests.Stream_recovers_when_replace_loses_the_original
   System.IO.IOException : simulated partial replace failure: …
失敗 …AtomicFileRecoveryTests.Bytes_preserves_tmp_when_recovery_also_fails
失敗 …AtomicFileRecoveryTests.Stream_preserves_tmp_when_recovery_also_fails
   Assert.Throws() Failure: Exception type was not an exact match
   Expected: typeof(kxEdit.Core.IO.AtomicReplaceFailedException)
   Actual:   typeof(System.IO.IOException)
失敗 …AtomicFileRecoveryTests.Save_text_propagates_recovery_failure_without_in_place_fallback
失敗 …AtomicFileRecoveryTests.Save_buffer_propagates_recovery_failure_without_in_place_fallback
   (同上)
失敗!   -失敗:     6、合格:     4、スキップ:     0、合計:    10
```

**「修正前から PASS する」側が本当に PASS することも単独で実測した**(回帰網として働いている
= 修正が従来挙動を壊していないことの基準線になる)。修正前に 4 本だけを filter して実行:

```
成功!   -失敗:     0、合格:     4、スキップ:     0、合計:     4
  (Bytes/Stream × still_deletes_tmp_when_original_survives, deletes_tmp_when_creating_a_new_file_fails)
```

#### Step 4 —— 修正後(全緑)

```
dotnet build kxEdit.sln -c Debug -warnaserror   →  0 個の警告 / 0 エラー (EXITCODE=0)
kxEdit.Core.Tests    成功!  合格: 1398 / 合計: 1398   (修正前 1388 + 新規 10)
kxEdit.App.Tests     成功!  合格:  734 / 合計:  734
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
```

既存テストの失敗はゼロ。§10.2 の「Task 3 で M-1 / M-2 の 2 本を書き換える必要が出たら判定表を
見直す合図」に該当する事態は起きなかった(2 本とも無修正で緑)。

#### Step 5 —— 2 変異の実測(どちらも殺せた)

いずれも**クリーンビルドの終了コード 0 / 0 warning を確認してから**テストを走らせている。

| # | 変異 | 結果 |
|---|------|------|
| 1 | `if (destExists && !File.Exists(path))` → `if (!File.Exists(path))` | Core 1398 中 **2 失敗** |
| 2 | `if (destExists && !File.Exists(path))` → `if (destExists)` | Core 1398 中 **9 失敗** |

```
# 変異 1(新規作成の失敗でも復旧枝へ入り、File.Move が成功して Write が返ってしまう)
失敗 kxEdit.Core.Tests.IO.AtomicFileRecoveryTests.Bytes_deletes_tmp_when_creating_a_new_file_fails
   Assert.Throws() Failure: No exception was thrown
失敗 kxEdit.Core.Tests.IO.AtomicFileRecoveryTests.Stream_deletes_tmp_when_creating_a_new_file_fails
   Assert.Throws() Failure: No exception was thrown
失敗!   -失敗:     2、合格:  1396、スキップ:     0、合計:  1398

# 変異 2(原本が残っているのに復旧枝へ入り、File.Move が「既にある」で失敗して
#        AtomicReplaceFailedException に化ける = tmp も残る)
失敗 kxEdit.Core.Tests.IO.AtomicFileRecoveryTests.Bytes_still_deletes_tmp_when_original_survives
失敗 kxEdit.Core.Tests.IO.AtomicFileRecoveryTests.Stream_still_deletes_tmp_when_original_survives
   Assert.Throws() Failure: Exception type was not an exact match
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Write_to_fully_locked_target_throws_share_violation_and_keeps_original
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Replace_step_failure_with_non_io_exception_still_cleans_tmp
失敗 kxEdit.Core.Tests.IO.AtomicFileStreamWriteTests.Write_Stream_TargetLocked_ThrowsShareViolation_KeepsOriginal_CleansTmp
失敗 kxEdit.Core.Tests.IO.AtomicFileStreamWriteTests.Write_Stream_ReplaceStepFailureWithNonIoException_StillCleansTmp
失敗 kxEdit.Core.Tests.Text.TextFileServiceSaveTests.Save_does_not_truncate_original_when_unrecoverably_locked
失敗 kxEdit.Core.Tests.Text.TextFileServiceSaveTests.Save_falls_back_to_inplace_when_replace_blocked_by_share_lock
失敗 kxEdit.Core.Tests.Text.TextFileServiceSaveTextBufferTests.SaveTextBuffer_ShareViolation_FallsBackToInPlaceOverwrite
失敗!   -失敗:     9、合格:  1389、スキップ:     0、合計:  1398
```

変異 2 が既存 7 本まで巻き込んだのは、**共有違反(= 本番で最も多い差替失敗)がこの変異では
`AtomicReplaceFailedException` に化けて in-place フォールバックの `when` 節を素通りする**ため。
判定の片側を落とすと §3.4 の設計まで同時に崩れることが、意図せず可視化された。
変異は 2 件とも `git diff` で完全復帰を確認済み(`if` 行が 1 本追加されているだけの差分)。

**適用範囲は §6.4 のとおり判定表の 2 変異に限定**し、復旧の `File.Move` や `TryDelete`、
ステージング段へは広げていない(CLAUDE.md §4-A の I/O 禁止に対する例外条件の適用範囲)。

#### 計画と実物が食い違った点

1. **テスト本数**: 計画は byte[] 4 本 + Stream 4 本 = 8 本。実際は **10 本**で、
   `Save_buffer_propagates_recovery_failure_without_in_place_fallback` /
   `Save_text_propagates_recovery_failure_without_in_place_fallback` を足した。
   §3.4 は Task 1 で `IsShareOrLockViolation(ex) == false` を固定済みだが、それは
   **判定関数の網であって配線の網ではない**。`TextFileService.Save` の
   `catch (IOException ex) when (…)` を実際に通して、例外がフォールバックへ落ちずに
   伝播することを固定した。**両版とも、退行を実際に検出しているのは例外の型**である
   (`Assert.Throws` は xUnit では型完全一致)。Stream 版はフォールバックへ落ちれば byte[] 版
   Save へ委譲されて seam をもう一度通るので機構としては `Invocations` が 2 になるが、
   型不一致の assert が先に中断して**そこへ到達しない**ため、観測点として数えてはいけない。
   `Assert.Equal(1, scope.Invocations)` はフック不発ガード(§10.4 I-2)として置いている。
2. **ヘルパを静的メソッドにした**: 計画は `DestroyThenBlockRecovery` をテスト内のローカル関数に
   していたが、何も捕捉していないため static メソッドへ引き上げた(byte[] / Stream の 2 本で共用)。
   同時に、例外メッセージへ 3 引数(`tmp` / `dest` / `destExists`)をすべて埋め込む形にした。
   S1172(未使用パラメータ)を確実に避けつつ、失敗時の出力に実パスが出る。
3. **`Assert.IsNotType<AtomicReplaceFailedException>(ex)` は現状では冗長**。xUnit の
   `Assert.Throws<IOException>` は型完全一致なので、その時点で復旧枝に入っていないことは
   確定している。`ThrowsAny` へ緩められた場合に弁別を残す重ね掛けとして残し、その旨を
   テストの xmldoc に書いた(「この網は何を守っているのか」を後から読める形にする)。

#### 仕様レビューで直した理由節 —— layout の tmp は sweeper 対象外(恒久残留する)

Task 3 の xmldoc(および下の申し送りの前提)は、`BackupStore.Write` と `SessionLayoutStore.Save` を
**まとめて**「残した tmp は次回起動の `BackupStore.SweepTempFiles` が回収する = 静かに消える」と
書いていた。**layout については偽**である。自分で確かめた結果:

- `src` 全体で `*.tmp` を消すコードは `BackupStore` にしか無い(`SweepTempFiles` `:426` /
  `DeleteAll` 経由 `:151` / `DeleteSessionDir` 経由 `:257` / `DeleteTargetTempsIn` `:388`)。
  `grep -rn '\*\.tmp' src/ --include=*.cs` の全ヒットを当たって確認した。
- そのうち**起動時の掃除**は `BackupCoordinator.cs:346-347` の 2 呼出だけで、対象は
  `_sessionDir` と `_dir`。`_dir = BackupStore.DefaultDirectory`(`BackupStore.cs:23-28` =
  `%APPDATA%\kxEdit\backups`)、`_sessionDir = Path.Combine(_dir, "session-" + guid)`
  (`BackupCoordinator.cs:120` / `:128`)。**どちらも `backups` 配下**である。
- 一方 `SessionLayoutStore.DefaultPath` は `%APPDATA%\kxEdit\session-state.json`
  (`SessionLayoutStore.cs:27-34`)で、`BackupCoordinator.cs:134` がそのまま `_layoutPath` にし、
  `SerialBackupWriter.cs:94` が `SessionLayoutStore.Save(path, layout)` を呼ぶ。したがって
  その tmp は **`%APPDATA%\kxEdit\` 直下**に落ちる = 上の掃除対象に**含まれない**。

つまり layout 経路では、残した tmp が**恒久残留**する。実害は小さい(本文を含まない数 KB・
差替失敗と復旧失敗の二重障害が要る)が、根拠が偽だったので xmldoc を 2 経路に書き分けた。
結論(「これらの経路では例外がユーザーへ届かない」)は `SerialBackupWriter.cs:46-53` / `:92-101` の
握り潰しで成立しており、変わらない。

**Task 6 への申し送り**: `SettingsStore.DefaultPath` は `%APPDATA%\kxEdit\settings.json`
(`SettingsStore.cs:13-18`)で、layout と**同じディレクトリ**である。Task 6 で設定を
`AtomicFile` 経由にすると、その tmp も同じく sweeper 対象外になる。保証範囲を書くときは
「静かに消える」ではなく**「残留する」**が正しい。

> §10.3(Task 2 の記録)は「**バックアップ側は**…バックアップ経路では『静かに消える』」と
> 範囲を限って書いており、その範囲では正しい。ただし layout 側に何も書いていないため、
> 表と併せて読むと 2 経路とも消えるように読める。また §10.3 の「(`:151` / `:257` から
> 呼ばれる)」は `DeleteAll` / `DeleteSessionDir` の呼出であって、**起動時の掃除は
> `BackupCoordinator.cs:346-347`** である。§10.3 は当時の記録なので書き換えず、ここに補正を残す。

#### 前倒し脆弱性レビューの反映(Critical / High ゼロ・Medium 1 / Low 3)

**§3.3 の ACL 非継承の受容は「妥当・緩和不要・コード変更不要」と裁定された。** 緩和案(差替の前に
`FileInfo.GetAccessControl()` を採って復旧後に復元する)は、**絶対に失敗してはいけない happy path に
新しい失敗点を足す**ため主目的(原本喪失の回避)を損なう、という理由で退けられている。

##### (a) 受容の中心根拠を実測へ差し替えた

従来書いていた「消えるより権限が広がる方がマシ」という比較衡量は間違いではないが弱い。より強い根拠が
レビューの実測で出た: **復旧後の ACL は、同じ状況でユーザーが手で保存し直したときの ACL と完全に
一致する。** 原本が消えた後の再保存は `destExists == false` → `File.Move` を通るので、
**本修正の前のコードでも継承 ACL になる**。つまり復旧は<b>ユーザーの再試行を代行しているだけ</b>で、
**変更前が到達できなかった権限状態を新しく作り出してはいない**。これを xmldoc の中心根拠に据えた。

##### (b) 頻度の前提が偽だった —— 「二重障害が要る」は ACL の話には当てはまらない

ACL が実際に置き換わるのは復旧が**成功**したときである。差替の直前に別プロセス(AV の隔離・同期
クライアント・ユーザー自身の削除)が宛先を消せば、**単一の平凡な失敗**で復旧枝に入り `File.Move` は
ほぼ確実に成功する。したがって「復旧成功 = よく起こる」側であり、稀ではない。
「差替失敗と復旧失敗の二重障害が要る」が正しいのは **tmp が残るケース**(= 復旧も失敗)の方であって、
ACL 置換の頻度の根拠にしてはいけない。xmldoc に書き分けた。

**挙動変化の記録(Low)**: 副作用として「別プロセスが消したファイルを kxEdit が黙って復活させる」が
入る(変更前は保存失敗ダイアログ)。起点がユーザー自身の保存操作なので攻撃者が駆動できるものではない。

##### (c) 非上書き overload の網 —— レビューが提案した形は実測で成立しなかった

`File.Move(tmp, path)` を `File.Move(tmp, path, overwrite: true)` にする変異が無網、という指摘は
**正しい**(下記のとおり生存を実測)。ただし提案された網の形
(「フックで `File.Delete(dest)` してから別内容のファイルを `dest` に置いて例外を投げる →
`AtomicReplaceFailedException` になり squat 側が無傷で tmp も保持されることを assert する」)は
**そのままでは動かない**。実測(使い捨てプローブ):

```
P1: ex=IOException | invocations=1 | destContent=squatter | tmpLeftovers=0
```

ファイルが名前を埋めると `File.Exists(path)` が **true** になるため、**復旧枝そのものへ到達しない**
(従来どおり `TryDelete(tmp); throw;` が走る)。`AtomicReplaceFailedException` にならず、tmp も残らない。

そこで「復旧枝へ到達できる(= `File.Exists` が false)占有物」を総当たりで実測した:

| 占有物 | `File.Exists` | 2 引数 `Move` | `overwrite: true` の `Move` |
|--------|---------------|---------------|------------------------------|
| 素のファイル | **true** | (復旧枝へ入らない) | (同左) |
| reparse タグ付きファイル(非 surrogate / surrogate) | **true** | (同上) | (同上) |
| ディレクトリ | false | `IOException` 0x800700B7 | `UnauthorizedAccessException` 0x80070005 |
| surrogate タグ付きディレクトリ | false | `IOException` 0x800700B7 | `UnauthorizedAccessException` 0x80070005 |
| 宙ぶらりんの symlink | — | — | **作成不可**(下記) |

`File.CreateSymbolicLink` は検証機で
`IOException: クライアントは要求された特権を保有していません。` で失敗した
(`SeCreateSymbolicLinkPrivilege` = 要管理者 / 開発者モード)。

**結論**: `overwrite: true` の差が事後状態(例外の型・tmp の有無・占有物の中身)に出る状態は、
**特権なしでは作れない**。復旧枝へ到達できる占有物はディレクトリ系だけで、そこでは両 overload とも
失敗して外形は同じ `AtomicReplaceFailedException` + tmp 保持になる。

**採った網**: 唯一決定的に差が出る `RecoveryError` の型と HResult を固定した。
`0x800700B7`(ERROR_ALREADY_EXISTS)=「既に埋まっているので触らない」に対し、
`overwrite: true` は `0x80070005`(ERROR_ACCESS_DENIED / `UnauthorizedAccessException`)=
「置換しようとして弾かれた」になる。**置換を試みたかどうかが型に出る**ので、そこを押さえる。
byte[] 版・Stream 版の 2 本を追加した(`*_recovery_refuses_to_replace_an_entry_occupying_the_name`)。

**この網が示していないことも、テストのセクションコメントに明記した**: 「他人が置いた**ファイル**を
潰さない」は直接観測していない(その状態では復旧枝へ到達しないため)。
[[net-absence-claims-are-also-verifiable]] の逆側 —— 張れない網を張ったと書かないこと。

**変異の実測**:

```
# 網を足す前(生存)
成功!   -失敗:     0、合格:  1398、スキップ:     0、合計:  1398

# 網を足した後(同じ変異)
失敗 …AtomicFileRecoveryTests.Bytes_recovery_refuses_to_replace_an_entry_occupying_the_name
失敗 …AtomicFileRecoveryTests.Stream_recovery_refuses_to_replace_an_entry_occupying_the_name
   Assert.IsType() Failure: Value is not the exact type
   Expected: typeof(System.IO.IOException)
   Actual:   typeof(System.UnauthorizedAccessException)
失敗!   -失敗:     2、合格:  1398、スキップ:     0、合計:  1400
```

変異は `git diff` で復帰確認済み(`src` 側の差分が空になることを確認)。

##### (d) Low-3: `session-state.json` の残置 tmp は実質リスクなし、ただし性質が 1 点違う

中身はパス列 + キャレット位置で数 KB・本文を含まず、`%APPDATA%` の既定 ACL はユーザー専用なので
実質リスクなしと裁定された。ただし **`session-state.json` 本体は正常終了時に
`SessionLayoutStore.Delete` で意図的に消される**のに、残置 tmp はその削除を生き延びる。
「開いていたファイルの一覧を残さない」という既存の挙動を、残骸だけが破ることになる。

また**検証機の `%APPDATA%` には追加 ACE(開発機固有のサンドボックス設定)があった**。
「`%APPDATA%` は常にユーザー専用」と決め打たない根拠として記録しておく。

#### 申し送り(Task 4 で回収すること)

1. **`PreservedTempPath` が実在しないケースがありうる。** 復旧の `File.Move` が
   `FileNotFoundException`(tmp まで失われていた)で落ちた場合も
   `AtomicReplaceFailedException` になり、メッセージは「書き込んだ内容は '…' に残してあります」と
   言い切る。**Task 4 の文言生成では `File.Exists(ex.PreservedTempPath)` で分岐すること**
   (実在しない退避先を案内するのは、単なる保存失敗より悪い)。**観測面はこれ 1 本に
   一本化する** —— `RecoveryError` の型(`FileNotFoundException` 等)で弁別してはいけない。
   tmp 喪失は親ディレクトリごと消えた場合の `DirectoryNotFoundException` でも起こり、
   **型の列挙は漏れる**。要求 1 でこのタスク自身が守った「前置の列挙は原理的に漏れる・
   事後条件で検査する」(監査 §9 V-7)と同型の誤りになる。
   `AtomicFile` 側は設計 §3.2 どおりに保ち、コードは変えていない
   (復旧直前に `File.Exists(tmp)` を採り直すと TOCTOU 窓を増やすため)。
2. **Task 1 の申し送り(App 層の広い `catch (IOException)`)は「握り潰し」ではなかった。**
   `FileController.WriteToPath`(`:900`)の catch は `_prompt.Error($"保存できませんでした:
   {SanitizeForDisplay.OneLine(ex.Message, 200)}", …)` なので、`AtomicReplaceFailedException`
   のメッセージ(保存先と退避先の 2 パスを含む)は**ユーザーへ届く**。ただし
   **200 文字で切り詰められる**ため、長いパスでは退避先が末尾から落ちうる。

3. **Medium-1(脆弱性レビュー)—— 文書保存経路で残す tmp は「本文の完全なコピー」で、掃除する者が
   誰もいない。** バックアップ経路と違い、原本と同じディレクトリ = ユーザーの Documents や共有
   フォルダー、クラウド同期フォルダーに落ちる(`*.tmp` を消すコードは `%APPDATA%\kxEdit\backups`
   配下しか見ない —— 上の sweeper 節を参照)。しかも ACL は原本のものではなくディレクトリの継承 ACL、
   拡張子 `.tmp` で通常の一覧に出ず、クラウド同期なら自動アップロードされる。
   そして**唯一の案内である例外メッセージが `FileController.cs:929` の
   `SanitizeForDisplay.OneLine(ex.Message, 200)` で切られ、tmp パスは文末にある**。
   レビュアーの算定: 固定部 36 文字 + 原本パス `L` + tmp パス `L+17` = `53 + 2L` なので
   **`L ≧ 74` で 200 を超える**(`%USERPROFILE%\OneDrive - <会社名>\Documents\…\notes.txt` を
   展開した程度の長さで普通に超える)。

   **自動削除は足さないこと**(M-12 の目的そのものを潰す)。Task 4 は次の 3 点セットで回収する:
   1. `ex.PreservedTempPath` を**別引数として組み立て、丸めの対象を原本パス側だけにする**
      (tmp パスは丸めない)
   2. 文言に「**復旧後にこのファイルを削除してください**」を入れる
   3. `File.Exists(ex.PreservedTempPath)` で**実在を確かめてから**案内する(上の申し送り #1)

### 10.6 Task 4 — 残した場所をユーザーへ届ける(M-12 の回収)

Task 3 で tmp を残せるようになったが、**残した場所が伝わらなければ M-12 の修正は意味を持たない**。
唯一の案内である例外メッセージは `FileController.WriteToPath` の共通文言
(`SanitizeForDisplay.OneLine(ex.Message, 200)`)で切られ、tmp パスは文末にある(§10.5 申し送り 3)。

#### 採った文言(実物)

`AtomicReplaceFailedException` 専用の分岐を、既存の `DocumentTooLargeException` 分岐の直後に置いた。

```
[退避先が実在するとき]
保存できませんでした: 保存先 '<原本パス(200 字で丸める)>' が失われました。
書き込んだ内容は '<退避先パス(丸めない)>' に残してあります。
内容を復旧したら、このファイルを削除してください。

[退避先が実在しないとき]
保存できませんでした: 保存先 '<原本パス(200 字で丸める)>' が失われ、書き込んだ内容も残せませんでした。
編集中の内容はまだ開いたままです。「名前を付けて保存」で別の場所へ保存してください。
```

(実際は 1 行。上は読みやすさのため折り返している)

3 点仕様の反映:

| 仕様 | 反映 |
|------|------|
| (a) tmp パスを丸めない | `ex.Message` に任せず `PreservedTempPath` を**別引数**として組む。`SanitizeForDisplay.OneLine(path)` = 無害化はするが `maxLength` 既定 (`int.MaxValue`) で**切り詰めない**。丸めるのは原本パス側だけ |
| (b) 削除を促す | 「内容を復旧したら、このファイルを削除してください。」**自動削除は足していない** |
| (c) 実在確認 | `System.IO.File.Exists(ex.PreservedTempPath)` **一本**で分岐。`RecoveryError` の型では分けない |

`return false;` は維持(保存できていないので `Modified` は立ったまま)。網でも固定した。

**原本パスだけ丸めてよい理由**: 原本はユーザーが今まさに保存しようとした先で**既知**だが、
退避先の名前は kxEdit がその場で作った乱数入り(`<原本ファイル名>.<乱数>.tmp`)で、
**ユーザーが他所から知る手段が無い**。切ってよい方と切ってはいけない方が非対称である。

**`File.Exists` が倒れる向き**: `File.Exists` は読めない/不正なパスでも例外を投げず `false` を返す。
つまり親ディレクトリが読めなくなった場合、tmp が実在しても「残せませんでした」側へ倒れる。
倒れる先が「無い場所を案内する」ではなく「あるのに案内しない」なので、(c) の目的
(実在しない退避先を案内しない)には反しない。コメントに明記した。

**catch フィルタは無変更で一致する**ことを実装時に確認した。`AtomicReplaceFailedException` は
`IOException` 派生で、既存の `when (ex is System.IO.IOException or …)` にそのまま載る。
確認は「読んで確かめた」ではなく**網で押さえている** —— 一致しなければ catch に入らず未処理例外に
なるため、新規 2 本が例外送出で赤くなる(§10.2 M-1/M-2 の教訓: 「現在そうなっていること」と
「変えられたら気付ける網」は別物)。

#### 「丸めない」ことの弁別 —— 実測

短いパスでは共通文言でも退避先が丸ごと収まるため、**短いパスの fixture では修正の有無を弁別できない**
(既定値から始める no-change テストと同型の罠)。実測値(修正前のコードに対して測定):

**以下はすべて無害化後(`SanitizeForDisplay` 適用後)の長さ**で揃えてある。切り詰めは無害化後の
文字列に対して効くので、比較すべきはこちらである。

| 原本パス長 | 退避先パス長 | メッセージ長 | 退避先の開始位置 | 200 字切り後に残る退避先 |
|---|---|---|---|---|
| 120 | 137 | 293 | 145 | **54 文字**(83 文字が欠落) |
| 189 | 206 | 431 | 214 | **0 文字**(案内が丸ごと消える) |

`SanitizeForDisplay.OneLine(s, 200)` は 199 文字 + `…` を返すので、退避先の開始位置が 199 を超えると
案内は**1 文字も残らない**。

> **単位についての訂正**(レビュー指摘 4): 本表の初稿は 2 行目を `| 190 | 206 | 431 | 214 | 0 |` と
> 書いていたが、**1 列目だけが生の値で 2〜4 列目は無害化後**という混在だった。2 行目の fixture は
> ファイル名に U+202E(RLO)を 1 個含むので、**生 190 / 無害化後 189・生 207 / 無害化後 206・
> 生 433 / 無害化後 431** と 1 文字ずつずれる(メッセージには原本と退避先の 2 か所へ RLO が入るので
> 2 文字ずれる)。上の式「固定部 36 + 原本 L + tmp L+17 = 53+2L」は**生の値**に対して成り立つ
> (L=190 → 433)。1 行目(120 の段)は RLO を入れる前の fixture なので生と無害化後が一致しており、
> 表の値のままで整合している。結論(0 文字)はどちらの読みでも変わらない。
> `FileController.cs` のコメント側は生の値で首尾一貫しているため、そのままにしてある。
`Assert.DoesNotContain(<退避先>, SanitizeForDisplay.OneLine(<例外の Message>, 200))` を
テスト内に置き、**fixture がその領域に入っていること自体を毎回検算**する
(この行が緑にならない fixture では、その下の `Assert.Contains` は修正前でも PASS してしまう)。

一時プローブで測った修正前の赤:

```
Assert.Equal() Failure: Strings differ
Actual:   "tgt=190 tmp=206 raw=431 at=214 shown=0 cut=206 act=212"
```

`act=212` = 修正前のダイアログ本文長(`"保存できませんでした: "` 12 + 切り詰め後 200)。
このプローブは測定用で、commit には含めていない。

#### 網(`FileControllerTests`・3 本)

Core の差替段 seam(`AtomicFile.OverrideReplaceStepForTest`)へ偽装を注入し、
`FileController` → `TextFileService` → `AtomicFile` の end-to-end で固定した。
**seam を張る 2 本(下記 1・2)には `Assert.Equal(1, scope.Invocations)` を置いている**
(§10.4 I-2 の不発ガード)。3 本目は seam を使わない(`Host.MetaChangedThrow` で
既知の `IOException` を注入する)ので、この assert は無い。

> 訂正: commit `0a5b9d9` のメッセージと、本節の初稿は「網 3 本はいずれも seam を張り
> `Invocations` を assert する」と書いていたが**偽**である(3 本目は seam を使わない)。
> commit は書き換えずここに補正を残す。§10.1 以来の「結論は正しいが理由節が偽」の再発で、
> しかも**自分が同じ節で数えた本数**を確かめずに書いた。

1. `Save_ReplaceLosesOriginal_ReportsPreservedTempPathInFull` —— 原本を消し、宛先に同名ディレクトリを
   作って復旧の `File.Move` も落とす。退避先は**実在する**。原本パス 190 文字。観測点は
   ①退避先が**完全な形**で載る ②「削除してください」が載る ③生の U+202E がダイアログへ載らない
   ④`Modified` が true のまま。
2. `Save_ReplaceLosesOriginalAndTemp_DoesNotPointAtAMissingFile` —— 原本と tmp の**両方**を消してから
   投げる。退避先は**実在しない**。観測点は「残してあります」と退避先パスを**言わない**こと。
   こちらは**短いパス**を使う —— 長いパスだと共通文言へ素通ししても 200 字切りで
   「残してあります」が末尾から落ち、**素通しと修正済みを区別できない**(前提として
   「短いパスなら共通文言には丸ごと載る」ことを assert して検算している)。
3. `Save_OrdinaryIoFailure_KeepsGenericMessageTruncatedAt200` —— 退行の確認。一般的な `IOException` の
   文言が**完全一致で**変わっていないこと(切り詰め込み)。新分岐の条件を `ex is IOException` 等へ
   広げるとここが赤くなる。

網を 2 つ足した(タスク本文の要求 3 点には無いが、実装が壊れやすい向き):

- **退避先パス単体でも 200 字を超える** fixture にした(`Assert.True(preservedShown.Length > 200)`)。
  「共通文言はやめたが、退避先に 200 字の上限を付け直す」形の退行が落ちる。
  これが無いと、`OneLine(path, 200)` へ差し替える変異が生存する。
- **ファイル名に U+202E(RLO)を混ぜた**。`SanitizeForDisplay` は「丸めない」を実装するときに
  一緒に外されやすい(切り詰めと無害化が同じ呼出にある)。生の RLO がダイアログへ載らないことを
  固定して、**無害化だけ外す変異**を落とす。比較は全て `StringComparison.Ordinal`
  (U+202E は `UnicodeCategory.Format` のため culture-sensitive な `Contains` は常に「見つかる」側に
  倒れる —— CSV-L-5 系テストと同旨)。

#### `InternalsVisibleTo` を `kxEdit.App.Tests` へ広げた判断

`kxEdit.Core.csproj` に 1 行足した。理由と、それでも受容する根拠:

- **必要性**: `File.Replace` の部分失敗は実環境で決定的に起こせない(§6.1)。App 層の文言を
  end-to-end で固定するには、App.Tests 側から差替段を差し替えるしかない。
  代替案「`AtomicReplaceFailedException` を直接 `_prompt` へ流す単体テスト」は、
  **例外が本当に catch フィルタに載るか**を検証できない(§10.1 の申し送りが心配していた当のもの)。
- **前例と同種**: `kxEdit.Editor.csproj` は既に `kxEdit.App.Tests` へ internal を可視化しており
  (A-13 の `SetClipboardForTest`)、理由も同じ「テスト専用 seam を public へ昇格させるより副作用が
  小さい」。今回は横並びで、新しい種類の緩和ではない。
- **副作用の範囲**: `kxEdit.Core` の internal は既に `kxEdit.Core.Tests` / `kxEdit.Core.Bench` /
  `kxEdit.Editor` の 3 つへ開いている。増えるのは**テストアセンブリ 1 つ**で、production 出荷物は
  増えない。§10.4 I-1 で確認したとおり「テストからのみ置換できる」は元々**強制ではない**ので、
  この 1 行がその保証を壊すわけでもない。

#### 計画と実物が食い違った点

1. **`Assert.Single(collection.Where(...))` がビルドを割った**。
   `error xUnit2031: Do not use a Where clause to filter before calling Assert.Single` で停止。
   `Assert.Single(collection, predicate)` overload(要素を返す)へ寄せた。
   **計画/実装案のコードがアナライザに弾かれたのは本ブランチ 3 件目**
   (Task 1 = RCS1194 / Task 2 = S2696 + S3398)。
2. **fixture は「既存の ReadOnly 属性テストと同じ形」にならなかった**。タスク本文はそう指示していたが、
   seam で差替段そのものを偽装するので**実失敗を作る必要が無い**。原本を実在させるだけでよく、
   ReadOnly 属性も、その後始末(`SetAttributes(..., Normal)` の `finally`)も要らない。
   ReadOnly 属性が要るのは「実失敗経路で押さえる」既存テストの方である。
3. **退行確認に ReadOnly 経路を使わなかった**。OS 由来のメッセージはロケール依存で完全一致に使えない。
   `Host.MetaChangedThrow` seam へ既知の 300 文字メッセージを注入し、
   切り詰めまで含めて完全一致で固定した。ReadOnly 経路の文言は既存の
   `Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly` ほかが押さえている(いずれも緑のまま)。
4. **原本パスの丸めは 200 のまま据え置いた**。「原本パスも切らない」案は採らない —— 切ってよい側と
   切ってはいけない側の非対称(上記)を、コードの形として残したかった。

#### 事故: `Copy-Item` のタイムスタンプ保持で古い DLL を読みかけた

修正前の赤を測るため `Copy-Item` で `FileController.cs` を退避 → `git checkout` → 測定 →
`Copy-Item` で書き戻し、という手順を踏んだ。`Copy-Item` は **LastWriteTime を複製元から引き継ぐ**ため、
書き戻した直後のソースは**ビルド済み DLL より古い**。MSBuild は最新と判断してリビルドせず、
`dotnet build` は EXITCODE=0 を返したのに**修正前の DLL のまま**テストが走り、2 本が赤のままだった。
`grep -E " (error|warning) [A-Z]+[0-9]+"` も終了コードも**この事故を検出しない**
(ビルドは本当に成功している)。`LastWriteTime` を現在時刻へ更新して解決。
§10.4 の「古い DLL のテスト結果を読みかけた」の別バリアントで、**終了コードの確認だけでは足りない**
ケースがあることの記録。以後、ファイルを退避 → 書き戻す手順では書き戻し後に必ずタイムスタンプを更新する。

#### 検証

```
dotnet build kxEdit.sln -c Debug -warnaserror   →  0 個の警告 / 0 エラー (EXITCODE=0)
kxEdit.Core.Tests    成功!  合格: 1400 / 合計: 1400
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737   (修正前 734 + 新規 3)
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
dotnet csharpier check <変更 2 ファイル>  →  EXITCODE=0
```

修正前の赤(新規 3 本を修正前の `src` に対して実行):

```
失敗 …FileControllerTests.Save_ReplaceLosesOriginalAndTemp_DoesNotPointAtAMissingFile
   Assert.DoesNotContain() Failure: Sub-string found
                                              ↓ (pos 189)
   String: ···"h2w.loe\\a.txt.sfokpzdf.52r.tmp' に残してあります。"
   Found:  "残してあります"
失敗 …FileControllerTests.Save_ReplaceLosesOriginal_ReportsPreservedTempPathInFull
   Assert.Contains() Failure: Sub-string not found
   String:    "保存できませんでした: 保存先 '%USERPROFILE%\AppData\Lo"···
   Not found: "%USERPROFILE%\AppData\Local\Temp\kxEditAp"···
失敗!   -失敗:     2、合格:     1、スキップ:     0、合計:     3
```

(上の実パスは `%USERPROFILE%` へ伏せてある。実出力はユーザーホーム下の絶対パス)

3 本目(退行確認)は**修正前から緑**である = 一般文言の基準線として働いている。

#### L5

この文言は **L5 の対象にしない**(§7 の「M-12 の復旧経路は L5 項目にしない」と同じ理由:
`File.Replace` の部分失敗を実機で決定的に起こせず、「起こらなかった」と「直っている」を区別できない)。
SR 前提での文言設計(何が起きたか / どこに残っているか / 何をすべきか の 3 点を過不足なく・1 行で)は
守っているが、**実発声の確認はしていない**。

### 10.7 Task 4 — 仕様レビューの反映(原本側の網・語順・無害化の格下げ)

3 点仕様(退避先を丸めない / 削除を促す / `File.Exists` 一本で弁別)は**充足**と裁定された。
以下は同レビューで出た 4 指摘の反映である。**12 変異のうち 3 件が生存していた。**

#### 指摘 1 —— 仕様 (a) の「原本側」が丸ごと無網だった(生存 2 件)

「丸めるのは原本パス側だけ」は 2 つの主張の合成なのに、**網は退避先側にしか無かった**。
原因は fixture の長さである。原本パスが無害化後 **189 文字**で、当時の上限 200 に **11 文字届いて
いない** —— 原本側は「載ること」も「丸められること」も 1 つも assert されていなかった。

**自分は §10.6 のセルフレビューで fixture を 120 → 190 へ引き上げているが、上げ幅が足りなかった。**
「200 を超えさせる」ときに超えさせたのは**メッセージ全体**(53+2L)であって、**原本パス単体**では
なかった。仕様が 2 つの経路を持つとき、fixture は**経路ごとに**閾値を跨がせる必要がある。

修正前の生存(いずれも `--no-incremental` ビルドで 0 warning / 0 エラーを確認してから実行):

| # | 変異 | 修正前 | 修正後 |
|---|------|--------|--------|
| M7 | `OneLine(TargetPath, 200)` → `OneLine(TargetPath)`(丸めの撤廃) | App 737 **全 PASS** | App 737 中 **1 失敗** |
| M13 | `OneLine(TargetPath, 200)` → `OneLine(Path.GetFileName(TargetPath), 200)`(ファイル名へ縮退) | App 737 **全 PASS** | App 737 中 **1 失敗** |

```
# 修正前(どちらも同じ)
成功!   -失敗:     0、合格:   737、スキップ:     0、合計:   737

# 修正後(M7 / M13 とも同一出力)
失敗 kxEdit.App.Tests.FileControllerTests.Save_ReplaceLosesOriginal_ReportsPreservedTempPathInFull
   Assert.Contains() Failure: Sub-string not found
   at …FileControllerTests.cs:line 1806          ← Assert.Contains(targetShown, error.Text)
失敗!   -失敗:     1、合格:   736、スキップ:     0、合計:   737
```

**採った網**: fixture を `minLength: 220` へ上げ(退避先 237 文字で MAX_PATH 260 に収まる)、
`Assert.Contains(SanitizeForDisplay.OneLine(path, 80), error.Text)` を置いた。丸めが無ければ
79 字の直後に `…` が来ないので落ちる。加えて `Assert.Equal(80, targetShown.Length)` /
`Assert.EndsWith("…", targetShown)` で**丸めが実際に起きる長さであること自体**を検算する
(これが無いと、また上限に届かない fixture へ静かに戻れる)。

**レビュアーが提案した網はそのままでは成立しない**。提案は
`Assert.DoesNotContain(<原本パス全体>, error.Text)` だったが、**退避先パスは原本パスを prefix として
含む**(`tmp = <原本パス>.<乱数>.tmp`)ため、退避先を完全な形で載せる正しい実装でも原本パス全体は
ダイアログに現れる = この assert は**正しい実装を落とす**。テストに
`Assert.StartsWith(path, preserved!)` を置いてこの prefix 関係を実測で示し、弁別は
「丸めた形が載っていること」の側だけで行う形にした。**計画/レビューのコードは検証すべき案である**
(§10.5 (c) と同型 —— レビュアー提案の網が実測で成立しなかったのは 2 件目)。

#### 指摘 2 —— `OneLine` → `MultiLine`(無害化の格下げ)は**網が張れた**

3 件目の生存変異。コーディネーターの見立ては「Windows のパスに CR/LF/TAB を入れられないので
**この変異を殺す fixture は原理的に作れない可能性が高い**」だった。**これは誤りである**ことを
実測で確かめた。

`OneLine` と `MultiLine` の差は CR/LF/TAB の扱いだけではない。**`OneLine` は連続空白を 1 個へ畳むが、
`MultiLine` は畳まない**(`SanitizeForDisplay.cs` の xmldoc どおり)。そして **Windows のパス構成要素は
途中に連続空白を持てる**。使い捨てプローブで実測:

```
exists=True
full=…\kxprobe_stqk5lwh.u21\aa  bb\x.txt
doubleSpacePreserved=True
realFullName=…\kxprobe_stqk5lwh.u21\aa  bb\x.txt
```

(末尾の空白は Win32 が落とすが、**途中の連続空白は作成できて正規化でも保たれる**)

そこで `MakeLongTargetPath` の詰め物の**末尾寄り**に連続空白を 1 か所埋め込み、
`Assert.Contains(SanitizeForDisplay.OneLine(preserved), error.Text)` が畳まれた形を要求する形にした。
`MultiLine` へ格下げすると畳まれないので Contains が外れる。位置を末尾寄りにするのは、原本側の
丸め(80 字)に掛からない場所に置いて指摘 1 の網と混線させないため。fixture が空振りしないことは
`Assert.Contains("  ", preserved)` と `Assert.DoesNotContain("  ", preservedShown)` の対で検算する。

| # | 変異 | 修正前 | 修正後 |
|---|------|--------|--------|
| M12 | `OneLine(PreservedTempPath)` → `MultiLine(PreservedTempPath)` | App 737 **全 PASS** | App 737 中 **1 失敗**(`…cs:line 1794` = 退避先の Contains) |

**「張れない網を張ったと言うのも嘘だが、張れる網を『張れない』と宣言するのも同じ嘘である**
([[net-absence-claims-are-also-verifiable]])。今回は後者を宣言しかけたケースで、
**差分の定義(何と何が違うのか)を API の xmldoc まで戻って読み直したら網は存在した**。

#### 指摘 3 —— 一番役に立つ事実が、実在する側の文言に 1 文字も入っていなかった

**指摘を採用した。** 退避先 tmp は「エディタが今も持っている内容の複製」であり、失われたのは
*元ファイル*の方である。したがって**最短の復旧は tmp を探すことではなく「名前を付けて保存」**で、
その一文が実在しない側の文言にしか無かったのは、案内として本末転倒だった。

**(1) 語順**: 実在する側にも復旧手段を入れ、**長いパスより前**に置いた。SR は線形に読むので、
後ろに置くと数百文字のパス朗読を聞き終えるまで到達できない。実測(本テストの fixture):

| 語順 | 「名前を付けて保存」に到達する位置 | 退避先パスの開始位置 | 本文全長 |
|---|---|---|---|
| 案内を後ろに置く(変異) | **383 文字目** | 117 | 430 |
| 採用(案内を前に置く) | **126 文字目** | 158 | 430 |

順序の網は `guideAt < tempAt` で固定した。語順を入れ替える変異は
`guideAt=383 tempAt=117` で落ちる(=網は空振りしていない)。

**(2) 原本パスの上限を 200 → 80 に縮めた**。§10.6 に自分で書いた「原本はユーザーが今まさに保存
しようとした先で**既知**」という非対称は、「退避先を切らない」だけでなく**「原本はもっと切ってよい」**も
導く。約 110〜130 文字短くなる。**丸めても操作に必要な情報は落ちない** —— 退避先のフォルダーは
tmp パス側に常に完全な形で載るので、丸められるのは「どこへ保存しようとしていたか」の文脈情報だけ。
逆向きの案(**退避先をファイル名だけにして「同じフォルダー」と言う**)は採らない: 原本を丸めた
場合にフォルダーが判らなくなる(レビュアーと同意見)。

#### 指摘 4 —— §10.6 の表で数値の単位が混ざっていた

修正済み(§10.6 の表に訂正注記を追記した)。1 列目だけが生の値で 2〜4 列目は無害化後、という混在で、
式 `53+2L` に L=190 を入れた 433 と表の 431 が合わなかった。**結論は変わらない**が
「結論は正しいが数字の根拠が再現できない」形だったので、表を無害化後で揃え、生↔無害化後の
対応を注記した。

#### 申し送り(対応せず記録に留める)

- **`OneLine` は連続空白を 1 個に畳むため、フォルダー名に空白が 2 つ続くパスは「実在しない形」で
  表示される。** この文言は他と違い「そこへ行って消す」ための**操作可能な情報**なので性質が少し違うが、
  無害化は外せない(仕様)。皮肉なことに、指摘 2 の網はこの欠陥を**利用して**張っている。
  直すなら「空白の畳み込みだけを行わない無害化」を別 API として足すことになるが、
  `SanitizeForDisplay` の API を増やす判断は本タスクの射程外。
- **3 本目のテスト名 `Save_OrdinaryIoFailure_…` は「保存が失敗した」と読めるが、実体は
  「保存後の `metaChanged` が失敗した」**。既存 seam(`Host.MetaChangedThrow`)の制約による。
  レビューでも受容と判定された。

#### ツールの罠 —— grep パターンが `xUnit####` を取りこぼす

本ブランチで使ってきた `grep -E " (error|warning) [A-Z]+[0-9]+"` は
**`error xUnit2031` / `error xUnit2020` を捕まえない**(先頭が小文字の `x`)。
「エラー無し」と読み違えると、§10.4 の「古い DLL のテスト結果を読みかけた」と同じ穴に落ちる。
以後は **`-E " (error|warning) [A-Za-z]+[0-9]+"`** を使う。
実際、本節の一時プローブで踏んだ `error xUnit2020`(`Assert.True(false, message)` は
`Assert.Fail(message)` を使え)は、新しいパターンでのみ可視化された。
**計画/実装案のコードがアナライザに弾かれたのは、これで本ブランチ 5 件目**
(RCS1194 / S2696 / S3398 / xUnit2031 / xUnit2020)。

#### 検証

```
dotnet build kxEdit.sln -c Debug --no-incremental -warnaserror  →  0 個の警告 / 0 エラー
kxEdit.Core.Tests    成功!  合格: 1400 / 合計: 1400
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
dotnet csharpier check <変更 2 ファイル>  →  EXITCODE=0
```

テスト本数は変わっていない(既存 3 本の網を強化しただけで、新規テストは足していない)。
変異は 4 件とも復帰を確認済み。

### 10.8 Task 5 — rename 前の fsync(M-13)。張れた網と、張れなかった網

#### 実装

- **Stream 版**: `writer(fs)` の直後・`using` を抜ける前に `fs.Flush(flushToDisk: true)`。
- **byte[] 版**: `File.WriteAllBytes(tmp, payload)` を
  `FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)` + `Write` +
  `Flush(flushToDisk: true)` へ置き換え(Stream 版と同じ形)。
- クラス xmldoc に §4.3 の限界(rename が届いたことは保証しない・その失敗では原本が無傷で残る)を明記。

**唯一の production 呼出側を読んで確認した**: Stream 版を使うのは
`TextFileService.Save(string, TextBuffer, Encoding, bool)`(`:375`)だけで、渡された `stream` へ
直接 `Write` するのみ。`StreamWriter` 等でラップしていないので、`fs.Flush(true)` が
「ラッパの中に溜まったバイト列」を取りこぼす経路は無い(下の食い違い 3 も参照)。

#### 性能の実測(設計 §4.2 の申し送り)

環境: Windows 11 Pro 26200 / .NET 9 / 保存先は `%TEMP%`(C: = INTEL SSDPEKNW512G8・NVMe SSD)。
計測プログラムはスクラッチパッドに置き、リポジトリには入れていない。

**測定 1 —— DLL レベルの前後比較**(Debug ビルドの `kxEdit.Core.dll` を修正前/修正後で差し替え、
`AtomicFile.Write` を直接呼ぶ。宛先を実在させて `File.Replace` 経路を通す。3 回の中央値・ms)

| サイズ | byte[] 前 | byte[] 後 | Stream 前 | Stream 後 |
|---|---|---|---|---|
| 1 MB | 9.6 | 10.0 | 10.7 | 10.0 |
| 10 MB | 22.2 | 23.4 | 21.8 | 22.0 |
| 100 MB | 139.0 | 148.6 | 153.8 | 155.1 |
| 200 MB | 284.1 | **258.6** | 288.3 | **273.7** |

n=3 ではばらつきに埋もれ、200 MB では「後」の方が速く出る。**これだけでは受容判断の根拠にならない**ので、
同一プロセス内で 1 反復ごとに交互に測る対照実験を別に行った(ブロック単位で測ると、NVMe の熱や
ライトバックの滞留が片側にだけ乗る)。

**測定 2 —— 交互・9 反復の中央値**(Release ビルドの対照プローブ・ms)

| サイズ | ステージングのみ 前<br>(WriteAllBytes) | ステージングのみ 後<br>(FileStream+Flush(true)) | 全体フロー 前<br>(stage+File.Replace) | 全体フロー 後 |
|---|---|---|---|---|
| 1 MB | 0.6 | 2.4 | 9.1 | **10.2** |
| 10 MB | 2.7 | 13.7 | 21.5 | **23.2** |
| 100 MB | 121.1 | 118.4 | 152.6 | **135.8** |

**読み**: fsync 単体のコストは 10 MB で +11 ms あるが、**差替まで含めた全体フローでは +1.7 ms**
(1 MB で +1.1 ms)に縮み、100 MB では**前より速い**。逆転の機序は測っていない
(想定: 差替段が同じ待ちを既に強いており、fsync を先に済ませた方が総額で安くなる)。
**想定は根拠にしない** —— 受容判断に使うのは「全体フローで最大 +1.7 ms」という測定値の方である。

**受容と判断した**。体感で分かる遅さではなく、`SerialBackupWriter` の Join 15 秒
(`SerialBackupWriter.cs:203`)に対しても無視できる(バックアップ 1 本あたり +1〜2 ms)。
設計 §4.2 の適用範囲(全書込に効かせる)は変更しない。

> **測定設計の訂正(レビュー指摘 1)**: 上の「測定 2」は**固定順の交互測定**であり、
> §10.9 が後から「この設計は NVMe の周期と位相ロックして偽の差を作る」と**自ら証明した設計**と
> 同じものである。したがって見出し値「**全体フローで最大 +1.7 ms**」と
> 「100 MB では**前より速い**」は、**偽と判明した設計の上に載っている**。
> **順序ランダム化で測り直した正しい数値は §10.10** にある(結論=受容は変わらないが、
> 「100 MB では前より速い」は**撤回**する)。本節は策定時の記録なので書き換えない。

**計測時の罠(自分で踏んだ)**: 最初の測定では 1 MB の全体フローが **75 ms** と出た。直前に
100/200 MB を連続で書いた直後で、ディスクがまだ吐いている最中の値だった。反復前に settle
(8〜30 秒)を入れると 9〜10 ms に落ち着く。**直前の I/O 負荷を落としてから測ること。**
また汚染された測定では 100 MB で **5,925 ms** の外れ値が 1 度出た(数 GB 連続書込後の NVMe の
ストール)。前後どちらの形でも起こりうるので、中央値で評価している。

#### 網が張れた範囲と、張れなかった範囲(変異 6 件を実測)

いずれもビルドの終了コード 0 / 0 warning を確認してからテストを走らせ、`git checkout --` で
復帰(`git status` が空)を確認している。

| # | 変異 | 結果 |
|---|------|------|
| M-A | byte[] 版 `FileMode.CreateNew` → `FileMode.Create` | **生存**(Core 1404 全 PASS) |
| M-B | Stream 版の `fs.Flush(flushToDisk: true)` を落とす | Core 1404 中 **1 失敗** |
| M-C | byte[] 版の `fs.Flush(flushToDisk: true)` を落とす | **生存**(Core 1404 全 PASS) |
| M-D | `ArgumentNullException.ThrowIfNull(payload)` を落とす | Core 1404 中 **1 失敗** |
| M-E | `fs.Write(payload, 0, payload.Length)` → `Math.Min(payload.Length, 4096)` | Core 1404 中 **1 失敗** |
| M-F | Stream 版 `Flush(flushToDisk: true)` → `Flush()` | **生存**(Core 1404 全 PASS) |

```
# M-B
失敗 kxEdit.Core.Tests.IO.AtomicFileStreamWriteTests.Write_Stream_WriterClosesTheStream_FailsSafely
   Assert.Throws() Failure: No exception was thrown
失敗!   -失敗:     1、合格:  1403、スキップ:     0、合計:  1404

# M-D
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Bytes_rejects_a_null_payload_without_creating_a_tmp
   Assert.Throws() Failure: Exception type was not an exact match
   Expected: typeof(System.ArgumentNullException)
   Actual:   typeof(System.NullReferenceException)

# M-E
失敗 kxEdit.Core.Tests.IO.AtomicFileTests.Bytes_writes_a_payload_larger_than_the_stream_buffer_completely
   Assert.Equal() Failure: Values differ
失敗!   -失敗:     1、合格:  1403、スキップ:     0、合計:  1404
```

**張れなかった網 1 —— fsync そのもの**。電源を落とせないので、`Flush(flushToDisk: true)` が
実際にディスクへ届いたことは自動テストでは観測できない(設計 §6.2 のとおり)。M-B が殺せるのは
「Stream 版が writer から戻ったあと、`using` を抜ける前に `fs` へ触ること」までであって、
**それが `Flush(flushToDisk: true)` であることは押さえていない**(M-F が生存)。byte[] 版に至っては
flush の有無すら網が無い(M-C が生存)。**「fsync の網がある」とは書かない。**

**張れなかった網 2 —— `CreateNew` の弁別(試した結果)**。`CreateNew` と `Create` の差は
**宛先が既に存在するときにだけ**現れるので、弁別するには「tmp と同名のファイルを先に置く」以外に
手が無い。tmp 名は `<ファイル名>.<Path.GetRandomFileName()>.tmp` で、`GetRandomFileName` は
暗号乱数(32^11 通り)だから、テストから予測も固定もできない。実際に検討して退けた案:

- **差替段 seam を使う**: `OverrideReplaceStepForTest` はステージングが**終わった後**に呼ばれる。
  そこで tmp 名を知っても手遅れで、ステージング段には効かない。
- **ステージング名の seam を新設する**: 計画も「過剰」としている。production に差替点を 1 つ増やして
  得られるのは「乱数名が衝突したとき上書きしない」という、実際には起こらない事象の網。
- **`FileStream.Name` から tmp パスを採る**: Stream 版なら writer の中で tmp パスを知れるが、
  その時点でファイルは既に作られている。byte[] 版にはそもそもコールバックが無い。
- **IL を読む**(`MethodBody.GetILAsByteArray()` で `ldc.i4.1` / `ldc.i4.2` を探す): 挙動を
  1 つも見ていないうえ、同じ定数が引数列の他の位置にも現れるので弁別が壊れやすい。
  モック検証と同種で、**網に見えるが網ではない**。

したがって **M-A は生存する**(上表で実測)。`CreateNew` を採る根拠は設計 §4.1 の判断
(Stream 版と形が揃う・衝突時に他者の書込中ファイルを潰さない)のままで、**網では守られていない**。

> **範囲の訂正(レビュー指摘 4・5)**: 無網なのは `CreateNew` **だけではない**。
> `FileShare.None` → `FileShare.ReadWrite` の変異も**同じ理由で生存する**(実測)。
> ステージング段に観測面が無いという 1 つの原因から出ている以上、片方にだけ
> 「網が無い」と書くのは一貫しない。また上の「4 案を試した」は**閉じた論証へ差し替えられる**。
> どちらも §10.10 を参照。

**張れた網(挙動不変ネット 3 本 + 契約 1 本)**。既存の byte[] 版テストは 1〜3 バイトしか
書いておらず、書き手を `File.WriteAllBytes` から `FileStream.Write` へ替えたときの
**空 payload / null payload / 部分書込が丸ごと無網**だった。M-D / M-E が実際に生存→死亡したのが
その証拠である(「現在そうなっていること」と「変えられたら気付ける網」は別物 —— §10.2 の再確認)。

#### 計画と実物が食い違った点

1. **計画の `Bytes_staging_uses_CreateNew_and_does_not_clobber_an_existing_tmp` は採らなかった。**
   tmp の名前しか見ておらず、`FileMode.Create` への退化を 1 つも弁別しない
   (= テスト名が「CreateNew を固定した」と主張するのに中身が伴わない。**張れていない網を
   張ったと言う**形になる)。tmp の命名は既存の `*.tmp` グロブ assertion が押さえている。
   代わりに実際に弁別する 4 本(上記)を書いた。
2. **計画に無い `ArgumentNullException.ThrowIfNull(payload)` を足した。** `File.WriteAllBytes` は
   null で `ArgumentNullException` を投げていたが、`FileStream` 版は `payload.Length` で
   `NullReferenceException` になり、しかも空 tmp を作ってから消す。入口で型を揃えた
   (Stream 版の `ThrowIfNull(writer)` とも形が揃う)。
3. **計画に無い契約変更が 1 つ出た —— writer はストリームを閉じてはいけない。**
   `using var sw = new StreamWriter(stream)` は**下位ストリームごと Dispose する**ため、
   変更後は続く `fs.Flush(true)` が `ObjectDisposedException` になる(変更前は成功していた)。
   現行の唯一の production 呼出は `stream` へ直接書くだけなので実害は無いが、将来の呼出側が
   素直に書くとこの形になる。**黙って fsync を飛ばす側には倒さない**(飛ばすと M-13 の保証が
   静かに消える)。xmldoc に契約として明記し、`Write_Stream_WriterClosesTheStream_FailsSafely` で
   「安全側に倒れる(例外伝播・tmp 掃除・原本不変)」を固定した。
4. **`AtomicFile.Write(path, null!)` は CS0121 でビルドが割れる**(byte[] 版と `Action<Stream>` 版が
   曖昧)。`(byte[])null!` へキャストした。アナライザではなくコンパイラだが、
   「実装案のコードはそのままでは通らない」の**本ブランチ 6 件目**
   (RCS1194 / S2696 / S3398 / xUnit2031 / xUnit2020 / CS0121)。
5. **変更ファイルは 3 つになった**(計画は 2 つ)。Stream 版の契約ネットは
   `AtomicFileStreamWriteTests.cs` に置く方が置き場として自然なため。

> **記録の欠落(レビュー指摘 3)**: 上のリストに **6 点目**が抜けていた ——
> **`FileShare` が `Read` → `None` に変わっている**。`File.WriteAllBytes` は内部で
> `FileShare.Read` を使い、置き換え後は Stream 版に揃えて `FileShare.None`。
> 判断と影響は §10.10 に書く。本節は策定時の記録なので書き換えない。

#### 環境ノート(踏んだ罠)

変異を回す自作スクリプトで **Windows PowerShell 5.1 の `2>&1`(ネイティブコマンドの stderr 統合)**が
`NativeCommandError` を起こし、`$ErrorActionPreference='Stop'` と組み合わさってスクリプトが
**revert の前に中断**した。作業ツリーに変異が残ったまま次の作業へ進みかける形になる
(`git status` で気付いて復帰)。native exe に `2>&1` を付けない・revert は `finally` 相当に置く。

#### 検証

```
dotnet build kxEdit.sln -c Debug --no-incremental -warnaserror  →  0 個の警告 / 0 エラー (EXITCODE=0)
kxEdit.Core.Tests    成功!  合格: 1404 / 合計: 1404   (修正前 1400 + 新規 4)
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
dotnet csharpier check <変更 3 ファイル>  →  EXITCODE=0
```

#### L5 への申し送り

設計 §7 の項目 5「大きい文書の Ctrl+S で体感で待たされない」は上の実測(全体フローで最大 +1.7 ms)で
裏付けたが、**実機の体感確認は L5 のまま残す** —— 測ったのは `AtomicFile` 単体で、
エンコード変換やスナップショット取得は含んでいない。

### 10.9 Task 5 追補 — byte[] 版を Stream 版へ委譲した(M-C の非対称)

§10.8 の懸念「byte[] 版の flush には網が無く(M-C 生存)、Stream 版だけ契約ネット経由で
間接的に守られている」に対する仮説の検証。**仮説は成立したが、成立の仕方は
「M-C が死ぬようになる」ではなく「M-C が式として存在しなくなる」である。** この差は重要なので
下に分けて書く。

#### 採った形

```csharp
public static void Write(string path, byte[] payload)
{
    ArgumentNullException.ThrowIfNull(payload);
    Write(path, stream => stream.Write(payload, 0, payload.Length));
}
```

M-13 の後、2 つの `Write` はステージングの中身以外まったく同じ形になっていた
(FileStream を作る → 書く → `Flush(true)`)。byte[] 版は「payload を 1 回書く writer を渡した
Stream 版」そのものなので委譲する。**狙いは重複削減ではなく、ステージングの実装を 1 つに保つこと。**
`CreateNew` / `FileShare.None` / tmp の命名 / 失敗時ポリシーの決定点も 1 か所になる。

#### 変異の実測 —— 仮説はどこまで成立したか

| # | 変異 | 委譲前 | 委譲後 |
|---|------|--------|--------|
| M-A' | `FileMode.CreateNew` → `FileMode.Create` | 生存 | **生存**(変わらず) |
| M-B' | 唯一の `Flush(flushToDisk: true)` を落とす | 1404 中 1 失敗 | **1404 中 1 失敗**(変わらず) |
| M-D' | `ArgumentNullException.ThrowIfNull(payload)` を落とす | 1404 中 1 失敗 | **1404 中 1 失敗** |
| M-E' | ラムダ内で部分書込(`Math.Min(payload.Length, 4096)`) | 1404 中 1 失敗 | **1404 中 1 失敗** |
| M-F' | `Flush(flushToDisk: true)` → `Flush()` | 生存 | **生存**(変わらず) |
| **M-C** | **byte[] 版の flush 行だけを落とす** | **生存** | **式として存在しない** |
| **M-G'** | **委譲を戻し、byte[] 専用のステージングを flush 無しで再インライン**(13 行) | — | **生存** |

```
# M-B'(委譲後)
失敗 …AtomicFileStreamWriteTests.Write_Stream_WriterClosesTheStream_FailsSafely
   Assert.Throws() Failure: No exception was thrown
   Expected: typeof(System.ObjectDisposedException)

# M-D'(委譲後)
失敗 …AtomicFileTests.Bytes_rejects_a_null_payload_without_creating_a_tmp
   Expected: typeof(System.ArgumentNullException) / Actual: typeof(System.NullReferenceException)

# M-E'(委譲後)
失敗 …AtomicFileTests.Bytes_writes_a_payload_larger_than_the_stream_buffer_completely
   Assert.Equal() Failure: Values differ / Expected: 1048583 / Actual: 4096
```

**正確に何が良くなったか**: ステージングの flush は 1 か所しか無くなったので、
**「1 行消すだけで byte[] 経路だけが静かに fsync を失う」形が作れなくなった**。唯一の flush を
落とす M-B' は網が殺す。§10.8 で挙げた「Stream 版だけ間接的に守られている」非対称は、
守る対象が 1 つになったことで消えている。

**何が良くなっていないか(過大に書かないための注記)**: **byte[] 経路の fsync に網が張れたわけではない。**
M-G'(委譲を戻して flush 無しで再インライン)は**依然として生存する** —— byte[] 版を叩くテストは
flush を観測できないままだからである。委譲が減らしたのは**「片方だけを落とす」変異の表面積**
(1 行 → 13 行)であって、**観測面は 1 つも増えていない**。
[[net-absence-claims-are-also-verifiable]] の逆側: 張れていない網を張ったと書かない。

**M-A' / M-F' は §10.8 のまま受容**(コーディネーターの裁定どおり)。委譲後も生存することを実測で
確認済みで、生存の理由も §10.8 から変わっていない。

#### 挙動不変であることの確認(潰した懸念)

- **`ArgumentNullException` の paramName**。ガードを委譲先へ落とすと `payload.Length` で
  `NullReferenceException` になる(あるいは `writer` 側の名前に化ける)。入口に残したうえで、
  **`Assert.Equal("payload", ex.ParamName)` を網に足した**(実測で "payload" のままであることを確認)。
- **CS0121(overload の曖昧さ)は変わらない**。2 つの overload のシグネチャを触っていないので、
  `Write(path, null!)` は引き続き曖昧で、テストの `(byte[])null!` キャストもそのまま。
- **`TextFileService` の共有違反フォールバックが二重委譲にならないか**。ならない。フォールバックは
  *TextFileService* の層にあり(`:448` 付近の `catch (IOException ex) when (…)` が
  `Save(path, text, encoding, hasBom)` へ委譲する)、その先で `AtomicFile.Write(path, payload)` →
  `AtomicFile.Write(path, Action<Stream>)` と 1 段降りるだけで、`AtomicFile` へ戻る再帰は無い。
  `SaveTextBuffer_ShareViolation_FallsBackToInPlaceOverwrite` /
  `Save_falls_back_to_inplace_when_replace_blocked_by_share_lock` /
  `Save_does_not_truncate_original_when_unrecoverably_locked` はいずれも緑のまま。
- **`BackupStore` / `SessionLayoutStore`(byte[] 版の利用者)**。委譲は同一スレッド上で 1 段降りるだけ
  なので、`[ThreadStatic]` の差替段 seam の効き方も、`SerialBackupWriter` の専用ワーカー上で走ることも
  変わらない。差替段 seam の発火回数(`Invocations`)も 1 のまま(`AtomicFileRecoveryTests` 全緑)。
- 例外の型・順序・`FileMode.CreateNew`・`FileShare.None`・tmp の命名・失敗時ポリシー
  (`TryDelete` して伝播)はいずれも委譲先の同じコードなので変わらない。

**§10.2 の記述への補正**: 「M-2 の網は Stream 側だけを赤にし、byte[] 側は緑のまま = 網が経路を
弁別できている」は**委譲後は成り立たない**(経路が 1 本になったため、Stream 版の差替を
インラインへ戻す変異は byte[] 側のテストも赤にする)。網が弱くなったのではなく、
**弁別すべき 2 経路が無くなった**。§10.2 は当時の記録なので書き換えず、ここに補正を残す。

#### 性能 —— 差は出なかった(測定と、その途中で踏んだ罠)

**測定 A(同一プロセス内・順序ランダム化・15 反復の中央値・ms)**。DIRECT = 委譲前の形、
DELEGATED = 委譲後の形。どちらも FileStream(CreateNew, None) + Write + Flush(true) + `File.Replace`。

| サイズ | DIRECT | DELEGATED |
|---|---|---|
| 1 MB | 10.9 | 10.4 |
| 10 MB | 22.7 | 22.4 |
| 100 MB | 152.9 | 137.0 |

**測定 B(DLL レベル・修正前後の `kxEdit.Core.dll` を差し替え・3 回の中央値・ms)**

| サイズ | 委譲前 byte[] | 委譲後 byte[] |
|---|---|---|
| 10 MB | 22.2 | 22.4 |
| 100 MB | 153.6 | 152.4 |

**差は無い**(測定 A では委譲後の方が全サイズで速く出ているが、これも差ではなくばらつき)。
理論上の増分は `Action<Stream>` 1 個のアロケーションと仮想呼出 1 回である。

**踏んだ罠 —— 交互測定が device の周期と歩調を合わせる**。最初は「DIRECT → DELEGATED」の固定順で
交互に測っており、100 MB で **DIRECT 137.0 / DELEGATED 149.9** と出た(+13 ms)。しかしサンプル列を
見ると 2 系列が**完全に反相関**しており(片方が ~136 のとき他方が ~152)、同じ測定を繰り返すと
**順位が入れ替わった**(DIRECT 149.4 / DELEGATED 147.3)。NVMe 側に ~2 反復周期の速い/遅い状態があり、
固定順の交互測定はそこへ位相ロックする。**反復ごとに順序をランダム化**して解消した。
§10.8 の settle(直前の I/O を落とす)と併せて、この機械での I/O 測定には両方が要る。

#### 検証

```
dotnet build kxEdit.sln -c Debug --no-incremental -warnaserror  →  0 個の警告 / 0 エラー (EXITCODE=0)
kxEdit.Core.Tests    成功!  合格: 1404 / 合計: 1404   (本数は変わらない = 既存網の強化のみ)
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
dotnet csharpier check <変更 2 ファイル>  →  EXITCODE=0
```

変異 6 件はいずれも `git checkout --` で復帰確認済み(`git status` が空)。今回は §10.8 で踏んだ
中断事故を避けるため、**revert を `finally` に置いて**変異を回した。

### 10.10 Task 5 — 仕様レビューの反映(性能の根拠を健全な測定へ差し替え、記録の欠落を埋める)

仕様レビューは**要求 1〜6 すべて充足・Blocker / Major ゼロ**と裁定された。レビュアーは変異 12 件を
独立に当て直し、§10.8 / §10.9 の自己申告(生存 3 件を含む)との**食い違いはゼロ**だった。
以下は同レビューの 4 指摘 + 推奨 1 件の反映である。

#### 指摘 1 —— 受容根拠が、§10.9 が自ら偽と証明した測定設計の上に載っていた

**採った対応: 測り直した**(桁の議論への置き換えではない)。§10.9 で健全と確かめた設計
——**反復ごとに順序をランダム化**し、直前の I/O を settle させる——で、**M-13 の前後**を測り直した。
`WriteAllBytes + File.Replace`(前)と `FileStream(CreateNew,None)+Write+Flush(true) + File.Replace`
(後)を同一プロセス内で交互に回している。15 反復の中央値を**サイズごとに 3 回**取った(ms)。

| サイズ | run | BEFORE | AFTER | 差 |
|---|---|---|---|---|
| 1 MB | 1 / 2 / 3 | 9.4 / 10.2 / 10.1 | 10.8 / 9.9 / 10.5 | **+1.4 / −0.3 / +0.4** |
| 10 MB | 1 / 2 / 3 | 22.4 / 21.7 / 21.9 | 22.6 / 23.2 / 22.6 | **+0.2 / +1.5 / +0.7** |
| 100 MB | 1 / 2 / 3 | 137.8 / 151.2 / 148.9 | 153.0 / 135.7 / 137.7 | **+15.2 / −15.5 / −11.2** |

**読み直した結論**:

- **10 MB: 約 +1 ms**(+0.2〜+1.5)。3 run とも符号が正で、ここだけ差が再現する。
- **1 MB: 検出できない**(±1.5 ms 以内で符号が反転する)。
- **100 MB: 本機では検出できない**。run 間のばらつき(±15 ms)が効果を上回り、**符号が反転する**。
  100 MB のサンプルは ~136 ms と ~152 ms の 2 クラスタに割れており(§10.9 で見つけた device 側の
  周期)、中央値がどちらのクラスタに落ちるかで符号が決まってしまう。

**§10.8 からの訂正 2 点**:

1. 見出し値「全体フローで最大 +1.7 ms」→ **「10 MB で約 +1 ms、1 MB と 100 MB では測定分解能以下」**。
   受容の向きは変わらない(むしろ小さい)。
2. **「100 MB では前より速い」は撤回する。** あれは固定順交互測定が位相ロックした結果であり、
   健全な設計では符号が run ごとに反転する = **速くも遅くもなっていない(測れていない)**。
   §10.8 が「逆転の機序は測っていない(想定)」と書いて根拠から外していたのは正しかったが、
   **そもそも逆転自体が測定の産物だった**。

**§10.8「測定 1」(DLL レベル・3 回)も同じ弱さを持つ**が、こちらは**プロセスをまたぐので原理的に
順序ランダム化できない**(同一アセンブリの 2 版を 1 プロセスへ同時にロードできない)。健全化できる
のは同一プロセス内の A/B だけなので、上の測り直しを正とする。**測定 1 は参考値の位置へ格下げする。**

**§10.8 の「ステージング段のみ」の列(fsync 単体 +11 ms 等)も固定順である。**受容根拠には
使っていない(使ったのは全体フローの値)ため測り直していないが、**同じ設計の弱さを共有する**
数字として読むこと。

**受容は変わらない**。最大でも 10 MB で約 +1 ms、100 MB では測定分解能(±15 ms)に埋もれる。
`SerialBackupWriter` の Join 15 秒に対しても、文書保存の体感に対しても桁が違う。
なお fsync のコストを避けることは M-13 の修正内容そのものを捨てることなので、
**仮に測定可能なコストが出ていても受容以外の選択肢は「適用範囲を狭める」しかない**
(設計 §8 でバックアップ側を外す案を既に却下している)。

**測定 A の読みについて(§10.9 への補正・レビュー指摘の小)**: §10.9 の測定 A は 100 MB で
DELEGATED が 152.9 → 137.0(約 10%)速く出ており、これを「ばらつき」と 1 行で処理していた。
**より強い反証は測定 B(DLL レベル: 153.6 → 152.4)が再現していないこと**である。加えて、
上の表で BEFORE/AFTER の差が 100 MB で ±15 ms 振れることが判った以上、**この 10% は
2 クラスタのどちらに落ちたかを見ているだけ**と言える。§10.9 は当時の記録なので書き換えず、
ここに補正を残す。

#### 指摘 2 —— コメントが paramName の変更を「不変」と読ませていた

`AtomicFile.cs` の null ガードのコメントは、直前の文が「旧実装の `File.WriteAllBytes`」を主語に
していたため「paramName も "payload" のまま保つ」が**旧実装からの保存**と読めた。**偽である**:
`File.WriteAllBytes(string path, byte[] bytes)` の paramName は **`"bytes"`**(レビュアー実測)。
M-13 で **`bytes` → `payload` に変わっている**。

実害はゼロ(依存コードが無く、むしろ本メソッドの公開引数名と一致する方へ寄っている)。
commit メッセージ・§10.8・テストの xmldoc は**いずれも「型」としか言っておらず正しい**。
コメント 1 行だけを直した ——「保たれるのは**例外の型**と**tmp を作らないこと**で、paramName は
変わっている(`bytes` → `payload`)」。§10.1 以来の「結論は正しいが理由節が偽」の再発である。

#### 指摘 3 —— `FileShare` が `Read` → `None` に変わったことが記録に無かった

`File.WriteAllBytes` は内部で `FileShare.Read` を使う。M-13 以降は Stream 版に揃えて
`FileShare.None`。**§10.8 の「計画と実物が食い違った点」に 6 点目として入るべきだった**
(実装報告では口頭で挙げていたのに、記録に落ちていなかった)。

**判断は受容**: ステージング中の tmb を他プロセスが**読めなくなる**方向で、これは
Stream 版が既に本番で使っている形である。むしろ「AV や同期ソフトが書込中の tmp を掴んで、
続く差替が共有違反で落ちる」窓を狭める向きに働く。tmp は乱数名で、書き終えた直後に
`File.Replace` へ渡すだけなので、読ませる理由が無い。

#### 指摘 4 —— `FileShare.None` にも網が無い(新規の生存変異)

レビュアーの **N-5(`FileShare.None` → `FileShare.ReadWrite`)は生存する**。自分でも当て直して
確認した(`FileAccess.Write, FileShare.None` を 1 か所だけ置換・Core 1404 全 PASS・0 warning)。

理由は `CreateNew` とまったく同じで、**ステージング段に観測面が無い**こと。したがって
§10.8 の「張れなかった網 2」は **`CreateNew` と `FileShare.None` の 2 つ**に広げる。
片方にだけ「網が無い」と明記して他方に与えないのは、**同じ原因から出た 2 つの穴のうち
1 つだけを可視化する**ことになり一貫しない。

#### 指摘 5(推奨)—— `CreateNew` の「張れない」を閉じた論証にする

**採用した。** §10.8 は「4 案を試して駄目だった」という**列挙**で終わっており、
「5 案目があるかもしれない」を残していた。次の形なら空間が閉じる:

> `Create` と `CreateNew` の観測可能な差は「**開く時点でその名前が既に存在する場合**」にしか
> 現れない。その名前は暗号乱数(32^11)であり、production がその名前を外へ出すのは
> **`FileStream` ctor が走った後**だけである(writer が受け取る `FileStream.Name` /
> 差替段 seam の `tmp` 引数)。したがって「差を観測する → ctor 前に同名を置く →
> **ctor 前に名前を知る**」という連鎖が、**production へ seam を足さない限り原理的に切れる**。

レビュアーが追加で潰した周辺案: 8.3 短縮名エイリアス / `FileSystemWatcher` で競走する
(ctor は名前決定と作成を原子的に行うので窓が無い)/ ディレクトリ ACL 細工(両 disposition とも
同じ権限を要求する)/ 特殊名前空間。**同じ論証が `FileShare.None`(N-5)にもそのまま当たる。**

**「4 案試して駄目」より「ctor 前に名前を知る手段が production に無い以上、seam なしでは
原理的に不可能」の方が、将来の再検討を正しく打ち切れる**(逆に、**ステージング名の seam を
足せば両方とも網が張れる**ことも同時に言えている ——それを足すかどうかは別の判断)。

#### 申し送り(対応せず記録に留める)

- **Stream 版の `ArgumentNullException.ThrowIfNull(writer)` は Task 5 以前から無網**である。
  自分でも当て直して確認した(落としても Core 1404 全 PASS)。今回の変更が作った穴ではないので
  射程外とする。塞ぐなら `Assert.Throws<ArgumentNullException>(() => AtomicFile.Write(path,
  (Action<Stream>)null!))` の 1 本で足りる(byte[] 版の null 網と対になる形)。

#### 検証

```
dotnet build kxEdit.sln -c Debug --no-incremental -warnaserror  →  0 個の警告 / 0 エラー (EXITCODE=0)
kxEdit.Core.Tests    成功!  合格: 1404 / 合計: 1404
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
dotnet csharpier check src/kxEdit.Core/IO/AtomicFile.cs  →  EXITCODE=0
```

本 fixup のコード変更はコメント 1 か所のみ(テスト本数は変わらない)。変異 3 件
(N-5 / `ThrowIfNull(writer)` / 再確認分)は `finally` 付きで回し、`git status` が空に戻ることを
毎回確認している。

### 10.11 Task 6 — 設定の保存を `AtomicFile` 経由へ(M-11 前半)。仮説の当たりどころが想定とずれていた

`SettingsStore.Save` は全永続化のうち唯一 `AtomicFile` の外(`File.WriteAllText` 直書き)にいた。
`File.WriteAllText` は `FileMode.Create` = **開いた時点で原本を切り詰める**ので、書込中の失敗
(ディスクフル・電源断)は「切り詰められた settings.json」を残す。これを差替経由へ移した。
`Directory.CreateDirectory(dir)` は残している(`AtomicFile` はディレクトリを作らない)。

#### バイト列不変の仮説 —— どう検証したか

§5.1 は「変わらないはず」と書いて「想定のまま進めない」と釘を刺していた。**2 段で確かめた。**

**(1) 修正前後の実バイト列を突き合わせた(想定ではなく実測)。**
一時的な `[Fact]`(`TempByteDumpProbe`・検証後に削除)で、日本語・エスケープ対象文字
(`" ' < > & \` ・制御文字 `U+0007`・NBSP・サロゲートペア `U+1F600`)・日本語パスの `RecentFiles`・
`LastSession` を積んだ設定を `SettingsStore.Save` で書き、`File.ReadAllBytes` の結果を hex で
ダンプした。**修正前**の実装で 1 回、**修正後**の実装で 1 回。

```
before.hex  length=1544  SHA256 FB86D256990A13AF9CDDE6A4AAC10FD12197FAC5F813BAE7A7F70007B7B99B6C
after.hex   length=1544  SHA256 FB86D256990A13AF9CDDE6A4AAC10FD12197FAC5F813BAE7A7F70007B7B99B6C
IDENTICAL=True
```

**一致した。1 バイトも変わっていない。**

**(2) その一致が「何のおかげか」を測った。ここで想定が 1 つ外れていた。**
同じダンプを走査した結果:

| 観測 | 実測値 | 意味 |
|---|---|---|
| 先頭 4 バイト | `7B 0D 0A 20` = `{` CRLF SP | **BOM なし**。`File.WriteAllText` の既定は BOM を付けない |
| 非 ASCII バイト数 | **0** | 既定 `JavaScriptEncoder` が非 ASCII を全部 `\uXXXX` へ逃がす |
| 改行 | CRLF(`0D 0A`)が存在 | `WriteIndented` の改行は JSON ライタ側が決める。`File.WriteAllText` は改行変換をしない |

つまり **「`File.WriteAllText` と `SerializeToUtf8Bytes` で UTF-8 の符号化が食い違う」余地は
元から無かった**——出力が全部 ASCII なので、どちらの経路でも同じバイトにしかならない。
タスク指示が挙げた 3 つの疑いのうち、**実際に危なかったのは BOM の有無だけ**であり、
エスケープ差と改行差は「同じ `Options` が決めている」ため原理的に生じ得ない側だった。
**仮説は正しかったが、その理由の重みづけは想定とずれていた。**

#### 恒久ネットは snapshot ではなく「旧実装のレシピを毎回走らせる」形にした

§5.1 は「期待バイト列と一致することを見る」と書いていたが、実物は**期待バイト列を持たない**。
`Save_writes_the_same_bytes_as_the_previous_writer` は、同じ設定オブジェクトに対して

- 新実装: `SettingsStore.Save(path, s)`
- 旧実装のレシピ: `File.WriteAllText(legacyPath, JsonSerializer.Serialize(s, LegacyOptions))`

を**両方その場で走らせて** `File.ReadAllBytes` 同士を比べる。

> **訂正(§10.12 指摘 1)**: ここには当初「レシピ比較なら `Options` の変更は両辺に等しく効き、
> 書き手だけが変わったときに落ちる」と書いていた。**偽である。** テストは
> `SettingsStore.Options`(private)に届かず複製 `LegacyOptions` を持つため、`Options` を変えると
> **左辺だけが動いて赤くなる = snapshot と同じ壊れ方をする**(実測)。レシピ比較を採る理由は
> 弁別能力ではなく、**失敗時に差分の由来が両辺の生成過程から読める**ことと、期待値の更新が
> 「レシピの再実行」で済むことである。結論(レシピ比較を採る)は変えていない。

加えて相対比較では捕まらない絶対条件——
BOM が無いこと・`File.ReadAllText` のデコード結果と一致すること・`Load` で読み戻せること——を
別に置いている。

**この網が空でないことを変異で確かめた。** `Save` を「BOM 3 バイトを前置してから書く」へ変異
させると `Save_writes_the_same_bytes_as_the_previous_writer` が落ちる(実測)。
バイト列不変の網は**修正前後どちらでも緑**になる性質のものなので、変異を当てないと
「何も見ていない網」と区別が付かない。

#### `AtomicFile` を通っていることの網は修正前に赤を実測した

`Save_goes_through_AtomicFile_and_leaves_the_original_when_the_replace_step_fails` は差替段の
seam(`AtomicFile.OverrideReplaceStepForTest`)に「原本に触れずに投げる」フックを張り、

- `Assert.Throws<IOException>` —— `Save` が例外を伝えること(`Assert.Throws` は**厳密型一致**なので、
  復旧枝へ入って `AtomicReplaceFailedException` に化けた場合も落ちる)
- `Assert.Equal(1, scope.Invocations)` —— seam が**実際に発火した**こと。`[ThreadStatic]` なので
  張ったスレッドと書込スレッドがずれると黙って既定実装が走る。事後状態(原本が残っている)だけでは
  不発と区別できない
- 原本が無傷であること・`*.tmp` が残っていないこと

を見る。**修正前の実装に対して実際に赤を出した**:

```
Assert.Throws() Failure: No exception was thrown
  at SettingsStoreTests.Save_goes_through_AtomicFile_and_leaves_the_original_when_the_replace_step_fails()
```

`File.WriteAllText` は `AtomicFile` を通らないので seam が発火せず、`Save` は素通しで成功する。

#### ★ 残した tmp は恒久残留する(Task 3 の実測をそのまま引き継ぐ)

差替が失敗し復旧も失敗した場合(M-12)、`AtomicFile` は tmp を**掃除せず残す**。settings.json の
tmp について、その行方は次のとおり:

- `*.tmp` を消すコードは **`BackupStore.SweepTempFiles` しか無い**。実装は
  `Directory.EnumerateFiles(dir, "*.tmp")` = **再帰なし・1 階層だけ**。
- 起動時に走るのは `BackupCoordinator.cs:346-347` の 2 呼出だけで、対象は `_sessionDir`
  (`%APPDATA%\kxEdit\backups\session-*`)と `_dir`(`%APPDATA%\kxEdit\backups`)。
  他の呼出元(`BackupStore.DeleteAll` / `DeleteSessionDir`)も `backups` 配下しか受け取らない。
- **`SettingsStore.DefaultPath` = `%APPDATA%\kxEdit\settings.json`** なので、その tmp は
  `%APPDATA%\kxEdit\` **直下**に落ちる。**どの sweeper の視野にも入らない。**

したがって **settings.json の tmp は恒久残留する**。「静かに消える」ではない。
`session-state.json`(`SessionLayoutStore`)と同じ性質で、`%APPDATA%\kxEdit\` 直下へ書く経路を
増やすたびに同じことが起きる。中身は**設定**であり、**最近使ったファイルの一覧(パス)を含む**。
本文は含まない。この事実は `SettingsStore.Save` の xmldoc にも書いた。

なお脆弱性レビューは同型の `session-state.json` について「`%APPDATA%` の既定 ACL はユーザー専用
なので実質リスクなし」と判定しているが、**「`%APPDATA%` は常にユーザー専用」と決め打たない**根拠も
§10.5 に記録されている(検証機には追加 ACE があった)。ここでは**事実の記録に留め**、リスク評価は
足していない。

#### ★ その保証はユーザーへ届かない

`SettingsStore.Save` の production 呼出側は **`MainForm.SaveSettingsSafe`(`MainForm.cs:987`)
1 か所だけ**で、`try { ... } catch { }` で例外を握り潰す。したがって
`AtomicReplaceFailedException.PreservedTempPath`(= 残した tmp の場所)は**誰にも伝わらない**。
Task 4 で組んだ「退避先を案内する」経路は文書保存(`TextFileService.Save` → `FileController`)に
しか効いておらず、設定は `BackupStore.Write` / `SessionLayoutStore.Save` と同じ「握り潰される側」に
並ぶ。本タスクで届くようになったのは**原本を壊さない**ところまでである。
**握り潰しの解消は B5(M-22)の担当**で、本タスクではコードを変えていない。
この限界も xmldoc に書いた。

#### 計画と実物が食い違った点

1. **src 側は計画のコードがそのまま通った**(`IO.AtomicFile.Write(path,
   JsonSerializer.SerializeToUtf8Bytes(settings, Options))`)。逸脱ゼロ。
2. **テスト側がアナライザに弾かれた —— 本ブランチ 7 件目。** `new JsonSerializerOptions { ... }` を
   テストメソッド内で組むと **CA1869**(シリアル化ごとに新インスタンスを作るな)。`static readonly`
   フィールドへ退避した。**テストプロジェクトにも production と同じアナライザ set が効く**ので、
   一時的な検証用 `[Fact]` も **S2699**(assert が 1 つも無い)と **CA1305**(`StringBuilder.AppendLine`
   の補間がロケール依存)で 2 回止まった。使い捨てのプローブでも素通りはしない。
3. **§5.1 の「期待バイト列と一致することを見る」を採らなかった**(上記「恒久ネットは snapshot では
   なく」)。計画より弱くはならず、`Options` 変更時の弁別能力の分だけ強い。
4. **§5.1 の「どちらも UTF-8」という根拠は正しいが効いていなかった。** 出力に非 ASCII バイトが
   1 つも無いため、UTF-8 かどうかは結果を左右しない。効いていたのは BOM の有無だけだった。

#### 事故 —— 変異の revert に `git checkout --` を使って自分の未 commit 実装ごと消した

BOM 変異を戻すのに `git checkout -- src/kxEdit.Core/Settings/SettingsStore.cs` を
(中断対策として)テスト実行と同じコマンドに繋いで置いた。**HEAD に戻るので、Task 6 の実装
そのものも一緒に消えた。** CLAUDE.md 環境ノートの「`Copy-Item` で退避せず `git checkout --` で
戻す」は**変異対象が既に commit 済みである**ことを暗黙の前提にしている。実装直後・未 commit の
状態で変異を当てるときは、**先に commit する**か、**変異を Edit で逆適用する**。
実害は再適用のみ(diff から復元・再測定で全緑を再確認)。

#### 懸念(受容・記録に留める)

- **fsync が 1 回増える。** `SaveSettingsSafe` の呼出は「設定ダイアログ OK」と「終了時」の
  2 か所だけ・約 1.5 KB なので体感差は無い(M-13 の測定では 1 MB でも分解能以下)。
  > **訂正(§10.12 指摘 2)**: 「2 か所だけ」は**偽**。`MainForm.cs:173` が
  > `saveSettings: SaveSettingsSafe` を `FileController` へ渡しており、
  > `FileController.RegisterRecent`(`:1571-1576`)経由で**ファイルを開くたび**(`:276` / `:405`)と
  > **「名前を付けて保存」のたび**(`:736`)にも走る。正しくは「**ファイルを開くたびに UI スレッドで
  > `FlushFileBuffers` が 1 回増える**」。受容の結論は変えないが、根拠は頻度ではなくサイズ
  > (約 1.5 KB)である。
- **`%APPDATA%` がフォルダーリダイレクトで UNC 上にある場合**、`File.Replace` の可否は
  `File.WriteAllText` と異なり得る。ただし同じディレクトリの `session-state.json` が既に
  `AtomicFile` 経由であり、失敗したときの観測(= 保存されない・無言)は修正前と同じ
  (`SaveSettingsSafe` が握るため)。新しい失敗の見え方は生じない。

#### L5

**不要。** SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)に
触れておらず、発声面・UI 面の変化はゼロ。M-11 が新設する `MessageBox`(§5.4)は Task 7 以降の
担当で、L5 はそちらで回収する。

#### 検証

```
（実装前・赤の確認）
dotnet test tests/kxEdit.Core.Tests --filter FullyQualifiedName~SettingsStoreTests
  → 失敗! -失敗: 1、合格: 24、合計: 25   (EXIT=1)
    Save_goes_through_AtomicFile_and_leaves_the_original_when_the_replace_step_fails [FAIL]
    Assert.Throws() Failure: No exception was thrown

（実装後）
dotnet build kxEdit.sln --no-incremental        →  警告 0 / エラー 0 (EXIT=0)
kxEdit.Core.Tests    成功!  合格: 1406 / 合計: 1406   (1404 + 新規 2)
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
dotnet csharpier check src/kxEdit.Core/Settings/SettingsStore.cs
                       tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs  →  EXIT=0

（変異 1 件・BOM 前置）
  → Save_writes_the_same_bytes_as_the_previous_writer [FAIL]  (失敗 1 / 合格 24)
```

grep は `-E " (error|warning) [A-Za-z]+[0-9]+"` を使い、終了コードで判定している。

### 10.12 Task 6 — 仕様レビューの反映(根拠 2 件が偽・生存変異 1 件・無網の枝 1 本)

仕様レビューは**実装本体(`src` 側)は要求どおり**・`Load` 無変更も diff で確認、と裁定した。
**バイト列不変はレビュアーが独立に 23 入力で検証し全一致**——こちらが試していない入力(空設定 /
明示 null / 孤立サロゲート / 非文字 / BiDi 制御 / `RecentFiles` 1 万件 = 1.86 MB /
禁則 100 万文字 = 6 MB 出力 / `int.MinValue` / `float.Epsilon`)でも崩れず、`NaN` / `+∞` は
**新旧とも同型の `ArgumentException`** で挙動一致。「非 ASCII バイトが 0」も全 23 ケースで再現し、
さらに `UnsafeRelaxedJsonEscaping` に替えると非 ASCII が 110 バイト出ることまで測って
「これは `Options` 依存の性質」と裏取りされている(§10.11 の結論を独立に補強)。

以下は同レビューの 4 指摘の反映。**結論はどれも変わらず、直したのは根拠と網である。**

#### 指摘 1 —— 「レシピ比較なら `Options` の変更を弁別できる」は成立しない

§10.11 とテストの xmldoc に「レシピ比較なら `Options` の変更は両辺に等しく効き、書き手だけが
変わったときに落ちる」と書いていた。**偽である。** テストは `SettingsStore.Options`(private)に
**届いていない** —— `SettingsStoreTests` は複製 `LegacyOptions` を独立に持ち、右辺はそちらを使う。
したがって `Options` を変えると**左辺だけが動く**。

レビュアーの実測(`SettingsStore.Options` へ `Encoder = UnsafeRelaxedJsonEscaping` を追加 =
意図した書式変更・書き手は不変):

```
失敗 Save_writes_the_same_bytes_as_the_previous_writer
  Assert.Equal() Failure: Collections differ
失敗! -失敗: 1、合格: 24、合計: 25
```

**snapshot とまったく同じ壊れ方をする。**

**レシピ比較を採る結論は維持する**が、理由を実態へ合わせた——**失敗時に差分の由来が両辺の
生成過程から読める**こと、期待値の更新が「レシピの再実行」で済むこと。テストの xmldoc と
§10.11 の当該箇所(訂正注記)を直した。**本プロジェクトが繰り返し踏んでいる
「結論は正しいが根拠が偽」の型**(§10.1 / §10.10 指摘 2 と同じ)。

#### 指摘 2 —— `SaveSettingsSafe` は 2 か所ではない(頻度の前提が偽)

§10.11 の懸念節に「`SaveSettingsSafe` の呼出は『設定ダイアログ OK』と『終了時』の 2 か所だけ」と
書いていた。**3 つ目の経路がある**(自分でも実コードで確認した):

`MainForm.cs:173` が `saveSettings: SaveSettingsSafe` を `FileController` へ渡し、
`FileController.RegisterRecent`(`:1571-1576`)が `_saveSettings()` を呼ぶ。その呼出元は

- `FileController.cs:276` / `:405` —— **ファイルを開くたび**
- `FileController.cs:736` —— **「名前を付けて保存」のたび**

(`_suppressRegisterRecent` で抑止されるのはセッション復元経路のみ。)

**受容の結論は変えない**が、根拠は「頻度が 2 回だから」ではなく「**約 1.5 KB だから**」である。
実態は「**ファイルを開くたびに UI スレッドで `FlushFileBuffers` が 1 回増える**」で、同じ節が
挙げているもう 1 つの懸念(リダイレクトされた `%APPDATA%`)では効き方が変わる。§10.11 に訂正注記。

なお **`SettingsStore.cs` の xmldoc「唯一の呼出側 `MainForm.SaveSettingsSafe`」は正しい**
(`SettingsStore.Save` の production 呼出元は 1 か所)。偽だったのは
「`SaveSettingsSafe` 自体がどれだけ走るか」の方だけなので、xmldoc は触っていない。

#### ★ 指摘 3 —— `Directory.CreateDirectory` の削除が生存する(自分で当てて確認した)

**修正前(生存)。** `Save` から `Directory.CreateDirectory(dir)` を削除しても全緑だった:

```
dotnet build kxEdit.sln --no-incremental   →  警告 0 / エラー 0 (BUILD=0)
kxEdit.Core.Tests   成功! -失敗: 0、合格: 1406、合計: 1406   (CORE=0)
kxEdit.App.Tests    成功! -失敗: 0、合格:  737、合計:  737   (APP=0)
```

計 2143 テストが 1 本も落ちない。原因は fixture の入力設計で、`SettingsStoreTests` の全 25 本が
`Path.GetTempPath()`(常に存在)を使い、seam テストだけが `Directory.CreateDirectory` を
**自分で先に呼んでいた**。つまり「**親ディレクトリが存在しない状態で `Save` する**」テストが
リポジトリに 1 本も無く、**初回起動(`%APPDATA%\kxEdit\` 不在)で設定が保存できなくなる経路が
無網**だった。

変更前も同じ穴なので**新規劣化ではない**。しかし本 commit は
「AtomicFile はディレクトリを作らないので、ここは残す」という**新しい主張**を足しており、
**その主張だけが網に支えられていない**状態だった。

**修正後(赤)。** byte テストの `path` を未作成の親配下
(`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json")`)へ変え、
**変異を当てたまま**走らせた:

```
失敗 Save_writes_the_same_bytes_as_the_previous_writer
  System.IO.DirectoryNotFoundException : Could not find a part of the path
    '...\Temp\1j5zqo4w.jbo\settings.json.c4w10ugr.obe.tmp'
    at kxEdit.Core.IO.AtomicFile.Write(String path, Action`1 writer)   AtomicFile.cs(98)
    at kxEdit.Core.Settings.SettingsStore.Save(String path, AppSettings settings)  SettingsStore.cs(161)
失敗! -失敗: 1、合格: 24、合計: 25
```

変異は `git checkout -- src/kxEdit.Core/Settings/SettingsStore.cs` で戻した(**今回は commit 済み
なので安全** —— §10.11 の事故はこれが未 commit だったことに起因する)。

#### 指摘 4 —— `File.Replace` 成功枝(`destExists == true`)が `SettingsStore` 側で無網

byte テストの `path` は存在しないランダム名なので `File.Move` 枝、seam テストはフックが投げる。
**本番の settings.json は通常「既存」なので、最も踏まれる枝を 1 本も通っていなかった**
(`AtomicFileTests` / `MainFormSmokeTests` が間接的に踏むので実害は低い)。

byte テストで `Save` を 2 回呼ぶ形にした。**1 回目は別内容(既定値)**で書くので、
2 回目の差替が実際に起きていなければバイト列比較が落ちる(「2 回呼んだだけ」にならない)。
指摘 3 の fixture 変更と同じテストで両方回収している。

#### 申し送り(Task 7 で意識すること)

- **`Options` は `Save` と `Load` で共有されている**(`SettingsStore.cs:10` を `:26` の
  `JsonSerializer.Deserialize` も使う)。分離すると将来 Save / Load が黙って乖離し得るが、
  **この結合を固定する網は無い**。Task 7 は `Load` を触るので、`Options` を Load 専用に
  分けたくなったときはここを思い出すこと(round-trip テストは同じ `Options` を通る限り
  乖離を検出しない)。

#### 検証

```
dotnet build kxEdit.sln --no-incremental        →  警告 0 / エラー 0 (EXIT=0)
kxEdit.Core.Tests    成功!  合格: 1406 / 合計: 1406
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737
dotnet csharpier check <変更 2 ファイル>  →  EXIT=0
```

テスト本数は変わっていない(既存 1 本の fixture を強化しただけ)。変異 1 件は復帰を確認済み。

### 10.13 Task 7 — 読込を 4 状態へ割った(M-11 後半)。「壊れている」を「読めない」と分けた理由

`Load` の catch-all を割り、`SettingsLoadStatus`(`Ok` / `Missing` / `Corrupt` / `Unreadable`)を
`out` で返すようにした。**このタスクは状態を返せるようにするところまで**で、退避(`.bad` への改名)と
通知は Task 8 / Task 9 の担当。`Program.cs` も含め既存の呼出は**全 25 か所を `out _`** にした
(計画は 23 か所と見積もっていた。実数は Core.Tests 22 / App.Tests 2 / production 1)。

`Save` は触っていない(Task 6 で完了済み)。**`Options` の Save / Load 共有(§10.12 の申し送り)は
そのまま**にした —— `WriteIndented` は読込に効かないので現時点の乖離は無く、分離すると
「Load 専用 Options」という新しい未固定点が増えるだけだと判断した。

#### 4 状態の切り分けが実際に効いていることの根拠

**署名が変わっただけで中身が割れていない実装(= 旧 catch-all をそのまま `status` へ写した実装)を
実際に一度書いて、テストを走らせた。** これが「4 状態のテストが状態を弁別しているのか、
それとも新 API を呼べているだけなのか」の分かれ目になる。

```csharp
// 赤の確認用に一度置いた「素朴な移植」(= 旧実装の catch-all をそのまま status にした形)
try { ... var s = Deserialize(...) ?? new AppSettings(); status = Ok; return Normalize(s); }
catch { status = Unreadable; return new AppSettings(); }
```

```
失敗 Load_reports_Corrupt_when_the_content_is_the_json_null_literal
  Assert.Equal() Failure: Values differ   Expected: Corrupt   Actual: Ok
失敗 Load_reports_Corrupt_for_unparsable_json
  Assert.Equal() Failure: Values differ   Expected: Corrupt   Actual: Unreadable
失敗! -失敗: 2、合格: 29、合計: 31
```

**旧実装の 2 つの潰し方が、それぞれ別のテストで別の値として現れた。**

| 観測点 | 弁別するテスト | 何と区別しているか |
|--------|----------------|--------------------|
| `Missing` | `Load_reports_Missing_when_the_file_does_not_exist` | 通知しない側。`Load` がファイルを**作らない**ことも同時に見る |
| `Missing`(初回起動の形) | `..._when_the_parent_directory_does_not_exist` | 親ごと不在 = 本番の初回起動。ここが `Unreadable` へ倒れると**毎回警告が出る** |
| `Corrupt`(パース不能) | `Load_reports_Corrupt_for_unparsable_json` | 素朴な移植では `Unreadable` になる = **退避が走らない** |
| `Corrupt`(`null` の 4 文字) | `..._when_the_content_is_the_json_null_literal` | 素朴な移植では `Ok` になる。**現状バグの本体** |
| `Unreadable` | `Load_reports_Unreadable_when_the_file_is_locked` | `Corrupt` と区別。ロック解除後に `Ok` + 元の `FontName` が読めることまで見て「**退避してはいけないファイルだった**」を示す |
| `Ok` | `Load_reports_Ok_and_reads_the_values_for_a_valid_file` | 非既定値(`FontName` / `WindowWidth`)なので既定値フォールバックと区別が付く |
| `Ok`(補正あり) | `Load_reports_Ok_when_the_values_only_needed_normalizing` / `Load_reports_Ok_for_valid_but_hostile_json`(11 入力) | 「補正が要った」を「破損」と混同しない |

`Missing` は指示どおり「非既定値を書いてから消す」ような準備をしていない。観測点は `status` で、
その既定値(`Ok`)とも他状態とも衝突しないので、**ファイルが無い状態そのもの**を作るだけで足りる。

#### `Unreadable` を `Corrupt` と分ける判断が Task 8 でどう効くか

Task 8 の退避は `Corrupt` でだけ走り、`settings.json` を `.bad` へ**改名**する。
`Unreadable` の実態は AV / 同期ソフト / 別プロセスによる一時的なロックで、**ファイルの中身は正常**
であることが多い。ここを `Corrupt` に潰したまま Task 8 を載せると、
**一時的にロックされただけの健全な設定を毎回 `.bad` へ改名し、既定値の新ファイルで上書きする** ——
無音リセットを直しに来て、より強い形のリセットを新設することになる。

`Load_reports_Unreadable_when_the_file_is_locked` は fixture に**非既定の `FontName` を持つ正常な
ファイル**を使い、ロック解除後の再読込で `Ok` + `"BIZ UDゴシック"` が戻ることまで見る。
「改名してはいけない対象だった」という主張が、`status` の値だけでなく**ファイルの中身**でも立つ。

#### 網にできなかったもの —— `Normalize` の例外経路

**張れなかった。** 直接の網は無い。以下は実際に試したこと。

1. **妥当だが敵対的な JSON を 11 種類**(`{}` / `RecentFiles:[null,null]` / `LastSession` の
   `null` / `{}` / `Tabs:null` / `Tabs:[null]` / 空白のみ `Path` / `int.MinValue` 混じりの負値群 /
   参照型を全部 `null` / 数値を全部範囲外 / 未知プロパティ)を投入 → **全 11 件が `Ok`**。
   `Normalize` は全枝が null 合体・`Math.Max` / `Math.Clamp`・null 要素 skip で書かれており、
   `Deserialize` が返しうるどのオブジェクトでも投げない。この 11 件は
   `Load_reports_Ok_for_valid_but_hostile_json` として残した。
2. **`Normalize` の先頭で強制的に投げるプローブ**を一時的に当てて、第 2 の catch が実際に
   そこを覆っているかだけ確認した(当てて戻す。ミューテーション検証ではなく到達確認)。

```
失敗 Load_reports_Ok_and_reads_the_values_for_a_valid_file
  Assert.Equal() Failure: Values differ   Expected: Ok   Actual: Corrupt
```

例外が `InvalidOperationException` として抜けず **`Corrupt` に化けた** = 旧 catch-all が持っていた
保護(破損 JSON 由来の NRE で起動時クラッシュしない)は残っている。ただし
**これは手で 1 回確かめただけで、恒久の網ではない。** `Normalize` の例外経路は現在の入力空間から
到達できず、網を張るには `Normalize` に seam を掘る = **仮定のために production の面を増やす**
ことになるので採らなかった。代わりに固定したのは**逆側**で、実害があるのはこちらである ——
「補正が要っただけの正常なファイル」を `Corrupt` へ倒すと、Task 8 が健全な設定を `.bad` へ改名する。
`Normalize` に補正しきれない枝が足された日は、この 11 入力の網が先に落ちる。

**併せて挙動が 1 つ変わっている。** 旧実装では `Normalize` の例外は「既定値を返す」で終わったが、
新実装では `Corrupt` = **Task 8 の退避対象**になる。現在到達不能なので実害は無いが、
`Normalize` に例外を投げうる枝を足すときは、それが「ファイルを改名してよい破損か」を考えること。

#### catch をどこまで広げるか —— catch-all のまま残した

`File.ReadAllText` 側・`Deserialize` + `Normalize` 側とも catch-all を維持した。
`OutOfMemoryException` のような「握ってはいけない例外」まで握る形だが、**ここでは握る方が安全**:

- **例外を素通しすると `Program.Main` が落ちる。** `SettingsStore.Load` の呼出(`Program.cs:22`
  —— **訂正**: 当初 `:20` と書いたが、本 commit が足した TODO 2 行で 2 行ずれた。§10.14 指摘 2)は
  `Application.SetUnhandledExceptionMode`(`:32`)と `CrashHandler`(`:35`)の配線**より前**にある。
  ここを抜けた例外は、ハンドラもクラッシュ記録もダイアログも無いまま起動を殺す。
- ~~巨大な settings.json での OOM は `Unreadable` 側に落ちる = **退避しない**ので、
  「読めなかっただけのファイルを改名する」事故にはならない。~~
  **訂正(§10.14 指摘 3): この保証は `ReadAllText` 段の OOM にしか当たらない。**
  `Deserialize` 段の OOM は `Corrupt` = **退避対象**へ落ちる。
- 握ってよい例外を前置で列挙する形は、§2.1 / 監査 §9 V-7 の「前置の列挙は原理的に漏れる」に触れる。

#### 計画と実物が食い違った点

1. **呼出数**: 計画 23 → 実数 **25**(Core.Tests 22 / App.Tests 2 / production 1)。
2. **`File.Exists` は失敗理由を返さない。** 親ディレクトリの ACL で拒否されても `false` を返すため、
   「権限が無くて読めない」は `Unreadable` ではなく **`Missing`(通知しない)** へ落ちる。
   **訂正(§10.14 指摘 4): これは「1 ケース」ではない。`File.Exists` が `false` を返す事由
   すべて**が同じ穴に落ちる(レビュアー実測: パスがディレクトリ / 長すぎるパス / 不正なパス文字 /
   空文字列パスの 4 例がすべて `Missing`)。
   安全側(退避も通知もしないので原本は動かない)だが、ユーザーには何も伝わらない。
   ACL で設定を扱えない件は**本ブランチ対象外の M-14** の担当なので、`ReadAllText` の例外種別へ
   判定を移して分類を先取りすることはせず、設計 §5.2 の形(存在判定 → 読込)のまま残した。
   xmldoc に明記してある。
3. **新規 `.cs` を Write ツールで作ると LF になり、CSharpier が
   `The file contained different line endings` で弾く**(`dotnet csharpier format` で解消)。
   ビルドもテストも通った後に出るので、ゲート前に `csharpier check` を掛けること。

#### 検証

```
dotnet build kxEdit.sln -c Release --no-incremental -warnaserror  →  警告 0 / エラー 0 (EXIT=0)
kxEdit.Core.Tests    成功!  合格: 1424 / 合計: 1424   (1406 → +18)
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516   (不変)
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737   (不変)
dotnet csharpier check <変更 5 ファイル>  →  EXIT=0
```

赤の確認は 2 段:(1)テストだけ先に書いた時点で **14 個の CS1501 / CS0103**(新 API 未実装)、
(2)素朴な移植で **`Corrupt` の 2 本が失敗**(上記)。緑は上のとおり。

### 10.14 Task 7 — 仕様レビューの反映(副作用の網が 1 枝しか無く、根拠 4 件が広すぎた)

仕様レビューは**要求 1〜8 すべて充足**と裁定した。レビュアーは境界入力 **38 種**を独立に実測し、
「素朴な移植」も再現して**§10.13 と逐語一致**の 2 失敗を得ている。§10.13 の主張はいずれも裏付けられた:

- **`Unreadable` が `Corrupt` に化ける入力は無い** —— 読込が try #1 に完全に閉じているため
  **構造的に交差しない**(§10.13 は「テストで区別できた」までしか言えていなかった。より強い根拠)。
- **`Normalize` が投げる入力はレビュアーも見つけられなかった** —— `Truncate` は null 耐性、
  `ClampColumns` は min/max が定数なので `ArgumentException` 不可能、`IsSelectableCodePage` は
  静的配列走査で `Encoding.GetEncoding` を呼ばない、`SessionTabRecord` は検証なしの positional record。
  §10.13 の「到達不能」は全枝追跡で追認された。
- 良い側の意外: **UTF-16LE/BE・UTF-32 の BOM 付き**正常 JSON は `Ok`(`ReadAllText` の BOM 自動検出)。
  **BOM なし UTF-16 は `Corrupt`**(UTF-8 復号で NUL 混じり = 実際に使えない)。分類は妥当と評価。

以下は 5 指摘の反映。**分類の判断はどれも変わらず、直したのは網 1 本と根拠 4 件である。**

#### ★ 指摘 1 —— 「副作用を持たない」が `Missing` 枝でしか張られていなかった

xmldoc と設計 §5.4 は「**判定して返すだけで、ディスクは書き換えない**」と宣言していたのに、
実際に張っていたのは `Missing` 枝の `Assert.False(File.Exists(path))` だけだった。
**Task 8 はまさに「`Corrupt` のとき原本を改名する」を足すタスク**なので、この不在は
**次のタスクで最も実害になる位置**にあった。

**修正前(生存)。** 3 枝すべてに `File.Delete(path)` を当てて全緑になることを自分で確認した
(`Corrupt` の null 枝 / `Ok` 枝 / `Corrupt` の catch 枝 = unparsable が通る道):

```
dotnet build kxEdit.sln -c Release --no-incremental   →  エラー 0 (BUILD_EXIT=0)
kxEdit.Core.Tests   成功! -失敗: 0、合格: 1424、合計: 1424   (EXIT=0)
kxEdit.App.Tests    成功! -失敗: 0、合格:  737、合計:  737   (EXIT=0)
```

**`Load` が読んだ設定ファイルを毎回削除しても、2161 テストが 1 本も落ちなかった。**

**修正後(撃墜)。** 3 本のテストに `Assert.Equal(original, File.ReadAllText(path))` を足し、
変異を当てたまま再実行:

```
失敗 Load_reports_Corrupt_when_the_content_is_the_json_null_literal
失敗 Load_reports_Corrupt_for_unparsable_json
失敗 Load_reports_Ok_and_reads_the_values_for_a_valid_file
  System.IO.FileNotFoundException : Could not find file '%TEMP%\....json'
    at System.IO.File.ReadAllText(String path, Encoding encoding)
失敗! -失敗: 3、合格: 40、合計: 43
```

変異を戻して全緑を確認済み(`git diff` で `SettingsStore.cs` が commit 状態と一致することも確認)。
この網は「消す」だけでなく「**中身を書き換える**」副作用も撃つ(その場合は `Assert.Equal` 失敗になる)。

#### 指摘 2 —— 行番号が**この commit 自身のせい**で陳腐化していた

xmldoc と §10.13 が引く `Program.cs:20`(Load)/ `:30`(`SetUnhandledExceptionMode`)は、
**同じ commit が足した TODO コメント 2 行で 2 行ずれ**、実際は `:22` / `:32`
(`CrashHandler` は `:35`)だった。**順序の主張(`Load` が先)は事実**なので行番号だけ直した。
[[stale-commit-hashes-before-github-flow]] と同型 —— **自分の変更が自分の引用を陳腐化させる**。

#### 指摘 3 —— OOM の保証が `ReadAllText` 段にしか当たらない

「巨大な settings.json は `Unreadable` = 退避しない側へ落ちるので原本を改名する事故にはならない」は
**無条件には成立しない**。第 2 catch も bare `catch` なので、`JsonSerializer.Deserialize`
(文字列 → UTF-8 トランスコード + オブジェクトグラフ構築)で OOM が出れば **`Corrupt` = 退避対象**へ
落ちる。`ReadAllText` は約 1GB 未満なら成功するため、**読めたがその先で落ちる帯は原理的に存在する**
(多 GB の fixture が要るので未実測・コード構造から確定できる事実)。

実害は低い(そのサイズの settings.json は壊れている扱いで構わない)ので**分岐は足さない**が、
xmldoc と §10.13 を「`ReadAllText` 段の OOM は `Unreadable` へ落ちる」に狭めた。
**catch-all を残す判断そのものはレビューでも妥当と評価**されている。

#### 指摘 4 —— 「区別しきれない 1 ケース」が 1 ケースではなかった

`File.Exists` が `false` を返す事由**すべて**が `Missing`(通知しない)へ落ちる。
レビュアー実測: **パスがディレクトリ / パスが長すぎる / 不正なパス文字 / 空文字列パス**の 4 例が
すべて `Missing`。いずれも安全側(原本を動かさない)なので判定は変えず、文言だけ事実へ合わせた。

#### 指摘 5 —— テストの rationale が成立していなかった(等価変異)

`Load_reports_Ok_when_the_values_only_needed_normalizing` の xmldoc に
「status を先に確定して補正前を返す実装で落ちる」と書いていた。**偽である。**
`Normalize` は `s` を **in-place で変異させて同じ参照を返す**ので「補正前を返す実装」が書けない。
レビュアー実測でも status 確定を前に出す変異・`return s` にする変異は**どちらも生存**(等価変異)。

このテストが実際に殺すのは「**`Normalize` の呼出そのものを消す**」変異(補正系 10 本が落ちる)。
文言をそちらへ直した。**§10.12 指摘 1 / §10.1 / §10.10 指摘 2 と同じ「結論は正しいが根拠が偽」の型**で、
本ブランチ 4 度目。

#### 申し送り(B4 の射程外・修正しない)

- **`{"FontSize":1e400}` が `Ok` かつ `FontSize = +∞` になる。** `Normalize` のガードは
  `if (s.FontSize <= 0f)` で **Infinity を通す**。破損 settings.json から起動時のフォント生成が
  失敗しうる経路。`Normalize` は Task 7 で無変更なので**既存バグ**であり、B4(保存の最終防衛線)の
  射程外。§9 の申し送りへ回した。

#### 検証

```
dotnet build kxEdit.sln -c Release --no-incremental -warnaserror  →  警告 0 / エラー 0 (EXIT=0)
kxEdit.Core.Tests    成功!  合格: 1424 / 合計: 1424   (不変)
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516   (不変)
kxEdit.App.Tests     成功!  合格:  737 / 合計:  737   (不変)
dotnet csharpier check <変更 2 ファイル>  →  EXIT=0
```

テスト本数は変わっていない(既存 3 本へ副作用の網を足しただけ)。変異 3 件は復帰を確認済み。

### 10.15 Task 8 — 退避と文言。計画の文言が「弱すぎた」理由

`SettingsStore.TryQuarantineCorrupt`(Core)と `SettingsStartup.Prepare`(App)を足した。
`Program.cs` は無変更(配線は Task 9)。**`Load` には副作用を足していない** ——
退避の呼出は `Prepare` の `Corrupt` 分岐 1 か所だけである。

#### 採った文言(実物)

```
[Corrupt・退避に成功]
設定ファイルが壊れていたため、既定の設定で起動しました。以前の設定は失われているので、必要な項目は設定し直してください。

壊れたファイルは次の場所へ退避しました。不要になったら削除してください:
  <退避先パス>

[Corrupt・退避に失敗]
設定ファイルが壊れていたため、既定の設定で起動しました。以前の設定は失われているので、必要な項目は設定し直してください。

壊れたファイルは退避できませんでした。kxEdit はファイルを開いたときや終了するときに設定を書き直すので、このまま使うと上書きされます。壊れた内容を残しておきたい場合は、先に次のファイルをコピーしてください:
  <原本パス>

[Unreadable]
設定ファイルを読み取れなかったため、既定の設定で起動しました。

kxEdit はファイルを開いたときや終了するときに設定を書き直すので、このまま使うと、読み取れなかったファイルは既定の設定で上書きされます。以前の設定を残したい場合は、先に次のファイルをコピーしてください:
  <原本パス>
```

パスはいずれも `SanitizeForDisplay.OneLine(path)` = **無害化はするが切り詰めない**。

#### 計画から変えた 4 点と、その理由

**(1) 「設定を変更すると上書きされます」は実物より弱かった(最重要)。**
§5.4 の文言案は「設定を変更すると上書きされる」だったが、実物はそうではない。

| 上書きの契機 | 場所 |
|---|---|
| **終了するたび**(キャンセルされなかった `OnFormClosing`) | `MainForm.cs:594` → `SaveSettingsSafe` |
| **ファイルを開く / 保存するたび**(最近のファイル更新) | `FileController.cs:1575` `RegisterRecent` → `_saveSettings` |

つまり**ユーザーが設定を 1 つも変えなくても** settings.json は書き直される。計画の文言を採ると、
「設定を変えなければ大丈夫」と読ませたうえで実際には終了だけで消える —— *より静かな*喪失を
案内文で作ることになる。文言を「kxEdit はファイルを開いたときや終了するときに設定を書き直すので」
= **実際の契機**に変えた。

**(2) 退避先パスを切り詰めない(計画の `260` を外した)。** §10.6 / §10.7 の「切ってよい側と
いけない側」をこの文言に当てると、**切ってはいけないのはパスの方**である。原本パス側を丸めてよかった
Task 4 と違い、ここでは原本も退避先も `%APPDATA%` 配下で、**ユーザーが他所から知る手段が無い**。
`260` は MAX_PATH に由来する数で、この文言が何を守るかとは無関係だった。

**(3) 退避に失敗した側と `Unreadable` 側でも「どのファイルか」を案内する。** 計画はどちらも
パスを載せない案だったが、「先にコピーしてください」は場所が判らなければ実行できない ——
§10.7 指摘 3(一番役に立つ事実が文言に入っていない)と同型である。案内するのは**原本**で、
**実在しない退避先は案内しない**(§10.6 (c))。`Corrupt` は `File.ReadAllText(path)` が成功した
後にしか出ないので、この原本は直前まで実在していた。

**(4) 語順**: 「次に何をすればよいか」を長いパスより前に置いた(§10.7 指摘 3)。
網 `Assert.InRange(guideAt, 0, pathAt - 1)` で固定してある。

#### `Unreadable` を退避しないことは、コード上どう保証されているか

**構造的な強制ではなく、位置と網の 2 つで保っている。**正直に書くと次のとおり。

1. `TryQuarantineCorrupt` の呼出は **`Prepare` の `case Corrupt:` の中 1 か所だけ**(production 全体を
   grep して確認)。`Unreadable` の分岐は `return` するだけで、退避の呼出を持たない。
2. `SettingsStore.Load` は副作用を持たない(§10.14 の網)。したがって「読んだだけで改名される」
   経路は存在しない。
3. `Prepare_warns_but_never_renames_an_unreadable_file` が観測面を押さえる。

**`TryQuarantineCorrupt(path, status, …)` にして構造的に封じる案は採らなかった。** App 層の判定を
Core の API 形状へ持ち込むうえ、「status 違いの no-op」と「退避の失敗」がどちらも `false` になって
呼出側から区別できなくなる。理由は xmldoc に残した。**この判断は脆弱性パスへ回す。**

#### 自分で見つけて直した fixture の欠陥 —— 網が別のことを見ていた

最初に書いた `Unreadable` の fixture は `FileShare.None` で原本を掴んでいた。**この形だと
`File.Move` 自身も共有違反で失敗する**ので、「`Unreadable` でも退避を呼ぶ」変異は
**「どのみち失敗するから」で素通しする** —— 網は「呼ばないこと」ではなく「呼んでも失敗すること」
しか見ていなかった。本タスクで最重要と指定された観測点が、そのままでは無網だった。

`FileShare.Delete`(読み取りは拒否するが改名は許す)へ変え、**その性質自体を実測で固定する**
fixture 検算テスト `A_delete_shared_lock_blocks_reading_but_not_renaming` を足した。
`Load` 側の `Load_reports_Unreadable_when_the_file_is_locked`(Task 7)は `FileShare.None` のままで
よい —— あちらの観測点は `status` であって改名の可否ではない。

#### 変異の実測(5 件・1 件生存)

CLAUDE.md §4-A はファイル I/O へのミューテーション検証を禁止しているが、本ブランチが §10.2 /
§6.4 で既に使っている例外条件(「厳密な挙動を保証する必要がある場合」)を適用した。適用範囲は
**「どの状態で改名するか」と「案内文がどのパスを指すか」に限り**、`Load` / `Save` / `AtomicFile` へは
広げていない。いずれも `--no-incremental` ビルドの終了コード 0 を確認してから実行し、
5 件とも sha256 で復帰を確認済み。

| # | 変異 | 結果 |
|---|------|------|
| M1 | `case Unreadable:` でも `TryQuarantineCorrupt` を呼ぶ | **殺** App 6 中 1 失敗 |
| M2 | `Corrupt`(成功)の語順を入れ替え、パスを案内より前へ | **殺** App 6 中 1 失敗 |
| M3a | `OneLine(quarantined)` → `OneLine(quarantined, 200)` | **殺** App 6 中 1 失敗 |
| M3b | `OneLine(quarantined)` → `OneLine(quarantined, 260)`(計画案の値) | **生存** App 6 全 PASS |
| M4 | 退避失敗側の案内を原本 → 退避先へ | **殺** App 6 中 1 失敗 |
| M5 | `File.Move(..., overwrite: true)` → `overwrite: false` | **殺** Core 3 中 1 失敗 |

```
# M1
失敗 …SettingsStartupTests.Prepare_warns_but_never_renames_an_unreadable_file
   Assert.False() Failure
# M2
失敗 …SettingsStartupTests.Prepare_quarantines_a_corrupt_file_and_points_at_it_in_full
   Assert.InRange() Failure: Value not in range
# M3a
失敗 …SettingsStartupTests.Prepare_quarantines_a_corrupt_file_and_points_at_it_in_full
   Assert.Contains() Failure: Sub-string not found
# M3b
成功!   -失敗:     0、合格:     6、スキップ:     0、合計:     6
# M4
失敗 …SettingsStartupTests.Prepare_does_not_point_at_a_quarantine_that_was_never_created
   Assert.DoesNotContain() Failure: Sub-string found
# M5
失敗 …SettingsStoreTests.TryQuarantineCorrupt_overwrites_the_previous_bad_file
   Assert.True() Failure
```

#### 網にできなかったもの —— 上限 260 の切り詰め(M3b)

**張れなかった。**この網を張るには退避先パスを 260 文字超にする必要があり、そのためには
MAX_PATH を越えるパスにファイルを作らなければならない。一時プローブで実測したところ、
**この開発機では 275 文字のパスを作成できた**(= 長パスが有効)。しかし同じことが CI の
ランナーで成立する保証が無く、成立しなければ fixture 作成の時点でテストが落ちる。
**「ローカルでだけ通る網」を足すのは、緑を根拠に使えなくする**ので採らなかった。

したがって fixture は退避先 250 文字(MAX_PATH 内)に留めてあり、**殺せるのは上限 249 未満まで**。
250〜260 の上限を付け直す変異は生存する。実害は小さい
(`%APPDATA%\kxEdit\settings.json` が 250 文字を越えるには `%APPDATA%` が 230 文字級である必要がある)
が、**「上限を外した」ことの網は上限 249 未満にしか効いていない**のが実態である。
[[net-absence-claims-are-also-verifiable]] の作法に従い、張れなかったことをここに書く。

#### 計画と実物が食い違った点

1. **文言 4 点**(上記)。とくに (1) は計画の文言が**事実として弱かった**もので、好みの問題ではない。
2. **テスト本数**: タスク本文の観測点は 8 個だが、実装は **9 本**になった
   (Core 3 / App 6)。増えた 1 本は fixture 検算の
   `A_delete_shared_lock_blocks_reading_but_not_renaming`。
3. **`case Ok: case Missing: default:` を 1 本のアームにまとめた**。計画は `default:` だけだったが、
   「警告しないのは Ok と Missing」という意図がコードから読めなくなる。将来 status が増えたときも
   ここへ落ちる(退避も通知もしない安全側)ことをコメントに書いた。
4. **計画のコードはアナライザに弾かれなかった**(本ブランチで初めて 2 回連続)。`switch` + 三項 +
   bare `catch` のいずれにも Sonar / Roslynator は何も言わなかった。

#### セキュリティ観点(実装時の自己確認・レビューは別エージェント)

- **`path + ".bad"` は危険な位置を指さない。**区切り文字を挟まない suffix 連結なので、`path` が
  `..` を含んでいても解決先は `path` と同じ親である。加えて `Corrupt` は
  `File.ReadAllText(path)` が成功した後にしか出ないので、到達時点の `path` は**実在する読める
  ファイル**を指していた —— 末尾が区切り文字・ディレクトリ・予約デバイス名のパスは `Load` の
  `File.Exists` か読込で `Missing` / `Unreadable` へ落ち、`Corrupt` には**到達しない**。
  長すぎるパスは `File.Move` が投げて `false` になるだけで、原本は動かない。
  「同じディレクトリに落ちる」は網でも押さえた(`Assert.Equal(dir, Path.GetDirectoryName(quarantined))`)。
- **`overwrite: true` が消しうるのは `<path>.bad` という決め打ちの 1 名だけ。**名前は入力から
  生成されない。**その名前が kxEdit の置いた `.bad` とは限らない**(ユーザーや他プログラムが
  同名ファイルを置いていれば消える)—— これは §5.4 の「最新の破損コピーだけを残す」を採った
  結果として受容する。宛先がディレクトリだった場合は `File.Move` が失敗して `false` を返す
  (消さない)ことは網で固定した。
- **退避が `Corrupt` とだけ結び付いていること**は上記のとおり「位置 + 網」で、構造的な強制ではない。

#### 申し送り(B4 の射程外・実装しない)

- **`Unreadable` のまま終了すると、案内した当のファイルが上書きされる。**§5.5 の判断
  (保存を止めない)は「設定を適用しました」の虚偽発声を避けるためのもので、**発声を伴わない
  `OnFormClosing` の保存だけを `Unreadable` のとき飛ばす**案はその理由に触れない。
  B5(M-22 = 設定保存失敗の通知)を触るときに併せて検討する価値がある。本タスクでは
  設計 §5.5 の決定どおり保存経路に一切触れていない。
- **`.bad` は掃除しない**(§9 のまま)。`%APPDATA%\kxEdit\` 直下なのでどの sweeper の視野にも
  入らない(§10.11 の tmp と同じ性質)。これは仕様であり、消すのはユーザーの判断。

#### 検証

```
dotnet build kxEdit.sln -c Release --no-incremental -warnaserror  →  0 エラー / 0 個の警告 (EXIT=0)
kxEdit.Core.Tests    成功!  合格: 1427 / 合計: 1427   (1424 → +3)
kxEdit.Editor.Tests  成功!  合格:  516 / 合計:  516   (不変)
kxEdit.App.Tests     成功!  合格:  743 / 合計:  743   (737 → +6)
dotnet csharpier check <変更 4 ファイル>  →  EXIT=0
```

赤の確認は 2 段。(1) テストだけ先に書いた時点で **18 個の CS0117 / CS0103 / CS8130**
(`SettingsStore.TryQuarantineCorrupt` 未定義・`SettingsStartup` 不在):

```
…SettingsStoreTests.cs(992,40): error CS0117: 'SettingsStore' に 'TryQuarantineCorrupt' の定義がありません
…SettingsStartupTests.cs(58,35): error CS0103: 現在のコンテキストに 'SettingsStartup' という名前は存在しません
BUILD_EXIT=1
```

(2) 実装後に上表の変異 5 件を当て、4 件が赤になることを実測(M3b は生存)。
