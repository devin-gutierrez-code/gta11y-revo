# iter-61 — bisect laneTargetV2; make the next config drift self-evident

Banner: `iter61-lanetarget-bisect-driftguard`

## What happened

`driveassist-2026-08-17-083434` ran at **12.31 markers/driving-min** against iter-57's
**1.54** — the iter-52 regime. The player's read ("back to mid-thirties quality") was
correct, and it is not a marker-rate artefact: the two thrash rates, which are
marker-independent, are the worst in the whole archive.

| session | iter | mk/min | mode-change/min | poly-distrust/min |
|---|---|---|---|---|
| 08-11 | iter52 | 12.73 | 7.61 | 4.97 |
| 08-12 am | iter54 | 10.97 | 7.77 | 13.72 |
| 08-12 pm | iter55 | 7.79 | 10.23 | 4.07 |
| 08-14 | iter56 | 8.54 | 7.12 | 7.42 |
| **08-15** | **iter57** | **1.54** | **0.48** | **1.68** |
| 08-16 pm | iter60 | 4.35 | 2.18 | 4.35 |
| **08-17** | **iter60** | **12.31** | **22.43** | **22.43** |

iter-57 is the best session on all three metrics by a wide margin; 08-17 is the worst
mode-change and poly-distrust ever recorded, more than double the previous worst.

### What the screenshots settled that the numbers did not

- `sheet-0002-DAMAGE-DRAWDOWN.jpg` — the Tezeract (bumper cam, ~30 cm off the tarmac)
  runs into a **stopped black Cavalcade** in a live La Puerta Fwy lane at 29 mph. The mod
  braked correctly: `[F1490] brake-episode v0=10.46 → vEnd=0.74 aPeak=15.21
  endReason=stopped`. **The crash is not the failure.**
- `sheet-0006-IMMOBILE.jpg`, `sheet-0020-PLAYER-INDICATED-FAILURE.jpg` — the same spot
  **90 seconds later**: still on that Cavalcade, sparks off the front wheel at 2 mph, a
  bus going past on the left. Twelve incidents at one point over 112 s. **The failure is
  that the mod could not get off the car afterwards.**
- `sheet-0025-STUCK-ARM.jpg`, `snap-0030-AUTO-COLLISION.jpg` — the BATI riding into the
  Sandy Shores storefront wall at 18 mph **with the Interaction Menu open in every
  frame**. `IS_PAUSE_MENU_ACTIVE` is false for that menu, so the assist believed this was
  ordinary driving and kept firing takeover pulses at a player who was in a menu.

### The chain

1. The lane polyline sits ~6.5 m off the car while the engine says the car is on the road
   (`poly-distrust: lat=6.6 headingDelta=0.0 nativeOff=False`, 22×/min).
2. `laneKeepOk` goes false; mode flips `LaneKeeping → RecoveringToRoad`.
3. `FindBestRecoveryNode` returns empty, so [GTA11Y.cs](../../GTA/GTA11Y.cs) falls back to
   `currentDriveMode = LaneKeeping` **while `laneKeepOk` is still false**. Next scan
   re-enters recovery, finds nothing again — 40 of the session's 82 transitions, all
   logged as `-> LaneKeeping laneKeepOk=False recoveryDist=999.0`, indistinguishable from
   a recovery that worked. In iter-57 every exit read `laneKeepOk=True`.
4. The car stops against something. ACC standstill holds (`acc-release … leadClear=False`),
   `creepClearV2` refuses 1789× and fires 0 (iter-57: 7 fires), `accStandstillV2` fires
   1/166 episodes (iter-57: 260/401 — a 100× collapse in the standstill release).
5. The player floors it → `acc-release reason=player` → a one-frame lurch into the
   obstacle → hold again. That is the frame-alternating throttle/brake chatter in
   `incident-05.log` and the 1910 wheelspin frames.

### Why it was unattributable

Three driving-model gates were ON that iter-57 had OFF:

| gate | iter-57 | 08-17 | how |
|---|---|---|---|
| `laneTargetV2` | 0 | 1 | new in iter-59; `[F5670] gate-toggle … src=menu`, mid-session 08-16 pm |
| `roadSpeedGovernorV2` | 0 | 1 | toggled mid-session 08-16 pm |
| `brakePhysicsAssist` | 0 | 1 | between the 08-16 morning and evening sessions |

None is in `defaultOnSettings`; `brakePhysicsAssist` contradicts its own source comment
("stays OFF: it is the fallback physics path"). Settings persist in
`gta11ySettings.json`, so a menu toggle outlives the session that made it and every later
build inherits it silently.

## What shipped

**1. Bisect config (field, not code).** `laneTargetV2` → 0, nothing else changed.
`roadSpeedGovernorV2` and `brakePhysicsAssist` stay ON. A clean result attributes the
regression to `laneTargetV2`; a null result clears only `laneTargetV2` and the next two
sessions bisect the other two. Toggle it **before** driving — a mid-run toggle is what
made the 08-16 evening log unusable.

**2. `EVENT mode-change` now carries `why=`, `distWhy=`, `heldMs=`.** `why` names the rule
that decided the mode (`realigned` is the only healthy way out of recovery;
`no-recovery-node` is the 08-17 thrash). `distWhy` disambiguates the `999` sentinel, which
two opposite paths write. `heldMs` measures the mode being left — it is *not* derivable
from `lastModeChangeTicks`, which is stamped before the log line, so a new field was
needed.

**3. `EVENT gate-drift` at F0 + a spoken startup note.** Compares the live gate values
against the **previous session's**, persisted to `ModSettings/gta11y-gatestate.json`.
Deliberately not "ON against shipped default" — many gates are promoted over time by
being left on, so that comparison names twenty-odd entries and points at nothing; the
count is kept on the same line for the triage tool and is never spoken. A gate absent
from the previous file is a new gate, not drift.

**4. `EVENT frontend-menu` — the Interaction Menu, DARK.** The menu does not pause the
world and does not disable player control (the bike was moving), but `pi_menu.ysc` calls
`SET_INPUT_EXCLUSIVE` each frame, which takes gameplay controls away. The detector reads
vehicle-exit (75) and horn (86) — two controls the mod never writes, so a disable on them
is someone else's frontend and not our own actuation — requires both plus no pause menu
and no warning modal, and confirms over 5 frames each way. Every raw reading is in the
event line. The acting half (suspend + announce, a fourth parallel latch on the
`intoxAssistSuspend` model, with `MENU-STANDDOWN` failClass and `menuOpen=`/`menuSusp=`
header fields) sits behind **`frontendSuspendV2`, shipped OFF**: a false positive here
stands the assist down mid-drive and would read as another regression, so it earns
promotion on a session's evidence. Deliberately not a script global — those are
build-specific.

**5. Triage.** `## Read this first` now carries the two thrash rates against a
six-session median, and **"gates changed since `<previous log>`"** — which on 08-17 prints
`laneTargetV2 0→1, roadSpeedGovernorV2 0→1` in one line. `modeChangePerMin` /
`polyDistrustPerMin` added to `log-index.json` (per-minute, so they compare across session
lengths) and backfilled over the last six driven sessions. `TRACKED_GATE_SETTINGS` had
fallen four gates behind — `laneTargetV2` was ON all session and the epoch table could not
say so.

## Session protocol

1. `powershell -File tools\deploy.ps1`; confirm the next log's banner reads `iter61-…`.
2. Drive a **baseline epoch first** (`driveAssistBaseline=1` for ~2 min, then off). 08-17
   had none, so its two epochs had nothing to be read against.
3. Same two places as iter-57 and 08-17 (Sandy Shores ~`(1407,3596)`, La Puerta
   ~`(-528,-2247)`), **at least 10 driving minutes** — 3.7 min with two traps cannot move
   a per-minute rate.
4. Do not open the Interaction Menu while driving: the detector is dark this session and
   cannot stand the assist down.

## Verification

- Banner reads `iter61-…` in the new log.
- `EVENT gate-drift` at F0 names `laneTargetV2 1->0` and the startup note is spoken.
- Every `RecoveringToRoad →` transition carries `why=`/`distWhy=`/`heldMs=`.
- `EVENT frontend-menu` transitions match when the menu was actually open, **zero** during
  clean driving. Zero false positives over a full session is the bar for promoting
  `frontendSuspendV2`.
- Read out on `mode-change/min` (0.48 in iter-57 → 22.43 on 08-17) and
  immobile-under-throttle as a fraction of driving time (1.8% → 22%), not on the damage
  column: 293 of 08-17's 324.7 was two BATI incidents with the assist already
  `SUSP:3735ms` / `GRACE`.
- `accStandstillV2` fire rate returning toward 65% is the proof the standstill release is
  alive again.
