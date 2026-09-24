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
    if ($null -eq $marker -or $marker.State -ne 'stashed') {
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
    Assert-StashIntact $ProfileDir $Root | Out-Null
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
        Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
        return $null
    }
    $marker = Assert-StashIntact $ProfileDir $Root
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
    Remove-Item -LiteralPath (Join-Path $Root 'stash') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Get-MarkerPath $Root) -Force
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

        # 4. 退避が壊れていたら、プロフィールを空にしないし復元もしない(退避を残す)
        Save-ProfileStash $prof $root
        Set-Content -LiteralPath (Join-Path (Get-StashDir $root) 'settings.json') -Value 'tampered' -Encoding utf8
        Check ((Throws { Clear-ProfileForRun $prof $root }) -and (Test-Path -LiteralPath (Join-Path $prof 'settings.json'))) '退避が壊れていたらプロフィールを空にしない'
        Check ((Throws { Restore-ProfileStash $prof $root }) -and (Test-Path -LiteralPath (Get-MarkerPath $root))) '退避が壊れていたら復元せず目印を残す'
        $diff = [System.Collections.Generic.List[string]]::new()
        Check (Compare-Manifest $orig (Get-Manifest $prof) $diff) '復元を拒否した後もプロフィールは無傷'
        Remove-Item -LiteralPath $root -Recurse -Force

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
    $displaced = Restore-ProfileStash $script:ProfileDir $script:HarnessRoot -Displace
    Write-Host "復元しました(照合済み): $($script:ProfileDir)"
    if ($displaced) {
        Write-Host "復元前のプロフィールの中身は次に残しています。不要なら削除してください: $displaced"
    }
    exit 0
}

# (計測本体は次の commit で追加する)
