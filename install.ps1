<#
  install.ps1 — RitschyMirror lokal installieren (auf JEDEM Windows-PC).
  Kopiert das Publish-Ergebnis nach %LOCALAPPDATA%\RitschyMirror, legt Start-Menue- +
  (optional) Autostart-Verknuepfung an und reserviert — wenn als Admin gestartet —
  URL-ACL + Firewall fuer den HTTP-Agenten (damit das Cockpit ihn uebers Netz erreicht).

  Aufruf:
    powershell -ExecutionPolicy Bypass -File install.ps1                 # Standard (Port 8788)
    powershell -ExecutionPolicy Bypass -File install.ps1 -AgentPort 8790 # anderer Port
    powershell -ExecutionPolicy Bypass -File install.ps1 -NoAutostart    # ohne Login-Autostart
  Fuer URL-ACL/Firewall (Netz-Erreichbarkeit) als Administrator ausfuehren.
#>
[CmdletBinding()]
param(
    [int]$AgentPort = 8788,
    [switch]$NoAutostart,
    [switch]$NoNetwork   # URL-ACL + Firewall-Regel ueberspringen (nur lokal/localhost)
)
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir "publish"
$exeSrc = Join-Path $src "RitschyMirror.exe"
if (-not (Test-Path $exeSrc)) { throw "Kein publish\RitschyMirror.exe — erst publish.ps1 ausfuehren." }

$dest = Join-Path $env:LOCALAPPDATA "RitschyMirror"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Dateien kopieren — vorhandene Config bei Updates NICHT ueberschreiben.
$keep = @("mirror_config.json", "app_settings.json")
Get-ChildItem $src -File | ForEach-Object {
    $target = Join-Path $dest $_.Name
    if (($keep -contains $_.Name) -and (Test-Path $target)) { return }
    Copy-Item $_.FullName $target -Force
}
$exe = Join-Path $dest "RitschyMirror.exe"
Write-Host "Installiert nach: $dest" -ForegroundColor Green

# Start-Menue-Verknuepfung
$wsh = New-Object -ComObject WScript.Shell
function New-Lnk($path) { $l = $wsh.CreateShortcut($path); $l.TargetPath = $exe; $l.WorkingDirectory = $dest; $l.Save() }
New-Lnk (Join-Path ([Environment]::GetFolderPath("Programs")) "RitschyMirror.lnk")

# Login-Autostart
if (-not $NoAutostart) {
    New-Lnk (Join-Path ([Environment]::GetFolderPath("Startup")) "RitschyMirror.lnk")
    Write-Host "Login-Autostart eingerichtet." -ForegroundColor Green
}

# Netz-Erreichbarkeit des HTTP-Agenten (braucht Admin) — sonst faellt er auf localhost zurueck.
if (-not $NoNetwork) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
    if ($isAdmin) {
        try {
            $url = "http://+:$AgentPort/"
            cmd /c "netsh http add urlacl url=$url user=$env:USERNAME" | Out-Null
            cmd /c "netsh advfirewall firewall add rule name=`"RitschyMirror Agent`" dir=in action=allow protocol=TCP localport=$AgentPort" | Out-Null
            Write-Host "URL-ACL + Firewall fuer $url gesetzt." -ForegroundColor Green
        } catch { Write-Host "URL-ACL/Firewall fehlgeschlagen: $_" -ForegroundColor Yellow }
    } else {
        Write-Host "Hinweis: nicht als Admin — Agent ist nur via localhost erreichbar." -ForegroundColor Yellow
        Write-Host "  Fuer Cockpit-Fernsteuerung install.ps1 EINMAL als Admin laufen lassen (URL-ACL+Firewall)." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Starten: `"$exe`"  → Tray-Icon → Einstellungen." -ForegroundColor Cyan
Write-Host "Cockpit: Connector 'gaming_pc' → Feld 'agent_port' = $AgentPort (host = IP dieses PCs)." -ForegroundColor Cyan
