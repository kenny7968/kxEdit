#Requires -Version 7
<#
.SYNOPSIS
    kxEdit の実アプリを SendInput で操作し、1 操作あたりのプロセス CPU 時間を測る(性能改善フェーズ 0)。

.DESCRIPTION
    設計書 docs/plans/2026-09-24-general-perf-improvements-design.md §5.2、
    仕様は docs/plans/2026-09-24-general-perf-audit.md §9.5、手順は tools/README.md §3。

    利用者の %APPDATA%\kxEdit(hot exit のバックアップ・セッション・設定)を失わないことを最優先にする。
      - 開始前: kxEdit が起動中 / backups にファイルがある / 前回の退避が残っている / 再解析ポイントがある /
        プロフィールがフォルダーでない、のいずれかなら何も変えずに中止する。
      - 退避はコピー。退避側のハッシュを元と照合してから計測に入る。
      - 計測中は空のプロフィール(既定設定)で起動する。
      - 終了時(finally)に元へ戻し、ハッシュが一致したら退避を消す。一致しなければ退避を残して非 0 で終わる。
      - finally が走らなかった場合も目印ファイルが残るので、次回の起動で検出できる(-Recover で復元)。
        -Recover はその時点のプロフィールを消さずに退かせて残す(異常終了の後に利用者が使った分を失わない)。

.PARAMETER PublishDir
    kxEdit.exe を含む publish フォルダー(dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o <dir>)。

.PARAMETER Scenario
    実行するシナリオ(M-1〜M-7)。既定は全部。

.PARAMETER OutCsv
    結果の CSV。既定は <作業ルート>\results-<日時>.csv。既存のファイルは上書きしない(中止する)。

.PARAMETER WorkDir
    計測用の文書を生成するフォルダー。既定は $env:TEMP\kxEdit-perf-harness。生成するファイルを書くだけで、削除はしない。

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

# pwsh -File では -Scenario M-2,M-7 が 1 つの文字列で届くので、カンマでも分割して検証する。
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
foreach ($s in $Scenario) {
    if ($s -notin 'M-1', 'M-2', 'M-3', 'M-4', 'M-5', 'M-6', 'M-7') { throw "未知のシナリオ: $s(M-1〜M-7)" }
}
# kxEdit は環境変数 APPDATA ではなく既知フォルダー(SHGetKnownFolderPath)でプロフィールを決める
# (SettingsStore / BackupStore ほか)。ハーネスも同じ解決をしないと、別の場所を退避して空にし、
# kxEdit は実プロフィールのまま起動する、という取り違えが起きうる。
function Get-KnownFolder([Environment+SpecialFolder]$Folder) {
    $p = [Environment]::GetFolderPath($Folder)
    if ([string]::IsNullOrWhiteSpace($p) -or -not [IO.Path]::IsPathFullyQualified($p)) {
        throw "既知フォルダー $Folder を解決できません('$p')。"
    }
    return $p.TrimEnd('\')
}
$script:ProfileDir = Join-Path (Get-KnownFolder ApplicationData) 'kxEdit'
$script:HarnessRoot = Join-Path (Get-KnownFolder LocalApplicationData) 'kxEdit-perf-harness'
$script:ProcessName = 'kxEdit'
# kxEdit の単一インスタンス mutex(SingleInstanceNames.MutexName)。実行ファイル名に依存せず起動中を検出する。
$script:KxMutexName = "Local\kxEdit.SingleInstance.$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
$script:HarnessMutexName = 'Local\kxEdit-perf-harness'

# =====================================================================
#  利用者プロフィールの退避・復元(設計書 §5.2「利用者データの保全」)
#  すべての関数はパスを引数に取る(-SelfTest が一時フォルダーで同じコードを通すため)。
# =====================================================================

function Get-MarkerPath([string]$Root) { Join-Path $Root 'STASH-MARKER.json' }
function Get-StashDir([string]$Root) { Join-Path $Root 'stash\kxEdit' }
function Get-RestoreTempDir([string]$ProfileDir) { "$ProfileDir.perf-harness-restore" }

# フォルダーの中身の目録(相対パス → SHA256、空フォルダーを含むフォルダーの一覧)。存在しなければ $null。
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
    if ($null -eq $Actual) { $Diff.Add('照合先が存在しない'); return $false }
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

function Get-ReparsePoints([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir)) { return , @() }
    $rp = @(Get-ChildItem -LiteralPath $Dir -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    $self = Get-Item -LiteralPath $Dir -Force
    if ($self.Attributes -band [IO.FileAttributes]::ReparsePoint) { $rp += $self }
    return , $rp
}

function Assert-NoReparsePoints([string]$Dir) {
    $rp = Get-ReparsePoints $Dir
    if ($rp.Count -gt 0) {
        throw "$Dir にシンボリックリンク等の再解析ポイントがあります($($rp[0].FullName) ほか)。安全に扱えないので中止します。"
    }
}

# kxEdit が 1 つでも動いているか(プロセス名と単一インスタンス mutex の両方で見る)。
function Test-KxEditRunning([string]$ProcessName, [string]$MutexName) {
    if (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) { return $true }
    $m = $null
    if ([Threading.Mutex]::TryOpenExisting($MutexName, [ref]$m)) { $m.Dispose(); return $true }
    return $false
}

function Get-RestoreGuide([string]$ProfileDir, [string]$Root, $Marker) {
    if ($null -ne $Marker -and $Marker.State -eq 'copying') {
        return @"
前回の計測が退避の途中で止まっています。この段階ではプロフィール($ProfileDir)は一度も変更されていません。
退避の残りを片づけるには、kxEdit を終了してから次を実行してください(プロフィールには触れません):
  pwsh -File tools\perf-harness.ps1 -Recover
"@
    }
    if ($null -ne $Marker -and $Marker.State -eq 'stashed') {
        return @"
前回の計測はプロフィールを空にする前に止まっています。プロフィール($ProfileDir)は計測で変更していません。
退避の残りを片づけるには、kxEdit を終了してから次を実行してください(プロフィールには触れません):
  pwsh -File tools\perf-harness.ps1 -Recover
"@
    }
    return @"
前回の計測の退避が残っています(異常終了した可能性があります)。
  退避: $(Get-StashDir $Root)
  目印: $(Get-MarkerPath $Root)
  元の場所: $ProfileDir
復元するには、kxEdit を終了してから次を実行してください(照合してから戻します):
  pwsh -File tools\perf-harness.ps1 -Recover
今のプロフィールの中身は消さずに $Root\displaced-<日時> へ退かせて残します。
自動の復元が失敗した場合は、退避の中身を元の場所へ手でコピーし、内容を確かめてから
$(Join-Path $Root 'stash') と $(Get-MarkerPath $Root) だけを削除してください。
"@
}

# 開始前の中止条件。問題がなければ空配列を返す。何も変更しない。
function Test-HarnessPreconditions([string]$ProfileDir, [string]$Root, [string]$ProcessName, [string]$MutexName) {
    $reasons = [System.Collections.Generic.List[string]]::new()
    if (Test-KxEditRunning $ProcessName $MutexName) {
        $reasons.Add("$ProcessName が起動しています。終了してから実行してください。")
    }
    if (Test-Path -LiteralPath (Get-MarkerPath $Root)) {
        $marker = $null
        try { $marker = Read-Marker $Root } catch { $marker = $null }
        $reasons.Add((Get-RestoreGuide $ProfileDir $Root $marker))
    }
    elseif (Test-Path -LiteralPath (Join-Path $Root 'stash')) {
        $reasons.Add("目印のない退避フォルダーが残っています: $(Join-Path $Root 'stash')。中身を確かめてから削除してください。")
    }
    if (Test-Path -LiteralPath (Get-RestoreTempDir $ProfileDir)) {
        $reasons.Add("復元の途中のフォルダーが残っています: $(Get-RestoreTempDir $ProfileDir)。中身を確かめてから削除してください。")
    }
    if ((Test-Path -LiteralPath $ProfileDir) -and -not (Test-Path -LiteralPath $ProfileDir -PathType Container)) {
        $reasons.Add("$ProfileDir がフォルダーではありません。安全に扱えないので中止します。")
    }
    elseif (Test-Path -LiteralPath $ProfileDir) {
        $backups = Join-Path $ProfileDir 'backups'
        if ((Test-Path -LiteralPath $backups) -and
            @(Get-ChildItem -LiteralPath $backups -Recurse -Force -File).Count -gt 0) {
            $reasons.Add("$backups にファイルがあります(未保存の本文が残っている可能性)。kxEdit を起動して処理してから実行してください。")
        }
        $rp = Get-ReparsePoints $ProfileDir
        if ($rp.Count -gt 0) {
            $reasons.Add("$ProfileDir にシンボリックリンク等の再解析ポイントがあります($($rp[0].FullName) ほか)。安全に退避できないので中止します。")
        }
    }
    return , $reasons.ToArray()
}

function ConvertTo-MarkerJson([hashtable]$Data) { $Data | ConvertTo-Json -Depth 5 }

# 目印の更新は tmp に書いてから置き換える(書込の途中で落ちても壊れた JSON を残さない)。
function Write-Marker([string]$Root, [hashtable]$Data) {
    $path = Get-MarkerPath $Root
    $tmp = "$path.tmp"
    [IO.File]::WriteAllText($tmp, (ConvertTo-MarkerJson $Data), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp, $path, $true)
}

function Read-Marker([string]$Root) {
    $p = Get-MarkerPath $Root
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return Get-Content -LiteralPath $p -Raw -Encoding utf8 | ConvertFrom-Json
}

function New-MarkerData([string]$ProfileDir, [bool]$Existed, [string]$State, $Manifest) {
    return @{
        Version        = 1
        CreatedAt      = (Get-Date).ToString('o')
        ProfilePath    = $ProfileDir
        ProfileExisted = $Existed
        State          = $State
        Manifest       = $Manifest
    }
}

# プロフィールをコピーで退避し、退避側を照合する。失敗したら退避と目印を片づけて例外(プロフィールは無傷)。
function Save-ProfileStash([string]$ProfileDir, [string]$Root) {
    $stash = Get-StashDir $Root
    $markerPath = Get-MarkerPath $Root
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    if (Test-Path -LiteralPath (Join-Path $Root 'stash')) {
        throw "退避フォルダーが既にあります: $(Join-Path $Root 'stash')。上書きしません。"
    }
    if ((Test-Path -LiteralPath $ProfileDir) -and -not (Test-Path -LiteralPath $ProfileDir -PathType Container)) {
        throw "$ProfileDir がフォルダーではありません。"
    }
    Assert-NoReparsePoints $ProfileDir
    $existed = Test-Path -LiteralPath $ProfileDir -PathType Container
    $manifest = if ($existed) { Get-Manifest $ProfileDir } else { $null }
    # 目印は CreateNew で作る=前回の目印(唯一の目録)を上書きしない。二重起動の排他も兼ねる。
    # コピーより先に書くので、コピーの途中で落ちても次回に検出できる。
    try {
        $fs = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "目印が既にあります: $markerPath。上書きしません。"
    }
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-MarkerJson (New-MarkerData $ProfileDir $existed 'copying' $manifest)))
        $fs.Write($bytes, 0, $bytes.Length)
    }
    finally { $fs.Dispose() }
    try {
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
        Write-Marker $Root (New-MarkerData $ProfileDir $existed 'stashed' $manifest)
    }
    catch {
        # プロフィールには触れていない。自分が作った退避と目印だけを片づける。
        Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
        throw
    }
}

# 退避が目印の目録どおりに揃っていること。揃っていなければ例外。
function Assert-StashIntact([string]$ProfileDir, [string]$Root) {
    $marker = Read-Marker $Root
    if ($null -eq $marker -or $marker.State -notin 'stashed', 'cleared') {
        throw "退避が完了していません(目印の状態: $(if ($marker) { $marker.State } else { 'なし' }))。プロフィールには触れません。"
    }
    if ($marker.ProfilePath -ne $ProfileDir) {
        throw "目印の元の場所($($marker.ProfilePath))が対象($ProfileDir)と違います。"
    }
    if ($marker.ProfileExisted -isnot [bool]) { throw '目印の ProfileExisted が真偽値ではありません。' }
    $stash = Get-StashDir $Root
    if ($marker.ProfileExisted) {
        if ($null -eq $marker.Manifest) { throw '目印に目録がありません(ProfileExisted=true と矛盾)。' }
        $diff = [System.Collections.Generic.List[string]]::new()
        if (-not (Compare-Manifest (ConvertFrom-MarkerManifest $marker.Manifest) (Get-Manifest $stash) $diff)) {
            throw "退避が壊れています: $($diff -join '; ')"
        }
    }
    elseif ((Test-Path -LiteralPath $stash) -and @(Get-ChildItem -LiteralPath $stash -Force).Count -gt 0) {
        throw "目印は「元のプロフィールなし」なのに退避に中身があります: $stash"
    }
    return $marker
}

# 計測用にプロフィールを空にする。退避の照合が済んでいなければ何もしない(例外)。
function Clear-ProfileForRun([string]$ProfileDir, [string]$Root) {
    $marker = Assert-StashIntact $ProfileDir $Root
    if ($marker.State -eq 'stashed') {
        # 初めて空にする直前に cleared へ進める。stashed のままなら「プロフィールは一度も空にしていない」
        # =復元でプロフィールに触れてはならない(その間に利用者が kxEdit を使っていたら最新のデータがある)。
        Write-Marker $Root (New-MarkerData $ProfileDir ([bool]$marker.ProfileExisted) 'cleared' $marker.Manifest)
    }
    if (-not (Test-Path -LiteralPath $ProfileDir)) { return }
    if (-not (Test-Path -LiteralPath $ProfileDir -PathType Container)) { throw "$ProfileDir がフォルダーではありません。" }
    Assert-NoReparsePoints $ProfileDir
    foreach ($child in Get-ChildItem -LiteralPath $ProfileDir -Force) {
        Remove-Item -LiteralPath $child.FullName -Recurse -Force
    }
}

# 退避から元へ戻し、照合できたら退避と目印を消す。照合できなければ退避を残して例外。
# -Displace: 今のプロフィールを消さずに $Root\displaced-<日時>\kxEdit へ退かせる(-Recover 用)。
#   異常終了の後に利用者が kxEdit を使っていれば、その本文や設定がここに入っているため。
#   計測の正常な終了(finally)では、今の中身は計測の産物だけなので消す。
# 戻り値: 退かせた場所($null なら退かせていない)。
function Restore-ProfileStash([string]$ProfileDir, [string]$Root, [switch]$Displace) {
    $marker = Read-Marker $Root
    if ($null -ne $marker -and $marker.State -eq 'copying') {
        # 退避の途中で止まった=プロフィールは一度も変更されていない。退避と目印だけを片づける。
        # 目印を先に消す(途中で落ちても「目印のない退避」として中止・案内される安全側に倒れる)。
        Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
        Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
        return $null
    }
    $marker = Assert-StashIntact $ProfileDir $Root
    if ($marker.State -eq 'stashed') {
        # 一度も空にしていない=プロフィールは計測で変更していない。プロフィールには触れない。
        # 退避を消してよいのは、プロフィールが退避の時点と同じとき(=退避が不要と確かめられたとき)だけ。
        $actual = Get-Manifest $ProfileDir
        $same = if ($marker.ProfileExisted) {
            Compare-Manifest (ConvertFrom-MarkerManifest $marker.Manifest) $actual ([System.Collections.Generic.List[string]]::new())
        }
        else { $null -eq $actual }
        if (-not $same) {
            throw "計測はプロフィールを空にする前に止まりましたが、プロフィールはその後に変更されています(kxEdit を使った可能性)。プロフィールには触れません。退避 $(Get-StashDir $Root) と目印 $(Get-MarkerPath $Root) は、内容を確かめてから手で削除してください。"
        }
        Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
        Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
        return $null
    }
    if ((Test-Path -LiteralPath $ProfileDir) -and (Test-Path -LiteralPath $ProfileDir -PathType Container)) {
        Assert-NoReparsePoints $ProfileDir
    }
    $tmp = Get-RestoreTempDir $ProfileDir
    if ($marker.ProfileExisted) {
        # 退避を隣の一時フォルダーへコピーして照合してから、名前の付け替えで差し替える
        # (「消してからコピー」の途中で落ちてプロフィールが半端に残る時間をなくす)。
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
        New-Item -ItemType Directory -Path $tmp | Out-Null
        foreach ($child in Get-ChildItem -LiteralPath (Get-StashDir $Root) -Force) {
            Copy-Item -LiteralPath $child.FullName -Destination $tmp -Recurse -Force
        }
        $diff = [System.Collections.Generic.List[string]]::new()
        if (-not (Compare-Manifest (ConvertFrom-MarkerManifest $marker.Manifest) (Get-Manifest $tmp) $diff)) {
            Remove-Item -LiteralPath $tmp -Recurse -Force
            throw "復元用のコピーの照合に失敗しました。プロフィールと退避はそのままです。`n$($diff -join "`n")"
        }
    }
    $displaced = $null
    if (Test-Path -LiteralPath $ProfileDir) {
        if ($Displace) {
            $displaced = Join-Path $Root ('displaced-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            New-Item -ItemType Directory -Path $displaced | Out-Null
            Move-Item -LiteralPath $ProfileDir -Destination (Join-Path $displaced 'kxEdit')
        }
        else {
            Remove-Item -LiteralPath $ProfileDir -Recurse -Force
        }
    }
    if ($marker.ProfileExisted) {
        Move-Item -LiteralPath $tmp -Destination $ProfileDir
        $diff = [System.Collections.Generic.List[string]]::new()
        if (-not (Compare-Manifest (ConvertFrom-MarkerManifest $marker.Manifest) (Get-Manifest $ProfileDir) $diff)) {
            throw "復元後の照合に失敗しました。退避は残しています: $(Get-StashDir $Root)`n$($diff -join "`n")"
        }
    }
    Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
    Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
    return $displaced
}

# =====================================================================
#  -SelfTest: 一時フォルダーで退避・復元と中止条件を検証する
# =====================================================================

function Invoke-SelfTest {
    $base = Join-Path ([IO.Path]::GetTempPath()) ("kxEdit-perf-harness-selftest-" + [guid]::NewGuid().ToString('N'))
    $prof = Join-Path $base 'profile\kxEdit'
    $root = Join-Path $base 'harness'
    $noProc = 'kxEdit-selftest-no-such-process'
    $noMutex = 'Local\kxEdit-selftest-no-such-mutex-' + [guid]::NewGuid().ToString('N')
    $script:failed = 0
    function Check([bool]$ok, [string]$msg) {
        if ($ok) { Write-Host "[PASS] $msg" } else { Write-Host "[FAIL] $msg"; $script:failed++ }
    }
    # 中止理由のうち、$Needle を含むものがあるか(別の理由で当たって PASS するのを防ぐ)。
    function HasReason([string[]]$Reasons, [string]$Needle) { return @($Reasons | Where-Object { $_.Contains($Needle) }).Count -gt 0 }
    function Pre { return Test-HarnessPreconditions $prof $root $noProc $noMutex }
    function Throws([scriptblock]$Block) { try { & $Block; return $false } catch { return $true } }
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
        Check ((Pre).Count -eq 0) '健全な状態では中止条件に当たらない'
        Save-ProfileStash $prof $root
        Check ((Read-Marker $root).State -eq 'stashed') '退避後の目印は stashed'
        Check (Throws { Save-ProfileStash $prof $root }) '退避が残っている間の再退避は拒否する(目印を上書きしない)'
        Clear-ProfileForRun $prof $root
        Check (@(Get-ChildItem -LiteralPath $prof -Force).Count -eq 0) '計測用にプロフィールが空になる'
        Check (HasReason (Pre) '退避が残っています') '退避が残っている間は中止条件に当たる'
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value 'changed' -Encoding utf8
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'backups') | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'backups\x.json') -Value 'leftover' -Encoding utf8
        $d = Restore-ProfileStash $prof $root
        $diff = [System.Collections.Generic.List[string]]::new()
        Check (Compare-Manifest $orig (Get-Manifest $prof) $diff) "復元後に元と一致する $($diff -join '; ')"
        Check ($null -eq $d) '通常の復元では計測の産物を退かせずに消す'
        Check (-not (Test-Path -LiteralPath (Get-MarkerPath $root))) '復元後に目印が消える'
        Check (-not (Test-Path -LiteralPath (Join-Path $root 'stash'))) '復元後に退避が消える'
        Check ((Get-Item -LiteralPath (Join-Path $prof 'hidden.txt') -Force).Attributes -band [IO.FileAttributes]::Hidden) '隠しファイルが隠し属性のまま戻る'

        # 2. -Recover 相当(-Displace): 異常終了の後に利用者が作ったものを消さずに退かせる
        Save-ProfileStash $prof $root
        Clear-ProfileForRun $prof $root
        New-Item -ItemType Directory -Force -Path (Join-Path $prof 'backups') | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'backups\user-new.json') -Value 'user text' -Encoding utf8
        $d = Restore-ProfileStash $prof $root -Displace
        $diff = [System.Collections.Generic.List[string]]::new()
        Check (Compare-Manifest $orig (Get-Manifest $prof) $diff) "-Displace でも元と一致する $($diff -join '; ')"
        Check (($null -ne $d) -and (Test-Path -LiteralPath (Join-Path $d 'kxEdit\backups\user-new.json'))) '-Displace は今の中身を退かせて残す'
        Remove-Item -LiteralPath $d -Recurse -Force

        # 3. 中止条件(それぞれ、その理由で当たること)
        Set-Content -LiteralPath (Join-Path $prof 'backups\0123.json') -Value '{}' -Encoding utf8
        Check (HasReason (Pre) 'にファイルがあります') 'backups にファイルがあれば中止する'
        Remove-Item -LiteralPath (Join-Path $prof 'backups\0123.json') -Force
        $self = (Get-Process -Id $PID).ProcessName
        Check (HasReason (Test-HarnessPreconditions $prof $root $self $noMutex) 'が起動しています') '対象のプロセスが起動していれば中止する'
        $mname = 'Local\kxEdit-selftest-mutex-' + [guid]::NewGuid().ToString('N')
        $held = [Threading.Mutex]::new($false, $mname)
        try { Check (HasReason (Test-HarnessPreconditions $prof $root $noProc $mname) 'が起動しています') '単一インスタンス mutex があれば中止する' }
        finally { $held.Dispose() }

        # 4. 退避が壊れていたら、プロフィールを空にしない
        Save-ProfileStash $prof $root
        Set-Content -LiteralPath (Join-Path (Get-StashDir $root) 'settings.json') -Value 'tampered' -Encoding utf8
        Check ((Throws { Clear-ProfileForRun $prof $root }) -and (Test-Path -LiteralPath (Join-Path $prof 'settings.json'))) '退避が壊れていたらプロフィールを空にしない'
        Check ((Read-Marker $root).State -eq 'stashed') '空にするのを拒否したら目印は stashed のまま'
        Remove-Item -LiteralPath $root -Recurse -Force

        # 4b. 空にした後に退避が壊れたら、復元せず目印と退避を残す
        Save-ProfileStash $prof $root
        Clear-ProfileForRun $prof $root
        Check ((Read-Marker $root).State -eq 'cleared') '空にしたら目印は cleared'
        Set-Content -LiteralPath (Join-Path (Get-StashDir $root) 'settings.json') -Value 'tampered' -Encoding utf8
        Check ((Throws { Restore-ProfileStash $prof $root }) -and (Test-Path -LiteralPath (Get-MarkerPath $root)) -and (Test-Path -LiteralPath (Get-StashDir $root))) '退避が壊れていたら復元せず目印と退避を残す'
        Remove-Item -LiteralPath $root -Recurse -Force
        Remove-Item -LiteralPath $prof -Recurse -Force
        New-FakeProfile

        # 4c. 一度も空にしていない(stashed)まま止まった場合: プロフィールに触れない
        Save-ProfileStash $prof $root
        Check (HasReason (Pre) '空にする前に止まっています') 'stashed の目印では「プロフィールは変更していない」と案内する'
        [void](Restore-ProfileStash $prof $root -Displace)
        $diff = [System.Collections.Generic.List[string]]::new()
        Check ((Compare-Manifest $orig (Get-Manifest $prof) $diff) -and -not (Test-Path -LiteralPath (Get-MarkerPath $root))) 'stashed からの Recover はプロフィールに触れず退避を片づける'
        Save-ProfileStash $prof $root
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value 'user changed later' -Encoding utf8
        Check ((Throws { Restore-ProfileStash $prof $root -Displace }) -and ((Get-Content -LiteralPath (Join-Path $prof 'settings.json') -Raw).Trim() -eq 'user changed later') -and (Test-Path -LiteralPath (Get-StashDir $root))) 'stashed の後に利用者が変えたプロフィールは差し替えない(退避も残す)'
        Remove-Item -LiteralPath $root -Recurse -Force
        Remove-Item -LiteralPath $prof -Recurse -Force
        New-FakeProfile

        # 5. 目印の矛盾・改ざん
        Save-ProfileStash $prof $root
        $m = Get-Content -LiteralPath (Get-MarkerPath $root) -Raw | ConvertFrom-Json -AsHashtable
        $m.ProfileExisted = $false
        Write-Marker $root $m
        Check ((Throws { Restore-ProfileStash $prof $root }) -and (Test-Path -LiteralPath (Get-StashDir $root))) '「元なし」なのに退避に中身があれば復元しない(退避を消さない)'
        $m.ProfileExisted = $true; $m.ProfilePath = Join-Path $base 'other\kxEdit'
        Write-Marker $root $m
        Check (Throws { Clear-ProfileForRun $prof $root }) '目印の元の場所が違えばプロフィールに触れない'
        Remove-Item -LiteralPath $root -Recurse -Force

        # 6. 退避の途中で止まった(copying)場合: プロフィールに触れず、退避と目印だけを片づける
        New-Item -ItemType Directory -Force -Path (Get-StashDir $root) | Out-Null
        Set-Content -LiteralPath (Join-Path (Get-StashDir $root) 'partial.json') -Value 'x' -Encoding utf8
        Write-Marker $root (New-MarkerData $prof $true 'copying' $orig)
        Check (HasReason (Pre) '一度も変更されていません') 'copying の目印では「プロフィールは無傷」と案内する'
        Check (Throws { Clear-ProfileForRun $prof $root }) 'copying の目印ではプロフィールを空にしない'
        [void](Restore-ProfileStash $prof $root -Displace)
        $diff = [System.Collections.Generic.List[string]]::new()
        Check ((Compare-Manifest $orig (Get-Manifest $prof) $diff) -and -not (Test-Path -LiteralPath (Get-MarkerPath $root)) -and -not (Test-Path -LiteralPath (Join-Path $root 'stash'))) 'copying からの Recover は退避と目印だけを消す'

        # 7. 元のプロフィールが存在しない場合: 復元で計測の産物を消す
        $saved = Join-Path $base 'saved-profile'
        Move-Item -LiteralPath $prof -Destination $saved
        Save-ProfileStash $prof $root
        Clear-ProfileForRun $prof $root
        Check ((Read-Marker $root).State -eq 'cleared') '元が無くても、空にする段で目印は cleared になる'
        New-Item -ItemType Directory -Force -Path $prof | Out-Null
        Set-Content -LiteralPath (Join-Path $prof 'settings.json') -Value '{}' -Encoding utf8
        [void](Restore-ProfileStash $prof $root)
        Check (-not (Test-Path -LiteralPath $prof)) '元が無かった場合は復元でプロフィールを消す'
        Move-Item -LiteralPath $saved -Destination $prof

        # 8. 再解析ポイント(ジャンクション)があれば中止し、退避もしない
        $target = Join-Path $base 'outside'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $prof 'link') -Target $target | Out-Null
        Check (HasReason (Pre) '再解析ポイント') '再解析ポイントがあれば中止する'
        Check (Throws { Save-ProfileStash $prof $root }) '再解析ポイントがあれば退避しない'
        Check (-not (Test-Path -LiteralPath (Get-MarkerPath $root))) '退避を拒否したら目印を残さない'
        (Get-Item -LiteralPath (Join-Path $prof 'link') -Force).Delete()

        # 9. プロフィールがファイルなら中止
        $fileProf = Join-Path $base 'file-profile'
        Set-Content -LiteralPath $fileProf -Value 'x' -Encoding utf8
        Check (HasReason (Test-HarnessPreconditions $fileProf $root $noProc $noMutex) 'フォルダーではありません') 'プロフィールがファイルなら中止する'

        # 10. 目印のない退避フォルダーが残っていれば中止
        New-Item -ItemType Directory -Force -Path (Get-StashDir $root) | Out-Null
        Check (HasReason (Pre) '目印のない退避フォルダー') '目印のない退避フォルダーが残っていれば中止する'
    }
    finally {
        if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
    }
    if ($script:failed -gt 0) { Write-Host "自己テスト: $($script:failed) 件失敗"; return 1 }
    Write-Host '自己テスト: すべて PASS'
    return 0
}

if ($SelfTest) { exit (Invoke-SelfTest) }

# ハーネス自身の二重起動を弾く(同時に 2 本走ると、片方が空にしたプロフィールをもう片方が退避しうる)。
$script:HarnessMutex = [Threading.Mutex]::new($false, $script:HarnessMutexName)
if (-not $script:HarnessMutex.WaitOne(0)) {
    Write-Host '別の perf-harness が実行中です。' -ForegroundColor Yellow
    exit 2
}

if ($Recover) {
    if (Test-KxEditRunning $script:ProcessName $script:KxMutexName) {
        Write-Host "$($script:ProcessName) が起動しています。終了してから実行してください。" -ForegroundColor Yellow
        exit 2
    }
    if ($null -eq (Read-Marker $script:HarnessRoot)) {
        Write-Host "復元するものはありません(目印がありません): $(Get-MarkerPath $script:HarnessRoot)"
        exit 0
    }
    $mk = Read-Marker $script:HarnessRoot
    Write-Host "退避の日時: $($mk.CreatedAt)(状態: $($mk.State))"
    $displaced = Restore-ProfileStash $script:ProfileDir $script:HarnessRoot -Displace
    Write-Host "復元しました(照合済み): $($script:ProfileDir)"
    if ($displaced) {
        Write-Host "復元前のプロフィールの中身は次に残しています。不要なら削除してください: $displaced"
    }
    exit 0
}

# =====================================================================
#  計測本体(M-1〜M-7)。仕様は調査記録 §9.5。
# =====================================================================

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class KxPerfNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>
    /// 送る直前(µs 級の隙間)に、前面の窓が期待するプロセスのものかを照合してから送る。
    /// PowerShell 側で照合してから送ると、その間の ms 級の隙間で前面が変わりうる。
    /// </summary>
    static void Send(uint expectedPid, INPUT[] inputs, ushort[] modsToRelease)
    {
        uint fgPid;
        GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
        if (expectedPid == 0 || fgPid != expectedPid)
            throw new InvalidOperationException("前面の窓が計測対象(pid " + expectedPid + ")ではありません(pid " + fgPid + ")。入力を送らずに中止します。");
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        if (sent != inputs.Length)
        {
            // 一部だけ入ると修飾キーが押されたまま残り、以後の利用者の打鍵が Ctrl+キー等になる。離しておく。
            if (modsToRelease != null && modsToRelease.Length > 0)
            {
                var ups = new List<INPUT>();
                foreach (var m in modsToRelease) ups.Add(Key(m, true, false));
                SendInput((uint)ups.Count, ups.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            }
            throw new InvalidOperationException("SendInput が失敗しました(err " + Marshal.GetLastWin32Error() + ")。UIPI(相手が昇格)か、入力がブロックされています。");
        }
    }

    static INPUT Key(ushort vk, bool up, bool ext)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = vk;
        i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (ext ? KEYEVENTF_EXTENDEDKEY : 0);
        return i;
    }

    // 矢印・Home・End・PageUp・PageDown・Insert・Delete は拡張キー(調査記録 §9.5)。
    static bool IsExtended(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }

    /// <summary>修飾キー(0 個以上)を押したまま vk を 1 回押す。</summary>
    public static void Tap(uint expectedPid, ushort vk, params ushort[] mods)
    {
        var list = new List<INPUT>();
        foreach (var m in mods) list.Add(Key(m, false, false));
        list.Add(Key(vk, false, IsExtended(vk)));
        list.Add(Key(vk, true, IsExtended(vk)));
        for (int k = mods.Length - 1; k >= 0; k--) list.Add(Key(mods[k], true, false));
        Send(expectedPid, list.ToArray(), mods);
    }

    /// <summary>文字列を KEYEVENTF_UNICODE で送る(IME を通らず WM_CHAR として届く)。</summary>
    public static void TypeText(uint expectedPid, string s)
    {
        var list = new List<INPUT>();
        foreach (char c in s)
        {
            foreach (bool up in new[] { false, true })
            {
                var i = new INPUT { type = INPUT_KEYBOARD };
                i.u.ki.wScan = c;
                i.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
                list.Add(i);
            }
        }
        Send(expectedPid, list.ToArray(), null);
    }

    /// <summary>ホイールはカーソル直下の窓へ届くので、直下が target でなければ送らない。</summary>
    public static void Wheel(uint expectedPid, IntPtr target, int delta)
    {
        if (WindowUnderCursor() != target)
            throw new InvalidOperationException("カーソル直下がエディタではありません(他の窓に覆われた・カーソルが動いた)。ホイールを送らずに中止します。");
        var i = new INPUT { type = INPUT_MOUSE };
        i.u.mi.mouseData = delta;
        i.u.mi.dwFlags = MOUSEEVENTF_WHEEL;
        Send(expectedPid, new[] { i }, null);
    }

    // ---- 起動した kxEdit を Job に入れる(pwsh の窓を閉じても計測用の kxEdit を残さない) ----
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    static IntPtr s_job = IntPtr.Zero;

    /// <summary>
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE の Job に入れる。ハンドルは pwsh が持ち続け、pwsh が終わる
    /// (窓を閉じる・強制終了)と OS が Job を閉じて kxEdit も終わる。空のプロフィールのまま計測用の
    /// kxEdit が利用者の手に残るのを防ぐ。
    /// </summary>
    public static void KillOnHarnessExit(IntPtr processHandle)
    {
        if (s_job == IntPtr.Zero)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject に失敗しました。");
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9 /*JobObjectExtendedLimitInformation*/, ref info, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                throw new InvalidOperationException("SetInformationJobObject に失敗しました。");
            s_job = job;
        }
        if (!AssignProcessToJobObject(s_job, processHandle))
            throw new InvalidOperationException("AssignProcessToJobObject に失敗しました(err " + Marshal.GetLastWin32Error() + ")。");
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO { public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }

    /// <summary>窓 h の UI スレッドでキーボードフォーカスを持つ HWND(取れなければ 0)。</summary>
    public static IntPtr FocusOf(IntPtr h)
    {
        uint pid;
        uint tid = GetWindowThreadProcessId(h, out pid);
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        return GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
    }

    /// <summary>窓 h の UI スレッドの ID。</summary>
    public static uint ThreadOf(IntPtr h) { uint pid; return GetWindowThreadProcessId(h, out pid); }


    /// <summary>前面化を最大 20 回試す(AttachThreadInput 併用)。成否を返す。</summary>
    public static bool Foreground(IntPtr h)
    {
        for (int k = 0; k < 20; k++)
        {
            if (GetForegroundWindow() == h) return true;
            uint pid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            Thread.Sleep(50);
        }
        return GetForegroundWindow() == h;
    }

    public static void Resize(IntPtr h, int w, int hgt)
    {
        const uint SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(h, IntPtr.Zero, 0, 0, w, hgt, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>外から全面の再描画を同期で起こす(RDW_INVALIDATE | RDW_UPDATENOW)。</summary>
    public static void Redraw(IntPtr h) { RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0100); }

    /// <summary>WM_NULL が処理されるまで待つ(= UI スレッドがメッセージを捌ける)。</summary>
    public static bool Ping(IntPtr h, uint timeoutMs)
    {
        IntPtr res;
        return SendMessageTimeout(h, 0, IntPtr.Zero, IntPtr.Zero, 0x0002 /*SMTO_ABORTIFHUNG*/, timeoutMs, out res) != IntPtr.Zero;
    }

    public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }

    [DllImport("user32.dll")] static extern int GetScrollPos(IntPtr h, int bar);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    /// <summary>エディタ内の縦スクロールバー(子の SCROLLBAR コントロール)の位置。見つからなければ -1。</summary>
    public static int VScrollPos(IntPtr editor)
    {
        IntPtr best = IntPtr.Zero;
        EnumChildWindows(editor, (h, l) =>
        {
            if (!IsWindowVisible(h) || ClassOf(h).IndexOf("SCROLLBAR", StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            if (r.Bottom - r.Top > r.Right - r.Left) { best = h; return false; }
            return true;
        }, IntPtr.Zero);
        return best == IntPtr.Zero ? -1 : GetScrollPos(best, 2 /*SB_CTL*/);
    }

    /// <summary>カーソル直下の窓(ホイールの配送先の確認用)。</summary>
    public static IntPtr WindowUnderCursor() { POINT p; GetCursorPos(out p); return WindowFromPoint(p); }

    /// <summary>
    /// エディタの中で、他の窓(NVDA のスピーチビューアー等の最前面の窓)に覆われていない点へカーソルを置く。
    /// ホイールはカーソル直下の窓へ届くため。見つからなければ false。
    /// </summary>
    public static bool CursorToVisiblePoint(IntPtr editor)
    {
        RECT r; GetWindowRect(editor, out r);
        for (int fy = 5; fy <= 95; fy += 10)
            for (int fx = 5; fx <= 75; fx += 10)
            {
                int x = r.Left + (r.Right - r.Left) * fx / 100, y = r.Top + (r.Bottom - r.Top) * fy / 100;
                SetCursorPos(x, y);
                if (WindowUnderCursor() == editor) return true;
            }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// 別プロセスの入力欄の文字列(WM_GETTEXT)。GetWindowText は別プロセスのコントロールには
    /// WM_GETTEXT を送らず、キャプションしか返さない。
    /// </summary>
    public static string ControlTextOf(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        IntPtr res;
        SendMessageTimeout(h, 0x000D /*WM_GETTEXT*/, (IntPtr)sb.Capacity, sb, 0x0002, 2000, out res);
        return sb.ToString();
    }

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TextOf(IntPtr h) { var sb = new StringBuilder(GetWindowTextLength(h) + 1); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

    /// <summary>
    /// エディタ本体の HWND: メインウィンドウの子孫で、可視・クラス名 WindowsForms10.Window.8.*・
    /// タイトル空(TabPage はタブ名を持つ)の中で面積が最大のもの(調査記録 §9.5)。
    /// </summary>
    public static IntPtr FindEditor(IntPtr main)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumChildWindows(main, (h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (!ClassOf(h).StartsWith("WindowsForms10.Window.8.", StringComparison.Ordinal)) return true;
            if (GetWindowTextLength(h) != 0) return true;
            RECT r; GetWindowRect(h, out r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>プロセス pid の可視なトップレベル窓のうち、main 以外で最初に見つかったもの(ダイアログ)。</summary>
    public static IntPtr FindOtherTopLevel(int pid, IntPtr main)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && h != main && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# ---- 仮想キー ----
$VK = @{
    Back = 0x08; Tab = 0x09; Enter = 0x0D; Shift = 0x10; Ctrl = 0x11; Escape = 0x1B
    PageUp = 0x21; PageDown = 0x22; End = 0x23; Home = 0x24; Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28
    N = 0x4E; O = 0x4F; F = 0x46; V = 0x56
}
# 入力は C# 側で送る直前に、前面の窓が計測対象のプロセスのものであることを照合する。単一インスタンスの
# 転送や利用者の操作で前面が変わっていたら、キー(Ctrl+V・x・BackSpace)を他のアプリへ送らずに中止する。
$script:TargetPid = 0
function Assert-TargetForeground {
    $fgPid = [uint32]0
    [void][KxPerfNative]::GetWindowThreadProcessId([KxPerfNative]::GetForegroundWindow(), [ref]$fgPid)
    if ($script:TargetPid -eq 0 -or $fgPid -ne $script:TargetPid) {
        throw "前面の窓が計測対象の kxEdit(pid $($script:TargetPid))ではありません(pid $fgPid)。入力を送らずに中止します。"
    }
}
function Tap([int]$Key, [int[]]$Mods = @()) { [KxPerfNative]::Tap([uint32]$script:TargetPid, [uint16]$Key, [uint16[]]$Mods) }
function Send-Text([string]$Text) { [KxPerfNative]::TypeText([uint32]$script:TargetPid, $Text) }
function Send-Wheel([IntPtr]$Target, [int]$Delta) { [KxPerfNative]::Wheel([uint32]$script:TargetPid, $Target, $Delta) }

# キーボードフォーカスが期待の窓にあること(前面の窓だけでは、フォーカスがボタン等にあってキーが
# 黙って捨てられるのを検出できない=「何もしない費用」を測ってしまう)。
function Assert-Focus($Kx, [IntPtr]$Expected, [string]$What) {
    $f = [KxPerfNative]::FocusOf($Kx.Main)
    if ($f -ne $Expected) {
        throw "$What にキーボードフォーカスがありません(フォーカス 0x$($f.ToString('X'))[$([KxPerfNative]::ClassOf($f))])。"
    }
}

# 入力の効果の確認に使う(計測区間の外で呼ぶ)。
function Test-TitleDirty($Kx) { return ([KxPerfNative]::TextOf($Kx.Main)).StartsWith('* ') }

# ---- 文書の生成(調査記録 §9.5。UTF-8・BOM なし・CRLF。バイト数を検査する) ----
function New-Doc([string]$Path, [string]$Format, [int]$Lines, [long]$ExpectedBytes) {
    $sb = [Text.StringBuilder]::new()
    for ($i = 1; $i -le $Lines; $i++) {
        [void]$sb.Append([string]::Format([Globalization.CultureInfo]::InvariantCulture, $Format, $i)).Append("`r`n")
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($sb.ToString())
    if ($bytes.Length -ne $ExpectedBytes) { throw "$Path の生成が仕様と違う: $($bytes.Length) バイト(仕様 $ExpectedBytes)" }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

$JaLine = '{0:D5}: 吾輩は猫である。名前はまだ無い。kxEdit の性能計測 sample 行です。'
$EnLine = '{0:D5}: The quick brown fox jumps over the lazy dog; perf sample line.'
$PasteLine = '{0:D3}: 吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。'

function Initialize-Docs([string]$Dir) {
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $docs = @{
        ja10k = Join-Path $Dir 'ja10k.txt'
        en10k = Join-Path $Dir 'en10k.txt'
        ja30k = Join-Path $Dir 'ja30k.txt'
    }
    New-Doc $docs.ja10k $JaLine 10000 990000
    New-Doc $docs.en10k $EnLine 10000 710000
    New-Doc $docs.ja30k $JaLine 30000 2970000
    $sb = [Text.StringBuilder]::new()
    for ($i = 1; $i -le 100; $i++) {
        [void]$sb.Append([string]::Format([Globalization.CultureInfo]::InvariantCulture, $PasteLine, $i)).Append("`r`n")
    }
    $docs.paste = $sb.ToString()
    return $docs
}

# ---- 結果 ----
$script:Results = [System.Collections.Generic.List[object]]::new()
# flags: 値の信頼性に関わる出来事(quiet_timeout=静穏待ちがタイムアウトし、背景の CPU が混ざった可能性)。
$script:PendingFlags = [System.Collections.Generic.List[string]]::new()
function Add-Result([string]$Scenario, [string]$Condition, [string]$Doc, [int]$N, [double]$Value, [string]$Unit) {
    $flags = ($script:PendingFlags | Select-Object -Unique) -join ';'
    $script:PendingFlags.Clear()
    $row = [pscustomobject]@{ scenario = $Scenario; condition = $Condition; doc = $Doc; n = $N; value = [math]::Round($Value, 2); unit = $Unit; flags = $flags }
    $script:Results.Add($row)
    Write-Host ("  {0,-4} {1,-28} {2,-6} n={3,-4} {4,9:F2} {5} {6}" -f $Scenario, $Condition, $Doc, $N, $Value, $Unit, $flags)
}

# ---- kxEdit の起動と終了 ----
$script:Launched = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

# 起動した Process オブジェクトだけを止める(名前で kill しない)。終了を確認できなければ例外。
function Stop-Proc([Diagnostics.Process]$P) {
    if (-not $P.HasExited) {
        try { $P.Kill() } catch [InvalidOperationException] { } # 直前に終了した
        if (-not $P.WaitForExit(10000)) { throw "kxEdit(pid $($P.Id))が停止しません。" }
    }
    [void]$script:Launched.Remove($P)
}

function Stop-Launched {
    foreach ($p in @($script:Launched)) { Stop-Proc $p }
}

# プロフィールを空にする前に、計測対象以外の kxEdit が動いていないことを確かめる
# (利用者が計測中に kxEdit を起動していたら、その実データの下でプロフィールを消すことになる)。
function Assert-NoKxEditRunning {
    if (Test-KxEditRunning $script:ProcessName $script:KxMutexName) {
        throw 'kxEdit が動いています(計測中に起動された可能性)。プロフィールに触れずに中止します。'
    }
}

# 空のプロフィールで起動し、窓を 900×700 にして前面へ出す。{ Proc, Main, Editor } を返す。
function Start-KxEdit([string]$Exe) {
    Assert-NoKxEditRunning
    Clear-ProfileForRun $script:ProfileDir $script:HarnessRoot
    $p = Start-Process -FilePath $Exe -PassThru
    $script:Launched.Add($p)
    [KxPerfNative]::KillOnHarnessExit($p.Handle)
    $script:TargetPid = $p.Id
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0 -and [KxPerfNative]::IsWindowVisible($p.MainWindowHandle)) { break }
        if ($p.HasExited) { throw "kxEdit が起動直後に終了しました(exit $($p.ExitCode))" }
        if ($sw.ElapsedMilliseconds -gt 15000) { throw 'kxEdit の窓が 15 秒以内に出ません' }
        Start-Sleep -Milliseconds 20
    }
    [void]$p.WaitForInputIdle(10000)
    if ($p.HasExited) { throw 'kxEdit が起動直後に終了しました(単一インスタンスの転送の可能性)。入力を送らずに中止します。' }
    $main = $p.MainWindowHandle
    [KxPerfNative]::Resize($main, 900, 700)
    Enter-Foreground $main
    [void](Wait-Quiet $p)
    $editor = [KxPerfNative]::FindEditor($main)
    if ($editor -eq 0) { throw 'エディタの HWND が見つかりません' }
    return [pscustomobject]@{ Proc = $p; Main = $main; Editor = $editor }
}

function Enter-Foreground([IntPtr]$Hwnd) {
    if (-not [KxPerfNative]::Foreground($Hwnd)) {
        throw "kxEdit を前面に出せません(SetForegroundWindow を 20 回試行)。計測中は画面・キーボード・マウスに触らないでください。"
    }
}

# 前面にあるのが kxEdit のメインウィンドウであることを確かめる(キーを他のアプリへ送らないため)。
function Assert-Foreground($Kx, [IntPtr]$Expected = $Kx.Main) {
    Assert-TargetForeground
    if ([KxPerfNative]::GetForegroundWindow() -ne $Expected) {
        throw '前面の窓が計測対象ではありません(フォーカスが奪われた)。計測を中止します。'
    }
}

function Stop-KxEdit($Kx) {
    Stop-Proc $Kx.Proc
    $script:TargetPid = 0
}

# Ctrl+O → ファイルを開くダイアログにフルパスを打って Enter(kxEdit はコマンドラインでファイルを開けない)。
function Open-Doc($Kx, [string]$Path) {
    Assert-Foreground $Kx
    Tap $VK.O @($VK.Ctrl)
    $dlg = Wait-Dialog $Kx
    Start-Sleep -Milliseconds 300 # ファイル名欄にフォーカスが入るまで
    Send-Text $Path
    Tap $VK.Enter
    $name = [IO.Path]::GetFileName($Path)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not ([KxPerfNative]::TextOf($Kx.Main)).StartsWith($name)) {
        if ($sw.ElapsedMilliseconds -gt 20000) { throw "$name を開けません(タイトル: $([KxPerfNative]::TextOf($Kx.Main)))" }
        Start-Sleep -Milliseconds 50
    }
    [void](Wait-Quiet $Kx.Proc)
    Enter-Foreground $Kx.Main
    $Kx.Editor = [KxPerfNative]::FindEditor($Kx.Main)
}

function Wait-Dialog($Kx, [int]$TimeoutMs = 5000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $h = [KxPerfNative]::FindOtherTopLevel($Kx.Proc.Id, $Kx.Main)
        if ($h -ne 0 -and [KxPerfNative]::GetForegroundWindow() -eq $h) { return $h }
        if ($sw.ElapsedMilliseconds -gt $TimeoutMs) { throw 'ダイアログが出ません' }
        Start-Sleep -Milliseconds 50
    }
}

# ---- 1 回あたり CPU の測り方(調査記録 §9.5 Measure-Op) ----
function Get-CpuMs([Diagnostics.Process]$P) { $P.Refresh(); return $P.TotalProcessorTime.TotalMilliseconds }

# 100 ms ごとに CPU を見て、増分 < 2 ms が 3 回続くまで待つ(最大 30 秒)。静穏になったかを返す。
function Wait-Quiet([Diagnostics.Process]$P) {
    $calm = 0; $prev = Get-CpuMs $P
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($calm -lt 3 -and $sw.ElapsedMilliseconds -lt 30000) {
        Start-Sleep -Milliseconds 100
        $now = Get-CpuMs $P
        if ($now - $prev -lt 2) { $calm++ } else { $calm = 0 }
        $prev = $now
    }
    return $calm -ge 3
}

# $Op を n 回(各回の後に interval ms)行い、1 回あたりの CPU ms を返す。$Op にはインデックスを渡す。
# 前後で、前面の窓とキーボードフォーカス($ExpectFocus。0 なら見ない)を確かめる。
# 静穏待ちがタイムアウトしたら警告し、次の Add-Result の flags に quiet_timeout を残す。
# -OnStart は静穏待ちの直後・計測開始の直前に呼ぶ(M-6 のスレッド別 CPU の開始点を揃えるため)。
function Measure-Op($Kx, [int]$N, [int]$IntervalMs, [scriptblock]$Op,
    [IntPtr]$ExpectForeground = $Kx.Main, [IntPtr]$ExpectFocus = $Kx.Editor, [scriptblock]$OnStart = $null) {
    if (-not (Wait-Quiet $Kx.Proc)) { $script:PendingFlags.Add('quiet_timeout'); Write-Warning '静穏待ちがタイムアウトしました(計測前)。' }
    Assert-Foreground $Kx $ExpectForeground
    if ($ExpectFocus -ne 0) { Assert-Focus $Kx $ExpectFocus '計測対象' }
    if ($OnStart) { $null = & $OnStart }
    $t0 = Get-CpuMs $Kx.Proc
    for ($i = 0; $i -lt $N; $i++) {
        $null = & $Op $i
        Start-Sleep -Milliseconds $IntervalMs
    }
    if (-not (Wait-Quiet $Kx.Proc)) { $script:PendingFlags.Add('quiet_timeout'); Write-Warning '静穏待ちがタイムアウトしました(計測後)。' }
    $t1 = Get-CpuMs $Kx.Proc
    Assert-Foreground $Kx $ExpectForeground
    if ($ExpectFocus -ne 0) { Assert-Focus $Kx $ExpectFocus '計測対象' }
    return ($t1 - $t0) / $N
}

function Get-ThreadCpu([Diagnostics.Process]$P) {
    $P.Refresh()
    $map = @{}
    foreach ($t in $P.Threads) {
        try { $map[$t.Id] = @($t.TotalProcessorTime.TotalMilliseconds, $t.UserProcessorTime.TotalMilliseconds) } catch { }
    }
    return $map
}

# =====================================================================
#  シナリオ
# =====================================================================

function Invoke-M1([string]$Exe) {
    Write-Host '== M-1 起動(6 回・1 回目を除く中央値) =='
    $runs = @()
    for ($k = 0; $k -lt 6; $k++) {
        Assert-NoKxEditRunning
        Clear-ProfileForRun $script:ProfileDir $script:HarnessRoot
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $Exe -PassThru
        $script:Launched.Add($p)
        [KxPerfNative]::KillOnHarnessExit($p.Handle)
        while ($true) {
            $p.Refresh()
            if ($p.MainWindowHandle -ne 0 -and [KxPerfNative]::IsWindowVisible($p.MainWindowHandle)) { break }
            if ($sw.ElapsedMilliseconds -gt 15000) { throw 'kxEdit の窓が 15 秒以内に出ません' }
            Start-Sleep -Milliseconds 5
        }
        $show = $sw.Elapsed.TotalMilliseconds
        [void]$p.WaitForInputIdle(10000)
        [void][KxPerfNative]::Ping($p.MainWindowHandle, 10000)
        $inputMs = $sw.Elapsed.TotalMilliseconds
        $rest = 800 - $sw.ElapsedMilliseconds
        if ($rest -gt 0) { Start-Sleep -Milliseconds $rest }
        $p.Refresh()
        $cpu = $p.TotalProcessorTime.TotalMilliseconds
        $ws = $p.WorkingSet64 / 1MB
        [KxPerfNative]::Close($p.MainWindowHandle)
        [void]$p.WaitForExit(10000)
        Stop-Proc $p
        Write-Host ("  run {0}: 表示 {1:F0} ms / 入力受付 {2:F0} ms / CPU@0.8s {3:F0} ms / WS {4:F0} MB" -f ($k + 1), $show, $inputMs, $cpu, $ws)
        if ($k -gt 0) { $runs += [pscustomobject]@{ Show = $show; Input = $inputMs; Cpu = $cpu; Ws = $ws } }
    }
    function Median([double[]]$xs) { $s = $xs | Sort-Object; return $s[[int][math]::Floor($s.Count / 2)] }
    Add-Result 'M-1' '窓の表示まで' '-' 5 (Median $runs.Show) 'ms'
    Add-Result 'M-1' '入力受付まで' '-' 5 (Median $runs.Input) 'ms'
    Add-Result 'M-1' '0.8秒時点のCPU' '-' 5 (Median $runs.Cpu) 'ms'
    Add-Result 'M-1' 'ワーキングセット' '-' 5 (Median $runs.Ws) 'MB'
}

function Invoke-M2([string]$Exe, $Docs) {
    Write-Host '== M-2 キャレット移動・打鍵(CPU ms/回) =='
    foreach ($doc in 'ja10k', 'en10k') {
        $kx = Start-KxEdit $Exe
        try {
            Open-Doc $kx $Docs[$doc]
            # アイドル: 何も送らない周期の費用(キャレットの点滅等)。各操作の値に interval ぶん乗るので、差し引きの目安。
            Add-Result 'M-2' 'アイドル' $doc 100 (Measure-Op $kx 100 100 { }) 'cpu_ms/op'
            Tap $VK.Home @($VK.Ctrl); for ($i = 0; $i -lt 10; $i++) { Tap $VK.Down }
            Add-Result 'M-2' '→←の交互' $doc 100 (Measure-Op $kx 100 100 { param($i) if ($i % 2 -eq 0) { Tap $VK.Right } else { Tap $VK.Left } }) 'cpu_ms/op'
            Add-Result 'M-2' '↓↑の交互' $doc 100 (Measure-Op $kx 100 100 { param($i) if ($i % 2 -eq 0) { Tap $VK.Down } else { Tap $VK.Up } }) 'cpu_ms/op'
            Add-Result 'M-2' 'Shift+→' $doc 40 (Measure-Op $kx 40 100 { Tap $VK.Right @($VK.Shift) }) 'cpu_ms/op'
            Tap $VK.Right
            if (Test-TitleDirty $kx) { throw 'M-2: 打鍵の前から文書が変更済みになっている' }
            Add-Result 'M-2' '文字入力x' $doc 60 (Measure-Op $kx 60 100 { Send-Text 'x' }) 'cpu_ms/op'
            if (-not (Test-TitleDirty $kx)) { throw 'M-2: x を打っても文書が変更済みにならない(打鍵が本文に届いていない)' }
            Add-Result 'M-2' 'BackSpace' $doc 60 (Measure-Op $kx 60 100 { Tap $VK.Back }) 'cpu_ms/op'
            Add-Result 'M-2' 'PageDown/PageUpの交互' $doc 50 (Measure-Op $kx 50 150 { param($i) if ($i % 2 -eq 0) { Tap $VK.PageDown } else { Tap $VK.PageUp } }) 'cpu_ms/op'
            $ed = $kx.Editor
            Add-Result 'M-2' '基準:全面の再描画' $doc 100 (Measure-Op $kx 100 50 { [KxPerfNative]::Redraw($ed) }) 'cpu_ms/op'
            Tap $VK.Home @($VK.Ctrl)
            Add-Result 'M-2' '基準:文書先頭での←' $doc 100 (Measure-Op $kx 100 100 { Tap $VK.Left }) 'cpu_ms/op'
            Add-Result 'M-2' '基準:Shiftの単押し' $doc 100 (Measure-Op $kx 100 100 { Tap $VK.Shift }) 'cpu_ms/op'
        }
        finally { Stop-KxEdit $kx }
    }
}

# UIA で本文の長さ(文字数)を読む(計測区間の外で、入力の効果を確かめるため)。
function Get-DocLength($Kx) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($Kx.Editor)
    $tp = [System.Windows.Automation.TextPattern]$el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    return $tp.DocumentRange.GetText(-1).Length
}

function Invoke-M3([string]$Exe, $Docs) {
    Write-Host '== M-3 新規文書への連続入力(F-6・CPU ms/打鍵) =='
    # クリップボードは上書きし、終了時に空にする(元の内容は戻さない)。戻すと、パスワードマネージャーが
    # 履歴・同期から外すための形式を付けずにパスワードを再投入し、自動消去も無効にしてしまうため。
    $kx = Start-KxEdit $Exe
    try {
        Set-Clipboard -Value $Docs.paste
        $pos = 0
        $pasteBytes = [Text.Encoding]::UTF8.GetByteCount($Docs.paste)
        for ($k = 0; $k -le 9; $k++) {
            if ($k -gt 0) {
                Assert-Foreground $kx
                Assert-Focus $kx $kx.Editor '本文'
                $len0 = Get-DocLength $kx
                Tap $VK.V @($VK.Ctrl)
                [void](Wait-Quiet $kx.Proc)
                $len1 = Get-DocLength $kx
                if ($len1 - $len0 -ne $Docs.paste.Length) { throw "M-3: 貼り付けが効いていない(本文長 $len0 → $len1)" }
                $pos += $pasteBytes
            }
            $len0 = Get-DocLength $kx
            Add-Result 'M-3' "書込位置≈$pos" 'new' 40 (Measure-Op $kx 40 100 { Send-Text 'x' }) 'cpu_ms/op'
            if ((Get-DocLength $kx) - $len0 -ne 40) { throw 'M-3: 打鍵が本文に届いていない' }
            $pos += 40
        }
    }
    finally {
        try { Stop-KxEdit $kx }
        finally {
            try { [Windows.Forms.Clipboard]::Clear() } catch { Write-Warning "クリップボードを空にできませんでした: $_" }
        }
    }
}

function Invoke-M4([string]$Exe, $Docs) {
    Write-Host '== M-4 スクロール(ja10k・CPU ms/回) =='
    $kx = Start-KxEdit $Exe
    try {
        Open-Doc $kx $Docs.ja10k
        if (-not [KxPerfNative]::CursorToVisiblePoint($kx.Editor)) {
            throw 'M-4: エディタの見えている点が見つかりません(最前面の窓に覆われている)。'
        }
        $ed = $kx.Editor
        # ホイールが本当に届いてスクロールすることを確かめる(届かないと「何もしない費用」を測る)。
        $pos0 = [KxPerfNative]::VScrollPos($ed)
        Send-Wheel $ed -120; [void](Wait-Quiet $kx.Proc)
        $pos1 = [KxPerfNative]::VScrollPos($ed)
        Send-Wheel $ed 120; [void](Wait-Quiet $kx.Proc)
        if ($pos0 -lt 0 -or $pos1 -le $pos0) {
            throw "M-4: ホイールでスクロールしません(縦スクロール位置 $pos0 → $pos1)"
        }
        Add-Result 'M-4' 'ホイール下' 'ja10k' 60 (Measure-Op $kx 60 100 { Send-Wheel $ed -120 }) 'cpu_ms/op'
        Add-Result 'M-4' 'ホイール上' 'ja10k' 60 (Measure-Op $kx 60 100 { Send-Wheel $ed 120 }) 'cpu_ms/op'
    }
    finally { Stop-KxEdit $kx }
}

function Invoke-M5([string]$Exe, $Docs) {
    Write-Host '== M-5 検索語の打鍵(CPU ms/実打鍵・×16/14 で補正) =='
    $term = '名前はまだ無い'
    foreach ($doc in 'empty', 'ja10k', 'ja30k') {
        $kx = Start-KxEdit $Exe
        try {
            if ($doc -ne 'empty') { Open-Doc $kx $Docs[$doc] }
            Assert-Foreground $kx
            Tap $VK.F @($VK.Ctrl)
            $dlg = Wait-Dialog $kx
            # ダイアログへ送る間は、メインウィンドウを前面に戻さない(戻すとキーが本文に入る)。
            # ダイアログが出てから入力欄にフォーカスが入るまで少し遅れることがある(最大 2 秒待つ)。
            $sw = [Diagnostics.Stopwatch]::StartNew()
            do {
                $box = [KxPerfNative]::FocusOf($dlg)
                if ($box -ne 0 -and [KxPerfNative]::ClassOf($box) -match 'EDIT') { break }
                Start-Sleep -Milliseconds 50
            } while ($sw.ElapsedMilliseconds -lt 2000)
            if ($box -eq 0 -or [KxPerfNative]::ClassOf($box) -notmatch 'EDIT') {
                throw "M-5: 検索語の入力欄にフォーカスがありません($([KxPerfNative]::ClassOf($box)))"
            }
            # 打鍵が入力欄に届くことを確かめる(計測区間の外)。
            Send-Text $term; [void](Wait-Quiet $kx.Proc)
            $typed = [KxPerfNative]::ControlTextOf($box)
            for ($i = 0; $i -lt $term.Length; $i++) { Tap $VK.Back }
            if ($typed -ne $term) { throw "M-5: 検索語が入力欄に入らない('$typed')" }
            $v = Measure-Op $kx 64 200 {
                param($i)
                $k = $i % 16
                if ($k -lt 7) { Send-Text ($term.Substring($k, 1)) }
                elseif ($k -lt 14) { Tap $VK.Back }
            } -ExpectForeground $dlg -ExpectFocus $box
            Add-Result 'M-5' '検索語の打鍵' $doc 56 ($v * 16 / 14) 'cpu_ms/op'
        }
        finally { Stop-KxEdit $kx }
    }
}

function Invoke-M6([string]$Exe, $Docs) {
    Write-Host '== M-6 タブ切替(Ctrl+Tab・CPU ms/回) =='
    foreach ($cond in '空の新規タブ4枚', '3ファイル+空', '3ファイル未保存+空') {
        $kx = Start-KxEdit $Exe
        try {
            if ($cond -eq '空の新規タブ4枚') {
                for ($i = 0; $i -lt 3; $i++) { Assert-Foreground $kx; Tap $VK.N @($VK.Ctrl); [void](Wait-Quiet $kx.Proc) }
            }
            else {
                foreach ($d in 'ja10k', 'en10k', 'ja30k') { Open-Doc $kx $Docs[$d] }
                if ($cond -eq '3ファイル未保存+空') {
                    # 各ファイルのタブで x を 1 文字打って未保存にする(4 タブを一巡)。
                    for ($i = 0; $i -lt 4; $i++) {
                        $title = [KxPerfNative]::TextOf($kx.Main)
                        if ($title -match '^(ja10k|en10k|ja30k)\.txt') { Send-Text 'x'; [void](Wait-Quiet $kx.Proc) }
                        Tap $VK.Tab @($VK.Ctrl); [void](Wait-Quiet $kx.Proc)
                    }
                    # もう一巡して、変更済みのタブが 3 枚あることを確かめる。
                    $dirty = 0
                    for ($i = 0; $i -lt 4; $i++) {
                        if (Test-TitleDirty $kx) { $dirty++ }
                        Tap $VK.Tab @($VK.Ctrl); [void](Wait-Quiet $kx.Proc)
                    }
                    if ($dirty -ne 3) { throw "M-6: 未保存のタブが 3 枚にならない($dirty 枚)" }
                }
            }
            # スレッド別の CPU は、Measure-Op の静穏待ちの直後(= プロセス全体と同じ開始点)で採る。
            $uiTid = [int][KxPerfNative]::ThreadOf($kx.Main)
            $script:M6Before = $null
            $title0 = [KxPerfNative]::TextOf($kx.Main)
            # タブごとにエディタの HWND が変わるので、フォーカスの照合はしない(前面の窓だけ見る)。
            $v = Measure-Op $kx 40 250 { Tap $VK.Tab @($VK.Ctrl) } -ExpectFocus 0 -OnStart { $script:M6Before = Get-ThreadCpu $kx.Proc }
            $after = Get-ThreadCpu $kx.Proc
            # 40 回(4 タブの倍数)で一巡して元のタブに戻る=Ctrl+Tab が効いていれば題名は元に戻る。
            if ([KxPerfNative]::TextOf($kx.Main) -ne $title0) { throw 'M-6: Ctrl+Tab の後に元のタブへ戻らない' }
            Add-Result 'M-6' $cond '-' 40 $v 'cpu_ms/op'
            $rows = foreach ($id in $after.Keys) {
                if ($script:M6Before.ContainsKey($id)) {
                    [pscustomobject]@{ Id = $id; Total = ($after[$id][0] - $script:M6Before[$id][0]) / 40; User = ($after[$id][1] - $script:M6Before[$id][1]) / 40 }
                }
            }
            $ui = @($rows | Where-Object Id -eq $uiTid)
            $other = @($rows | Where-Object Id -ne $uiTid | Sort-Object Total -Descending | Select-Object -First 1)
            foreach ($pair in @(@('UIスレッド', $ui), @('その他で最大のスレッド', $other))) {
                $label, $r = $pair
                if ($r.Count -gt 0) {
                    Add-Result 'M-6' "$cond/$label/合計" '-' 40 $r[0].Total 'cpu_ms/op'
                    Add-Result 'M-6' "$cond/$label/ユーザー" '-' 40 $r[0].User 'cpu_ms/op'
                }
            }
        }
        finally { Stop-KxEdit $kx }
    }
}

function Invoke-M7([string]$Exe, $Docs) {
    Write-Host '== M-7 UIA GetBoundingRectangles(ja10k・先頭から・所要 ms) =='
    $kx = Start-KxEdit $Exe
    try {
        Open-Doc $kx $Docs.ja10k
        Tap $VK.Home @($VK.Ctrl); [void](Wait-Quiet $kx.Proc)
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($kx.Editor)
        $tp = [System.Windows.Automation.TextPattern]$el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $EP = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
        $UNIT = [System.Windows.Automation.Text.TextUnit]
        $visible = $tp.DocumentRange.GetBoundingRectangles().Count
        if ($visible -lt 1) { throw 'M-7: 全文の範囲で矩形が返らない' }
        foreach ($case in @(@('1行', 1, 10), @('40行', 40, 10), @('1000行', 1000, 3), @('全文', 0, 3))) {
            $label, $lines, $reps = $case
            if ($lines -eq 0) { $range = $tp.DocumentRange }
            else {
                $range = $tp.DocumentRange.Clone()
                $range.MoveEndpointByRange($EP::End, $range, $EP::Start)
                $range.ExpandToEnclosingUnit($UNIT::Line)
                if ($lines -gt 1) { [void]$range.MoveEndpointByUnit($EP::End, $UNIT::Line, $lines - 1) }
            }
            $times = @(); $count = 0
            for ($r = 0; $r -lt $reps; $r++) {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $rects = $range.GetBoundingRectangles()
                $times += $sw.Elapsed.TotalMilliseconds
                $count = $rects.Count
            }
            # 範囲の行数ぶん。ただし可視域で頭打ち(Smoke S8 と同じ期待値)。
            $expected = if ($lines -eq 0) { $visible } else { [math]::Min($lines, $visible) }
            if ($count -ne $expected) { throw "M-7: $label の矩形数 $count(期待 $expected)" }
            $s = @($times | Sort-Object)
            $median = if ($s.Count % 2 -eq 1) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
            Add-Result 'M-7' "$label(矩形$count)" 'ja10k' $reps $median 'ms'
        }
    }
    finally { Stop-KxEdit $kx }
}

# =====================================================================
#  実行
# =====================================================================

$exe = Join-Path (Resolve-Path -LiteralPath $PublishDir).ProviderPath 'kxEdit.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "kxEdit.exe がありません: $exe" }
if (-not $OutCsv) { $OutCsv = Join-Path $script:HarnessRoot ("results-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date)) }
# 相対パスは開始時に絶対化する(finally の中で親フォルダーを解決できずに結果を失わないため)。
$OutCsv = [IO.Path]::GetFullPath($OutCsv, (Get-Location).ProviderPath)
if (Test-Path -LiteralPath $OutCsv) { throw "結果の CSV が既にあります(上書きしません): $OutCsv" }
$WorkDir = [IO.Path]::GetFullPath($WorkDir, (Get-Location).ProviderPath).TrimEnd('\')
foreach ($guarded in $script:ProfileDir, $script:HarnessRoot) {
    if ($WorkDir -eq $guarded -or $WorkDir.StartsWith("$guarded\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "-WorkDir を $guarded の中に置かないでください(退避・消去に巻き込まれます): $WorkDir"
    }
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$reasons = Test-HarnessPreconditions $script:ProfileDir $script:HarnessRoot $script:ProcessName $script:KxMutexName
if ($reasons.Count -gt 0) {
    Write-Host '計測を開始できません(何も変更していません):' -ForegroundColor Yellow
    foreach ($r in $reasons) { Write-Host "- $r" }
    exit 2
}

$docs = Initialize-Docs $WorkDir
# NVDA(UIA クライアント)が動いていると、UIA イベントとフォーカス変更の費用が乗る(調査記録 §9.1 は NVDA なし)。
$nvda = [bool](Get-Process -Name nvda -ErrorAction SilentlyContinue)
if ($nvda) { Write-Host '[注意] NVDA が起動しています。調査記録 §9 の値(NVDA なし)とは条件が違います。' -ForegroundColor Yellow }
Add-Result 'env' 'NVDA起動中' '-' 1 ([int]$nvda) 'bool'
# 前後比較で条件を取り違えないための記録(値は value 列ではなく condition 列に入れる)。
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.Substring(0, 12)
$dllPath = Join-Path (Split-Path -Parent $exe) 'kxEdit.dll'
$ver = if (Test-Path -LiteralPath $dllPath) { (Get-Item -LiteralPath $dllPath).VersionInfo.ProductVersion } else { '?' }
$scr = [Windows.Forms.Screen]::PrimaryScreen.Bounds
Add-Result 'env' "kxEdit=$ver sha256(exe)=$exeHash" '-' 1 0 'info'
Add-Result 'env' "screen=$($scr.Width)x$($scr.Height)" '-' 1 0 'info'
Add-Result 'env' "scenarios=$($Scenario -join ',')" '-' 1 0 'info'
Write-Host @"
計測を始めます。終わるまで(全シナリオで十数分)画面・キーボード・マウスに触らないでください。
計測中に kxEdit を起動しないでください(計測中の窓に入り、打った内容は失われます)。
プロフィールを退避します: $($script:ProfileDir)
"@
# 退避は try の外。失敗したら Save-ProfileStash が自分で後始末する(プロフィールは無傷)。
# 開始前の検査から文書の生成までの間に kxEdit が起動されていないことを、退避の直前にもう一度確かめる。
Assert-NoKxEditRunning
Save-ProfileStash $script:ProfileDir $script:HarnessRoot
$exitCode = 0
try {
    foreach ($s in $Scenario) {
        switch ($s) {
            'M-1' { Invoke-M1 $exe }
            'M-2' { Invoke-M2 $exe $docs }
            'M-3' { Invoke-M3 $exe $docs }
            'M-4' { Invoke-M4 $exe $docs }
            'M-5' { Invoke-M5 $exe $docs }
            'M-6' { Invoke-M6 $exe $docs }
            'M-7' { Invoke-M7 $exe $docs }
        }
    }
}
catch {
    Write-Host "計測を中止しました: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    $exitCode = 1
}
finally {
    $restorable = $true
    try { Stop-Launched } catch { Write-Host "$_" -ForegroundColor Red; $restorable = $false }
    $mk = $null
    try { $mk = Read-Marker $script:HarnessRoot } catch { $mk = $null }
    $neverCleared = $null -ne $mk -and $mk.State -eq 'stashed'
    if ($neverCleared) {
        # プロフィールを一度も空にしていない。kxEdit が動いていても、プロフィールに触れずに退避だけ片づけられる。
        $restorable = $true
    }
    elseif ($restorable -and (Test-KxEditRunning $script:ProcessName $script:KxMutexName)) {
        Write-Host 'kxEdit が動いているので、プロフィールの復元を見送ります。' -ForegroundColor Red
        $restorable = $false
    }
    if ($restorable) {
        try {
            [void](Restore-ProfileStash $script:ProfileDir $script:HarnessRoot)
            Write-Host "プロフィールを復元しました(照合済み): $($script:ProfileDir)"
        }
        catch {
            Write-Host "プロフィールの復元に失敗しました: $_" -ForegroundColor Red
            $restorable = $false
        }
    }
    if (-not $restorable) {
        $m = $null
        try { $m = Read-Marker $script:HarnessRoot } catch { $m = $null }
        Write-Host (Get-RestoreGuide $script:ProfileDir $script:HarnessRoot $m)
        $exitCode = 3
    }
    # 中止したときの途中までの結果を、完走した結果と取り違えないように記録する。
    Add-Result 'env' "status=$(if ($exitCode -eq 0) { 'completed' } else { "aborted(exit $exitCode)" })" '-' 1 $exitCode 'info'
    if ($script:Results.Count -gt 0) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutCsv) | Out-Null
        $script:Results | Export-Csv -LiteralPath $OutCsv -NoTypeInformation -Encoding utf8 -NoClobber
        Write-Host "結果: $OutCsv"
    }
}
exit $exitCode