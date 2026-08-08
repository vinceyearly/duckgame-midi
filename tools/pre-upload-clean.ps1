<#
.SYNOPSIS
  Deletes the mod loader's compiled artifacts from the Mods folder. Run this with the
  game open, immediately before pressing UPLOAD in the Mods menu.

.DESCRIPTION
  Duck Game's Workshop uploader copies the mod folder wholesale and *tries* to strip the
  compiled DLL and hash - but the paths it builds are wrong. It copies to
  `folderPath + "/" + name` and then deletes from `folderPath + name`, with no separator,
  so the delete targets a path that never exists and nothing is ever stripped. The
  "Rebuilt" variants were never on its list to begin with.

  That matters because ModLoader.AttemptCompile short-circuits: if it finds a
  `_compiled.hash` whose CRC32 matches the .cs files, and the matching `_compiled.dll`
  exists, it skips compilation entirely and loads the shipped DLL. A subscriber's .cs
  files are byte-identical to yours, so the hash *will* match - and they would load an
  assembly you compiled against your graphics stack (vanilla is XNA, Rebuilt is FNA).
  Shipping source exists precisely to avoid that; a leaked DLL silently undoes it.

  The game loads these assemblies from a byte array rather than mapping the file, so they
  are not locked and can be deleted while it runs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\pre-upload-clean.ps1
#>
[CmdletBinding()]
param(
    [string]$ModsDir,
    [string]$Name = 'DuckGameMidiController'
)

$ErrorActionPreference = 'Stop'

if (-not $ModsDir) {
    $root = Join-Path $env:APPDATA 'DuckGame'
    if (-not (Test-Path $root)) {
        $alt = Join-Path ([Environment]::GetFolderPath('Personal')) 'DuckGame'
        if (Test-Path $alt) { $root = $alt }
    }
    $acct = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d{17}$' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $ModsDir = if ($acct) { Join-Path $acct.FullName 'Mods' } else { Join-Path $root 'Mods' }
}

$target = Join-Path $ModsDir $Name
if (-not (Test-Path $target)) {
    Write-Host "Not installed at $target" -ForegroundColor Red
    exit 2
}

$patterns = @('*_compiled*.dll', '*_compiled*.pdb', '*_compiled*.hash',
              '*_compiled*Data.txt', '*_build.log')

$removed = 0
$failed = @()
foreach ($p in $patterns) {
    foreach ($f in (Get-ChildItem $target -Filter $p -File -ErrorAction SilentlyContinue)) {
        try {
            Set-ItemProperty $f.FullName -Name Attributes -Value Normal -ErrorAction SilentlyContinue
            Remove-Item $f.FullName -Force
            Write-Host "  removed $($f.Name)" -ForegroundColor DarkGray
            $removed++
        } catch {
            $failed += $f.Name
        }
    }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Could not delete (file locked?):" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $_" }
    Write-Host "Close Duck Game, run this again, then reopen the game and upload." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
if ($removed -eq 0) {
    Write-Host "Already clean - nothing to remove." -ForegroundColor Green
} else {
    Write-Host "Removed $removed artifact(s). Safe to upload now." -ForegroundColor Green
}
Write-Host "Do not relaunch or return to the main menu before uploading - the game" -ForegroundColor DarkGray
Write-Host "recompiles on load and the artifacts come straight back." -ForegroundColor DarkGray
