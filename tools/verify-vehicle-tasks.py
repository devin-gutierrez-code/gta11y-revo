#!/usr/bin/env python3
"""
verify-vehicle-tasks.py — establish, from Rockstar's own shipping scripts, how the vehicle /
plane / helicopter task natives are actually called, and check the mod's call sites against it.

WHY THIS EXISTS. The mod's aviation and autodrive code was written against community native
lists. Two of those inherited assumptions are demonstrably wrong:

  * The 32-bit driving-style table in GTA/GTA11Y.cs labels six bits wrongly or not at all,
    including bit 23 (PlaneTaxiMode) and bit 31 (StopAtDestination) — and the latter is
    unreachable anyway because the flag packer stops at bit 30.
  * pilot_school.c — Rockstar's own flight tutorial — never calls TASK_PLANE_MISSION. It uses
    TASK_VEHICLE_MISSION_COORS_TARGET plus CONTROL_LANDING_GEAR, a native this mod has never
    called at all.

Both were found by reading source, and both are exactly the kind of thing that decays again the
next time someone edits the table by hand. So this is a tool, not a one-off: it PARSES the
tables rather than transcribing them, the same rule the world-data harvester follows after
hand-transcription got the random-event ids wrong.

TWO SOURCES OF TRUTH, NEITHER SUFFICIENT ALONE

  1. `external/shvdn/ScriptHookVDotNet3.dll` — the vendored nightly, reflected for
     GTA.VehicleDrivingFlags. This is the pinned build the mod compiles against, so it cannot
     disagree with what the mod is able to express. Community flag lists can and do.
  2. `calamity-inc/GTA-V-Decompiled-Scripts`, branch `senpai` — what the shipping game does.
     Fetched by raw URL into tools/_cache/decompiled/. DO NOT CLONE that repo; it is enormous,
     and pilot_school.c alone is 2.6 MB.

An enum name says what a flag is called. The corpus says which combinations Rockstar trusts in
production, which mission modes exist in practice, and in what order the arguments go where the
documentation is ambiguous.

MODES

  (default)      fetch (cached) + write docs/vehicle-task-conventions.md
  --refresh      re-download every corpus file and re-reflect the DLL
  --verify       run the checks below, exit non-zero on any failure; downloads nothing new
  --write-flags  print the C# autodriveFlagNames array literal for pasting into GTA11Y.cs

WHAT --verify CHECKS

  A. Every bit that GTA.VehicleDrivingFlags names has curated spoken wording here, and the
     autodriveFlagNames array in GTA/GTA11Y.cs matches this file's generated version exactly.
     Bits the nightly does not name stay "Unknown (bit N)" — honestly unknown beats invented.
  B. The flag packer reaches all 32 bits.
  C. Every mission mode the mod passes to TASK_HELI_MISSION / TASK_PLANE_MISSION /
     TASK_VEHICLE_MISSION_* appears somewhere in the corpus. A mode Rockstar never uses is not
     proof of a bug — it is where the community lists were the only source, which is precisely
     where the known-wrong flag labels came from. Reported as a note, to be justified out loud.

SPOKEN LABELS ARE NOT ENUM NAMES. These strings are read aloud in a menu by a screen reader.
"UseWanderFallbackInsteadOfStraightLine" is a correct identifier and an awful sentence, so each
bit carries a curated phrase. The mapping from bit to MEANING is generated; only the wording is
hand-held, and --verify fails if a newly-named bit has no wording yet.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
CACHE = os.path.join(HERE, "_cache", "decompiled")
FLAGS_CACHE = os.path.join(HERE, "_cache", "drivingflags.json")
SHVDN_DLL = os.path.join(ROOT, "external", "shvdn", "ScriptHookVDotNet3.dll")
GTA11Y = os.path.join(ROOT, "GTA", "GTA11Y.cs")
REPORT = os.path.join(ROOT, "docs", "vehicle-task-conventions.md")

REPO = "calamity-inc/GTA-V-Decompiled-Scripts"
BRANCH = "senpai"
RAW = "https://raw.githubusercontent.com/{}/{}/decompiled_scripts/{}"

# Single-player and SP-adjacent scripts that actually drive or fly something. Deliberately
# excludes the fm_content_* family: those are ~10 MB each, multiplayer, and add no convention
# the smaller scripts do not already show.
CORPUS = [
    # aviation
    "pilot_school.c",            # the flight tutorial: takeoff, landing, gear, bridges
    "launcher_pilotschool.c",
    "am_plane_takedown.c",
    "re_seaplane.c",
    "flyunderbridges.c",
    "am_heli_taxi.c",            # heli mission modes including the drop-off landing
    "am_backup_heli.c",
    "heli_gun.c",
    # AI ground driving with a passenger aboard — the closest analogue to autodrive
    "am_taxi.c",
    "controller_taxi.c",
    "family1taxi.c",
    "taxi_clowncar.c",
    "taxi_cutyouin.c",
    "taxi_deadline.c",
    "taxi_followcar.c",
    "taxi_gotyounow.c",
    "taxi_gotyourback.c",
    "taxi_needexcitement.c",
    "am_boat_taxi.c",
    # driving styles under stress
    "re_getaway_driver.c",
    "re_drunkdriver.c",
]

NATIVES = [
    "TASK_VEHICLE_MISSION_COORS_TARGET",
    "TASK_VEHICLE_MISSION_PED_TARGET",
    "TASK_VEHICLE_MISSION",
    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE",
    "TASK_VEHICLE_DRIVE_TO_COORD",
    "TASK_VEHICLE_DRIVE_WANDER",
    "TASK_VEHICLE_ESCORT",
    "TASK_VEHICLE_FOLLOW",
    "TASK_HELI_MISSION",
    "TASK_PLANE_MISSION",
    "TASK_PLANE_LAND",
    "TASK_PLANE_GOTO_PRECISE_VTOL",
    "TASK_BOAT_MISSION",
    "CONTROL_LANDING_GEAR",
    "GET_LANDING_GEAR_STATE",
    "SET_DRIVE_TASK_DRIVING_STYLE",
    "SET_DRIVE_TASK_CRUISE_SPEED",
]

# Where the mission-mode / driving-style / gear arguments sit, KEYED ON OBSERVED ARITY. The
# decompiler renders a Vector3 either as three scalars or as one struct reference, so the same
# native turns up at two arities and a fixed index would silently read the wrong slot. Any
# (native, arity) pair missing from here is reported as undecoded rather than guessed at.
ARG_SLOTS = {
    "TASK_VEHICLE_MISSION_COORS_TARGET": {
        11: {"mission": 5, "style": 7},   # ped, veh, x, y, z, mission, speed, style, ...
        9:  {"mission": 3, "style": 5},   # ped, veh, vec3, mission, speed, style, ...
    },
    "TASK_VEHICLE_MISSION_PED_TARGET": {
        9: {"mission": 3, "style": 5},
    },
    "TASK_VEHICLE_MISSION": {
        9: {"mission": 3, "style": 5},
    },
    "TASK_VEHICLE_DRIVE_TO_COORD": {
        11: {"style": 8},                 # ped, veh, x, y, z, speed, p6, model, mode, ...
        9:  {"style": 6},                 # ped, veh, vec3, speed, p6, model, mode, ...
    },
    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE": {
        8: {"style": 6},                  # ped, veh, x, y, z, speed, mode, stopRange
        6: {"style": 4},                  # ped, veh, vec3, speed, mode, stopRange
    },
    "TASK_VEHICLE_DRIVE_WANDER": {
        4: {"style": 3},
    },
    "TASK_HELI_MISSION": {
        15: {"mission": 7},               # ped, veh, tgtVeh, tgtPed, x, y, z, mission, ...
        13: {"mission": 5},               # ped, veh, tgtVeh, tgtPed, vec3, mission, ...
    },
    "TASK_PLANE_MISSION": {
        14: {"mission": 7},
        12: {"mission": 5},
    },
    "CONTROL_LANDING_GEAR": {
        2: {"gear": 1},
    },
}

# Landing-gear semantics, READ OUT OF pilot_school.c rather than taken from a community list.
# The polarity is the part that matters: get it backwards and the assist retracts the gear on
# final approach. Each entry cites the line that pins it, in the cached corpus.
#
# The decisive site is pilot_school.c:114940 —
#     if (IS_ENTITY_IN_AIR(veh) && GET_LANDING_GEAR_STATE(veh) == 0) CONTROL_LANDING_GEAR(veh, 1)
# which fixes read-state 0 as DOWN and write-command 1 as RETRACT in one statement.
GEAR_STATES = {          # what GET_LANDING_GEAR_STATE returns
    0: ("deployed", "pilot_school.c:114940 retracts when in air and state==0; "
                    "pilot_school.c:113973 treats 0 and 3 as the gear-down test"),
    1: ("retracting/up", "pilot_school.c:107352 groups 1 with 4 as the gear-up branch"),
    2: ("unused in corpus", "no call site observed"),
    3: ("deploying/down", "pilot_school.c:106696 tests != 3 && != 0 for 'not down'"),
    4: ("retracted", "pilot_school.c:114585 accepts state==4 as the gear-up success condition"),
    5: ("broken", "pilot_school.c:106692 and the IS_PLANE_LANDING_GEAR_INTACT branch at 107352"),
}
GEAR_COMMANDS = {        # what CONTROL_LANDING_GEAR accepts
    0: ("deploy", "pilot_school.c:114174 lowers gear while placing a plane on the runway"),
    1: ("retract", "pilot_school.c:114942, in air with state==0"),
    2: ("set deployed instantly", "pilot_school.c:93285, teleporting a plane onto the ground"),
    3: ("set retracted instantly", "am_plane_takedown.c:580 and re_seaplane.c:2960, "
                                   "both airborne spawns"),
    4: ("set broken", "pilot_school.c:107959, the else-branch when gear was already damaged"),
}
# Rockstar gates every one of these on GET_VEHICLE_HAS_LANDING_GEAR and
# IS_PLANE_LANDING_GEAR_INTACT (pilot_school.c:107350). So does this mod.

# Curated spoken wording, keyed on the canonical nightly enum name for that bit. These are read
# aloud, so they are phrases, not identifiers. --verify fails if the nightly names a bit that is
# absent here — that is the signal a nightly bump added a flag nobody has worded yet.
SPOKEN = {
    "StopForVehicles":                        "Stop for vehicles",
    "StopForPeds":                            "Stop for pedestrians",
    "SwerveAroundAllVehicles":                "Swerve around all vehicles",
    "SteerAroundStationaryVehicles":          "Steer around parked vehicles",
    "SteerAroundPeds":                        "Steer around pedestrians",
    "SteerAroundObjects":                     "Steer around objects",
    "DontSteerAroundPlayerPed":               "Don't steer around the player",
    "StopAtTrafficLights":                    "Stop at traffic lights",
    "UseBlinkers":                            "Use indicators, go off-road when avoiding",
    "AllowGoingWrongWay":                     "Allow driving into oncoming lanes",
    "Reverse":                                "Drive in reverse",
    "UseWanderFallbackInsteadOfStraightLine": "Cruise instead of beelining when lost",
    "AvoidRestrictedAreas":                   "Avoid restricted areas",
    "PreventBackgroundPathfinding":           "No background pathfinding, cruise only",
    "AdjustCruiseSpeedBasedOnRoadSpeed":      "Match the road's speed limit",
    "UseShortCutLinks":                       "Use shortcut links and median crossings",
    "ChangeLanesAroundObstructions":          "Change lanes around obstructions",
    "UseSwitchedOffNodes":                    "Use dirt roads and car parks",
    "DriveBySight":                           "Prefer navmesh routes, drive by sight",
    "PlaneTaxiMode":                          "Plane taxi mode, drive on the ground",
    "ForceStraightLine":                      "Ignore pathfinding, go straight there",
    "UseStringPullingAtJunctions":            "Smooth the line through junctions",
    "TryToAvoidHighways":                     "Avoid highways",
    "ForceJoinInRoadDirection":               "Join the road facing the right way",
    "StopAtDestination":                      "Stop at the destination",
}

# When several enum members share a bit, this is the one whose wording is used. Nightly aliases
# heavily (FollowTraffic == StopForVehicles == 1) and picking alphabetically would be arbitrary.
CANONICAL = {
    1: "StopForVehicles", 2: "StopForPeds", 4: "SwerveAroundAllVehicles",
    8: "SteerAroundStationaryVehicles", 16: "SteerAroundPeds", 32: "SteerAroundObjects",
    64: "DontSteerAroundPlayerPed", 128: "StopAtTrafficLights", 256: "UseBlinkers",
    512: "AllowGoingWrongWay", 1024: "Reverse",
    2048: "UseWanderFallbackInsteadOfStraightLine", 4096: "AvoidRestrictedAreas",
    8192: "PreventBackgroundPathfinding", 16384: "AdjustCruiseSpeedBasedOnRoadSpeed",
    262144: "UseShortCutLinks", 524288: "ChangeLanesAroundObstructions",
    2097152: "UseSwitchedOffNodes", 4194304: "DriveBySight", 8388608: "PlaneTaxiMode",
    16777216: "ForceStraightLine", 33554432: "UseStringPullingAtJunctions",
    536870912: "TryToAvoidHighways", 1073741824: "ForceJoinInRoadDirection",
    2147483648: "StopAtDestination",
}


# ----------------------------------------------------------------------------- corpus fetch

def read_cached(path):
    # newline="" on BOTH ends of the cache round-trip. The corpus is CRLF; text-mode
    # writing added a second carriage return to every line ending, and universal-newline
    # reading then split each mangled ending into two lines. Every line number this tool
    # reported came out at exactly twice the truth, which reads as plausible until you
    # open the file. Never round-trip a foreign file through translated newlines.
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as fh:
        return fh.read()


def fetch(name, refresh=False):
    """The text of one decompiled script, cached on disk. Never clones. Returns None only when
    the file is neither cached nor reachable — an unfetched script is reported, not silently
    treated as containing no call sites."""
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    if os.path.exists(path) and not refresh:
        return read_cached(path)
    url = RAW.format(REPO, BRANCH, name)
    try:
        with urllib.request.urlopen(url, timeout=180) as resp:
            data = resp.read().decode("utf-8", errors="replace")
        # Keep the bytes as they arrived; see read_cached for why translation is fatal here.
    except (urllib.error.URLError, urllib.error.HTTPError, OSError):
        return read_cached(path) if os.path.exists(path) else None
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(data)
    return data


# ----------------------------------------------------------------------- call-site extraction

def split_top_level(text):
    """Splits an argument list on commas that are not inside nested brackets or strings."""
    args, depth, cur, i, n = [], 0, [], 0, len(text)
    in_str = None
    while i < n:
        ch = text[i]
        if in_str:
            cur.append(ch)
            if ch == "\\" and i + 1 < n:
                cur.append(text[i + 1])
                i += 2
                continue
            if ch == in_str:
                in_str = None
        elif ch in "\"'":
            in_str = ch
            cur.append(ch)
        elif ch in "([{":
            depth += 1
            cur.append(ch)
        elif ch in ")]}":
            depth -= 1
            cur.append(ch)
        elif ch == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
        i += 1
    tail = "".join(cur).strip()
    if tail or args:
        args.append(tail)
    return args


def match_paren(text, start):
    """Index of the ')' closing the '(' that `start` sits just past, or -1."""
    depth, i, n, in_str = 1, start, len(text), None
    while i < n:
        ch = text[i]
        if in_str:
            if ch == "\\":
                i += 2
                continue
            if ch == in_str:
                in_str = None
        elif ch in "\"'":
            in_str = ch
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def extract_calls(text, native):
    """Yields (line_no, [args]) for every call to `native`, paren-matched, strings respected."""
    pat = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(native) + r"\s*\(")
    for m in pat.finditer(text):
        end = match_paren(text, m.end())
        if end < 0:
            continue
        yield text.count("\n", 0, m.start()) + 1, split_top_level(text[m.end():end])


INT_RE = re.compile(r"^-?\d+$")
HEX_RE = re.compile(r"^0[xX][0-9a-fA-F]+$")
ROUND_RE = re.compile(r"^SYSTEM::ROUND\(\s*(-?\d+(?:\.\d+)?)f?\s*\)$")


def literal_int(arg):
    """The int value of an argument that is a bare literal, else None. Expressions and named
    constants are deliberately NOT resolved — an unresolved value is reported as unresolved."""
    a = arg.strip()
    if INT_RE.match(a):
        return int(a)
    if HEX_RE.match(a):
        return int(a, 16)
    m = ROUND_RE.match(a)
    if m:
        return int(round(float(m.group(1))))
    return None


def decode_record(native, line, args, script=None):
    arity = len(args)
    slots = ARG_SLOTS.get(native, {}).get(arity, {})
    rec = {"line": line, "arity": arity, "args": args,
           "decoded": bool(slots) or native not in ARG_SLOTS}
    if script:
        rec["script"] = script
    for key, idx in slots.items():
        rec[key] = literal_int(args[idx]) if idx < arity else None
    return rec


def scan_corpus(refresh=False):
    """{native: [record]} over the corpus, plus the list of scripts that could not be read."""
    found, missing = {n: [] for n in NATIVES}, []
    for name in CORPUS:
        text = fetch(name, refresh)
        if text is None:
            missing.append(name)
            continue
        for native in NATIVES:
            for line, args in extract_calls(text, native):
                found[native].append(decode_record(native, line, args, script=name))
    return found, missing


# --------------------------------------------------------------------------- nightly reflection

PS_DUMP = (
    "$a=[Reflection.Assembly]::LoadFrom('{dll}');"
    "$t=$a.GetType('GTA.VehicleDrivingFlags');"
    "$o=@{{}};foreach($n in [Enum]::GetNames($t)){{$o[$n]=[uint32][Enum]::Parse($t,$n)}};"
    "$o|ConvertTo-Json -Compress"
)


def reflect_flags(refresh=False):
    """name -> value, straight out of the vendored nightly. Cached so --verify still runs with
    no PowerShell available, but a cached read is announced rather than passed off as fresh."""
    if os.path.exists(FLAGS_CACHE) and not refresh:
        with open(FLAGS_CACHE, "r", encoding="utf-8") as fh:
            return {k: int(v) for k, v in json.load(fh).items()}, "cache"
    if not os.path.exists(SHVDN_DLL):
        return None, "missing-dll"
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
             PS_DUMP.format(dll=SHVDN_DLL.replace("'", "''"))],
            capture_output=True, text=True, timeout=180)
    except (OSError, subprocess.SubprocessError):
        return None, "no-powershell"
    if out.returncode != 0 or not out.stdout.strip():
        return None, "reflect-failed"
    try:
        data = {k: int(v) for k, v in json.loads(out.stdout).items()}
    except (ValueError, AttributeError):
        return None, "reflect-unparseable"
    os.makedirs(os.path.dirname(FLAGS_CACHE), exist_ok=True)
    with open(FLAGS_CACHE, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=1, sort_keys=True)
    return data, "dll"


def bit_table(flags):
    """bit index -> (canonical enum name or None, [every enum name on that exact bit])."""
    table = {}
    for bit in range(32):
        val = 1 << bit
        names = sorted(n for n, v in flags.items() if v == val)
        canon = CANONICAL.get(val)
        if canon not in names:
            canon = names[0] if names else None
        table[bit] = (canon, names)
    return table


def flag_labels(flags):
    """(bit -> spoken label, [(bit, canonical name, all names) with no wording yet])."""
    table, labels, unworded = bit_table(flags), {}, []
    for bit in range(32):
        canon, names = table[bit]
        if not names:
            labels[bit] = "Unknown (bit {})".format(bit)
        elif canon not in SPOKEN:
            unworded.append((bit, canon, names))
            labels[bit] = "Unknown (bit {})".format(bit)
        else:
            labels[bit] = SPOKEN[canon]
    return labels, unworded


def csharp_array(labels, flags):
    """The exact autodriveFlagNames literal, so the C# is generated rather than transcribed."""
    table = bit_table(flags)
    out = ["        private string[] autodriveFlagNames = {"]
    for bit in range(32):
        _, names = table[bit]
        alias = " / ".join(names) if names else "(unnamed in nightly)"
        body = '"{}"{}'.format(labels[bit], "," if bit < 31 else "")
        out.append("            {:<52}// {} - {}  {}".format(body, bit, 1 << bit, alias))
    out.append("        };")
    return "\n".join(out)


# ------------------------------------------------------------------------------- mod call sites

def read_mod():
    with open(GTA11Y, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def mod_call_sites():
    """The mod's own task-native call sites, parsed the same way as the corpus. The mod calls
    through Function.Call(Hash.NAME, ...), so the argument list starts after the Hash token."""
    src = read_mod()
    sites = {}
    for native in ("TASK_HELI_MISSION", "TASK_PLANE_MISSION", "TASK_PLANE_LAND",
                   "TASK_VEHICLE_MISSION_COORS_TARGET", "TASK_VEHICLE_MISSION_PED_TARGET",
                   "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE", "TASK_VEHICLE_DRIVE_WANDER",
                   "CONTROL_LANDING_GEAR"):
        out = []
        pat = re.compile(r"Function\.Call\s*(?:<[^>]*>)?\s*\(\s*Hash\." + native + r"\s*,")
        for m in pat.finditer(src):
            end = match_paren(src, m.end())
            if end < 0:
                continue
            args = split_top_level(src[m.end():end])
            out.append(decode_record(native, src.count("\n", 0, m.start()) + 1, args))
        sites[native] = out
    return sites


def mod_flag_array():
    """The autodriveFlagNames literal currently in GTA11Y.cs, as a list of strings."""
    m = re.search(r"private\s+string\[\]\s+autodriveFlagNames\s*=\s*\{(.*?)\n\s*\};",
                  read_mod(), re.S)
    if not m:
        return None
    return re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))


def packer_bit_bound():
    """The exclusive upper bound of GetDrivingStyleFromFlags's loop, or None if unparseable."""
    m = re.search(r"private\s+int\s+GetDrivingStyleFromFlags\s*\(\s*\)\s*\{(.*?)\n\s{8}\}",
                  read_mod(), re.S)
    if not m:
        return None
    lm = re.search(r"for\s*\(\s*int\s+i\s*=\s*0\s*;\s*i\s*<\s*(\d+)", m.group(1))
    return int(lm.group(1)) if lm else None


# ------------------------------------------------------------------------------------- report

def histogram(records, key):
    hist = {}
    for r in records:
        v = r.get(key)
        if v is None:
            continue
        hist.setdefault(v, []).append(r)
    return dict(sorted(hist.items(), key=lambda kv: (-len(kv[1]), kv[0])))


def decode_style(value, flags):
    if value is None:
        return ""
    table = bit_table(flags)
    parts = [table[b][0] or "bit{}".format(b) for b in range(32) if value & (1 << b)]
    return " | ".join(parts) if parts else "(none)"


def scripts_of(records, limit=4):
    names = sorted({r["script"] for r in records if "script" in r})
    shown = ", ".join("`{}`".format(s) for s in names[:limit])
    return shown + (" +{} more".format(len(names) - limit) if len(names) > limit else "")


def write_report(found, missing, flags, flag_source):
    labels, unworded = flag_labels(flags)
    table = bit_table(flags)
    L = []
    L.append("# Vehicle, plane and helicopter task conventions")
    L.append("")
    L.append("Generated by `tools/verify-vehicle-tasks.py`. **Do not hand-edit** — regenerate.")
    L.append("")
    L.append("Two first-party sources, neither sufficient alone:")
    L.append("")
    L.append("- **Flag names** reflected from `external/shvdn/ScriptHookVDotNet3.dll` "
             "(`GTA.VehicleDrivingFlags`), read from the {}.".format(
                 {"dll": "vendored DLL", "cache": "cached reflection"}.get(
                     flag_source, flag_source)))
    L.append("- **Usage** parsed from `{}` branch `{}` — {} of {} scripts read.".format(
        REPO, BRANCH, len(CORPUS) - len(missing), len(CORPUS)))
    L.append("")
    if missing:
        L.append("> **The corpus is incomplete.** These scripts could not be fetched and are")
        L.append("> absent from every count below: `{}`. A count is not a ceiling when the"
                 .format("`, `".join(missing)))
        L.append("> corpus is short of what it claims to cover.")
        L.append("")

    L.append("## Driving-style bits")
    L.append("")
    L.append("| bit | value | nightly name(s) | spoken label |")
    L.append("|---:|---:|---|---|")
    for bit in range(32):
        _, names = table[bit]
        L.append("| {} | {} | {} | {} |".format(
            bit, 1 << bit,
            ", ".join("`{}`".format(n) for n in names) if names else "*(unnamed)*",
            labels[bit]))
    L.append("")
    L.append("Seven bits (15, 16, 17, 20, 26, 27, 28) are unnamed by the nightly as well. Those")
    L.append("stay `Unknown (bit N)` in the menu: a bit whose meaning nobody knows is read out")
    L.append("as unknown rather than given a plausible-sounding invention.")
    L.append("")
    if unworded:
        L.append("**{} bit(s) named by nightly have no spoken wording yet** — `--verify` fails"
                 " on each:".format(len(unworded)))
        for bit, canon, _ in unworded:
            L.append("- bit {} — `{}`".format(bit, canon))
        L.append("")

    L.append("## Composite presets the nightly ships")
    L.append("")
    L.append("| name | value | decoded |")
    L.append("|---|---:|---|")
    for name, val in sorted(flags.items(), key=lambda kv: kv[1]):
        if val and (val & (val - 1)):
            L.append("| `{}` | {} | {} |".format(name, val, decode_style(val, flags)))
    L.append("")

    L.append("## What Rockstar actually passes")
    L.append("")
    for native in NATIVES:
        recs = found.get(native) or []
        if not recs:
            continue
        L.append("### `{}` — {} call site(s)".format(native, len(recs)))
        L.append("")
        arities = {}
        for r in recs:
            arities[r["arity"]] = arities.get(r["arity"], 0) + 1
        L.append("Arity: " + ", ".join("{} args x{}".format(a, c)
                                       for a, c in sorted(arities.items())))
        unmapped = sorted(a for a in arities
                          if native in ARG_SLOTS and a not in ARG_SLOTS[native])
        if unmapped:
            L.append("")
            L.append("> Arity {} has no argument map in this tool, so those call sites are "
                     "**left undecoded** below rather than read at a guessed offset.".format(
                         ", ".join(str(u) for u in unmapped)))
        L.append("")
        mh = histogram(recs, "mission")
        if mh:
            L.append("| mission mode | uses | scripts |")
            L.append("|---:|---:|---|")
            for mode, rs in mh.items():
                L.append("| {} | {} | {} |".format(mode, len(rs), scripts_of(rs)))
            L.append("")
        sh = histogram(recs, "style")
        if sh:
            L.append("| driving style | uses | decoded |")
            L.append("|---:|---:|---|")
            for val, rs in sh.items():
                L.append("| {} | {} | {} |".format(val, len(rs), decode_style(val, flags)))
            L.append("")
        gh = histogram(recs, "gear")
        if gh:
            L.append("| gear state | uses | scripts |")
            L.append("|---:|---:|---|")
            for st, rs in gh.items():
                L.append("| {} | {} | {} |".format(st, len(rs), scripts_of(rs, limit=6)))
            L.append("")
            L.append("Raw call sites, so the surrounding intent can be read directly:")
            L.append("")
            for r in recs[:24]:
                L.append("- `{}:{}` — `{}({})`".format(
                    r.get("script", "?"), r["line"], native, ", ".join(r["args"])))
            L.append("")

    L.append("## Landing-gear semantics")
    L.append("")
    L.append("Read out of `pilot_school.c`, not taken from a community native list. The polarity")
    L.append("is the part that matters: reversed, an assist retracts the gear on final approach.")
    L.append("")
    L.append("The decisive site is `pilot_school.c:114940` —")
    L.append("`if (IS_ENTITY_IN_AIR(veh) && GET_LANDING_GEAR_STATE(veh) == 0) "
             "CONTROL_LANDING_GEAR(veh, 1)` — which fixes read-state 0 as DOWN and")
    L.append("write-command 1 as RETRACT in a single statement.")
    L.append("")
    L.append("| GET_LANDING_GEAR_STATE | meaning | evidence |")
    L.append("|---:|---|---|")
    for k in sorted(GEAR_STATES):
        L.append("| {} | {} | {} |".format(k, GEAR_STATES[k][0], GEAR_STATES[k][1]))
    L.append("")
    L.append("| CONTROL_LANDING_GEAR | meaning | evidence |")
    L.append("|---:|---|---|")
    for k in sorted(GEAR_COMMANDS):
        L.append("| {} | {} | {} |".format(k, GEAR_COMMANDS[k][0], GEAR_COMMANDS[k][1]))
    L.append("")
    L.append("Rockstar gates every gear call on `GET_VEHICLE_HAS_LANDING_GEAR` and")
    L.append("`IS_PLANE_LANDING_GEAR_INTACT` (`pilot_school.c:107350`). So does this mod, and a")
    L.append("gear reading 5 (broken) is spoken aloud rather than silently worked around — a")
    L.append("blind pilot has no other way to know the landing is going to be bad.")
    L.append("")
    L.append("## The mod's own call sites")
    L.append("")
    L.append("| native | GTA11Y.cs | arity | mission | gear | in corpus? |")
    L.append("|---|---:|---:|---:|---:|---|")
    for native, recs in mod_call_sites().items():
        corpus_modes = set(histogram(found.get(native) or [], "mission").keys())
        for r in recs:
            if r.get("mission") is None:
                verdict = "n/a"
            elif not corpus_modes:
                verdict = "no corpus data"
            else:
                verdict = "yes" if r["mission"] in corpus_modes else "**NO**"
            L.append("| `{}` | {} | {} | {} | {} | {} |".format(
                native, r["line"], r["arity"],
                "-" if r.get("mission") is None else r["mission"],
                "-" if r.get("gear") is None else r["gear"], verdict))
    L.append("")
    L.append("A mode marked **NO** is not automatically a bug — this corpus is a sample and")
    L.append("Rockstar had no reason to use every mode. It marks the places where a community")
    L.append("native list was the only source, which is exactly where the mod inherited its")
    L.append("known-wrong flag labels. Each one gets justified out loud or changed.")
    L.append("")

    os.makedirs(os.path.dirname(REPORT), exist_ok=True)
    with open(REPORT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(L) + "\n")
    return REPORT


# ------------------------------------------------------------------------------------- verify

def verify(flags, flag_source, found, missing):
    fails, notes = [], []

    if flag_source == "cache":
        notes.append("flag values came from the cache, not a fresh reflection — "
                     "run --refresh after a nightly bump")

    labels, unworded = flag_labels(flags)
    for bit, canon, _ in unworded:
        fails.append("bit {} is named `{}` by nightly but has no wording in SPOKEN[]"
                     .format(bit, canon))

    have = mod_flag_array()
    if have is None:
        fails.append("could not find autodriveFlagNames in GTA/GTA11Y.cs")
    elif len(have) != 32:
        fails.append("autodriveFlagNames has {} entries, expected 32".format(len(have)))
    else:
        for bit in range(32):
            if have[bit] != labels[bit]:
                fails.append('bit {}: GTA11Y.cs says "{}", nightly says "{}"'
                             .format(bit, have[bit], labels[bit]))

    bound = packer_bit_bound()
    if bound is None:
        notes.append("could not parse the loop bound in GetDrivingStyleFromFlags")
    elif bound < 32:
        fails.append("GetDrivingStyleFromFlags stops at bit {}, so bit(s) {}..31 cannot be set"
                     .format(bound - 1, bound))

    if missing:
        notes.append("corpus incomplete — {} script(s) unfetched: {}".format(
            len(missing), ", ".join(missing)))

    for native, recs in mod_call_sites().items():
        corpus_modes = set(histogram(found.get(native) or [], "mission").keys())
        if not corpus_modes:
            continue
        for r in recs:
            # A mission argument that is a call rather than a literal cannot be checked from
            # here. Say so: a check that silently stops checking reads exactly like a check
            # that passes, and this tool exists because that failure already happened once.
            slots = ARG_SLOTS.get(native, {}).get(r["arity"], {})
            if "mission" in slots and r.get("mission") is None:
                notes.append("{} at GTA11Y.cs:{} computes its mission mode at runtime "
                             "({}) - not checkable against the corpus from source".format(
                                 native, r["line"], r["args"][slots["mission"]].strip()))
            if r.get("mission") is not None and r["mission"] not in corpus_modes:
                notes.append("{} at GTA11Y.cs:{} uses mission mode {}; no corpus script does "
                             "(corpus uses {})".format(
                                 native, r["line"], r["mission"],
                                 ", ".join(str(m) for m in sorted(corpus_modes))))
    return fails, notes


def main():
    ap = argparse.ArgumentParser(
        description="Verify the mod's vehicle/plane/heli task usage against Rockstar's scripts.")
    ap.add_argument("--refresh", action="store_true",
                    help="re-download the corpus and re-reflect the DLL")
    ap.add_argument("--verify", action="store_true",
                    help="run the checks and exit non-zero on failure")
    ap.add_argument("--write-flags", action="store_true",
                    help="print the C# autodriveFlagNames array literal")
    args = ap.parse_args()

    flags, flag_source = reflect_flags(refresh=args.refresh)
    if flags is None:
        print("FATAL: cannot read GTA.VehicleDrivingFlags ({}). Not knowing is not the same "
              "as being current.".format(flag_source), file=sys.stderr)
        return 2

    if args.write_flags:
        labels, _ = flag_labels(flags)
        print(csharp_array(labels, flags))
        return 0

    found, missing = scan_corpus(refresh=args.refresh)

    if args.verify:
        fails, notes = verify(flags, flag_source, found, missing)
        for n in notes:
            print("note: " + n)
        for f in fails:
            print("FAIL: " + f)
        print("\n{} check(s) failed, {} note(s).".format(len(fails), len(notes)))
        return 1 if fails else 0

    path = write_report(found, missing, flags, flag_source)
    print("scanned {}/{} scripts, {} call sites".format(
        len(CORPUS) - len(missing), len(CORPUS),
        sum(len(v) for v in found.values())))
    if missing:
        print("UNFETCHED: " + ", ".join(missing))
    print("wrote " + os.path.relpath(path, ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
