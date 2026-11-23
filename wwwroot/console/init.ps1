# VoidCode console init — handed to PowerShell at spawn (-NoExit -File), so the
# console is styled before you ever see it. Emits OSC 133 / OSC 633 shell-integration
# markers so VoidCode can track command blocks, exit codes, durations, copy and rerun.

$e = [char]27
$bel = [char]7
$vcAccent = "${e}[38;2;143;211;193m"
$vcDim = "${e}[38;2;141;151;165m"
$vcGood = "${e}[38;2;107;226;179m"
$vcBad = "${e}[38;2;242;145;138m"
$vcReset = "${e}[0m"

$global:__vcStart = $null

function global:prompt {
    $ok = $?
    $code = $LASTEXITCODE
    $exitVal = if ($ok -and ($code -eq 0 -or $null -eq $code)) { 0 } elseif ($null -ne $code -and $code -ne 0) { [int]$code } else { 1 }

    # OSC 133;D closes the previous command block with its exit code
    if ($global:__vcStart) {
        [Console]::Write("${e}]133;D;${exitVal}${bel}")
        $dur = [math]::Round(((Get-Date) - $global:__vcStart).TotalSeconds, 2)
        $global:__vcStart = $null

        if ($exitVal -eq 0) {
            "$vcGood" + "ok" + "$vcReset $vcDim$($dur)s$vcReset"
        } else {
            "$vcBad" + "err" + "$vcReset $vcDim$exitVal in $($dur)s$vcReset"
        }

        $width = 56
        try {
            $consoleWidth = $Host.UI.RawUI.WindowSize.Width
            if ($consoleWidth -gt 24 -and $consoleWidth -lt 240) { $width = $consoleWidth - 2 }
        } catch { }
        $vcDim + ('-' * $width) + $vcReset
    }

    $path = (Get-Location).Path
    $leaf = Split-Path -Leaf $path
    if (-not $leaf) { $leaf = $path }
    ""
    "${e}]133;A${bel}$vcAccent$leaf$vcReset $vcDim$path$vcReset"
    "$vcAccent>$vcReset ${e}]133;B${bel}"
}

# Stamp command line + execution start (OSC 633;E + OSC 133;C) on Enter
try {
    Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
        $line = $null
        $cursor = $null
        [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
        $global:__vcStart = Get-Date
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $safe = $line -replace "`r|`n", ' '
            [Console]::Write("${e}]633;E;${safe}${bel}")
        }
        [Console]::Write("${e}]133;C${bel}")
        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
    }
} catch { }
