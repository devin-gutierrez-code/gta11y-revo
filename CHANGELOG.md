# Changelog

Everything that changed between the original **gta11y** by liamerven
(<https://github.com/liamerven/gta11y>, last updated February 2022) and this fork,
**GTA11Y - Revo**.

This is written for the person playing the game, not for the person reading the code.
Where a feature has a switch, the switch's exact name in the Settings menu is given so
you can find it by arrowing through the list.

---

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
| Settings | 21 | 98 |
| Keys used | 11 | 22 |
| Shipped data files | 0 | 4 |

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
- **Stereo panning** places each obstacle left or right. Things behind you play centred
  and an octave down.
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

### On-demand queries (NumPad Divide) — new

Rather than listening to ambient beeps all the time, you can ask:

- **Tap** — *Around Me*. Names the nearest obstacle in each zone in plain words
  ("pedestrian", "wall", "low obstacle", or the vehicle's actual name).
- **Double-tap** — *What's Ahead*. Fires an eight-direction fan of casts and reports
  what is in each. It is spread across two ticks so it does not stutter the frame rate.
- **Hold** — *Where Am I*. Street, zone, and orientation.

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
- Lane centring against a road polyline (`Lane Centering`, experimental, off by
  default).
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

### The actuation fix

Worth calling out on its own, because it explains why drive assist behaved so poorly
for so long. The mod was disabling the same game controls it was then trying to write
to — so its own brake and steering commands were being thrown away before they reached
the car. Roughly two dozen rounds of tuning were built on top of a control channel that
was never connected. This is fixed (`Real braking and steering`, on by default), and
most of the drive-assist switches that came after it exist to manage the consequences of
the commands suddenly becoming real.

### The revertible-switch system

Drive assist behaviour is exposed as 55 individually named switches in the Settings
menu, each written in plain language describing what you will *feel or hear*, not what
code it runs — for example *"Stop the assist braking against your own throttle"*,
*"Say why the pedal is being held"*, *"Brake when off the road surface, not just far
from it"*.

They are there so a bad change can be switched off in-game without waiting for a new
build. Switches that have been proven in road testing default **On**; anything still
under evaluation ships **Off** and is enabled one at a time, so a session's result is
attributable to exactly one change. 39 of the 136 settings currently default on.

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
game's own driving-style bits, named in plain English where their meaning is known
("Stop at traffic lights", "Avoid highways when possible", "Change lanes around
obstructions") and honestly labelled "Unknown (bit N)" where it is not.

The final approach is a hybrid: cruise on the road graph, then navmesh, then park.

---

## 4. Request Plane Flight — new

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

---

## 15. Shipped data

The mod now ships four generated data files into the `scripts` folder. None of these
existed before.

| File | What it is |
|---|---|
| `gta11y-map.json` | ~225 named roads classified freeway / highway / surface / alley, plus ~190 petrol stations, garages and lots |
| `gta11y-nodes.json.gz` | The vehicle path-node graph, 67,000+ nodes |
| `gta11y-junctions.json.gz` | Signalised junction templates — per-entrance turn rules and stop positions |
| `gta11y-menulabels.json` | Pause menu and phone label tables |

Plus `vehicleaihandlinginfo.meta`, the game's own AI handling table, so drive assist can
look up the right cornering speed for the class of vehicle you are in.

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

136 settings, up from 21. All 21 originals are still there except `vehicleSpeed`, which
was removed as redundant. 97 are new.

36 default to **On**. The remainder default to **Off**, which is deliberate: anything
that changes how the car behaves ships off and gets switched on one at a time, so that
when something goes wrong it is clear what caused it.

**Default On:** heading, zone and time announcements; altitude indicator; target pitch
indicator; speed announcements; navigation assist; shape casting; turn-by-turn
navigation; pause menu reader; intoxication visuals and audio; civil unrest safety
watchdog; auto-resume creep; and 22 proven drive-assist behaviours.

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
- **`tools/deploy.ps1`** — explicit deploy, with warnings if the game has files locked.
- **Debug logging** for drive assist (F1 marks a failure moment) and for the menu
  reader, both off by default and both switchable from the Settings menu.

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

- Drive assist has been through 47 numbered iterations of tuning against recorded road
  tests. It is much better than it was, but it is an assist, not a chauffeur.
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
