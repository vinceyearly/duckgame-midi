<#
.SYNOPSIS
  Links this repo into Duck Game's Mods folder so edits are picked up on next launch.

.DESCRIPTION
  Creates a directory junction from the game's Mods folder to this repo. Because the mod
  ships as source and Duck Game compiles it at startup, a junction means "edit, relaunch,
  test" with no build step in between.

  Duck Game scans two locations; this targets the per-Steam-account one:
    %APPDATA%\DuckGame\<SteamID64>\Mods\
  falling back to the shared folder if no account folder exists yet.

  Junctions need no administrator rights (unlike symlinks), which is why one is used.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install-dev.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install-dev.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ModsDir,
    [string]$Name = 'DuckGameMidiController',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-ModsDir {
    $root = Join-Path $env:APPDATA 'DuckGame'
    if (-not (Test-Path $root)) {
        # Older installs kept the save data under Documents instead.
        $alt = Join-Path ([Environment]::GetFolderPath('Personal')) 'DuckGame'
        if (Test-Path $alt) { $root = $alt } else { return $null }
    }
    # Prefer the per-account folder (a 17-digit SteamID64), newest first.
    $acct = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d{17}$' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($acct) { return (Join-Path $acct.FullName 'Mods') }
    return (Join-Path $root 'Mods')
}

if (-not $ModsDir) { $ModsDir = Resolve-ModsDir }
if (-not $ModsDir) {
    Write-Host "Could not find the Duck Game save folder. Run the game once, then retry." -ForegroundColor Red
    Write-Host "Or pass -ModsDir explicitly." -ForegroundColor DarkGray
    exit 2
}

$target = Join-Path $ModsDir $Name

if ($Uninstall) {
    if (-not (Test-Path $target)) {
        Write-Host "Nothing installed at $target" -ForegroundColor DarkGray
        exit 0
    }
    $item = Get-Item $target -Force
    if ($item.LinkType -eq 'Junction' -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # Remove the junction itself, never its contents.
        cmd /c rmdir "`"$target`"" | Out-Null
        Write-Host "Removed link $target" -ForegroundColor Green
    } else {
        Write-Host "$target is a real directory, not a link - not deleting it." -ForegroundColor Yellow
        Write-Host "Remove it by hand if you mean to." -ForegroundColor DarkGray
        exit 1
    }
    exit 0
}

if (-not (Test-Path $ModsDir)) {
    New-Item -ItemType Directory -Path $ModsDir -Force | Out-Null
}

if (Test-Path $target) {
    $item = Get-Item $target -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        cmd /c rmdir "`"$target`"" | Out-Null
    } else {
        Write-Host "$target already exists and is a real directory." -ForegroundColor Red
        Write-Host "Move or delete it first (it may be a Workshop copy of this mod)." -ForegroundColor DarkGray
        exit 1
    }
}

cmd /c mklink /J "`"$target`"" "`"$repoRoot`"" | Out-Null
if (-not (Test-Path $target)) {
    Write-Host "Failed to create the junction." -ForegroundColor Red
    exit 1
}

Write-Host "Linked:" -ForegroundColor Green
Write-Host "  $target" -ForegroundColor Green
Write-Host "  -> $repoRoot" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Launch Duck Game, then check the Mods menu for 'MIDI Controller'." -ForegroundColor Cyan
Write-Host "If it shows an error, read: $repoRoot\$Name`_build.log" -ForegroundColor DarkGray
