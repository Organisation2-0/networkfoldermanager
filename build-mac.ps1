# build-mac.ps1
# Erstellt die macOS-App (NetworkFolderManager.app, als ZIP) für Apple Silicon (arm64) und Intel (x64).
# Läuft unter Windows (Cross-Build) – benötigt .NET 8 SDK und Python 3.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-mac.ps1
#
# Hinweis: Die App ist nicht signiert/notarisiert. Auf dem Mac einmalig: Rechtsklick → Öffnen,
# oder im Terminal:  xattr -cr NetworkFolderManager.app && codesign --force --deep --sign - NetworkFolderManager.app
# Ein signiertes Build liefert alternativ der GitHub-Workflow .github/workflows/build-mac.yml (macOS-Runner).

$ErrorActionPreference = "Stop"
$Root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $Root "NetworkFolderManager.Mac"
$Project    = Join-Path $ProjectDir "NetworkFolderManager.Mac.csproj"
$OutputDir  = Join-Path $Root "output"

[xml]$csproj = Get-Content $Project
$Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { $Version = "1.0.0" }

Write-Host "=== NetworkFolderManager $Version - macOS-Build ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $OutputDir | Out-Null

foreach ($rid in @("osx-arm64", "osx-x64")) {
    $publish = Join-Path $OutputDir "publish-$rid"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "[$rid] publish..." -ForegroundColor Yellow
    & dotnet publish $Project -c Release -r $rid --self-contained true -o $publish -p:DebugType=none -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "Fehler beim Publizieren ($rid)." -ForegroundColor Red; exit 1 }

    $suffix = if ($rid -eq "osx-arm64") { "arm64" } else { "x64" }
    $zip = Join-Path $OutputDir "NetworkFolderManager-$Version-mac-$suffix.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host "[$rid] App-Bundle..." -ForegroundColor Yellow
    & python (Join-Path $Root "tools\make_mac_app.py") $publish $zip $Version `
        (Join-Path $ProjectDir "Packaging\Info.plist.template") (Join-Path $ProjectDir "Assets\appicon.png")
    if ($LASTEXITCODE -ne 0) { Write-Host "Fehler beim Erstellen des Bundles ($rid)." -ForegroundColor Red; exit 1 }

    Remove-Item $publish -Recurse -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content -Path ([IO.Path]::ChangeExtension($zip, ".sha256")) -Encoding ASCII
}

Write-Host ""
Write-Host "Fertig:" -ForegroundColor Green
Get-ChildItem $OutputDir -Filter "*-mac-*" | ForEach-Object { Write-Host "  $($_.FullName)" }
