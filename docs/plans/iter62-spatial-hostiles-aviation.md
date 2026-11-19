# iter-62 — sound that comes from somewhere, and aviation checked against Rockstar

Banner: `iter62-spatial-hostiles-aviation`. Built and deployed 2026-08-18. **Never played.**

The first iteration since iter-38 that does not touch the driving model. Nothing here writes a
vehicle control, so none of the new gates joins `GATE_SETTINGS` — they would census
`eval:0/fire:0` forever and pollute the drive-assist ledger, the same call iter-44 made for
`intoxAudio` and iter-52 made for the world-data gates. The iter-61/62 drive-assist bisect is
undisturbed and can land before or after this.

---

## W1 — spatial audio (ships ON)

### What was wrong

Every positioned cue ended in `new PanningSampleProvider(shaped) { Pan = pan }` where
`pan = clamp(dot(toTarget, right) × 1.4)`. One scalar. Two consequences:

- **Front and back were the same number.** A target 30° ahead-right and one 150° behind-right
  produce an identical dot-with-right. The four-zone scanner hid this by making BEHIND its own
  zone with an octave-down double tick — which works for four fixed zones and cannot work for a
  continuous bearing or for a scan sweep.
- **Nothing had a height**, on a map of staircases, overpasses and rooftops.

`UpdateNavZoneState` never received the hit position at all; every one of its four call sites
already had the geometry and was throwing it away.

### What shipped

`GTA/Core/Spatializer3D.cs` — mono in, stereo out, the same contract `PanningSampleProvider`
has, so it dropped into all twelve emitter sites without restructuring any of them.

| cue | how it is produced | why |
|---|---|---|
| azimuth | constant-power pan on `sin(az)` | unchanged in spirit; now from a real bearing |
| ITD | Woodworth `(r/c)(az + sin az)`, r = 8.75 cm, interpolated fractional delay | level differences localise poorly at 330–620 Hz; below ~1.5 kHz timing is what the ear uses. One sample at 44.1 kHz is 23 µs and the JND is nearer 10, so integer delay is not enough |
| front/back | one-pole low-pass closing as the source moves behind (20 kHz → 3.5 kHz), plus ~2 dB rear trim | **this** is what resolves front from back; no amount of panning can |
| distance | floored inverse-ish gain + air-absorption low-pass | reinforces the iter-37 pulse rate, never replaces it. Floored at 0.30 because inaudible is indistinguishable from absent |
| elevation | **not rendered here** | the real cue is a notch through 7–9 kHz and these are narrowband tones an octave or two above middle C. A notch there would filter silence — it would measure as implemented and be perfectly inaudible |

Elevation instead gets an explicit two-tone chirp past ±20° (`spatialElevationCue`, OFF).
Symbolic rather than psychoacoustic, honestly labelled, and audible.

**Failure is loud.** A degenerate listener basis falls back to the legacy pan and logs
`EVENT spatial-fallback`, rate-limited to one line per 10 s. A spatializer that quietly
degrades to mono is indistinguishable from one that works.

### Two bugs found while wiring it

- **The band gain was double-counting distance.** `EmitNavBeepRaw` lifted gain as the obstacle
  closed (`0.05 + 0.05 × (1 − norm)`). With the spatializer applying its own curve, two
  multiplied falloffs make near obstacles blare and far ones vanish. Now a flat per-band level;
  band still separates contact, because contact is a category, not a distance.
- **The listener-basis cache was nearly keyed on `driveLogFrameCount`.** That counter only
  advances inside the drive-assist logging path, so with the debug log off — or simply while on
  foot — it never moves and the cache would have pinned the forward vector to whatever it was
  the first time anything made a sound. Keyed on `Game.GameTime` instead.

### `listenerFrame` (default 1)

`0` body · `1` camera on foot, vehicle when driving · `2` camera always. Camera-relative is the
default because in third-person the camera is what the right stick aims and what the movement
stick is measured against — so it is the frame the player is actually steering in, and sweeping
the stick becomes a way to scan a room by ear. It is a *setting* because it also means the
soundscape swings when the camera does, and that is a question for a session.

### Scan localization (`scanAudioSweep`, default 1)

- *Around Me*: one positioned ping per non-clear zone, ahead/right/behind/left, 180 ms apart,
  each at its own bearing, distance and height, in its own type timbre. **Clear zones are
  silent — the absence is the information.**
- *What's Ahead*: eight pings swept left to right along the ray fan, each placed where its ray
  actually struck. **A doorway is a hole in the sweep**, heard as the sweep passes it, without
  being counted, ranked or named first. The prose version reports at most two openings.

### One audio language

Three cues still encoded *facing* or *distance* as pitch, contradicting iter-37's rule that
pitch means **what kind of thing this is**. Both encodings had been shipping simultaneously
since the attic waypoint tracker. The waypoint (was 400 ± 200 Hz by facing, halved when behind),
the mission blip (350 ± 150 Hz) and the hostile beep (800 − 400 Hz by distance, halved when
behind) now hold fixed notes. Facing no longer needs an encoding — it is where the sound is.

---

## W2 — hostile detection

`TickProactiveThreatScan` — the layered detector with armed-proximity, aiming promotion,
police-during-wanted and an all-clear — had exactly one call site, inside `TickBodyguardSystem`,
which returns immediately unless `bodyguardSystemEnabled && bodyguards.Count > 0`.

**Everyone without bodyguards got a different, cruder detector**: a 2 s `GetNearbyPeds(100f)`
filtered on `IsInCombatAgainst` alone. No armed layer. No promotion. No police tell. No
all-clear. And neither path said *where* anything was — both spoke `"N hostiles detected"` and
left bearing entirely to a stereo beep that could not distinguish ahead-right from behind-right.

The duplicate is deleted. One detector, gated by `hostileDetection`:

- `0` off · **`1` direct combat only — exactly the old behaviour, so the default changes
  nothing** · `2` + relationships, police, all-clear · `3` + armed proximity.

The bodyguard system's own *Proactive Detection* and *Armed Ped Alert* toggles still force their
layers on regardless: a guard that can see a threat should engage it whether or not the player
asked to be told.

Announcements now name and place the nearest two via `ModelNames.DescribePed`, using the **same
listener frame the audio uses** — the word and the sound disagreeing would be worse than either
alone. Capped at two plus a count, matching the `ScanResultCap` idiom: a crowd enumerated one by
one is a paragraph spoken while someone is shooting at you.

---

## W3 — aviation, verified rather than assumed

### `tools/verify-vehicle-tasks.py`

Two first-party sources, neither sufficient alone:

1. `external/shvdn/ScriptHookVDotNet3.dll` reflected for `GTA.VehicleDrivingFlags` — the pinned
   build the mod compiles against, so it cannot disagree with what the mod can express.
2. 21 of Rockstar's shipping scripts from `calamity-inc/GTA-V-Decompiled-Scripts` branch
   `senpai`, fetched by raw URL — what the game actually does.

Writes `docs/vehicle-task-conventions.md`. `--verify` exits non-zero on flag drift, on an
unreachable bit, or on a newly named bit with no spoken wording yet.

> **A bug the tool had, worth recording.** Its first run reported every line number at exactly
> **2×** the truth. The corpus is CRLF; caching it through text-mode write added a second
> carriage return to each line ending, and universal-newline read then split each mangled ending
> into two lines. It looked entirely plausible until a citation was opened. Both ends of the
> cache round-trip now use `newline=""`.

### What it found

| bit | mod said | nightly says |
|---:|---|---|
| 12 | `Unknown` | `AvoidRestrictedAreas` |
| 13 | `Unknown` | `PreventBackgroundPathfinding` |
| 18 | "Take shortest path" | `UseShortCutLinks` / `AllowMedianCrossing` |
| 22 | "Ignore roads (local pathing)" | `PreferNavmeshRoute` / `DriveBySight` |
| 23 | `Unknown` | `PlaneTaxiMode` |
| 31 | `Unknown` | `StopAtDestination` |

And `GetDrivingStyleFromFlags` looped `i < 31`, commented *"bit 31 would make it negative"* — so
`StopAtDestination` was not merely unlabeled, it was **unreachable**. The native takes a bit
pattern; `unchecked((int)…)` passes it fine. The table is now generated; seven bits stay
`Unknown (bit N)` because the nightly does not name them either.

### Landing gear — the polarity is pinned by one line

`pilot_school.c` never calls `TASK_PLANE_MISSION`; it uses `TASK_VEHICLE_MISSION_COORS_TARGET`
plus `CONTROL_LANDING_GEAR`, a native this mod had **never called at all** — it only read
`GET_LANDING_GEAR_STATE` once for a status line.

```
pilot_school.c:114940
    if (IS_ENTITY_IN_AIR(veh) && GET_LANDING_GEAR_STATE(veh) == 0)
        CONTROL_LANDING_GEAR(veh, 1);
```

That single statement fixes read-state **0 = DOWN** and write-command **1 = RETRACT**, which is
the only part that matters: reversed, the assist raises the gear on final approach. Corroborated
by `:107352` (states {3,0} vs {4,1} vs 5-broken), `:114585` (state 4 = gear-up success),
`:114644/658/671` (states {0,3,5} airborne = lesson failure), `:114174` (command 0 while placing
a plane on the runway).

### The three aviation gates (all OFF)

- **`planeGearControl`** — retract on Takeoff→Cruise, extend before the approach task is issued
  so extension overlaps the descent rather than the flare. Broken gear (state 5) is **spoken**,
  not worked around: otherwise the go-around watchdog charges the crash to the runway and
  spends one of three attempts on a problem retries cannot fix.
- **`planeTaxiAssist`** — new `PlanePhase.Taxi` using bit 23. Not hypothetical: the shipped
  `RUNWAYS` table puts the LSIA spawn ~560 m from its own `runwayStart`, and the Sandy Shores
  spawn sits **on the runway END**. Style is `PlaneTaxiMode | ForceStraightLine`, deliberately
  *not* Rockstar's 262144 — their call is an airborne goto, and a runway apron has no vehicle
  path nodes for a node-following style to follow. A taxi that cannot reach the threshold in
  60 s departs anyway and says so.
- **`heliLandModeV2`** — mode 9 instead of 20. Mode 20 appears in **no** corpus script;
  `am_heli_taxi.c` lands passengers with 9, twice. Not proof 20 is wrong, so: a gate.

`EVENT plane-phase` ships **ON** (behaviour-neutral), carrying gear state, AGL, airspeed and
distance-to-destination at every transition. The runway coordinates are community-sourced and
**cannot** be verified from script — runway geometry lives in map data — so recording what the
aircraft was actually doing is the only way a session can settle them.

- **`autodriveStyleV2`** (OFF) — Rockstar's `am_taxi.c` style **1076627627** = the mod's 786603
  plus `UseWanderFallbackInsteadOfStraightLine`, `UseSwitchedOffNodes`, `ForceJoinInRoadDirection`.
  Cruise instead of beeline when the route fails; reach driveways and car parks; never join a
  road backwards.

---

## W4 — recovered backlog

- **`xyDistanceFix`** (OFF) — all four scans computed ground distance as `3D − |Δz|`, which is
  not a projection of anything. A prop 10 m out and 6 m up is 8.0 m away on the ground; the old
  form said 5.66 m. Always an under-report, growing with slope, spoken with full confidence.
  Both values logged so one session settles it.
- **`groundHeightV2`** (OFF) — the curb/drop probe tested `> 0.01f` to mean "ground found",
  conflating *no ground* with *ground at sea level* along the entire Los Santos shoreline. The
  nightly's out-parameter form separates them. Gate OFF keeps the legacy ambiguity exactly, so
  the probe's tuning is unchanged until a session says otherwise.
- **`pathFindRuntimeV2`** (OFF, **telemetry only, drives nothing**) — samples nightly
  `PathFind`/`PathNode` against the bundled graph and logs position and flag agreement. Every
  read is validity-checked (`IsLoaded`, non-zero `MemoryAddress`, non-zero position) before it
  is believed, because this is pattern-scanned memory and **a plausible zero is worse than an
  exception** for a fail-closed mod: silently-zero is indistinguishable from "no junction here".
  An exception is counted as a refusal, never as agreement.
- **`guardPatrol` / `guardWeaponFile` — planned and DROPPED.** Both already ship: Auto-Patrol is
  bodyguard-menu item 17 (30 s stationary, 50 m foot, 100 m mounted) and per-guard weapons
  already read `ModSettings/guardWeapons.json` with keys `butler`/`guard1..6`, failing closed on
  an unknown weapon name. A second switch for a feature that has one is a row a blind user
  arrows onto, toggles, and hears nothing happen.

### Guard patrol keeps the whole detail with the vehicle

The stay-in-the-vehicle rule was keyed on index 0, so only the butler was exempt. Every other
guard fell through to `TASK_WANDER_STANDARD` — trigger a patrol while sitting in a car and the
rest of the detail climbs out and scatters, then the player drives off without them. Now any
guard seated in the *player's* vehicle stays seated, and any guard on foot with a free seat
waiting is left to the existing entry logic. Seats are counted and claimed nearest-first so one
empty seat does not excuse six guards. The patrol-end path no longer clears a seated guard's
task either — that is how you get a guard stepping out of a moving car. The announcement says
which happened instead of claiming everyone is patrolling.

---

## Settings added (147 → 158)

**ON:** `listenerFrame` (1) · `scanAudioSweep` (1) · `hostileDetection` (1 = today's behaviour).
The spatializer itself is not a setting — it replaces the panner outright, with `listenerFrame 0`
as the escape hatch.

**OFF:** `spatialElevationCue` · `planeGearControl` · `planeTaxiAssist` · `heliLandModeV2` ·
`autodriveStyleV2` · `pathFindRuntimeV2` · `xyDistanceFix` · `groundHeightV2`.

---

## Verification — NOTHING BELOW HAS BEEN PLAYED

Offline: `dotnet build` clean · `verify-vehicle-tasks.py --verify` 0 failures ·
`triage-log.py --selftest` and `triage-menulog.py --selftest` both pass · deployed, DLL synced,
18 data files current.

1. **Confirm the banner reads `iter62-spatial-hostiles-aviation`.** A mismatch is a stale DLL
   and the session is worthless.
2. **Audio first, alone, on foot.** Vespucci pavement, standing still. A ped walking past must
   audibly cross **front to back**, not just left to right. A car behind must sound behind
   without relying on the double tick. `EVENT spatial-fallback` must be **absent**.
3. Toggle `Sound Direction Reference` 1 → 0 mid-session: the soundscape must stop following the
   camera. Then `spatialElevationCue` ON near a footbridge or stairwell.
4. **NumPad 6** in a doorway, **NumPad 8** facing an alley mouth. The opening must be audible as
   a gap in the eight-ping sweep *before* the speech names it. Then `Scan Audio Sweep` = 2
   (sound only) to hear whether the sweep alone is enough.
5. **Hostiles:** `hostileDetection` 1 → 2 → 3 across one populated session (Vespucci /
   Strawberry). Level 3 must name and place armed peds; the all-clear must fire when the last
   one dies.
6. **Aviation, one gate per flight.** `planeGearControl` LSIA→Sandy, reading `EVENT plane-phase`
   for gear state at both transitions. Then `planeTaxiAssist` — watch for the taxi actually
   reaching the threshold rather than timing out. Then `heliLandModeV2` on a heli taxi.
7. **`pathFindRuntimeV2` ON for one whole session, telemetry only.** Read
   `EVENT pathfind-census` — `agreedPos` and `flagAgree` against `samples`, and `refused`. Do
   not propose switching any consumer until that number exists.
8. `xyDistanceFix` ON, then a scan on a hillside: `EVENT xydist` gives old vs new side by side.
9. `python tools\triage-log.py`, read only `## Read this first`, then `tools\package.ps1`.

## Not in scope

Drive-assist behaviour of any kind — the recovery-ladder convergence defect and the missing
motorcycle path both remain the standing drive-assist candidates. The blanket SHVDN deprecation
sweep (239 warnings; `World.GetDistance` alone is 156 sites) stays its own mechanical iteration;
only the two deprecations this work actually depended on were touched. Guard stealth mode, per
`butlerguide.txt`'s own reasoning that it drops guards below follow speed.
