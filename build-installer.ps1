# build-installer.ps1
# Erstellt den NetworkFolderManager-Installer (Inno Setup) und zusätzlich ein portables ZIP.
# Läuft mit Windows PowerShell 5.1.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1

$ErrorActionPreference = "Stop"
$Root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $Root "NetworkFolderManager"
$Project    = Join-Path $ProjectDir "NetworkFolderManager.csproj"
$OutputDir  = Join-Path $Root "output"
$PublishDir = Join-Path $OutputDir "publish"
$IssFile    = Join-Path $Root "installer\NetworkFolderManager.iss"

# Version aus der .csproj lesen
[xml]$csproj = Get-Content $Project
$Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { $Version = "1.0.0" }

Write-Host "=== NetworkFolderManager $Version – Installer-Build ===" -ForegroundColor Cyan

# [1/4] Aufräumen
Write-Host "[1/4] Ausgabeordner vorbereiten..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force $OutputDir | Out-Null
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

# [2/4] Publish: eigenständig (keine .NET-Installation beim Benutzer nötig), eine EXE
Write-Host "[2/4] Projekt wird publiziert (self-contained, win-x64)..." -ForegroundColor Yellow
& dotnet publish $Project -c Release -r win-x64 --self-contained true -o $PublishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "Fehler beim Publizieren." -ForegroundColor Red; exit 1 }

# [3/4] Inno Setup
Write-Host "[3/4] Installer wird kompiliert..." -ForegroundColor Yellow
$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "Inno Setup 6 nicht gefunden. Installation: winget install JRSoftware.InnoSetup" -ForegroundColor Red
    exit 1
}

& $iscc /Q "/DAppVersion=$Version" "/DPublishDir=$PublishDir" $IssFile
if ($LASTEXITCODE -ne 0) { Write-Host "Fehler beim Kompilieren des Installers." -ForegroundColor Red; exit 1 }
$setup = Join-Path $OutputDir "NetworkFolderManager-Setup-$Version.exe"

# Prüfsumme für das Auto-Update (zusammen mit dem Setup ins GitHub-Release hochladen)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
$shaFile = [System.IO.Path]::ChangeExtension($setup, ".sha256")
[System.IO.File]::WriteAllText($shaFile, "$hash  $([System.IO.Path]::GetFileName($setup))`n")

# [4/4] Portables ZIP
Write-Host "[4/4] Portables ZIP wird erstellt..." -ForegroundColor Yellow
$zipPath = Join-Path $OutputDir "NetworkFolderManager-$Version-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $zipPath, "Optimal", $false)

Write-Host ""
Write-Host "=== Fertig ===" -ForegroundColor Green
Write-Host ("  Installer : {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host ("  Prüfsumme : {0}" -f $shaFile)
Write-Host ("  Portabel  : {0}" -f $zipPath)
Write-Host ""
Write-Host "Für Auto-Updates: GitHub-Release mit Tag v$Version anlegen und Setup + .sha256 hochladen." -ForegroundColor Cyan
exit 0
