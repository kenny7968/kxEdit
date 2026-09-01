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
   伝播することを固定した。Stream 版は「フォールバックへ落ちれば byte[] 版 Save へ委譲されて
   seam をもう一度通る」ので `Invocations == 1` が観測点になる(2 なら落ちている)。
2. **ヘルパを静的メソッドにした**: 計画は `DestroyThenBlockRecovery` をテスト内のローカル関数に
   していたが、何も捕捉していないため static メソッドへ引き上げた(byte[] / Stream の 2 本で共用)。
   同時に、例外メッセージへ 3 引数(`tmp` / `dest` / `destExists`)をすべて埋め込む形にした。
   S1172(未使用パラメータ)を確実に避けつつ、失敗時の出力に実パスが出る。
3. **`Assert.IsNotType<AtomicReplaceFailedException>(ex)` は現状では冗長**。xUnit の
   `Assert.Throws<IOException>` は型完全一致なので、その時点で復旧枝に入っていないことは
   確定している。`ThrowsAny` へ緩められた場合に弁別を残す重ね掛けとして残し、その旨を
   テストの xmldoc に書いた(「この網は何を守っているのか」を後から読める形にする)。

#### 申し送り(Task 4 で回収すること)

1. **`PreservedTempPath` が実在しないケースがありうる。** 復旧の `File.Move` が
   `FileNotFoundException`(tmp まで失われていた)で落ちた場合も
   `AtomicReplaceFailedException` になり、メッセージは「書き込んだ内容は '…' に残してあります」と
   言い切る。**Task 4 の文言生成では `File.Exists(ex.PreservedTempPath)` で分岐すること**
   (実在しない退避先を案内するのは、単なる保存失敗より悪い)。`RecoveryError` の型で
   弁別してもよい。`AtomicFile` 側は設計 §3.2 どおりに保ち、コードは変えていない。
2. **Task 1 の申し送り(App 層の広い `catch (IOException)`)は「握り潰し」ではなかった。**
   `FileController.WriteToPath`(`:900`)の catch は `_prompt.Error($"保存できませんでした:
   {SanitizeForDisplay.OneLine(ex.Message, 200)}", …)` なので、`AtomicReplaceFailedException`
   のメッセージ(保存先と退避先の 2 パスを含む)は**ユーザーへ届く**。ただし
   **200 文字で切り詰められる**ため、長いパスでは退避先が末尾から落ちうる。Task 4 の主眼は
   ここになる。
