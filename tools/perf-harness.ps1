#Requires -Version 7
<#
.SYNOPSIS
    kxEdit の実アプリを SendInput で操作し、1 操作あたりのプロセス CPU 時間を測る(性能改善フェーズ 0)。

.DESCRIPTION
    設計書 docs/plans/2026-09-24-general-perf-improvements-design.md §5.2、
    仕様は docs/plans/2026-09-24-general-perf-audit.md §9.5、手順は tools/README.md §3。

    利用者の %APPDATA%\kxEdit(hot exit のバックアップ・セッション・設定)を失わないことを最優先にする。
      - 開始前: kxEdit が起動中 / backups にファイルがある / 前回の退避が残っている / 再解析ポイントがある
        のいずれかなら、何も変えずに中止する。
      - 退避はコピー。退避側のハッシュを元と照合してから計測に入る。
      - 計測中は空のプロフィール(既定設定)で起動する。
      - 終了時(finally)に元へ戻し、ハッシュが一致したら退避を消す。一致しなければ退避を残して非 0 で終わる。
      - finally が走らなかった場合も、目印ファイルが残るので次回の起動で検出できる(-Recover で復元)。

.PARAMETER PublishDir
    kxEdit.exe を含む publish フォルダー(dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o <dir>)。

.PARAMETER Scenario
    実行するシナリオ(M-1〜M-7)。既定は全部。

.PARAMETER OutCsv
    結果の CSV。既定は <作業ルート>\results-<日時>.csv。

.PARAMETER WorkDir
    計測用の文書を生成するフォルダー。既定は $env:TEMP\kxEdit-perf-harness。

.PARAMETER SelfTest
    一時フォルダーの偽プロフィールで、退避・復元と中止条件を検証する。実プロフィールには触れない。

.PARAMETER Recover
    前回の異常終了で残った退避から、プロフィールを復元する(照合してから退避を消す)。
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [string]$PublishDir,
    [Parameter(ParameterSetName = 'Run')]
    [ValidateSet('M-1', 'M-2', 'M-3', 'M-4', 'M-5', 'M-6', 'M-7')]
    [string[]]$Scenario = @('M-1', 'M-2', 'M-3', 'M-4', 'M-5', 'M-6', 'M-7'),
    [Parameter(ParameterSetName = 'Run')]
    [string]$OutCsv,
    [Parameter(ParameterSetName = 'Run')]
    [string]$WorkDir = (Join-Path $env:TEMP 'kxEdit-perf-harness'),
    [Parameter(ParameterSetName = 'SelfTest', Mandatory = $true)]
    [switch]$SelfTest,
    [Parameter(ParameterSetName = 'Recover', Mandatory = $true)]
    [switch]$Recover
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$script:ProfileDir = Join-Path $env:APPDATA 'kxEdit'
$script:HarnessRoot = Join-Path $env:LOCALAPPDATA 'kxEdit-perf-harness'
$script:ProcessName = 'kxEdit'

# =====================================================================
#  利用者プロフィールの退避・復元(設計書 §5.2「利用者データの保全」)
#  すべての関数はパスを引数に取る(-SelfTest が一時フォルダーで同じコードを通すため)。
# =====================================================================

function Get-MarkerPath([string]$Root) { Join-Path $Root 'STASH-MARKER.json' }
function Get-StashDir([string]$Root) { Join-Path $Root 'stash\kxEdit' }

# フォルダーの中身の目録(相対パス → SHA256、空フォルダーの一覧)。存在しなければ $null。
function Get-Manifest([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { return $null }
    $full = (Resolve-Path -LiteralPath $Dir).ProviderPath.TrimEnd('\')
    $files = [ordered]@{}
    $dirs = [System.Collections.Generic.List[string]]::new()
    foreach ($item in (Get-ChildItem -LiteralPath $full -Recurse -Force | Sort-Object FullName)) {
        $rel = $item.FullName.Substring($full.Length + 1)
        if ($item.PSIsContainer) {
            $dirs.Add($rel)
        }
        else {
            $files[$rel] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    return [pscustomobject]@{ Files = $files; Dirs = @($dirs) }
}

# 2 つの目録が同じか。違いは $Diff(List[string])に書く。
function Compare-Manifest($Expected, $Actual, [System.Collections.Generic.List[string]]$Diff) {
    foreach ($k in $Expected.Files.Keys) {
        if (-not $Actual.Files.Contains($k)) { $Diff.Add("欠落: $k") }
        elseif ($Actual.Files[$k] -ne $Expected.Files[$k]) { $Diff.Add("内容の不一致: $k") }
    }
    foreach ($k in $Actual.Files.Keys) {
        if (-not $Expected.Files.Contains($k)) { $Diff.Add("余分: $k") }
    }
    $ed = @($Expected.Dirs); $ad = @($Actual.Dirs)
    foreach ($d in $ed) { if ($ad -notcontains $d) { $Diff.Add("フォルダーの欠落: $d") } }
    foreach ($d in $ad) { if ($ed -notcontains $d) { $Diff.Add("余分なフォルダー: $d") } }
    return $Diff.Count -eq 0
}

# JSON から読んだ目録(PSCustomObject)を Get-Manifest と同じ形へ戻す。
function ConvertFrom-MarkerManifest($m) {
    $files = [ordered]@{}
    foreach ($p in $m.Files.PSObject.Properties) { $files[$p.Name] = $p.Value }
    return [pscustomobject]@{ Files = $files; Dirs = @($m.Dirs) }
}

function Get-RestoreGuide([string]$ProfileDir, [string]$Root) {
    return @"
前回の計測の退避が残っています(異常終了した可能性があります)。
  退避: $(Get-StashDir $Root)
  目印: $(Get-MarkerPath $Root)
  元の場所: $ProfileDir
復元するには、kxEdit を終了してから次を実行してください(照合してから退避を消します):
  pwsh -File tools\perf-harness.ps1 -Recover
自動の復元が失敗した場合は、退避の中身を元の場所へ手でコピーし、内容を確かめてから $Root を削除してください。
"@
}

# 開始前の中止条件。問題がなければ空配列を返す。何も変更しない。
function Test-HarnessPreconditions([string]$ProfileDir, [string]$Root, [string]$ProcessName) {
    $reasons = [System.Collections.Generic.List[string]]::new()
    if (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) {
        $reasons.Add("$ProcessName が起動しています。終了してから実行してください。")
    }
    if (Test-Path -LiteralPath (Get-MarkerPath $Root)) {
        $reasons.Add((Get-RestoreGuide $ProfileDir $Root))
    }
    elseif (Test-Path -LiteralPath (Join-Path $Root 'stash')) {
        $reasons.Add("目印のない退避フォルダーが残っています: $(Join-Path $Root 'stash')。中身を確かめてから削除してください。")
    }
    $backups = Join-Path $ProfileDir 'backups'
    if ((Test-Path -LiteralPath $backups) -and
        @(Get-ChildItem -LiteralPath $backups -Recurse -Force -File).Count -gt 0) {
        $reasons.Add("$backups にファイルがあります(未保存の本文が残っている可能性)。kxEdit を起動して処理してから実行してください。")
    }
    if (Test-Path -LiteralPath $ProfileDir) {
        $rp = @(Get-ChildItem -LiteralPath $ProfileDir -Recurse -Force |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ((Get-Item -LiteralPath $ProfileDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $rp += Get-Item -LiteralPath $ProfileDir -Force
        }
        if ($rp.Count -gt 0) {
            $reasons.Add("$ProfileDir にシンボリックリンク等の再解析ポイントがあります($($rp[0].FullName) ほか)。安全に退避できないので中止します。")
        }
    }
    return , $reasons.ToArray()
}

function Write-Marker([string]$Root, [hashtable]$Data) {
    $json = $Data | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Get-MarkerPath $Root), $json, [Text.UTF8Encoding]::new($false))
}

function Read-Marker([string]$Root) {
    $p = Get-MarkerPath $Root
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return Get-Content -LiteralPath $p -Raw -Encoding utf8 | ConvertFrom-Json
}

# プロフィールをコピーで退避し、退避側を照合する。照合できなければ例外(何も消していない)。
function Save-ProfileStash([string]$ProfileDir, [string]$Root) {
    $stash = Get-StashDir $Root
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $existed = Test-Path -LiteralPath $ProfileDir -PathType Container
    $manifest = if ($existed) { Get-Manifest $ProfileDir } else { $null }
    # 目印はコピーより先に書く(コピーの途中で落ちても次回に検出できる)。
    Write-Marker $Root @{
        Version        = 1
        CreatedAt      = (Get-Date).ToString('o')
        ProfilePath    = $ProfileDir
        ProfileExisted = $existed
        State          = 'copying'
        Manifest       = $manifest
    }
    if ($existed) {
        New-Item -ItemType Directory -Force -Path $stash | Out-Null
        foreach ($child in Get-ChildItem -LiteralPath $ProfileDir -Force) {
            Copy-Item -LiteralPath $child.FullName -Destination $stash -Recurse -Force
        }
        $diff = [System.Collections.Generic.List[string]]::new()
        if (-not (Compare-Manifest $manifest (Get-Manifest $stash) $diff)) {
            throw "退避の照合に失敗しました(元のプロフィールは変更していません): $($diff -join '; ')"
        }
    }
    Write-Marker $Root @{
        Version        = 1
        CreatedAt      = (Get-Date).ToString('o')
        ProfilePath    = $ProfileDir
        ProfileExisted = $existed
        State          = 'stashed'
        Manifest       = $manifest
    }
}

# 退避が目印の目録どおりに揃っていること。揃っていなければ例外。
function Assert-StashIntact([string]$ProfileDir, [string]$Root) {
    $marker = Read-Marker $Root
    if ($null -eq $marker -or $marker.State -ne 'stashed') {
        throw "退避が完了していません(目印の状態: $(if ($marker) { $marker.State } else { 'なし' }))。プロフィールには触れません。"
    }
    if ($marker.ProfilePath -ne $ProfileDir) {
        throw "目印の元の場所($($marker.ProfilePath))が対象($ProfileDir)と違います。"
    }
    if ($marker.ProfileExisted) {
        $diff = [System.Collections.Generic.List[string]]::new()
        $expected = ConvertFrom-MarkerManifest $marker.Manifest
        if (-not (Compare-Manifest $expected (Get-Manifest (Get-StashDir $Root)) $diff)) {
            throw "退避が壊れています: $($diff -join '; ')"
        }
    }
    return $marker
}

# 計測用にプロフィールを空にする。退避の照合が済んでいなければ何もしない(例外)。
function Clear-ProfileForRun([string]$ProfileDir, [string]$Root) {
    Assert-StashIntact $ProfileDir $Root | Out-Null
    if (Test-Path -LiteralPath $ProfileDir) {
        foreach ($child in Get-ChildItem -LiteralPath $ProfileDir -Force) {
            Remove-Item -LiteralPath $child.FullName -Recurse -Force
        }
    }
}

# 退避から元へ戻し、照合できたら退避と目印を消す。照合できなければ退避を残して例外。
function Restore-ProfileStash([string]$ProfileDir, [string]$Root) {
    $marker = Assert-StashIntact $ProfileDir $Root
    $stash = Get-StashDir $Root
    if ($marker.ProfileExisted) {
        New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null
        foreach ($child in Get-ChildItem -LiteralPath $ProfileDir -Force) {
            Remove-Item -LiteralPath $child.FullName -Recurse -Force
        }
        foreach ($child in Get-ChildItem -LiteralPath $stash -Force) {
            Copy-Item -LiteralPath $child.FullName -Destination $ProfileDir -Recurse -Force
        }
        $diff = [System.Collections.Generic.List[string]]::new()
        if (-not (Compare-Manifest (ConvertFrom-MarkerManifest $marker.Manifest) (Get-Manifest $ProfileDir) $diff)) {
            throw "復元の照合に失敗しました。退避は残しています: $stash`n$($diff -join "`n")"
        }
    }
    elseif (Test-Path -LiteralPath $ProfileDir) {
        # 元は存在しなかった=計測で作られたものだけ。
        Remove-Item -LiteralPath $ProfileDir -Recurse -Force
    }
    Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
}

# =====================================================================
#  -SelfTest: 一時フォルダーで退避・復元と中止条件を検証する
# =====================================================================

function Invoke-SelfTest {
    $base = Join-Path $env:TEMP ("kxEdit-perf-harness-selftest-" + [guid]::NewGuid().ToString('N'))
    $prof = Join-Path $base 'profile\kxEdit'
    $root = Join-Path $base 'harness'
    $noProc = 'kxEdit-selftest-no-such-process'
    $script:failed = 0
    function Check([bool]$ok, [string]$msg) {
        if ($ok) { Write-Host "[PASS] $msg" } else { Write-Host "[FAIL] $msg"; $script:failed++ }
    }
    function New-FakeProfile {
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'sub\深い') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'empty') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'backups') | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value '{"FontName":"ＭＳ ゴシック"}' -Encoding utf8
        Set-Content -LiteralPath (Join-Path $prof 'sub\深い\セッション.json') -Value 'あいう' -Encoding utf8
        $h = Join-Path $prof 'hidden.txt'
        Set-Content -LiteralPath $h -Value 'hidden' -Encoding utf8
        (Get-Item -LiteralPath $h -Force).Attributes = 'Hidden'
    }
    try {
        # 1. 退避 → 空にする → 書き換え → 復元 → 元と一致
        New-FakeProfile
        $orig = Get-Manifest $prof
        Check ((Test-HarnessPreconditions $prof $root $noProc).Count -eq 0) '健全な状態では中止条件に当たらない'
        Save-ProfileStash $prof $root
        Check ((Read-Marker $root).State -eq 'stashed') '退避後の目印は stashed'
        Clear-ProfileForRun $prof $root
        Check (@(Get-ChildItem -LiteralPath $prof -Force).Count -eq 0) '計測用にプロフィールが空になる'
        Check ((Test-HarnessPreconditions $prof $root $noProc).Count -gt 0) '退避が残っている間は中止条件に当たる'
        # 計測中に kxEdit が書いたもの(設定・異常終了のバックアップ)は復元で消える
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value 'changed' -Encoding utf8
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'backups') | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'backups\x.json') -Value 'leftover' -Encoding utf8
        Restore-ProfileStash $prof $root
        $diff = [System.Collections.Generic.List[string]]::new()
        Check (Compare-Manifest $orig (Get-Manifest $prof) $diff) "復元後に元と一致する $($diff -join '; ')"
        Check (-not (Test-Path -LiteralPath (Get-MarkerPath $root))) '復元後に目印が消える'
        Check (-not (Test-Path -LiteralPath (Join-Path $root 'stash'))) '復元後に退避が消える'

        # 2. backups にファイルがあれば中止
        Set-Content -LiteralPath (Join-Path $prof 'backups\0123.json') -Value '{}' -Encoding utf8
        Check ((Test-HarnessPreconditions $prof $root $noProc).Count -gt 0) 'backups にファイルがあれば中止する'
        Remove-Item -LiteralPath (Join-Path $prof 'backups\0123.json') -Force

        # 3. 退避が壊れていたら、プロフィールを空にしないし復元もしない(退避を残す)
        Save-ProfileStash $prof $root
        Set-Content -LiteralPath (Join-Path (Get-StashDir $root) 'settings.json') -Value 'tampered' -Encoding utf8
        $threw = $false
        try { Clear-ProfileForRun $prof $root } catch { $threw = $true }
        Check ($threw -and (Test-Path -LiteralPath (Join-Path $prof 'settings.json'))) '退避が壊れていたらプロフィールを空にしない'
        $threw = $false
        try { Restore-ProfileStash $prof $root } catch { $threw = $true }
        Check ($threw -and (Test-Path -LiteralPath (Get-MarkerPath $root))) '退避が壊れていたら復元せず目印を残す'
        Remove-Item -LiteralPath $root -Recurse -Force

        # 4. 元のプロフィールが存在しない場合: 復元で計測の産物を消す
        Remove-Item -LiteralPath $prof -Recurse -Force
        Save-ProfileStash $prof $root
        New-Item -ItemType Directory -Force -Path $prof | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value '{}' -Encoding utf8
        Restore-ProfileStash $prof $root
        Check (-not (Test-Path -LiteralPath $prof)) '元が無かった場合は復元でプロフィールを消す'

        # 5. 再解析ポイント(ジャンクション)があれば中止
        New-FakeProfile
        $target = Join-Path $base 'outside'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $prof 'link') -Target $target | Out-Null
        Check ((Test-HarnessPreconditions $prof $root $noProc).Count -gt 0) '再解析ポイントがあれば中止する'
        (Get-Item -LiteralPath (Join-Path $prof 'link') -Force).Delete()

        # 6. 目印のない退避フォルダーが残っていれば中止
        New-Item -ItemType Directory -Force -Path (Get-StashDir $root) | Out-Null
        Check ((Test-HarnessPreconditions $prof $root $noProc).Count -gt 0) '目印のない退避フォルダーが残っていれば中止する'
    }
    finally {
        if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
    }
    if ($script:failed -gt 0) { Write-Host "自己テスト: $($script:failed) 件失敗"; return 1 }
    Write-Host '自己テスト: すべて PASS'
    return 0
}

if ($SelfTest) { exit (Invoke-SelfTest) }

if ($Recover) {
    if (Get-Process -Name $script:ProcessName -ErrorAction SilentlyContinue) {
        Write-Error "$($script:ProcessName) が起動しています。終了してから実行してください。"
        exit 1
    }
    Restore-ProfileStash $script:ProfileDir $script:HarnessRoot
    Write-Output "復元しました: $($script:ProfileDir)"
    exit 0
}

# (計測本体は Task 5 で追加する)
