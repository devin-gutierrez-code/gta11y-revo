# Drive-assist review — 2026-08-29

> ## Reconciled 2026-08-31 — read this before the review
>
> This file is a **Codex** artifact, kept as written. It was reconciled against the code and the
> triage artifacts during iter-71; the outcome is
> `docs/plans/iter71-bridge-harness-authority-convergence.md`, which is the plan of record.
>
> **The review is substantially correct.** Its line citations resolve against the tree with an
> offset of roughly +30 to +375 lines (it read a slightly earlier snapshot), its description of
> the layered controller matches the code, and its three headline diagnoses — the historical
> actuation-authority failure, recovery non-convergence, and ACC opening throttle while lateral
> confidence is already lost — all check out and were all actioned.
>
> **Five things in it are wrong and must not be carried forward:**
>
> 1. **Session attribution.** "3.7 driving minutes, 12.31 markers/minute, mode changes
>    22.43/minute" is attributed to `driveassist-2026-08-17-111533`. Those figures belong to
>    `driveassist-2026-08-17-083434`, which ran **iter-60**. 111533 ran **iter-61 for 20.6
>    driving minutes at 5.64/min de-duplicated** — the iter-61 bisect *was* played. The
>    `pending-verification` memory carried the same conflation and has been corrected.
> 2. **It never mentions the baseline A/B arm.** `driveAssistBaseline` already stands the eleven
>    experimental gates down as a unit and `triage-log.py` already renders
>    `Baseline vs experimental` per vehicle. It has run twice and the two runs disagree
>    (08-17: `BASE 4.02 vs EXP 7.62 = 1.90x`; 08-19: `9.31 vs 8.35 = 0.90x`). The review's
>    investigation-order item 1 describes a mechanism that already exists, and that
>    contradiction is the loudest unread signal in the archive.
> 3. **`brakeWriteV2` did not add a wheel-contact guard.** It added `!playerVeh.IsInAir` only.
>    The proven 08-12 incident logged `wheels contact=0000`; a car off all wheels on a kerb, a
>    barrier or its roof need not read `IsInAir`. iter-71 B4 adds the guard the review believed
>    was already there.
> 4. **`why=no-recovery-node` already exists** (iter-61), as does
>    `distWhy=search-empty:<reason>`. Only the per-rejection counters were missing (iter-71 A2).
> 5. **"The banner contains a long incident diagnosis" is wrong.** The banner is a cumulative
>    iter-9…iter-62 patch-letter string. The long write-up is the `accLateralInterlockV2`
>    rationale, which is load-bearing and stays.
>
> **What the review missed:** `accLateralInterlockV2` had never appeared in any session's gate
> manifest — written in iter-64 and never shipped. And iter-61 through iter-64 had no
> `CHANGELOG.md` entries, with iter-63/64 having no plan file at all; all four are now written.
>
> **Its structural limitation, which is why iter-71 begins where it does:** every one of its
> eight "next investigation" steps assumes someone can drive to a named freeway, reproduce a
> specific overshoot, provoke a wedge on demand, and flip gates between epochs, by eye and by
> hand, repeatably. This mod is developed and played by a blind driver. The measurement
> apparatus, not the controller, was the binding constraint — so iter-71 built the bridge test
> harness first (`GTA/BridgeHarness.cs`) and only then landed the fixes the review earned.

## Scope and confidence

This is a read-only review of the current `testing` working tree, the project memory, the existing triage artifacts, selected incident excerpts, and failure screenshots. No code was changed. This file is the only write made by this pass.

Primary evidence:

- `GTA/GTA11Y.cs`, especially the state/constant block around lines 2239–3650; `ProcessSteeringAssist` at 14073; engine-road and vehicle sampling at 17158–18590; recovery at 18809–21335; actuation and ACC at 21400–24164; path construction at 42508–44407; Stanley control at 44407; curve braking at 44922; frame logging at 40624.
- `GTA/DriveAssistLogger.cs`, the asynchronous double-buffered log writer.
- Project memories `driveassist-physics.md`, `driveassist-recovery.md`, `pending-verification.md`, and `log-triage-pipeline.md`.
- Recent digests and incident slices from August 15, 16, 17, and 19. I did not read any raw drive-assist log wholesale.
- Contact sheets and individual captures around the dominant failure clusters, with particular attention to `driveassist-2026-08-19-051310` incidents 29 and 38.

Confidence is high about the control flow, the already-proven actuation defect, the recovery-state oscillation, the August 19 La Puerta acceleration/overshoot sequence, and the trainer-spawn nature of the later August 19 fall. Confidence is lower wherever the active runtime gate state was not logged, where an incident happened under GTA auto-drive rather than drive assist, or where the log lacks a causal field such as fire/petrol-tank state before vehicle death.

## What the drive assist does

The feature is a layered driver aid, not one controller. It continuously builds a local driving model, chooses a lateral target, detects hazards, arbitrates throttle/brake/steering authority, and escalates through recovery maneuvers when ordinary lane keeping cannot make progress.

In practical terms it does five jobs:

1. Perception and road modelling. It combines nearby entities, shape tests, GTA road-node natives, a bundled graph/polyline, GPS route information, road facts, node properties, kerb geometry, wheel state, vehicle geometry, and vehicle-health state.
2. Lateral control. It constructs and stabilizes a path polyline, estimates cross-track and heading error, and produces a Stanley-style steering command with speed scheduling, lateral-error saturation, derivative damping, curvature feed-forward, low-speed suppression, and vehicle steering-lock limits.
3. Longitudinal control. It merges collision braking, curve/junction braking, lane-departure braking, road-speed governors, recovery braking, reverse logic, and adaptive cruise control. It also confiscates or restores player throttle when the mod claims authority.
4. Mode and recovery management. It moves among `LaneKeeping`, `AligningHeading`, and `RecoveringToRoad`, then applies a separate stuck ladder: wait/arm, reverse or pivot, temporary vehicle actions, AI auto-drive takeover, and finally teleport-to-road.
5. Accessibility and diagnosis. It speaks state and failure reasons, plays directional/urgency cues, records per-frame telemetry and discrete events, detects damage and immobility, and captures pre-roll screenshots at failure emitters.

The central design constraint is important: everything is advisory or corrective for a blind human driver, but GTA itself provides no script-level human-driver assist and no reliable traffic-light colour. Therefore junction support can safely govern speed but cannot promise a red-light stop.

## How it is structured

### Runtime orchestration

The game tick decides whether the player is in a supported vehicle and whether steering assist is active. It periodically calls `ProcessSteeringAssist`, keeps cached outputs alive between scan ticks with `ApplyCachedSteeringInputs`, monitors temporary actions and stuck auto-drive separately, and writes a frame record when debug logging is active.

`ProcessSteeringAssist` is the main scan-and-decide routine. It is very large because it owns the complete perception-to-decision pass:

- validate/reset on vehicle changes and teleports;
- sample wheel, handling, road, node, obstacle, and stuck state;
- update the path and road context;
- classify hazards and compute TTC/clearance;
- run ACC and curve/road governors;
- evaluate lane validity and change the drive mode;
- resolve or retain a recovery target;
- combine lane, obstacle, alignment, and recovery steering;
- call the stuck ladder;
- hand the chosen demand to the actuator path;
- push a diagnostic history snapshot.

This function is the behavioural hub. A future refactor should preserve its ordering semantics before splitting it: many values are intentionally sampled once and then reused so two downstream gates cannot see different realities in one scan.

### Road and path model

The road model is a hybrid:

- `BuildPathPolyline` / `BuildSteerPolyline` select a GPS or graph-derived path.
- `BuildPathPolylineRaw` walks the graph and constructs forward vertices.
- `StabilizePolyline` rejects or holds sudden lateral jumps.
- `SampleEngineRoadModel`, `TryGetNodeProperties`, `TryRoadBoundary`, `SampleKerbGeometry`, and related helpers cross-check the shipped graph against GTA's engine view.
- `laneTargetV2` optionally offsets the target toward a selected lane and backs vertices away from road edges.
- `polyDistrustV2` fades the cross-track term when the engine says the polyline belongs to a parallel/different road.

The graph is not merely navigation data. Its path is the reference for steering, lane departure, junction and speed governors, wrong-way checks, and recovery decisions. A bad polyline can therefore contaminate several apparently independent subsystems.

### Lateral controller

`ComputeStanleySteer` is the important lateral-control implementation. Despite older comments that mention pure pursuit, current lane keeping is Stanley-family control:

- nearest-segment cross-track and signed heading error;
- capped cross-track error and speed-scheduled gain;
- capped cross-track steering authority so heading remains influential;
- derivative damping from cross-track-error rate;
- curvature feed-forward using the vehicle's measured wheelbase;
- feed-forward fade when far off the path;
- low-speed cross-track fade under `steerFreezeAtStop`;
- optional polyline-distrust fade;
- final clamp to the vehicle's measured steering lock.

It also computes a windowed minimum heading error for the lane-validity state machine. That distinction matters: the controller steers against the closest segment, while the failure gate looks forward to avoid treating a short perpendicular junction dogleg as a real road departure.

### Longitudinal control and authority

`ComputeACC` tracks a lead vehicle, applies PID-like headway control, and owns a sticky standstill-release state. `ComputeCurveBrake` and the road/junction governors add speed-dependent braking. Collision hazards provide steer/brake TTC demands. `ApplySteeringAssist` merges those demands; `ApplyCachedSteeringInputs` performs the per-frame control writes and throttle/brake arbitration between scans.

The distinction between demand, owner, and physical result is fundamental:

- a channel can request brake without being the winning owner;
- a winning request can be redirected to control 71 or 72 depending on perceived motion direction;
- GTA's control readback can describe reverse-pending rather than useful brake force;
- `Vehicle.BrakePower` is unreliable at stop/creep and near the brake/reverse transition;
- `ctrlB=0` alone does not prove a dead brake write.

The trustworthy evidence is the combination of owner, requested control, `ctrlRet`, speed, wheel contact, achieved deceleration, and whether the mod actually had authority.

### Recovery system

Recovery is two related state machines.

The road-alignment machine uses `LaneKeeping`, `AligningHeading`, and `RecoveringToRoad`. `FindBestRecoveryNode` scans candidate road nodes, rejects perpendicular or impossible high-speed flips, and latches a target. `GetAlignmentRecoverySteer` decides forward/reverse alignment steering and recovery braking.

The stuck ladder in `UpdateStuckRecovery` watches no-progress, throttle demand, obstruction, skew, wheel state, damage, GTA's stuck timers, and recovery-mode state. It escalates through low-level maneuvers and eventually calls `EngageStuckAutodrive`; `MonitorStuckAutodrive` either hands control back or teleports via `TeleportToNearestRoad`. The last resort already exists and is not controlled by the user-facing `roadTeleport` setting.

The unresolved problem is reachability and convergence, not absence of a last resort. Resets and mode transitions can erase the very timers that are meant to escalate. Even when escalation fires, no persistent attempt count or cooldown prevents the same ladder from restarting after a teleport.

### Observability

`DriveAssistLogger` appends on the game thread into a locked memory buffer and swaps buffers to a background writer every 500 ms. This is appropriate for the high-volume telemetry: disk I/O does not block the tick.

The debug system is unusually central to the product because silence is itself a user-facing failure. It includes:

- frame snapshots of command, owner, achieved control, health, wheels, pose, road model, and recovery state;
- discrete mode, gate, brake, throttle, recovery, stuck, teleport, and damage events;
- a bounded in-memory history used for pre-failure context;
- synthetic immobility and damage detectors;
- screenshot capture with pre-roll and per-tag quotas;
- gate manifests and reach/fire counters.

The instrumentation is capable but has repeatedly produced confident false interpretations when fields were absent, stale, misnamed, or aggregated across incompatible epochs. The review conclusions below account for those known traps.

## The most important logic locations

- `GTA/GTA11Y.cs:14073`, `ProcessSteeringAssist`: the behavioural hub and ordering contract.
- `GTA/GTA11Y.cs:42941` through `44406`: path construction, engine direction sampling, road governors, stabilization, and raw graph walk.
- `GTA/GTA11Y.cs:44407`, `ComputeStanleySteer`: lane-keeping steering and the road-validity signals consumed by the mode machine.
- `GTA/GTA11Y.cs:21818`, `ComputeACC`: lead tracking, throttle/brake output, and standstill release.
- `GTA/GTA11Y.cs:21400`, `ApplySteeringAssist`, plus `22070`, `ApplyCachedSteeringInputs`: demand combination, ownership, input lockout, and actual per-frame control writes.
- `GTA/GTA11Y.cs:18931`, `FindBestRecoveryNode`, and `19102`, `GetAlignmentRecoverySteer`: target selection and road realignment.
- `GTA/GTA11Y.cs:19472`, `UpdateStuckRecovery`, through `20782`, `ResetStuckRecovery`: stuck detection, ladder reachability, AI takeover, teleport, and reset semantics.
- `GTA/GTA11Y.cs:20877` through `21065`: bounded temporary vehicle actions and their lifecycle.
- `GTA/GTA11Y.cs:21091`, `MonitorAuthorityBlackout`: diagnosis of control loss during temporary actions/recovery.
- `GTA/GTA11Y.cs:1638` and `1773`: immobility-under-throttle and brake-owner attribution.
- `GTA/GTA11Y.cs:40624` and `42869`: canonical frame logging and history snapshots.
- `GTA/DriveAssistLogger.cs`: non-blocking persistence of the resulting telemetry.

## Proven historical failures that define the current design

### The controls were disabled before being written

For roughly 24 iterations, steering/brake/handbrake commands were paired with `DISABLE_CONTROL_ACTION` on the same controls. `SET_CONTROL_VALUE_NEXT_FRAME` then failed to affect the enabled channel. The disabled-channel readback misleadingly held the requested value, so the logger appeared to show successful writes.

The proof was strong: commanded brake 0 and 1 both produced about -1.25 m/s² coasting; enabled full brake produced +5.65 m/s²; enabled steering produced 8–18 degrees/s while disabled steering produced exactly 0. This explains why threshold and gain tuning did not improve the long-run failure rate. The durable rule is: do not disable a control that the mod intends to write; arbitrate the player's and mod's values explicitly.

### Airborne reverse detection could command full accelerator as “brake”

Control 72 is brake/reverse. The code redirects braking to accelerator control 71 plus handbrake when it believes the car is rolling backward. The historical direction test used velocity dot forward-vector without requiring wheel contact. In the August 12 incident, an airborne car whose attitude had flipped was classified as reversing and received full accelerator for 2.25 seconds while the logical brake demand was 1.0. `brakeWriteV2` is intended to add the wheel-contact guard but remains unproven with the gate off.

### The zero-speed grind has multiple causes

The repository's strongest diagnosis is not one bad threshold. ACC can pin a stopped car; Stanley can demand lock at near-zero speed; obstruction geometry can miss bumper contact; queue vetoes and keep-alive states can block escalation; several governors compete without a single speed target; and scan-rate filtering adds phase lag. This is why isolated gain changes have not been reliable. Standstill escape is a planning and authority problem, not a lateral-gain problem.

## Recent session findings

### August 15, 15:05 — best clean-enough reference, not a proof of every gate

Session: `driveassist-2026-08-15-150525`, 20.8 driving minutes, 30 deduplicated markers, 1.44 markers/minute, 7.6 maximum settled incident damage, mode changes 0.5/minute, poly distrust 1.7/minute. Vehicles included CLIFFHANGER and DILETTANTE; the session was long enough to be more useful than the later 3–6 minute runs.

Important visual:

- `triage/driveassist-2026-08-15-150525/snaps/sheet-0006-PLAYER-INDICATED-FAILURE.jpg`: 15:xx session time, Elgin cluster; a motorcycle is stopped immediately behind a dark SUV at a signalized intersection, then moves to the SUV's right side. One-line description: a genuine close-queue/side-clearance situation, not an invisible wall or road-model-only failure.

The session supports the iter-57 authority-chain changes and bounded temporary action, but it does not validate motorcycle behaviour generally. The memory notes that later sessions were dominated by motorcycles even though the code has no motorcycle-specific recovery path.

### August 17, 11:15 — state-machine/standstill regression

Session: `driveassist-2026-08-17-111533`, 3.7 driving minutes in the summarized comparison, 12.31 markers/minute, mode changes 22.43/minute, poly distrust 22.43/minute. `accStandstillV2` fired only 1 of 166 episodes versus 260 of 401 in the August 15 reference. Vehicles included CLIFFHANGER, BATI, DUBSTA3, DUKES/DUKES2, EMPEROR, and TEZERACT, with short epochs and large trainer teleports.

The code/log mechanism is specific: when `FindBestRecoveryNode` returns empty, the state machine declares `LaneKeeping` even though `laneKeepOk` remains false. The next scan re-enters recovery. This accounted for 40 of 82 transitions in the memory's focused audit. It is a structural contradiction, not ordinary hysteresis noise.

Worst highlighted failure:

- 11:27:34.882, Algonquin, vehicle in a recovery/suspend sequence, marker #60. Body 931 to 871: 59.7 lost. Engine 889 to 727: 161.2 lost. Speed 0.3 m/s; stale lateral perception 2.801 seconds; `suspended=True`; recovery had flipped repeatedly at about 1.5–2.5 second intervals.
- Contact telemetry said nearest vehicle about 10–13 m away, so it does not establish a vehicle collision. The damage is a grind/physics event during suspension and stale perception.
- `triage/driveassist-2026-08-17-111533/snaps/sheet-0060-AUTO-COLLISION.jpg`: recommended image for this marker; review before any repair because the numeric “contact vehicle” label is only nearest-entity attribution at this historical point, not guaranteed contact.

Actionable reading: restore coherent exit semantics first. A recovery failure must not return to `LaneKeeping` until lane validity is true or a distinct safe holding state owns the car. Then separately verify why the standstill-release fire rate collapsed.

### August 19, 05:26 — La Puerta freeway loss of lateral control and ACC acceleration

Session: `driveassist-2026-08-19-051310`, incident 29, TEZERACT. This is the strongest recent drive-assist failure because it is a continuous driving sequence, repeatedly player-marked, and supported by telemetry plus images.

Timeline and damage counters:

- 05:26:05.663, F33294: 13.9 m/s, lateral -2.04 m, ACC owns throttle at 1.00.
- 05:26:07.644, F33394: 17.0 m/s, lateral +0.84 m, skew 16.3 degrees, full throttle.
- 05:26:08.150, F33418: 19.6 m/s, lateral +3.04 m, full curve brake, but achieved steer is zero for this sample (`!STEER-DEAD`).
- 05:26:08.663, F33447, player marker #57: lateral +4.89 m, body 965, engine 984, speed 16.2 m/s.
- 05:26:09.657, F33494: ACC again owns full throttle; speed 18.5 m/s.
- 05:26:11.199–11.242, markers #58/#59: speed 28.6 m/s after peaking at 32.4; lateral -7.33 m; body falls to 938, engine remains 984. First impact loss: 26.1 body.
- 05:26:11.911, player marker #61: body 927, engine 932; car remains about 5.62 m off line at 22.8 m/s.
- 05:26:14.218, marker #63 grind: body 927 to 884, engine 932; 43 body lost at 0.6 m/s with 72.8-degree skew.
- 05:26:15.682, marker #65: body 843, engine 727. This means a further 41 body and 205 engine were lost between the wedge and recovery movement.
- 05:26:15.921, marker #66: PANTO reported 7.2 m left; body 843 to 837 and engine 727 to 694, a further 6.1 body and 33.7 engine.
- 05:26:16.636 onward: player continues marking lateral-offroad/contact failures; settled body 837, engine 694. Total recorded degradation across the cluster is 128 body and 290 engine from the initial 965/984 state.

Images:

- `triage/driveassist-2026-08-19-051310/snaps/sheet-0063-AUTO-COLLISION.jpg`: 05:26:14 cluster; TEZERACT is against the concrete median while an industrial truck occupies the opposite carriageway; sparks appear at failure. One-line description: low-speed divider grind after the high-speed cross-road excursion.
- `triage/driveassist-2026-08-19-051310/snaps/sheet-0067-PLAYER-INDICATED-FAILURE.jpg`: 05:26:16 cluster; TEZERACT is stopped/creeping at the median with a red SUV immediately beside it and other traffic across the divider. One-line description: boxed against the median in live freeway traffic after recovery has moved onto the wrong side.

The immediate control defect is longitudinal authority without lateral confidence. ACC accelerated from roughly 15.4 to 32.4 m/s while lateral error crossed the carriageway and achieved steering remained modest. The current iter-64 `accLateralInterlockV2` constants directly address this by vetoing ACC throttle for excessive lateral error or skew above a minimum speed, but the gate is off and unplayed. The deeper problem is that recovery can choose/move toward the opposing carriageway after the initial wedge.

### August 19, 05:28 — trainer-spawn fall, not a normal driving failure

Session: `driveassist-2026-08-19-051310`, incident 38, TEZERACT. At F37946 the trainer replaced the vehicle and teleported 6,169 m from Sandy Shores to La Puerta. The new vehicle began at Z about 59.6 and then fell roughly 46 m with nearly constant X/Y. Treat this as a spawn-placement/physics artifact and an instrumentation stress test, not controller-performance evidence.

Timeline and damage counters:

- 05:28:06.178–06.211, markers #92/#93: body 1000 to 988, engine 1000 to 932; settled first grind loss 12.4 body and 68.4 engine.
- 05:28:08.725–09.542, markers #94–#96: recovery layers 1–2 and an authority-blackout marker fire while the car is far off the road model (`lat` about +56 m). Health still 988/932.
- 05:28:12.079, marker #98: body 988 to 930, engine 932; 58.1 body lost.
- 05:28:13.550, marker #100: body 925 to 849 and engine 932 to 155 across the intervening impacts; this is the catastrophic engine loss.
- 05:28:14.874, marker #101: body 829, engine 21.
- 05:28:16.136, marker #103: body 802, engine 21; 27.6 more body lost in a grind.
- 05:28:18.932, marker #105: body 795, engine 21 while assist is suspended. Total degradation: 205 body and 979 engine.

Images:

- `triage/driveassist-2026-08-19-051310/snaps/sheet-0098-AUTO-COLLISION.jpg`: vehicle/camera is beside a tall industrial building or storage structure; the failure frame is dominated by a close vertical surface. One-line description: mid-fall or hard impact beside industrial structures, incompatible with ordinary lane driving.
- `triage/driveassist-2026-08-19-051310/snaps/sheet-0101-STUCK-ARM.jpg`: overhead view of the dark TEZERACT on uneven ground beside bright industrial ducting/roof equipment. One-line description: stopped on an industrial roof/ground surface after the fall, not on the freeway carriageway.

What this incident does reveal: airborne/falling classification is weak. The mod wrote brake and full-lock steering during free fall, and contact `0000` was logged as lockup rather than flight. Fire and petrol-tank state were not emitted because those fields are only logged on vehicle death, so the cause of the extreme engine drain cannot be recovered. The actionable instrumentation change is to log `inAir`, wheel-contact mask, on-fire, and petrol-tank health during large engine-health drawdowns, not only at death.

### August 19, 07:42 — auto-drive runway impacts

Session: `driveassist-2026-08-19-074058`, incidents 1–2, Runway1. These were `autodrive=True`, not human drive-assist control, and should not be used to tune Stanley/ACC.

- 07:42:19.226–19.257, markers #1/#2: speed 8.9 m/s; body 1000 to 923, engine 1000 to 942. Settled loss was 87.3 body and 65.4 engine. Screenshot `snap-0001-DAMAGE-DRAWDOWN.jpg`: one-line description: runway/airport auto-drive impact; inspect as task-driving behaviour, not lane-assist behaviour.
- 07:42:22.760, marker #4: body reaches 799, engine 849. Settled marker loss was 131.4 body and 102.3 engine, much of it a prolonged low-speed grind. Screenshot `snap-0004-AUTO-COLLISION.jpg`: one-line description: continued auto-drive collision/grind on Runway1.

The session's headline 2.27 markers/minute is therefore not strong evidence that the eleven experimental drive-assist gates improved normal driving. Its highest costs are auto-drive incidents, and the six-minute duration is short.

## What surprised me

1. The biggest historical plateau was not a weak controller but a complete actuation-authority failure created by the mod itself. The perception stack could be accurate while every tune was effectively changing a number that never reached the car.
2. The code contains both an advanced recovery ladder and repeated paths that make it unreachable or non-convergent. “Add a teleport fallback” would duplicate an existing capability and miss the state-reset defect.
3. The lane controller is more mature than the surrounding planner. It has gain scheduling, damping, feed-forward, path stabilization, engine/graph distrust, geometry scaling, and detailed telemetry. Yet one contradictory mode transition or ACC throttle decision can overwhelm all of that work.
4. ACC can become the most aggressive actuator precisely when lateral confidence is poor. In the La Puerta incident it accelerated across a whole carriageway rather than merely failing to brake.
5. Screenshot evidence is indispensable. Numeric attribution suggested traffic/contact narratives that the images refined into median grind, opposing-carriageway boxing, or an industrial-roof fall. Conversely, the August 15 motorcycle image shows a real stopped queue where waiting is correct.
6. Vehicle type is a large confound. The August 17 comparison mixed motorcycles, sedans, SUVs, muscle cars, and a supercar in short epochs, while motorcycle-specific geometry/recovery is absent.
7. The logging system is sophisticated enough to create false certainty. Known examples include gate state retroactively applied to earlier epochs, `model`/`newModel` case mismatch, stale markers counted repeatedly, nearest-entity labels presented as contact, final events occurring after the last census, and a capture self-test that could accept desktop images.
8. The current source banner contains a long incident diagnosis. It is useful evidence but also shows that durable analysis is living inside executable source text, where it increases noise and will age poorly.

## What remains uncertain

- Whether `accLateralInterlockV2` actually prevents the La Puerta acceleration sequence without making ACC unusably timid. It is built into iter-64 but still off and unplayed.
- Whether `laneTargetV2=0` restores the August 15 standstill behaviour by itself. The August 17 regression correlates with lane targeting/road governor state, but the short mixed-vehicle epochs and trainer teleports prevent clean causality.
- Why `accStandstillV2` collapsed from 260/401 fires to 1/166. The mode thrash is a plausible upstream cause, but the exact interlock that suppresses the release needs a focused trace.
- Whether `FindBestRecoveryNode` returns empty because nodes are genuinely unavailable, rejected by heading/speed filters, or corrupted by the `OutputArgument`/node-query issue. Existing `recovery-search` lines need reason counters, not only `found=False`.
- Whether the opposing-carriageway target in incident 29 came from graph direction, GTA node direction, lane offset, or recovery target selection. The images prove where the car ended; they do not by themselves identify which reference selected it.
- Whether the long-range static cast's 99.9% `belowBumper` rejection is only an origin-height bug or also a slope/vehicle-geometry interaction.
- Whether `roadBoundaryV2`'s repeated zero-width result is a heading-convention error. Both boundary calls returning the same point strongly suggests misuse, but it has not been field-proven.
- Whether `brakeWriteV2` fully fixes the airborne reverse redirect. The underlying defect is proven; the gated fix is not.
- Whether truck/long-vehicle geometry materially improves control. Vehicle dimensions and wheelbase are now consumed, but no clean same-route vehicle comparison exists.
- Whether the engine-health collapse in the trainer-spawn fall was collision physics, fire, petrol-tank damage, or a combination. Current telemetry cannot decide.
- Whether the apparent achieved-steer dead samples in incident 29 are true transient authority losses or normal wheel/ground-state effects. A single `!STEER-DEAD` sample should not be promoted to a new actuation regression without a sustained, wheels-down series.

## Actionable next investigation order

This is not an implementation plan; it is the order that would most efficiently reduce uncertainty before editing behaviour.

1. Run one clean, long, wheels-down normal-driving session with a stable manifest and one vehicle. No trainer spawning, multi-kilometre teleports, auto-drive, or Interaction Menu. Record a baseline epoch first.
2. Test only `accLateralInterlockV2` against the same La Puerta route. Success is not just fewer markers: ACC must stop owning positive throttle when lateral error exceeds the carriageway margin or skew becomes large, without oscillating ownership in normal curves.
3. In the same trace, count every transition out of recovery by `why`. `why=no-recovery-node` must not lead directly to an invalid `LaneKeeping` state. Capture explicit rejection reasons from `FindBestRecoveryNode`.
4. Measure `accStandstillV2` by eligible episodes, not wall-clock rate. Log why each eligible stopped-following episode did or did not latch release.
5. Separate vehicle classes. Do not combine BATI/CLIFFHANGER results with TEZERACT/EMPEROR results. A motorcycle needs its own recovery acceptance criteria or must be declared unsupported by the relevant rungs.
6. Add diagnostic coverage, when code changes are authorized, for large engine drawdowns: in-air, wheel mask, roll/pitch, on-fire, petrol-tank health, current task/temporary action, and last teleport/spawn age.
7. Treat auto-drive incidents as a separate population in summaries. They exercise GTA task driving, not the lane/ACC control loop.
8. Preserve screenshot review as a mandatory step for the worst-cost incident and every player-indicated cluster. Prefer contact sheets and state the one-line physical scene before drawing a numerical cause.

## Bottom line

The drive assist is an ambitious hybrid controller and recovery system built around a detailed observability stack. Its best-developed part is the lane-controller/perception instrumentation; its weakest boundary is authority and state coordination between ACC, lane validity, recovery, temporary actions, and GTA's own vehicle tasks.

The recent evidence does not justify another steering-gain tune. The most actionable normal-driving defect is ACC applying full throttle while lateral confidence is already lost, followed by recovery selecting or reaching the wrong carriageway and then oscillating back into invalid lane keeping. The catastrophic later August 19 cluster is a trainer-spawn fall and must be excluded from controller evaluation, though it exposes useful airborne and damage-telemetry gaps.
