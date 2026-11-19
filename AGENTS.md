gta11y - revo: an accessibility mod for Grand Theft Auto V, for blind and visually
impaired players.

## Ground rules

- Working directory is always `c:\dev\gta11y`.
- Build with the dotnet SDK (`dotnet build`, `dotnet publish`).
- Work on the **testing** branch. Do not commit to or touch **main** unless prompted.
- Do not commit or push to testing without prompting me first.
- Target is **ScriptHookVDotNet 3.7.0-nightly or later**. The nightly is not on NuGet;
  it is vendored into `external\shvdn\` by `tools\fetch-shvdn.ps1` and pinned by
  `<ShvdnNightlyTag>` in the csproj. A 3.6 install cannot load this build at all — and
  the symptom is silence, which for this audience is the worst failure mode there is.
- Prefer the managed API where one exists; raw `Function.Call` is the fallback, not the
  default. Nightly roughly doubled the v3 surface — check for a wrapper before hand-rolling
  a native. `docs/shvdn-nightly-migration.md` has the deprecation backlog and a survey of
  what nightly newly exposes.
- **Everything this mod says is heard, not seen.** A wrong label is worse than no label,
  and a feature that fails silently is indistinguishable from one that was never installed.
  Fail closed, and say so out loud.

## The iteration loop

1. **Pick the number.** The source of truth is the `EVENT iter-version:` banner in
   `GTA/GTA11Y.cs` — grep it, add one. Never guess from memory or from a plan file; that
   is how iter-46 and iter-47 both got claimed twice.
2. **New gates ship OFF**, one per session, so a session's result is attributable to one
   change. Exceptions are instrumentation that changes no behaviour (the snapshot gates)
   and safety watchdogs. Anything that touches the driving model ships OFF.
3. **Build → deploy → verify the banner.** `dotnet build` alone never reaches the game:
   `powershell -File tools\deploy.ps1`, then confirm the next log's banner matches what
   you built. A mismatch means the game loaded a stale DLL and the session is worthless.
4. **Field session**, then triage (below), then record the verdict.
5. **Package**: `powershell -File tools\package.ps1` refreshes `dist\` for the new iter.

Per-iteration narrative goes in `CHANGELOG.md` and `docs/plans/`. Do not create a memory
file per iteration — merge the durable facts into the relevant topic memory instead.

## Drive-assist log triage

Raw logs run to 80+ MB. **No agent, including the main session, may read a driveassist log
wholesale.** The triage artifacts are the source of truth; the raw log is a
targeted-Grep-with-head_limit-only fallback.

**Stage 0 (deterministic, zero agents, zero raw-log reads):** run
`python tools\triage-log.py` (defaults to the newest log). It writes `triage\<log-stem>\`:

- `digest.md` — opens with **`## Read this first`**, ~20 lines: failure rate against a
  median baseline, worst incidents, which images to open, and any caveat that makes the
  rest less trustworthy than it looks. **The main session reads that block and stops.**
  Everything below it is detail for agent 1.
- `incidents.md`, `incident-NN.log` (compact excerpt), `incident-NN-frames.log` (raw ±3 s
  FRAME slice — read selectively with offset/limit), `events.log`
- `snaps\` — screenshots, contact sheets, and `manifest.md`
- appends a trend row to `tools\log-index.json`

**Agent 1** works from `digest.md` + `incidents.md` + the `incident-NN.log` excerpts, at
max effort and thinking, on the causes of player-indicated failures and any other failure
(vehicle-health drops, auto-collision indications). Escalate to `incident-NN-frames.log` or
targeted Grep on the raw log only for questions the excerpts cannot answer.

**Agent 1b (logging/debug blind-spot auditor)** runs in parallel: audits the debug-logging
code in `GTA\GTA11Y.cs` against the digest (especially the unmarked-damage sweep),
`events.log` and the excerpts, for failures the auto-detectors missed, snapshot/FRAME
fields that are stale or uninformative at the marked failures, and failure moments that
cannot be diagnosed from the artifacts at all. Recommends debug-code fixes.

**Agent 2 (synthesis)** plans corrections for BOTH drive-assist behaviour and the debug
logging, with web research where needed. Failure-rate trend comes from
`tools\log-index.json` — never re-scan previous logs.

**Agent 3** sanity-checks agents 1/1b/2, reality-checks their claims, writes the plan file.

### The artifact contract

This is version-free on purpose. It describes what the artifacts mean, not which iteration
introduced them.

- **`snaps\manifest.md` is the entry point for images, not the directory listing.** It
  flags at most 8 as recommended, ranked by settled cost and then spread across failure
  tags — eight frames of one failure kind teach less than eight of eight kinds. Read the
  recommended set; escalate to other frames only for a specific question.
- **Prefer `sheet-*.jpg` over the individual frames.** A contact sheet is the run-up plus
  the failure in one image, labelled `t-4s … FAILURE`. Five reads become one, and the
  approach reads as motion instead of four unrelated stills.
- **READ THE IMAGES.** The Read tool renders them. One frame settles "wedged on a kerb" vs
  "nose-in to a wall" vs "boxed in by traffic" — a question that numbers alone did not
  separate in 24 iterations.
- **In the scene-geometry ray fan, `refused` and `clear` are NOT the same.** `refused`
  means the shape test did not run. Reading it as open road is exactly how a wedge
  disappears.
- Snapshot markers fire at **every** failure emitter, not just AUTO-COLLISION and
  PLAYER-INDICATED-FAILURE. The tag appears in the incident line (`#12A:STUCK-LAYER`).
- `suppressedSinceLast=N` on a marker means N further firings of that tag were rate-limited
  while the car had not moved. A large number is itself a signal.
- `EVENT capture-*` lines are the instrumentation's own health report. `capture-selftest`
  missing means the writer never started; `capture-cap-hit` means the image budget ran out
  and the artifact set is incomplete despite looking whole; `dropPreroll`/`dropTagQuota` in
  a census are the budget working as designed, not a fault.

Absence of images is normal on older logs and is reported honestly by the digest — it is
never something to infer a clean session from.

## Menu/phone log triage

Menulogs (`menulog-*.log`, 3–140 KB) are small enough to read directly, but the digest is
still the entry point because a coverage table is a different artifact from a transcript.

`python tools\triage-menulog.py` writes `triage\<log-stem>\`:

- `digest.md` — named-vs-visited coverage table, session health (probe error fuses,
  open-flicker), input→speech latency, and a **version-segmented** trend
- `gaps.md` — **the actionable list**: every row the reader could not name, ranked by how
  often it was hit, with the fix for each kind and the screenshot to look at. This is a
  direct input to `tools\build-menulabels.py`, replacing hand-transcription from raw logs.
- `snaps\` + `manifest.md` — gap frames ranked above coverage frames
- appends to `tools\menulog-index.json`

**Always segment menulogs by reader version before counting anything.** The archive spans
8 versions (spike1 → reader1.5) and they do not name the same things, so a mixed comparison
makes a coverage improvement look like a regression. The index is keyed on the version
banner and the trend table excludes other versions rather than merging them.

Menu snapshots fire on `SETTINGS-UNMAPPED`, `PANE-NODATA`, `ACCEPT-NOOP`,
`PHONE-UNMAPPED-HOME`, `PHONE-UNMAPPED-ROW` and `PANE-FIRST-VISIT`, deduped by the identity
of what failed. Reading them is a developer capability, not the player's — which makes it
the only way anyone in this loop can see the label that failed to map.

Both triage tools share `tools\logfmt.py`. After touching it, `python tools\triage-log.py
--selftest` and `python tools\triage-menulog.py --selftest` must both pass.

## Regenerating bundled data

- `tools\build-worlddata.py` → `gta11y-worlddata.json` (collectibles, restricted areas,
  Strangers & Freaks points, interactables, random-event registry)
- `tools\build-menulabels.py` → `gta11y-menulabels.json` (pause menu + phone label tables);
  `tools\align-panes.py --write` fills pane row positions
- `tools\build-map-data.py` → the map/node/junction graph
- `tools\build-modelnames.py` → `gta11y-modelnames.tsv.gz` (model hash → spoken name for
  peds, props, doors and gates). Sources: `DurtyFree/gta-v-data-dumps` (`peds.json`,
  `ObjectList.ini`, `VehicleList.ini`) merged with the legacy `External Resources\hashes.txt`.
  `--refresh` re-downloads; `--verify` runs the post-build checks and exits non-zero.
  Hand fixes go in `tools\modelnames-overrides.json`, never in the generated file.

  **After a DLC drops**, one command does it:

  ```
  python tools\build-modelnames.py --refresh --verify
  ```

  `--check` (iter-60) is the staleness signal — it asks the GitHub API whether the three
  source files moved since the table's `#generated` stamp, prints the upstream commit
  subject (which names the build, e.g. `mp2025_02 / b3717 / A Safehouse In The Hills`),
  and exits non-zero when stale. It downloads nothing. An unreachable API exits non-zero
  too: not knowing is not the same as being current. Upstream re-dumps within days of
  each DLC, roughly twice a year.

  **`m`-flagged rows** (iter-60, schema v2) are models whose name the generator would not
  read out loud — map archetypes (`Ch3 07 Decals50`) and raw model stems (`Isldj`). They
  keep their hash, so coverage is unchanged and the gaps log stays clean, but the runtime
  describes them instead: a ped by type and sex, a prop by size, a door as "door or gate".
  They are listed in the generated `tools\modelnames-review.txt`, which is the worklist
  for the overrides file. Sayability is judged on the **final composed string**, and
  `--verify` fails if any unflagged row still reads as a machine name.

  **Vehicles are deliberately absent from that table** and `--verify` fails if any appear.
  The game names them itself (`Vehicle.LocalizedName` → `DisplayName` → class), localized,
  for every DLC ever shipped; a bundled vehicle name could only ever be staler than the
  game's own. The same is true of weapons (`Weapon.GetDisplayNameFromHash`).

  **hashes.txt is retired as a shipped asset** (iter-58) and is now only an input to this
  generator. It was hand-placed 2022-12-21, contained zero ambient pedestrian models, and
  was read through `Model.NativeValue` — a `UInt64`, so every negative model hash
  sign-extended and could never match its own row. **Key model tables on `Model.Hash`
  (Int32), never `NativeValue`.** The generator's coverage guard fails the build if a
  regeneration would name fewer models than the file it replaces.

  What the mod could not name during a session lands in `gta11y-modelgaps-<date>.log` at
  the game root — the same role `gaps.md` plays for the menu reader, and the input to the
  next regeneration.

**Mining Rockstar's decompiled scripts** (the source for world data): repo
`calamity-inc/GTA-V-Decompiled-Scripts`, branch **`senpai`**. `curl` individual files — do
not clone, it is enormous. Parse the tables; do not transcribe them by hand. Hand-transcribing
the random-event ids got them wrong (`re_securityvan` is id 9, not 8; id 8 is
`RE_BIKETHIEFSTAMP` and has no script).

## Packaging

`powershell -File tools\package.ps1` builds Release and stages
`dist\gta11y-<iter>-testing\` — a `GTA-V-folder\` laid out like a real install, the live
triage tools, `AGENTS.md`, and a generated `INSTALL.md` stamped with the iter and the exact
required SHVDN nightly tag. `dist\` is gitignored; refresh it once per iteration.

It deliberately does not bundle ScriptHookVDotNet — that is someone else's binary, so
INSTALL.md names the required tag and links the official download instead.
