# ローカルパスの混入を検出する健全性チェック。
# 対象は Windows / Git Bash / 混合スラッシュを網羅する 2 系統(いずれも
# case-insensitive):
#   - Windows ユーザーホーム下の任意 username を含む絶対パス -> %USERPROFILE%\ に置換
#   - 本リポジトリ (kxEdit / 旧名 yEdit) の任意 drive 絶対パス           -> <repo>          に置換
#
# 具体的な variant 例(いずれも検出対象):
#   X:\Users\<name>\   X:/Users/<name>/   /x/Users/<name>/   (X は任意 drive)
#   X:\src\kxEdit       X:/src/kxEdit       /x/src/kxEdit       (X は任意 drive)
#   X:\src\yEdit       X:/src/yEdit       /x/src/yEdit       (旧名・改名途上の作業ディレクトリも検出)
#
# このスクリプト自身は regex に構造 literal (Users, kxEdit / yEdit 等) を含むため、
# 走査対象から明示除外している($selfPath 参照)。
#
# 用途:
#   pre-commit (Husky) : -Staged で `git diff --cached` 対象のみ検査
#   CI                 : 引数なしで tracked files 全体を検査
#
# text 拡張子ホワイトリストでフィルタしてバイナリは触らない。
# 違反があれば file:line: content を列挙して exit 1。
[CmdletBinding()]
param(
    [switch]$Staged
)
$ErrorActionPreference = 'Stop'

# drive prefix (C:, D:, /c, /d ... 任意の英字) + separator (\ or /) + 対象コンポーネント。
# username は [^\\/\s]+ で generic 化(区切り/空白以外の 1 文字以上)。
# kxEdit / yEdit は public 情報 (=repo 名・旧 repo 名) なので literal のまま。
# 改名後もローカル作業ディレクトリが旧名 (\src\yEdit) のままでありうるため、kxEdit / yEdit の両方を検出する。
$patterns = @(
    '(?i)([a-z]:|/[a-z])[\\/]Users[\\/][^\\/\s]+',
    '(?i)([a-z]:|/[a-z])[\\/]src[\\/](yEdit|kxEdit)\b'
)

$selfPath = 'tools/check-no-local-paths.ps1'

$textExtensions = @(
    '.md', '.txt', '.cs', '.csproj', '.props', '.targets', '.sln',
    '.json', '.yml', '.yaml', '.xml', '.config',
    '.ps1', '.psm1', '.psd1', '.sh', '.bat', '.cmd',
    '.iss', '.editorconfig', '.gitattributes', '.gitignore',
    '.resx', '.settings'
)

if ($Staged) {
    $files = git -c core.quotepath=false diff --cached --name-only --diff-filter=ACM | Where-Object { $_ -and $_.Trim() }
} else {
    $files = git -c core.quotepath=false ls-files
}

$violations = @()
foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
    # このスクリプト自身は構造 literal を含む。走査対象外。
    if (($f -replace '\\', '/') -eq $selfPath) { continue }
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($ext -and ($textExtensions -notcontains $ext)) { continue }
    try {
        $lines = Get-Content -LiteralPath $f -ErrorAction Stop
    } catch {
        continue
    }
    if ($null -eq $lines) { continue }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $patterns) {
            if ($lines[$i] -match $p) {
                $violations += ('{0}:{1}: {2}' -f $f, ($i + 1), $lines[$i].Trim())
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Output ''
    Write-Output '[no-local-paths] ローカルパスが検出されました。プレースホルダに置換してください:'
    Write-Output '  Windows/Git Bash 形式のユーザーホーム系 (X:\Users\<name>\ / X:/Users/<name>/ / /x/Users/<name>/ 等・X は任意 drive) -> %USERPROFILE%\'
    Write-Output '  kxEdit / yEdit リポジトリの絶対パス (X:\src\kxEdit / X:/src/kxEdit / /x/src/kxEdit 等・X は任意 drive・旧名 yEdit も同様)                        -> <repo>'
    Write-Output ''
    $violations | ForEach-Object { Write-Output ('  ' + $_) }
    Write-Output ''
    exit 1
}

Write-Output '[no-local-paths] OK'
