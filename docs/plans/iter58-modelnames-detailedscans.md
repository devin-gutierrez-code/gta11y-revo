# iter-58 — model naming without a stale table, and detailed entity scans

**Banner:** `iter58-modelnames-detailedscans` (`GTA/GTA11Y.cs`, the `EVENT iter-version:`
line). Previous: `iter57-authority-recovery-chain`.

**Prompt:** bypass `hashes.txt`, which is several DLCs out of date, by pulling model
information from the game at runtime where possible; and add richer, switchable detail to
the nearest-ped and nearest-vehicle scans.

---

## What the investigation actually found

`External Resources/hashes.txt` — 53,858 rows, dated 2022-12-21, hand-placed in one commit
and never regenerated — was the stated problem. It was not the main one. Three faults
compounded, and the compound failure was silent.

**1. Half the table was unreachable.** Every lookup keyed on
`entity.Model.NativeValue.ToString()`. `Model.NativeValue` is `UInt64` (verified by
reflection against the vendored `ScriptHookVDotNet3.dll` 3.7.0.188; `Model.Hash` is
`Int32`). A negative model hash sign-extends: `adder`, `-1216765807`, arrives as
`18446744072492785809` and can never equal the `-1216765807` row. The file splits
26,825 negative / 27,034 positive, so **49.8% of it could not be read**. One call site
already did it right — `unchecked((int)prop.Model.NativeValue)` in the interaction-suffix
path — which is why the vending-machine example in the iter-52 comments worked and made
the rest look healthy.

**2. There were no ambient pedestrians in it, ever.** Verified by prefix census: zero rows
matching `a_m_*`, `a_f_*`, `s_m_*`, `s_f_*`, `g_m_*`, `g_f_*`. What it had was `ig_` (104),
`csb_` (50), `u_m_y_` (25), `player_` (3) — story and cutscene peds. This was not DLC rot;
an ordinary passer-by was never nameable, in 2022 or now.

**3. A missing name deleted the entity.** `hashes.ContainsKey(...)` was a term in the
`valid` predicate of the ped, door and object scans. Unknown hash ⇒ no `Result` ⇒
`listToString` returns the bare header. So **NumPad 6 on a crowded pavement said
"Nearest Characters:" and stopped**, which is exactly what it says on an empty street.

Two smaller instances of the same class, found in passing:

- The weapon readout looked up `wep.Hash.ToString()` — the *enum* name, `"Pistol"` —
  against a table of numeric keys that had no weapon rows at all. Dead twice over; the
  symptom was the enum name leaking into speech.
- `hashes[...] != "player_one"` was used three times to exclude the protagonists.
  `player_one` and `player_two` are negative, so only Michael was ever excluded.

## Can the names come from memory instead?

Partly, and the parts that can are the valuable ones. joaat is one-way, and GTA V retail
keeps only the hash in `fwArchetype` — the model-name strings are not in the executable.
They survive in the RPF table-of-contents name heap, but reaching that from a script means
pattern-scanning undocumented structures that move on every game patch, which for a mod
whose rule is "a wrong label is worse than no label" is the wrong trade.

What the game *will* answer at runtime, and now does:

| Data | Source | Table needed |
|---|---|---|
| Vehicle name, localized | `Vehicle.LocalizedName` → `DisplayName` → `ClassLocalizedName` | none, ever |
| Weapon name, localized | `Weapon.GetDisplayNameFromHash` + `Game.GetLocalizedString` | none |
| Ped role, localized | director-name GXT key → `MenuLabelDb.ResolveGxt` | key only |
| Ped category | `Ped.PedType`, `Ped.Gender`, `Model.IsAnimalPed` | none |
| Is this a door | `DOOR_SYSTEM_FIND_EXISTING_DOOR` | none |
| Door state | `DOOR_SYSTEM_GET_DOOR_STATE` / `GET_OPEN_RATIO` | none |
| Size of an unknown model | `Model.GetDimensions` | none |

Vehicles are therefore **deliberately excluded from the bundled table**, and
`build-modelnames.py --verify` fails if any appear — a bundled vehicle name could only
ever be staler than the game's own.

---

## What shipped

### A. `GTA/ModelNames.cs` — the naming ladder

One class owns hash → speech. The rule it enforces: **naming never decides whether an
entity is announced.** Every `Describe*` returns a non-empty string on every path.

- `DescribeVehicle` — game-native only, with the `""` / `"NULL"` / `"CARNOTFOUND"` / `~`
  rejections `MenuLabelDb.ResolveGxt` already established.
- `DescribePed` — GXT key resolved live (a German player hears "Wildschwein"), then the
  bundled English name, then `PedType` + `Gender` + animal flag, then `"person"`.
- `DescribeProp` — table, then size from `Model.GetDimensions`, then `"unknown object"`.
- `DescribeModel(int)` — the one method that *may* return null, for callers doing name
  shape-matching rather than speech (the pickup heuristic, the ladder earcon).
- `IsDoorLike` / `DoorState` — door system first. `DoorState` returns null when the system
  does not own the door, and the caller says "state unknown" rather than guessing.
- Gap log — every hash that reaches a derived description is recorded, deduped, capped at
  500, rewritten (not appended) at most once a minute, to
  `gta11y-modelgaps-<date>.log` at the game root. All write failures swallowed and the
  writer stands itself down; instrumentation must never interrupt a session.

`Model.GetDimensions(out, out)` rather than the `Dimensions` property: the property returns
a `ValueTuple`, which net48 only has via a package reference this project does not carry.

### B. `tools/build-modelnames.py` → `GTA/scripts/gta11y-modelnames.tsv.gz`

Sources: `DurtyFree/gta-v-data-dumps` (`peds.json`, `ObjectList.ini`, `VehicleList.ini`),
merged with the legacy `hashes.txt` so none of its map/door archetypes are lost.

**65,247 rows** (1,109 peds / 62,661 objects / 1,421 doors / 56 other) against the old
53,859. 801 KB gzipped, 2,001 KB raw. 268 peds get the game's own director name — which is
most of the ones actually met on a street: "Tourist", "Skate Punk", "Office Drone", "Surf
Rat", "Airline Pilot", "LSFD", "NOOSE", "Rottweiler", "Meth Heavy".

Format is gzipped TSV, not JSON like the other four tables. Two reasons, both stated in the
tool header: it is an order of magnitude more rows than anything else here and sits on the
startup path, so a `Split('\t')` parser beats Newtonsoft; and when someone reports a wrong
name, a human needs to grep one line out of it.

Guards that fail the build rather than shipping quietly:

- joaat round-trips four anchors, two of them negative.
- Every legacy hash must survive, except the 501 vehicle rows deliberately handed to the
  game — a regeneration that names *fewer* models is the one regression invisible in play.
- No blank names; no vehicle rows; no GXT key on a non-ped row.

`--refresh` re-downloads; `--verify` runs the checks and exits non-zero. Hand fixes live in
`tools/modelnames-overrides.json` (14 seeded: the three protagonists, freemode avatars,
vending machines whose derived name is the model id's word order — "Vend Coffe" — and ATMs,
which screen readers spell out letter by letter).

**Sign normalization matters at build time too.** The dumps use unsigned hashes and
hashes.txt uses signed; the first run of the vehicle-exclusion pass only caught 252 of 501
because of it. Same bug class as the runtime one, twelve lines apart in cause.

### C. Rewiring — nine call sites

All `hashes[...]` expressions and the `Dictionary<string,string> hashes` field are gone.
`ModelNames.LoadStatus` joins the consolidated startup warning (prefix test, so
`ok(Nmalformed)` is not treated as failure). `hashes.txt` is removed from the csproj,
`deploy.ps1` and `package.ps1`; it stays in `External Resources/` as a generator input only.

### D. The two new settings

- **`detailedScans`** — boolean, **ships OFF**, the one new behaviour gate this iteration.
- **`scanResultCap`** — 3 / 5 / 10 / 25 / All, **defaults to 10**, and applies to all four
  scans. Not a second gate: it is a parameter of the same feature and of the naming repair
  that made a cap necessary, the same relationship `menuSnapshotImage` has to
  `menuDebugLog`.

**The cap is the one default that changes behaviour on purpose**, and it needs to be
defended rather than glossed: repairing the naming turns the ped scan from near-silent into
a full enumeration, and the shipped `gta11ySettings.json` has `detectionRadius` at index 12
= **1000 m**. Uncapped, that scan speaks for minutes with no way to stop it. Truncation is
never silent — the dropped count is spoken — because omitting entities without saying so is
the behaviour this iteration exists to end. Index 4 ("everything in range") is exactly the
old behaviour for anyone who wants it.

### E. Scan detail

`PedActivity(Ped)` shares the ladder of `GetPlayerStateString` and puts combat, fleeing and
scenario use ahead of it. It is deliberately *not* a generalization of that method:
`GetPlayerStateString` returns Title Case standalone labels for the Status menu and is
verified as such, while this returns a lowercase mid-sentence clause. Two registers, one
ladder.

`VehicleDetailClause` uses `getDir(v.Heading)` — the counterclockwise convention
`Entity.Heading` is already in. **Do not substitute `HeadingToCompass`**, which is
clockwise; the readout would come out mirrored. Occupancy reuses the phrasing
`AnnounceAutolockTarget` already established.

Every per-entity read in all four loops is inside a `try`, per the
`SafeIsVehicleOnFire` / `SafePetrolTankHealth` precedent — a despawning entity is exactly
when a property read throws, and one throw would lose the whole scan.

### F. Two `listToString` bugs

`vertical` was declared outside the loop and only reassigned when `zDistance != 0`, so every
result at the player's own level inherited the previous result's *"2.1 meters above"* — a
wrong altitude spoken with full confidence. And `" meters above , "` carried a stray space
before the comma.

---

## State

Builds clean: **0 errors, 239 warnings** — the same 239 in
`docs/shvdn-nightly-migration.md`, so nothing new was introduced. `--verify` passes.

**Not yet played.** Everything below is unverified in game:

1. **The headline comparison.** `detectionRadius` = 25 m, busy Vinewood pavement, NumPad 6.
   Before: the bare header. After: a list of named people. Nothing else in this iteration
   matters if this does not change.
2. NumPad 4 in traffic; NumPad 5 at a garage gate; NumPad 8 at a vending machine (the
   interaction suffix must still be there).
3. `detailedScans` on, all four again; confirm the `"...and N more."` tail.
4. `detectionRadius` = 1000 m with detail on — the utterance must stay bounded.
5. A post-2022 DLC vehicle and a DLC ped. The vehicle must name with no table involvement;
   the ped must produce *something*, and appear in `gta11y-modelgaps-*.log` if unnamed.
6. Rename `scripts/gta11y-modelnames.tsv.gz` and relaunch: the startup warning must name
   the missing table, and all four scans must still announce entities generically.
   Degraded, never silent.

**The one assumption nothing here tests:** that the ped director-name GXT keys (`CM_*`)
resolve through `Game.GetLocalizedString` on a retail build. `MenuLabelDb.ResolveGxt` fails
closed if they do not and the bundled English name is the fallback, so a miss degrades to
English rather than to an id — but step 1 is where that is actually settled. If ped names
come out in English on a non-English install, this is why.

**Provenance note.** `DurtyFree/gta-v-data-dumps` states no license. It is auto-generated
metadata (names and hashes) extracted from the game's own files — the same category of data
`hashes.txt` already carried and that `build-map-data.py` already sources from that repo —
but it is worth knowing that no explicit grant exists.
