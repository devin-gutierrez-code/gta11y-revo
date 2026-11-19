# iter-56 — the brake that was never written, and the perception outage nobody saw

> **Revised after the full pipeline completed.** An earlier draft of this plan led with the
> snapshot-capture defect and proposed `strandOverrideV2` as the single OFF gate. Agents 1
> and 1b then found two larger things, and one of my own supporting numbers turned out to be
> an artifact. What changed is called out in **“Corrections”** at the end.

## Context

Two sessions ran on 2026-08-12 evening, both on `iter55-capture-framing`:

- `driveassist-2026-08-12-214638.log` — 11 KB stub, ~2 min, abandoned
- `driveassist-2026-08-12-214825.log` — **44.3 MB, 17.4 min wall / 8.6 min driving** — the
  session under analysis

Stage 0 triage has been run for the drive-assist log and its menulog; artifacts are in
`triage/driveassist-2026-08-12-214825/` and `triage/menulog-2026-08-12-214825/`, and both
trend indexes are updated. Agents 1 (causes), 1b (logging audit) and the digest all agree on
the load-bearing facts below; where they disagreed I checked the arithmetic myself and say so.

**The screenshots do not exist for this session.** `selfTest=black running=False`, zero
images. The only images on disk are the 120 from the 11:17 (iter-54) session; those were
opened as part of this analysis and are what settles Finding 3.

---

## Finding 1 — the brake demand reaches 1.00 while nothing is written to any control

**~478 of the session's 667 damage drawdown. This is the session.**

Incident 09 (F4102/F4103, JoshuaRd): the car left an embankment at **21 m/s with the
assist's brake demand pinned at 1.00 and no brake written to any control**, flew ~1.3 s with
zero wheel contact, and hit world geometry pitched 40° / rolled 61°. Engine health 1000 → 688
in a single frame.

The decisive log shape, sustained F4055–F4106 (2.25 s) and again F4261–F4391:

```
apply-assist: aReq=0.00 hasBrakeThreat=False brakeTTC=none brakeMag=0.000 handbrakeMag=0.000
brake:        brakeCmd=0.000 rampedBrake=1.000 handbrake=0.000 isBraking=False
              brakeArmed=False ctrlB=0.00 ctrlBd=0.00      sites=-   thr=1.00/1.00
```

`rampedBrake=1.000` is the demand; `brakeCmd=0.000` is the output. **This is not the iter-39
disabled-control bug** — `ctrlBd=0.00` too, so nothing was written into *either* channel.

Four independent failures stacked:

1. **The brake output path is threat-gated.** `hasBrakeThreat=False`, so the
   lane-departure / recovery / curve demands never become a brake. A demand of 1.00 that no
   threat corroborates is silently discarded.
2. **Throttle arbitration handed the car back mid-crisis.**
   `[F4030] throttle-arbitration: resolved=release-both playerThr=1.00 ramped=0.35
   owner=lanedepart` — from F3968 to F4106 `sites=-` and `thr=1.00/1.00`, no hold. The
   assist was asking to stop and letting the car accelerate at the same time.
3. **`handbrakeBandV2` suppressed the last actuator.** `[F4085] hb-band-skip: ramped=1.00
   emerg=False spd=20.1 wheelsOk=False` — its redefined trigger (`emergencyBrakeActive &&
   speed>8`, `GTA11Y.cs:22026`) no longer coincides with the old `rampedBrake>=0.95`. 41
   skips session-wide, 12 in this run-up.
4. **By then there were no wheels.** `contact=0000 mode=lockup` F4055–F4155.

Two of the nine "car accelerated during a commanded brake" episodes are the same defect:
`[F4144] v0=5.17 vEnd=12.51 dv=-7.33 owner=recovery peakCmd=1.00 ms=4025 endReason=cap` —
full-authority recovery brake held to its 4 s cap while the car **gained 7.3 m/s**.

## Finding 2 — a silent, latched perception outage the assist drives straight through

**~199 of 516 driving-seconds (~39%).**

23 markers (not 24 — #55 is `CONTACT-R/world` and is *fresh*, which is what proves perception
recovered between the two outages) carry `UNKNOWN-STALE`. Subtracting each marker's
`stale<N>ms` from its own timestamp collapses all 23 onto **two instants** — I verified this
arithmetic independently of both agents:

| freeze origin | markers | count | spread |
|---|---|---|---|
| 21:59:50.82 | #44–#54 | 11 | **28 ms** |
| 22:03:18.30 | #56–#67 | 12 | **132 ms** |

So the failure table shows 23 independent failures for **two** perception stalls. That is 34%
of the marker count behind the "7.79/driving-min vs median 2.76 — WELL ABOVE baseline"
headline; de-duplicated the session is ~46 markers ⇒ **~5.35/driving-min**.

They are two *different* failures wearing one label:

- **Group A (F11343–F18901) — total outage.** `latAgeMs == perceptAgeMs == scanAgeMs`,
  15 742 → 142 402 ms. Event-stream gaps confirm it: `road-facts` **F10395→F18901** (the only
  gap in the session), `poly-lat` F9382→F18901, `recovery-search` F10383→F20888 — while
  `nav-scan-stats` and `shapetest-stats` have **no gap**, so the raw shapecasts kept running.
  Recovery came only from `[F18901] teleport-reset: source=planar-jump` — the player
  teleporting 557 m un-stuck the road model.
- **Group B (F20909–F23879) — lateral only.** `scanAgeMs = 0–58 ms (FRESH)`, `latAgeMs`
  1 669 → 56 974 ms, with the frozen value visible as `lat=0.90` in every trace line. The car
  was off the road graph entirely (`navC=999.0 navCt=none cacheEmpty=True`), so no polyline
  resolved and `lat` was never recomputed.

### The cause of Group A: **autodrive owned the car, and the log cannot say so**

Both agents read this as "the assist silently kept steering on a two-minute-old model." That
is wrong, and I traced it to the end rather than ship a watchdog for a stall that never
happened. The speech track brackets the blackout to the frame:

```
[F10405] speech: "Drive assist still stuck. Auto-drive taking over."
[F18901] speech: "Auto-drive ending. You have control."
[F18901] teleport-jump: jumpDist=557.3   teleport-reset: source=planar-jump
```

`apply-assist` fired **0 times** between F10400 and F18900, against **55** during incident 09.
The drive-assist block is gated at `GTA11Y.cs:7263` on
`steeringAssistMode > 0 && !assistSuspended && !intoxAssistSuspend && IsInVehicle() &&
`**`!isAutodriving`**. When iter-35's last-resort ladder handed the car to auto-drive, that
gate closed and took everything with it: `ProcessSteeringAssist` stopped stamping
`lastScanCompletedTicks`, `ApplyCachedSteeringInputs` stopped running — **which is why
`EVENT scan-stale-release` fired zero times.** The dead-man at `:20277` is real and correctly
written; it simply lives inside the function that stops being called.

So the mod was behaving correctly. **The instrumentation cannot represent "autodrive owns the
car."** The marker header reports `assistActive=True suspended=False grace=False` because
those fields describe drive-assist latches and **`isAutodriving` is not one of them** — the
same shape iter-46 0b-6 already fixed twice, for dead-vehicle (`:38589`) and intox (`:38593`).
`isAutodriving` is the missing third arm.

**Group B is a different, genuine fault.** `scanAgeMs` is *fresh* (0–58 ms) there, so the scan
was running; only `lastLaneLateralError` froze, at `lat=0.90`, because the car was off the
road graph (`navC=999.0 navCt=none cacheEmpty=True`) and no polyline resolved. `assist-stalled`
did fire, 4 times. This one is real — and it exposes a self-latching risk in the alive-gate at
`:7332-7355`, where `|lastLaneLateralError| > 4f` is both an *input* to the gate and an
*output* of the scan the gate controls.

`UNKNOWN-STALE` sits *above* WEDGE / OVERSHOOT / LATERAL-OFFROAD in a first-match ladder
(`:38600`), so once perception is stale no other class can ever be assigned — which is how two
completely different situations, one of them not a failure at all, ended up sharing 23 rows.

## Finding 3 — the capture self-test was never testing the game

**iter-55 did not break capture. It revealed that the self-test had never worked.**

| session | iter | captureRect | clientFrac | selfTest | images |
|---|---|---|---|---|---|
| 11:17:58 | iter54 | *(whole screen)* | — | `ok` | 120 |
| 21:46:38 | iter55 | `3,32,1530x830` | 61% | `black` | 0 |
| 21:48:25 | iter55 | `0,0,1920x1080` | 100% | `black` | 0 |

- **iter-54 blitted the whole screen.** Confirmed by opening
  `triage/driveassist-2026-08-12-111758/snaps/snap-0042-AUTO-COLLISION.jpg`: the full
  1920×1080 desktop with the game in a ~1010×580 window — icons, taskbar and clock fill ~70%
  of every frame. `IsEffectivelyBlank` (`SnapshotWriter.cs:192`) samples a grid over the
  *whole captured rect*, so a desktop in frame guaranteed variation. **The test could not
  fail.** It passed for the wrong reason for its entire life.
- **iter-55 correctly cropped to the game window** and the test became honest for the first
  time, immediately catching a black game frame at startup.
- **"Exclusive fullscreen" is not a sound inference.** At 21:46 the game was in a plain
  1530×830 window at (3,32) — a title bar at y=32 — which cannot be exclusive fullscreen, and
  it still read `black`. The player then heard *"Failure screenshots are unavailable in
  exclusive fullscreen. Switch to borderless windowed"* (`GTA11Y.cs:38441`), **did exactly
  that** — 21:48 measures `0,0,1920x1080` — and it still failed. A blind player was given a
  confident, specific instruction that did not work.

Three defects: the self-test is **one-shot and latched** (`SnapshotWriter.cs:159-164`, nothing
re-tests); it runs at **startup**, the moment the game is most likely mid-load or mid-fade; and
it **asserts a cause it never measured** (`videoWindowed` comes from a `settings.xml` that
`SnapshotWriter.cs:379` already documents as unreliable).

Worse, the priority is inverted: the total-failure announcement is `Speech.Ambient` — the
lowest tier, dropped outright if anything Critical fires the same tick — while the *lesser*
failures shout: degraded framing is `Speech.Critical` (`:38464`) and cap-hit is
`Speech.Critical` (`:39040`). And `WarnIfCaptureDegraded()` sits on the `else` at `:38445`, so
a black self-test skips the framing warning entirely.

## Finding 4 — a forward slope classified as a wall, latching a brake GTA executes as reverse

**Trap 1: incidents 04–08 at (552.5, 3484.2), 5 incidents / 10 markers / 21 s / zero damage.**

The car is beached 22–29 m off the road line on an embankment, in a forward/reverse livelock:

- **Forward is blocked by a slope the mod calls a wall.** `brake-floor-latch: type=wall
  brakeDist=0.70–0.93 ttc=0.31–0.43` against `low-obstacle-cache normalZ=0.64–0.78
  relZ=0.20–0.28`. A vertical wall has `normalZ≈0`; 0.64–0.78 is a **39–50° slope**, hit
  20–28 cm above the bumper datum — a berm lip. The **rear** scanner already has the test the
  forward path lacks: `rear-ground-ignored: normZ=0.93 keptAsSlope=True` (51 session-wide).
  The same surface is treated asymmetrically.
- **Reverse is vetoed on a demonstrably clear rear.** `reverse-abort: rearDist=999.0
  rearType=none trig=emerg clearNeeded=3.5 emergBehind=True` — nothing behind the car, and the
  abort fires anyway because `emergBehind=True`.
- **The emergency brake is pushing backwards.** `own=emergency brake=1.00 ctl=B1.00/1.00 …
  ach=B0.00/T-1.00`. Control 72 at ~1 m/s is GTA's *reverse*, so `BrakePower→0`,
  `ThrottlePower→−1`. The car creeps back → never "stopped" → the floor stays latched →
  `reverse-abort` fires → repeat.
- `creepClearV2` — the gate that would have let the car creep out — **vetoed 5 743×, fired 0**.

## Finding 5 — 53 s of futile recovery on a physically high-centred car

**Trap 2: incidents 22–25 at (2065.6, 3943.9), 53 s, 94.6 gross / 69.5 de-duplicated.**

Entry was a second airborne launch (`contact=0000 mode=lockup` at 11 m/s, `steerAch=0.000`
with `roadCorrection` saturated at −0.98). Then: `contact=1000/1100` with **slip 16–43 m/s at
`spd=0.0`** — one or two wheels touching, the rest spinning freely. The car is *beached*, and
the assist is innocent: F21823 reads `thr=1.00/1.00 ctl=T1.00/1.00 ach=T1.00` — throttle
commanded **and achieved** — with no brake anywhere.

The failure is the response: `recovery-search ×319` across three incidents with no success,
`pivot-done reason=no-progress` (startSkew 57.7 → finalSkew 170.7) and `reason=timeout`
(8003 ms), `wedge-damage pinned=True`, and *"Handbrake turn left"* spoken **6 times in one
10 s window** (24 session-wide). Body health bled 941 → 921 during it.

**Per the artifact contract:** the forward ray reads `C=none` for the entire 53 s. That is
**refused, not clear** — the mod had no forward clearance measurement at all here.

**And the last-resort ladder ran its full cycle three times without freeing the car:**

```
[F21283] "Drive assist still stuck. Auto-drive taking over."
[F21762] "Auto-drive stuck. Teleporting to nearest road."
[F22383] "Drive assist still stuck. Auto-drive taking over."
[F22862] "Auto-drive stuck. Teleporting to nearest road."
[F23880] "Drive assist still stuck. Auto-drive taking over."
[F24357] "Auto-drive stuck. Teleporting to nearest road."
```

Three takeover→teleport cycles in 53 s, and the car was only freed when the player manually
swapped vehicle at F24420. iter-35's 45 s-autodrive / 75 s-teleport ladder is firing exactly
as designed and **not converging** — the teleport puts the car back somewhere it immediately
re-strands. That is a distinct defect from the strand itself and belongs in iter-57.

## Finding 6 — measurement and reporting defects that would misdirect the next fix

These do not cost health; they cost aim.

- **Damage is double-counted by ~88 of 754.6.** Marker settle windows overlap other markers:
  `[F4427] marker-settle marker=20 winMs=1032` contains `[F4395] collision-suppressed
  bodyDelta=-10.4 engineDelta=-57.4`, which is marker #21's entire 67.8 cost. Same shape for
  grind windows. **De-duplicated ranking: 09 → 477.7, 12 → 69.9, 22 → 69.5** (12 and 22 become
  a tie, not 105 vs 80). The digest's `MISMATCH: -90.6 … unexplained by markers` is worded as a
  *shortfall* when it is an *excess* — anyone acting on it hunts for damage that does not exist.
- **Session drawdown is stated three ways** in one digest: 664.0 (`:184`), 667.0 (`:591`), and
  the epoch rows summing to 667.0. Vehicle-health arithmetic gives **667.0**.
- **`gate-manifest` at F0 is wrong.** It reports `roadFactsV2` etc. `=0`, yet those gates fired
  4 119 times each and were toggled `from=1 to=0` at F19020. The manifest is written before
  settings load, and the epoch table inherits it — showing E01 and E02 with **identical** gate
  lists, hiding the only real config change of the session.
- **Four junction oracles disagree by 1.5 km.** `fcJunction` says junction-within-12 m;
  `junction-eval` says `nearestJuncDist=1512 armedFrames=0` on all 18 census lines;
  `node-props` says `junctionArmed=False junctionDist=-1` while the flag is set; the marker's
  own `failClass:` line says `junction=False`. The JUNCTION bit is cheap — 48 of 210 sampled
  nodes (23%) carry it, including `offroad|switchedoff` dirt tracks. This is what mislabelled
  markers #32/#33, which were really **a Dune buggy hitting the car in the left flank**
  (`attacker=3358:DUNE:1.1:left`).
- **`triage-log.py` cites `GTA11Y.cs:7452` for an `else if` mis-chaining bug that no longer
  exists.** I checked: line 7452 is now `if (driveLogger.IsRunning)` inside the log banner, and
  the other cited lines (`:7401`, `:7434`, `:7463`) are banner text too. Every digest sends an
  analyst to the wrong 60 lines of a 41 731-line file.
- **The `road:` block is a scan carry-over reprinted every frame with no age field**, so the
  digest's `width jitter p50=0.00 p95=0.00` is measuring the frame:scan ratio (~3.9×), not
  measurement stability — and it reads as a rock-solid measurement.
- **B1 lateral position is doubly wrong.** `edgeR - edgeL` is labelled "0 == centred" but the
  centre offset would be half that, *and* the probes are not bracketing the car:
  `Dist2D(eL,eR)` equals `|edgeL − edgeR|`, not `edgeL + edgeR`, so both land on the same
  side. 1 095 of 1 148 sampled frames read `edgeStat=implausible:0.0` — both probes returning
  the same point — and the tool truncates the `:0.0` that says so.

---

## Plan — iter-56

Banner reads `iter55-capture-framing`, so this is **iter-56**. Set `GTA/GTA11Y.cs:7586` to
`iter56-brake-write-perception-watchdog`.

Per `CLAUDE.md`: instrumentation and **safety watchdogs** may ship ON; anything touching the
driving model ships OFF, one per session.

### A. Authority representation — ships **ON** (instrumentation, no behaviour change)

Re-scoped after tracing Finding 2 to autodrive. There is no perception outage to watchdog;
there is an authority the log cannot name.

- **A1. Add the `isAutodriving` arm to the failClass chain**, above the stale test at
  `:38600` and alongside the existing dead-vehicle (`:38589`) and intox (`:38593`) arms:
  `failClass = "AUTODRIVE"`. This alone reclassifies 11 of the 23 bogus rows.
- **A2. Carry autodrive authority in the marker header and FRAME line** (`autodrive=True`), so
  `assistActive=True suspended=False` stops being a lie about who is driving.
- **A3. Keep the perception stamps honest during autodrive.** Either keep-alive
  `lastScanCompletedTicks` / `lastLaneLateralErrorTicks` the way iter-34 HIGH-4 already does
  for `assistSuspended` (`:7189-7191`), or report `scanAgeMs=autodrive` instead of an age that
  invites a staleness reading. Prefer the sentinel — a fake fresh stamp would be a worse lie.
- **A4. Make staleness an annotation, not a class**: `failClass=<real class> stale=<ms>`, and
  split `SCAN-STALLED` from `LAT-STALLED` — Group B's `scanAgeMs` is fresh and only `lat` is
  frozen, so one label for both hides the difference.
- **A5. Log the alive-gate decision.** `:7332-7355` gates both the scan and the apply, and
  `|lastLaneLateralError| > 4f` is both an input to it and an output of what it controls — a
  self-latching shape. Emit which term held the gate open (or that it closed) at ~1 Hz; this
  is currently unobservable and it is the mechanism behind Group B.

**Not doing:** hoisting the `scan-stale-release` dead-man. It is correctly written and
correctly placed; it reported nothing because it was never called. Moving it would have
papered over the real finding.

### B. One new behaviour gate, shipped **OFF** — `brakeWriteV2`

Targets Finding 1 directly. **Replaces the `strandOverrideV2` proposal** in the earlier draft:
Finding 1 is ~478 of 667 drawdown against trap-1's zero, so it is where the single OFF gate
belongs.

- **B1.** Allow a sustained high `rampedBrake` to reach the control channel **without** a
  corroborating `hasBrakeThreat`. The demand already exists and is already ramped; the gate is
  only whether a threat must co-sign it.
- **B2.** Do not let `throttle-arbitration` resolve to `release-both` while the assist's own
  brake demand is high (the F4030 case).
- **B3.** Restore the top-band handbrake when `rampedBrake >= 0.95` regardless of
  `emergencyBrakeActive` (the `hb-band-skip` path, `GTA11Y.cs:22026`).
- Log `EVENT brake-write` with demand, threat state and what was written, so the gate can be
  judged from one line.

### C. Instrumentation — ships **ON**, no behaviour change

- **C1. Capture retry instead of latch** (`SnapshotWriter.cs:140`): re-attempt every ~30 s
  while `snapshotImage==1` and the writer is down, writing a fresh `capture-selftest` each
  time; log `EVENT capture-recovered`. One GDI blit per 30 s.
- **C2. Fix the capture speech**: promote `selfTest=="black"` and `!IsRunning` to
  `Speech.Critical`, latch drive+menu on one flag so it is said once, and **stop asserting
  exclusive fullscreen** — say what was measured. Call `WarnIfCaptureDegraded()` on every path.
- **C3.** Count captures dropped while the writer is down and report the total (currently one
  `capture-unavailable` line stands in for ~66 losses). Write `capture-final` on the
  script-unload path (`:29267`), not only on the log-off toggle.
- **C4.** Add `edgeAgeMs=` to the `road:` block; add `tempAction=` (id + ms remaining) and a
  player brake/handbrake readback to the FRAME line — Agent 1b's "single most load-bearing
  absent fact" for the `own=none` state.
- **C5.** Fix `gate-manifest` to emit after settings load (Finding 6), so the epoch table stops
  hiding config changes.

### D. Triage tooling — `tools/triage-log.py`, no game rebuild

- **D1.** Collapse markers sharing a freeze origin (`t − perceptAgeMs` within 250 ms) into one
  row with `×N`, and exclude the duplicates from the per-driving-minute rate.
- **D2.** De-duplicate damage: subtract from `marked` any marker inside another marker's
  settle/grind window. Reword the MISMATCH line for a negative residual as an **excess**.
  Reconcile the three drawdown figures on 667.0.
- **D3.** Exclude `owner=none` from the dead-brake bands (see Corrections), or split the table
  by owner.
- **D4.** Delete the `GTA11Y.cs:7452` citation and stop printing C# line numbers from Python —
  they rot silently.
- **D5.** Keep `implausible:<n>`, report B1 refusal per *scan* not per frame, and either drop
  the lateral-position line or emit `(edgeR-edgeL)/2` with a caveat.
- **D6.** Print the `immobile-under-throttle` event's own `owner=` / `thrSites=` / `ctrlT=`
  fields (12 exist this session and the tool discards them); never render `held` and `owners`
  so they invite being read as the same frames.
- **D7.** Add a per-gate `fireKind` (`activity` | `differential` | `veto-healthy`) so
  `skewGateV2` stops reading DEAD-MID-SESSION and `ON-NEVER-FIRED` stops contradicting the
  table's own `(ev:N)` footnote.
- **D8.** `python tools\triage-log.py --selftest` and `python tools\triage-menulog.py
  --selftest` must both pass.

---

## Verification

1. **Build, deploy, confirm the banner.**
   ```bash
   dotnet build && powershell -File tools/deploy.ps1
   ```
   The next log must banner `iter56-brake-write-perception-watchdog`. A mismatch means a stale
   DLL and the session is worthless.

2. **Baseline first, every session** (standing policy from this session's decision): open with
   `driveAssistBaseline` ON for 3–5 min, then off. The digest must show an all-gates-off epoch.

3. **Capture — the check that has never been done.** `EVENT capture-recovered` with
   `running=True`; census `written>0`; `captureRect` matching the game window. **Then open one
   image**: it must be *only* the game — no desktop, no taskbar, no title bar. No counter in
   the log substitutes for this.

4. **Perception watchdog.** Provoke a stall (the F10395 shape followed a `pivot-done
   reason=no-progress`). Expect `EVENT scan-skip` naming the early-return site, a spoken
   warning, and no `steerWritten=True` on a frozen model. `UNKNOWN-STALE` should no longer
   appear as a bare class.

5. **`brakeWriteV2` gets its own epoch** — baseline → gates on / override OFF → gates on /
   override ON. Success: `hasBrakeThreat=False` with `rampedBrake>0.9` no longer produces
   `ctrlB=0.00`, and drawdown falls without a rise in false braking.

6. **Triage.** Re-run both tools; confirm the de-duplicated marker rate (~5.35/min for this
   session if backfilled) and a single consistent drawdown figure.

---

## Deferred to iter-57+

- **`strandOverrideV2`** (`STUCK_ARM_BUDGET_PER_MIN = 2`, `GTA11Y.cs:834`; 404 vetoes vs 4
  fires) and the `creepClearV2` veto condition (5 743 vetoes, 0 fires) — Finding 4/5. Real,
  but iter-56 spends its one OFF gate on Finding 1.
- **Forward slope-vs-wall test** — port the rear path's `keptAsSlope` logic forward, and fix
  `reverse-abort` firing on `emergBehind` with a 999 m clear rear.
- **`fcJunction`** — require more than a bare JUNCTION flag within 12 m.
- **Lane-model flip** (`polyDiverge` max 52.9 m, `lat-snap` 0↔56 m in 40 frames).
- **Steering lock** — `lock-override` resolved 40.1° and 22.5° for the same TEZERACT in one
  session; `|steerAch| p95=1.010` is normalised by a lock the mod has already disproved.
- **Menu/phone reader** — 12/12 rows named is clean but a 12-row sample; menu snapshots have
  still never fired, and C1 fixes that as a side effect.

---

## Corrections to the earlier draft

- **The brake channel's "65.1% dead at 5–10 m/s" is an artifact, not a defect.** 470 of the
  472 dead frames are `owner=none`, `ctrlB=1.00`, `contact=1111`, and **469 of 470 sit between
  5.0 and 6.0 m/s** — the *player* holding control 72 while GTA has already re-interpreted it
  as reverse-pending, so `BrakePower` reads 0. Every brake the mod actually owns actuates
  (ramped 1.3% dead, curve/acc/recovery/lanedepart 0%). The earlier draft listed this as a
  real finding deferred for later; it should be fixed in the *tool*, not the mod.
- **23 `UNKNOWN-STALE` markers, not 24.** Marker #55 is `CONTACT-R/world` and is fresh — which
  is the evidence that perception recovered between the two outages.
- **The strand table in the earlier draft conflated frames.** `held` and `owners` are computed
  over the whole span and never intersected (`triage-log.py:1264-1272`), so "player flooring it
  while nothing braked" was not supportable from those two columns. The wheel data supports the
  conclusion directly instead: `slip 16–43 m/s at spd=0.0` with 1–2 wheels down.
- **`strandOverrideV2` is no longer iter-56's OFF gate**, for the cost reason given in B.
- Agent 1 repeated the digest's claim that the `GTA11Y.cs:7452` `else if` bug is live. It is
  not — Agent 1b caught it and I verified line 7452 is now banner code.
- **Finding 2 was wrong in both agent reports and in my first write-up of it.** Both agents
  concluded the assist kept steering on a 142-second-old model, and I repeated it. It did not.
  Auto-drive had taken the car at F10405 and gave it back at F18901 — the drive-assist block is
  gated on `!isAutodriving` (`:7263`), so the scan, the apply and the dead-man all correctly
  stood down. `apply-assist` fired 0 times in that window against 55 at incident 09.
  Agent 1b's structural read — "a third parallel authority latch with no representation in the
  classifier chain" — was right; the latch is `isAutodriving`, not a stall.
- **`scan-stale-release` is not a broken watchdog.** It fired zero times because
  `ApplyCachedSteeringInputs` was never called, not because it is misplaced. The earlier plan
  proposed hoisting it; that would have hidden the real finding behind a fix for a
  non-problem.
- **Finding 1 is unaffected** by any of the above and remains the top item: the apply path was
  demonstrably alive through incident 09.
