# gta11y - revo
A mod for Grand Theft Auto V to add accessibility for blind gamers

## What is it?

GTA11Y was an attempt to create functions for the popular Grand Theft Auto V game to
promote accessibility for blind gamers. It worked. Then it stopped, because the original
author was unable and unwilling to keep going, and he put the source up for anyone who
cared to tinker with it.

This is the tinkering. It's a fork, it's called Revo, and it is a lot bigger than what it
started from. The original was about 1,500 lines that told you where you were and what
was near you. This is about 37,000 lines that will also drive the car for you, fly you
across the map, read the pause menu out loud, and start a riot if you ask nicely.

Everything the original did still works. If you used the old mod, the keys you know are
the keys you know. There's just a great deal more of it now.

Full details of what changed are in [CHANGELOG.md](CHANGELOG.md).

Same spirit as the original, though: I can't guarantee this will work on your machine, in
your game build, with your screen reader. It's a mod. Mods break.

## What it does

### Getting around

The heart of the thing is the obstacle scanner. It watches four zones — left, centre,
right, and behind you when you're reversing — and beeps to tell you what's there. It
works on foot and in a car.

How to read the beeps:

- **How fast it ticks** is how close the thing is. Slow tick, far away. Fast tick, you're
  about to walk into it.
- **What note it plays** is what kind of thing it is. Pedestrians are a soft 440. Moving
  cars are a nasty sawtooth at 620. Parked cars 480. Walls 330. Low things you'd trip
  over sit way up at 1320 so you can't mistake them for anything else.
- **Which ear it's in** is which side it's on. Things behind you play in the middle and
  an octave down.
- **If the note slides up** it's getting closer. Sliding down means it's moving away.

It sweeps a capsule rather than firing a single hair-thin ray, so it actually catches
poles, kerbs and half-open doors instead of slipping straight past them.

If you'd rather ask than listen, **NumPad Divide** does that. Tap it for "around me" and
it names the nearest thing in each direction in plain words. Double-tap for "what's
ahead" and it fans out in eight directions. Hold it for "where am I".

You can turn the ambient beeping down without losing the warnings — see
`Navigation Assist Detail` in settings.

### Driving

The original couldn't help you drive at all. This can.

Drive assist is off until you turn it on. Find `Steering Assist Mode` in the settings and
cycle it:

- **Off** — you're on your own.
- **Assistive** — it nudges. It'll straighten you up and stand on the brake when
  something's about to happen, but you're driving.
- **Full** — it drives. Adds cruise control that follows the car in front.

It brakes for cars, people and walls on a time-to-collision basis, so it brakes earlier
when you're going faster. It slows for bends before you get to them. It knows when
you've left the road surface. It knows what size vehicle you're in, so it doesn't try to
steer a truck like a hatchback. It'll tell you about junctions and shout if you're going
the wrong way up a road.

When you inevitably get wedged against something, it escalates: waits, reverses, pivots
on the spot, and finally teleports you to the nearest road. If it genuinely can't free
you it says so and hands the car back — and it tells you what's in front of you first,
rather than dropping you into a wall in silence.

Turn on `Controller Haptic Feedback` if you want the pad involved. The rumble native has
no left/right motor, so it's done with patterns instead: one short pulse is left, a
double pulse is right, and a long low buzz is the edge of the road.

**Pull Over** is in the Auto-Drive menu. It stops you at the kerb on your own side of the
road, without crossing oncoming traffic to get there. If the mod is driving it parks you
there; if you're driving it tells you which side the kerb is on and how far, and eases the
speed off — touch the throttle and it lets go immediately.

There are 55 individually named drive-assist switches in the settings. They
are all written in plain English describing what you'll hear or feel — "stop the assist
braking against your own throttle", "say why the pedal is being held" — and they exist so
that when something makes your driving worse you can switch it off in the game instead of
waiting for me to build a new DLL. The ones that have been proven on the road are on by
default. Anything still being tested is off.

Worth mentioning: for a long time the mod was disabling the same game controls it was then
trying to write to, so its own brake and steering commands were being thrown in the bin
before they ever reached the car. That's fixed. If you tried an earlier build and thought
drive assist did nothing, that's why.

### Auto-drive, autopilot, auto-walk

**NumPad Multiply** starts and cancels it. What it does depends on what you're in:

- **A car** — drives you to your waypoint, or wanders if you haven't set one.
- **A plane or helicopter** — full autopilot. Climbs, flies to the waypoint, finds a
  landing zone, lands. Up and Down arrows set the altitude.
- **On foot** — walks you there.

Left and Right arrows set the speed in 5 mph steps. There's a whole Auto-Drive menu with
32 driving-style flags plus Land, Park at Nearest Safe Spot, Hitch to Nearest Trailer,
Refresh Driving Task and Pull Over to Kerb.

### Flying places

There's a Request Plane Flight menu. Pick a fixed route between two runways, or tell it
to fly to your waypoint and it'll take the nearest runway to you, land at the nearest
runway to your destination, and then finish the journey by road in a car it spawns for
you. Door to door.

You can fly it yourself or set the pilot to AI and ride along. There's a helicopter taxi
option too.

### The Butler and the bodyguards

You get up to seven guards. Model, weapon, combat style, formation, spacing, armour, god
mode, auto-respawn and patrol are all configurable, globally or per guard. You can send
them to a waypoint, tell them to hold, follow, attack your target or stand down.

The Butler is the first guard and he's the one that matters if you can't see:

- **Vehicle delivery.** Turn on `Butler Vehicle Delivery` and when you spawn a car it
  doesn't just appear next to you — he drives it to you and hands it over.
- **Ground and helicopter extraction.** He comes and gets you. Standoff distance is
  configurable for both.
- **Boats**, if you're in the water.
- **A beacon**, so you can find him.
- **POI narration**, so he tells you what you're driving past.

There's also proactive threat detection and an armed-ped alert, which will tell you
someone nearby has a gun before they use it.

### Reading the game's own menus

This is probably the single most useful thing in here.

**Esc** opens a readable pause menu and reads it to you. Arrows or WASD to move, Enter to
accept, Backspace or Esc to go back. The iFruit phone reads too — apps, contacts, rows.

Small explanation for why it's Esc and not P: GTA V's real pause menu suspends every
Script Hook script while it's open, which means a reader living inside one can't read it.
So Esc opens the same menu without pausing. **P** still opens the normal paused one if you
want it for some reason.

The labels come out of the game's own data — `pausemenu.xml`, the menu enums, and the
game's localised strings — so they're the real names, not my guesses. Where it *can't*
work out what a row is, it says "unknown setting" instead of guessing at it. A wrong label
is worse than an honest one when you can't look at the screen to check.

It's on by default. `Pause Menu and Phone Reader` in settings if you want it off.

### The status menu

88 things you can read, in five groups: player, vehicle, aircraft, world, and navigation.
Health, armour, wanted level, money, stamina, underwater time, heading in both degrees and
compass points, position, speed, weapon, ammo in clip and reserve, whether you're in
water, in the air, on fire, indoors, aiming, and what you're targeting. Then the whole
vehicle instrument set, the aircraft instrument set, weather now and next, FPS, ped and
vehicle counts, and so on.

Anything in there can be **monitored** — pin it and it re-announces itself as it changes,
so you can keep half an ear on your altitude or your engine health while you're busy.

### Waypoints and markers

**Double-tap NumPad Decimal** to track your waypoint. It beeps faster as you close on it
and pans toward it. If you haven't set a waypoint it finds the nearest mission marker and
tracks that instead — missions, gang attacks, strangers, objectives, crew, activities.

**NumPad Plus and Minus** cycle through every marker on the map, dropping a waypoint on
whichever one you land on and telling you "marker 3 of 11", what type it is, how far and
which way.

Turn-by-turn navigation is on by default and will say things like "turn left in 50
metres" while you drive, timed against how fast you're going.

One thing worth knowing if you used the old mod: every spoken direction had east and west
the wrong way round. The bearing was being worked out clockwise and then read against
GTA's counter-clockwise headings. It's fixed. If you learned not to trust the directions,
you can trust them now.

### Shooting things

- Enemy detection sweeps 100 metres every couple of seconds, tells you how many there are,
  and plays a harsh tone toward each one.
- Aim autolock holds onto a target while you're aiming, with a grace period so letting go
  for a moment doesn't lose it. You can cycle body parts left and right.
- Hit, headshot and kill each have their own sound.
- Ammo is announced when you switch weapons, and you get a warning under 25% of a mag.
- Cover detection points you at cover while you're in a fight.

### Everything else it tells you about

Water ahead (a low rumble), drops and ledges (a descending tone plus a spoken warning),
doors, ladders, going indoors and out again, how deep you are when swimming, whether
you're on a slope, pickups, your safehouse, Ammu-Nations, hospitals, clothes shops, mod
shops, barbers, tattoo parlours, ATMs, petrol stations, garages, cars coming at you fast
from the side, your vehicle's body/engine/fuel-tank health as it drops, your stamina, and
how many police are currently interested in you.

### Civil unrest

Because someone was going to ask.

It's in the Functions menu. It escalates through tension, agitation, unrest, full riot and
citywide riot over about ten minutes, and narrates the escalation as it goes. You can scope
it to civilians, civilians and gangs, civilians and police, or everyone. Melee and firearm
handouts are separate switches and both are off by default.

There's a safety watchdog on by default. If a rioter turns on you it says so and removes
them, and if it keeps happening it shuts the whole thing down. If the police come for your
wanted level rather than the riot, it tells you that too.

**Making it worse.** Five more switches control how far the riot spreads into the world.
All of them ship off, and the intention is that you turn on one at a time.

- **Fight intensity** — Normal, Brutal or Savage. Brutal makes rioters close the distance
  and actually brawl instead of standing off at range; Savage adds flanking and a faster,
  more committed crowd. Counter-intuitively it also turns *off* blind firing from cover,
  because unaimed rounds from a shooter you can't locate are the worst thing this feature
  can produce.
- **Riot ambience** — Crowd, or Crowd and city. Crowd gives you the actual sound of a riot:
  peds shouting, swearing and threatening each other with terrified bystanders underneath,
  plus a denser street. Crowd and city adds car alarms going off nearby. None of it uses
  the screen reader — it's the game's own voices, and it always gets out of the way when
  something is being spoken to you.
- **Police response** — Hostile crowd, Dispatch, Street stops, or Heavy response. Hostile
  crowd makes rioters go after ordinary police, no scripting involved. Dispatch calls real
  police units in, anchored a short way off so you hear sirens arrive nearby rather than
  converge on you. Street stops has police grab rioters off the street — and the further
  the riot has escalated, the more likely it is that the officer tases or shoots them
  instead of arresting them. Heavy response brings in SWAT and the army. Any time you pick
  up a wanted level of your own, the crowd backs off the police and says so, so you're
  never standing in the middle of somebody else's firefight while being chased.
- **Aggressive traffic** — Aggressive or Reckless. Aggressive means cars run reds, tailgate
  and drive far too fast, but still stop for people on foot. Reckless means they stop
  stopping. Reckless *requires* Navigation Assist to be on, because its "car approaching"
  warning is the only notice you'd get; if it's off, the mod refuses, drops back to
  Aggressive, and tells you why. Cars are also handed straight back to normal driving if
  one gets within ten metres of you on foot.
- **Street fires** — needs Riot ambience on Crowd and city, and only at the top stage. Sets
  actual fires in the street. They're never lit within 25 metres of you, never more than two
  at once, and every one is announced with a bearing and distance whether you have riot
  commentary on or not.

There's also an **Escalation** switch, Timed or Dynamic. Timed is the ten-minute schedule.
Dynamic lets the riot run ahead of schedule when it gets genuinely violent — casualties,
new fights breaking out, you joining in. It never runs backwards: once you've been told
it's a full riot, it will not tell you things calmed down while people are still fighting.

"Read riot escalation status" in the Functions menu tells you the stage, how long until the
next one, and what traffic and the police are currently doing. It's status item 88 too, so
you can pin it to the rotating readout.

### Eat, drink, smoke

31 things to consume, in their own menu. Beer, whiskey and wine in each protagonist's
safehouse style, bongs, a joint, a cigar, Trevor's gas, wheatgrass, Mr Raspberry Jam,
nightclub drinks, vending machine cans, and a pile of food that doesn't do anything and
says so in the menu.

The good ones use the game's real animations, props and audio banks, including the second
prop that opens or lights the first — the lighter for the bong, the cap off the beer —
because that's where the sound actually lives.

Alcohol and cannabis run separate meters. The engine has exactly three drunk stages and
the mod uses those rather than making up its own: one beer is tipsy, three drinks is the
next one, six is the last. It's all off by default. Drive assist will refuse to work if
you're too drunk, and it will say so.

## keys

Everything the original bound does what it always did.

| Key | What it does |
|---|---|
| NumPad 7 / 9 | Previous / next menu |
| NumPad 1 / 3 | Previous / next item |
| NumPad 2 | Select or toggle |
| Ctrl+NumPad 2 | Turn all the accessibility keys off and on |
| NumPad 0 | Where you are, what street, what zone, how much money |
| Ctrl+NumPad 0 | Date and time |
| NumPad 4 | Nearby vehicles |
| NumPad 5 | Nearby doors and gates |
| NumPad 6 | Nearby people |
| NumPad 8 | Nearby objects |
| NumPad Decimal | Tap for your heading, double-tap to toggle tracking |
| NumPad Divide | Tap: around me. Double: what's ahead. Hold: where am I |
| NumPad Multiply | Start or cancel auto-drive, autopilot, auto-walk |
| NumPad Plus / Minus | Cycle mission markers forward and back |
| Left / Right arrows | Auto-drive speed, 5 mph a step |
| Up / Down arrows | Autopilot altitude |
| Esc | Open the readable pause menu |
| F1 | Mark a failure in the drive-assist log (for bug reports) |

## menus

NumPad 7 and 9 move between these. NumPad 1 and 3 move within one.

1. **Teleport to location** — 28 places.
2. **Spawn Vehicle** — every vehicle in the game. The Butler will deliver it if you've
   turned that on.
3. **Functions** — blow up nearby cars, make everyone fight, wanted level up and down,
   drop a waypoint where you're standing, and the civil unrest controls.
4. **Auto-Drive** — 32 driving-style flags plus Land, Park, Hitch Trailer, Refresh and
   Pull Over.
5. **Settings** — all 136 of them.
6. **Bodyguard** — 36 items covering guards and the Butler.
7. **Status** — 88 readouts, any of which can be monitored.
8. **Request Plane Flight** — fixed routes, fly to waypoint, helicopter taxi.
9. **Eat, drink, smoke** — 31 consumables.

## settings

There are 136. All but one of the original 21 are still in there. The full list with
defaults is in [CHANGELOG.md](CHANGELOG.md); the short version:

- **On out of the box:** the obstacle scanner, shape casting, turn-by-turn navigation, the
  pause menu reader, heading/zone/time announcements, speed announcements, the altitude
  and target-pitch indicators, the civil unrest safety watchdog, and the drive-assist
  behaviours that have been proven on the road.
- **Off out of the box:** drive assist itself, haptics, cruise control, lane centring,
  riot weapon handouts, every riot escalation switch (fight intensity, riot ambience,
  police response, aggressive traffic, street fires and dynamic escalation), drinking and
  smoking, all debug logging, and any drive-assist change still being evaluated.

Anything that changes how the car behaves ships off on purpose, and gets turned on one at
a time. That way when something goes wrong it's obvious what caused it. If a session goes
badly, switching off the last drive-assist thing you enabled is usually the quickest way
back to something that works.

Settings live in
`Documents\Rockstar Games\GTA V\ModSettings\gta11ySettings.json`. If that file gets
corrupted the mod deletes it and rebuilds it rather than falling over.

## dependencies

You will need these things to hopefully get this thing to run.

- [.NET SDK](https://dotnet.microsoft.com/download) — any recent version. The project
  targets .NET Framework 4.8. You do **not** need Visual Studio any more.
- [Script Hook V](https://www.dev-c.com/gtav/scripthookv/)
- [Script Hook V dotnet](https://github.com/crosire/scripthookvdotnet) — **v3.6 or later.**
  Every native call in here is checked against 3.6; older versions will not work.
- [NAudio](https://github.com/naudio/NAudio) — comes from NuGet, you don't fetch it
  yourself
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) — same
- [CSCore](https://github.com/filoe/cscore) — same
- [tolk](https://github.com/dkager/tolk) — the screen reader bridge. This one is native
  and it's bundled, see below.

## building it

```bash
dotnet build GTA\GrandTheftAccessibilityRevo.csproj
```

Output lands in `GTA\bin\<Debug|Release>\net48\`, and a post-build step copies it straight
into your GTA V `scripts` folder. It defaults to
`D:\SteamLibrary\steamapps\common\Grand Theft Auto V\scripts`, which is where mine lives —
override it with `-p:Gta5ScriptsDir=<your path>` or a `Gta5ScriptsDir` environment
variable.

For an explicit deploy:

```bash
pwsh tools\deploy.ps1 -ScriptsDir "D:\Games\GTAV\scripts" -IncludeData
```

Use `-IncludeData` on a first install. It copies the map data, the menu label tables, the
handling meta, `hashes.txt` and all the sound files, and it puts the native DLLs where
they actually need to go.

## installing it

1. Install Script Hook V and Script Hook V .NET 3.6+ into your GTA V folder.
2. Build it, or take a built copy.
3. `GrandTheftAccessibilityRevo.dll`, `CSCore.dll`, `NAudio.dll` and
   `Newtonsoft.Json.dll` go in `scripts\`.
4. `Tolk.dll`, `nvdaControllerClient64.dll` and `SAAPI64.dll` go in the **GTA V root**,
   next to `GTA5.exe`. Not in `scripts\`. These are native and Windows looks for them next
   to the executable. If you put them in `scripts\` it may look like it works, but only
   because there's an old copy sitting in the root already. Without `Tolk.dll` you get no
   speech whatsoever.
5. The data files (`gta11y-map.json`, `gta11y-nodes.json.gz`,
   `gta11y-junctions.json.gz`, `gta11y-menulabels.json`), `vehicleaihandlinginfo.meta`,
   `hashes.txt` and the `.wav` files go in `scripts\`.
6. Start the game.

The build and `deploy.ps1` both do steps 3 to 5 for you. Step 4 in particular is easy to
get wrong by hand.

If you're missing `hashes.txt` or the sounds, the mod will tell you out loud at startup
rather than just quietly not naming things at you.

### sounds it wants in the scripts folder

`pickup.wav`, `cover.wav`, `interact.wav`, `hit.wav`, `headshot.wav`, `kill.wav`,
`door.wav`, `ladder.wav`, `tped.wav`, `tvehicle.wav`, `tprop.wav`, and `hashes.txt`.
Mono is better for the ones that get panned. These all ship with the build now — they
didn't used to, which is why a clean install used to be strangely quiet.

## if something breaks

Every subsystem is wrapped in its own error handling, so one thing falling over doesn't
take the whole mod down with it. That said:

- **No speech at all** — `Tolk.dll` isn't in the GTA V root. See step 4 above.
- **Nothing is named** — `hashes.txt` isn't in `scripts\`.
- **Drive assist is making things worse** — turn off the last drive-assist setting you
  switched on. They're all individually revertible in-game for exactly this reason.
- **It doesn't load at all** — check your Script Hook V .NET version. It needs 3.6+.
- **You want to report a driving problem** — turn on `Drive Assist Debug Logging` in
  settings, press **F1** at the moment it goes wrong, and the log will have a marker in it.

## Legal stuff

I'm providing this project as is, same as it was given to me. I'm still unsure what
licenses if any need to be invoked. I can't guarantee it'll even run, let alone work. Tolk
is LGPLv3 and belongs to Davy Kager. The map data is redrawn from community datasets into
this project's own schema. Everything else is a mod for a game I don't own, so take that
for what it's worth.

The original author asked to be left alone about this and that request stands — please
don't go bothering liamerven about anything in this fork. He wrote the thing that made it
possible and then handed it over. This is on me.

This is meant to make GTA V playable if you can't see it. That's the whole point. If you
can do something cool with it, be my guest.
