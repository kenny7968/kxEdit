# B6「プレビューの CSP と仮想ホスト」設計書

- 日付: 2026-09-03
- 位置づけ: 傘設計書 `2026-08-31-v0.2-remaining-work-design.md` §4 の **B6**(v0.2 に入れる 6 ブランチの最後)
- 一次資料: `2026-08-22-v0.2-release-bug-audit.md` §9(V-2〜V-6)/ PR #57 の申し送り
  (`src/kxEdit.App/RemoteAwareDirectory.cs` の XML doc)/ 監査 §6 の M-23

## 1. 目的と射程

プレビュー(WebView2)の**仮想ホストと CSP のまわりで、実態と食い違っている点を全て潰す**。
射程は次の 4 つ。

| 区分 | 項目 |
|------|------|
| 挙動を変える | V-2(未マップ時の実 DNS 解決)/ PR #57 申し送り(無境界 `Directory.Exists`)/ V-3(`%2f` 保持)/ M-23(サイズ判定前の全文 string 化) |
| 挙動を変えない | V-4 / V-5 / V-6(**コメントが実在しない防御を謳っている**の訂正。V-4 のみ定数を 1 語削る) |

**この 4 つを 1 ブランチに束ねる理由**は、V-2 と PR #57 申し送りが同じ 1 行(`MarkdownPreviewForm`
の `Directory.Exists` → `SetVirtualHostNameToFolderMapping`)の**表と裏**だからである。
片方だけ直すと逆向きの意味論を持ち込む(§4.2)。V-3〜V-6 と M-23 は同じファイル群の
小変更で、レビューの読解コンテキストを共有できる。

## 2. 出発点(2026-09-03 時点の実コード)

### 2.1 マッピングの現状 — `src/kxEdit.App/MarkdownPreviewForm.cs:88-96`

```csharp
// 相対リソース（画像・ローカルリンク）を .md のフォルダから解決する。
if (!string.IsNullOrEmpty(_baseDir) && System.IO.Directory.Exists(_baseDir))
{
    core.SetVirtualHostNameToFolderMapping(
        MarkdownRenderer.PreviewVirtualHost,
        _baseDir,
        CoreWebView2HostResourceAccessKind.Allow
    );
}
```

この `Directory.Exists` は `Shown += async (_, _) => await InitAsync()` の継続、つまり
**UI スレッドで無境界に**走る。`_baseDir` は `MainForm` が `Path.GetDirectoryName(doc.State.Path)`
で作るので、**共有上の .md を開いた後にその共有が不達になるとプレビュー表示で固まる**。
A-17 の実測では到達不能 UNC の `Directory.Exists` は **21,002 ms** 返らない。

### 2.2 相対 URL の絶対化は `_baseDir` を見ていない

`MarkdownRenderer.Render(markdown, PreviewBaseHref)` は `MainForm.ShowMarkdownPreview` から
**常に `PreviewBaseHref` で**呼ばれ(`MainForm.cs:1689`)、`PreviewRelativeUrlExtension` が
本文中の相対 URL を `https://kxedit.preview/...` へ絶対化する。**`_baseDir` の有無は
この判断に一切入らない。**

したがって未保存タブ(`_baseDir` = null)では「絶対 URL は在るがマッピングは無い」状態になる。
これが V-2 の本体である。

### 2.3 CSP 定数 — `src/kxEdit.Core/Text/MarkdownRenderer.cs:71-93`

`PreviewCspHeader` は meta http-equiv 側と `PreviewCspHeaderInjector`(CSS レスポンスの
HTTP ヘッダ)の single source of truth。現物のうち本設計が触るのは 2 directive:

- `style-src 'self' https://kxedit.preview`
- `frame-ancestors 'none'`

### 2.4 M-23 — `src/kxEdit.App/MainForm.cs:1684`

```csharp
string markdown = doc.Editor.SnapshotText; // 編集中バッファ（未保存も反映）
```

サイズ cap(`MarkdownRenderer.MaxMarkdownChars` = 4,000,000 文字)を見るのは
`Render` の内部(`MarkdownRenderer.cs:185`)であり、**その手前で全文が string 化される**。
`EditorControl.TextLength`(`src/kxEdit.Editor/EditorControl.cs:438`)は既に在り、
材料化せずに文字数を聞ける。

## 3. 根拠の検証状態

**実測・文書・未検証を混ぜない**(memory: 結論ではなく根拠を検証する)。

### 3.1 文書で確定していること

WebView2 公式ドキュメント(`CoreWebView2.SetVirtualHostNameToFolderMapping`)より:

- **"There is no DNS resolution for host name."** — 仮想ホスト名は Chromium のホスト名
  正規化を通って内部で解決され、**DNS は引かれない**。
- `folderPath` に文書化された制約は **MAX_PATH 長のみ**。不存在パスに対する例外の記載は無い。
- 相対パスは **exe のフォルダー基準**で解釈される(本設計では絶対パスしか渡さないが、
  `Path.GetDirectoryName` が空文字を返す経路を弾く根拠になる)。
- マッピングの変更は**現在のページには適用されないことがある**(リロードが要る)。
  本設計の呼び出しは `NavigateToString` の前なので影響しない。

この 1 点目が本設計の要である。**マッピングを張れば DNS は起きない**のだから、
V-2 は「マッピングを張らない状態を作らない」ことで消える。

### 3.2 コードで確定していること

§2 に引用した 4 点(すべて現物の行を確認済み)。

### 3.3 未検証 — Task 0 で確かめる

1. `SetVirtualHostNameToFolderMapping` に**不存在フォルダー**を渡しても例外を投げないか。
2. **不達 UNC** を渡したとき、呼び出し自体が UI スレッドでブロックしないか
   (ブロックするなら §4 の設計は作り直し)。
3. `new Uri(PreviewBase, "..%2f..%2fsecret.txt").AbsoluteUri` が `%2f` を**エスケープのまま**
   保つか(V-3 のガードが機能する前提)。

**→ §13.1 で実測(2026-09-03)。結果: 1 は偽・2 は偽・3 は真。**
1 と 2 が偽だったので、**本節を前提にしている §4 は作り直しになる**(§13.1)。
上の 3 項目は策定時の記述としてそのまま残す。

### 3.4 推定のまま扱うもの

監査 §9 は V-2 の「実 DNS 解決 + HTTPS 接続」を**推定**としている。本設計は
**この推定の真偽に依存しない**: マッピングを常に張れば、未マップという状態自体が無くなる。
パケットキャプチャによる直接観測は射程外(§9)。

## 4. V-2 + PR #57 申し送り — 「マッピングは常に在る」を不変条件にする

> **⚠ 本節の前提は §13.1 の実測で偽と判明した(2026-09-03)。**
> 不変条件(マッピングは常に在る)は維持しているが、到達手段は **§13.2 が現行設計**である。
> 本節は策定時スナップショットとして残す(CLAUDE.md §8)。§4.1 の表の 1 行目
> 「存在確認しない」は成立しない —— `SetVirtualHostNameToFolderMapping` 自身が
> 実在確認を内蔵しており、不存在なら投げ、不達 UNC では 21 秒返らない。

### 4.1 設計

`Directory.Exists` を**削除**し、マッピング先を必ず決める。

| `_baseDir` | マッピング先 |
|------------|-------------|
| 非 null かつ非空 | `_baseDir`(**存在確認しない**) |
| null / 空文字 | `_userData` 配下の空フォルダー |
| 上記の登録が例外で失敗 | 同じ空フォルダー(fail-safe。§4.3) |

### 4.2 なぜ存在確認を消してよいか

今の `Directory.Exists` が生んでいる唯一の効果は「`_baseDir` が無ければマッピングを張らない」
= **V-2 の状態を作ること**である。フォルダーが無いことをマッピング側に伝える必要は無い
(要求はローカルで失敗して終わる。Task 0-1 で確認)。

PR #57 の申し送りは「プレビュー側を**ついでに**境界付きにしてはいけない。到達不能を
『フォルダーが無い』に畳む `RemoteAwareDirectory` の意味論は、V-2 のフェイルセーフとは
**向きが逆**だから」と書いている。**常に張る**ならその衝突自体が消える —— 到達不能かどうかを
判断する必要がなくなるので、境界付きプローブ(5 秒待ち)も導入しない。

不達な共有上の .md では、画像要求が WebView2 のネットワークスタック側で待つことになるが、
**UI スレッドは待たない**(L5 項目 3 で確認する)。

### 4.3 例外時は「未マップ」へ戻さない

`_baseDir` が MAX_PATH を超える等で `SetVirtualHostNameToFolderMapping` が投げた場合、
**何もせず抜けると V-2 の状態が復活する**。そこで空フォルダーで張り直す。
現状この呼び出しは `InitAsync` の広い `try/catch` の中にあり、例外はプレビュー自体の
失敗ダイアログになる —— つまり「画像が出ない」で済む話が「プレビューが開かない」に
なる回帰でもある。

捕捉する例外型は実装時に確定する(`ArgumentException` / `COMException` の想定。
アナライザが `catch (Exception)` を許さない可能性がある。memory: 計画コードがアナライザに
7 回弾かれた)。**握り潰さない**: fallback の登録も失敗したらそのまま外側の catch へ送る。

### 4.4 空フォルダーの置き場所と契約

`PreviewUserDataFolder` に `EnsureEmptyBaseFolder()` を足し、`{Path}\empty-base` を
idempotent に作って絶対パスを返す(ctor と同じ流儀)。

- **契約: このフォルダーには何も置かない。** マッピング先専用。
- 破棄は既存の `Dispose`(親を recursive 削除)が担うので、後始末の経路を増やさない。
- WebView2 のプロファイル実体は同じ親の下に作られるが、**マッピングはこのサブフォルダーに
  閉じる**ためプロファイルは露出しない。
- getter ではなくメソッドにする(ディレクトリ作成という副作用を名前で見せる)。

### 4.5 網

`MarkdownPreviewForm.InitAsync` は WebView2 実体を要求するので単体テストから触れない。
**しかし判断とフォールバックは純粋なロジックに出せる**(memory: 「網が無い」も検証対象)。

新設 `PreviewVirtualHostMapping`(App 層 `internal static`):

```csharp
internal static void Apply(string? baseDir, Func<string> emptyFallback, Action<string> map)
```

- `map` は `SetVirtualHostNameToFolderMapping(PreviewVirtualHost, folder, Allow)` の薄いラッパ。
  **デリゲートなので `CoreWebView2` インスタンス無しでテストできる。**
- App.Tests で張る網:
  - `baseDir` が null / 空 → `emptyFallback()` が渡る
  - `baseDir` が非空 → **実在しないパスでもそのまま渡る**(存在確認しないことの網)
  - `map` の 1 回目が投げる → `emptyFallback()` で 2 回目が呼ばれる
  - fallback も投げる → 例外が外へ出る(握り潰さない)
  - `emptyFallback` は**必要なときだけ**呼ばれる(baseDir 正常時にフォルダーを作らない)
- `PreviewUserDataFolder.EnsureEmptyBaseFolder()`: 実フォルダーが作られる / 2 回呼んでも
  投げない / 親の `Dispose` で消える(既存 `PreviewUserDataFolderTests` に追加)。

`InitAsync` に残るのは「`Apply` を呼ぶ 1 行」だけになる。この 1 行は L5 で観測する。

## 5. V-3 — 事後条件でエスケープ済み区切りを弾く

> **⚠ 本節のガードは置き場所が間違っている(2026-09-03・§14 で作り直した)。**
> `PreviewUrlResolver.TryResolve` は絶対 URL に触らないので、
> `![x](https://kxedit.preview/..%2f..%2fsecret.txt)` と書かれると素通りする(実測)。
> **§14 が現行設計**。本節は策定時スナップショットとして残す(CLAUDE.md §8)。

### 5.1 設計

`PreviewUrlResolver.TryResolve` の既存の事後条件(scheme / host / port / userinfo)に 1 条件を足す:

```csharp
// 解決結果のパスに %2f / %5c が残る = 区切り文字をエスケープで密輸している。
// Windows のファイル名に / \ は入らないので、正当な相対リソースは該当しない。
if (resolved.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
    || resolved.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
{
    return false;
}
```

書き換えなければ相対 URL のまま残る。プレビュー文書の origin は `data:text/html;...` = opaque
なので**相対 URL はそもそも解決されず要求が飛ばない**(A-2 の機構)。つまり
「絶対化しない」= 安全側である。

### 5.2 なぜ前置ガードにしないか

V-7 の教訓(監査 §9)そのもの。**前置ガードの列挙は原理的に漏れる**(`%2F` / `%2f` /
二重エスケープ / `Uri` の正規化順)。既に scheme / host / port / userinfo を解決結果側で
検査しているので、同じ場所に同じ流儀で足す。

### 5.3 網(Core.Tests)

- `..%2f..%2fsecret.txt` → 書き換えない
- `..%2F..%2Fsecret.txt`(大文字)→ 書き換えない
- `..%5c..%5csecret.txt` → 書き換えない
- **非退化の対照**: `my%20file.png`(空白入りファイル名)と `sub/dir/pic.png` は**従来どおり
  絶対化される** —— ガードが全部を弾く退化になっていないことを示す
- 既存の事後条件テスト(host / userinfo 等)が緑のままであること

**ミューテーション検証(スポット)**: 2 条件(`%2f` / `%5c`)を**1 つずつ**外して落ちることを
確認する(memory: OR ガードは条件ごとに 1 行ずつ変異させる)。CLAUDE.md §4-A の「有効」に
挙がる中核アルゴリズムではないが、セキュリティ境界の判定なので**厳密な挙動保証が要る箇所**
としてスポット実施する。

## 6. V-4 / V-5 / V-6 — CSP の記述を実態に合わせる

いずれも「**コメントが実在しない防御を謳っている**」型。次に CSP を触る人が誤った前提で
判断するのを止めるのが目的。

### 6.1 V-4: `style-src` から `'self'` を削除する

data: 文書の origin は opaque なので `'self'` は**何にもマッチしない**。実際に `<link>` を
通しているのは `https://kxedit.preview` の方。**削除して差し支えない**:
将来プレビュー文書を仮想ホスト経由で出すようになったとしても、その origin は
`https://kxedit.preview` であり明示的に列挙済みだからである。

`MarkdownRendererTests.cs:439` が `style-src 'self' https://kxedit.preview` を assert して
いるので、**ここが定数変更の網になる**(期待値を更新する)。
`MarkdownRenderer.cs:64-66` のコメント(「data: URI 起点の bootstrap でも動くよう保険」)を
「opaque origin では `'self'` が無効なので置かない」へ差し替える。

### 6.2 V-5: `frame-ancestors 'none'` は残し、コメントを訂正する

meta 配信の `frame-ancestors` は**仕様上無視される**(HTTP ヘッダでのみ有効)。
プレビュー文書は data: 起点でヘッダを注入できないため、現状**効いている経路は無い**。

**残す理由**: 実害が無く(`MarkdownPreviewForm` は iframe に置かれない)、将来ヘッダ経路で
文書を配信するときに効く。ただしコメントには「meta では無視される。現在これが効く経路は無い」
と書く —— 「多層防御が在る」と読める記述を消すことが本項目の目的である。

### 6.3 V-6: CSS レスポンスの CSP ヘッダは強制されない

CSP はドキュメントとワーカーにのみ適用される。CSS レスポンスに付けたヘッダは**強制されない**。
`@import` / `url(...)` を実際に縛っているのは**文書側**の `style-src` / `img-src` / `font-src`。

`PreviewCspHeaderInjector.cs:26-29` の XML doc から「styles.css レスポンス自体の CSP を
強化する defense-in-depth」「両者は intersect される」を削り、上の事実に置き換える。
**ヘッダの送出自体は残す**(single source of truth の定数を共有しており、害が無い)。

## 7. M-23 — 材料化の前に測る

### 7.1 設計

`MainForm.ShowMarkdownPreview` の先頭で `doc.Editor.TextLength` を見て、cap 超過なら
**`SnapshotText` を呼ばずに**既存と同じダイアログを出す。1G 文字級の文書での
未捕捉 `OutOfMemoryException` はこれで起きなくなる。

文言を 2 か所に持たないため、超過メッセージを Core 側へ切り出す:

```csharp
public static bool ExceedsMaxChars(int charCount);   // charCount > MaxMarkdownChars
public static string TooLargeDetail(int charCount);  // "マークダウン本文が上限を超えました(n/m 文字)"
```

`Render` 内の `throw new DocumentTooLargeException(...)` も `TooLargeDetail` を使うように
書き換える。**`Render` の cap は残す**(二重の壁。`Render` は将来 caller が増えうる)。

### 7.2 網(Core.Tests)

- 境界: `MaxMarkdownChars` ちょうど → `ExceedsMaxChars` は false / +1 → true
- `Render` が投げる例外の `Message` が `TooLargeDetail(n)` と**一致する**
  (App 側の事前チェックと catch 経路が同じ文面になることの担保)

App 側の 1 行(`TextLength` を見て早期 return)は L5(既存の MD-L-3 項目)で観測する。

## 8. テスト戦略(CLAUDE.md §5)

| 層 | 内容 |
|----|------|
| L1 Core.Tests | V-3 のガード(§5.3・スポット変異込み)/ CSP 定数(§6.1)/ M-23 の境界と文言(§7.2) |
| L3 App.Tests | `PreviewVirtualHostMapping.Apply` の 5 ケース / `EnsureEmptyBaseFolder`(§4.5) |
| L5 | §10 の 3 項目 + 既存 MD-L-3 の再確認 |

## 9. 射程外にするもの(理由付き)

- **仮想ホスト名の改名**(`.internal` / RFC 6761 の `.invalid` 等)。MS の推奨に沿う変更だが、
  **常時マッピングで DNS が起きなくなる**ため追加の防御価値がほぼ無い。定数・テスト・
  過去文書の参照更新コストの方が大きい。v0.2 後の再監査で扱う。
- **V-2 の直接観測**(パケットキャプチャで DNS クエリを見る)。修正が推定の真偽に
  依存しないため(§3.4)。
- **プレビューの外部変更検知・リロード**。傘設計書 §5 の M-18 と同じ理由で v0.2 に入れない。
- **`SetVirtualHostNameToFolderMapping` の `Allow` → `DenyCors` 化**。挙動変更の影響
  (CSS / 画像の読み込み経路)を測る L5 が別途要り、B6 の主題ではない。申し送りへ。

## 10. L5(実機 SR 検証)

傘設計書 §4.2 は B6 の L5 を「**V-3 の 1 項目のみ**」としていたが、**3 項目に増える**。
V-2 の修正方式(常時マッピング)と PR #57 申し送りの回収を同じブランチで行うため、
その 2 つの観測が要るようになった。**傘設計書からの逸脱として記録する**(CLAUDE.md §2)。

1. **V-3**: `![x](..%2f..%2fsecret.txt)` を含む .md をプレビュー → フォルダー外のファイルが
   読まれない(画像が出ない)。
2. **V-2**: 未保存タブに `![](pic.png)` を書いてプレビュー → 窓が開き、画像は「読み込めない」
   表示で終わる(ハングしない・外部へ出ない)。
3. **PR #57 申し送り**: 不達になった共有上の .md でプレビュー → **21 秒固まらず窓が即開く**。
   到達不能 UNC の再現手順は `2026-08-31-network-cloud-path-freeze-l5-checklist.md` に従う。

加えて既存の **MD-L-3**(4M 文字超でダイアログが出てプレビュー窓は開かない)を、
M-23 の変更で挙動が変わっていないことの確認として再実施する。

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)には
触れないが、プレビュー窓の開閉とダイアログの文言は SR 利用者に直接届くので、
NVDA を起動した状態で実施する。

## 11. 工程

CLAUDE.md §3 のフロー。傘設計書 §8 が **B6 をタスク時の脆弱性レビュー前倒し対象**に
指定しているので、Task 1 と Task 2 の完了時に脆弱性レビューを行う。

| Task | 内容 |
|------|------|
| 0 | スパイク(§3.3 の 3 点)。**①②が偽なら §4 を作り直す**ので最初に置く |
| 1 | V-2 + PR #57 申し送り(§4)→ **脆弱性レビュー** |
| 2 | V-3(§5)→ **脆弱性レビュー**(スポット変異込み) |
| 3 | V-4 / V-5 / V-6(§6) |
| 4 | M-23(§7) |

最終ブランチレビューは 2 パス(コード品質 / 脆弱性)を**別エージェント**で。
その後 `tools/pre-merge-check.ps1` → PR。

## 12. 申し送り

- **L5 は 3 項目 + 既存 MD-L-3 の再確認**として傘設計書 §7.1 の台帳へ起こす
  (傘設計書の「V-3 の 1 項目」を上書きする)。
- **`Allow` → `DenyCors`** の見直し(§9)は次リリースの候補。仮想ホスト名の改名と同じ棚。
- 本ブランチで得た知見は本書の実施記録節へ書き、**傘設計書には書き戻さない**
  (CLAUDE.md §8 のスナップショット原則)。
- B6 の完了をもって傘設計書は役目を終える。残る v0.2 作業は傘設計書 §6(コード変更を
  伴わない 5 件。**GHSA 4 件を含む**)と §7 の L5 一括実施。

## 13. 実施記録

### 13.1 Task 0 スパイクの実測(2026-09-03)

**結論: §3.3 の 1(不存在フォルダーで例外を投げない)と 2(不達 UNC でブロックしない)は
どちらも偽。§4「マッピングは常に張る(存在確認を廃止する)」は、この 2 つに依存しているので
成立しない。** 3(`%2f` 保持)のみ真。

環境: WebView2 Runtime `152.0.4191.53` / `Microsoft.Web.WebView2` 1.0.4022.49
(App と同版)/ .NET 9 WinForms。プローブはスクラッチパッドの使い捨てプロジェクトで、
リポジトリ外・実行後に削除。ログは `<TEMP>\wv2probe.log`。

#### 生ログ

```
WebView2 runtime: 152.0.4191.53
AbsoluteUri : https://kxedit.preview/..%2f..%2fsecret.txt
AbsolutePath: /..%2f..%2fsecret.txt
probe0.invalid -> OK (0 ms) [C:\Windows]
probe1.invalid -> System.IO.DirectoryNotFoundException: 指定されたパスが見つかりません。 (0x80070003) (2 ms)
probe2.invalid -> System.IO.DirectoryNotFoundException: 指定されたパスが見つかりません。 (0x80070003) (21004 ms)
probe3.invalid -> System.IO.DirectoryNotFoundException: 指定されたパスが見つかりません。 (0x80070003) (0 ms)
control Directory.Exists -> False (21002 ms) [\\198.51.100.9\share\nosuch]
background-thread call -> System.InvalidOperationException: CoreWebView2 members can only be accessed from the UI thread. (18 ms)
```

`probe0` = 実在フォルダー(陽性対照)/ `probe1` = `C:\no\such\folder\kxedit-probe`(①)/
`probe2` = `\\198.51.100.7\share\nosuch`(②。RFC 5737 の経路無し IP)/
`probe3` = `C:\` + `a` × 300(④)。

計画には無い 3 つ(`probe0` / `control` / `background-thread call`)は、
**測定が空虚でないことを確かめるために足した**もの(下記)。

#### 判定

| 観測 | 期待 | 実測 | 判定 |
|------|------|------|------|
| ① 不存在フォルダー | `OK` | `DirectoryNotFoundException` (2 ms) | **外れた** |
| ② 不達 UNC | `OK` かつ 1000 ms 未満 | `DirectoryNotFoundException` (**21,004 ms**) | **外れた**(例外・ブロックの両方) |
| ③ `Uri` の `%2f` | `AbsolutePath` が `/..%2f..%2fsecret.txt` | 同左 | 期待どおり |
| ④ MAX_PATH 超 | 何らかの例外(型を記録) | `System.IO.DirectoryNotFoundException` (0 ms) | 期待どおり(型を記録) |

#### 対照群 — 「例外が出た」が測定不良でないことの確認

1. **陽性対照 `probe0`**: 実在フォルダー `C:\Windows` は **OK (0 ms)**。
   API 自体は同じハーネス・同じホスト名形式で成功する。よって ①②④ の例外は
   「プローブが壊れている」ではなく**フォルダーの側の性質**に由来する。
2. **`control Directory.Exists`**: ② とは**別の**不達 IP(`198.51.100.9`)を使い、
   負のキャッシュが載っていない冷えた状態で `Directory.Exists` を測ると **21,002 ms**。
   `RemoteAwareDirectory` の doc にある 21,002 ms と一致する。
   ② の 21,004 ms はこれと同じ TCP 再送タイムアウトであり、
   **`SetVirtualHostNameToFolderMapping` が内部でフォルダーの実在確認をしている**ことを示す。

#### 分かったこと(§4 以降への含意。差し替え設計は本記録では決めない)

- **`Directory.Exists` を消してもブロックは消えない。** 21 秒の待ちは `Directory.Exists`
  固有ではなく、`SetVirtualHostNameToFolderMapping` 自身が同じ待ちを持つ。
  §2.1 の「`Directory.Exists` が UI スレッドで無境界に走る」は、
  **その次の行も同じ性質だった**というのが実態。
- **「存在確認せずに `_baseDir` を渡す」は成立しない。** 不存在なら例外になるので、
  §4.1 の表の 1 行目(存在確認しない)はそのままでは書けない。
  一方 **fail-safe 側(実在する空フォルダーへ倒す)は `probe0` のとおり成立する**ので、
  「マッピングは常に在る」という不変条件自体は保てる。壊れたのは*到達手段*であって*目標*ではない。
- **計画が挙げていた代替案「登録を `Task.Run` へ逃がす」は使えない。**
  背景スレッドからの呼び出しは
  `InvalidOperationException: CoreWebView2 members can only be accessed from the UI thread.`
  で弾かれる。**UI スレッド以外から呼ぶ選択肢は無い**ので、
  「先に境界付きで実在を確定してから、UI スレッドで実在するフォルダーだけを渡す」形しか残らない
  (`RemoteAwareDirectory` / `IReachabilityProbe` が既にその形の 5 秒契約を持つ)。
- **④ の例外型は ① と同じ `DirectoryNotFoundException`。** catch フィルタで
  「MAX_PATH 超」と「不存在」を型で弁別することはできない。
  ドキュメントが `folderPath` の制約を MAX_PATH 長のみと書いている(§3.1)のに対し、
  実際には**不存在でも投げる**。§3.1 の読みは「例外の記載が無い = 投げない」ではなかった。
- ③ が真なので、**V-3 のガードの前提(`%2f` がデコードされない)は保たれる**。
  §5 は本実測の影響を受けない。

### 13.2 §4 の作り直し(2026-09-03・ユーザー承認済み)

§13.1 の実測で §4.1 の表の 1 行目(存在確認せずに `_baseDir` を渡す)が成立しなくなった。
**不変条件「マッピングは常に在る」は維持し、到達手段だけを差し替える。**

#### 制約(実測から確定したもの)

1. マッピングは**実在するフォルダーしか受け付けない**(不存在は `DirectoryNotFoundException`)
2. その実在確認は **API 内部で UI スレッド同期に**走り、不達 UNC では 21 秒返らない
3. `CoreWebView2` は **UI スレッド専有**(背景スレッドから登録できない)
4. 実在フォルダーへの登録は **0 ms**

#### 採る形 —— 非同期の境界付きプローブ

`InitAsync` は既に `async` なので、実在確認を**スレッドプールへ逃がして await する**。
UI スレッドは 1 ミリ秒もブロックしない。

```csharp
// 実在確認は UI スレッドから外す。SetVirtualHostNameToFolderMapping 自身が
// 実在確認を内蔵しており(§13.1)、不達な共有では 21 秒返らないため、
// 「実在が確定したフォルダーだけを UI スレッドで渡す」形にする。
bool usable =
    !string.IsNullOrEmpty(_baseDir)
    && await Task.Run(() => RemoteAwareDirectory.Exists(_probe, _baseDir!));
if (IsDisposed || Disposing)
    return;

PreviewVirtualHostMapping.Apply(
    _baseDir,
    usable,
    _userData.EnsureEmptyBaseFolder,
    folder => core.SetVirtualHostNameToFolderMapping(
        MarkdownRenderer.PreviewVirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow));
```

- `RemoteAwareDirectory.Exists` は**ローカルは `Directory.Exists` 直呼び**(挙動不変・高速)、
  **リモートのみ 5 秒の境界付きプローブ**。grep と同じ seam・同じ 5 秒契約を使い回す。
- `IReachabilityProbe` は `MarkdownPreviewForm` の ctor で受け取る(`MainForm` が
  `new FileReachabilityProbe()` を渡す。`FileController` と同じ流儀)。
- `Task.Run` の継続は WinForms の同期コンテキストで UI スレッドへ戻る。
  await の直後に既存の流儀どおり `IsDisposed || Disposing` を見る。

#### PR #57 の警告との関係

`RemoteAwareDirectory` の doc は「プレビュー側を境界付きにすると未マップ状態ができて
V-2 を踏む」と警告していた。**その根拠は空フォルダーの fail-safe が消す** —— 到達不能を
「フォルダーが無い」に畳んだ結果は未マップではなく空フォルダーへのマッピングになる。
警告は「境界付きにするな」ではなく「**フェイルセーフとセットでなければするな**」だったと読む。

#### 受容する残余リスク

- **確認と登録の間に共有が落ちる競合**。`Apply` の catch(`IOException` /
  `UnauthorizedAccessException`)が空フォルダーへ倒すが、**その catch に入るまでに
  UI スレッドで 21 秒ブロックされうる**。窓は数ミリ秒差で開いた後なので影響は限定的だが、
  原理的には残る(登録を UI スレッド外へ出せない以上、塞げない)。
- **不達な共有では最大 5 秒、画像なしの表示になるまで待つ**。UI は応答するが、
  プレビュー窓の中身は空のまま。21 秒の凍結よりは良いが「即座」ではない。
- `UnauthorizedAccessException` は**未実測の想定**(アクセス拒否の共有を用意していない)。
  実測したのは `DirectoryNotFoundException` のみ。

#### B6 の主張がどう変わったか

「プレビューの 21 秒凍結を消す」ではなく、**「UI スレッドのブロックを消し、最悪でも
5 秒で画像なし表示へ倒す」**になった。PR description にはこの表現で書く。

#### L5 項目 3 の文言差し替え

§10 の項目 3(`:323`)は「21 秒固まらず窓が即開く」と書いているが、実測を受けて次に読み替える:

> 3. **PR #57 申し送り**: 不達になった共有上の .md でプレビュー → **窓は即座に開き、UI は
>    応答し続ける**(他のタブ操作・Esc が効く)。**最大 5 秒後に**画像なしの本文が表示される。
>    21 秒のフリーズが起きないことが合格条件で、「即座に本文が出る」ことではない。

#### 実装中に判明した残余リスクの追加(Task 1・2026-09-03)

- **到達可能だが 5 秒より遅い共有では、画像が黙って出ない。** 境界付きプローブが期限切れで
  false を返し、空フォルダーへ倒れるため。**従来は「長く固まってから画像が出る」だった**ので、
  ここは挙動が変わる(UI 凍結との引き換えとして受容する)。
  「B6 で画像が出なくなった」という報告が来たときの**第一容疑者**なので PR description に書く。
- **`MarkdownPreviewForm` の ctor 変更は公開 API の破壊的変更**(クラスが `public`)。
  リポジトリ内の呼び出し元は `MainForm.ShowMarkdownPreview` の 1 か所だけで実害は無いが、
  「挙動不変ではない変更点」として PR description に併記する。

## 14. §5(V-3)の作り直し(2026-09-03・Task 1 の脆弱性レビュー由来)

### 14.1 何が間違っていたか

§5 は V-3 のガードを `PreviewUrlResolver.TryResolve` の事後条件に置く設計だった。
**このガードは絶対 URL 形を一度も見ない。** `TryResolve` は
`Uri.TryCreate(url, UriKind.Absolute, out _)` が真の時点で `false` を返す
(`PreviewUrlResolver.cs:44`)ので、攻撃者が `..%2f...` の前に `https://kxedit.preview/` を
書き足すだけで迂回できる。しかも**画像は `SafeLinkExtension` も通らない**
(`SafeLinkExtension.cs:82` = `link.IsImage` は base へ委譲)。

レビュアーがビルド済み `kxEdit.Core.dll` に対して実測した出力:

```
IN : ![x](..%2f..%2fsecret.txt)                        → <img src="https://kxedit.preview/..%2f..%2fsecret.txt">
IN : ![x](https://kxedit.preview/..%2f..%2fsecret.txt) → <img src="https://kxedit.preview/..%2f..%2fsecret.txt">  ★同一
```

§5 のまま実装すると 1 行目だけが塞がり、2 行目は素通りする。**「V-3 を塞いだ」という宣言が
偽になる**ので作り直す。これはこのリポジトリが繰り返し踏んできた「嘘の安全宣言」の型である。

### 14.2 採る形 —— 全 `LinkInline` に対する事後条件へ移す

ガードを `PreviewRelativeUrlExtension.OnDocumentProcessed` へ移す。ここは**相対も絶対も
区別せず本文中の全リンク・全画像を通る**唯一の場所である。

```csharp
foreach (var link in document.Descendants<LinkInline>())
{
    if (PreviewUrlResolver.TryResolve(link.Url, out string? absolute))
    {
        link.Url = absolute;
    }
    // V-3: 相対・絶対の両方をここで覆う。TryResolve は絶対 URL に触らないので、
    // 事後条件をそちらに置くと絶対 URL 形が素通りする(§14.1 の実測)。
    link.Url = PreviewUrlResolver.NeutralizeEncodedSeparators(link.Url);
}
```

`NeutralizeEncodedSeparators` の契約:

- **preview origin(`kxedit.preview` ホスト)を指す URL だけ**を対象にする。外部 URL は触らない
  (仮想ホストのマッピングは我々のものだけなので、他所のパス解釈に口を出さない)。
- パスに `%2f` / `%5c`(大小問わず)が残っていたら、**`%` 自身をエスケープして
  `%252f` / `%255c` にする**。結果は「区切り文字を含まない 1 つのファイル名」への要求になり、
  マッピング先で 404 で終わる。**URL を空にする案は採らない** —— `<img src="">` の解決は
  文書 URL(data:)に対して曖昧で、ブラウザ依存の要求が飛びうるため。
- 大小は保存する(`%2F` は `%252F`)。**置換対象は `%2f` / `%5c` の並びだけ**で、
  `%20` など他の percent-escape は触らない(退化させない)。

### 14.3 網(Core.Tests)

**`MarkdownRenderer.Render` の出力**まで見る(単体の関数だけ見ると §14.1 の見落としを繰り返す):

- `![x](..%2f..%2fsecret.txt)` → 出力に `..%2f` が**残らない**
- `![x](https://kxedit.preview/..%2f..%2fsecret.txt)` → 同上(**これが今回の本命**)
- `[a](https://kxedit.preview/..%5c..%5cx)` → 同上(リンク側・バックスラッシュ)
- 大文字 `%2F` / `%5C`
- **非退化の対照**: `![x](my%20file.png)` と `![x](sub/dir/pic.png)` は従来どおり絶対化され、
  `[a](https://example.com/a%2fb)` は**外部 URL なので触らない**
- `NeutralizeEncodedSeparators` 自体の単体テスト(preview origin 判定・大小・非対象)

**ミューテーション(スポット)**: `%2f` / `%5c` の 2 条件と「preview origin だけ」の 1 条件を、
それぞれ 1 つずつ変異させて落ちることを確認する。

### 14.4 traversal そのものは L5 で確かめる

このガードは「密輸された区切り文字をマッピングへ届かせない」ことを保証するが、
**WebView2 が `%2f` をパス区切りとしてデコードするかどうかは依然として未実測**である。
L5 項目 1 を次のとおり拡張する:

1. `![x](..%2f..%2fsecret.txt)`(相対形)
2. `![x](https://kxedit.preview/..%2f..%2fsecret.txt)`(**絶対形**)
3. **未保存タブ**(= 空フォルダーへマッピングされる状態)で 2 を実施し、
   `..%2f..%2fEBWebView/Default/Preferences` が読まれないこと
   —— 空フォルダーは WebView2 プロファイルの直下 1 階層なので、traversal が成立するなら
   プロファイルに届く。**ここが成立したら**フォルダーマッピングをやめて
   `WebResourceRequested` で自前に正規化して返す方式へ切り替える必要がある(次リリース)。

## 15. Task 2b の追加(F-7)—— `http://kxedit.preview/…` が実 DNS へ出る

`PreviewNavigationPolicy.Classify` は `https` + preview ホストだけを Block し、
`http` は `LaunchExternal`(既定ブラウザ起動)へ落とす(`PreviewNavigationPolicy.cs:89`)。
本文に `[x](http://kxedit.preview/leak)` を書いてクリックさせると、**既定ブラウザが
`kxedit.preview` を実 DNS 解決する**。

これは本ブランチが守ろうとしている不変条件(この名前を実 DNS に出さない = V-2)の
**唯一の残存経路**であり、しかも同ファイルのコメントは
「kxedit.preview は実ホストではないので LaunchExternal しても無意味」と書いている ——
**無意味ではなく、DNS 要求が出る**。V-4 / V-6 と同じ「実在しない前提を謳うコメント」の型。

**やること**: `Classify` の `when` を `"http" or "https"` の両方で preview ホストを Block にし、
コメントを訂正する。既存テスト `Classify_HttpPreviewHost_ReturnsLaunchExternal` の期待値を
`Block` へ変える(名前も変える)。**B6 の射程外だが、同じ不変条件の穴なので本ブランチで直す**
(傘設計書からの逸脱として PR description に記載する)。

### 14.5 Task 2 実装時の実測(2026-09-03)

- **`System.Uri` は `%2f` / `%2F` / `%5c` / `%5C` のいずれも復号しない。大小もそのまま保つ**
  (`AbsoluteUri` / `AbsolutePath` の両方。`ToString()` は表示用に `%20` を空白へ戻すが
  これらは戻さない)。したがって **`%5c` は「未確認」ではなく、実際に生のまま WebView2 へ
  届いていた**。V-3 の攻撃面は `%2f` だけでなく `%5c` も等しく実在していた。
- ガード導入前の `Render` 出力で、相対形 2 本・絶対形 3 本のすべてが素通りすることを
  RED として確認済み(失敗 5 / 合格 3)。
- 変異 5 種(`%2f` / `%5c` / ホスト判定 / 大小無視 / 正規表現の大小クラス)をそれぞれ
  単独で殺し、全て kill を確認(生存ゼロ)。

#### 本ガードの唯一の前提 —— L5 で確かめること

`%252f` へ倒す方式は、**WebView2 / Chromium が `%25` を二重復号しない**ことに依存する。
二重復号するならガードは無効化される。L5 項目 1 に次を足す:

> `![x](https://kxedit.preview/..%252f..%252fsecret.txt)` を含む .md をプレビューし、
> **フォルダー外のファイルが読まれないこと**(= `%252f` が `/` へ二重復号されないこと)。

### 15.1 F-7 の挙動変更(ユーザー可視)

`http://kxedit.preview/...` のクリックは、これまで既定ブラウザが起動して名前解決エラー
ページを出していた。今後は**プレビュー内で何も起きない**(Block)。B5「実際と違うことを
言わない」の観点では、Block をユーザーに伝える経路の有無を確認する価値がある
(`MarkdownPreviewForm` 側の Block ハンドリングは本ブランチで未変更)。**申し送り**とする。

### 15.2 画像 URL の関門は 1 か所しかない(将来の変更で壊しやすい)

`SafeLinkExtension` は `link.IsImage` を base へ委譲するため画像を検査しない。したがって
画像 URL に対する検査は `PreviewRelativeUrlExtension.OnDocumentProcessed` の
`NeutralizeEncodedSeparators` **だけ**である。今後この拡張の登録順や `DocumentProcessed` の
構成を触る変更は、**画像の唯一のガードを外す変更になりうる**。レビュー時に明示的に見ること。

### 14.6 §14.2 の作り直し(2026-09-03・Task 2 の脆弱性レビュー由来)

§14.2 は「全 `LinkInline` に対する事後条件」で相対・絶対の両方を覆う設計だった。
**ホスト判定が `Uri.Host` であること**と、**ガードが AST 段に居るのに主張が出力 HTML に
ついてなされていること**から、実測で 4 系統の迂回路が残っていた。

| ID | 入力 | 何が起きたか(実測) |
|----|------|--------------------|
| F-1 | `https://kxedit。preview/..%2f..%2fx`(U+3002 が 1 文字) | `Uri.Host` は Unicode を保つのでガードが一致せず素通り。ところが Markdig の `WriteEscapeUrl` は **`IdnHost` で ASCII 化して出力**するため、**修正前とバイト同一の出力**になる |
| F-2 | `https://%6bxedit.preview/..%2f..%2fx` | `Uri.TryCreate` が **false**(.NET は parse 失敗)→ ガードは即 return。WHATWG のホスト解析は percent-decode してから ASCII 化するので **Chromium では `kxedit.preview`**(Node/Ada で確認) |
| F-3 | `https://kxedit.preview./..%2f..%2fx`(末尾ドット) | ガードも F-7 の Block も通り抜ける |
| F-4 | `https://kxedit.preview/..\..\x`(生の `\`) | `Uri` が `\` を `/` に直して dot-segment を畳むのでガードは「区切りは無い」と判断 → **その後 Markdig が `%5C` へエスケープして出力する** |
| F-5 | `<https://kxedit.preview/..%2f..%2fa>` | `AutolinkInline` は `LinkInline` ではないので `Descendants<LinkInline>()` に掛からない |

#### 採る形

1. **ガードを `HtmlRenderer.LinkRewriter` へ移す**(`PreviewRelativeUrlExtension.Setup(pipeline, renderer)`
   —— それまで空実装だった)。実測で画像・インラインリンク・角括弧宛先・**CommonMark autolink**・
   GFM 裸 URL・参照リンク定義・表セル内で発火し、**Markdig がエスケープする前の生の URL**が届く。
   発火しないのは**脚注リンクのみ**(Markdig 採番の固定形式でユーザー入力が入らない)。
2. **判定を default-deny にする**。null / 空 と `#` 始まりは素通し、
   `Uri.TryCreate` に成功して **`IdnHost` の末尾ドットを除いたもの**が preview と一致しない場合だけ
   「明確に外部」として素通し、**それ以外(preview 宛 / parse 不能 / 相対)は無害化**する。
   `IdnHost` が F-1 を、末尾ドット除去が F-3 を、parse 不能を無害化側へ倒すことが F-2 を塞ぐ。
3. **`AbsolutePath` の前置チェックを撤去**する(F-4 の原因そのもの)。判定も置換も URL 全体に掛ける。
4. **生のバックスラッシュも無害化対象**に足す(`\` → `%255C`)。

#### 実装時に反証された根拠(記録)

本設計は当初「`LinkRewriter` への移設が F-4 と F-5 を同時に塞ぐ」と書いていた。
**実装担当が変異で反証した** —— ガードを AST 段へ戻す変異で落ちたのは **autolink 1 本だけ**で、
F-4 のケースは落ちない。**F-4 を塞いだのは resolver 側の 2 修正(前置チェック撤去 + 生 `\` の対象化)**
であり、移設が実測で必須なのは F-5 に対してだけである。移設には「主張の対象(出力 HTML)と
ガードの位置を一致させる」という設計上の根拠が別にあるが、それは現時点で穴ではない。
**結論は正しかったが根拠が偽だった**型(memory: 結論ではなく根拠を検証する)。

#### 副作用として受容するもの

- `mailto:a%2fb@kxedit.preview` はローカル部の `%2f` が `%252f` になる(`Classify` は mailto を
  LaunchExternal のまま通すので実害なしと判断)。
- protocol-relative `//kxedit.preview/a%2fb` は .NET が file scheme + Host=preview と解釈するため
  無害化される(外部の `//example.com/…` は不変)。
- autolink はリンク**テキスト**に生 URL を出すので、HTML 全文への `DoesNotContain("%2f")` は
  成立しない。テストは `href` / `src` の属性値を抽出して検証している。

#### §15 の訂正 —— 「唯一の残存経路」は偽だった

F-3 により、`http://kxedit.preview./leak`(末尾ドット 1 文字)は F-7 の Block を通り抜けて
`LaunchExternal` に落ちていた。**§15 の「唯一の残存経路」という記述は偽**である。
`Classify` のホスト判定も `IdnHost` + 末尾ドット除去へ揃えて塞いだ。

#### L5 に足す項目(V-3 の前提を測る)

1. `![a](https://kxedit。preview/sub%2fchild.png)` —— 表示されたら WebView2 は `%2f` を区切りとして
   **復号する** = V-3 は実在し、本ガードは load-bearing。表示されなければ V-3 の脅威度自体が下がる。
2. `![b](https://kxedit.preview/sub%252fchild.png)` —— **本ガードの唯一の前提**。表示されたら
   `%25` が二重復号され、無害化は無効。
3. `![d](https://kxedit.preview./pic.png)` —— 末尾ドットが仮想ホストマッピングに一致するか。
4. `![f](https://kxedit.preview/sub\child.png)` —— `%5C` を Windows のパス区切りとして復号するか。
5. `![g](https://%6bxedit.preview/pic.png)` —— Chromium のホスト正規化がマッピングに一致するか。
6. `![h](https://kxedit.preview/pic.png?x=%2f../secret.txt)` —— query がフォルダー解決に
   使われないという**未実測の前提**の確認。

## 16. 実施記録(2026-09-03)

### 16.1 結果

コード 12 commit + docs 10 commit。テストは Core 1477→**1505** / App 806→**829** / Editor 516(不変)。
ビルド警告 0。実装は全 5 タスク(+ Task 2b)完了、**L5 は未実施**。

| タスク | 内容 | commit |
|--------|------|--------|
| 0 | スパイク | `175bfb9`(docs のみ) |
| 1 | V-2 + PR #57 申し送り | `56a5103` / `d7fb100` |
| 2 | V-3 | `da05496` / `cb1fe93` |
| 2b | F-7 | `1a26f57` / `2724e79` |
| 3 | V-4 / V-5 / V-6 | `415e4e6` |
| 4 | M-23 | `0090f6b` |
| 最終レビュー反映 | A〜F | `1f81fd7` / `20bd049` / `c0b980d` / `b261b31` / `41a9655` / `9c1dd1b` |

### 16.2 本設計書と実装計画が含んでいた誤り(4 件)

**計画のコードは正解ではない**の実例。いずれも実測で覆った。

1. **§4 の前提が偽**(Task 0)。`SetVirtualHostNameToFolderMapping` は実在確認を内蔵しており、
   不存在フォルダーは投げ、不達 UNC では 21 秒返らない。「存在確認せずに渡す」も
   「登録を `Task.Run` へ逃がす」(UI スレッド専有)も成立しない。→ §13.2 で作り直し。
2. **§5 のガード位置が偽**(Task 1 の脆弱性レビュー)。`TryResolve` は絶対 URL に early return
   するので、事後条件をそこに置くと `![x](https://kxedit.preview/..%2f..%2f…)` が素通りする。
   → §14 で作り直し。
3. **§14.2 のガード位置も不十分**(Task 2 の脆弱性レビュー)。ホスト判定が `Uri.Host` だったため
   非 ASCII ホスト(U+3002)/ percent-encoded ホスト / 末尾ドットで迂回でき、生バックスラッシュは
   Markdig が**ガードの後で** `%5C` へエスケープしていた。→ §14.6 で作り直し。
4. **§14.6 の根拠の一部が偽**。「`LinkRewriter` への移設が F-4 と F-5 を同時に塞ぐ」は変異検証で
   反証された(AST 段へ戻す変異で落ちるのは autolink 1 本のみ)。F-4 を塞いだのは resolver 側の
   2 修正。**結論は正しく根拠が偽**だった型。

### 16.3 セキュリティ修正が作った退行(最終レビューで発見)

**F-1 / F-3 の修正で導入した `Uri.IdnHost` が、IDNA 不正ホストで `UriFormatException` を投げる。**
`MainForm.ShowMarkdownPreview` は `DocumentTooLargeException` しか捕まえないため、
`![x](https://xn--あ/pic.png)` を含む .md を開いてプレビューを押すと**アプリが落ちる**(実測)。
`U+FFFD` は文字コード誤検出でも混入しうるので攻撃者不在でも踏む。main は `Uri.Host` で投げなかった。

倒す向きを 2 か所で逆にして塞いだ(resolver = 判断不能なら無害化 / `Classify` = 判断不能なら Block)。
**セキュリティ修正それ自体が可用性の退行を作りうる**という教訓として残す。

同じ経路で、**main 既存**の欠陥も 1 件見つかった: Markdig の `MaximumNestingDepth`(既定 128)超過が
素の `ArgumentException` で抜け、`"> " × 200`(**400 バイト**)で同じくアプリが落ちる。
`MarkdownTooComplexException` へ翻訳して塞いだ。MD-L-3 のコメント「入口一箇所の cap で
pathological な入力を封じる」も**実態と違った**(封じているのは Markdig 側の深度制限で、
その失敗様式はアプリ終了)ので訂正した。

### 16.4 「張れるのに張っていなかった」網(4 件)

いずれも最終レビューで指摘され、変異で kill を確認して追加した。

- `ShowMarkdownPreview` が `SnapshotText` **より前**に cap 判定する順序(IL 出現順で固定)
- `Apply` の `ThrowIfNull` が try の**手前**にある不変条件(移設する変異が 806 本全緑で生存していた)
- `InitAsync` が実際に `RemoteAwareDirectory.Exists` を**呼んでいる**こと(プローブを落とす退化を検出)
- `Apply` → `NavigateToString` の**呼出順**(V-2 の要)

### 16.5 受容した残余・申し送り

- **到達可能だが 5 秒より遅い共有では画像が黙って出ない**(従来は「長く固まってから出る」)。
  「B6 で画像が出なくなった」報告の第一容疑者。
- **`http://kxedit.preview/…` の Block は無音**。従来は既定ブラウザが名前解決エラーを出していた。
  ただし `https` の preview ホスト Block は元から無音なので、**既存の沈黙を http へ広げた**形。
- `MarkdownPreviewForm` の ctor 変更は**公開 API の破壊的変更**(呼び出し元はリポジトリ内 1 か所)。
- **画像 URL の関門は 1 か所**(`LinkRewriter`)。`MediaLinkExtension` は `WriteEscapeUrl` を通らない
  ため本ブランチでパイプラインから除去した(出力バイト一致を実測確認)。
- `mailto:…@kxedit.preview` のローカル部と、preview 宛 URL の query / fragment は無害化に
  巻き込まれる(安全側)。
- **`Allow` → `DenyCors`** の見直しは次リリース候補(§9)。

### 16.6 L5(実機 SR 検証)—— 未実施

**項目数は当初の「3 項目 + MD-L-3」から大きく増えた**(§13.2 / §14.4 / §14.5 / §14.6 / 最終レビュー)。
チェックリストは `docs/plans/2026-09-03-preview-csp-virtual-host-l5-checklist.md` に起こした。
傘設計書 §7.1 の台帳へは**そのファイルの実数**を記録すること
(§10 / §12 に残る「3 項目」をそのまま転記しない —— L5 台帳の数え違いは過去に実際に起きている)。
