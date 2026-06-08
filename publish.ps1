<#
  publish.ps1 — baut RitschyMirror self-contained (eine .exe, ohne .NET am Ziel-PC) nach .\publish\.
  Danach install.ps1 ausfuehren (kopiert + Verknuepfungen + optional URL-ACL/Firewall).

  Aufruf:  powershell -ExecutionPolicy Bypass -File publish.ps1
#>
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $dir
try {
    Write-Host "=== dotnet publish (self-contained, single-file) ===" -ForegroundColor Cyan
    dotnet publish -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o publish
    $exe = Join-Path $dir "publish\RitschyMirror.exe"
    if (-not (Test-Path $exe)) { throw "Publish fehlgeschlagen: $exe nicht gefunden." }
    Write-Host "Fertig: $exe" -ForegroundColor Green
    Write-Host "Naechster Schritt: install.ps1 (am Ziel-PC)" -ForegroundColor Green
}
finally { Pop-Location }
