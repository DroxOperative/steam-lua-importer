# Builds SteamLuaImporter and packs it as SteamLuaImporter.pext next to this script.
# Requires the .NET SDK (any version 6+)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build SteamLuaImporter.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$out = Join-Path $PSScriptRoot 'bin\Release'
$zip = Join-Path $PSScriptRoot 'SteamLuaImporter.zip'
$pext = Join-Path $PSScriptRoot 'SteamLuaImporter.pext'
Remove-Item $zip, $pext -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $out 'SteamLuaImporter.dll'), (Join-Path $out 'extension.yaml'), (Join-Path $out 'icon.png') -DestinationPath $zip
Move-Item $zip $pext
Write-Host "Done: $pext  (double-click it to install into Playnite)"
