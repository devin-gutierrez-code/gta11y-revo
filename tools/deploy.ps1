<#
.SYNOPSIS
  Deploys the built GTA11Y mod into GTA V's ScriptHookVDotNet `scripts` folder.

.DESCRIPTION
  `dotnet build` only writes bin\Release\net48\ — the game never sees it until the
  DLL is copied into <GTA V>\scripts\. That missing step is why iter-25 and iter-26
  were built but never tested (the in-game build stayed at iter-24). Run this after
  building to actually deploy, then confirm the in-game drive-assist log banner
  reads the expected iter-version.

  Data files are synced by CONTENT, every run: a bundled file is copied when it is
  missing at the destination or differs from it. It used to be opt-in behind
  -IncludeData, and that is how iter-58 shipped a name table the game never received —
  every DLL-only redeploy left `gta11y-modelnames.tsv.gz` uninstalled, the mod loaded
  `fileNotFound, 0 rows`, and every pedestrian and prop in the nearby scans went
  unnamed for weeks. A deploy that silently leaves the game without data is the exact
  failure mode this mod cannot afford, so the flag no longer gates the copy.
  -IncludeData now only forces a re-copy of files that already match.

.PARAMETER ScriptsDir
  The GTA V `scripts` folder, e.g. "C:\Program Files\Rockstar Games\Grand Theft Auto V\scripts".
  Falls back to the GTA5_SCRIPTS_DIR environment variable if omitted.

.PARAMETER Configuration
  Build configuration to deploy from. Omit to auto-detect: whichever of
  Debug/Release contains the newest built DLL wins (dotnet build defaults to
  Debug, so a bare build-then-deploy now just works).

.PARAMETER IncludeData
  Force a re-copy of every bundled data file even when the deployed copy already
  matches. Not needed for normal use — data is synced by content on every run.
  Useful when you suspect the destination was corrupted or edited in place.

.NOTES
  Use `powershell`, not `pwsh`. This script has no PowerShell 7-only syntax and runs
  fine on the Windows PowerShell 5.1 that ships with Windows; `pwsh` is the separate
  PowerShell 7 install that most machines do not have, and calling it gives a bare
  CommandNotFoundException that looks like the script is broken.

.EXAMPLE
  powershell -File tools\deploy.ps1 -ScriptsDir "D:\Games\GTAV\scripts"
.EXAMPLE
  $env:GTA5_SCRIPTS_DIR = "D:\Games\GTAV\scripts"; powershell -File tools\deploy.ps1
#>
param(
    [string]$ScriptsDir = $(if ($env:GTA5_SCRIPTS_DIR) { $env:GTA5_SCRIPTS_DIR } else { "D:\SteamLibrary\steamapps\common\Grand Theft Auto V\scripts" }),
    # Empty = auto-detect: deploy whichever of Debug/Release has the newest
    # built DLL. `dotnet build` with no -c produces Debug, but this script's
    # old default was Release — that mismatch stranded builds before.
    [string]$Configuration = "",
    [switch]$IncludeData
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$mainDll  = "GrandTheftAccessibilityRevo.dll"

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $candidates = @("Debug", "Release") | ForEach-Object {
        $dll = Join-Path $repoRoot "GTA\bin\$_\net48\$mainDll"
        if (Test-Path $dll) { [pscustomobject]@{ Config = $_; Stamp = (Get-Item $dll).LastWriteTime } }
    }
    if (-not $candidates) {
        Write-Error "No build output found in GTA\bin\Debug\net48 or GTA\bin\Release\net48. Run: dotnet build GTA\GrandTheftAccessibilityRevo.csproj"
    }
    $picked = $candidates | Sort-Object Stamp -Descending | Select-Object -First 1
    $Configuration = $picked.Config
    Write-Host "Auto-detected configuration: $Configuration (newest $mainDll built $($picked.Stamp))"
    if ($candidates.Count -gt 1) {
        $other = $candidates | Where-Object { $_.Config -ne $Configuration }
        Write-Host "  (ignoring $($other.Config), older build from $($other.Stamp))"
    }
}
$outDir = Join-Path $repoRoot "GTA\bin\$Configuration\net48"

if ([string]::IsNullOrWhiteSpace($ScriptsDir)) {
    Write-Error "No target scripts folder. Pass -ScriptsDir '<GTA V>\scripts' or set `$env:GTA5_SCRIPTS_DIR."
}
if (-not (Test-Path $ScriptsDir)) {
    Write-Error "Scripts folder not found: $ScriptsDir"
}
if (-not (Test-Path $outDir)) {
    Write-Error "Build output not found: $outDir  (run: dotnet build GTA\GrandTheftAccessibilityRevo.csproj -c $Configuration)"
}

# The managed DLLs the mod needs at runtime. ScriptHookVDotNet3.dll is excluded
# from the build output on purpose (it ships with SHVDN itself) — never deploy it.
$dlls = @(
    "GrandTheftAccessibilityRevo.dll",
    "CSCore.dll",
    "NAudio.dll",
    "Newtonsoft.Json.dll"
)

$lockedFiles = @()
foreach ($f in $dlls) {
    $src = Join-Path $outDir $f
    if (Test-Path $src) {
        try {
            Copy-Item $src (Join-Path $ScriptsDir $f) -Force -ErrorAction Stop
            Write-Host "  copied $f"
        } catch {
            $lockedFiles += $f
            Write-Warning "  LOCKED: $f - is GTA V running? Close the game (or reload SHVDN) and re-run this script."
        }
    } else {
        Write-Warning "  missing in build output: $f"
    }
}

# Mirror the bundled data next to the DLL. Runs EVERY deploy (iter-60) — see the
# -IncludeData note in the header for why this is no longer opt-in.
#
# The mod resolves "scripts/gta11y-*.json*" relative to the GAME ROOT, i.e.
# flat inside $ScriptsDir. The old map re-joined "scripts\..." onto
# $ScriptsDir and landed everything in a dead-end scripts\scripts\ folder —
# that is how the 07-02 session ran v1 data despite an -IncludeData deploy.
$dataMap = @{
        "scripts\gta11y-map.json"          = "gta11y-map.json"
        "scripts\gta11y-nodes.json.gz"     = "gta11y-nodes.json.gz"
        "scripts\gta11y-junctions.json.gz" = "gta11y-junctions.json.gz"
        "scripts\gta11y-menulabels.json"   = "gta11y-menulabels.json"
        # iter-52: world data (collectibles, restricted areas, places, interactions,
        # random-event registry). Regenerate with tools\build-worlddata.py.
        "scripts\gta11y-worlddata.json"    = "gta11y-worlddata.json"
        "vehicleaihandlinginfo.meta"       = "vehicleaihandlinginfo.meta"
        # iter-58: the model-name table, replacing hashes.txt. Regenerate with
        # tools\build-modelnames.py. hashes.txt is deliberately NOT deployed any
        # more — it is an input to that generator now, not a shipped asset.
        "scripts\gta11y-modelnames.tsv.gz" = "gta11y-modelnames.tsv.gz"
        # iter-41: earcons, same story - hand-placed, never built, never deployed.
        "scripts\tped.wav"                 = "tped.wav"
        "scripts\tvehicle.wav"             = "tvehicle.wav"
        "scripts\tprop.wav"                = "tprop.wav"
        "scripts\pickup.wav"               = "pickup.wav"
        "scripts\cover.wav"                = "cover.wav"
        "scripts\interact.wav"             = "interact.wav"
        "scripts\hit.wav"                  = "hit.wav"
        "scripts\headshot.wav"             = "headshot.wav"
        "scripts\kill.wav"                 = "kill.wav"
    "scripts\door.wav"                 = "door.wav"
    "scripts\ladder.wav"               = "ladder.wav"
}

# Copy when missing or different. Length first (cheap, and settles almost every
# case), hash only when the lengths agree.
function Test-NeedsCopy($src, $dst) {
    if (-not (Test-Path $dst)) { return $true }
    $s = Get-Item $src
    $d = Get-Item $dst
    if ($s.Length -ne $d.Length) { return $true }
    return (Get-FileHash $src -Algorithm SHA256).Hash -ne (Get-FileHash $dst -Algorithm SHA256).Hash
}

$dataCopied  = 0
$dataCurrent = 0
$dataMissing = @()
foreach ($rel in $dataMap.Keys) {
    $src = Join-Path $outDir $rel
    $dst = Join-Path $ScriptsDir $dataMap[$rel]
    if (-not (Test-Path $src)) {
        Write-Warning "  missing data in build output: $rel"
        $dataMissing += $dataMap[$rel]
        continue
    }
    if (-not ($IncludeData -or (Test-NeedsCopy $src $dst))) {
        $dataCurrent++
        continue
    }
    $dstDir = Split-Path -Parent $dst
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
    try {
        Copy-Item $src $dst -Force -ErrorAction Stop
        Write-Host "  copied data $rel"
        $dataCopied++
    } catch {
        $lockedFiles += $rel
        Write-Warning "  LOCKED: $rel - is GTA V running? Close the game and re-run this script."
    }
}
Write-Host "  data: $dataCopied copied, $dataCurrent already current"

# Verify what the game will actually load. A deploy that succeeds while the
# destination has no data is the iter-58 failure verbatim, so this reports the
# INSTALLED files, not the ones we meant to install.
Write-Host ""
Write-Host "Installed data in ${ScriptsDir}:"
$absent = @()
foreach ($rel in ($dataMap.Keys | Sort-Object)) {
    $name = $dataMap[$rel]
    $dst  = Join-Path $ScriptsDir $name
    if (Test-Path $dst) {
        $i = Get-Item $dst
        Write-Host ("  {0,-32} {1,10:N0} bytes  {2:yyyy-MM-dd HH:mm}" -f $name, $i.Length, $i.LastWriteTime)
    } else {
        Write-Host "  $name  -- ABSENT"
        $absent += $name
    }
}
if ($absent.Count -gt 0) {
    Write-Warning ("Data files NOT installed: " + ($absent -join ", "))
    Write-Warning "The mod will start, announce the failure, and run degraded. Fix this before a session."
}
# iter-41: Tolk and its screen-reader clients are NATIVE and are P/Invoked by
# GTA/Tolk.cs. DllImport resolves from the executable's folder, so these go to
# the GTA V ROOT (the parent of scripts\), NOT into scripts\. Putting them in
# scripts\ appears to work only because a stale copy already sits in the root.
# Without Tolk.dll the mod has no speech at all.
$gameRoot = Split-Path -Parent $ScriptsDir
foreach ($n in @("Tolk.dll", "nvdaControllerClient64.dll", "SAAPI64.dll")) {
    $src = Join-Path $outDir $n
    if (-not (Test-Path $src)) {
        Write-Warning "  missing native in build output: $n"
        continue
    }
    $dst = Join-Path $gameRoot $n
    if (-not ($IncludeData -or (Test-NeedsCopy $src $dst))) { continue }
    try {
        Copy-Item $src $dst -Force -ErrorAction Stop
        Write-Host "  copied native $n -> game root"
    } catch {
        $lockedFiles += $n
        Write-Warning "  LOCKED: $n - is GTA V running? Close the game and re-run."
    }
}

Write-Host "NOTE: the user's ModSettings copy of vehicleaihandlinginfo.meta overrides this one."

Write-Host ""
if ($lockedFiles.Count -gt 0) {
    Write-Host "DEPLOY INCOMPLETE - $($lockedFiles.Count) file(s) were locked: $($lockedFiles -join ', ')" -ForegroundColor Red
    Write-Host "The game is still running the OLD build. Close GTA V (or reload SHVDN) and re-run."
    exit 1
}
Write-Host "Deployed to $ScriptsDir"

# This machine is the primary live-test bed. Keep its loopback-only diagnostic bridge
# enabled on every deploy. package.ps1 writes the inverse public-release configuration.
$bridgeConfig = Join-Path $ScriptsDir "gta11y-bridge.json"
# iter-71: allow_world_writes unlocks the test harness (settings.set, world.teleport,
# vehicle.spawn, scenario.*). It is a second privilege on purpose - reading a live
# session and steering it are different things - and only the test bed gets it.
@{ enabled = $true; port = 27738; allow_world_writes = $true } | ConvertTo-Json | Set-Content -Path $bridgeConfig -Encoding UTF8
Write-Host "  diagnostic bridge: ENABLED on 127.0.0.1:27738, world writes ON (test-bed deployment)"

# Tell the user exactly which iter-version banner to expect in the next log,
# straight from the source that was just built.
$srcFile = Join-Path $repoRoot "GTA\GTA11Y.cs"
$bannerLine = Select-String -Path $srcFile -Pattern 'EVENT iter-version:\s*(\S+)' | Select-Object -First 1
if ($bannerLine) {
    $iterVer = $bannerLine.Matches[0].Groups[1].Value.TrimEnd('"', ')', ';')
    Write-Host "Expected in-game banner: 'EVENT iter-version: $iterVer'"
    Write-Host "Launch GTA V, enable drive-assist debug logging, and confirm the new"
    Write-Host "driveassist-*.log banner matches - if it doesn't, the game loaded a stale DLL."
} else {
    Write-Host "Launch GTA V, enable drive-assist debug logging, and confirm the new"
    Write-Host "driveassist-*.log banner's 'EVENT iter-version:' matches the iter you just built."
}
Write-Host "A v2 data deploy also prints 'EVENT map-data:' with nodesV=2."
