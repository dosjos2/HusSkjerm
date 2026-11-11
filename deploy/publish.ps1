# Lager ferdig pakke i .\dist\pi
#   .\deploy\publish.ps1                    -> linux-arm64 (64-bit Raspberry Pi OS)
#   .\deploy\publish.ps1 -PiRid linux-arm   (32-bit Raspberry Pi OS)
param([string]$PiRid = 'linux-arm64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root\src\HusSkjerm" -c Release -r $PiRid --self-contained -p:DebugType=none -o "$dist\pi\app"
if ($LASTEXITCODE -ne 0) { throw 'Publish feilet.' }
Copy-Item "$PSScriptRoot\install.sh", "$PSScriptRoot\husskjerm.service" "$dist\pi"

Write-Host "`nFerdig. Kopier til Pi-en:  scp -r dist\pi <bruker>@husskjerm.local:~/husskjerm-install"
