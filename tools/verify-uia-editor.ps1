# P5 Task 14: UIA client verification for kxEdit.Editor.Smoke (--uia mode).
# ASCII only for the framing lines to avoid PS 5.1 encoding issues (BOMless UTF-8).
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\kxEdit.Editor.Smoke\bin\Release\net9.0-windows\kxEdit.Editor.Smoke.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Start-Process -FilePath $Exe -ArgumentList "--uia" -PassThru
Start-Sleep -Seconds 2

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$TP   = [System.Windows.Automation.TextPattern]
$EP   = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
$UNIT = [System.Windows.Automation.Text.TextUnit]

function Write-Check($ok, $msg) {
    if ($ok) { Write-Output "[PASS] $msg" } else { Write-Output "[FAIL] $msg" }
}

$exitCode = 0
try {
    $root = $AE::RootElement
    # AutomationId="editor" は EditorControl の IUiaTextHost.AutomationId(P5 Task 5)
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "editor")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    Write-Check ($null -ne $doc) "Found EditorControl element (AutomationId=editor)"
    if ($null -eq $doc) { $exitCode = 1; return }

    # ControlType=Document / Name="本文"
    $ctName = $doc.Current.ControlType.ProgrammaticName
    Write-Output ("    ControlType = {0}" -f $ctName)
    Write-Check ($ctName -match "Document") "ControlType is Document"

    $name = $doc.Current.Name
    Write-Output ("    Name = {0}" -f $name)

    # TextPattern
    $hasText = $doc.GetCurrentPattern($TP::Pattern)
    Write-Check ($null -ne $hasText) "Exposes TextPattern"
    if ($null -eq $hasText) { $exitCode = 1; return }
    $tp = [System.Windows.Automation.TextPattern]$hasText

    # DocumentRange - smoke 起動時は空 buffer なので 0 でも OK。ここでは length を報告するのみ。
    $text = $tp.DocumentRange.GetText(-1)
    Write-Output ("    DocumentRange length = {0}" -f $text.Length)

    # GetSelection
    $sel0 = $tp.GetSelection()
    Write-Check ($sel0.Length -ge 1) "GetSelection returns a range"

    # RangeFromPoint(左上あたり) が例外なく返る
    try {
        $rect = $doc.Current.BoundingRectangle
        $pt = New-Object System.Windows.Point ($rect.Left + 5), ($rect.Top + 5)
        $rp = $tp.RangeFromPoint($pt)
        Write-Check ($null -ne $rp) "RangeFromPoint returns a range near top-left"
    } catch {
        Write-Check $false ("RangeFromPoint threw: {0}" -f $_.Exception.Message)
        $exitCode = 1
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
exit $exitCode
