# iter-57 — the mod hands the car to the engine, and does not know it

Source session: `driveassist-2026-08-14-052343` (44 MB, 9.8 driving min, banner
`iter56-brakewrite-autodrive-capture`). First session with a working screenshot set —
246 frames, 53 contact sheets, 83 markers.

## What the session actually said

**The headline was an artefact.** Stage 0 reported 8.54 markers/driving-min against a
median of 5.66 — "WELL ABOVE baseline". Three corrections, all now applied to the tool:

- Five frames carried duplicate `(frame, failClass)` markers. De-duplicated: **8.03/min**.
- **30 of 84 markers came from tags whose gates were OFF** for the first 28 % of the
  session. Like-for-like ≈ 5.5/min — *at* the median.
- The median itself is contaminated (two of six baseline sessions are barely driven).
  Against the three most recent comparable sessions (mean 10.5), 8.03 is **below**, and
  continues a downward run 12.73 → 10.97 → 7.79 → 8.03.

**The metric that genuinely regressed is `immobS`** — throttle-held-at-zero-speed
seconds, measured independently of markers: 23.8 → 28.4 → 32.5 → 48.8 → **67.2 s**.

## Root cause: `tempActionV2` blacked out the mod's control authority

At F8130, during an 11.49 s pause-menu stall in which the player also teleported 1313 m
and swapped TEZERACT → TURISMOR, eleven gates were switched on at once. Among them
`tempActionV2`, which routes the layer-2 recovery rung through a native engine task:

```
GTA11Y.cs  TryTempAction(playerVeh, TA_GO_IN_REVERSE, STUCK_LAYER2_TICKS/10000, "layer2-reverse")
```

That is a **10-second** `TASK_VEHICLE_TEMP_ACTION`. While it runs the mod has zero
steering and zero brake authority — and no idea it doesn't.

| window | temp action? | frames with \|liveSteer\|>0.3 | dead |
|---|---|---|---|
| inc 17, 05:27:20.6–28.2 | `action=22 ms=10000` | 182 | **182 (100 %)** |
| inc 36, 05:32:06.1–14.1 | `action=22 ms=10000` | 229 | **229 (100 %)** |
| inc 12 (same rung, pre-gate) | no | 31 | **0 (0 %)** |
| inc 15 / 04 / 11 | no | 256 / 264 / 195 | 1 % / 2 % / 6 % |

Incident 12 is the control: same ladder rung, same route, 0 % blackout.

Three compounding defects:

1. **No guards.** `TryTempAction` checked only gate/vehicle/Full-mode/layer/driver. The
   control-path reverse it replaced checks `clearNeeded=3.5 hardFloor=4.0 abortDist=6.5`.
   At F10112 the mod aborted its own reverse four times on `rearDist=2.1`, then at F10188
   handed the engine a ten-second full reverse into that same wall.
2. **The cancel did not cancel.** After `temp-action-clear` at F9161 the car *accelerated*
   backwards from 8.8 to 16.7 m/s and stayed `reversing=True` for the full remaining `ms`.
3. **The give-up handed the car back while the engine still held it.** `thrAch` stayed at
   -0.50 for 1.4 s past the handover with the player's `playerRaw=1.00` doing nothing.

Cost: incident 17 ≈ 591 units (118 body + 473 engine) in ~1.5 s and three impacts, and
the COG552 driven **backwards** off Calafia Rd, 8 m down an embankment at 26 mph, into
the river — where the session ended, 0 MPH, after four give-ups in two minutes.

## What landed

**A — temp-action containment.** Rear-clearance guard on reverse actions (same terms as
the control path, and a stale scan is not clearance); `TEMP_ACTION_MAX_MS = 1500` cap on
every action, so exposure is bounded whether or not the cancel works; `MonitorTempAction`
re-evaluates the authorising condition every tick and cancels the moment it lapses; the
cancel now overwrites the task slot (`TA_BRAKE_ACTION` 1 ms, then `TA_NONE`) and emits
`EVENT temp-action-cancel-check` reporting whether the native honoured it. New events:
`temp-action-veto`, `temp-action-cancel-check`. `temp-action` gains `askedMs`/`capped`/
`rearDist`.

**B — authority-blackout detector. Ungated.** `steerWritten && |steerAch| < 0.02` for
>200 ms with a real command emits `EVENT authority-blackout` and a snapshot; at 1 s it
speaks *"Drive assist has lost steering. Take the wheel."* `achValid=false` is treated as
unknown, never as dead. `tempAction`/`tempSite`/`tempLeftMs` now appear on the FRAME
decision line — nothing previously said a native task was running.

**C — no unclean handover.** `recovery-giveup` defers up to twice while a temp action is
live, cancelling first. If the native still refuses, it hands over anyway and says
*"The car may still be moving on its own. Brake now."* rather than "You have control".
New: `EVENT giveup-deferred`, `handoverUnclean=` on `recovery-giveup`.

**D — water guard. Ungated, per decision.** Arming drops from 8 m/s to 0.5; probe
distance scales with speed, with a bumper-relative near-probe at a crawl; the low-speed
path is *stricter* (both probes wet AND off-road AND water within 0.5 m of car level) so
marinas and piers stay quiet; the >4 m/s brake floor is gone; a commanded reverse or live
temp action is abandoned, not merely braked against; and `EVENT water-probe` reports
armed-and-quiet every 30 s so absence is informative. `water-ahead` gains
`slow`/`spd`/`tempAction`/`fullMode` and fires a snapshot.

**E — `reverseEmergCorroborateV2` (ON).** All 20 reverse-aborts read `trig=emerg
emergBehind=True` with `rearDist` 6.1–11.3 m. `emergencyBehind` derives from the
*forward* brake-threat cache, a world-space point up to 300 ms old; a wedged, skewed car
makes the obstacle it is escaping dot negative. Now the rear channel overrules it when
fresh and clear beyond `abortDist` — `none` (999) explicitly does not count as clearance.
The correct `rearDist=2.0–2.1` deadlock aborts are unaffected. New:
`EVENT reverse-emerg-overrule`; `rearStationary=` now reports `world` instead of a
misleading `false`.

**F — `stuckEscalationV2` (ON).** Both escalation routes were unreachable. Per-episode:
9 of 10 escalations stopped at layer 2, because layer 3 needs 10 s of layer-2 while the
give-up fires at 12 s. Cumulative: thresholds were 30 s/60 s of *accrued* distress, and
the river strand reached 17.5 s across two minutes and four give-ups; the session
maximum ever was 32.8 s and `stuck-locus-clear reason=fast` discarded it. Now:
thresholds 12 s/25 s; the fast-clear **decays** (×0.5 per 10 s of sustained healthy
driving) instead of zeroing, keeping the locus valid so the carry survives; and the
give-up is held while the ladder still has rungs and escalation is live, bounded at
twice the window, never for the damage-accruing path. New: `EVENT giveup-held-for-ladder`,
`cumMsKept`/`decay` on `stuck-locus-clear`.

**G — `giveupHazardV2` (ON).** Across all six give-ups `fwdClear` read 13.5, 13.3, 16.2,
17.1, none, none — so `recoverySuspendV2` censused `eval:5/fire:0`: no throttle hold, no
direction, while the images show the car nose-in to a corrugated fence and in a river.
99.9 % of the 6607 static rejects are `belowBumper`, so the channel the give-up consulted
cannot see fences, kerbs or shorelines. Now sourced from `cachedLowObstaclePos` and
sub-bumper hits, `none` is treated as **unknown and fails closed**, and
`DescribeNearestHazard` can say "ahead, low". New: `hazardSrc=` on `recovery-giveup`.

**H–N — instrumentation.** `TELEPORT-JUMP` now tests implied speed against a stamped
baseline and drops the `sameVehicle` gate (3 of 3 were false positives measuring elapsed
travel; the one real 1313 m jump was skipped because it came with a vehicle change).
Static rejects record *which gate* fired rather than re-deriving it from `relZ` — a
cone-rejected low wall was being counted as `belowBumper` — and carry `relRoad` /
`vehAboveRoad` so the datum question (`relZ` is measured from the entity origin, not the
bumper) becomes a measurement; below-bumper rejects are now sampled (all within 4 m, 1 in
64 beyond) where `STATIC-REJECTED` previously appeared **0 times in 6607 rejects**. The
threshold itself is deliberately unchanged pending that measurement. The pre-roll ring is
stamped and invalidated on staleness, on foot, on vehicle change and on teleport. Markers
gain an `impact:` block (collision entity, material, normal, `normalDotFwd`, signed
speed) — marker #30 cost 475 with every ray `clear` and no way to name what it hit.
Brake episodes record `ownerAt0` (the old `owner=` was read at episode end, *after* the
cmd-drop that ends it — 44 % spuriously `none`), plus achieved throttle/brake,
`reverseRedirect`, `framesAirborne` and `tempAction`. `capture-final` is now written on
the abort path, so quitting the game records an honest census. `EngageStuckAutodrive`
emits an event, and `MonitorStuckAutodrive` gains the missing third branch for a car that
auto-drives >8 m without finding a node (previously: wanders indefinitely).

## Triage tooling

- `capture-recovered` is parsed; the **last** census is printed; the "writer was not
  running" alarm is gated on absence of recovery. 08-14's digest claimed the writer never
  ran while 300 files sat on disk.
- Hard contradiction warning when images exist and the census says `written=0`.
- **`## Continuity`** section: every gap > 1 s (was 5 s) with frame range, distance
  jumped, vehicle change, and which markers fall in its pre-roll shadow. The list was
  already being computed and thrown away.
- Rate de-duplicated on `(frame, failClass)`; both raw and de-duplicated reported.
- `gatesAtStart` / `gatesChanged` / `gateManifests` persisted to `log-index.json`, and a
  top-of-digest caveat when the manifest changes mid-session.
- Capture geometry read from the last census, not the startup sample.

## Verification

See `C:\Users\Devin\.claude\plans\a-new-drive-assist-cozy-boole.md`. In short: confirm
the banner reads `iter57-`; confirm `EVENT gate-manifest` is emitted once and does not
change; approach a shoreline at walking pace (expect `water-ahead armed=true`) **and**
drive bridges/waterfront/ferry (expect zero fires — this one is ungated); wedge the car
nose-in with 6+ m behind and expect `reverse-abort` to stop firing `trig=emerg`, the
ladder to reach layer 3 then 4, and `recoverySuspendV2` to actually fire.

Because several changes land together, compare **per-defect counters**, not the headline
rate: `temp-action` (expect capped/vetoed), `authority-blackout`, `reverse-abort
trig=emerg`, `recovery-giveup` count and `heldMs`, max `stuck-layer` reached,
`water-ahead` — and above all **`immobS`**, the one marker-independent metric, which has
doubled since iter-52. If `immobS` does not fall, the root cause was not contained.

## Not broken — do not chase

- **Brake authority.** 79/5385 mod-owned commands dead (1.5 %), 0.0 % above 10 m/s. All
  ten `aAch < 0` episodes carry `playerThrMax=1.00` (the player was flooring it); six
  also carry `ctrlB=0.00` (no brake was ever written); three of those six are inside
  temp-action windows where the brake is redirected to 71+76 by design for a reversing
  car; two more are duration bias with `aPeak` of +4.01 and +14.37 m/s².
- **No damage event was missed** — the unmarked sweep found 0.0 against an 812.0
  drawdown; the error runs the other way (+205.1 over-attribution).
- **Marker #1 is not a failure** — F1241, all controls 0.00, speech ends *"Failure
  screenshots are working now."*
- **Only 1 contact sheet in 53 is spliced** (`sheet-0020`), and its FAILURE frame is the
  honest one; the three run-up frames are the 11.5 s-stale splice. Every other image
  conclusion holds.
