<#
.SYNOPSIS
  Reproduces Duck Game's in-game mod compilation locally, against BOTH the vanilla and
  the Duck Game Rebuilt builds, so errors are found in seconds instead of by launching
  the game.

.DESCRIPTION
  ModLoader.AttemptCompile compiles a mod's .cs files in-process with
  Microsoft.CSharp.CSharpCodeProvider - the in-box .NET Framework csc, which is C# 5:

    no string interpolation ($"..."), no null-conditional (?.), no expression-bodied
    members (=>), no nameof, no tuples, no pattern matching, no auto-property
    initializers, no out-var, no using static.

  This script drives that same csc.exe through a response file. It checks both targets
  because the two builds sit on different graphics stacks - vanilla Duck Game is XNA
  (assemblies from the GAC) while Rebuilt is FNA - and a mod shipped as source has to
  compile against whichever one the subscriber is running.

  (A response file is used rather than CompilerParameters because the CodeDom wrapper
  throws "The given path's format is not supported" once the generated command line
  passes a certain length - an artefact of the wrapper, not of the compiler.)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\check-compile.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('both', 'vanilla', 'dgr')]
    [string]$Target = 'both',
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repoRoot 'src'

if (-not (Test-Path $srcDir)) {
    Write-Host "No src\ directory at $srcDir" -ForegroundColor Red
    exit 2
}

$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs -Recurse | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) {
    Write-Host "No .cs files under src\" -ForegroundColor Yellow
    exit 0
}

# --- locate csc ------------------------------------------------------------
$csc = $null
foreach ($v in @('Framework64', 'Framework')) {
    $p = Join-Path $env:WinDir "Microsoft.NET\$v\v4.0.30319\csc.exe"
    if (Test-Path $p) { $csc = $p; break }
}
if (-not $csc) {
    Write-Host "Could not find the .NET Framework csc.exe (v4.0.30319)." -ForegroundColor Red
    exit 2
}
$fwDir = Split-Path $csc -Parent

# --- discover the two game builds -----------------------------------------
function Get-VanillaDir {
    foreach ($c in @("${env:ProgramFiles(x86)}\Steam\steamapps\common\Duck Game",
                     "$env:ProgramFiles\Steam\steamapps\common\Duck Game")) {
        if (Test-Path (Join-Path $c 'DuckGame.exe')) { return $c }
    }
    return $null
}
function Get-DgrDir {
    $roots = @("${env:ProgramFiles(x86)}\Steam\steamapps\workshop\content\312530",
               "$env:ProgramFiles\Steam\steamapps\workshop\content\312530")
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem $r -Recurse -Filter 'DuckGame.exe' -ErrorAction SilentlyContinue |
               Where-Object { $_.DirectoryName -match 'dgr' } | Select-Object -First 1
        if ($hit) { return $hit.DirectoryName }
    }
    return $null
}

# XNA lives in the GAC; only vanilla needs it.
function Get-XnaRefs {
    $out = @()
    foreach ($n in @('Microsoft.Xna.Framework', 'Microsoft.Xna.Framework.Game',
                     'Microsoft.Xna.Framework.Graphics')) {
        $dir = Join-Path $env:WinDir "Microsoft.NET\assembly\GAC_32\$n"
        if (-not (Test-Path $dir)) { continue }
        $dll = Get-ChildItem $dir -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($dll) { $out += $dll.FullName }
    }
    return $out
}

function Invoke-Check([string]$label, [string]$gameDir, [string[]]$extraRefs) {
    Write-Host "=== $label ===" -ForegroundColor Cyan
    Write-Host "    $gameDir" -ForegroundColor DarkGray

    $refPaths = @()
    foreach ($n in @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll',
                     'System.Drawing.dll','System.Windows.Forms.dll')) {
        $p = Join-Path $fwDir $n
        if (Test-Path $p) { $refPaths += $p }
    }
    foreach ($n in @('DuckGame.exe','NAudio.dll','FNA.dll','DGSteam.dll','DGInput.dll')) {
        $p = Join-Path $gameDir $n
        if (Test-Path $p) { $refPaths += $p }
    }
    $refPaths += $extraRefs

    $outDll = Join-Path ([System.IO.Path]::GetTempPath()) ("DGMidiCheck_$label.dll")
    $rsp = [System.IO.Path]::GetTempFileName() + '.rsp'

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('/target:library')
    $lines.Add('/nologo')
    $lines.Add('/define:DGR')
    $lines.Add('/langversion:5')     # fail loudly here, not mysteriously in-game
    $lines.Add("/out:`"$outDll`"")
    foreach ($r in $refPaths) { $lines.Add("/reference:`"$r`"") }
    foreach ($s in $sources) { $lines.Add("`"$s`"") }
    # csc reads response files in the system ANSI codepage unless told otherwise.
    [System.IO.File]::WriteAllLines($rsp, $lines, [System.Text.Encoding]::Default)

    $output = & $csc "@$rsp" 2>&1
    $exit = $LASTEXITCODE

    Remove-Item $rsp -ErrorAction SilentlyContinue
    if (-not $KeepOutput) { Remove-Item $outDll -ErrorAction SilentlyContinue }

    $errorCount = 0; $warnCount = 0
    foreach ($line in $output) {
        $t = ([string]$line) -replace [regex]::Escape($repoRoot + '\'), ''
        if ($t -match ':\s*error\s') { $errorCount++; Write-Host "  $t" -ForegroundColor Red }
        elseif ($t -match ':\s*warning\s') { $warnCount++; Write-Host "  $t" -ForegroundColor DarkYellow }
        elseif ($t.Trim() -and $t -notmatch 'Location of symbol') { Write-Host "  $t" -ForegroundColor DarkGray }
    }

    if ($exit -ne 0 -or $errorCount -gt 0) {
        Write-Host "    FAILED - $errorCount error(s), $warnCount warning(s)" -ForegroundColor Red
        return $false
    }
    Write-Host "    OK ($warnCount warning(s))" -ForegroundColor Green
    return $true
}

Write-Host "Files: $($sources.Count)   csc: $csc" -ForegroundColor DarkGray
Write-Host ""

$allOk = $true
$ran = 0

if ($Target -eq 'both' -or $Target -eq 'vanilla') {
    $dir = Get-VanillaDir
    if ($dir) {
        $ran++
        if (-not (Invoke-Check 'vanilla' $dir (Get-XnaRefs))) { $allOk = $false }
        Write-Host ""
    } else {
        Write-Host "vanilla Duck Game not found - skipping" -ForegroundColor DarkYellow
    }
}

if ($Target -eq 'both' -or $Target -eq 'dgr') {
    $dir = Get-DgrDir
    if ($dir) {
        $ran++
        if (-not (Invoke-Check 'rebuilt' $dir @())) { $allOk = $false }
        Write-Host ""
    } else {
        Write-Host "Duck Game Rebuilt not found - skipping" -ForegroundColor DarkYellow
    }
}

if ($ran -eq 0) {
    Write-Host "No Duck Game install found to compile against." -ForegroundColor Red
    exit 2
}
if (-not $allOk) { exit 1 }
Write-Host "All targets compiled cleanly." -ForegroundColor Green
exit 0
