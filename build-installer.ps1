# Builds FocusSetup.exe, a normal Windows installer you can share or download.
#
# Needs (both free):
#   .NET 10 SDK   https://dotnet.microsoft.com/download
#   Inno Setup 6  https://jrsoftware.org/isdl.php   (or: winget install JRSoftware.InnoSetup)
#
# Run it from PowerShell in this folder:
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building Focus..." -ForegroundColor Cyan
dotnet publish -c Release
if ($LASTEXITCODE -ne 0) { throw "The app didn't build. Read the red error text above." }

$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 isn't installed. Get it free from https://jrsoftware.org/isdl.php, then run this again." }

# Use the version number from FocusApp.csproj.
$version = (Select-String -Path FocusApp.csproj -Pattern '<Version>(.+)</Version>').Matches[0].Groups[1].Value

Write-Host "Making the installer (version $version)..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$version" FocusSetup.iss
if ($LASTEXITCODE -ne 0) { throw "The installer didn't build. Read the error text above." }

Write-Host "Done: installer\FocusSetup.exe" -ForegroundColor Green
explorer.exe (Join-Path $PSScriptRoot "installer")
