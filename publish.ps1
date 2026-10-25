# Builds the single self-contained exe into .\publish\
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish src/WoRModManager.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-Item "publish\War of Rights Mod Manager.exe" | Select-Object Name, Length
