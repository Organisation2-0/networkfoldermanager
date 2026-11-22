# build-server.ps1
# Baut den NetworkFolderManager-Server (nfm-server) als eigenständige Einzeldatei für Linux, Windows und macOS.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-server.ps1 [-Rids linux-x64,linux-arm64]
#
# Ergebnis in output\: nfm-server-<Version>-<rid>.zip (inkl. Installationsskript)

param(
    [string[]]$Rids = @("linux-x64", "linux-arm64", "win-x64", "osx-arm64", "osx-x64")
)

$ErrorActionPreference = "Stop"
$Root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project   = Join-Path $Root "NetworkFolderManager.Server\NetworkFolderManager.Server.csproj"
$Deploy    = Join-Path $Root "NetworkFolderManager.Server\deploy"
$OutputDir = Join-Path $Root "output"

[xml]$csproj = Get-Content $Project
$Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { $Version = "1.0.0" }

Write-Host "=== NetworkFolderManager Server $Version ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $OutputDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($rid in $Rids) {
    Write-Host "[$rid] wird gebaut..." -ForegroundColor Yellow
    $pub = Join-Path $OutputDir "server\$rid"
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

    & dotnet publish $Project -c Release -r $rid --self-contained true -o $pub `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "Fehler beim Publizieren ($rid)." -ForegroundColor Red; exit 1 }

    if ($rid -like "linux-*") {
        Copy-Item (Join-Path $Deploy "install-linux.sh") $pub
        Copy-Item (Join-Path $Deploy "nfm-server.service") $pub
    } elseif ($rid -like "win-*") {
        Copy-Item (Join-Path $Deploy "install-windows.ps1") $pub
    }

    $zip = Join-Path $OutputDir "nfm-server-$Version-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pub, $zip, "Optimal", $false)
    Write-Host ("  -> {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

Write-Host ""
Write-Host "=== Fertig ===" -ForegroundColor Green
Write-Host "Linux:   ZIP entpacken, dann  sudo sh install-linux.sh"
Write-Host "Windows: ZIP entpacken, dann als Admin  install-windows.ps1"
Write-Host "macOS:   ZIP entpacken, dann  ./nfm-server  (läuft im Terminal)"
exit 0
