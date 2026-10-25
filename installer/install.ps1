# Installs Claw Control from the zip package (or a development build) to
# C:\Program Files\ClawControl and starts it. The setup .exe does the same with its own checks.
#
#   Install.cmd                          from the unpacked zip (asks for admin rights)
#   installer\install.ps1 -Source <dir> -Yes   development install of a build folder
param(
    # Folder with ClawControl.exe and its files. Default: the zip's "app" folder.
    [string] $Source = (Join-Path $PSScriptRoot '..\app'),
    # Don't ask anything (development installs). A device other than the Claw A8 is still refused.
    [switch] $Yes
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'ClawControl'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Ask for admin rights by starting this script again elevated.
if (-not (Test-Admin)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$((Resolve-Path $Source).Path)`"")
    if ($Yes) { $arguments += '-Yes' }
    Start-Process powershell -Verb RunAs -ArgumentList $arguments
    return
}

function Finish([int] $code) {
    if (-not $Yes) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}
function Stop-Install([string] $message) {
    Write-Host "`n$message`n`nNothing was installed." -ForegroundColor Red
    Finish 1
}
function Confirm-Continue([string] $message) {
    Write-Host "`n$message" -ForegroundColor Yellow
    if ($Yes) { return }
    if ((Read-Host 'Install anyway? (y/n)') -notmatch '^(y|yes)$') { Stop-Install 'Cancelled.' }
}

Write-Host 'Claw Control setup' -ForegroundColor Cyan

# 1. Made for, and only tested on, the MSI Claw A8.
$bios = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.BaseBoardProduct -ne 'MS-1T8K') {
    Stop-Install ("Claw Control is made only for the MSI Claw A8 (board MS-1T8K).`n" +
        "This device is $($bios.SystemManufacturer) $($bios.SystemProductName) ($($bios.BaseBoardProduct)).`n" +
        'It writes the Claw A8''s firmware and controller settings, which could be wrong for any other device.')
}

# 2. Windows 10 version 2004 (build 19041) or newer.
if ([Environment]::OSVersion.Version.Build -lt 19041) {
    Stop-Install 'Claw Control needs Windows 10 version 2004 or newer (Windows 11 recommended).'
}

# 3. MSI Center M: controller modes and the MSI button work together with it.
$msiCenter = Join-Path ${env:ProgramFiles(x86)} 'MSI\MSI Center M\MSI_Center_M_Server.exe'
if (-not (Test-Path $msiCenter)) {
    Confirm-Continue ("MSI Center M isn't installed. Claw Control runs alongside it: it switches the controller`n" +
        "to gamepad mode through MSI Center M while the panel is open, and takes over its button.`n" +
        'Install it from MSI''s support page for the Claw A8 (or the Microsoft Store), then run this again.')
}

# 4. AMD graphics driver (for the Graphics tab).
if (-not (Test-Path (Join-Path $env:windir 'System32\amdadlx64.dll'))) {
    Confirm-Continue ("The AMD graphics driver wasn't found, so the Graphics tab's AMD settings won't work.`n" +
        'Install the graphics driver from MSI''s support page for the Claw A8, then run this again.')
}

$app = (Resolve-Path $Source).Path
if (-not (Test-Path (Join-Path $app 'ClawControl.exe'))) { Stop-Install "ClawControl.exe not found in $app." }

Write-Host "`nInstalling to $target ..."
& (Join-Path $PSScriptRoot 'task.ps1') -Remove   # quits a running copy first
robocopy $app $target /MIR /NJH /NJS /NFL /NDL /NP /XF *.pdb | Out-Null
if ($LASTEXITCODE -ge 8) { Stop-Install "Copying failed (robocopy exit code $LASTEXITCODE)." }

# Keep the uninstall scripts with the program, so it can be removed without the zip.
$scripts = Join-Path $target 'scripts'
New-Item -ItemType Directory -Force $scripts | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'task.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $scripts -Force
$uninstallCmd = @((Join-Path $PSScriptRoot 'Uninstall.cmd'), (Join-Path $PSScriptRoot '..\Uninstall.cmd')) |
    Where-Object { Test-Path $_ } | Select-Object -First 1   # installer\ (development) or the zip's top folder
if ($uninstallCmd) { Copy-Item $uninstallCmd (Join-Path $target 'Uninstall Claw Control.cmd') -Force }

& (Join-Path $PSScriptRoot 'task.ps1') -Exe (Join-Path $target 'ClawControl.exe')
Write-Host "`nDone. Claw Control is running and starts with Windows. Press the MSI Center M button to open it." -ForegroundColor Green
Finish 0
