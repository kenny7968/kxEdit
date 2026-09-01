#Requires -Version 5.1
<#
.SYNOPSIS
  main マージ前のローカルゲート。Release ビルド 0 警告+全テスト緑を確認し、
  加えて 3 テストプロジェクトすべてを Debug でも走らせる(Debug.Assert 由来の網を
  有効にするため)。
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
# Issue #48 最終レビュー Q-I-4: 上の 3 ステップはすべて Release で、Debug.Assert は
# [Conditional("DEBUG")] のため Release バイナリに残らない。DocumentState.Path の不変条件
# (「null か正規化済み絶対パス」= 設計書 §3.1)を守る網は Debug.Assert なので、Release だけの
# ゲートでは 1 行も走らない = 「網に見えるがゲート上は無効」という嘘の安全宣言になっていた
# (実証: IsPathFullyQualified → IsPathRooted の変異が Release 全緑で生存・Debug では赤)。
# --no-build を付けないのは、上の Release ビルドとは別構成のバイナリが要るため。
#
# 2026-09-01(B1): Core / Editor も Debug で走らせて 3 本を揃えた。旧コメントの
# 「Core.Tests の Debug は足さない(既知 S-5 で 4 件赤)」は S-5 の解消により失効
# (docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md)。
# kxEdit.Editor 自身に Debug.Assert は現状 0 件だが、プロジェクト単位で歯抜けにしておくと
# 「assert を足したのにゲートステップを足し忘れる」= Q-I-4 が踏んだのと同じ失敗モードが
# 再発する。3 本揃えれば「どの層に assert を入れてもゲートに乗る」が構造で保証される。
# Release 側と同じくフィルタなし(LocalOnly も全件)で走らせる。
Invoke-Step 'Core.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Core.Tests') -c Debug
}
Invoke-Step 'Editor.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Editor.Tests') -c Debug
}
Invoke-Step 'App.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.App.Tests') -c Debug
}
Write-Host 'OK: pre-merge チェック全通過' -ForegroundColor Green
exit 0
