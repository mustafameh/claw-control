# Removes a Claw Control installed from the zip package: quits it (MSI gets its button back),
# removes the startup task and C:\Program Files\ClawControl, and on request the settings and log.
# (Installed with the setup .exe? Use Windows Settings > Apps > Installed apps instead.)
param(
    # Don't ask; keep the settings and log.
    [switch] $Yes
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'ClawControl'
$data = Join-Path $env:LOCALAPPDATA 'ClawControl'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    # The copy in Program Files is about to be deleted: run a temporary copy instead.
    $temp = Join-Path $env:TEMP 'ClawControl-uninstall'
    New-Item -ItemType Directory -Force $temp | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'task.ps1') $temp -Force
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $temp 'uninstall.ps1')`"")
    if ($Yes) { $arguments += '-Yes' }
    Start-Process powershell -Verb RunAs -ArgumentList $arguments
    return
}

Write-Host 'Removing Claw Control' -ForegroundColor Cyan
Set-Location $env:SystemRoot   # never stand in the folder being deleted
& (Join-Path $PSScriptRoot 'task.ps1') -Remove
if (Test-Path $target) { Remove-Item $target -Recurse -Force }

if (-not $Yes -and (Test-Path $data)) {
    if ((Read-Host "`nAlso delete your Claw Control settings and log ($data)? (y/n)") -match '^(y|yes)$') {
        Remove-Item $data -Recurse -Force
    }
}

Write-Host ("`nDone. MSI Center M has its button back. Settings Claw Control applied to the device stay as`n" +
    'they are (power mode, controller, lights, display, graphics); change them in MSI Center M if needed.') -ForegroundColor Green
if (-not $Yes) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
