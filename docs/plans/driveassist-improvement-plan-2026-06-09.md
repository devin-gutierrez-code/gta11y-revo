# Drive Assist + Shape Casting — Improvement Plan (2026-06-09)

Scope: deep code review of the drive-assist and shape-casting subsystems in `GTA/GTA11Y.cs`
(~18.3k lines), plus web research on the GTA V shape-test natives, SHVDN 3.6 API surface, and
road-data natives. This is an architecture/code-driven plan, not a log-driven iteration plan —
it layers ON TOP of iter-17 (implemented, built, **untested in game**) and autodrive-1
(implemented, built, **untested in game**).

**Sequencing constraint:** in-game test iter-17 and autodrive-1 FIRST. Several items below
deliberately wait for that signal (the iter-17 junction-phantom fixes may eliminate or reshape
failure classes this plan would otherwise chase). Nothing here should be implemented on top of
an untested iteration without a fresh log.

---

## Architecture as found (reference)

Sensing per detection tick (30–50 ms cadence, `:3754-3762`):
- Entity scans: `World.GetNearbyPeds/Vehicles` at `detectionRange = 10 + min(v/30,1)*30` m (`:7993`),
  corridor-filtered for stationary entities via `IsInPathCorridor` (2 m, `:8026/:8072`).
- 5-ray fan 0/±17°/±35° (`:8132`), inner 3 brake-eligible; ground filter `|normalZ|>0.85`.
- Volumetric swept-sphere along travel dir, speed-scaled radius 1.0–2.5 m (`:8183`, `PerformShapeCast :12681`).
- Long-range static-wall swept sphere, Map-only, range `min(180, v*4)` (`PerformStaticWallCast :12714`),
  accept gate = vertical normal OR bumper-height relZ, plus brake-cone/head-on gate (`:8291-8321`).
- Nav-assist scanner (separate 40–60 ms loop, `:3270`) contributes L/C/R/B distances + entity refs,
  range up to 120 m via per-class meta lookahead (`:3296`).

Control: Stanley over `pathPolyline` (GPS route → static NodeGraph → live natives, `:17929`),
curve brake from per-class AI curve tables (`:18222`), TTC-gated brake pipeline with arm/release
hysteresis + ramp (`:11898-12010`), ACC PID (`:11795`), recovery state machine + 4-layer stuck
escalation (`:11081`) + pivot escape + watchdog force-release (`:12012`).

Failure-rate baseline (per 1000 frames): iter-9 3.50 → iter-11 1.62 → iter-12/13/14 plateau
1.5–1.8 → iter-16 best-case 0.20 (one structural episode). Remaining losses are concentrated in:
junction/path artifacts (iter-17 targets these), recovery maneuvers, highway closing-speed
margins, and curve-geometry edge cases.

---

## A. Shape-casting correctness and sensing

### A1. Shape-test status contract — instrument, then fix (P0, small, do first)
`CapsuleSweep` (`:12644`) STARTs a swept sphere and reads `GET_SHAPE_TEST_RESULT` in the same
frame, accepting **status==1** as "ready" and treating status 2 as no-hit (`:12673`).
Authoritative docs disagree with the code comment:
- SHVDN `ShapeTestStatus` enum: `0 = NonExistent`, `1 = NotReady ("try again next frame")`,
  `2 = Ready` (results returned, request destroyed).
- FiveM/citizenfx native docs say the same; SHVDN deprecated `World.RaycastCapsule()` in 3.6.0
  explicitly because "the result may not be made in the same frame".
- Additional contract facts: a request is **destroyed if not polled every frame**, and there is
  an **in-flight request limit** — START can fail and return handle 0.

The mod demonstrably gets real hits with `==1` (the logs are full of plausible
`shapecast: STATIC dist=…` lines), so the in-game behavior does not match the documented
contract — we are relying on undefined/undocumented behavior that could break per game patch
or under shape-test load from other scripts.

Plan:
1. **Instrument** (zero risk): in `CapsuleSweep`, count status 0/1/2 and didHit per status per
   session; emit one summary line per ~30 s: `EVENT shapetest-stats: s0=… s1=… s1hit=… s2=… s2hit=…`.
   One session of driving definitively resolves the real semantics.
2. **Costless widening** (same patch): accept the result when `(status == 1 || status == 2) && didHit`.
   Whichever interpretation is right, this is strictly ≥ current detection (if 1=pending with
   zeroed args, didHit is false and nothing changes; if 2=ready is being dropped today, we
   recover lost detections).
3. Treat `handle == 0` from START as a **failed request** and log
   `EVENT shapetest-overflow` (currently invisible; the request limit is real).

### A2. Cross-frame shape-test pump (P1, medium — after A1 telemetry lands)
If A1 telemetry shows a meaningful rate of status-2-after-one-frame (i.e., true async
completions), convert the drive-assist casts to a small **pump**:
- A `PendingCast { handle, slotId, issuedFrame }` list polled **every tick** (poll-every-frame is
  required by the engine contract) at the top of `ApplyCachedSteeringInputs`.
- Completed results land in named slots (`fanCapsule`, `staticWall`, `navL/C/R/Side/Rear`);
  `ProcessSteeringAssist` consumes the freshest completed slot (typically 16 ms old — fresher
  than today's 30–50 ms scan cadence) and issues the next batch.
- Bonus: casts can be staggered across frames (e.g., static-wall every other frame), cutting
  per-frame native count without losing coverage.
- Implementation can use raw natives (current style) or SHVDN 3.6 `ShapeTest.StartTestSweptSphere`
  / `ShapeTestHandle.GetResult` — both satisfy the SHVDN 3.6+ constraint. Raw natives keep the
  single-place `CapsuleSweep` pattern; the SHVDN class buys the maintained contract. Either is fine.

### A3. Entity detection range vs physics (P0, small)
`detectionRange` for **entity** threats caps at 40 m (`:7993-7994`). At 30 m/s closing on stopped
traffic, 40 m ≈ 1.3 s — less than reaction + ramp + stopping distance (~45–55 m at firm brake).
Geometry rays already scale to 150 m and the nav scanner to 120 m, but the primary entity scan
(the thing that catches stopped lead vehicles with proper velocity data) is the short pole.
Change: `detectionRange = clamp(rayAi.BrakeLookaheadForSpeed(v) * 1.3, 15, 120)` — the same
per-class formula already used for rays (`:8120`) and the nav scanner (`:3296`). Watch for: more
entities per scan → slightly more TTC computations; the corridor filter already bounds the set.

### A4. True vehicle dimensions (P1, small)
Half-width/cone math derives from class-based `BASE_COLLISION_RADIUS * 0.42` (`:7683`), and
Stanley uses a fixed 2.8 m wheelbase (`PURE_PURSUIT_WHEELBASE`, front-axle at `:18103`).
SHVDN provides `vehicle.Model.Dimensions` (managed, no new natives): use real half-width for
`IsInBrakeCone` (`:7697`) + capsule radius floors, and real half-length for the Stanley
front-axle offset. Motorbikes get a honest ~0.4 m cone instead of 1.1 m; buses stop steering
like sedans. Keep the current values as fallback when the model read fails.

### A5. Path-relative brake cone (P1, medium)
`IsInBrakeCone` measures lateral offset in the **vehicle frame straight ahead** (`:7697`). On
curves this both (a) misses genuinely in-path threats that sit beyond the cone at 20–30 m around
the bend, and (b) admits out-of-path threats that are dead ahead in frame (parked car on the
outside of a curve). The polyline already exists: when `pathPolyline` is fresh and the threat is
within the look-ahead, gate brake eligibility on `DistanceToPolyline2D(threatPos) <= halfWidth + 0.5`
instead of the straight cone; keep the straight cone as fallback when no polyline / in recovery.
This is the principled fix for the curve-phantom class the static-wall gates currently patch
case-by-case.

### A6. CPA-based threat assessment for moving entities (P2, medium)
`CalculateTTC` (`:9672`) is a closing-speed-along-LOS model; lateral miss is handled by the
*current* lateral offset only. Crossing and oncoming traffic on bends produce TTCs that the
entity gates then have to suppress with streak/age heuristics. Standard upgrade: compute
time-to-closest-point-of-approach `tCPA = -dot(relPos, relVel)/|relVel|²` and **predicted miss
distance** at CPA; a threat brakes only if `missDistance < combined half-widths + margin` and
`tCPA` is within horizon. This subsumes several special cases (monotonic-drop gate exists mostly
to kill this class of phantom) and is cheap (pure vector math on data already in hand).
Keep the artificial-TTC path for static obstacles. A/B-log first: emit `cpaMiss=`/`tCpa=` on
every brake decision for one session before switching any gate over.

### A7. Reverse-arc clearance for recovery reverses (P2, small-medium)
All reverse maneuvers (straight reverse-out, pivot reverse bite, reverse U-turn) veto on
`navAssistDistBehind` — a single straight cast from the vehicle center along −forward
(`:12278`, scan at `:3543-3569`). A pivot reverse with full counter-steer swings the rear
corners through an arc the straight cast never samples (the iter-14 reverse-into-prop −54.4 was
exactly an unseen rear obstacle). Change: when `alignmentEngageReverse` with non-zero steer,
issue 2–3 short swept spheres along the **predicted arc** (bicycle model, current steer, 2–4 m
of travel, radius = real half-width from A4) and veto/score the reverse direction on the worst
hit. Costs ~2 casts only while actively reversing.

### A8. Overhead-clearance gate on the dynamic capsule (P3, tiny)
The fan capsule accepts any hit with `|normalZ| <= 0.85` (`:8187`) with no height gate. At
highway speed the radius reaches 2.5 m from a start point 0.5 m up — the swept volume tops out
~3 m, where overhead signage/canopy edges live. Add the same `relZ ∈ [0.3, 2.0]`-style elevation
sanity used by the static cast (`:8294`). One `if`; prevents a rare-but-confusing phantom class.

### A9. Material-aware classification (P3, optional)
`GET_SHAPE_TEST_RESULT_INCLUDING_MATERIAL` (0x65287525D951F6BE; SHVDN 3.6 exposes
`GetResultIncludingMaterial` → `MaterialHash`) can label hits (concrete vs chain-link vs bush).
Possible uses: soften brake response to crashable-through vegetation, speak richer obstacle
names ("fence ahead" vs "wall ahead") for the blind user. Keep as an experiment behind the
debug log; material taxonomy in GTA is large and noisy.

---

## B. Lane keeping and path quality

### B1. Lane-offset polyline — stop tracking the road centerline (P0 impact, staged)
**Finding:** the Stanley migration lost the lane model. `LaneCenterFromNode` (`:10159` — full
carriageway-offset + lane-snap math), `GetRoadGuidance` (`:10231`) and `GetRoadCurveGuidance`
(`:10397`) are **dead code** — nothing calls them. `BuildPathPolylineRaw` (`:17929`) emits raw
GPS-route samples / node positions, which on undivided two-way roads run at/near the **road
center**, so lane-keeping holds the car toward the centerline (oncoming side-swipes follow; the
55% lead-vehicle blend `:9095-9100` masks it only when there's traffic to follow).

Staged plan:
1. **Instrument (iter-18):** log per-tick `polyLat=` signed lateral offset of the vehicle from
   the polyline while the tester drives normally in a known lane. If the distribution centers
   ~1.5–2 m right of the polyline on two-way streets, the centerline hypothesis is confirmed
   (and the offset magnitude calibrates step 2). Zero risk.
2. **Offset the polyline per segment:** the data is already on hand —
   - static-graph segments: `NodeGraph.GetLink` exposes per-link **fwd/bwd lane counts**
     (used today in `FindBestRecoveryNode :10650-10665`); `bwd > 0` ⇒ undivided two-way ⇒
     offset right-perpendicular by `laneWidth * (0.5 + laneIndex)`; `bwd == 0` ⇒ divided
     carriageway ⇒ offset within carriageway only (`laneIndex` from the player's current
     lateral position, reusing the `LaneCenterFromNode` snap+hysteresis math).
   - GPS/native segments: query `GET_CLOSEST_ROAD` (0x132F52BBA570FE92 — returns
     `laneCountForward/Backward` + road `width`) at polyline build time (once per detection
     tick, cached per segment) to make the same decision.
   - Apply offsets to a **copy** consumed by Stanley; keep the raw polyline for corridor
     filtering and curve detection so threat gating semantics don't shift in the same patch.
3. **Re-tune:** `NPC_PATH_CORRIDOR_M` (2.0) and `LANEFAIL_LATERAL_GATE_M` (2.5) were calibrated
   against the centerline path; re-check both against logs after the offset lands.

Risks: GTA nodes on some divided roads already run per-carriageway (offsetting again would put
the car in the gutter) — the `bwd==0` signal is exactly the divider; junction segments should
keep zero offset (blend the offset to 0 within ~10 m of a detected junction vertex to avoid
corner-cutting into the oncoming turn pocket).

### B2. Junction-dogleg smoothing at the CONTROL level (P1, medium — after iter-17 verdict)
iter-17 Fix 1 made the **failure gate** look-ahead-aware (`lastLookaheadHeadingDelta`,
`:18179-18213`) but the **control steer** still tracks the closest segment tangent (`:18148-18165`)
— a perpendicular GPS dogleg still yanks the wheel for the frames the car straddles it, it just
no longer triggers recovery. Reuse the windowed per-segment walk to feed control: measure the
heading term against the **arc-length look-ahead tangent** (`s + L`, `L ≈ clamp(v·0.8, 6, 20)` m)
while keeping the cross-track term against the closest segment (hybrid Stanley/pure-pursuit; both
quantities are already computed in the same loop). Alternative cheap option: one pass of
Chaikin corner-cutting on the polyline before control. Wait for the iter-17 in-game log first —
it tells us how much residual yank actually remains.

### B3. Curvature feed-forward + per-class gain (P2, small)
Stanley with `STANLEY_K = 1.5` fixed (`:934`) is purely reactive: on a constant-radius bend it
needs standing error to hold the arc, which shows up as late corner entry that the curve-brake
then compensates. Add a feed-forward term `δ_ff = atan(wheelbase · κ)` with κ from the
3-point Menger curvature at the look-ahead vertex (the points are already walked in
`ComputeCurveBrake :18302-18324`), and scale `STANLEY_K` mildly by vehicle class (sports > truck).
While there: the `R = chord/(2 sin(θ/2))` estimate (`:18329-18332`) uses `min(l1,l2)` as chord —
with 12 m GPS steps this quantizes badly; the same Menger κ (circumscribed circle through a,b,c)
is a drop-in improvement for the curve-brake radius too.

### B4. Dead-code and stale-comment cleanup (P2, hygiene)
Remove (or move into B1 as the offset helper): `LaneCenterFromNode`, `GetRoadGuidance`,
`GetRoadCurveGuidance`, `GetWaypointAlignmentScore` (only caller is the dead
`GetRoadCurveGuidance`; note `UpdateWaypointDirection` + the `waypointDriveAssist` setting then
have no remaining consumer — either delete the setting or wire the waypoint preference into
`FindBestRecoveryNode`/polyline source selection where it arguably belongs). Fix the stale
comment at `:9361` ("Already calculated by GetRoadCurveGuidance" — it's Stanley now). In an
18.3k-line file every dead path costs real comprehension time during failure analysis.

---

## C. Braking model

### C1. Required-deceleration brake arbiter (P1, medium, A/B-logged)
Today's arbiter is TTC-threshold soup: speed-scaled `armTtc` (`:11908`), artificial
`distance/3` TTC for stopped-facing-static (`:9708-9712` — source of the iter-16 dead-band trap),
per-type swerve/brake distances, low-speed TTC tightening, and the urgent ramp patch. The
standard AEB formulation is one number: required deceleration
`a_req = closingSpeed² / (2 · max(gap − buffer, 0.5))`. Arm when `a_req` exceeds a comfort
threshold (~3.5 m/s²), full-brake as it approaches the vehicle's capability (~7–9 m/s²; could be
read per-class from the AI handling meta). It self-scales with speed (no `armTtc` ladder), has
no stopped-car dead-band (gap fixed, closing 0 ⇒ a_req 0 ⇒ releases — replaces iter-16 F1's
special case), and maps directly onto brake magnitude (`brake = a_req / a_max` instead of the
squared/linear urgency blend `:11551-11563`).
Stage it: **log `aReq=` alongside every existing brake decision for one full session**, compare
against actual arm/release/severity outcomes offline, then switch `brakeArmed` to the new
criterion behind the existing event vocabulary (the downstream ramp/lockout/watchdog machinery
is untouched — only the arm/magnitude source changes).

### C2. Surface-aware cornering grip (P3, small)
`CURVE_LATERAL_ACCEL_MAX = 4.5` is constant. `MapDb.GetRoadTypeAt` already classifies
freeway/highway/surface/alley, and the off-road street-name hash detection exists (iter-12
Patch Q). Scale lateral-accel (and the skew-brake floor) down to ~3.0 m/s² off-road/alley.
Cheap, uses existing data, directly reduces the dirt-road oversteer class.

---

## D. Architecture, performance, hygiene

### D1. One clock: `Game.GameTime` (P2, small but wide)
`DateTime.Now.Ticks` appears 60+ times across the drive-assist path — per call it does a
timezone conversion, and it keeps advancing during pause menus (wall-clock timers like streak
decay, watchdog holds, and pause/resume gates silently accumulate while the world is frozen).
`Game.GameTime` (int ms, game-clock) is the SHVDN-idiomatic, cheap, pause-coherent choice.
Mechanical migration (`ticks/10000 → ms`), one focused patch, no behavior tuning in the same
commit. Audit greps: `DateTime.Now`, `/ 10000`, `* 10000000`.

### D2. One world snapshot per tick (P2, small)
Per detection tick the code calls `GetNearbyPeds` twice and `GetNearbyVehicles` up to 4× (nav
scan `:3341/:3389`, drive scan `:8015/:8058`, lead guidance `:10051`, recovery-pause `:9225`) —
each allocates an array via native round-trip. Collect once per tick into a shared
`WorldSnapshot { peds[], vehs[] }` (max of the consumers' ranges) and have all consumers filter
from it. Less GC, fewer native transitions, and consumers stop disagreeing about the world.

### D3. Managed math instead of `World.GetDistance` (P2, trivial)
`World.GetDistance` is a native call (`GET_DISTANCE_BETWEEN_COORDS`) used inside entity loops
(`:8030`, `:8076`, `:9144`, `CapsuleSweep :12678`, etc.). Replace with `(a-b).Length()` /
existing `PlanarDist`. Pure win, no behavior change.

### D4. Split the drive-assist into partial-class files (P3, hygiene)
`GTA11Y.cs` is ~18.3k lines; iteration velocity (and agent log-analysis) pays the cost every
session. `GTA/Core/` already exists (SpeechQueue). Move coherent regions into
`GTA11Y.DriveAssist.Sensing.cs`, `.Control.cs`, `.Recovery.cs`, `.Telemetry.cs` as
`partial class` — purely mechanical, zero behavior risk, big comprehension payoff. Do it as a
standalone commit with no logic changes so diffs stay reviewable.

### D5. Tester-tunable constants (P3)
The tuning constants that recur in iteration work (corridor width, skew gates, brake thresholds,
pivot timings) are compile-time consts. Surface a `driveAssistTuning` JSON block (read at init,
fall back to consts) so the blind tester can A/B a value without a rebuild round-trip.

---

## E. Verification protocol (applies to every phase)

1. Per-change `EVENT` markers in the established vocabulary; bump the iter banner **in the same
   patch** (iter-16 lesson: banner lagged two iterations).
2. Failure-rate comparison per the established method: normalized per 1000 frames, weighted by
   per-incident severity (healthDelta), compared against the baseline series (3.50 → 1.62 →
   ~1.7 plateau → 0.20 best). Short runs are noisy — prefer per-incident review.
3. Re-test the known problem spots: (1255,3531)/(1267,3545) region, the y≈3563 junction
   (iter-17's episode), Fort Zancudo lane-end, and one highway stopped-traffic approach at
   ≥60 mph (A3's target case).
4. New telemetry to watch: `shapetest-stats` (A1), `shapetest-overflow` (A1), `polyLat=` (B1),
   `aReq=` (C1), `cpaMiss=` (A6).

## Suggested phasing

- **Phase 0:** in-game test iter-17 + autodrive-1 (already built). Run the standard log pipeline.
- **Phase 1 (iter-18, low-risk sensing + instrumentation):** A1 (status telemetry + widened
  accept + overflow logging), A3 (entity range), A8 (overhead gate), D3 (managed distance),
  B1-step-1 (polyLat instrumentation). All small, independently revertable.
- **Phase 2 (iter-19, lane model):** B1 offset polyline + A4 real dimensions (+ corridor retune).
- **Phase 3 (iter-20, sensing architecture):** A2 cross-frame pump, A5 path-relative brake cone,
  B2 control-level smoothing (informed by iter-17 results).
- **Phase 4 (iter-21, brake model):** C1 a_req arbiter (logged → switched), A6 CPA, A7 reverse arc.
- **Continuous hygiene, standalone commits:** B4 dead code, D1 clock, D2 snapshot, D4 file split,
  C2/D5/A9 as appetite allows.

## Sources

- SHVDN 3.6.0 release notes (ShapeTest API added; `World.RaycastCapsule` deprecated for same-frame
  result unreliability): https://github.com/scripthookvdotnet/scripthookvdotnet/releases/tag/v3.6.0
- SHVDN `ShapeTestStatus`/`ShapeTestHandle` sources (status enum semantics; poll-every-frame;
  request limit): https://github.com/scripthookvdotnet/scripthookvdotnet/tree/main/source/scripting_v3/GTA/Physics/WorldProbe
- FiveM/citizenfx GET_SHAPE_TEST_RESULT doc (0=invalid, 1=pending, 2=completed):
  https://github.com/citizenfx/natives/blob/master/SHAPETEST/GetShapeTestResult.md
- GET_CLOSEST_ROAD signature (lane counts + width): https://docs.fivem.net/natives/ (PATHFIND)
- START_SHAPE_TEST_SWEPT_SPHERE: https://docs.fivem.net/natives/?_0xE6AC6C45FBE83004=
