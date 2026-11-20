<#
  DiscordUnblock - gets around DPI/DNS blocking of Discord without a VPN.

  How it works:
    * Uses GoodbyeDPI (open source, github.com/ValdikSS/GoodbyeDPI), which splits /
      disguises the TLS handshake so the ISP's DPI can't read the "discord.com" name.
    * Only Discord domains are touched (blacklist) unless you pass -AllSites.
    * DNS lookups are sent to Yandex DNS on a non-standard port so the ISP
      can't hand back fake answers. Your Windows DNS settings are NOT changed.
    * Your normal traffic still goes straight out - no VPN, no speed loss.

  Usage (from an admin PowerShell, or just double-click DiscordUnblock.cmd):
    .\DiscordUnblock.ps1 start        # run until you close the window
    .\DiscordUnblock.ps1 install      # run as a Windows service, auto-starts at boot
    .\DiscordUnblock.ps1 uninstall    # remove the service
    .\DiscordUnblock.ps1 status
    .\DiscordUnblock.ps1 start -Mode 2   # try another bypass method if mode 1 fails
#>
param(
    [ValidateSet('menu', 'start', 'install', 'uninstall', 'status')]
    [string]$Action = 'menu',
    [ValidateSet('1', '2', '3')]
    [string]$Mode = '1',
    [switch]$AllSites
)

$ErrorActionPreference = 'Stop'
$InstallDir  = Join-Path $env:ProgramData 'DiscordUnblock'
$ServiceName = 'DiscordUnblock'

$DiscordDomains = @(
    'discord.com', 'discordapp.com', 'discord.gg', 'discord.media', 'discordapp.net',
    'discord.dev', 'discord.new', 'discord.gift', 'discord.co', 'discordcdn.com',
    'discordstatus.com', 'dis.gd', 'discord-attachments-uploads-prd.storage.googleapis.com'
)

# Bypass methods. If Discord still won't load, try the next one.
$ModeArgs = @{
    '1' = '-6'    # split handshake + wrong-seq decoys (most reliable in testing)
    '2' = '-5'    # decoys with automatic low TTL
    '3' = '-1'    # legacy tricks for older DPI systems
}
$DnsArgs = '--dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Exe { Join-Path $InstallDir 'goodbyedpi.exe' }

function Install-GoodbyeDPI {
    if (Test-Path (Get-Exe)) { return }
    Write-Host 'Downloading GoodbyeDPI from the official GitHub release...' -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    # Newest release incl. pre-releases: the -9 preset only exists in 0.2.3+
    $all = Invoke-RestMethod 'https://api.github.com/repos/ValdikSS/GoodbyeDPI/releases?per_page=5' `
        -Headers @{ 'User-Agent' = 'DiscordUnblock' }
    $rel = $all[0]
    $asset = $rel.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
    if (-not $asset -or $asset.browser_download_url -notlike 'https://github.com/ValdikSS/GoodbyeDPI/*') {
        throw 'Could not find the official GoodbyeDPI zip in the latest release.'
    }
    $tmp = Join-Path $env:TEMP ('gdpi_' + [guid]::NewGuid())
    New-Item -ItemType Directory $tmp | Out-Null
    $zip = Join-Path $tmp 'gdpi.zip'
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $tmp 'x') -Force

    $arch = if ([Environment]::Is64BitOperatingSystem) { 'x86_64' } else { 'x86' }
    $exe = Get-ChildItem (Join-Path $tmp 'x') -Recurse -Filter goodbyedpi.exe |
        Where-Object { $_.Directory.Name -eq $arch } | Select-Object -First 1
    if (-not $exe) { throw "goodbyedpi.exe ($arch) not found in the release zip." }

    New-Item -ItemType Directory $InstallDir -Force | Out-Null
    Copy-Item (Join-Path $exe.DirectoryName '*') $InstallDir -Force
    Remove-Item $tmp -Recurse -Force
    Write-Host "Installed GoodbyeDPI $($rel.tag_name) to $InstallDir" -ForegroundColor Green
}

function Get-Args {
    $list = Join-Path $InstallDir 'discord-domains.txt'
    $DiscordDomains | Set-Content $list -Encoding ASCII
    $a = @(("$($ModeArgs[$Mode]) $DnsArgs").Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
    if (-not $AllSites) { $a += '--blacklist', $list }
    $a   # unrolled to the caller as separate string arguments
}

function Remove-Service {
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        sc.exe stop $ServiceName | Out-Null
        Start-Sleep 1
        sc.exe delete $ServiceName | Out-Null
        Write-Host 'Service removed.' -ForegroundColor Yellow
    }
}

function Show-Status {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    $proc = Get-Process goodbyedpi -ErrorAction SilentlyContinue
    Write-Host ("Service : " + $(if ($svc) { "$($svc.Status) ($($svc.StartType))" } else { 'not installed' }))
    Write-Host ("Running : " + $(if ($proc) { 'yes' } else { 'no' }))
}

if (-not (Test-Admin)) { throw 'Run this as Administrator (or use DiscordUnblock.cmd).' }

if ($Action -eq 'menu') {
    Write-Host "`n=== DiscordUnblock ===" -ForegroundColor Cyan
    Show-Status
    Write-Host "`n 1) Start now (this window)`n 2) Install as auto-start service`n 3) Uninstall service`n 4) Try another method (start, mode 2)`n 5) Try another method (start, mode 3)`n 6) Exit"
    switch (Read-Host 'Choose') {
        '1' { $Action = 'start' }
        '2' { $Action = 'install' }
        '3' { $Action = 'uninstall' }
        '4' { $Action = 'start'; $Mode = '2' }
        '5' { $Action = 'start'; $Mode = '3' }
        default { return }
    }
}

switch ($Action) {
    'status'    { Show-Status }
    'uninstall' { Remove-Service; ipconfig /flushdns | Out-Null }
    'start' {
        Install-GoodbyeDPI
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Write-Host 'The service is installed and already handling this - nothing to start.' -ForegroundColor Yellow
            return
        }
        ipconfig /flushdns | Out-Null
        Write-Host "Running (mode $Mode). Restart Discord now. Close this window or press Ctrl+C to stop." -ForegroundColor Green
        [string[]]$gdpiArgs = Get-Args
        & (Get-Exe) $gdpiArgs
    }
    'install' {
        Install-GoodbyeDPI
        Remove-Service
        Get-Process goodbyedpi -ErrorAction SilentlyContinue | Stop-Process -Force
        $bin = "`"$(Get-Exe)`" " + ((Get-Args) -join ' ')
        sc.exe create $ServiceName binPath= $bin start= auto DisplayName= 'Discord Unblock (GoodbyeDPI)' | Out-Null
        sc.exe description $ServiceName 'Bypasses DPI blocking for Discord.' | Out-Null
        sc.exe start $ServiceName | Out-Null
        ipconfig /flushdns | Out-Null
        Write-Host "Service installed and started (mode $Mode). It will start with Windows. Restart Discord." -ForegroundColor Green
    }
}
