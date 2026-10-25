# Builds the release files into dist\:
#   ClawControl-Setup-<version>.exe     setup program (Inno Setup 6)
#   ClawControl-<version>-win-x64.zip   the same app with Install.cmd / Uninstall.cmd
#   SHA256SUMS.txt                      checksums of both
# Needs the .NET 10 SDK and Inno Setup 6. The version comes from ClawControl.csproj.
param(
    [string] $Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'ClawControl.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

function Find-Tool([string] $name, [string[]] $candidates) {
    $found = @($candidates | Where-Object { $_ -and (Test-Path $_) }) + @((Get-Command $name -ErrorAction SilentlyContinue).Source) |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $found) { throw "$name not found." }
    $found
}
$dotnet = Find-Tool 'dotnet' @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")
$iscc = Find-Tool 'ISCC' @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")

$dist = Join-Path $root 'dist'
$name = "ClawControl-$Version"
$stage = Join-Path $dist "stage\$name"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Write-Host "Building Claw Control $Version" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'ClawControl.csproj') -c Release -o (Join-Path $stage 'app') -p:Version=$Version -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem (Join-Path $stage 'app') -Filter *.pdb | Remove-Item

# Zip package: Install.cmd / Uninstall.cmd on top, scripts\ and app\ inside.
$installer = Join-Path $root 'installer'
New-Item -ItemType Directory -Force (Join-Path $stage 'scripts') | Out-Null
Copy-Item (Join-Path $installer 'install.ps1'), (Join-Path $installer 'uninstall.ps1'), (Join-Path $installer 'task.ps1') (Join-Path $stage 'scripts')
Copy-Item (Join-Path $installer 'Install.cmd'), (Join-Path $installer 'Uninstall.cmd') $stage
(Get-Content (Join-Path $installer 'zip-readme.txt') -Raw).Replace('{VERSION}', $Version) |
    Set-Content (Join-Path $stage 'README.txt') -Encoding utf8
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'licenses') $stage -Recurse

# Entries are written one by one: Windows PowerShell's CreateFromDirectory stores "\" in paths,
# which some unzip tools turn into file names instead of folders.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = Join-Path $dist "$name-win-x64.zip"
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = "$name/" + $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }

# Setup program from the same app files.
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$(Join-Path $stage 'app')" (Join-Path $installer 'ClawControl.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Remove-Item (Join-Path $dist 'stage') -Recurse -Force

$sums = Get-ChildItem $dist -File | Where-Object Extension -in '.exe', '.zip' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem $dist | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
