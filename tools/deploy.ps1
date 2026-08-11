<#
.SYNOPSIS
  Deploys the built GTA11Y mod into GTA V's ScriptHookVDotNet `scripts` folder.

.DESCRIPTION
  `dotnet build` only writes bin\Release\net48\ — the game never sees it until the
  DLL is copied into <GTA V>\scripts\. That missing step is why iter-25 and iter-26
  were built but never tested (the in-game build stayed at iter-24). Run this after
  building to actually deploy, then confirm the in-game drive-assist log banner
  reads the expected iter-version.

  By default copies only the managed DLLs (the DLL is the only thing that changes
  per build). Pass -IncludeData on a first install (or when the bundled map/handling
  data changed) to also copy the data files.

.PARAMETER ScriptsDir
  The GTA V `scripts` folder, e.g. "C:\Program Files\Rockstar Games\Grand Theft Auto V\scripts".
  Falls back to the GTA5_SCRIPTS_DIR environment variable if omitted.

.PARAMETER Configuration
  Build configuration to deploy from. Omit to auto-detect: whichever of
  Debug/Release contains the newest built DLL wins (dotnet build defaults to
  Debug, so a bare build-then-deploy now just works).

.PARAMETER IncludeData
  Also copy gta11y-map.json, gta11y-nodes.json.gz, gta11y-junctions.json.gz,
  gta11y-menulabels.json, gta11y-worlddata.json, vehicleaihandlinginfo.meta and
  hashes.txt.

.EXAMPLE
  pwsh tools\deploy.ps1 -ScriptsDir "D:\Games\GTAV\scripts"
.EXAMPLE
  $env:GTA5_SCRIPTS_DIR = "D:\Games\GTAV\scripts"; pwsh tools\deploy.ps1
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

if ($IncludeData) {
    # Mirror the bundled data next to the DLL (first install / data changes only).
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
        # iter-41: the object/vehicle name table. Was hand-placed in 2022 and
        # never carried by any build or deploy, so a clean install lost vehicle
        # and object naming with only a spoken warning to show for it.
        "scripts\hashes.txt"               = "hashes.txt"
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
    foreach ($rel in $dataMap.Keys) {
        $src = Join-Path $outDir $rel
        $dst = Join-Path $ScriptsDir $dataMap[$rel]
        if (Test-Path $src) {
            $dstDir = Split-Path -Parent $dst
            if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
            try {
                Copy-Item $src $dst -Force -ErrorAction Stop
                Write-Host "  copied data $rel"
            } catch {
                $lockedFiles += $rel
                Write-Warning "  LOCKED: $rel - is GTA V running? Close the game and re-run this script."
            }
        } else {
            Write-Warning "  missing data in build output: $rel"
        }
    }
    # iter-41: Tolk and its screen-reader clients are NATIVE and are P/Invoked by
    # GTA/Tolk.cs. DllImport resolves from the executable's folder, so these go to
    # the GTA V ROOT (the parent of scripts\), NOT into scripts\. Putting them in
    # scripts\ appears to work only because a stale copy already sits in the root.
    # Without Tolk.dll the mod has no speech at all.
    $gameRoot = Split-Path -Parent $ScriptsDir
    foreach ($n in @("Tolk.dll", "nvdaControllerClient64.dll", "SAAPI64.dll")) {
        $src = Join-Path $outDir $n
        if (Test-Path $src) {
            try {
                Copy-Item $src (Join-Path $gameRoot $n) -Force -ErrorAction Stop
                Write-Host "  copied native $n -> game root"
            } catch {
                $lockedFiles += $n
                Write-Warning "  LOCKED: $n - is GTA V running? Close the game and re-run."
            }
        } else {
            Write-Warning "  missing native in build output: $n"
        }
    }

    Write-Host "NOTE: verify the mod loads data from this layout in-game; the user's"
    Write-Host "      ModSettings copy of vehicleaihandlinginfo.meta overrides this one."
}

Write-Host ""
if ($lockedFiles.Count -gt 0) {
    Write-Host "DEPLOY INCOMPLETE - $($lockedFiles.Count) file(s) were locked: $($lockedFiles -join ', ')" -ForegroundColor Red
    Write-Host "The game is still running the OLD build. Close GTA V (or reload SHVDN) and re-run."
    exit 1
}
Write-Host "Deployed to $ScriptsDir"

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
