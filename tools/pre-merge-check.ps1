#Requires -Version 5.1
<#
.SYNOPSIS
  main マージ前のローカルゲート。Release ビルド 0 警告+全テスト緑を確認する。
  失敗があれば EXIT 1。典拠: docs/plans/2026-07-13-test-strategy-design.md §2.1
.EXAMPLE
  powershell -File tools\pre-merge-check.ps1
#>
# テストプロジェクトを追加/削除する場合は 3 箇所同期: tools/pre-merge-check.ps1・.github/workflows/ci.yml・.github/workflows/release.yml
# (sln 一括ステップ寄せは検討済みだが、dotnet test kxEdit.sln が Editor/App 両 UI アセンブリを並列実行するため現状維持=2026-07-15 実測 sln 14s vs 個別合計 18s)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "NG: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

Invoke-Step 'Local tool restore' {
    dotnet tool restore
}
Invoke-Step 'CSharpier check (format verify)' {
    dotnet csharpier check $repoRoot
}
Invoke-Step 'Release ビルド(警告=エラー)' {
    dotnet build (Join-Path $repoRoot 'kxEdit.sln') -c Release -warnaserror
}
Invoke-Step 'Core.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Core.Tests') -c Release --no-build
}
Invoke-Step 'Editor.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Editor.Tests') -c Release --no-build
}
Invoke-Step 'App.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.App.Tests') -c Release --no-build
}
Write-Host 'OK: pre-merge チェック全通過' -ForegroundColor Green
exit 0
