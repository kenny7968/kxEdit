# Simulate SR word-navigation call patterns against EditorSmoke (--uia).
# Verifies the provider's TextUnit.Word behavior (Expand/Move span/GetSelection/MoveEndpointByUnit).
# Background: NVDA relies on TextUnit.Word for word navigation.
# P6 Task 17: v1 用 ProbeExe 分岐を撤去。EditorSmokeExe(v2)固定。
param(
    [string]$Exe = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not $Exe) { $Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\kxEdit.Editor.Smoke\bin\Release\net9.0-windows\kxEdit.Editor.Smoke.exe') }
$automationId = 'editor'
# smoke には --uia + ファイルパスを渡す(word-sim が期待する "ABC abc 123" を含むテキストを作る)
$tmp = Join-Path $env:TEMP ("kxedit-word-sim-{0}.txt" -f ([guid]::NewGuid()))
[System.IO.File]::WriteAllText($tmp, "prelude ABC abc 123 tail`nsecond line`n", [System.Text.UTF8Encoding]::new($false))
$extraArgs = @("--uia", $tmp)

$proc = Start-Process -FilePath $Exe -ArgumentList $extraArgs -PassThru
Start-Sleep -Seconds 2

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$TP   = [System.Windows.Automation.TextPattern]
$EP   = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
$UNIT = [System.Windows.Automation.Text.TextUnit]

function Write-Check($ok, $msg) {
    if ($ok) { Write-Output "[PASS] $msg" } else { Write-Output "[FAIL] $msg" }
}

try {
    $root = $AE::RootElement
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $automationId)
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    if ($null -eq $doc) { Write-Output "[FAIL] element not found (AutomationId=$automationId)"; exit 1 }
    $tp  = [System.Windows.Automation.TextPattern]$doc.GetCurrentPattern($TP::Pattern)

    $full = $tp.DocumentRange.GetText(-1)
    $lineIdx = $full.IndexOf("ABC abc 123")
    Write-Output ("line offset of 'ABC abc 123' = {0}" -f $lineIdx)
    $abcPos = $lineIdx + 4   # start of 'abc'
    $numPos = $lineIdx + 8   # start of '123'

    function New-CollapsedRange([int]$pos) {
        $r = $tp.DocumentRange.Clone()
        $r.MoveEndpointByRange($EP::End, $r, $EP::Start)  # collapse to [0,0]
        if ($pos -gt 0) { [void]$r.Move($UNIT::Character, $pos) }
        return $r
    }

    # Case 1: Expand(Word) at start of 'abc' -> whole word
    $r1 = New-CollapsedRange $abcPos
    $r1.ExpandToEnclosingUnit($UNIT::Word)
    $t1 = $r1.GetText(-1)
    Write-Check ($t1 -eq "abc") ("Case1 Expand(Word)@abc -> '{0}' (expect 'abc')" -f $t1)

    # Case 2: Expand(Character) at same position -> single char (what a char-reader would say)
    $r2 = New-CollapsedRange $abcPos
    $r2.ExpandToEnclosingUnit($UNIT::Character)
    $t2 = $r2.GetText(-1)
    Write-Check ($t2 -eq "a") ("Case2 Expand(Char)@abc -> '{0}' (expect 'a')" -f $t2)

    # Case 3: Word span preservation on Move(Word,1): 'ABC' -> 'abc'
    $r3 = New-CollapsedRange $lineIdx
    $r3.ExpandToEnclosingUnit($UNIT::Word)
    $t3a = $r3.GetText(-1)
    [void]$r3.Move($UNIT::Word, 1)
    $t3b = $r3.GetText(-1)
    Write-Check (($t3a -eq "ABC") -and ($t3b -eq "abc")) ("Case3 Expand(Word)='{0}' then Move(Word,1)='{1}' (expect 'ABC' then 'abc')" -f $t3a, $t3b)

    # Case 4: caret route: Select() collapsed at 'abc', then GetSelection + Expand(Word)
    $r4 = New-CollapsedRange $abcPos
    $r4.Select()
    Start-Sleep -Milliseconds 200
    $sel = $tp.GetSelection()[0]
    $selLen = $sel.GetText(-1).Length
    $sel.ExpandToEnclosingUnit($UNIT::Word)
    $t4 = $sel.GetText(-1)
    Write-Check (($selLen -eq 0) -and ($t4 -eq "abc")) ("Case4 GetSelection(len={0})+Expand(Word) -> '{1}' (expect len=0 then 'abc')" -f $selLen, $t4)

    # Case 5: degenerate + MoveEndpointByUnit(End, Word, 1) -> word incl. trailing space
    $r5 = New-CollapsedRange $abcPos
    [void]$r5.MoveEndpointByUnit($EP::End, $UNIT::Word, 1)
    $t5 = $r5.GetText(-1)
    Write-Check (($t5 -eq "abc ") -or ($t5 -eq "abc")) ("Case5 MoveEndpointByUnit(End,Word,1)@abc -> '{0}' (expect 'abc ' or 'abc')" -f $t5)

    # Case 6: same checks at '123'
    $r6 = New-CollapsedRange $numPos
    $r6.ExpandToEnclosingUnit($UNIT::Word)
    $t6 = $r6.GetText(-1)
    Write-Check ($t6 -eq "123") ("Case6 Expand(Word)@123 -> '{0}' (expect '123')" -f $t6)
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    if ($tmp -and (Test-Path $tmp)) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}
