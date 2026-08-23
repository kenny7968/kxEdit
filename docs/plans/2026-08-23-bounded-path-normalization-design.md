# パス正規化を境界付きにする(Issue #48 / S-15)設計書

- 対象: [Issue #48](https://github.com/kenny7968/kxEdit/issues/48)(PR #47 由来の退行・S-15)
  + 同一機構の既存バグ 1 件(`RecentFilesList.Add`)
- 前提 main: `ceda783`(PR #47 マージ後)
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装中の精密化と実施記録の追記のみ行う。

## 1. 目的

`Path.GetFullPath` は、正規化後のパスに `~` が含まれると `GetLongPathName` を呼ぶ。これは実
ファイルシステム / ネットワーク呼び出しで、**境界が無い**。不達の共有に対して約 21 秒 UI
スレッドを止める。

UI スレッドから無境界の `GetFullPath` が消えることを本ブランチのゴールとする(A-16 を除く。
§6 参照)。

### 1.1 実測(2026-08-23・.NET 10.0.9)

各ケースで別ホストを使い、Windows の SMB 否定キャッシュによる相互汚染を排除した測定:

```
\\198.51.100.7\share\PROGRA~1\a.txt   21002 ms   <- ディレクトリー成分の ~
\\203.0.113.9\share\notes~.txt        21004 ms   <- ファイル名の ~
\\198.51.100.8\share\plain.txt            0 ms
```

**測定手順の注意**: 最初の測定では 3 ケースを**同一ホスト**で連続実行し、
`PROGRA~1` が 0 ms に見えた。これは Windows が不達ホストを否定キャッシュするためで、
「`~` がディレクトリー成分にあるときは発火しない」という誤った結論を一度出しかけた。
**この種の測定はケースごとにホストを変える。** 裏返せば、同じ不達共有を指す N 件は
21 秒 1 回で済み、**別々の**不達共有 N 件なら N×21 秒になる(§2 の増幅係数)。

### 1.2 誤認の出どころ

`src/kxEdit.App/FileController.cs:589-590` のコメントが

> `Path.GetFullPath` は `GetFullPathNameW` による名前解決のみで実 I/O を行わない

と断言している。**これが S-15 を通した誤認そのもの。** 本ブランチで訂正する
(コメントの訂正も成果物に含める)。

## 2. 発火点は Issue が挙げた 3 箇所ではない

Issue #48 は `TryNormalizeSavePath` / `SaveDocument` の `FindByPath` /
`SaveAsDocument` の `FindByPath` の 3 箇所を挙げた。**これは実際より狭い。**

`DocumentManager.FindByPath`(`DocumentManager.cs:106-113`)は照会パスだけでなく
**開いている全タブの `State.Path` にも `PathKey.For` を打つ**:

```csharp
string key = PathKey.For(path);
foreach (var d in _docs)
    if (d.State.Path is not null && PathKey.For(d.State.Path) == key)
```

= 呼び出し 1 回あたり `GetFullPath` が **1 + N 回**(N = 開いているタブ数)。したがって
不達共有上の `~` タブが 1 つ開いているだけで、`FindByPath` を呼ぶ**すべての経路**が止まる。
Ctrl+S / SaveAs だけでなく `TryOpenOrActivate`(「開く」「最近のファイル」「grep ジャンプ」
「hot exit 復元」)も対象。

同じ 1+N が `RecentFilesList.Add`(`src/kxEdit.Core/Text/RecentFilesList.cs:41-46`)にもある。
こちらは `RegisterRecent` 経由で**開くたび・保存が成功するたび**に走り、最近のファイルは
設定に永続するので、一度不達共有上の `~` パスを開けば以後ずっと踏む。

### 2.1 UI スレッドの無境界 `GetFullPath` 全数

| # | 場所 | 回数 | 由来 | 本ブランチ |
|---|------|------|------|-----------|
| 1 | `FileController.TryNormalizeSavePath`(SaveAs) | 1 | #47 退行 | 直す |
| 2 | `DocumentManager.FindByPath` → `PathKey.For` | 1 + タブ数 | #47 退行(新規呼出) | 直す |
| 3 | `RecentFilesList.Add` → `PathKey.For` | 1 + 最大 10 | **既存**(監査外) | 直す |
| 4 | `Core/Backup/OriginalPathValidator.Check` | 1 | 既存 = **監査 A-16** | **直さない**(§6) |

`AtomicFile` / `BackupStore` / `SessionLayoutStore` の `GetFullPath` は対象外。前者は到達性
プローブ通過後にのみ走り、後 2 者は `%AppData%` 配下(ローカル)を対象にする。

## 3. 中核設計 — 正規化の回数を「操作あたり 1 本」に落とす

無境界呼び出しを**境界付きに包む**前に、まず**数を減らす**。1+N を境界付きで包むだけ
(検討案 B)では、タイムアウト時のフェイルセーフをどちらへ倒しても副作用が残るため:

- **閉**(重複判定不能 → 保存拒否)に倒す: 無関係な不達タブが 1 つあるだけで、**ローカル
  ファイルの保存が拒否される**。
- **開**(重複なしとみなす)に倒す: A-7 (b)(同一ファイルを 2 タブが編集し、片方の Ctrl+S が
  もう片方を消す)が不達タブに対して復活する。

N 側を消せばこの二択自体が消える。残るのは「その操作が今まさに触ろうとしているパス 1 本」
だけになり、そこでのタイムアウト = 操作の中止は素直で、他を巻き込まない。

### 3.1 不変条件

> **`DocumentState.Path` は `null` か、正規化済みの絶対パスである。**
>
> **`AppSettings.RecentFiles` に本バージョンが書き込む項目は、正規化済みの絶対パスである。**

これは A-19(相対パスが `State.Path` に残り保存先が CWD 依存になる)が既に要求していた性質を、
明文化して比較側にも使う、という位置づけ。

### 3.2 `PathKey` を 2 契約に割る(`kxEdit.Core`)

小文字化の規則は 1 箇所に保ったまま、入力の契約だけ分ける。

```csharp
/// 生入力用。GetFullPath を通す(= 実 I/O を伴いうる)。
public static string For(string path);

/// 正規化済み絶対パス用。ToLowerInvariant のみ。ファイルシステムに触れない。
public static string ForNormalized(string fullPath);
```

`For` は `ForNormalized` を呼ぶ形に書き換える(規則の single source)。

`OrdinalIgnoreCase` 比較には**しない**。`ToLowerInvariant` + 序数比較と
`OrdinalIgnoreCase` は Unicode の一部で結果が異なるため、挙動不変を優先する。

### 3.3 `DocumentManager.FindByPath` の契約変更

引数を「正規化済み絶対パス」とし、比較を `ForNormalized` に替える。

```csharp
string key = PathKey.ForNormalized(path);
foreach (var d in _docs)
    if (d.State.Path is not null && PathKey.ForNormalized(d.State.Path) == key)
```

**ファイルシステム呼び出しは 0 回になる。**

### 3.4 `RecentFilesList.Add` の契約変更

同様に両辺を `ForNormalized` にする。既存 `settings.json` に残る未正規化エントリーは
dedup がやや緩くなる(同一ファイルが最大 1 件重複して並びうる)。データ損失は無く、
1 度開き直せば正規化済みで入り直すため、受容する。

### 3.5 不変条件の担保

`DocumentState.Path` の setter に、I/O を伴わない構造チェックを置く:

```csharp
set
{
    Debug.Assert(value is null || System.IO.Path.IsPathFullyQualified(value),
        "State.Path は正規化済み絶対パスであること(Issue #48 §3.1)");
    _path = value;
}
```

`IsPathFullyQualified` は純粋な文字列判定で FS に触れない。相対パス(= A-19 の再発)を
Debug ビルドで捕まえる網として置く。

**注意**: 既知事項 S-5(main の Core テストが Debug 構成で 4 件赤 = `WordBoundary.cs:258` の
`Debug.Assert`)がある。本アサートを足す前後で Debug 構成のテストを走らせ、**赤の件数が
増えていない**ことを確認する(S-5 由来の 4 件は本ブランチの対象外)。

### 3.6 非 null 代入 4 箇所の充足

| 箇所 | 値 | 充足 |
|------|----|------|
| `FileController.cs:236`(`LoadInto`) | `path` 引数 | **未充足** → §4 で入口正規化 |
| `FileController.cs:534`(SaveAs) | `full` | 済(`TryNormalizeSavePath` 出力) |
| `FileController.cs:764`(復元) | `safePath` | 済(`OriginalPathValidator.Check` 出力) |
| `FileController.cs:1008`(復元) | `normalized` | 済(同上) |

`LoadInto` の呼び出し元は 2 つ。`ReopenWithEncoding` は `doc.State.Path` を渡す(不変条件より
正規化済み)。`TryOpenOrActivate` は生パスを渡すので、そこを直す。

## 4. 境界付き正規化 seam

残る無境界呼び出しは 2 本(`TryNormalizeSavePath` と `TryOpenOrActivate` の入口)。これを
`IReachabilityProbe` の 3 つ目のメンバーとして境界付きにする。既存 2 本と同じ書式に揃える。

```csharp
public readonly record struct PathNormalizeResult(bool Ok, string Full);

/// パスを境界付きで正規化する。タイムアウト・失敗は (false, "")。
PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout);
```

実装は `FileReachabilityProbe` に置き、フェイルセーフ値は**ヘルパー側**に置く:

```csharp
internal static PathNormalizeResult RunNormalizeProbe(
    Func<PathNormalizeResult> work, TimeSpan timeout
) => WaitBounded(Task.Run(work), timeout, new PathNormalizeResult(false, string.Empty));
```

置き場所が load-bearing である理由は既存 2 本のコメントが記録しているとおり:
`WaitBounded(task, timeout, <定数>)` と直書きするとフェイルセーフ値が 1 トークンの引数に
なり、書き換えてもコンパイルが通り・ハングもせず・全緑で変異が生存する。`work` を
差し替えられる形にしておけば、完了しない `TaskCompletionSource` でタイムアウト経路を
決定的にテストできる。

- **タイムアウト値**: 既存と同じ 5 秒。到達可能なら 0 ms なので十分に緩い。
- **例外**: 現行 `TryNormalizeSavePath` の catch フィルタ
  (`ArgumentException` / `NotSupportedException` / `IOException` / `SecurityException`)を
  background 側の `work` の中へそのまま移す。#47 の V-2(32767 境界で素の `IOException`)対策を
  落とさない。
- **スレッド leak**: 不達 UNC ではバックグラウンドスレッドが 1 本、最大 21〜60 秒 leak する。
  既存 2 本と同じ受容(`FileReachabilityProbe` のクラスコメント)。

### 4.1 呼び出し 2 箇所

- `TryNormalizeSavePath` → seam 経由に置換。失敗時の文言は現行のまま
  (「パスが正しくありません: …」)。**タイムアウトは別文言**にする — 打ち間違いではなく
  到達不能が原因なので、同じ文言では利用者が入力を疑い続けることになる。
- `TryOpenOrActivate` 冒頭 → seam で正規化してから `FindByPath` / `LoadInto` /
  `RegisterRecent` へ流す。失敗時はエラー表示して `null` を返す(既存の「開けなかった」経路)。

`SaveDocument`(Ctrl+S)は不変条件により `doc.State.Path` が正規化済みなので、
**`GetFullPath` を 1 回も打たなくなる**。

## 5. テスト設計

自動テストで**実際の 21 秒ブロックは再現しない**(不達ホストに依存する)。代わりに
「境界があること」と「回数が減ったこと」を別々に固定する。

| 層 | 対象 | 網 |
|----|------|----|
| L1 | `PathKey.ForNormalized` | `ToLowerInvariant` 相当・FS 非依存(存在しないパスでも同一結果) |
| L1 | `RecentFilesList.Add` | 正規化済みパスの dedup が従来と同一・**未正規化エントリーは dedup されない**(§3.4 の受容を明示的に固定) |
| L3 | `FileReachabilityProbe.RunNormalizeProbe` | 完了しない `TaskCompletionSource` で**タイムアウトが (false, "") を返す**(既存 `RunFileExistsProbe` テストと対称) |
| L3 | `FakeReachabilityProbe` | `NormalizePathWithTimeout` の呼び出し回数と引数を記録 |
| L3 | `SaveDocument`(Ctrl+S) | **`NormalizePathWithTimeout` を 1 回も呼ばない**(= 回数削減そのものの網) |
| L3 | `SaveAsDocument` | seam のタイムアウトでダイアログへ戻り、保存が起きない |
| L3 | `TryOpenOrActivate` | seam のタイムアウトで `null` + エラー表示・タブを作りかけで残さない |
| L3 | `FindByPath` | 正規化済みパス同士の重複検知が従来と同一(A-7 (b) の網が生きていること) |

**ミューテーション検証(最終品質パスのスポットチェック)**:

1. `RunNormalizeProbe` のフェイルセーフを `(true, path)` へ変異 → タイムアウトテストが赤になるか。
2. `FindByPath` の `ForNormalized` を `For` へ戻す変異 → 「Ctrl+S が seam を呼ばない」テストは
   **赤にならない**(`For` は seam を経由しないため)。この変異を殺すには回数ではなく
   **`PathKey.For` 自体の呼び出し回数**を見る網が要る。設計段階で先に気づいた穴として記録し、
   実装時に L1 側で `For` / `ForNormalized` の弁別を固定する。
3. `DocumentState.Path` の `Debug.Assert` を外す変異 → 相対パス代入テストが赤になるか。

`kxEdit.App.Tests` の既存テストは `FakeReachabilityProbe` を使うため、インターフェイス追加で
コンパイルが通らなくなる箇所は Fake 1 ファイルのみ(`tests/kxEdit.App.Tests/Fakes/`)。

## 6. 非目標(YAGNI)

- **A-16**(`OriginalPathValidator.Check` の同期 I/O で hot exit 復元が凍結)は直さない。
  ユーザー判断で #48 単独スコープとした。§2.1 の #4 として残る。
- **`~` の前置ゲート**(パスに `~` が無ければ境界を張らない最適化)は入れない。
  `GetLongPathName` を呼ぶ条件は .NET の実装詳細であり、そこに正しさを預けない。
  `Task.Run` のオーバーヘッドはマイクロ秒で、避ける価値が無い。
- **独自のパス正規化**(`GetFullPath` を使わず自前で `..` / `.` / UNC を解決)は入れない。
  セキュリティ上の危険が大きく、`OriginalPathValidator` の前提も崩れる。
- **`RecentFiles` の永続データのマイグレーション**は行わない。読み込み時に一括正規化すると、
  起動時に不達パスぶんの無境界 I/O が走る = 直そうとしている問題そのものになる。

## 7. L5(実機 SR 検証)

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)には触れない。
ただし**エラーダイアログの文言を 1 つ足す**(§4.1 のタイムアウト文言)ため、CLAUDE.md §5 の
「判定に迷ったら必要に倒す」に従い、**L5 チェックリストに 1 項目だけ足す**:

- 到達不能な共有パスを「名前を付けて保存」に入力 → 5 秒後にタイムアウト文言が NVDA で
  読み上げられ、ダイアログへ戻ること。

PR #36〜#47 分の L5 と合わせて 1 回で実施する(監査 §8 手順 5)。

## 8. 申し送り

- **S-1**: A-16(§6)。本ブランチの seam は `IReachabilityProbe` にあり、
  `OriginalPathValidator` は `kxEdit.Core` にあるので、そのままでは使えない。Core 側に
  境界付き I/O を持ち込むか、検証を App 層へ引き上げるかの設計判断が要る。
- **S-2**: `RecentFiles` の未正規化レガシーエントリーは dedup されない(§3.4)。
  実害が観測されたら、読み込み時ではなく**メニュー構築時**に遅延正規化する案がある。
