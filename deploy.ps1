# Installs a build of Claw Control to C:\Program Files\ClawControl and starts it.
# Must run elevated. A running copy is asked to quit first, so it hands the MSI button back cleanly.
#
# Claw Control is always started through its scheduled task, never as a child of this script:
# anything started from inside a packaged (MSIX) app, such as some editors and terminals,
# belongs to that package, has its AppData redirected into it, and is closed with that app.
param(
    [Parameter(Mandatory)] [string] $Source,
    # Turn the MSI Center M button takeover back on in the saved settings.
    [switch] $EnableTakeover
)
$ErrorActionPreference = 'Stop'
$taskName = 'Claw Control'
$target = Join-Path $env:ProgramFiles 'ClawControl'
$exe = Join-Path $target 'ClawControl.exe'

if (Get-Process ClawControl -ErrorAction SilentlyContinue) {
    Start-Process $exe -ArgumentList '--quit' -Wait
    Get-Process ClawControl -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Get-Process ClawControl -ErrorAction SilentlyContinue | Stop-Process -Force
}
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

robocopy $Source $target /MIR /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy exit $LASTEXITCODE)" }

if ($EnableTakeover) {
    $settingsFile = Join-Path $env:LOCALAPPDATA 'ClawControl\settings.json'
    if (Test-Path $settingsFile) {
        $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $s.TakeOverMsiButton = $true
        $s | ConvertTo-Json -Depth 5 | Set-Content $settingsFile -Encoding utf8
    }
}

# Starts at login, and again 10 s after it crashes: Windows logs every crash as an
# "Application Error" (event 1000) naming the exe, and the task listens for that. Nothing runs
# in between (the old watchdog started a copy every minute just to check). Restart-on-failure
# only covers a start that fails. "--background" exits at once if it already runs, or when the
# user chose Quit this boot.
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
    -Description 'Starts Claw Control at login and restarts it if it ever crashes. It owns the MSI Center M button and the TDP controls.' `
    -Action (New-ScheduledTaskAction -Execute $exe -Argument '--background') `
    -Trigger @($atLogon, $crashTrigger) `
    -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest) `
    -Settings $settings | Out-Null

Start-ScheduledTask -TaskName $taskName
