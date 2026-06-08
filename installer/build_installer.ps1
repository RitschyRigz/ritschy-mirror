<#
  build_installer.ps1 - baut die App self-contained (publish.ps1) und kompiliert
  danach den Inno-Setup-Installer (RitschyMirror-Setup-x.y.z.exe) nach installer\out\.

  Voraussetzung: .NET 9 SDK + Inno Setup 6 (https://jrsoftware.org/isinfo.php).

  Aufruf:  powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)  # ..\ (Projekt-Root)

Write-Host "=== 1/2  App bauen (publish.ps1) ===" -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $root "publish.ps1")
if (-not (Test-Path (Join-Path $root "publish\RitschyMirror.exe"))) {
    throw "publish\RitschyMirror.exe fehlt - Build fehlgeschlagen."
}

Write-Host "=== 2/2  Installer kompilieren (ISCC) ===" -ForegroundColor Cyan
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe nicht gefunden - Inno Setup 6 installieren." }

& $iscc (Join-Path $root "installer\RitschyMirror.iss")
Write-Host ""
Write-Host "Fertig -> installer\out\RitschyMirror-Setup-*.exe" -ForegroundColor Green
