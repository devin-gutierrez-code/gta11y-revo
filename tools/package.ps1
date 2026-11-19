<#
.SYNOPSIS
  Builds and stages a per-iteration testing package into dist\.

.DESCRIPTION
  `dist\` used to be assembled by hand. It drifted: it sat at iter41 while the source
  was at iter53, and the copy of triage-log.py it shipped was twelve iterations stale.
  This script makes refreshing it one command, so it can be part of the iteration loop
  instead of a thing someone remembers to do.

  It produces dist\gta11y-<iter>-testing\ containing:
    GTA-V-folder\           laid out exactly like a GTA V install, so the whole thing
                            is one merge-copy for the person receiving it
    tools\                  the LIVE triage tools, not a snapshot of them
    INSTALL.md              generated, stamped with this iter and the exact SHVDN
                            nightly tag the DLL was compiled against
    CLAUDE.md               the project's operating instructions

  and then an archive beside it.

  What it deliberately does NOT ship is ScriptHookVDotNet itself. That is someone
  else's binary; INSTALL.md names the exact required tag and links the official
  download instead.

.PARAMETER Configuration
  Build configuration. Release by default — a testing build handed to someone else
  should not be a Debug build.

.PARAMETER NoBuild
  Package whatever is already in bin\ instead of rebuilding.

.PARAMETER NoArchive
  Skip the .7z/.zip step and leave only the folder.

.NOTES
  Use `powershell`, not `pwsh` — see deploy.ps1 for why.

.EXAMPLE
  powershell -File tools\package.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$NoArchive
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj   = Join-Path $repoRoot "GTA\GrandTheftAccessibilityRevo.csproj"
$srcFile  = Join-Path $repoRoot "GTA\GTA11Y.cs"

# ---- identify the build -----------------------------------------------------
# The iter banner is the source of truth for what this build IS, and it is the same
# string the user will hear confirmed in the next log. Reading it here means the
# package name, INSTALL.md and the in-game banner cannot disagree.
$m = Select-String -Path $srcFile -Pattern 'EVENT iter-version:\s*([A-Za-z0-9._-]+)' | Select-Object -First 1
if (-not $m) { Write-Error "No 'EVENT iter-version:' banner found in $srcFile." }
$iter = $m.Matches[0].Groups[1].Value

$t = Select-String -Path $csproj -Pattern '<ShvdnNightlyTag>\s*([^<]+?)\s*</ShvdnNightlyTag>' | Select-Object -First 1
if (-not $t) { Write-Error "No <ShvdnNightlyTag> in $csproj." }
$shvdnTag = $t.Matches[0].Groups[1].Value

Write-Host "Packaging $iter (built against SHVDN $shvdnTag)"

# ---- build ------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "Building $Configuration..."
    & dotnet build $csproj -c $Configuration -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed. Not packaging a broken build." }
}
$outDir = Join-Path $repoRoot "GTA\bin\$Configuration\net48"
if (-not (Test-Path $outDir)) { Write-Error "No build output at $outDir." }

# ---- stage ------------------------------------------------------------------
$pkgName = "gta11y-$iter-testing"
$pkgRoot = Join-Path $repoRoot "dist\$pkgName"
if (Test-Path $pkgRoot) { Remove-Item $pkgRoot -Recurse -Force }
$gameRoot  = Join-Path $pkgRoot "GTA-V-folder"
$scriptDir = Join-Path $gameRoot "scripts"
$toolsDir  = Join-Path $pkgRoot "tools"
foreach ($d in @($scriptDir, $toolsDir)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$missing = @()
function Copy-Required($src, $dst, $label) {
    if (Test-Path $src) {
        Copy-Item $src $dst -Force
        return $true
    }
    $script:missing += $label
    Write-Warning "  MISSING: $label"
    return $false
}

# Managed DLLs -> scripts\. ScriptHookVDotNet3.dll is NOT among them: SHVDN ships its
# own and overwriting it from here is actively harmful. deploy.ps1 makes the same
# exclusion for the same reason.
Write-Host "Staging scripts\..."
foreach ($f in @("GrandTheftAccessibilityRevo.dll", "CSCore.dll", "NAudio.dll", "Newtonsoft.Json.dll")) {
    Copy-Required (Join-Path $outDir $f) (Join-Path $scriptDir $f) $f | Out-Null
}

# Bundled data + earcons -> scripts\, FLAT. The build puts these under bin\...\scripts\,
# and the mod resolves them relative to the game root, so they must land directly in
# scripts\ and never in scripts\scripts\ — the exact mistake that made the 07-02
# session run v1 map data despite a data deploy.
$dataFiles = @("gta11y-map.json", "gta11y-nodes.json.gz", "gta11y-junctions.json.gz",
               "gta11y-menulabels.json", "gta11y-worlddata.json",
               # iter-58: replaces hashes.txt. See tools\build-modelnames.py.
               "gta11y-modelnames.tsv.gz",
               "tped.wav", "tvehicle.wav", "tprop.wav", "pickup.wav", "cover.wav",
               "interact.wav", "hit.wav", "headshot.wav", "kill.wav", "door.wav", "ladder.wav")
foreach ($f in $dataFiles) {
    Copy-Required (Join-Path $outDir "scripts\$f") (Join-Path $scriptDir $f) "scripts\$f" | Out-Null
}
Copy-Required (Join-Path $outDir "vehicleaihandlinginfo.meta") (Join-Path $scriptDir "vehicleaihandlinginfo.meta") "vehicleaihandlinginfo.meta" | Out-Null

# Native libraries -> the GAME ROOT, next to GTA5.exe. DllImport resolves beside the
# executable, so these do NOT go in scripts\. Without Tolk.dll the mod has no speech
# at all, which for this audience is total failure rather than a degraded mode.
Write-Host "Staging native libraries (game root)..."
foreach ($f in @("Tolk.dll", "nvdaControllerClient64.dll", "SAAPI64.dll")) {
    Copy-Required (Join-Path $outDir $f) (Join-Path $gameRoot $f) $f | Out-Null
}

# The LIVE triage tools. The old hand-built dist shipped a triage-log.py from twelve
# iterations earlier, which silently produced a digest missing every section added
# since. Copying from the working tree is the whole point.
Write-Host "Staging tools\..."
foreach ($f in @("triage-log.py", "triage-menulog.py", "logfmt.py")) {
    Copy-Required (Join-Path $repoRoot "tools\$f") (Join-Path $toolsDir $f) "tools\$f" | Out-Null
}
foreach ($f in @("gta11y-cli.py", "test-bridge.py")) {
    Copy-Required (Join-Path $repoRoot "tools\$f") (Join-Path $toolsDir $f) "tools\$f" | Out-Null
}
$mcpDir = Join-Path $toolsDir "mcp"
New-Item -ItemType Directory -Force -Path $mcpDir | Out-Null
Copy-Required (Join-Path $repoRoot "tools\mcp\gta11y_mcp.py") (Join-Path $mcpDir "gta11y_mcp.py") "tools\mcp\gta11y_mcp.py" | Out-Null
Copy-Required (Join-Path $repoRoot "CLAUDE.md") (Join-Path $pkgRoot "CLAUDE.md") "CLAUDE.md" | Out-Null

# Public/test-recipient packages must never expose an unauthenticated command channel
# unless the recipient deliberately edits this file. Local deploy.ps1 writes enabled=true.
@{ enabled = $false; port = 27738; allow_world_writes = $false } | ConvertTo-Json | Set-Content -Path (Join-Path $scriptDir "gta11y-bridge.json") -Encoding UTF8

# ---- INSTALL.md -------------------------------------------------------------
$built = Get-Date -Format "yyyy-MM-dd"
$nScripts = (Get-ChildItem $scriptDir -File).Count
$install = @"
# GTA11Y Revo — $iter testing build

An accessibility mod for Grand Theft Auto V, for blind and visually impaired
players. This is a **testing build**, not a general release.

Built $built. In-game banner: ``$iter``.

---

## 1. What you need first

Install these in order. Both are free.

**1. ScriptHookV** (by Alexander Blade)
Download: http://www.dev-c.com/gtav/scripthookv/

ScriptHookV must match your game's build number. If Rockstar updates GTA V,
ScriptHookV stops working until Alexander Blade posts an update. That is normal
and there is no way around it.

From that download, copy into your **GTA V main folder** (the one with
``GTA5.exe``):

- ``ScriptHookV.dll``
- ``dinput8.dll``

**2. ScriptHookVDotNet — a 3.7.0 NIGHTLY build**
Download: https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases

This build was compiled against **$shvdnTag**. Use that release or a newer
nightly.

> **This is a hard requirement.** The 3.6 stable release **cannot load this mod at
> all**. It does not run with pieces missing — ScriptHookVDotNet refuses the script
> outright, and the only symptom is silence: no startup announcement, nothing.
> If the mod seems completely absent, check this first. ``ScriptHookVDotNet.log``
> in your GTA V main folder will say ``Unable to resolve API version 3.7.0``.

From that download, copy into your **GTA V main folder**:

- ``ScriptHookVDotNet.asi``
- ``ScriptHookVDotNet3.dll``

(``ScriptHookVDotNet2.dll`` is optional and only needed by other, older mods.)

**3. Microsoft .NET Framework 4.8**
Already present on any up-to-date Windows 10 or 11.

---

## 2. Where the GTA11Y files go

This package contains a folder named ``GTA-V-folder``, laid out exactly like your
GTA V installation.

**Copy the contents of ``GTA-V-folder`` into your GTA V main folder** — the one
holding ``GTA5.exe``. Merge, and allow overwriting.

A typical Steam path is:

``C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\``

That puts files in two places, and the split matters.

### Three native libraries, directly in the GTA V main folder

These sit next to ``GTA5.exe``, **not** in ``scripts``, because Windows looks for
native libraries beside the running program.

1. ``Tolk.dll`` — screen reader output. **Without this the mod cannot speak at all.**
2. ``nvdaControllerClient64.dll`` — lets Tolk talk to NVDA
3. ``SAAPI64.dll`` — lets Tolk talk to Dolphin and Supernova

With only ``Tolk.dll`` the mod still speaks, but falls back to SAPI, so NVDA and
Dolphin users lose their own voice and rate settings. Install all three.

### $nScripts files in the ``scripts`` sub-folder

The mod itself, its audio and JSON libraries, the map and menu data, the vehicle
handling table, the name lookup, and the ``.wav`` earcons.

Everything in ``scripts`` goes **flat** — do not create sub-folders, and do not
end up with ``scripts\scripts\``.

Do **not** move ``ScriptHookVDotNet3.dll`` into ``scripts``; it belongs in the main
GTA V folder and ships with ScriptHookVDotNet.

---

## 3. Check that it loaded

1. Start GTA V in **Story Mode**.
2. Listen for the mod's startup announcement.

If nothing happens, open ``ScriptHookVDotNet.log`` in your GTA V main folder — it
names the script that failed and why. The most likely cause is a 3.6 install; see
section 1.

**Never load this in GTA Online.** ScriptHookV disables itself there, and modding
online risks a ban.

---

## 4. Settings

Settings live in:

``Documents\Rockstar Games\GTA V\ModSettings\gta11ySettings.json``

They are all editable in-game from the mod's own Settings menu, which speaks.

---

## 5. Reporting a problem

**Driving:** turn on ``Drive Assist Debug Logging`` in Settings, play, and press
**F1** the moment something goes wrong — that drops a marker in the log. Failure
screenshots are on by default; they need the game in **borderless windowed** mode,
because Windows cannot capture an exclusive-fullscreen frame.

**Menus or the phone:** turn on ``Menu Debug Logging`` and walk the menu or app
that misbehaved.

Both write to ``Documents\Rockstar Games\GTA V\ModSettings\``. Send the whole
``.log`` file, and the ``-snaps`` folder beside it if there is one.

The ``tools`` folder in this package digests those logs without anyone having to
read them:

``````
python tools\triage-log.py           # newest drive-assist log
python tools\triage-menulog.py       # newest menu/phone log
``````

Needs Python 3 and, for the screenshot contact sheets, Pillow (``pip install pillow``).
"@
Set-Content -Path (Join-Path $pkgRoot "INSTALL.md") -Value $install -Encoding utf8

# ---- archive ----------------------------------------------------------------
if (-not $NoArchive) {
    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue)
    if ($sevenZip) {
        $archive = Join-Path $repoRoot "dist\$pkgName.7z"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        & $sevenZip.Source a -t7z $archive $pkgRoot | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "Archive: dist\$pkgName.7z" }
        else { Write-Warning "7z failed; the folder is still complete." }
    } else {
        $archive = Join-Path $repoRoot "dist\$pkgName.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Compress-Archive -Path $pkgRoot -DestinationPath $archive -Force
        Write-Host "Archive: dist\$pkgName.zip  (7z not on PATH, used zip)"
    }
}

# ---- report -----------------------------------------------------------------
Write-Host ""
if ($missing.Count -gt 0) {
    # Say what is absent rather than shipping a quietly incomplete package. A
    # missing earcon is cosmetic; a missing Tolk.dll is a mod that cannot speak.
    Write-Host "PACKAGE INCOMPLETE - $($missing.Count) file(s) missing:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Build with -IncludeData equivalents present, or fix the csproj <None> items."
} else {
    Write-Host "Packaged $pkgName" -ForegroundColor Green
}
Write-Host "  folder : dist\$pkgName"
Write-Host "  banner : $iter"
Write-Host "  SHVDN  : $shvdnTag (players need this nightly or newer)"
