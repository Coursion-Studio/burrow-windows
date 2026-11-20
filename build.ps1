# Builds DiscordUnblock from source.
#   Output: dist\DiscordUnblock.exe   (the app; run it directly, it asks for admin rights)
# Needs: the .NET 8 SDK (https://dotnet.microsoft.com/download). Everything else ships with Windows.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host '1/2  Drawing the app icon...' -ForegroundColor Cyan
$ico = Join-Path $root 'DiscordUnblockApp\app.ico'
powershell -NoProfile -Command "Add-Type -Path '$root\Shared\IconArt.cs' -ReferencedAssemblies System.Drawing; [DiscordUnblock.IconArt]::WriteIco('$ico', [int[]](256,128,64,48,32,24,16))"
if ($LASTEXITCODE) { throw 'Icon generation failed' }

Write-Host '2/2  Building the app...' -ForegroundColor Cyan
dotnet publish "$root\DiscordUnblockApp\DiscordUnblock.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o (Join-Path $root 'dist') --nologo -v q
if ($LASTEXITCODE) { throw 'Build failed' }
Write-Host "Done: $(Join-Path $root 'dist\DiscordUnblock.exe')" -ForegroundColor Green
