# iter-71 — a test harness first, then the drive-assist fixes it can actually measure

Banner: `iter71-bridge-harness-authority-convergence`. Written 2026-08-30/31 from
`DRIVE-ASSIST-REVIEW-2026-08-29.md`, a read-only Codex review of drive assist on `testing`.

## Why the review needed reconciling before it could be actioned

The review is substantially correct. Its line citations resolve against the tree with an offset
of roughly +30 to +375 lines, so it read a slightly earlier snapshot; its description of the
layered controller matches the code; and its three headline diagnoses — the historical
actuation-authority failure, recovery non-convergence, and ACC opening throttle while lateral
confidence is already lost — all check out.

**But its recommendations were unrunnable by the person who has to run them.** Every one of its
eight "next investigation" steps assumes someone can drive to a named freeway, reproduce a
specific overshoot, provoke a wedge on demand, and flip gates between epochs — by eye, by hand,
repeatably. That is not a reasonable ask of a blind driver, and it is the actual reason the
archive is full of short sessions dirtied by trainer spawns and 6 km teleports. **The
measurement apparatus, not the controller, was the binding constraint.** So W0 comes first.

### Five corrections that must not be carried forward from the review

1. **Session attribution.** It attributes "3.7 driving minutes, 12.31 markers/minute, mode
   changes 22.43/minute" to `driveassist-2026-08-17-111533`. Those figures belong to
   `driveassist-2026-08-17-083434`, which ran **iter-60**. 111533 ran **iter-61 for 20.6 driving
   minutes at 5.64/min de-duplicated**. The iter-61 bisect *was* played. The
   `pending-verification` memory carried the same conflation and has been corrected.
2. **It never mentions the baseline A/B arm.** `driveAssistBaseline` already stands the eleven
   experimental gates down as a unit, and `triage-log.py` already renders
   `Baseline vs experimental` per vehicle. It has run twice and **the two runs disagree** —
   08-17 read `BASE 4.02 vs EXP 7.62 = 1.90x`, 08-19 read `9.31 vs 8.35 = 0.90x`. The review's
   investigation-order item 1 describes a mechanism that already exists.
3. **`brakeWriteV2` did not add a wheel-contact guard.** It added `!playerVeh.IsInAir` only. The
   proven 08-12 incident logged `wheels contact=0000`; a car off all wheels on a kerb, a barrier
   or its roof need not read `IsInAir`. (iter-71 B4 adds the guard the review believed was there.)
4. **`why=no-recovery-node` already exists** (iter-61), as does `distWhy=search-empty:<reason>`.
   Only the per-rejection counters were missing.
5. **"The banner contains a long incident diagnosis" is wrong.** The banner is a cumulative
   iter-9…iter-62 patch-letter string. The long write-up is the `accLateralInterlockV2`
   rationale, which is load-bearing and stays.

**Also found, and not in the review:** `accLateralInterlockV2` had never appeared in any
session's gate manifest — it had never shipped. iter-61 through iter-64 had no `CHANGELOG.md`
entries and iter-63/64 no plan file; all four are written in this iteration (W-D).

## Protocol exception

`CLAUDE.md` says new behaviour gates ship OFF, one per session. **This iteration ships three ON,
at the user's explicit direction.** Attribution is preserved by the harness instead:
`settings.set` creates a clean gate-toggle epoch boundary on demand mid-session, and
`triage-log.py` already cuts epochs on exactly that boundary — so one drive yields several
attributable arms rather than one unattributable blend. `iter71Experimental` stands all three
down as a unit, the same idiom `driveAssistBaseline` uses for the eleven.

---

## W0 — the bridge becomes a test harness

New file `GTA/BridgeHarness.cs` (`partial class GTA11Y`), reached from
`DiagnosticBridge.cs`'s dispatcher before the read-only commands. Changes no driving behaviour.

Ten commands: `settings.get`, `settings.set`, `world.locations`, `world.teleport`,
`world.conditions`, `vehicle.spawn`, `vehicle.set`, `scenario.start`, `scenario.status`,
`scenario.abort`. All run on the script tick, which is the bridge's existing contract.

- **`settings.set` is the highest-value addition.** It takes a batch, validates every id before
  writing any, writes straight into `settingsMenu` and calls `saveSettings()` **once** —
  `setSetting` serialises the whole file per call, which is why `ApplyDriveAssistBaseline` does
  the same. Returns before/after per id. The gate poller then emits `EVENT gate-toggle` plus a
  fresh `EVENT gate-manifest` on its own. This replaces the standing rule "toggle a bisect gate
  by editing `gta11ySettings.json` before launching, never from the in-game menu" with something
  better than either: gates set from outside the game, mid-session, recorded as a clean epoch
  boundary, no menu navigation, no relaunch.
- **`world.teleport` returns the achieved position, heading and speed.** That readback is the
  accessibility-critical part of the whole workstream: placement is verified by reading a number
  rather than by looking at the screen. Optional `snap_to_road` reuses
  `FindNearestSameDirectionRoadNode`; optional `speed_mps` uses the existing
  `veh.Velocity = veh.ForwardVector * speed` idiom.
- **Named locations** so a scenario is named rather than typed as three floats:
  `sandy-shores` and `la-puerta` (the protocol's two arms since iter-57), `la-puerta-fwy` (the
  incident-29 ACC overshoot run), `elgin` (the 08-15 signalised-queue cluster),
  `vinewood-hills` (curved two-lane, the lane-target A/B route) and `capital-blvd`.
- **`vehicle.spawn` / `vehicle.set`** — spawn by model with position and heading and warp the
  player in; set body health, engine health, petrol tank, on-fire, dirt, or `Repair()`. The
  second is what makes A5's damage telemetry testable without crashing a car repeatedly. The
  model request waits 800 ms and then asks the caller to retry rather than stalling the tick.
- **`world.conditions`** — clock, weather, traffic and ped density, re-asserted each tick.
  Two runs of the same scenario were not previously comparable, and the review's "run one clean
  session" premise depends on them being so.
- **`scenario.*`** — a declarative state machine ticked from `onTick`, one step per tick, ten
  ops (`gates`, `teleport`, `vehicle`, `conditions`, `marker`, `hold`, `release`, `wait`,
  `wait_until`, `capture`), capped at 64 steps. **`status` is polled, never blocking** — the
  socket caps a request at 5 s and `Pump()` services 32 per tick, so waiting inside a command is
  not available.
- **`log.write` gained an optional `tag`** so a bridge marker trips `SnapshotFailure` and
  receives the same artifact treatment as a player F1 flag.
- **Every world write is written into the drive log** (`BridgeNoteInLog`). A session where the
  harness moved the car and the log does not say so is a poisoned session.

**Deliberately not added: an arbitrary native dispatcher.** Every risk the review names is
reachable through the managed API above, and a raw native call can hard-crash the game or write
unidentified memory — which is what iter-69 just removed. If a specific native proves necessary
it gets a named command with validated arguments.

**Safety.** The bridge is loopback-only and opt-in. A second config flag `allow_world_writes`
defaults false, so a bridge enabled for read-only diagnosis cannot teleport or respawn anything.
`deploy.ps1` sets it true; `package.ps1` ships the bridge disabled and the flag false.

`tools/mcp/gta11y_mcp.py` gained ten named tools for these (29 total); the existing
`gta11y_command` passthrough reached them regardless.

---

## W-A — telemetry that would otherwise corrupt the measurement

No vehicle control changes; lands ON.

- **A1. `driveModeWhy` was stale at four of seven mode-assignment sites.** Only three set it;
  the other four left whatever the lane-decision block wrote earlier, so `EVENT mode-change`
  printed a reason belonging to a different transition. Now set at all seven
  (`vehicle-change`, `recovery-giveup`, `pivot-forced`, `assist-released` added). Without this
  the `why=` histogram the review asks for counts stale values.
- **A2. `FindBestRecoveryNode` rejection counters.** Three failure exits had none, so the scan
  could not separate "zero candidates examined" from "every candidate scored badly".
  `EVENT recovery-search` now carries `scanned=N` and per-reason rejects (perp, flip, z,
  unloaded, primary-hit, u-turn).
- **A3. `found=False` rows reported stale `out` values.** The reject path did not reset them, so
  `targetPos / dist / headingDelta / flipAvoided / perpBand / targetBlocked` carried the
  best-scoring *rejected* candidate's numbers and read as if a target had been found. This trap
  sat directly on the diagnosis the review recommends. Zeroed on every false-returning path.
- **A4. `acc-interlock` could not be judged, and its skew input was unguarded.** It failed
  closed on a stale lateral estimate but read `vehicleSkewAngle` with no freshness check — even
  though `vehicleSkewStampTicks` exists, added in iter-35 *because* stale skew produced a wrong
  diagnosis. The same staleness rule now applies to `ilSkew`, and the event carries `skewAgeMs`,
  `laneKeepOk`, `mode` and the poly-distrust state. Without the last there is no way to separate
  a genuine out-of-lane veto from one driven by a polyline the mod has itself decided to
  distrust.
- **A5. Fire, petrol tank, attitude, in-air, ages.** `SafeIsVehicleOnFire` /
  `SafePetrolTankHealth` had exactly one call site. They now join `EVENT damage-drawdown`, the
  `AUTO-COLLISION` marker and `EVENT airborne`, with body roll/pitch, explicit `inAir`,
  `sinceTeleportMs` and `sinceSpawnMs`. `vehicleEntryTicks` was assigned and read by nothing —
  a half-built field — and was moved to `ModClock` in the process, because it was being
  differenced against a `ModClock` teleport stamp on the same line. **Never mix ModClock and
  wall clocks on one variable.** The FRAME record gets only the cheap subset; the wheel mask and
  wheel-plane pitch stay on their abnormal-or-1 Hz gate.

---

## W-B — behaviour, shipping ON

- **B1. `accLateralInterlockV2` = 1.** Written in iter-64 and never shipped in any manifest. It
  only removes acceleration — `accBrakeOut` untouched — so it cannot strand a moving car, and it
  addresses the worst measured incident (08-19 #29). See
  `docs/plans/iter64-acc-lateral-interlock.md`.
- **B2. `recoveryHoldV2` = 1 — stop the no-recovery-node round trip.** An empty
  `FindBestRecoveryNode` unconditionally declared `DriveMode.LaneKeeping` while `laneKeepOk` was
  still false; the next scan re-entered recovery. The existing comment documented this and did
  not fix it. A fourth `DriveMode` member, `HoldingNoTarget`, is taken instead — passive, with no
  claim of lane validity — with a bounded dwell that escalates into `UpdateStuckRecovery` and
  speaks after 2.5 s rather than round-tripping in silence.

  *The risk, and how it was handled.* 40 `DriveMode.` references, 28 of them `LaneKeeping`, no
  `switch` statements. A blanket `!= LaneKeeping` → `IsRecoveryManeuverMode()` rewrite would
  have handed recovery **steering authority** to a state that has no target. The 16 comparison
  sites turned out to carry **three** distinct intents, not one. Each was audited individually
  and the mapping encoded in a conversion script that refuses on any inversion; the semantics
  now live in `IsLaneKeepingMode()` and `IsRecoveryManeuverMode()` so they are decided once.
- **B3. `stuckConvergeV2` = 1 — make the ladder converge.** Three faults, all of which make the
  escape ladder restart rather than terminate:
  - `TeleportToNearestRoad` had no cooldown check and never stamped `lastTeleportTicks`.
    `ROAD_TELEPORT_COOLDOWN_TICKS` was honoured only inside the `roadTeleport` monitor; all
    three ladder sites bypassed it. Now honoured and stamped.
  - The cumulative-bypass branch cleared `stuckLocusValid` and `stuckLocusCumTicks` immediately
    after teleporting, so escalation restarted from zero at the new location and the ~20.5 s
    floor-to-teleport loop repeated forever. A decayed value is carried across instead.
  - No persistent attempt counter existed, and `ResetStuckRecovery` is called from five sites,
    three of them ordinary mode transitions. A position-anchored counter now survives the reset;
    past three attempts within 60 m the ladder stops re-entering at the same locus and **says so
    out loud**.
- **B4. `brakeWriteV2` — the wheel-contact guard the review believed was already there.** Added,
  reusing `hbWheelsOk`'s reasoning. **Default stays OFF** — a separate unproven fix, not part of
  this ON set.

---

## W-C — triage tooling

- **C1. Auto-drive was pooled into the human drive-assist rate.** The mod writes `autodrive=` on
  every marker header; `triage-log.py` never lifted it, and `Marker.authority()` — whose
  docstring is "who was actually driving when this marker fired" — had no auto-drive branch, so
  an auto-drive frame reported `assist=ON`. `menuOpen=` and `menuSusp=` (iter-61) were dropped
  the same way. All three slots added, an `AUTO` branch, and a population split in
  `## Read this first`. **On `driveassist-2026-08-19-074058` this turns a pooled 2.27/driving-min
  into 4 drive-assist markers at 0.65/driving-min plus 10 auto-drive markers — 71% of that
  session's markers were measuring GTA's task driving, not the lane/ACC loop.** The digest now
  says so above the fold and flags the majority case as a caveat.
- **C2. Vehicle class dimension.** Segmentation was by raw model string only. A model→class
  table (two-wheel / big / car) adds a class column and `_all <class>_` rollup rows to the epoch
  and `Baseline vs experimental` tables. **On `driveassist-2026-08-17-111533` the headline 1.90x
  is an average of `_all car_` 2.63x and `_all two-wheel_` 1.03x** — two different experiments,
  and the mod has no motorcycle-specific code path in recovery, geometry or the stuck ladder.
- **C3. "Previous log" ordering.** The two 08-19 digests each named the other as predecessor,
  because the comparison ordered by generation time rather than session timestamp. Now ordered
  by the stamp in the log name, skipping any row at or after the current session.
- **C4.** 15 new `--selftest` checks covering C1–C3. Both `triage-log.py --selftest` and
  `triage-menulog.py --selftest` pass (they share `tools/logfmt.py`).

---

## W-D — documentation debt

`CHANGELOG.md` back-entries for iter-61 through iter-64, and the orphaned iter-65 block moved up
out of the footer into the chronological list. Retro plan notes for iter-63 and iter-64, the two
drive-assist iterations whose evidence dominates `triage/` and which had no plan file at all.
This file. And a "reconciled 2026-08-31" preamble on `DRIVE-ASSIST-REVIEW-2026-08-29.md` naming
the five corrections, so the next reader does not re-derive the 08-17 session conflation.

---

## Verification

**Offline (all passing at time of writing):** clean build; both selftest suites;
`verify-vehicle-tasks.py --verify`; `check_gates.py` (every new gate present in `GATE_SETTINGS`,
`GATE_UNITS` and `defaultOnSettings`, with `GateReach`/`GateFire` sites and a menu description,
and `iter71Experimental` correctly in `GATE_UNINSTRUMENTED` as an arm switch); triage re-run over
both 08-17-111533 and 08-19-074058 confirming C1 and C2 render.

**In game, the user's role is: launch, get in a car, and say what they hear.** Everything else is
driven from MCP and verified by reading returned values.

1. **Harness self-check.** `ping`, `settings.get` the three gates, `world.teleport` to
   `la-puerta-fwy` with a heading and a speed, confirm the achieved pose matches what was asked,
   `vehicle.spawn` a known car and confirm handle and model come back. This proves the rig
   before any result depends on it.
2. **Phase 1, gates OFF.** `settings.set` all three to 0; confirm `EVENT gate-manifest` in
   `log.tail`. Then read the telemetry from `log.tail` rather than from driving:
   `EVENT recovery-search` `found=False` rows show the new reject counters and zeroed target
   fields (A2/A3); `EVENT mode-change` reasons belong to their own transitions (A1);
   `vehicle.set` engine health down and confirm `EVENT damage-drawdown` carries `onFire`,
   `petrolTank`, roll/pitch, `inAir`, `sinceTeleportMs`, `sinceSpawnMs` (A5).
3. **Phase 2, gates ON.** `settings.set` all three to 1 — a clean epoch boundary in the same
   log — and re-run the same scenarios from the same placements. This is the A/B the archive has
   never had.
4. **The interlock (B1).** Scripted approach to the incident-29 stretch at the same entry speed.
   `EVENT acc-interlock` must appear with `why=lat` or `why=skew`, carrying `skewAgeMs` and the
   poly-distrust fields. **Success is not a lower marker rate alone:** ACC must stop owning
   positive throttle when lateral error exceeds the carriageway margin and must **not** oscillate
   ownership through ordinary curves. `why=stale` dominating means the lane model is going dark
   — a different bug, and it must not be read as the interlock working.
5. **The mode machine (B2).** Count `EVENT mode-change` by `why=`. `why=no-recovery-node` should
   collapse. Compare against iter-61's 8.03 mode-changes/min on `driveassist-2026-08-17-111533`.
6. **The ladder (B3).** Place the car in a wedge with `world.teleport` — the scenario the review
   asks for and nobody could stage by hand — and confirm `stuck-layer` escalates to 4, the
   teleport honours its cooldown, the attempt counter increments, and no second
   floor-to-teleport cycle occurs at the same locus.
7. **What only the user can judge:** that the assist still feels drivable, that speech and beeps
   are unchanged, and that the interlock does not read as an unexplained refusal to accelerate.
   **A gate that measures well and drives badly has failed.**
8. Re-run `triage-log.py`, read only `## Read this first`, confirm the auto-drive split is
   present and the gate-drift line names the correct predecessor log.

## Open questions for the session to decide, not to assume

- **The baseline A/B contradiction** (1.90x vs 0.90x for the same eleven-gate set). One is noise
  or a confound. iter-71 does not resolve it and leaves that set alone so it cannot confound the
  three new gates. With W0 it becomes cheaply testable afterwards — same scenario, same
  placement, gates flipped from outside the game. Note that C1 and C2 both bear on it and neither
  had been applied when either figure was produced.
- **`accStandstillV2` fired 1/166 episodes on 08-17 against 260/401 on 08-15.** Its release latch
  is gated on the setting, so with the gate off the latch can never set. Whether the collapse is
  that gate's state or the mode thrash upstream needs a focused trace, measured by eligible
  episodes rather than wall-clock rate.
- **No motorcycle code path exists**, on sessions up to 84% two-wheeled. C2 makes it visible;
  whether bikes get their own recovery acceptance criteria or are declared unsupported for
  particular rungs is a later call.
- **`roadBoundaryV2` returns a degenerate zero 33,128 times**, both boundary calls returning the
  same point. Untouched here; still the cheapest dark gate to make live.
