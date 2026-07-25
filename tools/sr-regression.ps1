#Requires -Version 5.1
<#
.SYNOPSIS
  Phase 3 SR 呼び出しパターン回帰スイート。UIA プロバイダに対する SR 側の呼び出しを
  UIA クライアントとして再現し、a11y 関連変更(yEdit.Accessibility / EditorControl の UIA 部 /
  Speech 系)のマージ前+リリース前の一次スクリーニングとして使う。
  典拠: docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md §1
.DESCRIPTION
  実行内容:
    1. yEdit.Editor.Smoke を Release ビルド(1 回のみ・子スクリプトが二重ビルドしない)
    2. verify-uia-editor.ps1 を実行(TextPattern/GetSelection/RangeFromPoint 疎通)
    3. word-sim.ps1 を実行(NVDA の TextUnit.Word 6 ケース)

  子スクリプトは pwsh(PowerShell 7+)がインストールされていればそれを優先使用する。
  理由: word-sim.ps1 には日本語コメントが BOMless UTF-8 で入っており、WinPS 5.1 の
  日本語ロケール環境ではパーサが Shift-JIS 誤解釈してエラーになる pre-existing 問題を
  持つ。pwsh は BOMless UTF-8 をデフォルトで正しく扱うためこの問題を回避できる。pwsh
  未検出環境では powershell(WinPS 5.1)フォールバックで実行するが、警告バナーを出して
  運用者が失敗理由を切り分けられるようにする。

  総合 EXIT: 全 PASS で 0・1 つでも FAIL なら 1。
  実行タイミング(設計書 §1): a11y 関連変更のマージ前+リリース前・pre-merge には組み込まない
  (UIA クライアントはフォアグラウンドのデスクトップセッションが必要で、常時実行に向かない)。
  判定の限界(設計書 §1 明記): 「UIA プロバイダが SR 呼び出しに正しく応答するか」までしか
  検証しない。「SR が実際に発声するか」(発声側で起きる事象)は検出できない。
  PASS でも L5 の人手確認は省略しない。
.EXAMPLE
  powershell -File tools\sr-regression.ps1
  pwsh       -File tools\sr-regression.ps1
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$smokeExe = Join-Path $repoRoot 'tests\yEdit.Editor.Smoke\bin\Release\net9.0-windows\yEdit.Editor.Smoke.exe'

# 子スクリプト実行に使う PowerShell を決定する。pwsh 優先(BOMless UTF-8 対応)。
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) {
    $childShell = $pwshCmd.Source
    Write-Host "[INFO] 子スクリプトは pwsh で実行: $childShell" -ForegroundColor DarkGray
} else {
    $childShell = 'powershell'
    Write-Host '[WARN] pwsh(PowerShell 7+) が見つかりません。WinPS 5.1 でフォールバック実行します。' -ForegroundColor Yellow
    Write-Host '       word-sim.ps1 が日本語コメントの Shift-JIS 誤解釈でパーサエラーになる可能性があります。' -ForegroundColor Yellow
    Write-Host '       解決策: `winget install Microsoft.PowerShell` などで pwsh を導入してください。' -ForegroundColor Yellow
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body | Out-Host
    return $LASTEXITCODE
}

$fail = 0

$rc = Invoke-Step 'Release ビルド(yEdit.Editor.Smoke)' {
    dotnet build (Join-Path $repoRoot 'tests\yEdit.Editor.Smoke') -c Release
}
if ($rc -ne 0) {
    Write-Host "NG: ビルド失敗 (exit $rc)" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $smokeExe)) {
    Write-Host "NG: ビルド後 exe が見つからない: $smokeExe" -ForegroundColor Red
    exit 1
}

$rc = Invoke-Step 'verify-uia-editor.ps1' {
    & $childShell -File (Join-Path $PSScriptRoot 'verify-uia-editor.ps1') -Exe $smokeExe
}
if ($rc -ne 0) { $fail++ ; Write-Host "  → FAIL (exit $rc)" -ForegroundColor Red }

$rc = Invoke-Step 'word-sim.ps1' {
    & $childShell -File (Join-Path $PSScriptRoot 'word-sim.ps1') -Exe $smokeExe
}
if ($rc -ne 0) { $fail++ ; Write-Host "  → FAIL (exit $rc)" -ForegroundColor Red }

Write-Host ''
if ($fail -eq 0) {
    Write-Host 'OK: SR 回帰スイート 全通過(ただし L5 人手確認は別途必要)' -ForegroundColor Green
    exit 0
} else {
    Write-Host "NG: $fail 件の子スクリプトが FAIL" -ForegroundColor Red
    exit 1
}
