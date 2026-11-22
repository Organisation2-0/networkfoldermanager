# NetworkFolderManager Server – Installation als Windows-Dienst
# Als Administrator ausführen:
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-windows.ps1

$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Bitte als Administrator ausführen." -ForegroundColor Red
    exit 1
}

$src = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "nfm-server.exe"
$dir = Join-Path $env:ProgramFiles "NFM-Server"
$exe = Join-Path $dir "nfm-server.exe"

Write-Host "== NetworkFolderManager Server wird installiert ==" -ForegroundColor Cyan

$svc = Get-Service -Name "NFM-Server" -ErrorAction SilentlyContinue
if ($svc) { Stop-Service $svc -Force -ErrorAction SilentlyContinue; Start-Sleep 2 }

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $src $exe -Force

if (-not $svc) {
    New-Service -Name "NFM-Server" -BinaryPathName "`"$exe`"" -DisplayName "NetworkFolderManager Server" `
        -Description "Verschlüsselte, passwortgeschützte Netzwerkordner (HTTPS, WebDAV, Web-App)" -StartupType Automatic | Out-Null
    sc.exe failure "NFM-Server" reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

# Firewall: nur private Netzwerke / Domäne
Remove-NetFirewallRule -DisplayName "NFM-Server*" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "NFM-Server HTTPS" -Direction Inbound -Protocol TCP -LocalPort 45830 -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "NFM-Server Erkennung" -Direction Inbound -Protocol UDP -LocalPort 45823 -Action Allow -Profile Private,Domain | Out-Null

Start-Service "NFM-Server"
Start-Sleep 3
& $exe info
Write-Host ""
Write-Host "Fertig. Admin-Konto anlegen:  & `"$exe`" admin add <Name>" -ForegroundColor Green
Write-Host "Oder im Browser https://<IP-Adresse>:45830 öffnen und mit dem Setup-Code oben registrieren."
