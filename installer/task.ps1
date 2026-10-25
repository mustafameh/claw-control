# Registers (or removes) the "Claw Control" scheduled task. Used by the setup program, the zip's
# install / uninstall scripts and development installs. Must run elevated.
#
# The task starts Claw Control at login with admin rights (no prompt each time), and again 10 s
# after it crashes: Windows logs every crash as an "Application Error" (event 1000) naming the
# exe, and the task listens for that, so nothing runs in between. Task Scheduler's own
# restart-on-failure only covers a start that fails.
#
# Claw Control is always started through this task, never as a child of an installer or script:
# anything started from inside a packaged (MSIX) app, such as some editors and terminals,
# belongs to that package, has its AppData redirected into it, and is closed with that app.
param(
    # Full path of ClawControl.exe. Registers the task and starts it.
    [string] $Exe,
    # Quits Claw Control (it hands the MSI button back to MSI) and removes the task.
    [switch] $Remove,
    # Registers the task without starting Claw Control.
    [switch] $NoStart
)
$ErrorActionPreference = 'Stop'
$taskName = 'Claw Control'

function Stop-ClawControl {
    # The quit signal makes a running copy hand everything back and exit; force only if it hangs.
    $running = Get-Process ClawControl -ErrorAction SilentlyContinue
    if (-not $running) { return }
    try {
        [Threading.EventWaitHandle]::OpenExisting('Local\ClawControl.Quit').Set() | Out-Null
    } catch { }
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Get-Process ClawControl -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Remove) {
    Stop-ClawControl
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    return
}

if (-not $Exe -or -not (Test-Path $Exe)) { throw "ClawControl.exe not found: '$Exe'" }
Stop-ClawControl

$user = "$env:USERDOMAIN\$env:USERNAME"
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
$crashTrigger = New-CimInstance -ClientOnly -CimClass (Get-CimClass -Namespace Root/Microsoft/Windows/TaskScheduler -ClassName MSFT_TaskEventTrigger)
$crashTrigger.Enabled = $true
$crashTrigger.Delay = 'PT10S'   # lets Windows finish with the crashed copy first
$crashTrigger.Subscription = '<QueryList><Query Id="0" Path="Application"><Select Path="Application">' +
    "*[System[Provider[@Name='Application Error'] and EventID=1000]] and *[EventData[Data='ClawControl.exe']]" +
    '</Select></Query></QueryList>'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$settings.Priority = 3   # above normal, so the panel stays responsive while a game is running
Register-ScheduledTask -TaskName $taskName -Force `
    -Description 'Starts Claw Control at login and restarts it if it ever crashes. It owns the MSI Center M button and the power limits.' `
    -Action (New-ScheduledTaskAction -Execute $Exe -Argument '--background') `
    -Trigger @($atLogon, $crashTrigger) `
    -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest) `
    -Settings $settings | Out-Null

if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }
