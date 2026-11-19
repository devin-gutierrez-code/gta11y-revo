# Changelog

Everything that changed between the original **gta11y** by liamerven
(<https://github.com/liamerven/gta11y>, last updated February 2022) and this fork,
**GTA11Y - Revo**.

This is written for the person playing the game, not for the person reading the code.
Where a feature has a switch, the switch's exact name in the Settings menu is given so
you can find it by arrowing through the list.

---

## San Andreas Wildfires (unreleased, rides on iter71)

- **New setting: "San Andreas Wildfires"**, second-last row of the Settings menu, ships
  OFF. A chaos feature. When on, any burning car or person, or any fire you did not start
  through the mod (molotov, explosion, fire ammo), within 150 m sets off a wildfire.
  - It burns on everything except water and spreads square by square, faster with the
    wind and uphill, but it also creeps sideways and slowly against the wind, so it grows
    into a burning area rather than a line. Embers start spot fires 12 to about 40 m
    downwind, but only while the fire has room to grow.
  - Each patch burns for 35 to 70 seconds. Rain slows it and shortens each fire. Burnt
    ground does not relight.
  - When the fire is as big as the game allows, it keeps the part nearest you burning and
    lets the far side go out, so it stays around you instead of running off downwind.
    The first version did the opposite and the fire raced away within seconds.
  - Fire brigades are called to the fire front nearest you, up to two at once, and a
    line they put out stays out until embers jump it.
  - **Fire crews now fight the fire.** Before this, trucks arrived, sat with sirens
    blaring, nobody got out, and they stayed until you walked away. The game's own crews
    ignore fires the mod starts. Now, when a truck stops within 60 m of the fire, the
    mod takes it over:
    - The siren sound stops. The lights keep flashing.
    - The driver sprays the water cannon at fire within 30 m of the truck. Otherwise
      the driver gets out too.
    - The rest of the crew gets out with fire extinguishers, runs to the nearest
      burning patch and sprays it. They are fireproof, and they do not run from the
      flames.
    - When there has been no fire within 80 m of the truck for 10 seconds, or after
      5 minutes, the crew gets back in and drives off with traffic. After 30 seconds
      the truck leaves without anyone who has not got back in. Once it has left, the
      game removes it the normal way.
    - No new brigade is called while two crews are already working.
  - People and drivers near the flames run or drive away. Mission characters and your
    bodyguards are left alone.
  - Fire more than 350 m from you is removed, and turning the setting off removes all
    of it and cancels the fire brigade calls.
  - No spoken per-fire announcements. The one thing it says is "Wildfire could not
    create any fire" if it is on and nothing will light.
- **New setting: "Drive assist slows for wildfire on the road ahead"**, last row, ships
  OFF. Drive assist brakes toward a crawl 10 m before fire on the road it is about to
  drive, and says "Fire on the road, N meters" at most every 15 seconds. It slows you,
  it does not stop you, so you can still crawl through.
- Replaces the standalone `san_andreas_wildfires.cs`, which never loaded: it used
  syntax ScriptHookVDotNet's script compiler rejects, and used the wrong native to
  remove fires. The hold-Q toggle is gone; Q is the game's cover key.

## iter71-bridge-harness-authority-convergence

- **The three drive-assist fixes below ship ON**, which is a deliberate exception to the
  usual one-switch-per-session rule. **Debug: turn iteration 71's three drive assist
  changes off in one go** stands all three down together, so a drive with them off can be
  compared against the same drive with them on without arrowing through three rows.
- **Stop cruise control accelerating while the car is out of its lane or pointing across
  the road** is now on. Cruise control could previously open the throttle while the mod
  had already lost confidence in where the lane was — accelerating away from the road it
  was trying to rejoin. It only ever withholds acceleration; it never takes braking away,
  so it cannot leave a moving car unable to stop.
- **When the assist cannot find any road to recover to, hold still and say so.** The
  assist used to declare itself back in normal lane keeping the instant a recovery search
  came up empty, even though it knew lane keeping was invalid — then immediately re-enter
  recovery, over and over. It now holds, tells you, and escalates to the stuck-car rescue
  instead of round-tripping in silence.
- **Stop the stuck-car rescue repeating forever in the same place.** The rescue ladder
  could teleport the car to the nearest road, clear its own escalation state, and start the
  same twenty-second climb again at the new spot indefinitely. It now waits between
  teleports, carries its escalation across them, counts attempts at one location, and after
  three says out loud that it is handing the car back to you.
- Drive-assist reporting fixed in four places where it was quietly lying. The reason
  printed for a mode change belonged to a different mode change; a failed recovery search
  reported the position and distance of a candidate it had *rejected* as though it had
  found one; the cruise-control interlock read a steering-angle figure with no freshness
  check; and damage reports carried no fire, fuel-tank, roll, pitch or airborne state.
- Added an opt-in test-harness half to the diagnostic bridge (iter-65): read and write
  settings, place the player and vehicle at a named location with a heading and a speed,
  spawn and damage a vehicle, set time, weather and traffic density, and run a scripted
  scenario. Every world write is recorded in the drive log. This is what makes a
  drive-assist experiment repeatable without driving it by hand. It is off unless the
  bridge is explicitly enabled **and** `allow_world_writes` is set; `dist` packages ship
  both off.
- Triage: auto-drive markers are no longer pooled into the drive-assist failure rate — on
  one archived session 71% of the markers were the game's own task driving, not the assist.
  Comparison tables gained a vehicle-class column, because a session mixing motorcycles and
  cars was averaging two experiments into one number and the mod has no motorcycle-specific
  code path anywhere. The "previous session" comparison now orders by when the session ran
  rather than when the report was generated.

## iter70-animal-melee-setting

- Fixed **Animal direct melee** reporting that it was on without actually changing
  combat. The switch is now registered in the canonical settings collection, persists
  to `gta11ySettings.json`, defaults off, and controls both the direct-melee task and
  the target-stickiness/tactical-bypass behavior added in iter 68.

## iter69-intox-lifecycle

- Removed the unsafe build-specific bridge into Rockstar's drunkenness script globals.
  The existing setting now says once that the bridge is unavailable safely and uses
  the mod's owned intoxication model instead; it never writes unidentified game memory.
- Intoxication tasks, props, stashed weapons, movement, and ped flags now retain their
  owning character and are torn down on that character. Turning the feature off, changing
  protagonist, or reloading the script cannot leave a consume scene holding control.
- A drink or smoke now commits its dose when the enter animation completes. Cancelling
  before that gives no dose; cancelling the put-down or stand-up outro keeps the full dose.
- Safehouse animation waits for its audio bank as well as its dictionary and props, so
  cold-cache opening, pouring, and lighting sounds cannot fire before the bank is ready.
- Synchronized scenes use engine phase for normal completion and the hard watchdog for
  failure. The earlier timer no longer makes the hard-timeout path unreachable.
- **Impaired driving when intoxicated** and **Blackout takes the wheel** now operate as
  independent switches. Drive-assist suspension follows the effect that actually owns
  the controls, and stage announcements no longer claim driving effects that are off.

## iter68-animal-direct-melee

- Added the **Animal direct melee** bodyguard switch, shipping off for controlled field
  testing. When enabled, animal guards use Rockstar's dedicated model-native melee task,
  retain a live target, and bypass firearm-oriented flank, sniper, cover, and suppression
  task replacement. Damage remains entirely game-native; no simulated damage is used.

## iter67-animal-guard-combat

- Animal guards now keep their model-native unarmed attack instead of being equipped
  with a firearm they cannot animate. Mountain lions receive explicit unarmed-versus-
  armed combat permission and slot zero animals participate in automatic engagement.
- Guard combat tasks reject another active bodyguard as a target.
- Added `gta11y-bodyguards-<date-time>.log` in the GTA V folder. It records spawns,
  target assignments, two-second state samples, stalled attacks, and guard-on-guard
  combat so a silent AI failure leaves evidence.

## iter66-chained-guard-groups

- Raised the bodyguard limit from 7 to 21 by chaining three Rockstar ped groups. Guards
  6 and 13 act as group anchors while the existing combat coordinator controls all guards.
- Added cat, Chop, husky, poodle, pug, retriever, Rottweiler, shepherd, West Highland
  terrier, mountain lion, cow, and deer to the selectable guard models.

## iter65-mcp-diagnostic-bridge

- Added an opt-in, loopback-only diagnostic bridge and dependency-free MCP/CLI clients
  for live mod, player, vehicle, scanner, shapecast, and drive-assist state.
- Added bounded game-control injection with achieved-state readback, live screenshot
  requests, attributed drive-log markers, and a separate SAPI tester speech channel.
- Test-bed deployment enables the bridge; public `dist` packages explicitly disable it.

## iter64-acc-lateral-interlock

- Added **Stop cruise control accelerating while the car is out of its lane or pointing
  across the road**, shipped off. Cruise control could open the throttle while the mod had
  already lost the lane; this withholds acceleration in that case and never touches
  braking. (Promoted to on in iter-71.)
- Every takeover and failure announcement now carries what state the car was actually in:
  engine and body health, fuel tank, whether a wheel is broken and by how much its steering
  is limited, and whether the car was in the air. A takeover message that could not say
  whether the car was wrecked or merely stuck was the same message in both cases.
- Airborne and road-departure episodes are tracked as episodes with a start, an end and a
  duration, instead of being inferred from single frames.
- Fixed a class of settings that were invisible to the mod's own experiment ledger.
  **Precise ground detection** in particular could be toggled mid-session and the log
  recorded nothing at all — no change event, no re-issued settings list, no boundary in the
  report. A session was run specifically to test it and left no evidence that it had been.
- Triage: each incident now reports what the car actually lost across it, measured
  independently of the failure markers, so a marker count that misses damage is caught.

## iter63-baseline-visibility-governor-bisect

- The startup settings-drift line now **names** the settings that differ from the previous
  session instead of counting them. A count cannot tell you that a drive-assist switch you
  never chose was on from a saved settings file — which is exactly how one session ran a
  configuration nobody had picked for it.
- The eleven experimental drive-assist settings that **Debug: turn the eleven experimental
  drive assist settings off in one go** controls are now visible to the reports. One
  session flipped that switch nine times and every one of its seventeen segments rendered
  an identical settings list, so the report told a player who had just driven ten balanced
  minutes of a controlled experiment that no such experiment was present.
- The once-per-session warning about settings that depend on a switched-off sampler now
  re-arms on every configuration change. Firing once for the first of nine identical
  configurations is not a warning.
- Triage: added the comparison the switch exists for — failure rate with the eleven on
  against the same vehicle with them off.

## iter62-spatial-hostiles-aviation

- **Positioned sound now has a front, a back and a height.** Every cue previously ended in
  a single left-right pan number, so a target 30 degrees ahead-right and one 150 degrees
  behind-right produced an identical sound, and nothing had elevation at all on a map of
  staircases, overpasses and rooftops. Replaced with a proper 3D spatializer across all
  twelve cue sites. **Head-relative listener** is on; turn it off to get the old panner back.
  **Elevation cue** ships off.
- **One hostile detector instead of two.** Players without bodyguards were getting a cruder
  detector — direct combat only, no armed layer, no police tell, no all-clear — and neither
  path said *where* anything was. **Hostile detection** now takes a level: 1 is exactly the
  old behaviour and is the default, 2 adds relationships, police and an all-clear, 3 adds
  armed proximity. Announcements name and place the nearest two using the same listener
  frame the audio uses.
- Aviation behaviour checked against Rockstar's own shipping scripts rather than assumed,
  with a verification tool that fails the build on drift. **Plane landing gear**, **Plane
  taxi assist** and **Helicopter landing mode** ship off.
- A patrol order no longer empties your car. Only the butler was exempt from the wander
  task, so triggering a patrol while driving made the rest of the detail climb out and
  scatter. Any guard seated in your vehicle now stays seated, and the announcement says
  which guards actually went.
- Backlog fixes, all shipped off: **Flat-ground distances** (ground distance was computed
  as a subtraction that under-reports on any slope), **Precise ground detection** (the curb
  probe could not tell "no ground" from "ground at sea level", which is the entire Los
  Santos shoreline), and a pathfinding telemetry switch that drives nothing.
- Settings count 147 to 158.

## iter61-lanetarget-bisect-driftguard

- **`EVENT gate-drift` and a spoken startup note.** The mod now compares its settings
  against the *previous session's* and says at startup when they differ. The session this
  came from ran two drive-assist switches nobody had turned on that session, at more than
  eight times the failure rate of the session before it, and nothing said so.
- **Interaction Menu detection.** The Interaction Menu does not pause the game and does not
  read as a pause menu, so the assist believed a player sitting in a menu was driving, and
  kept firing take-over pulses at them. It is now detected and logged. **Pause drive assist
  while the Interaction Menu is open, and say so** ships off — a false positive here would
  stand the assist down mid-drive.
- Mode changes now record which rule decided them, how long the previous mode was held, and
  what a distance sentinel actually meant. Two opposite code paths wrote the same sentinel
  value and the log could not tell them apart.
- **Aim at the middle of a lane on multi-lane one-way roads** switched off for a controlled
  session, to attribute a regression to it or clear it.
- Triage: the two thrash rates (mode changes and road-model distrust, both per minute and
  both independent of the marker count) now appear in the report headline against a
  six-session median, along with which settings changed since the previous session.

## The short version

The original mod was a single 1,497-line script that told you where you were, what was
near you, and let you spawn cars and teleport around. It was a good foundation and most
of it is still here, untouched, doing the same job.

This fork adds roughly 36,000 lines on top of that. The headline additions are:

- **A drive assist** that brakes and steers for you, so you can drive a car on real
  roads without sight.
- **An autopilot** that will drive, fly, or walk you to a waypoint on its own.
- **A pause menu and phone reader**, so the game's own menus and the iFruit phone
  finally speak.
- **A rewritten obstacle scanner** that pulses faster as things get closer, tells you
  what kind of thing it is, and answers questions on demand.
- **A bodyguard system** with a butler who fetches your car, flies you places, and
  extracts you from trouble.
- **A status menu** with 87 readable readouts covering the player, the vehicle,
  aircraft instruments, the world, and navigation.
- **Civil unrest and consumables** — start a riot, or have a drink.

By the numbers:

| | Original | Revo |
|---|---|---|
| Lines in the main script | 1,497 | 34,147 |
| Source files | 5 | 16 |
| Top-level menus | 4 | 9 |
| Settings | 21 | 143 |
| Keys used | 11 | 22 |
| Shipped data files | 0 | 5 |

---

## 1. Navigation Assist — the obstacle scanner

The original mod had nothing like this. It was added early in the fork and then
completely rewritten.

**What it does now.** It scans continuously in four zones — left, centre, right, and
behind (behind only when you are actually reversing) — and beeps to tell you what is
there. It works on foot and in a vehicle.

- **Distance is the pulse rate, not the pitch.** Something far away ticks slowly;
  something close ticks fast. This replaced an older design where pitch encoded
  distance, which meant the pitch and the object type fought each other for the same
  channel.
- **Pitch tells you what it is.** Pedestrians are a warm triangle wave at 440 Hz;
  moving vehicles are a harsh sawtooth at 620 Hz; parked vehicles 480 Hz; walls and
  world geometry 330 Hz; low obstacles you would trip over, 1320 Hz. These notes were
  chosen so they do not collide with each other or with the 262 Hz brake warning.
- **A glide tracks closing speed.** A tone that slides up is getting closer; sliding
  down is moving away. It is capped at two semitones so it never becomes a siren.
- **Sounds come from where the thing is** — not just left or right. Every cue is now
  placed in three dimensions: the tone reaches your near ear slightly before your far
  one, sounds behind you go duller rather than merely quieter (which is how your ears
  tell front from back in real life), and distance takes the edge off the tone the way
  air actually does. A pedestrian walking past you now crosses from front to back
  audibly, instead of just sliding between the speakers.

  The old cue could not do this. It was a single left-right slider, so something 30
  degrees ahead of you on the right and something 150 degrees behind you on the right
  sounded identical, and nothing had a height at all. Behind was handled by giving it
  its own zone with a double tick an octave down — that still works and still sounds
  the same, but it is no longer the only thing keeping the two apart.
- **Height gets its own cue** if you turn it on. Anything more than 20 degrees above or
  below you is followed by a short two-note chirp, rising for above and falling for
  below. This is deliberately a *learned* signal rather than an attempt to fake the way
  real ears sense height: the real cue lives around 8 kHz, and these tones are far below
  that, so doing it "properly" would have been completely inaudible while looking
  correct in the code.
- **One meaning per channel, finally.** Three cues — the waypoint tracker, the mission
  marker and the hostile beep — were still bending their pitch to encode direction or
  distance, which contradicts the rule above that pitch tells you *what a thing is*.
  Both meanings had been shipping at once. Those three now hold a fixed note and let
  their position do the talking.
- **Shape casting.** Instead of a single hair-thin ray per direction, the scanner
  sweeps a capsule, so it catches thin poles, kerbs, and half-open doors that a single
  ray slips straight past. On foot it uses a 0.35 m capsule at knee height, which is
  what catches the things that actually trip you.
- **Beeps are enveloped.** Every tone now gets a ~5 ms attack and release, because you
  hear thousands of them per session and the raw square-wave click was fatiguing.

**Settings:**

- `Navigation Assist (Obstacle Detection)` — master switch. **On by default.**
- `Navigation Assist Detail` — Off / Alerts Only / **Nearest Obstacle** (default) /
  All Zones. "Alerts Only" mutes the ambient pulse layer and leaves the warnings.
  "All Zones" is the old always-on behaviour.
- `Enhanced Obstacle Detection (Shape Casting)` — **On by default.**
- `Detection Radius` — now thirteen steps: 10 / 25 / 50 / 100 / 125 / 150 / 200 / 250 /
  300 / 400 / 500 / 750 / 1000 m. The original offered four steps topping out at 100 m.
  **25 m by default.**
- `On-Foot Detection Range` — 5 / **8** / 12 / 20 m.
- `Sound Direction Reference` — Your body / **The camera on foot, the vehicle when
  driving** (default) / The camera, always. This decides what "ahead" means for every
  positioned sound. Camera-relative is the default because the camera is what your right
  stick aims and what your movement stick is measured against, so it matches what you
  are actually steering — and it lets you sweep the camera to scan a room by ear. If you
  would rather the soundscape stay put when you look around, choose "Your body".
- `Announce sounds above and below with a chirp` — **Off by default.**

### On-demand queries (NumPad Divide) — new

Rather than listening to ambient beeps all the time, you can ask:

- **Tap** — *Around Me*. Names the nearest obstacle in each zone in plain words
  ("pedestrian", "wall", "low obstacle", or the vehicle's actual name), then plays one
  positioned ping per zone in turn — ahead, right, behind, left. Zones that are clear
  stay silent, so what you hear is the shape of the space rather than a list of it.
- **Double-tap** — *What's Ahead*. Fires an eight-direction fan of casts and reports
  what is in each. It is spread across two ticks so it does not stutter the frame rate.
  It now also sweeps eight pings across that fan, left to right, each one placed where
  its ray actually hit. **A doorway is a hole in the sweep** — you hear the gap as the
  sweep passes it, at the moment it passes, without it having to be counted, ranked and
  named first. The spoken version only ever reported the best two openings.
- **Hold** — *Where Am I*. Street, zone, and orientation.

`Scan Audio Sweep` controls this: Off (speech only) / **Speech, then sound** (default) /
Sound only, no speech.

---

## 2. Drive Assist — new

The single largest addition. The original mod could not help you drive at all.

Drive assist watches the road ahead and takes over the brake and steering when it has
to. It is off by default and has three modes, cycled from `Steering Assist Mode` in
Settings:

- **Off** — no intervention.
- **Assistive** — nudges. It will correct your line and brake for hazards, but you are
  driving.
- **Full** — it takes over. Adds adaptive cruise control (see
  `Adaptive Cruise Control (Full Mode)`).

**What it handles:**

- Emergency braking for vehicles, pedestrians and walls, with a time-to-collision model
  rather than a fixed distance, so it brakes earlier at speed.
- Curve braking — it reads the road ahead from the shipped node graph and slows for
  bends before you reach them.
- Adaptive cruise: follows the car in front, and releases both brake and throttle when
  you come to a stop behind one instead of grinding against it.
- Lane centring against a road polyline (`Lane Centering`, experimental). On a two-way
  road this aims half a lane to your side of the centre line, which is right. On a
  **one-way or divided road with two or more lanes** it used to aim at nothing at all —
  and the road node in that case sits exactly on the painted line *between* the lanes, so
  the assist was steering you along the lane divider by design. In a recent test session
  that was 27% of the driving, and it is the reason the car sometimes tracked close to a
  lane edge. `Aim at the middle of a lane on multi-lane one-way roads` (off by default)
  fixes it, and also stops the guidance being pushed off the road surface on bends — the
  check that prevented that only ever ran for the point under the car, not for the points
  further ahead that the steering actually aims at.
- Junction slow-down and spoken junction announcements (`Junction Slow-Down and
  Announcements`).
- Wrong-way detection (`Wrong-Way Driving Alerts`).
- Off-road detection — it knows when you have left the road surface, not merely when
  you are far from its centre.
- Per-vehicle geometry: wheelbase, steering lock, rear overhang, and braking distance
  are read from the vehicle you are actually in, so a truck is not steered like a hatchback
  (`Adapt steering and braking to the size and weight of the vehicle you are driving`).

**Pull Over** (`Pull Over — stops you at the kerb on your own side of the road`, off by
default). Stops you at the kerb rather than in the middle of the road, and picks the kerb
you can reach without crossing oncoming traffic. If the mod is driving it parks you there;
if you are driving it says which side the kerb is on and how far, and eases the speed off
— touching the throttle cancels it at once. The same kerb target is used by *Park at
Nearest Safe Spot* and by auto-drive's arrival, which previously both stopped on a road
node, i.e. the middle of the carriageway.

**Reading the game's own road data.** For a long time everything the assist knew about
lane counts, road width, junctions and being stuck came from data files shipped with the
mod plus a lot of hand-tuning. The game itself will answer all of it directly, and
Rockstar's own taxi scripts have been doing exactly that since the game shipped. That is
now wired in, behind switches:

- Live lane counts and road width instead of an estimate, for straighter lane keeping
  (`Use the game's own lane width instead of an estimate`).
- Junctions, traffic lights, **give ways** and **dead ends** anywhere on the map, not
  only at the 82 intersections the shipped data covered
  (`Announce junctions, give ways and dead ends anywhere on the map`).
- A fix for the lane reference snapping onto driveways and side turnings
  (`Stop the lane reference snapping to driveways and side turnings`).
- The game's own opinion of whether you are stuck, so recovery starts sooner
  (`Let the game tell the assist you are stuck`).
- Freeing a wedged car using the game's own steering and braking rather than the pedals
  (`Free a wedged car using the game's own steering and braking instead of the pedals`) —
  a second, independent way of actually reaching the car, which matters given the
  actuation history below.
- Damage that is **gunfire** rather than a crash is now called that, and recovery no
  longer fights it (`Say when damage is gunfire rather than a crash`). Nearly two-thirds
  of the "damage" in one test session turned out to be police shooting at the car.

Four further switches only record measurements and change nothing you can perceive; they
exist so the above can be proven on the road before being trusted.

**Stuck recovery.** A four-layer system for when you end up wedged against a wall, in a
ditch, or nose-to-nose with a parked car. It escalates: wait, reverse, pivot in place,
and finally teleport to the nearest road (`Auto-Teleport to Road`). When it genuinely
cannot free the car it says so and hands control back — and, since the "safer handover"
work, it tells you what is in front of you first rather than dropping you into a hazard
in silence.

**Spoken cues** include *"Braking. Obstacle ahead."*, *"Emergency brake!"*,
*"Water ahead!"*, *"Stop. Drop ahead"*, *"Stopped in traffic. Driving clear."*,
*"Waiting for traffic."*, *"Clear to proceed."*, *"Drive assist can't free the car.
You have control."*

**Haptics** (`Controller Haptic Feedback`, off by default). The rumble native has no
left/right motor selector, so the side is encoded as a pattern instead: a single short
pulse means left, a double pulse means right, and a long low buzz means road edge.

### The warning tones, and what they mean

Drive assist makes three sounds that are not speech. They were never written down
anywhere, which meant the only way to learn them was to guess. All three can now be
silenced together with `Drive assist warning beeps` (on by default) — **speech is not
affected by that switch**, and neither are the obstacle-scanner beeps, which have their
own settings.

| sound | what it means | how to read it |
|---|---|---|
| a clean tone that rises in pitch | **steer around something ahead** | the higher the pitch, the less time you have. It also repeats faster as it gets more urgent. |
| a buzzing tone, always the same pitch | **brake now** | faster and louder means sooner. It starts about 1.8 seconds before impact and saturates at 0.2. |
| a low growl | **the road ahead is blocked and the assist is holding the car** | you are pressing the throttle and it is not going to move until the way clears. |

The rising tone had a bug that made it useless: it was supposed to sound at most once
every two seconds, but the timer it checked was never reset, so it restarted every single
frame — around forty times a second. The 80-millisecond tone never finished playing, so
the pitch, which is the entire message, could not be heard as pitch. It sounded like a
stutter and was reasonably mistaken for a debug artifact. It now paces itself properly,
from about one beep a second when a hazard is distant down to four a second when it is
imminent.

The same bug was silencing nothing but repeating a phrase: *"Handbrake turn left"* shared
that broken timer and was spoken 55 times in a single 20-minute session. It now has its
own four-second limit.

### The actuation fix

Worth calling out on its own, because it explains why drive assist behaved so poorly
for so long. The mod was disabling the same game controls it was then trying to write
to — so its own brake and steering commands were being thrown away before they reached
the car. Roughly two dozen rounds of tuning were built on top of a control channel that
was never connected. This is fixed (`Real braking and steering`, on by default), and
most of the drive-assist switches that came after it exist to manage the consequences of
the commands suddenly becoming real.

### When the assist is not actually driving

Three things could go wrong silently, which for a mod you listen to rather than look at
is the worst way for anything to fail.

**The car being driven by the game, not by the assist.** To free a wedged car the assist
could hand the wheel to the game's own driving code. While that was happening the assist
had no steering and no brakes — and did not know it, so it went on talking as though it
were driving. In one recorded case the game reversed a car from a standstill to 37 mph
into a row of concrete pillars, and in another it reversed a car down a river bank into
the water. That hand-off is now short, is refused unless the space behind the car is
actually clear, is re-checked continuously, and is cancelled the moment the reason for it
goes away.

**Losing the steering without saying so.** If the assist is asking for steering and the
wheels are not moving, it now says *"Drive assist has lost steering. Take the wheel."*
This has no switch. A feature that fails quietly is indistinguishable from one that was
never installed.

**Being told you have control when you do not.** When the assist gives up on a stuck car
it hands back to you. If the game is still driving the car at that moment it now says
*"The car may still be moving on its own. Brake now."* instead of *"You have control."*

**Water.** The assist would only watch for water ahead above about 18 mph, so a car
rolling slowly toward a shoreline was never checked — and one ended a session sitting in
a river. It now watches at walking pace, brakes and warns at any speed, and abandons a
reverse that is heading into water. There is no switch for this one either. It is
deliberately cautious about what counts: on a slow approach it wants water at wheel level
and off the road network, so bridges, piers and waterfront roads stay quiet.

Three further switches, all **on** by default and all individually revertible:
*"Stop something in front of the car from cancelling a reverse when the way behind is
clear"*, *"Keep trying longer before giving up on a stuck car, so it can move itself
clear"*, and *"When the assist hands back a stuck car, say what is in front of it,
including low things like kerbs and fences"* — the last because the assist could
previously report thirteen metres of clear road while the car was nose-in to a fence.

### The revertible-switch system

Drive assist behaviour is exposed as 55 individually named switches in the Settings
menu, each written in plain language describing what you will *feel or hear*, not what
code it runs — for example *"Stop the assist braking against your own throttle"*,
*"Say why the pedal is being held"*, *"Brake when off the road surface, not just far
from it"*.

They are there so a bad change can be switched off in-game without waiting for a new
build. Switches that have been proven in road testing default **On**; anything still
under evaluation ships **Off** and is enabled one at a time, so a session's result is
attributable to exactly one change. 42 of the 139 settings currently default on.

A handful of switches say outright that they *"change nothing you can feel"*. Those only
record measurements, and they are how anything else here earns the right to be turned on.

---

## 3. Auto-Drive / autopilot — new

A whole new top-level menu, plus **NumPad Multiply** to start and cancel.

It adapts to what you are doing:

- **In a car** — drives you to your waypoint, or wanders.
- **In an aircraft** — full autopilot: climbs to cruise altitude, flies to the
  waypoint, searches for a landing zone, and lands. Altitude is set with the Up/Down
  arrows.
- **On foot** — auto-walk to the waypoint.

Speed is set with the Left/Right arrows in **5 mph steps** (an early version stepped in
metres per second, which produced unusable numbers when spoken).

**36 menu items:** 32 driving-style flags plus four actions — *Land*, *Park at Nearest
Safe Spot*, *Hitch to Nearest Trailer*, and *Refresh Driving Task*. The flags are the
game's own driving-style bits, named in plain English where their meaning is known and
honestly labelled "Unknown (bit N)" where it is not.

**Six of those names were wrong, and one flag could not be set at all.** The list had
been maintained by hand against community documentation. Checked against the game
library the mod is actually built on, "Take shortest path" turned out to be *use shortcut
links*, "Ignore roads" turned out to be *prefer navmesh routes*, and four flags labelled
"Unknown" have real names — including *avoid restricted areas* and *plane taxi mode*.
Worse, the last flag, *stop at the destination*, was unreachable: the code that packs the
switches into a number stopped one short, on the mistaken belief that setting the top bit
would break the game's own function. It takes a bit pattern, not a number, so it never
would have.

The list is now generated from the game library rather than typed, and there is a check
that fails if the two ever drift apart again. Seven flags remain "Unknown (bit N)"
because the library does not name them either — that is honest, and better than a
plausible guess a player would then act on.

- `Use Rockstar's taxi driving style for auto-drive` — **Off by default.** Rockstar's own
  taxi missions use a slightly richer style than the mod's default: it cruises instead of
  beelining when the route fails, it will use dirt roads and car parks so "drive me
  there" can actually arrive, and it never joins a road facing the wrong way.

The final approach is a hybrid: cruise on the road graph, then navmesh, then park.

---

## 4. Request Plane Flight — new

### Landing gear, and taxiing to the runway

Two additions checked against Rockstar's own flight-school script rather than against
community documentation:

- `Autopilot raises and lowers the landing gear` — **Off by default.** The mod had never
  touched the landing gear at all. It read the gear's position once, for a status
  readout, and never moved it. So the autopilot could rotate with the gear down and flare
  with the gear up — and when the resulting crash failed the landing, the "try again"
  watchdog would score it against the runway and spend one of its three attempts on a
  problem no number of retries could fix. Gear now comes up once established in the
  climb and goes down before the approach begins, and if the gear is damaged the mod
  **says so out loud**, because that is not something you can otherwise find out.
- `Autopilot taxis to the runway before takeoff` — **Off by default.** The aeroplane
  spawns at a point that is not always the end of the runway: at Los Santos airport it is
  about 560 m from the runway threshold, and at Sandy Shores it sits on the *far* end of
  the strip. Taking off from there means departing from the wrong end, or from the apron
  beside it. With this on, the plane drives to the threshold first and then rotates. If
  it cannot get there within a minute it takes off anyway and tells you — a taxi that
  cannot finish must never strand the flight.
- `Use Rockstar's helicopter landing mode` — **Off by default.** The mod's helicopter
  landing used a mode number taken from a community list. It appears in none of
  Rockstar's own scripts; their helicopter-taxi script lands passengers with a different
  one. That is not proof the old one is wrong, so it gets a switch and a test flight
  rather than a silent swap.

Its own menu. Pick a fixed route between two runways, or:

- **Fly to waypoint** — flies from the runway nearest you to the runway nearest your
  waypoint, then continues by road in a spawned car. Door to door.
- **Helicopter taxi to waypoint.**
- **Pilot: AI or Player** — either fly it yourself with autopilot assistance, or have
  an NPC pilot fly while you ride.

It refuses sensibly: it will tell you if the waypoint is over water, if the runway
coordinates are unusable, or if the nearest airport to you is also the nearest to your
destination.

---

## 5. Bodyguards and the Butler — new

**Guards stay with the car.** When you stood still for 30 seconds, the guards went on
patrol — and the rule that kept the butler in the driver's seat applied only to the
butler. Everyone else got pulled out of the vehicle and sent wandering on foot, so you
would drive away and find you had left the detail behind. Now any guard sitting in your
vehicle stays in it, and any guard on foot with a free seat waiting is left alone rather
than sent off. Seats are counted, so one empty seat does not excuse the whole detail. The
announcement says which happened instead of claiming everyone is patrolling.

A 36-item menu covering an AI companion system.

**Guards.** Up to seven. Configurable model, weapon (globally or per guard slot),
combat style, formation type, formation spacing, armour level, god mode, auto-respawn,
and auto-patrol. Commands: send to waypoint, hold position, follow me, attack my
target, cease fire, recall all, dismiss last, dismiss all, plus a status readout.

**The Butler** is the primary guard, and he is the useful one if you cannot see:

- **Vehicle delivery / valet** (`Butler Vehicle Delivery`). Spawn a car from the
  vehicle menu and instead of it appearing next to you, the Butler drives it to you
  from a configurable distance and hands it over.
- **Ground extraction** and **helicopter extraction**, each with a configurable
  standoff distance. He drives or flies to you, and tells you when you are aboard.
- **Water support** — he will bring a boat.
- **Butler beacon** — a directional audio beacon so you can find him.
- **POI narration** — he tells you what you are passing as he drives.
- **Evasive driving** when things go wrong.

**Threat detection.** `Proactive Detection` warns of threats before they engage;
`Armed Ped Alert` says *"Someone near you has a gun."* Guard callouts can be toggled
separately so you are not drowned in chatter.

---

## 6. Pause Menu and Phone Reader — new

For a blind player this is arguably the most important addition: the game's own
interface now speaks.

**Settings switch:** `Pause Menu and Phone Reader`. **On by default.**

- **Esc** opens a readable pause menu. This is deliberately the *unpaused* frontend —
  GTA V's real pause menu suspends every Script Hook script, which means a reader
  running inside one cannot read it. **P** still opens the normal paused menu if you
  want it.
- Arrow keys / WASD navigate, Enter accepts, Backspace or Esc cancels.
- The iFruit phone reads too — apps, contacts, and rows.

Labels come from a generated table (`gta11y-menulabels.json`) built from the game's own
`pausemenu.xml`, the `eMenuScreen` and `eMenuPref` enums, and the game's localised
strings, refined by a calibration pass against a real game build.

There is an **honesty gate**: if the reader cannot confidently identify a row, it says
*"unknown setting"* rather than guessing. A wrong label is worse than an honest
admission when you cannot see the screen to check.

Fast arrowing is coalesced — you hear the first and final selection, not every row in
between.

### Settings rows now say what they are set to

The reader could always tell you a row was called *Vibration*. It usually could not tell
you whether vibration was **on**. Only the graphics pages had values, because those
happen to live in a file the mod can read; the rest — Display, Controls, Voice Chat,
Mouse, Camera, Notifications, Rockstar Editor — named themselves and stopped. And
pressing **Left** or **Right** to change a setting was completely silent, because the
game gives a script no signal at all when a value scrolls. You changed something and
heard nothing until you closed the whole menu.

Both are fixed:

- **Left / Right now speaks the new value** as you change it — *"Music Volume, 8 of
  10"*, *"Output, Headphones"*, *"Vibration, off"*.
  **Settings switch:** `Say a setting's new value as you change it with Left and Right`.
  **On by default.**
- **The reader teaches itself where each value lives.** The first time you change a row,
  it watches which of the game's settings moved and remembers that row from then on —
  in this session and every session after. Nothing to configure; just change a setting
  once and it can read it back forever.
  **Settings switch:** `Learn what each setting row controls the first time you change
  it`. **On by default.**
- **Radio Station** and **Measurement System** are read straight from the game.

Two pages still cannot announce a value *mid-change*: **Graphics** and **Advanced
Graphics** are only written to disk when the menu closes, so the reader stays quiet
rather than read you a value that is one keypress out of date.

### How far down a list you are

**Settings switch:** `Say how far down a settings list you are`. **On by default.**

Rows can announce their position — *"Brightness, 4 of 11"* — so a list has a shape
instead of going on until the names start repeating.

This one has to earn the right to speak. The game's own menu file disagrees with what
the game actually displays (it lists 17 Audio rows where the PC build shows 11 — the
rest are console-only), so a position counted from that file would confidently tell you
"2 of 17" in a list of eleven. Instead the reader stays silent about position on any
page it has not seen walked end to end. Walk a page from top to bottom once with logging
on and the counts can be built from that.

### Fewer wrong noises

- Opening the pause menu used to announce itself up to **three times** — *"Pause menu"*,
  *"Menu closed"*, *"Pause menu"* — on a single Esc press, because the frontend often
  fails to open on the first attempt and quietly retries. It now waits a quarter second
  to see whether the menu is actually staying open before saying anything, so one press
  is one announcement.
- **Contacts said their internal names out loud.** Scrolling your phonebook produced
  *"CELL_FRANKLIN_N"*, *"CELL_DRE_N"*, *"CELL_JUNK_EN_N"*. Those are the game's internal
  labels; they are now translated to the real names — *Franklin*, *Dre* — and anything
  that cannot be translated says *"row 3"* instead, because a number is more use than an
  identifier.
- **The phonebook was reading the wrong list.** Two rows apart could give you the same
  contact. The reader was following a lookup table that merely looked plausible; it now
  follows the one the phone's own code uses, and it will fall back to plain row numbers
  if it ever sees the same name twice in a row again.

### Inside phone apps

**Settings switch:** `Read the rows inside phone apps such as Settings, Texts and
Email`. **Off by default** — this one reads the phone's internals directly and is still
being verified against real game builds; turn it on if you want to try it.

Rows inside phone apps used to be *"row 0"*, *"row 1"*, *"row 2"*. Every phone app turns
out to write its visible rows into one shared place, so a single change covers Phone
Settings, Checklist, Texts and Email together. Home-screen tiles that are greyed out and
cannot be opened now say so, instead of silently doing nothing when you press Enter.

---

## 7. Status menu — new

87 readable items, grouped into five sections you can arrow through:

1. **Player Status** — health, armour, wanted level, money, sprint stamina, remaining
   underwater time, player state, heading in degrees plus compass point, XYZ position,
   height above ground, speed, current weapon, ammo in clip and reserve, in
   vehicle/water/air, on fire, submersion level, in interior, special ability, aiming,
   and what you are targeting.
2. **Vehicle Status** — 34 items.
3. **Aircraft Status** — instruments.
4. **World and Environment** — weather now and next, game speed, gravity, nearby ped
   and vehicle counts, FPS, frame time, game timer, night vision, thermal vision.
5. **Navigation.**

Any item can be **monitored** — pin it and it re-announces as it changes, so you can
keep an ear on your altitude or your engine health while doing something else.

---

## 8. Civil Unrest — new

Start a riot. Available from the Functions menu.

It escalates through five stages — *tension*, *agitation*, *unrest*, *full riot*,
*citywide riot* — over about ten minutes, and narrates the escalation (*"The crowd is
turning. Arguments breaking out nearby."*, *"Fighting has broken out. Riot in progress."*,
*"Full riot. It is dangerous here."*, *"Citywide riot. The police are losing control."*).

**Settings:**

- `Civil Unrest Scope` — Civilians / Civilians and gangs / Civilians and police /
  Everyone.
- `Civil Unrest melee weapon handouts` — off by default.
- `Civil Unrest firearm handouts` — off by default.
- `Civil Unrest Commentary` — Off / **Brief** (default) / Detailed.
- `Civil Unrest safety watchdog` — **On by default.** If a rioter turns on *you*, it
  says so and ejects them. If it keeps happening it shuts the whole thing down:
  *"Civil unrest shut down: rioters were targeting you."*

Police responding to your own wanted level are pulled out of the riot and it tells you:
*"Police are responding to you. They have left the riot."*

The unrest commentary deliberately skips its slot entirely rather than queueing behind
navigation or collision cues — periodic chatter must never delay a brake warning.

### Escalating into the world

Six more switches, **all off by default**, meant to be enabled one at a time.

- `Civil Unrest fight intensity` — Normal / Brutal / Savage. *Brutal* closes the fighting
  distance so rioters actually brawl instead of standing off, lets them chase, and lets
  them climb obstacles. *Savage* adds flanking, a faster crowd, and a higher rate of fire.
  Both tiers deliberately turn **off** blind firing from cover — "more brutal" here means
  closer and more committed, not more unaimed rounds from a shooter you cannot locate.
- `Civil Unrest riot ambience` — Off / Crowd / Crowd and city. *Crowd* gives you the sound
  of an actual riot using the game's own ped voices: shouting, swearing, threats, with
  frightened bystanders mixed underneath, plus a denser street. *Crowd and city* adds car
  alarms 12–50 m away. None of it uses the screen reader, and the barks always yield to
  spoken lines — never the other way round.
- `Civil Unrest police response` — Off / Hostile crowd / Dispatch / Street stops / Heavy
  response. *Hostile crowd* makes rioters attack ordinary police with no scripting at all.
  *Dispatch* calls real units in, anchored 30–50 m off so sirens arrive nearby instead of
  converging on you. *Street stops* has an officer grab a rioter — and the further the riot
  has gone, the likelier it is they tase or shoot instead of arresting (80/15/5 at *unrest*,
  20/40/40 at *citywide riot*). Every shooting is announced with a bearing, whatever your
  commentary setting. *Heavy response* brings in SWAT and the army.
- `Civil Unrest aggressive traffic` — Off / Aggressive / Reckless. *Aggressive* cars run
  reds and tailgate but still stop for pedestrians. *Reckless* stops them stopping, and so
  it **requires Navigation Assist**: its "car approaching" alert is the only warning you
  would get. Without it the mod refuses, drops to Aggressive and says why. Any car that
  comes within ten metres of you on foot is handed straight back to normal driving.
- `Civil Unrest street fires` — needs riot ambience on *Crowd and city* and the top stage.
  Never within 25 m of you, never more than two at once, and every one announced with a
  bearing and distance regardless of commentary setting.
- `Civil Unrest escalation` — **Timed** (default) or Dynamic. Timed is the fixed ten-minute
  schedule. Dynamic lets casualties, new fights and your own gunfire push it ahead of
  schedule. It never runs backwards — once you have been told it is a full riot, it will
  not claim things calmed down while people are still fighting near you.

Whenever you pick up a wanted level of your own, the crowd stands down from the police and
says so, and any street stop in progress is cancelled — so you are never left standing in
the middle of somebody else's firefight while being chased.

A new status readout, **Riot escalation** (also on the Functions menu as *"Read riot
escalation status"*), gives you the stage, the time until the next one, and what traffic
and the police are currently doing.

The engine's own `SET_RIOT_MODE_ENABLED` was evaluated and **rejected**: it turns every NPC
hostile *including the player*, which breaks the invariant that keeps this feature safe for
someone who cannot see an attacker, and it hands out weapons the mod deliberately never
gives anyone.

---

## 9. Eat, drink, smoke — new

Its own menu with **31 consumables**. Beer, whiskey, and wine in each of the three
protagonists' safehouse styles; Franklin's and Michael's bongs; a joint; a cigar;
Trevor's gas; wheatgrass; Mr Raspberry Jam; nightclub drinks; vending-machine eCola and
Sprunk; and a range of food that doses nothing and says so in the menu.

Rows 0–12 use the real animations, props and audio banks transcribed from the game's
own safehouse activity scripts, including the second prop that opens or lights the
first — the lighter for the bong, the cap off the beer — because that is where the
foley actually lives. A bong with no lighter is silently unlit.

**Intoxication.** Alcohol and cannabis feed separate meters. There are exactly three
drunk stages in the engine, and the mod uses those rather than inventing its own:
one beer is Tipsy, three drinks is the second stage, six the third.

- `Drinking and smoking effects` — master switch, off by default.
- `Intoxication screen effects, visual only` — **on by default**, cosmetic only.
- `Intoxication audio effects` — **on by default.** This is the game's real drunk audio
  treatment; it muffles the engine note, traffic, and sirens, so it gets its own switch.
- `Use the game's own drunkenness, one drink at a time` — stands this mod's whole
  presentation layer down and hands over to the game's native drunk controller.
- `Impaired driving when intoxicated` and `Blackout takes the wheel at maximum
  intoxication` — both off by default, both affect drive assist.

Drive assist refuses to operate when you are too drunk and says so:
*"You are too drunk to drive. Drive assist is off."* Blackouts are announced before
they happen and when you come round.

---

## 10. Waypoints, markers and turn-by-turn

**Kept from the original:** NumPad Decimal announcing your heading.

**New:**

- **Double-tap NumPad Decimal** toggles waypoint tracking, with beeps that speed up as
  you approach and pan toward the target. If no waypoint is set it finds and tracks the
  nearest mission marker instead — destinations, missions, gang attacks, strangers,
  objectives, crew, bosses, and activities.
- **NumPad Plus / Minus** cycle forward and backward through every mission marker on
  the map, automatically placing a waypoint on the selected one and announcing
  "marker X of Y" with type, distance and direction.
- **Turn-by-turn navigation** (`Turn-by-Turn Navigation`, **on by default**) —
  "Turn left in 50 metres", "Bear right", "Turn around", timed against your speed.
  With `Announce upcoming turns anywhere on the map` enabled it works off the shipped
  junction data rather than only on known roads.

**A compass bug is fixed.** Every spoken direction — waypoints, blips, markers — had
east and west mirrored, because the bearing was computed clockwise and then interpreted
against GTA's counter-clockwise heading convention. If you used the old mod and found
its directions untrustworthy, that is why.

---

## 11. Combat and awareness

### Hostile detection — rewritten

There were two hostile detectors, and the good one was unreachable unless you were using
bodyguards. If you had never opened the bodyguard menu, what you actually got was a flat
sweep for people already shooting at you — no check for someone drawing a weapon nearby,
no promotion when a bystander started aiming, no mention of police, and no all-clear when
it was over.

There is now one detector, and it runs for everyone:

- `Hostile Detection` — Off / **People already fighting you** (default) / Add hostile
  groups and police / Add armed people nearby. The default is exactly what the mod did
  before, so nothing changes unless you ask it to.
- **It says where they are.** "Two hostiles. Armed man, ahead left, 30 meters. Police
  officer, behind, 50 meters." Previously every threat announcement was the word "two
  hostiles detected" and nothing else — no direction, no distance, no idea what you were
  facing. The nearest two are named using the same model-name table the scanner uses,
  and the rest are counted.
- The direction words use the same reference as the positioned audio, so what you are
  told and what you hear agree with each other.


New since the original:

- **Enemy detection** — scans 100 m every two seconds, announces the count, and plays
  harsh directional tones toward each hostile. Enemies behind you play centred and
  lower.
- **Aim autolock and target tracking** (`Aim Autolock & Target Tracking`). Locks onto a
  target while aiming, with a 300 ms grace period so a momentary release does not lose
  it. Body parts can be cycled left and right with audio feedback. Homing launchers are
  excluded, since the game already does this for them.
- **Hit, headshot and kill confirmation** sounds.
- **Ammo announcements** on weapon switch, and a low-ammo warning below 25% of a
  magazine.
- **Cover detection** during combat, with directional audio.
- **Target pitch indicator** when aiming.

---

## 12. Environment and proximity

All new:

- **Water detection** — a low 80–100 Hz rumble ahead of water.
- **Drop-off detection** — a descending tone for ledges and long falls, with a spoken
  *"Drop ahead"* / *"Stop. Drop ahead"*.
- **Door and ladder detection** with directional panning.
- **Indoor/outdoor announcements.**
- **Swimming depth** in metres.
- **Slope and terrain feedback** — steep uphill, steep downhill, level ground.
- **Pickup detection** — health, armour, weapons, money.
- **Safe-house proximity**, character-specific.
- **Service proximity** — Ammu-Nations, hospitals, clothing stores, mod shops, barbers,
  tattoo parlours, ATMs, and, from the shipped map data, petrol stations and garages.
- **Interactable detection** — stores, mission givers.
- **Traffic awareness** — warns when a fast vehicle approaches from the side or behind.
- **Vehicle health feedback** — spoken warnings for body, engine, and fuel tank at 75%,
  50%, 25%, and 10%, plus fuel-tank leak and critical warnings.
- **Stamina and sprint warnings.**
- **Wanted level details** — periodic cop count and helicopter presence.

### The nearby scans — NumPad 4, 5, 6 and 8 (rewritten in iteration 58)

These four keys are original. They ask "what vehicles / doors and gates / people /
objects are near me", and read a list nearest-first.

**What was wrong with them.** Names came from `hashes.txt`, a lookup table placed by hand
in December 2022 and never updated since. Three separate faults compounded:

1. The table was consulted with a number that had been widened from 32 bits to 64 bits
   the wrong way, so any model whose id was negative — about half of them — could never
   be found.
2. The table had never contained a single ordinary pedestrian model. Not one. Only story
   and cutscene characters.
3. And critically, "is this in the table" was used to decide whether an entity got
   announced **at all** — not merely whether it could be named.

Together those meant NumPad 6 on a busy pavement produced the words "Nearest Characters:"
and then nothing. A street full of people and an empty one sounded identical, with no
indication anything had gone wrong. That is the worst failure mode this mod has, because
it is indistinguishable from the feature working correctly on a quiet street.

**What happens now.** Nothing is ever dropped for want of a name. Each kind of thing is
named by the best source available, in order:

- **Vehicles** are named by the game itself, so every DLC vehicle works — including ones
  released after any given build of this mod — and the name arrives in whatever language
  you play in. No bundled table is involved at all. Same for weapon names, which had
  their own version of this bug and had never once resolved.
- **People** are described as a person first and a label second: *"older man, south
  central"*, *"woman, on the promenade"*, *"young barman"*, *"Police Officer"*,
  *"Seagull"*. Where the game has a real name it is used and localized; where it does
  not, the person's approximate age and sex come from the model itself, and the
  neighbourhood or wardrobe note the game *does* carry trails behind as context.
- **Objects, doors and gates** come from a bundled table that is now *generated* rather
  than hand-placed, and can be regenerated in one command whenever a DLC lands. The old
  file is folded into it, so nothing it knew was lost — the new table has 65,247 entries
  against the old 53,859.
- **Anything still unknown** is described rather than skipped: "unknown object, about 2
  metres across", or "door or gate" for something you can walk through. It is also
  written to a log file, so it can be given a real name in the next regeneration.

**Whether something is a door** is now asked of the game's door system instead of being
guessed from whether the word "door" appeared in the model's internal name — so a gate
that was added to the game after 2022 is no longer invisible to the door scan.

### Iteration 60: the fix above spent weeks not installed

Iteration 58 rewrote all of this and then never reached the game. The deploy script
copied the mod's data files only when asked to with a flag, so every ordinary rebuild
left the new name table uninstalled. The mod started, found no table, and fell back to
"man" / "woman" / "unknown object" for everything — which is exactly what a working mod
on a quiet street sounds like.

It had said so, in two places nobody was looking: a line in its own diagnostic log
reading `table: fileNotFound, 0 rows`, and a spoken startup warning whose words were
"some navigation features unavailable" — true of nothing that had actually broken.

Three changes so that cannot recur:

- **Data files are now synced by content on every deploy**, not on request, and the
  deploy prints what is actually *installed* rather than what it meant to install. If a
  file is missing it says so twice and tells you not to start a session.
- **A missing name table gets its own sentence**: *"the model name table did not load,
  so people and objects will not be named in the nearby scans."*
- **Names are checked for whether they can be said out loud.** The old table shipped a
  row for every model it had, and the mod preferred any of them over its own description
  — so once installed it would have read out "Ch3 07 Decals50" and "Isldj" in place of
  "unknown object" and "man". Those are worse. Each row now records whether its name is
  defensible aloud; the 87% that are not are described instead, and are listed in a
  generated review file to be given real names over time.

Also fixed: an unnamed object with detailed scans on used to state its size twice, with
two different numbers, because the two measurements used different axes.

**When a DLC lands**, `--check` now asks upstream whether the source data has moved and
names the game build it corresponds to, so a stale table is something you find out
rather than something you assume.

One more fix, unrelated to naming: a result at your own height used to inherit the
previous result's *"2.1 meters above"*. Heights are now correct per result.

### Detailed scans — new, off by default

Setting: **"Say what nearby people and vehicles are doing, not just where they are."**

- **People** — *"5.2 meters north, Marine, walking east, armed with a Carbine Rifle."*
  Covers fighting, brawling, shooting, fleeing, taking aim, on fire, on the ground,
  swimming, diving, falling, climbing, on a ladder, in cover, lying down, crouching,
  sprinting / running / walking with the direction they are travelling, and driving or
  riding in a named vehicle. Firearms are called out; fists are not, or every scan would
  read as a threat.
- **Vehicles** — *"5.2 meters north, Tornado, 15 miles per hour east, with 2 occupants."*
  Plus sirens.
- **Doors and gates** — open, closed, or locked. If the door is not one the game's door
  system owns, its state genuinely cannot be read, and it says **"state unknown"** rather
  than reporting a confident "closed" that might be wrong.
- **Objects** — rough size, and whether the object is attached to something.

It ships off because it makes every line about three times longer.

### Nearby Scan Length — new, defaults to the nearest 10

A second setting, and the one place in iteration 58 where a default changes behaviour on
purpose. Repairing the naming turned these scans from near-silent into full
enumerations, and the detection radius goes up to 1000 metres — uncapped, a scan in a
city could speak for minutes with no way to stop it. It reads the nearest 10 by default
and then says how many it left out (*"...and 23 more."*). Options are 3, 5, 10, 25, or
everything in range; "everything in range" is exactly the old behaviour if you want it.

---

## 13. Speech handling — rewritten

The original called the screen reader directly from wherever a message came from. Since
each call carries its own interrupt flag, the last one in a frame cut off everything
before it — so a brake warning and a zone change arriving on the same tick meant you
heard a fragment, or the wrong one entirely.

Speech now goes through a priority arbiter with three tiers:

- **Critical** — collision and brake warnings, arrivals, target lost. Always interrupts.
- **Informational** — normal announcements.
- **Ambient** — status chatter, heading and zone changes. Only voiced when the reader
  is otherwise idle, so it never steps on something you are still listening to.

If anything Critical fires in a tick it is spoken first, Informational queues behind it,
and Ambient is dropped for that tick.

Tolk is also health-checked and reloaded if it dies mid-session, so losing speech no
longer means losing the session.

---

## 14. Reliability

- **Every subsystem is wrapped.** A crash in one — the scanner, the menu reader, drive
  assist — is caught, reported, and the rest keeps running. In the original, one bad
  frame took the whole mod down.
- **A monotonic mod clock.** Durations are measured on a clock that only advances while
  the script is receiving frames. Previously, a timer armed before a pause menu,
  cutscene, or alt-tab saw the entire wall-clock gap the instant you came back, which
  misfired stuck-escalation, watchdog releases and grind detection. Rate limiters stay
  on wall time on purpose.
- **Corrupt settings files recover.** A bad `gta11ySettings.json` is deleted and rebuilt
  a bounded number of times, then falls back to defaults in memory. The old code could
  recurse until it stack-overflowed.
- **Settings defaults come from one table.** There used to be two hand-maintained lists —
  one for a fresh file and one for missing keys — and they had silently drifted, so
  fresh installs got some settings off that upgrades got on.
- **Missing-asset warnings.** If `hashes.txt` or the earcon `.wav` files are absent, the
  mod says so out loud instead of degrading in silence.
- **Settings changes are announced at the next drive.** Experimental drive-assist
  switches persist, so one flipped mid-session quietly stays flipped for every session
  afterwards — and a change to how the car is driven is the last thing that should be
  invisible. On starting a drive, the mod compares the switches against the ones you
  last drove with and says how many moved, naming them in the diagnostic log. A drive
  that behaves differently now tells you what changed.

### Iteration 61: drive assist stopped being able to get unstuck

Drive assist was measurably worse on 17 August than it had been two days earlier: it
changed its mind about what it was doing 22 times a minute, against half a time a minute
in the good session. The failure was not crashing — it braked correctly for a stopped car
on the freeway — it was that once stopped against that car it spent ninety seconds
alternating between throttle and brake every frame, wheels spinning, and never got clear.

The cause is being narrowed by turning one experimental switch off at a time, starting
with lane targeting. Three of them had been turned on together, two of them part-way
through a session, which left nothing attributable to anything.

Two things changed so this is quicker to see next time:

- **The drive-assist log now records *why* it changed its mind**, not just that it did.
  "I recovered and I am back on the road" and "I looked for somewhere to recover to,
  found nowhere, and gave up" used to be written identically.
- **The mod now notices the Interaction Menu.** It could not before — the menu does not
  pause the game and does not register as the pause menu — so it kept steering, and kept
  telling you to take over, while your stick was driving a menu. This is currently
  detection only, off by default: getting it wrong would take drive assist away in normal
  driving, so it has to prove itself over a session before it is allowed to act.

---

## 15. Shipped data

The mod now ships five generated data files into the `scripts` folder. None of these
existed before.

| File | What it is |
|---|---|
| `gta11y-map.json` | ~225 named roads classified freeway / highway / surface / alley, plus ~190 petrol stations, garages and lots |
| `gta11y-nodes.json.gz` | The vehicle path-node graph, 67,000+ nodes |
| `gta11y-junctions.json.gz` | Signalised junction templates — per-entrance turn rules and stop positions |
| `gta11y-menulabels.json` | Pause menu and phone label tables |
| `gta11y-modelnames.tsv.gz` | 65,247 model names — people, objects, doors and gates. New in iteration 58; replaces `hashes.txt`. Each row records whether its name is worth saying aloud (iteration 60) |

Plus `vehicleaihandlinginfo.meta`, the game's own AI handling table, so drive assist can
look up the right cornering speed for the class of vehicle you are in.

`hashes.txt` is **no longer shipped**. It was placed by hand in December 2022 and never
updated; it is now an input to the generator that produces `gta11y-modelnames.tsv.gz`, so
nothing it knew is lost. See section 12 for why that mattered more than it sounds.

---

## 16. Keys

Everything the original bound still does the same thing. New bindings:

| Key | Action | Status |
|---|---|---|
| NumPad 0 | Location, street, zone, cash | kept |
| Ctrl+NumPad 0 | In-game date and time | new |
| NumPad 1 / 3 | Previous / next item in menu | kept |
| NumPad 2 | Select / toggle | kept |
| Ctrl+NumPad 2 | Enable/disable all accessibility keys | kept |
| NumPad 4 | Scan nearby vehicles | kept |
| NumPad 5 | Scan nearby doors and gates | kept |
| NumPad 6 | Scan nearby pedestrians | kept |
| NumPad 7 / 9 | Previous / next menu | kept |
| NumPad 8 | Scan nearby objects | kept |
| NumPad Decimal | Tap: heading. Double-tap: toggle tracking | extended |
| **NumPad Divide** | Tap: Around Me. Double: What's Ahead. Hold: Where Am I | **new** |
| **NumPad Multiply** | Start / cancel auto-drive, autopilot, auto-walk | **new** |
| **NumPad Plus / Minus** | Cycle mission markers forward / backward | **new** |
| **Left / Right arrows** | Auto-drive speed, in 5 mph steps | **new** |
| **Up / Down arrows** | Autopilot altitude | **new** |
| **Esc** | Open the readable pause menu | **new** |
| **F1** | Mark a failure in the drive-assist log | **new** |

---

## 17. Settings reference

146 settings, up from 21. All 21 originals are still there except `vehicleSpeed`, which
was removed as redundant.

49 default to **On**. The remainder default to **Off**, which is deliberate: anything
that changes how the car behaves ships off and gets switched on one at a time, so that
when something goes wrong it is clear what caused it.

**Default On:** heading, zone and time announcements; altitude indicator; target pitch
indicator; speed announcements; navigation assist; shape casting; turn-by-turn
navigation; pause menu reader; drive assist warning beeps; intoxication visuals and
audio; civil unrest safety watchdog; auto-resume creep; and 22 proven drive-assist
behaviours.

**Notable defaults Off:** steering assist (you opt in to drive assist), haptic feedback,
adaptive cruise, lane centring, civil unrest weapon handouts, intoxication effects, all
debug logging, and every drive-assist change still under evaluation.

---

## 18. Building and installing

The original was a Visual Studio 2019 project using `packages.config` and required the
full IDE.

Now:

- **SDK-style project**, builds with `dotnet build` — no Visual Studio needed.
- Dependencies come from NuGet: CSCore, NAudio 1.10.0, Newtonsoft.Json 13.0.4,
  ScriptHookVDotNet3 3.6.0. Newtonsoft was 12.0.3 and was committed to the repo as
  ~200,000 lines of vendored binaries and XML; those are gone.
- **Auto-deploy on build** to your GTA V `scripts` folder, overridable with
  `-p:Gta5ScriptsDir=<path>` or an environment variable.
- **`tools/deploy.ps1`** for an explicit deploy, with `-IncludeData` for a first install.
- **Tolk is vendored** (`GTA/Tolk.cs`) rather than referenced.

### The packaging fix

`hashes.txt` and all eleven `.wav` earcons were placed by hand in 2022 and were never
part of any build or deploy. Existing installs only had them by inheritance — a fresh
clone silently lost vehicle and object naming and had no target, pickup, cover or hit
audio at all. They are now built and deployed.

Likewise `Tolk.dll` and its screen-reader clients (`nvdaControllerClient64.dll` for NVDA,
`SAAPI64.dll` for Dolphin and Supernova) are now vendored and deployed. These are native
and must sit in the **GTA V root next to `GTA5.exe`**, not in `scripts\` — `DllImport`
resolves from the executable's directory. Putting them in `scripts\` appears to work
only if a stale copy is already in the root. Without `Tolk.dll` the mod has no speech at
all.

---

## 19. Developer tooling

Not user-facing, but it is a large part of what changed.

- **`tools/build-map-data.py`** — builds the road and POI database from community
  datasets, applying a hand-curated road classification.
- **`tools/build-menulabels.py`** — builds the menu label tables from `pausemenu.xml`
  and the game's enums.
- **`tools/align-panes.py`** — pins settings-row identities against a real game build by
  aligning observed menu walks to the XML row order. Game builds insert preferences,
  which shifts every row after them.
- **`tools/triage-log.py`** — drive-assist debug logs run to 80 MB. This streams one and
  emits a digest, per-incident traces, and a trend index, so analysis never touches the
  raw log.
- **`tools/verify-vehicle-tasks.py`** — checks how the mod drives and flies against how
  Rockstar does. It reads the game library the mod is built on for the real names of the
  32 driving-style flags, parses 21 of Rockstar's own shipping scripts for how the
  vehicle, plane and helicopter task functions are actually called, and writes
  `docs/vehicle-task-conventions.md`. `--verify` fails if the mod's flag table has drifted
  from the library, if a flag cannot be set, or if a newly named flag has no spoken
  wording yet. It found six wrong flag names, one unreachable flag, and a landing-gear
  function the mod had never called.
- **`tools/deploy.ps1`** — explicit deploy, with warnings if the game has files locked.
- **Debug logging** for drive assist (F1 marks a failure moment) and for the menu
  reader, both off by default and both switchable from the Settings menu.
- **Failure screenshots** — when a failure fires, the mod saves the frame next to the
  log so a developer can see what the car was actually touching. Two things about these
  are worth knowing if you are sending logs in:
  - They capture **only the game window**, not your whole screen. Before iter-55 they
    captured the entire desktop, so if you have older ones lying around, they contain
    whatever else was on screen at the time.
  - If the mod cannot find the game window, or the window is small, **it now says so out
    loud** when logging starts, and repeats nothing after that. The same applies when the
    session's image limit is reached — you hear it once rather than playing on believing
    the session is still being recorded.

---

## Removed and changed

- **`vehicleSpeed` setting removed** — redundant with `speed`.
- **`navAssistBeeps` replaced** by `Navigation Assist Detail`, which has four levels
  instead of on/off. Your old value migrates automatically: beeps on becomes "All
  Zones", beeps off becomes "Alerts Only", and the mod tells you it happened. If you
  were running with beeps off you will land on **Alerts Only**, not silence.
- **Auto-drive speed steps changed** from metres per second to 5 mph.
- **Vendored NuGet binaries removed** from the repository (~213,000 lines deleted).
- **Direct screen-reader calls removed** in favour of the priority arbiter.

---

## Status and caveats

This fork is under active development and is not a finished product.

- Drive assist has been through 57 numbered iterations of tuning against recorded road
  tests. It is much better than it was, but it is an assist, not a chauffeur.
- **New in iteration 58: the nearby scans (NumPad 4, 5, 6, 8) name things properly for
  the first time.** Read section 12 — the old behaviour silently dropped anything it
  could not name, and it could not name any ordinary pedestrian at all. This one is ON,
  because it is a repair rather than a new behaviour, and the thing it repairs is a
  feature that was failing in a way you could not hear. Two new settings ride with it:
  detailed scans (off) and scan length (nearest 10).
- **New in iteration 56, off by default: "when the car is in the air, do not treat it as
  rolling backwards."** When the car brakes, the mod checks whether you are rolling
  backwards, because the brake button is also the reverse button — so on a car rolling
  back it presses the accelerator instead. That check compares which way the car is
  pointing against which way it is moving, and while the car is *airborne* those two
  come apart. A road test found the mod holding the accelerator at full for over two
  seconds, mid-air at 46 mph, while its own brake demand was at maximum. Turning this
  on makes the mod ignore the reverse check whenever no wheel is on the ground. It
  ships off so it can be tested on its own.
- **Failure screenshots now retry.** They used to be tested once when the mod started
  and disabled for the rest of the session if that one test failed — and mod startup,
  when the game is often still on a loading or fade screen, is the worst possible moment
  to test. The mod also used to tell you the cause was exclusive fullscreen, which it
  had not measured and which was wrong. It now retries every 30 seconds, says so at a
  priority you will actually hear, and tells you when capture starts working.
- Several features are **implemented but not yet verified in-game**, including the
  Civil Unrest weapon-handout paths and parts of the intoxication work. They ship off
  by default for that reason.
- Any setting whose name ends in a plain-language description of a drive-assist
  behaviour can be switched off in-game if it makes things worse. That is what they are
  for.
- If something regresses, turning off the most recently enabled drive-assist switch is
  usually the fastest way back to a working state.

---

*Original mod © liamerven, 2022. This fork continues the work with his blessing to
"do something cool with it".*
