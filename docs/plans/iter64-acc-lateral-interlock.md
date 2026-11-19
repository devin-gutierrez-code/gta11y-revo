# iter-64 — do not accelerate out of the lane, and stop over-counting what it cost (retro note)

Banner: `iter64-acc-lateral-interlock`. **Built and never played.** No session in
`tools/log-index.json` carries this banner; the two 08-19 logs it was written from both ran
`iter63-baseline-visibility-governor-bisect`.

**This file was written retrospectively on 2026-08-31**, during the iter-71 reconciliation of
`DRIVE-ASSIST-REVIEW-2026-08-29.md`. iter-64 shipped without a plan file. It is reconstructed
from the code and tooling comments that cite it — `GTA11Y.cs:1078`, `:1086`, `:1345`, `:2530`,
`:13234`, `:21645`, `:22394`, `:43466`, `:43532`, `:43603`, and `tools/triage-log.py:182`,
`:213`, `:451`, `:542`, `:1113`, `:1781`, `:2235`, `:2321`, `:3173`, `:4272` — not from a
contemporaneous document.

## The incident it was written from

`driveassist-2026-08-19-051310`, incident 29. ACC took throttle ownership at `lat=4.28` against
a 5.3 m half-carriageway and held `thr=1.00` while the mod's **own** lateral estimate crossed
the entire road, +4.3 m to -7.3 m, accelerating 15.4 to 32.4 m/s in 2.1 s. Achieved steering
never exceeded 0.22. The brake arrived one frame before a 28.6 m/s impact.

The wedge that followed is what sent the recovery controller onto the **opposing** carriageway,
which is where that session's cascade actually came from. So this is the first link, not a late
symptom.

**Nothing anywhere stopped ACC opening throttle while the car was leaving its lane.**

## W1 — `accLateralInterlockV2`, shipped OFF

`ComputeACC` (`GTA11Y.cs:22394-22470`). Withholds positive throttle when any of three
conditions holds, each logged separately under its own `why=` so a session can tell "the lane
model went dark" from "the car really was out of its lane":

- `why=lat` — `|lastLaneLateralError|` exceeds the carriageway half-width plus 1.5 m. The
  formula deliberately matches `EVENT offroad-risk`'s additive margin so the veto and the
  early-warning line can never disagree about where the road ends.
- `why=skew` — `vehicleSkewAngle` over 20 degrees. That sits above ordinary cornering and below
  the overshoot regime: incident 29 read 15.8–16.3 one second before impact and 73.6 at the wedge.
- `why=stale` — the lateral estimate is older than 1 s. **Fails closed.** If we cannot say where
  we are, we do not accelerate. An unexpected refusal to speed up is recoverable for a blind
  driver; an unexpected acceleration into a barrier is what 08-19 cost.

`ACC_INTERLOCK_MIN_SPD = 5` keeps it clear of the standstill and creep logic, which have their
own owners. **`accBrakeOut` is untouched**, so a genuine headway threat still brakes — the gate
only ever *removes* acceleration, which is why it cannot strand a car that is already moving.

Shipped OFF on the standing rule: it withholds throttle, and a false veto on a motorway is its
own kind of failure. (Promoted to ON in iter-71, with the freshness guard on `ilSkew` that this
iteration left out — see A4 there.)

## W2 — the settings that escaped the ledger

`driveassist-2026-08-19-051310` was run specifically to A/B **Precise ground detection**
(`groundHeightV2`), and **the log cannot say what it was set to.** Every gate emitter iterates
`GATE_SETTINGS`, and `groundHeightV2` was not in it. The player toggled the menu item
mid-session and the mod wrote nothing: no `gate-toggle`, no manifest re-emission, no epoch
boundary. An experiment ran and left no trace.

The iter-46 claim in that block — "a gate added later cannot escape the ledger" — is true of the
*consumers* and false of the *membership*. Adding a row to the `ids[]` list does not add it to
`GATE_SETTINGS`. Twenty-two settings were added at `:1086`, including `steeringAssist`,
`adaptiveCruise`, `roadTeleport`, `shapeCasting`, `vehicleGodMode` and `driveAssistBaseline`
itself, none of which had ever been attributable.

None of the newly-listed settings has a `GateReach` call site, so they are also listed in
`GATE_UNINSTRUMENTED` — otherwise the census reads `reach:0` as "the gate has no call site"
rather than "the gate is a plain setting". iter-71's `check_gates.py` is the standing guard for
exactly this pair of lists getting out of step.

## W3 — the state of the car, in every message that speaks

`HealthTag` (`:43466`), `WheelDamageTag` (`:43603`) and the airborne-episode tracker (`:43532`).

`AUTHORITY-BLACKOUT` **speaks** "Drive assist has lost steering. Take the wheel." at a blind
driver. It already suppressed that cue on two-wheelers, because a wrong label is worse than no
label. **A wrecked car is the same class of false positive** and was neither suppressed nor
recorded — a driver whose engine had failed heard the mod blame its own steering. iter-64
records it first, on the theory that a session decides the suppression rule, not the author.

The same two tags went onto every obedience emitter, plus body roll and pitch, wheel-contact
mask and per-wheel steer limit (a damaged wheel lowers its own steering authority, which is a
mechanical fact the controller had no way to see). Airborne and road-departure become episodes
with a start, an end and a duration rather than per-frame guesses, and wheel-state lines that
had been suppressed while nominal are now unconditional — a suppressed nominal reading is
indistinguishable from a missing one.

## W4 — triage: five ways the numbers were wrong

1. **Settle-window double counting** (`:451`, `:542`, `:4272`). A marker with its own
   `EVENT marker-settle` owns a `[t, t+winMs]` slice of damage. Any *other* marker landing in
   that slice without a settle of its own took the header delta as if it were its own. Incident
   29 was reported at 838.7 against a true 418 — **2.0x** — and incident 38 at 1448.4 against
   1184. The ranking survived; every absolute number downstream did not, including the trend row
   in `log-index.json` that future sessions compare against.
2. **An independent bound on incident cost** (`:1781`, `:2235`). Marker costs come from three
   different sources and their sum had never been checked against reality. `incident_health_delta`
   reads the FRAME lines directly. It returns `None` — never a guess — when a vehicle change
   falls inside the window, because a fresh car resets health to 1000 and a delta across that
   boundary is meaningless rather than merely noisy.
3. **`HOSTILE_RE` had never matched a single line in the archive** (`:1113`). It looked for
   "hostiles detected"; the mod says "1 hostile." and "2 hostiles, police among them." A
   detector reporting zero because it is listening for words the mod does not say is worse than
   no detector.
4. **Say *why* the car was not moving** (`:3173`). One immobility span rendered as
   `pedalHeld=66% own=none:87,reverse:57,ramped:31`, which reads as controller thrash. The mod
   had logged the real answer in the same frames: `contact=0000` on 246 of 329 wheel blocks —
   all four wheels clear of the ground under full throttle. High-centred, not a controller
   failure. A different span ran 35.2 s at full engine health with contact intact, a completely
   different failure, and the two printed identically.
5. **"No window reached either tier" beside a 2110-point drawdown** (`:2321`). Two different
   facts were being printed as one sentence, so a session with 13 damage episodes — all of them
   within 3 s of a marker — read as though nothing had happened.

## What has never been checked

The interlock has not run in a game. Its verification is item 10 of the iter-71 plan, and the
criterion there is worth restating: **a lower marker rate alone is not success.** ACC must stop
owning positive throttle when lateral error exceeds the carriageway margin and must *not*
oscillate ownership through ordinary curves; and `why=stale` dominating means the lane model is
going dark, which is a different bug and must not be read as the interlock working.
