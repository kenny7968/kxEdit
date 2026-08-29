# L5(実機 SR 検証)チェックリスト — 未処理例外と入力の取りこぼし(A-13 / M-1 / A-20)

対象ブランチ: `feature/unhandled-exception-safety`
設計書: [2026-08-29-unhandled-exception-safety-design.md](2026-08-29-unhandled-exception-safety-design.md) §9
実装計画: [2026-08-29-unhandled-exception-safety.md](2026-08-29-unhandled-exception-safety.md)

**L5 が必須な理由**(CLAUDE.md §5): App の Speech 系(`IAnnouncer`)に新しい発声を足したため。
`tools/sr-regression.ps1` は UIA 応答までしか見ないので**本項目の代替にならない**。

## 前提

- Release ビルドの `kxEdit.exe` を起動する(`src/kxEdit.App/bin/Release/net9.0-windows/kxEdit.exe`)。
- NVDA を起動する。**スピーチビューアー**を開いておくと発声を逐語で確認できる
  (2026-08-25 の A-10 で確立した手法)。
- 項目 1〜3 は「別プロセスがクリップボードを保持している」状態が要る。
  別ウィンドウの PowerShell で下を実行し、**25 秒以内**に操作する。

```powershell
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;
public static class Cb{
  [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr h);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
}' -Language CSharp
[void][Cb]::OpenClipboard([IntPtr]::Zero); Start-Sleep -Seconds 25; [void][Cb]::CloseClipboard()
```

## チェック項目

| # | 手順 | 期待 | 結果 | 備考 |
|---|------|------|------|------|
| 1 | 適当な本文を入力して選択し、クリップボード占有中に **Ctrl+C** | 未処理例外ダイアログが**出ない**。NVDA が「クリップボードにコピーできません。他のアプリが使用中の可能性があります」と読む | ☐ | |
| 2 | 同状態で **Ctrl+X** | **本文が消えない**(選択も残る)。1 と同じ発声 | ☐ | A-13 の核心 |
| 3 | 同状態で **Ctrl+V** | 本文が変わらない。NVDA が「クリップボードから貼り付けられません。他のアプリが使用中の可能性があります」と読む | ☐ | 1 と**別の文言**であること |
| 4 | 絵文字パネル(**Win+.**)で 😂 を挿入 | 1 文字として入り、→ / ← が **1 回で跨ぐ**。ステータスバーは「桁 3」 | ☐ | A-20 の**非退行**(この経路は元から正常) |
| 5 | IME で通常の日本語変換・確定 | 退行がない(変換中の読み・確定後の読みが従来どおり) | ☐ | |
| 6 | **上書きモード**(Insert)で既存文字の上に絵文字を挿入 | 既存が **1 文字だけ**置き換わる(2 文字潰れない) | ☐ | |

## 補足: 既に済んでいる確認(L5 ではない)

設計書 §9.1 に記録済み。UIA / Win32 で観測した**手動スモーク**(2026-08-29)で、
項目 1 の「ダイアログが出ない」「通知ラベルの文言」と、項目 2 の「本文が消えない」は確認済み。
ただし**実発声は未検証**なので、本チェックリストの 1〜3 は改めて NVDA で行うこと。

M-1(未処理例外ハンドラ)は意図的にクラッシュさせないと確認できないため、L5 では扱わない。
実機での確認は「A-13 の修正後に例外ダイアログが出ないこと」(項目 1)で間接的に済ませ、
ハンドラ本体は自動テスト(`CrashHandlerTests` / `UiCrashSinkTests` /
`MainFormSmokeTests.FlushBackupsForCrash_*`)で担保する。

## 結果

- 実施日:
- NVDA バージョン:
- 総合:
