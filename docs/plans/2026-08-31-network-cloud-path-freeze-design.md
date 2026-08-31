# ネットワーク/クラウド配下のパス喪失と UI 凍結(A-15 / A-16 / A-17)設計書

策定日: 2026-08-31 / ベース: main `eb911bd`(PR #56 マージ後)

一次資料は `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §4。本書は**策定時スナップショット**
(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行い、後日書き換えない。

## 1. 目的

v0.2 リリース前バグ監査 §4 の残り 4 件のうち、**ネットワーク/クラウドのパスに起因する
「無言のパス喪失」と「UI 凍結」** の 3 件を 1 テーマとして解消する。

| ID | 症状 | 場所 |
|----|------|------|
| A-15 | OneDrive Files On-Demand 配下が reparse point として拒否され、hot exit 復元で無言の「無題」降格(本文は残るがパス喪失) | `Core/Backup/OriginalPathValidator.cs` の `RejectIfReparsePresent` |
| A-16 | マップドネットワークドライブが不達のとき `OriginalPathValidator.Check` の同期 I/O で UI が長時間凍結 | 同上 + `Check` 入口の `Path.GetFullPath` |
| A-17 | grep 実行 / 参照ボタンの `Directory.Exists` が UI スレッドで SMB タイムアウト(〜60 秒)を待つ | `App/GrepController.cs:90`、`App/GrepDialog.cs:126` |

**A-18(grep ジャンプのオフセット不整合)は本ブランチに含めない。** 「grep のオフセットと
エディタのオフセットを同じ空間に揃える」という別種の設計判断を含むため、単独ブランチで扱う。

## 2. 策定時に行った実測(2026-08-31)

A-15 は監査時点で **[推定]** だった。実測で確定した事実と、確定できなかった事実を分けて記録する。

### 2.1 確定したこと

開発機で reparse point を実際に作って `.NET 9` の各 API の応答を測った。

| 対象 | `File.GetAttributes` | `FileInfo.LinkTarget` | `File.ResolveLinkTarget` | 実タグ |
|------|----------------------|------------------------|---------------------------|--------|
| junction | `Directory, ReparsePoint` | 解決先を返す | 解決先を返す | `0xA0000003`(surrogate) |
| カスタムタグ `0x00000123` | `Archive, ReparsePoint` | **`null`** | **`null`** | `0x00000123`(非 surrogate) |
| カスタムタグ `0x20000123` | `Archive, ReparsePoint` | **`null`** | **`null`** | `0x20000123`(**surrogate**) |

ここから 3 つが確定した。

1. **未対応タグの reparse point に対して `LinkTarget` / `ResolveLinkTarget` は例外を投げず `null` を返す。**
   クラウドプレースホルダーもこの枝に落ちる。
2. **`LinkTarget != null` は name surrogate 判定と等価ではない。** 非 Microsoft の surrogate タグ
   (`0x20000123`)にも `null` を返す。したがって `LinkTarget` ベースの判定に置き換えると、
   サードパーティ製フィルタドライバの surrogate が**現状より緩く**通る。
3. **カスタムタグの reparse point は管理者権限なしで作成できた**
   (`FSCTL_SET_REPARSE_POINT` + `REPARSE_GUID_DATA_BUFFER`、非 Microsoft タグ = bit31 が 0)。
   ファイルシンボリックリンクの作成は同じ環境で `IOException`(要管理者 / 開発者モード)。

コストも測った(10,000 回・同一ローカルファイル): `File.GetAttributes` 315 ms /
`FileInfo.LinkTarget` 378 ms / タグ読み 446 ms。**非 reparse なパスでは 3 者に有意差がない**
(`LinkTarget` は属性ビットで短絡するため)。

### 2.2 確定できなかったこと

- **実クラウドプレースホルダーの属性とタグ。** 開発機の `%OneDrive%`(`%USERPROFILE%\OneDrive`)は
  同期実体が無く(`desktop.ini` のみ・ルート属性は `ReadOnly, Directory`)、`G:` は普通の NTFS
  固定ドライブだった。Files On-Demand のプレースホルダーを作れないため、
  「hydrate 済みでも reparse 属性とクラウドタグが残るか」は測れていない。**L5 送り**(§7)。
- **不達のマップドネットワークドライブ上の各 API の実測。** 開発機にネットワーク割当が無い。
  §6 の受容と §7 の L5 項目で扱う。

### 2.3 策定中に踏んだ罠(実装時に繰り返さないため)

タグ取得を最初 `FindFirstFileW` の `dwReserved0` で書いたところ、junction に対して `0x00000000`
を返した。原因は `WIN32_FIND_DATAW` の**アラインメント**で、先頭 `DWORD` の後に `FILETIME`
(8 バイト境界)が来るため既定 `Pack` でパディングが入り、以降のフィールドがずれていた。
**この経路は採らない**(§3.1)。

## 3. 設計

### 3.1 A-15 — reparse tag の name surrogate 判定へ置き換える

現状の `RejectIfReparsePresent` は `FileAttributes.ReparsePoint` ビットだけを見て拒否する。
しかし塞ぎたいのは「攻撃者 JSON のパスが `C:\Windows\System32\...` へ解決されること」であり、
それを行うのは junction / symbolic link / mount point = Windows 自身が **name surrogate**
(タグの `0x20000000` ビット)と呼ぶ種別だけである。クラウドプレースホルダー・重複除去(DEDUP)・
WOF 圧縮・AppExecLink はこのビットを持たず、**名前を別の場所へ横取りしない**。

判定を次の形にする。

```
if ((attrs & FileAttributes.ReparsePoint) != 0)
{
    uint? tag = ReparseTagReader.TryRead(cursor);
    if (tag is null || IsNameSurrogate(tag.Value))
        return PathValidation.Rejected;
    // ここに来たのは「reparse point だが名前を横取りしないと積極的に判明した」場合だけ
}
```

規律は 2 つ。

- **ガードが開くのは積極的な判明時だけ。** タグを読めなかった場合は Rejected = 現状の挙動のまま
  (fail closed)。「読めなかった」と「安全だと分かった」を混ぜない。
- **拒否したいタグを列挙しない。** 既存クラス doc が事後条件の議論で確立した規律
  (「拒否したい綴りの列挙は原理的に漏れるので、許可する形だけを書く」)と同型で、
  ここでは **OS 自身の述語**(surrogate ビット)を使う。

belt-and-suspenders の `File.ResolveLinkTarget` による BlockedRoots 再照合は**そのまま残す**。

#### 実装配置

`src/kxEdit.Core/IO/ReparseTagReader.cs` を新設し、P/Invoke をここに隔離する。
**Core にとって初の P/Invoke** になるが、次の理由で許容する。

- 取得 API は `GetFileInformationByHandleEx(FileAttributeTagInfo)`。構造体は `DWORD` 2 本のみで、
  §2.3 のパディング事故が構造的に起きない。
- `CreateFileW` に `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS` を付けるので、
  **未ダウンロードのクラウドファイルを hydrate しない**(復元経路がダウンロードを誘発しない)。
  ディレクトリにも同じ呼び出しで対応できる。
- 失敗(ハンドルが開けない / API 失敗)は `null` を返し、呼出側が Rejected へ倒す。例外は投げない。

**`FileSystemInfo.LinkTarget` を使う P/Invoke なし案は採らない。** §2.1-2 の実測どおり
現状より判定が緩くなるため。この却下理由は実装時に再浮上しやすいので、`ReparseTagReader` の
クラス doc に実測値ごと残す。

#### 網

L1 に置く。カスタムタグの reparse point を実際に作って `OriginalPathValidator.Check` を通す。

| fixture | 期待 |
|---|---|
| タグ `0x00000123`(非 surrogate)を持つファイル | `Ok` ← **A-15 の本体** |
| タグ `0x20000123`(surrogate)を持つファイル | `Rejected` |
| junction 経由のパス(既存テスト) | `Rejected`(挙動不変) |
| reparse でない通常ファイル | `Ok`(挙動不変) |

CI で `FSCTL_SET_REPARSE_POINT` が通らない可能性があるため、既存の
`Check_Rejects_PathThroughJunction` と同じ **skip フォールバック**を付ける
(作成に失敗したら `return`)。ローカルでは実行される。

分類器(`IsNameSurrogate`)は純関数なので、タグ値を直接与える `[Theory]` でも網を張る
(実 fixture が skip された環境でもビット判定そのものは固定される)。

### 3.2 A-16 — 凍結を 2 か所で止める

#### (i) reparse walk の対象をリモート全体へ広げる

現状は `bool isUnc = forCheck.StartsWith(@"\\")` で UNC のときだけ walk を skip している。
これを `RemotePathDetector.IsRemote(forCheck)` に広げる。

- walk の契約はもともと**「ローカルドライブのみ対象」**であり、その根拠は
  「UNC はサーバ側 NTFS でクライアントから検査不能」だった。マップドネットワークドライブ
  (`Z:\`)も実体はサーバ側にあるので、**同じ根拠がそのまま当てはまる**。つまりこれは
  性能上の回避ではなく**契約の食い違いの是正**である。
- `RemotePathDetector` は `kxEdit.Core.IO` にあるので、Core → App の逆依存は生じない。
- **プローブを足すのではなく I/O 自体を消す**ので、フェイルセーフによる降格が新たに発生しない。

代償を明示する: **マップドドライブ上の junction が拒否されなくなる。** ただし既存クラス doc が
「subst / ネットワークドライブ割当はドライブ文字の許可リストでは原理的に閉じない」と受容済みで、
UNC 側は元から未検査。受容範囲の**形**は変わらず、境界がドライブ文字から
「リモートかどうか」へ移るだけである。

#### (ii) 入口の `Path.GetFullPath` を境界付き正規化の後ろに置く

`Check` 冒頭の `Path.GetFullPath` は名前解決のみで実 I/O を行わない —— **ただし正規化後のパスに
`~` が含まれる場合だけは `GetLongPathName` を呼び、不達共有で約 21 秒ブロックする**
(Issue #48 / S-15)。これは既存クラス doc が「A-16 の受容範囲」として明示的に残した部分である。

呼出側(`FileController` の 3 箇所: `RestoreFromBackup` / `RestoreDirtyFromBackup` /
path-only extras)が、既にある `IReachabilityProbe.NormalizePathWithTimeout` を**先に**通し、
その出力を `Check` へ渡す。

- `PathNormalizeStatus.Ok` 以外(`TimedOut` / `Invalid`)は `Check` を呼ばずに
  **各呼出側の既存 Rejected 経路と同じ扱い**にする(無題降格 / skip)。新しい分岐を増やさない。
- **`Check` 自身の正規化は残す。** クラス doc が「再正規化の順序が load-bearing」と明記しており、
  自衛としての正規化を外すと事後条件と BlockedRoots が照合する形が食い違う。事前に正規化済みの
  パスに対する 2 度目の `GetFullPath` は `~` を含まないので速い。

### 3.3 A-17 — grep の 2 か所を境界付きにする

`IReachabilityProbe` に `ProbeDirectoryExistsWithTimeout(string, TimeSpan)` を追加する。
実装は既存 3 本と対称に、フェイルセーフ値を骨格メソッド `RunDirectoryExistsProbe` に置く
(定数を直書きすると変異が生存する —— PR #49 の I-1 / I-3 の教訓)。フェイルセーフは
`false` = 「存在を確認できなかった」。

| 呼出点 | 変更 | フェイルセーフ時の挙動 |
|---|---|---|
| `GrepController.RunAsync:90` | `RemotePathDetector.IsRemote(folder)` のときだけ 5 秒プローブ | 既存の「フォルダが見つかりません」通知(挙動不変) |
| `GrepDialog.BrowseFolder:126` | 同上 | `SelectedPath` を初期設定しない(ダイアログは開く) |

ローカルパスは `Directory.Exists` 直呼びのまま = **挙動不変**。タイムアウトは 5 秒
(HIGH-6 / CSV-M-1 / `FileTimestampProvider` と同じ契約)。

注入は既存パターンに揃える。`GrepController` と `GrepDialog` のコンストラクタに
`IReachabilityProbe? probe = null` を足し、既定で `new FileReachabilityProbe()`
(`FileTimestampProvider` と同型)。`MainForm` の配線(`:186` / `:191`)は既定のままでよい。

## 4. 変更するファイル

| ファイル | 変更 |
|---|---|
| `src/kxEdit.Core/IO/ReparseTagReader.cs` | **新規**。P/Invoke でタグを読む・surrogate 判定 |
| `src/kxEdit.Core/Backup/OriginalPathValidator.cs` | `RejectIfReparsePresent` のタグ判定化・walk の skip 条件をリモートへ |
| `src/kxEdit.App/Abstractions/IReachabilityProbe.cs` | `ProbeDirectoryExistsWithTimeout` 追加 |
| `src/kxEdit.App/FileReachabilityProbe.cs` | 同実装 + `RunDirectoryExistsProbe` 骨格 |
| `src/kxEdit.App/FileController.cs` | 3 呼出点で境界付き正規化を前置 |
| `src/kxEdit.App/GrepController.cs` / `GrepDialog.cs` | `Directory.Exists` をリモート時のみプローブへ |

## 5. テスト方針(CLAUDE.md §5)

- **L1** — `ReparseTagReader`(実 reparse point・skip フォールバック付き)、`IsNameSurrogate` の
  `[Theory]`、`OriginalPathValidator` の A-15 fixture 群、walk skip がリモートで効くこと。
- **L3** — `FileController` の 3 復元経路(正規化 `TimedOut` / `Invalid` / `Ok`)、grep の 2 経路。
  Fake だけで固定すると実装の意味論違いを隠すため(監査 §9 の教訓)、
  `FileReachabilityProbeTests` に `ProbeDirectoryExistsWithTimeout` の**実 probe** テストを足す。
- **L4** — 不要(性能ゲートに影響する変更ではない)。
- **L5** — §7。**SR 経路(復元時の発声・grep 通知)に触れるため必須。**

### ミューテーション検証について

CLAUDE.md §4-A により、本ブランチは**原則実施しない**。`ReparseTagReader` は I/O 処理、
grep 配線はイベント配線で、いずれも §4-A の禁止側に当たる。例外として
`IsNameSurrogate` のビット判定(`0x20000000`)だけはスポットチェックの対象にしてよい
(判定 1 本にセキュリティ境界が乗るため)。

## 6. 受容とトレードオフ

1. **A-15 は誤降格を減らし、A-16 (ii) のタイムアウトは降格を増やす。** 方向は逆だが、どちらも
   本文は失われず(無題タブとして本文は復元される)、60 秒凍結よりはるかにましである。
2. **マップドドライブ上の junction が拒否されなくなる**(§3.2-i)。既存の受容範囲と同型。
3. **スレッド leak は PR #49 の受容算術を踏襲する。** `ProbeDirectoryExistsWithTimeout` は
   `Directory.Exists` を 1 本増やすだけで、grep は単発操作なので直列に積み上がらない。
4. **`RemotePathDetector.IsRemote` 自身のコストは未実測。** `DriveInfo.DriveType` が不達の
   マップドドライブでブロックしないことは、既存の `FileController.TryProbeFileExists` /
   `FileTimestampProvider` が同じ前提で UI スレッドから呼んでいる(= 本ブランチが新しく作る
   リスクではない)。ただし**前提であることは事実**なので、L5 で観測する(§7)。

## 7. L5(実機 SR 検証)チェックリストの骨子

実施表は `docs/plans/2026-08-31-network-cloud-path-freeze-l5-checklist.md` に別途起こす。
確認したいのは次の 4 点。

1. **OneDrive Files On-Demand 配下のファイル**を開いて hot exit → 再起動で
   **無題に降格せずパス付きで復元される**こと(A-15 の本体。§2.2 の未確定を潰す)。
   併せてプレースホルダーの属性とタグを実測して本書に追記する。
   dehydrated / hydrated の両方で見る。
2. **切断済みマップドネットワークドライブ**上の文書を hot exit → 不達状態で起動し、
   UI が長時間凍結しないこと。降格時に無言でないこと(§6-1)。
   ここで `RemotePathDetector` 自体がブロックしないことも観測する(§6-4)。
3. **grep** で不達のリモートフォルダを指定 → 5 秒で「フォルダが見つかりません」が**発声**されること。
   参照ボタンでダイアログが固まらないこと。
4. 上記いずれもローカルパスでの通常操作(復元・grep)に**退行がない**こと。

**このブランチの L5 は、[[l5-backlog-after-v02-audit]] の未消化 13 本とは別に必ず実施する。**

## 8. 申し送り

- **A-18**(grep ジャンプが未保存タブでディスク基準オフセットを使い誤った行を発声)は別ブランチ。
- **V-2〜V-6**(プレビューの CSP コメントが実在しない防御を謳う)も別ブランチ。
- `OriginalPathValidator` クラス doc の **V-m-1 / V-m-2 / V-m-3**(事後条件の穴・`\\?\unc\` の
  過剰拒否・ループバック admin share)は本ブランチでは触らない。§3 の変更がこれらの前提を
  動かしていないことだけ、最終ブランチレビューの脆弱性パスで確認する。
- 本ブランチが `RejectIfReparsePresent` の意味論を変えるため、**脆弱性レビューを前倒しで実施する**
  (CLAUDE.md §3-4 の「セキュリティ敏感面」該当)。
