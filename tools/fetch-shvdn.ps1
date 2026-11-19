<#
.SYNOPSIS
  Downloads a pinned ScriptHookVDotNet nightly build and vendors it into external\shvdn\.

.DESCRIPTION
  The mod targets SHVDN 3.7.0-nightly, and there is NO NuGet package for it — the newest
  published ScriptHookVDotNet3 package is 3.6.0 from December 2022. So the csproj cannot
  use a PackageReference; it references a vendored assembly instead, and this script is
  what puts that assembly on disk.

  The pinned tag lives in ONE place: the <ShvdnNightlyTag> property in
  GTA\GrandTheftAccessibilityRevo.csproj. This script reads it from there rather than
  keeping its own copy, so a version bump is a one-line csproj edit and the build and the
  fetch can never disagree about which nightly is expected.

  Pinning is not optional bookkeeping. The nightly assembly version carries the build
  number (3.7.0.189 for tag v3.7.0-nightly.189), so an unpinned "just grab latest" fetch
  would let a new nightly arrive silently in the middle of an iteration and turn a
  behaviour change into an unattributable one.

  external\shvdn\ is gitignored — this is a build input, not source. A fresh clone runs
  this script once before its first build.

.PARAMETER Tag
  Nightly tag to fetch, e.g. "v3.7.0-nightly.189". Defaults to the csproj's pinned tag.

.PARAMETER Force
  Re-download even when the vendored copy already matches the requested tag.

.NOTES
  Use `powershell`, not `pwsh` — same reasoning as deploy.ps1: this script has no
  PowerShell 7-only syntax, and `pwsh` is a separate install most machines do not have.

.EXAMPLE
  powershell -File tools\fetch-shvdn.ps1
.EXAMPLE
  powershell -File tools\fetch-shvdn.ps1 -Tag v3.7.0-nightly.190 -Force
#>
param(
    [string]$Tag = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj   = Join-Path $repoRoot "GTA\GrandTheftAccessibilityRevo.csproj"
$destDir  = Join-Path $repoRoot "external\shvdn"
$nightlyRepo = "scripthookvdotnet/scripthookvdotnet-nightly"

# Windows PowerShell 5.1 defaults to SSL3/TLS1.0 on some boxes; GitHub requires 1.2+.
# Without this the download fails with a bare "could not create SSL/TLS secure channel".
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- resolve the pinned tag -------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Tag)) {
    if (-not (Test-Path $csproj)) {
        Write-Error "Cannot find $csproj to read the pinned tag from. Pass -Tag explicitly."
    }
    $m = Select-String -Path $csproj -Pattern '<ShvdnNightlyTag>\s*([^<]+?)\s*</ShvdnNightlyTag>' |
         Select-Object -First 1
    if (-not $m) {
        Write-Error "No <ShvdnNightlyTag> property in $csproj. Add one, or pass -Tag explicitly."
    }
    $Tag = $m.Matches[0].Groups[1].Value
    Write-Host "Pinned tag from csproj: $Tag"
}

# --- skip if already vendored at this tag -----------------------------------
$stampFile = Join-Path $destDir "VERSION.txt"
$refDll    = Join-Path $destDir "ScriptHookVDotNet3.dll"
if ((-not $Force) -and (Test-Path $stampFile) -and (Test-Path $refDll)) {
    $have = (Get-Content $stampFile -First 1).Trim()
    if ($have -eq $Tag) {
        Write-Host "Already vendored: $Tag (use -Force to re-download)"
        exit 0
    }
    Write-Host "Vendored copy is $have, want $Tag - refreshing."
}

# --- find the release asset -------------------------------------------------
# Query the API rather than guessing the filename, so a naming change upstream
# surfaces as a clear error instead of a 404 on a URL we invented.
$apiUrl = "https://api.github.com/repos/$nightlyRepo/releases/tags/$Tag"
Write-Host "Querying $apiUrl"
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "gta11y-fetch-shvdn" }
} catch {
    Write-Error "Could not fetch release '$Tag' from $nightlyRepo. Is the tag correct? $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
if (-not $asset) {
    Write-Error "Release '$Tag' has no .zip asset. Assets found: $(($release.assets | ForEach-Object { $_.name }) -join ', ')"
}
Write-Host "Asset: $($asset.name) ($([math]::Round($asset.size / 1MB, 2)) MB)"

# --- download + extract -----------------------------------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("shvdn-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zipPath = Join-Path $tmp $asset.name

try {
    Write-Host "Downloading..."
    # ProgressPreference kills Invoke-WebRequest's per-chunk progress bar, which makes
    # a large download roughly an order of magnitude faster in Windows PowerShell 5.1.
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
    $ProgressPreference = $oldProgress

    $extractDir = Join-Path $tmp "x"
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

    $srcDll = Join-Path $extractDir "ScriptHookVDotNet3.dll"
    if (-not (Test-Path $srcDll)) {
        Write-Error "ScriptHookVDotNet3.dll not found in the archive. Layout may have changed upstream."
    }

    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }

    # The reference assembly the csproj binds against.
    Copy-Item $srcDll (Join-Path $destDir "ScriptHookVDotNet3.dll") -Force
    Write-Host "  vendored ScriptHookVDotNet3.dll"

    # XML docs, named to match the DLL so the compiler and IDE pick them up for
    # IntelliSense automatically. 2.7 MB, and worth every byte given how much of the
    # new 3.7 API surface is undocumented anywhere else.
    $srcXml = Join-Path $extractDir "Docs\ScriptHookVDotNet3.xml"
    if (Test-Path $srcXml) {
        Copy-Item $srcXml (Join-Path $destDir "ScriptHookVDotNet3.xml") -Force
        Write-Host "  vendored ScriptHookVDotNet3.xml (API docs)"
    } else {
        Write-Warning "  no Docs\ScriptHookVDotNet3.xml in the archive - IntelliSense will be bare"
    }

    # The RUNTIME half: what the player actually installs into the GTA V folder. Kept
    # so tools\package.ps1 can ship the exact nightly this build was compiled against.
    # A user on 3.6.0 cannot load a 3.7.0-targeted script at all - it fails with
    # "Unable to resolve API version 3.7.0" and, for a blind user, that is a silent
    # non-load with no audible explanation.
    $runtimeDir = Join-Path $destDir "runtime"
    if (-not (Test-Path $runtimeDir)) { New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null }
    foreach ($n in @("ScriptHookVDotNet.asi", "ScriptHookVDotNet.ini", "ScriptHookVDotNet2.dll", "ScriptHookVDotNet3.dll")) {
        $s = Join-Path $extractDir $n
        if (Test-Path $s) {
            Copy-Item $s (Join-Path $runtimeDir $n) -Force
            Write-Host "  vendored runtime\$n"
        } else {
            Write-Warning "  missing runtime file in archive: $n"
        }
    }

    $asmVer = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $destDir "ScriptHookVDotNet3.dll")).Version.ToString()
    Set-Content -Path $stampFile -Encoding utf8 -Value @(
        $Tag,
        "assemblyVersion=$asmVer",
        "asset=$($asset.name)",
        "source=https://github.com/$nightlyRepo/releases/tag/$Tag",
        "fetched=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    )

    Write-Host ""
    Write-Host "Vendored SHVDN $Tag (assembly $asmVer) into external\shvdn\"
    Write-Host "Players need this same nightly installed - a 3.6.0 install cannot load this build."
}
finally {
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
