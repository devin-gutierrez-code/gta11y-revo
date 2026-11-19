# iter-59 — pick a lane, and fix the beep that never stops

Source session: `driveassist-2026-08-15-150525` (92 MB, 20.8 driving min, banner
`iter57-authority-recovery-chain`).

## What the session said

**It is the best drive-assist session on record, and it is not close.** Five independent
metrics moved together against the previous session:

| metric | iter-56 (08-14) | iter-57 (08-15) | change |
|---|---|---|---|
| driving minutes | 9.8 | **20.8** | 2.1x longer |
| markers / driving-min | 8.54 | **1.54** | 5.5x fewer |
| health drawdown | 816.0 | **15.0** | 54x less |
| damage episodes | 8 | **2** | |
| throttle holdFrac | 25% | **7%** | |
| immobile-under-throttle | 67.2 s | **22.2 s** | 3x less |

Total settled damage 7.6, all of it in one incident; end health `bh=992 eh=1000`. A
CLIFFHANGER motorcycle rode from Sandy Shores to Vinewood Hills essentially intact.
**iter-57's authority-recovery work is the cause and nothing in iter-59 touches it.**

(The table uses **1.54**, the raw rate. The digest headline's 1.44 is de-duplicated and was
being compared against a median built from raw rates — not like-for-like.)

So iter-59 is the first iteration that works on *quality of the drive* rather than on
stopping the car destroying itself. The player named both remaining defects:

1. *"Sometimes we're still varying close to the edge of lanes, but this is a lot more rare."*
2. *"Debug beeps that seem to play every frame or so while drive assist is on."*

## Finding 1 — the mod aims at the lane divider on multi-lane roads

Lane centering was ON (`poly-offset applied=True`, all 1017 samples, `offset=2.10`).
Measured lateral error (`carLat`): mean **−0.01 m**, mean |error| **1.51 m**, p95 **3.83 m**,
range −8.42…+8.28. **26.2% of samples >2 m off target** — a GTA lane is 4.2 m, so >2 m is at
or past the lane edge, a quarter of the time. Mean ≈ 0 with that spread means the target line
is wrong and tracking is loose, not that the car sits on one side.

**The decisive bug.** `BuildSteerPolyline` offset only when `biasBwd > 0 && biasFwd > 0`,
justified as *"a divided carriageway (bwd==0) already runs per-roadway."* True for a ONE-lane
roadway, where the node line is the lane centre. False for a TWO-lane roadway, where the node
line sits exactly on the divider.

| fwd/bwd | samples | offset | correct? |
|---|---|---|---|
| 1/1 | 447 (44%) | 2.10 m right | yes |
| **2/0** | **280 (27.5%)** | **0.00 m** | **no — aims at the lane divider** |
| 1/0 | 135 (13%) | 0.00 m | yes |
| 2/2 | 96 (9%) | 2.10 m right | yes |

`snaps/snap-0022-PLAYER-INDICATED-FAILURE.jpg` — the frame the **player flagged as a lane
problem** — is the bike riding the dashed line of a multi-lane road with a semi-trailer
alongside. That is `fwd=2,bwd=0` as a photograph.

**Second bug: the on-road back-off guard ran for vertex 0 only**, while Stanley aims at the
lookahead vertices (6 hops / 80 m), which took the full offset unchecked.
`snaps/snap-0003-OFFROAD-FLOOR.jpg` is the bike at 33 mph fully off the tarmac on the dirt
shoulder, road curving away left.

**Controller headroom.** Stanley's cross-track term saturates on **11.3%** of scans (115/1017
at the 0.35 rad cap). Curvature feed-forward is effectively dead: zero on **30.8%** of scans,
and where non-zero averaging **0.0095 rad against a 0.25 rad cap — 3.8% of budget**. Probable
cause, specific to this session: the feed-forward is wheelbase-scaled and
`GEOM_WHEELBASE_MIN = 2.0 m` clamps every motorcycle up from its real ~1.4–1.6 m
(`vehicle-geometry` logged `CLIFFHANGER wheelbase=2.00` — the clamp, not a measurement), on a
session that was 84% bike.

## Finding 2 — the sine beep was a bug, not a debug tone

It is the **steer-threat urgency cue**. The call site tested `lastThreatCueTicks` and stamped
`lastAssistAnnounceTicks` instead; the only write to `lastThreatCueTicks` in the whole file is
the emergency-brake branch. So `Now.Ticks - 0` ≈ 6.4e17, the 2 s gate never closed, and the
80 ms sample restarted every frame (~44/s) — never playing to completion, so the pitch that
carries the entire message was inaudible as pitch.

The speech shared that gate. `"Handbrake turn"` was the one callout with no throttle of its
own and was spoken **55 times**.

## Finding 3 — `CONTACT-*` did not mean contact

`NearestContactLabel()` starts at `best = 999f` with no distance bound. As the failClass
fall-through, it labelled anything the scanner could see at any range: marker #2 was
`CONTACT-C/vehicle` with the vehicle **33.4 m** away, and **24 of 32 markers** carried a
`CONTACT-*` class. The failure table every iteration is ranked on read as a collision list and
was not one.

## Finding 4 — `AUTHORITY-BLACKOUT` is detect-only and wrong on a bike

Detect-and-announce only — no fallback channel, no recovery. De-facto gated on debug logging
(it needs `achValid`, set only inside `actuationFeedbackV2`, inside `driveLogger.IsRunning`).
And its dead-steering test reads `veh.SteeringAngle`, a **front-wheel** angle, while a GTA
motorcycle turns by lean: two of three fires were a CLIFFHANGER in LaneKeeping at `layer=0`
with `tempAction=0`. Nothing was spoken only because all three held ~200 ms, under the 1 s
threshold.

## What shipped

### A — the beep (ON; bug fix + user request)
- **A1/A2** `PlaySteerCue` owns its own interval, mirroring `PlayBrakeWarning`'s
  compute→compare→**stamp** shape. Interval scales 900 ms → 250 ms with the same urgency the
  pitch encodes (400→1000 Hz). The emergency-brake stamp still suppresses the steer cue.
- **A3** New setting **`assistBeeps`, default ON** — gates the steer cue, the pre-impact brake
  warn and the ACC block cue. **Speech is never gated**; the nav-assist zone beeps keep
  `navigationAssist` / `navAssistVerbosity`.
- **A4** `"Handbrake turn"` gets a 4 s throttle, placed *inside* the branch so a throttled
  handbrake turn still swallows the "Braking"/"Slowing" arms rather than falling through.
- **A5** `AUTHORITY-BLACKOUT` no longer **speaks** on a two-wheeler; `class=` and `wheelCount=`
  added to the event. Logging and snapshots continue on bikes — that data is what a lean-aware
  threshold would be built from.

### B — `laneTargetV2` (the one behaviour gate, ships **OFF**)
- **B1** `bwd == 0 && fwd >= 2` now offsets to the innermost forward lane's centre.
- **B2** the `IsPositionOnRoad` back-off ladder applies to every vertex, not just vertex 0.
- **B3** `poly-offset` now reports the split source so B3a/B3b can be decided from data.

### C — instrumentation (ON, behaviour-neutral)
- **C1** `poly-offset` publishes `biasFwd`/`biasBwd`/`splitSrc` — the counts the decision
  *used* and their provenance. Graph and engine agreed on only 63.2% of samples and the line
  printed the graph's either way.
- **C2** `poly-lat` gains `crossSat`/`ffSat`/`cteSat`.
- **C3** failClass falls through to `NEAREST-*` beyond `2.5 m + vehicle half-length`;
  `CONTACT-*` now means contact. `NearestContactLabel()` stays unbounded for its other callers.
- **C4** `capture-final` moved **above** `driveLogger.Stop()` in `onAborted` — iter-57 J
  inserted it 45 lines *below* the call that disables the logger, so it was dead on arrival and
  08-15 has zero such lines. Plus `capture-census` on the existing 30 s heartbeat.
- **C5** A **final gate census** at session end. The census samples every 30 s, so any gate
  first firing in the last <30 s reads as never-fired: on 08-15 the last census was F55372 and
  `tempActionV2`'s only fire was F55985. The triage verdict now prints `NO-FIRE-COUNTED — but
  N matching event(s) exist` instead of asserting absence over its own contrary evidence.
- **C6** `edgeStat` no longer collapses `implausible:<w>` to a bare `implausible`. All 33,128
  discards are exactly `implausible:0.0` — both boundary calls returning the same point. The
  "refusal rate" headline is renamed; nothing refused (`edgeStat=refused` count: 0).
- **C7** the discarded-damage table carries `nearestMarker=`. 5.2 of the session's 5.4
  unexplained damage is one tally at F39160, the same frame as marker #18.
- **C8** the menu-side capture message no longer claims exclusive fullscreen — the same
  confident wrong instruction iter-56 C2 removed from the drive side, refuted again by
  `selfTest=black videoWindowed=0` recovering 10 s later with nothing changed.

## Corrections to earlier claims

- **iter-57's temp-action guard is PROVEN.** The digest read `tempActionV2 — QUIET,
  never-reached`; that was a census-timing artifact, not a fact. `[F55985] EVENT temp-action:
  action=22 ms=1500 askedMs=1500 site=layer2-reverse` — `ms=1500`, not the 10,000 ms that
  caused the iter-56 blackout. The guard held.
- **`handbrakeBandV2`'s counter is NOT missing.** Its 94 `hb-band-skip` events are
  *suppressions*, not fires; `GateFire` correctly fires only when the handbrake engages. Adding
  a `GateFire` call there would have corrupted the counter. `fire=0 (ev:94)` is the truth.
- **`refused` vs `clear` in the ray fan is fine.** The emitter is correct and `refused`
  occurred zero times this session.

## Still open — not iter-59

- **The recovery ladder does not converge.** No ladder-attempt counter, no escalation counter,
  no teleport cooldown on the ladder's own three `TeleportToNearestRoad` sites, and the layer-4
  branch zeroes `stuckLocusCumTicks` so escalation restarts at the new location. Floor-to-
  teleport ≈ 20.5 s, repeatable indefinitely. Unchanged since 2026-08-12 and the largest
  untouched defect in the drive assist. **This is the strongest candidate for iter-60.**
- **No motorcycle-aware code path**, on a session that was 84% bike: `GEOM_WHEELBASE_MIN` 2.0,
  `VehicleBrakeScale` floored at 1.0 (a 160 kg bike computes 0.34 and brakes on the sedan
  tune), and the wheel-plane fit needing `contacts >= 3`, which a two-wheeler can never meet.
- **`steerFfV2`** — the curvature feed-forward. Deliberately deferred: it is control-law work
  and B1–B3 are planner work, and `driveassist-recovery` is explicit that mixing them makes
  neither readable. Diagnose before touching a gain.
- **Polyline divergence p50 = 5.5 m → SNAP.** Part B fixes which *lane*; not which *road*.
- **Still no all-gates-off baseline epoch**, two sessions running.

## Verification

1. Confirm the banner reads `iter59-lanetarget-beepfix`. A mismatch = stale DLL.
2. **Beeps first, `laneTargetV2` still OFF.** Drive up behind a car: discrete beeps rising in
   pitch, not a stutter. Toggle `assistBeeps` off — tones stop, **all speech continues**, nav
   beeps unaffected. Settling this before the lane gate moves keeps the two from confounding.
3. Baseline epoch: `driveAssistBaseline` ON 3–5 min, then off.
4. **Lane A/B in one session**: same route both directions, including a multi-lane one-way
   stretch (CapitalBlvd / ElRanchoBlvd) and a curved two-lane road (Vinewood Hills), with
   `laneTargetV2` OFF for one leg and ON for the next.
5. Success criteria: `carLat` fraction >2 m falls from **26.2%**; `fwd=2,bwd=0` samples show a
   non-zero `offset=`; cross saturation falls from **11.3%**; and marker rate stays at or below
   **1.54/min** with drawdown near **15** — the iter-57 gains must not regress.
