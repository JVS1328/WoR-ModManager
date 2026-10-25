# Builds the single self-contained exe into .\publish\, plus what a GitHub release needs:
#   publish\WoRModManager.exe                     the app (self-update downloads this)
#   publish\WoR-Mod-Manager-vX.Y.Z.zip            the same exe zipped, for Nexus
#   publish\legacy\War of Rights Mod Manager.exe  the same exe under the 1.0.x name, so 1.0.x copies can still update themselves
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish src/WoRModManager.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$version = ([xml](Get-Content src/WoRModManager.App/WoRModManager.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$exe = "publish\WoRModManager.exe"
Remove-Item "publish\War of Rights Mod Manager.exe", "publish\WoR-Mod-Manager-v*.zip" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "publish\legacy" | Out-Null
Copy-Item $exe "publish\legacy\War of Rights Mod Manager.exe" -Force
Compress-Archive -Path $exe -DestinationPath "publish\WoR-Mod-Manager-v$version.zip" -CompressionLevel Optimal -Force
Get-Item $exe, "publish\WoR-Mod-Manager-v$version.zip", "publish\legacy\War of Rights Mod Manager.exe" | Select-Object Name, Length
