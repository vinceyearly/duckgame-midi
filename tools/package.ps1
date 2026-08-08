<#
.SYNOPSIS
  Builds a clean mod folder suitable for the Steam Workshop.

.DESCRIPTION
  The dev workflow links the whole git repo into Duck Game's Mods folder, which is
  convenient but wrong to publish: Duck Game's uploader copies the mod folder wholesale
  and only strips build/, .vs/ and compiled artifacts - it would happily upload .git/,
  tools/ and docs/ along with everything else.

  This produces a folder containing only what the mod needs at runtime:

      mod.conf, src\**\*.cs, content\, README.md, LICENSE

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
.EXAMPLE
  # build it straight into the Mods folder, ready to publish from the in-game Mods menu
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -InstallToMods
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$InstallToMods,
    [string]$Name = 'DuckGameMidiController'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($InstallToMods) {
    $root = Join-Path $env:APPDATA 'DuckGame'
    $acct = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d{17}$' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $modsDir = if ($acct) { Join-Path $acct.FullName 'Mods' } else { Join-Path $root 'Mods' }
    $OutDir = Join-Path $modsDir $Name
}
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "dist\$Name" }

# Refuse to clobber a junction (the dev link) without being told to.
if (Test-Path $OutDir) {
    $item = Get-Item $OutDir -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Write-Host "$OutDir is the dev junction. Removing the link (not its target)." -ForegroundColor Yellow
        cmd /c rmdir "`"$OutDir`"" | Out-Null
    } else {
        Remove-Item $OutDir -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# --- copy exactly what ships ----------------------------------------------
Copy-Item (Join-Path $repoRoot 'mod.conf') $OutDir

$srcOut = Join-Path $OutDir 'src'
New-Item -ItemType Directory -Path $srcOut -Force | Out-Null
Push-Location (Join-Path $repoRoot 'src')
try {
    Get-ChildItem -Recurse -Filter *.cs | ForEach-Object {
        $rel = $_.FullName.Substring((Get-Location).Path.Length + 1)
        $dest = Join-Path $srcOut $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item $_.FullName $dest
    }
} finally { Pop-Location }

$contentSrc = Join-Path $repoRoot 'content'
if (Test-Path $contentSrc) {
    Copy-Item $contentSrc (Join-Path $OutDir 'content') -Recurse
}

foreach ($doc in @('README.md', 'LICENSE')) {
    $p = Join-Path $repoRoot $doc
    if (Test-Path $p) { Copy-Item $p $OutDir }
}

# --- sanity checks ---------------------------------------------------------
$stray = Get-ChildItem $OutDir -Recurse -Filter *.cs |
         Where-Object { $_.FullName -notlike (Join-Path $srcOut '*') }
if ($stray) {
    Write-Host "Stray .cs outside src\ - Duck Game would compile these too:" -ForegroundColor Red
    $stray | ForEach-Object { Write-Host "  $($_.FullName)" }
    exit 1
}

$csCount = (Get-ChildItem $srcOut -Recurse -Filter *.cs).Count
$size = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB), 1)

Write-Host "Packaged to:" -ForegroundColor Green
Write-Host "  $OutDir"
Write-Host "  $csCount source file(s), $size KB total" -ForegroundColor DarkGray

if (-not (Test-Path (Join-Path $OutDir 'content\preview.png'))) {
    Write-Host "  note: content\preview.png missing - Steam will use an auto-generated shot" -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "To publish: launch Duck Game, open the Mods menu, select MIDI Controller," -ForegroundColor Cyan
Write-Host "then use the Workshop upload option. Publish PRIVATE first and verify a" -ForegroundColor Cyan
Write-Host "clean subscribe before making it public." -ForegroundColor Cyan
