# SHVDN 3.7.0-nightly migration backlog

The mod retargeted from ScriptHookVDotNet **3.6.0** (NuGet) to **3.7.0-nightly** (vendored)
on 2026-08-12. The retarget itself is complete and building: **0 errors**.

What remains is a deprecation backlog. Every deprecated API still works — SHVDN keeps them
as `[Obsolete]` *warning* shims, not errors — so nothing here is urgent or blocking. Work
through it opportunistically, ideally while already editing the surrounding code.

## How the dependency works now

- Pinned tag lives in **one** place: `<ShvdnNightlyTag>` in
  `GTA/GrandTheftAccessibilityRevo.csproj`. Currently `v3.7.0-nightly.189`
  (assembly `3.7.0.189`).
- `powershell -File tools\fetch-shvdn.ps1` reads that property and vendors the assembly,
  its XML docs, and the matching runtime into `external\shvdn\` (gitignored).
- A fresh clone that builds before fetching gets a one-line MSBuild error telling it to run
  the fetch, instead of thousands of "type or namespace not found".
- `<Private>false</Private>` keeps `ScriptHookVDotNet3.dll` out of `bin\` — SHVDN ships its
  own and deploying ours over it would be actively harmful.

**Bumping the nightly:** edit `<ShvdnNightlyTag>`, re-run `tools\fetch-shvdn.ps1`, rebuild,
and re-check the warning counts below for anything newly deprecated.

## Player-facing consequence

A script built against 3.7.0 **cannot load on a 3.6.0 install**. It fails with
`Unable to resolve API version 3.7.0`. From the player's side that is a silent non-load —
no speech, no error they can hear. The required nightly must therefore be stated in
`README.md`, in the generated `INSTALL.md`, and in whatever `dist/` ships;
`tools\fetch-shvdn.ps1` vendors `external\shvdn\runtime\` precisely so the packaging step
can ship the exact matching runtime.

## Warning census (Release build, 2026-08-12)

239 warnings, all non-fatal. MSBuild reports each twice, so the raw grep counts below are
2× the true occurrence count.

| Deprecated API | Raw | Replacement | Notes |
|---|---:|---|---|
| `World.GetDistance(Vector3, Vector3)` | 156 | `Vector3.Distance` | Pure mechanical. Biggest single win. |
| `Ped.AlwaysKeepTask` | 88 | `Ped.KeepTaskWhenMarkedAsNoLongerNeeded` | Rename only — the new name states that it applies solely when the ped is marked no-longer-needed. Heavily used by butler/bodyguard. |
| `Player.WantedLevel` (get/set) | 44 | `Player.Wanted.WantedLevel` | Part of the new `GTA.Wanted` class. |
| `Game.GenerateHash(string)` | 26 | `StringHash.AtStringHash` / `AtHashValue.FromString` | Also replaces the hand-rolled joaat in `tools/build-menulabels.py:177`. |
| `World.CreateVehicle(Model, Vector3, float)` | 22 | `Vehicle.Create` | |
| `World.GetGroundHeight(Vector3)` / `(Vector2)` | 36 | `GetGroundHeight(Vector3, out float, GetGroundHeightMode)` | **Not a pure rename** — the new form separates "no ground found" from "ground at 0", which matters for the water/terrain paths. Treat as a real change, not a sed. |
| `TaskInvoker.FightAgainst(Ped)` | 18 | `TaskInvoker.Combat` | Rename. Sits in the unrest/riot code — see the iter-51 finding that R\* tasks combat explicitly. |
| `Game.IsLoading` | 16 | *(delete)* | Obsolete outright: since SHV v1.0.3351.0 scripts cannot start before the loading screen finishes, so the check is always false. |
| `World.CreatePed(Model, Vector3, float)` | 14 | `Ped.Create` | |
| `Ped.CanBeKnockedOffBike` | 8 | `Ped.KnockOffVehicleType` | bool → enum; check the intended mode rather than mapping true/false blindly. |
| `World.CurrentTimeOfDay` | 6 | `GTA.Chrono.GameClock.Today` | Ties into the new `GTA.Chrono` namespace. |
| `Player.IgnoredByPolice` (set) | 4 | `Player.Wanted.SetPoliceIgnorePlayer` | |
| `World.MillisecondsPerGameMinute` | 2 | `GameClock.MillisecondsPerGameMinute` | |
| `World.CreateProp(Model, Vector3, bool, bool)` | 2 | `Prop.Create` | |
| `VehicleWheel.IsBursted` | 2 | `VehicleWheel.IsBurst` | |
| `Ped.IsGettingIntoVehicle` | 2 | `Ped.IsEnteringVehicle` | |

Non-obsolete warnings, unrelated to the retarget and pre-existing: 30 × `CS0414` (assigned
but never used) and 2 × `CS0169` (never used). Worth a separate dead-field sweep.

## Ordering advice

Do the two **non-mechanical** entries deliberately and on their own:

1. `World.GetGroundHeight` — the new signature changes what a failure looks like.
2. `Ped.CanBeKnockedOffBike` — bool collapses several enum cases.

Everything else is safe to batch. `World.GetDistance` → `Vector3.Distance` alone clears 65%
of the backlog and cannot change behaviour.

## What the retarget opens up

See §G3 of the plan at
`C:\Users\Devin\.claude\plans\time-for-some-general-glimmering-yao.md` for the full survey.
The headline is `PathFind` / `PathNode` / `VehiclePathNodePropertyFlags` — runtime path-node
enumeration read through `SHVDN.NativeMemory`, which bypasses the `OutputArgument`
garbage-read guarded against at `GTA/GTA11Y.cs:16193` and is a candidate replacement for the
bundled 1 MB `gta11y-nodes.json.gz`. Adopting it is its own iteration with its own gate and
field test — not part of the retarget.
