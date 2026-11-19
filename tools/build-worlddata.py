#!/usr/bin/env python3
"""
build-worlddata.py — produces GTA/scripts/gta11y-worlddata.json for the gta11y mod.

Harvests static world data out of Rockstar's own decompiled scripts: collectible
coordinates, restricted-area boxes, activity/stranger trigger points and the ambient
object-interaction catalogue. None of this is queryable from natives at runtime — it is
baked into the scripts — and all of it is content a blind player cannot otherwise find.

Consumed at runtime by the WorldDataDb class (GTA/WorldDataDb.cs).

Source corpus: calamity-inc/GTA-V-Decompiled-Scripts, branch `senpai`, folder
`decompiled_scripts/`. That repo is ~1.8 GB across 1,156 files. DO NOT CLONE IT.
This script fetches only the handful of files it parses, by raw URL, into
tools/_cache/decompiled/. Re-run with --refresh to re-download.

Hand overrides: tools/worlddata-overrides.json (merged LAST; survives regeneration).
Rows carry status = assumed | verified | refuted plus an `evidence` string, the same
provenance model tools/menulabels-overrides.json uses. Generator output is never
hand-edited — correct it in the overrides file so the fix survives the next run.

Output schema (version 1):
{
  "version": 1,
  "generated": {"by": ..., "utc": ..., "sources": {"<file.c>": {sha256, mtime, bytes}}},
  "collectibles": [{"set", "index", "variant", "x", "y", "z", "statSet", "gxtTitle"}],
  "areas":        [{"id", "name", "gxt", "wanted", "boxes": [{"a": [x,y,z], "b": [x,y,z],
                    "width": f, "ceilingBase": f|null}]}],
  "places":       [{"kind", "id", "name", "gxt", "x", "y", "z", "blip"}],
  "interactions": [{"model", "hash", "kind", "script", "animDict", "clips": [...],
                    "gxt": [...]}],
  "randomEvents": [{"id", "script", "gxt"}],
  "dispatch":     [{"service", "id", "name", "units", "radius"}]
}

Why the parsers look the way they do: the decompiler's output is extremely regular, so
three small shape-specific parsers cover every table we need. They are deliberately
strict — a parser that silently returns fewer rows when Rockstar reshuffles a script is
worse than one that raises, because a half-populated collectible set reads to the player
as "you have found everything" when they have not. Every extractor therefore asserts an
expected row count where we know it (see EXPECTED below).
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys
import urllib.request

HERE      = os.path.dirname(os.path.abspath(__file__))
CACHE     = os.path.join(HERE, "_cache", "decompiled")
OVERRIDES = os.path.join(HERE, "worlddata-overrides.json")
OUT       = os.path.join(HERE, os.pardir, "GTA", "scripts", "gta11y-worlddata.json")

RAW_BASE = ("https://raw.githubusercontent.com/calamity-inc/"
            "GTA-V-Decompiled-Scripts/senpai/decompiled_scripts/")

# Scripts we parse. Anything not listed here is never downloaded.
SCRIPTS = [
    # collectibles
    "letterscraps.c",
    "spaceshipparts.c",
    "underwaterpickups.c",
    # restricted / denial areas
    "restrictedareas.c",
    # ambient object interactions — see INTERACTION_TARGETS for why this list is short
    "ob_tv.c", "ob_jukebox.c", "ob_telescope.c", "ob_vend1.c", "ob_vend2.c",
]

# The ob_* family is heterogeneous: some scripts test their target with
# GET_ENTITY_MODEL, some with GET_CLOSEST_OBJECT_OF_TYPE, some only ever name it in a
# bare joaat() passed to a helper. An auto-scrape across all 30 of them produced 99 rows
# of which most were held props (glasses, joints, lighters) shared by the consumable
# scripts — which this mod already models properly in its own 31-item consumables
# system, so harvesting them adds nothing and actively pollutes the table.
#
# So: name the target explicitly per script, the same way build-map-data.py uses a
# hand-curated road-classification.json. Small and correct beats large and wrong — this
# text is spoken to someone who cannot see the object being described.
#   script -> (model-name pattern, spoken noun, spoken verb phrase)
INTERACTION_TARGETS = {
    "ob_jukebox":   (r"jukebox",                      "jukebox",         "play music"),
    "ob_telescope": (r"telescope",                    "telescope",       "look through"),
    "ob_tv":        (r"(_tv_|_tv0|tvsmash|mm_scre)",  "television",      "watch"),
    "ob_vend1":     (r"^prop_vend_",                  "vending machine", "buy a drink or snack"),
    "ob_vend2":     (r"^prop_vend_",                  "vending machine", "buy a drink or snack"),
}

# Row counts we know from the game. A mismatch means the upstream script changed shape
# and the parser is now lying; fail loudly rather than ship a short list.
EXPECTED = {
    "letterscraps":   50,
    "spaceshipparts": 50,
}

# re_arrests.c lives in the repo root already (it was pulled by hand during the phone
# investigation). Every re_*.c embeds the same ambient-flow library, so this one file
# carries the whole Strangers & Freaks table — no download, no 1.8 GB checkout.
LOCAL_SOURCES = {
    "re_arrests.c": os.path.join(HERE, os.pardir, "re_arrests.c"),
}

# Which packed-stat family each collectible set is scored against. Collected-state MUST
# come from these at runtime, never from HAS_PICKUP_BEEN_COLLECTED: the scripts only
# CREATE_PICKUP within 50 m of the player, so the pickup handle does not exist for the
# 49 collectibles you are not standing next to.
STAT_SET = {
    "letterscraps":      "num_hidden_packages_5",
    "spaceshipparts":    "num_hidden_packages_6",
    "underwaterpickups": None,
}

GXT_TITLE = {
    "letterscraps":      "LETTERS_TITLE",
    "spaceshipparts":    "SSHIP_TITLE",
    "underwaterpickups": None,
}

# restrictedareas.c identifies its area groups only by switch index. These four are
# named because their coordinate signature is unmistakable (centroid plus the airspace
# ceiling the script grants them). The rest are deliberately left unnamed: the runtime
# speaks GET_NAME_OF_ZONE for those instead, which is strictly better than a guess here.
# Pin a verified name in tools/worlddata-overrides.json rather than inventing one.
AREA_NAMES = {
    2: ("lsia",        "Los Santos International airside",  4),   # (-1277,-3001) ceil 250
    3: ("zancudo",     "Fort Zancudo",                      4),   # (-2220, 3164) ceil 250
    4: ("bolingbroke", "Bolingbroke Penitentiary",          4),   # ( 1712, 2589) ceil 150
    5: ("humane_labs", "Humane Labs and Research",          4),   # ( 3532, 3713) ceil  40
}

# The vanilla random-event registry is parsed out of re_arrests.c rather than
# transcribed — see parse_random_events(). Transcribing it by hand got the ids wrong on
# the first attempt (re_securityvan is 9, not 8; id 8 is RE_BIKETHIEFSTAMP, which no
# script maps to), and a wrong id means the director starts a different event than the
# one the player chose.

# Dispatch services, read straight out of emergencycall.c (repo root). The service ids
# and unit counts are Rockstar's own 911 wiring, not guesses.
DISPATCH = [
    {"service": 7, "id": "police",    "name": "Police",       "units": 2, "radius": 3.0},
    {"service": 5, "id": "ambulance", "name": "Ambulance",    "units": 2, "radius": 3.0},
    {"service": 3, "id": "fire",      "name": "Fire Brigade", "units": 4, "radius": 3.0},
]


# ---------------------------------------------------------------------------
# Fetch / provenance (same conventions as build-map-data.py + build-menulabels.py)
# ---------------------------------------------------------------------------

def fetch(name, refresh=False):
    """Download decompiled_scripts/<name> into CACHE unless already present. Prints the
    cached file's age so a stale snapshot is visible on every run rather than silently
    pinning the pipeline."""
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    if os.path.exists(path) and not refresh:
        age = (datetime.datetime.now().timestamp() - os.path.getmtime(path)) / 86400
        print(f"  cached {name} (age {age:.0f} days; --refresh to re-download)")
    else:
        url = RAW_BASE + name
        print(f"  downloading {name}")
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "gta11y-build"})
            with urllib.request.urlopen(req, timeout=90) as r:
                data = r.read()
            with open(path, "wb") as fh:
                fh.write(data)
        except Exception as exc:
            print(f"  ERROR downloading {name}: {exc}")
            if os.path.exists(path):
                print(f"  keeping existing cached copy of {name}")
            else:
                raise
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def read_local(name):
    path = LOCAL_SOURCES[name]
    if not os.path.exists(path):
        sys.exit(f"FATAL missing local source {os.path.normpath(path)}. It ships in the "
                 f"repo root; restore it or point LOCAL_SOURCES at a copy.")
    print(f"  local {name}")
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def src_meta(name):
    path = LOCAL_SOURCES.get(name) or os.path.join(CACHE, name)
    with open(path, "rb") as fh:
        data = fh.read()
    return {
        "sha256": hashlib.sha256(data).hexdigest()[:12],
        "mtime": datetime.datetime.fromtimestamp(
            os.path.getmtime(path), datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "bytes": len(data),
    }


def joaat(s):
    """Rockstar's string hash, signed-int32 like the rest of the mod's tables."""
    h = 0
    for c in s.lower():
        h = (h + ord(c)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h ^= h >> 6
    h = (h + (h << 3)) & 0xFFFFFFFF
    h ^= h >> 11
    h = (h + (h << 15)) & 0xFFFFFFFF
    return h - 0x100000000 if h >= 0x80000000 else h


# ---------------------------------------------------------------------------
# Parsers
# ---------------------------------------------------------------------------

FLOAT = r"(-?\d+(?:\.\d+)?)f?"
VEC3_RETURN = re.compile(r"return\s+%s,\s*%s,\s*%s\s*;" % (FLOAT, FLOAT, FLOAT))
VEC3_BRACE  = re.compile(r"\{\s*%s,\s*%s,\s*%s\s*\}" % (FLOAT, FLOAT, FLOAT))
CASE_ANY    = re.compile(r"^(\t*)case\s+(\d+):")


def parse_case_vec3(src, setname):
    """Shape 1 — `Vector3 func_N(int idx, int variant) { switch (idx) { case K:
    ... return x, y, z; } }`. Used by letterscraps.c and spaceshipparts.c.

    Several cases carry TWO returns inside an `if (iParam1 == 1)` — an interior/exterior
    or pickup/blip pair. We keep both and tag them variant 0/1 rather than guessing which
    one the player walks to, because guessing wrong sends someone to a coordinate inside
    solid geometry.
    """
    lines = src.splitlines()
    # Find the Vector3 dispatch function that actually contains coordinate returns.
    start = None
    for i, line in enumerate(lines):
        if re.match(r"^Vector3 func_\d+\(int \w+, int \w+\)", line):
            window = "\n".join(lines[i:i + 400])
            if VEC3_RETURN.search(window):
                start = i
                break
    if start is None:
        raise RuntimeError(f"{setname}: no Vector3 case-dispatch function found")

    out, index, variant = [], None, 0
    for line in lines[start:]:
        if line.startswith("Vector3 func_") and out:
            break                                   # next function; done
        m = CASE_ANY.match(line)
        if m:
            index, variant = int(m.group(2)), 0
            continue
        if index is None:
            continue
        r = VEC3_RETURN.search(line)
        if r:
            x, y, z = (float(r.group(1)), float(r.group(2)), float(r.group(3)))
            # The dispatch ends with a `return 0, 0, 0;` fallback. That is not a
            # position, and routing a blind player to the world origin is exactly the
            # class of bug that makes a navigation aid untrustworthy.
            if x == 0.0 and y == 0.0 and z == 0.0:
                continue
            out.append({"set": setname, "index": index, "variant": variant,
                        "x": x, "y": y, "z": z})
            variant += 1
    return out


def parse_nested_case_assign(src, setname):
    """Shape 2 — nested switch, `case G:` (site) containing `case I:` (item) with
    `*uParam1 = joaat("pickup_x"); *uParam2 = { x, y, z };`. Used by underwaterpickups.c.

    Note this script is the 12 underwater dive-site loot caches (135 pickups), NOT the
    nuclear waste barrels — those are not present anywhere in the corpus under a
    recognisable name, so they are simply not covered.
    """
    lines = src.splitlines()
    out, group, index, pending = [], None, None, None
    for line in lines:
        m = CASE_ANY.match(line)
        if m:
            depth, num = len(m.group(1)), int(m.group(2))
            if depth <= 2:
                group, index, pending = num, None, None
            else:
                index, pending = num, None
            continue
        if group is None or index is None:
            continue
        j = re.search(r'joaat\("(pickup_[a-z_0-9]+)"\)', line)
        if j and pending is None:
            pending = j.group(1)
        v = VEC3_BRACE.search(line)
        # Only a vec3 that FOLLOWS a pickup-type assignment in the same case is a
        # pickup position. Without this guard the out-param initialisers at the top of
        # the function (`*uParam3 = { 0f, 0f, 0f }`) and the rotation vectors get
        # harvested as if they were places to swim to.
        if v and pending:
            out.append({"set": setname, "index": len(out), "site": group,
                        "slot": index, "pickup": pending,
                        "x": float(v.group(1)), "y": float(v.group(2)),
                        "z": float(v.group(3))})
            pending = None
    return out


def parse_indexed_boxes(src):
    """Shape 3 — `Var0[i /*3*/] = {a}; Var46[i /*3*/] = {b}; fVar92[i] = width;`
    grouped by an enclosing `case N:`, with the live box count in `iVar108`.
    Used by restrictedareas.c.

    The `b` corner's Z is often `IntToFloat((250 + iParam4))` — a runtime-adjusted
    ceiling, not a literal. We record the base (250) and leave the ceiling open rather
    than baking a number that is wrong whenever the game passes a non-zero iParam4.
    """
    lines = src.splitlines()
    groups, cur = {}, None
    a_re = re.compile(r"Var0\[(\d+) /\*3\*/\] = \{\s*%s,\s*%s,\s*%s\s*\}" % (FLOAT, FLOAT, FLOAT))
    b_lit = re.compile(r"Var46\[(\d+) /\*3\*/\] = \{\s*%s,\s*%s,\s*%s\s*\}" % (FLOAT, FLOAT, FLOAT))
    b_par = re.compile(r"Var46\[(\d+) /\*3\*/\] = \{\s*%s,\s*%s,\s*IntToFloat\(\((\d+)" % (FLOAT, FLOAT))
    w_re  = re.compile(r"fVar92\[(\d+)\] = %s;" % FLOAT)

    for line in lines:
        m = CASE_ANY.match(line)
        if m:
            cur = int(m.group(2))
            groups.setdefault(cur, {})
            continue
        if cur is None:
            continue
        slot = groups[cur]
        for rx, key in ((a_re, "a"), (b_lit, "b"), (b_par, "bp"), (w_re, "w")):
            g = rx.search(line)
            if not g:
                continue
            i = int(g.group(1))
            box = slot.setdefault(i, {})
            if key == "a":
                box["a"] = [float(g.group(2)), float(g.group(3)), float(g.group(4))]
            elif key == "b":
                box["b"] = [float(g.group(2)), float(g.group(3)), float(g.group(4))]
                box["ceilingBase"] = None
            elif key == "bp":
                # Ceiling is `IntToFloat((250 + iParam4))` — a runtime-adjusted airspace
                # cap. Write the BASE into b[2] rather than null: the consumer reads
                # float[], where null would silently become 0 and collapse the box to a
                # zero-height slab that never matches. The base is also the conservative
                # choice, since the game only ever raises it (iParam4 >= 0).
                box["b"] = [float(g.group(2)), float(g.group(3)), float(g.group(4))]
                box["ceilingBase"] = float(g.group(4))
            else:
                box["width"] = float(g.group(2))

    areas = []
    for gid, boxes in sorted(groups.items()):
        complete = [b for _, b in sorted(boxes.items())
                    if "a" in b and "b" in b and "width" in b]
        if not complete:
            continue
        known = AREA_NAMES.get(gid)
        ident, name, wanted = known if known else (f"area_{gid}", None, 0)
        areas.append({"id": ident, "group": gid, "name": name, "gxt": None,
                      "wanted": wanted, "named": known is not None,
                      "boxes": complete})
    return areas


# Name prefixes that are not world props. The ob_* scripts test the player's own model
# as often as the thing you interact with (ped prefixes), and they also hash MP
# money-service ids and destruction-set names that look exactly like model names
# (service_spend_jukebox, des_tvsmash_start). Without this filter the catalogue ends up
# telling a blind player they can walk up to a billing event.
NON_PROP_PREFIXES = ("mp_", "a_m_", "a_f_", "s_m_", "s_f_", "g_m_", "g_f_", "u_m_",
                     "u_f_", "ig_", "csb_", "cs_", "player_", "hc_",
                     "service_", "des_")


def parse_interactions(name, src):
    """Shape 4 — the ob_* ambient-interaction family. Collect every model name the script
    mentions, then keep only the ones matching that script's declared target pattern from
    INTERACTION_TARGETS. The script is the evidence that these props are interactive; the
    pattern is what stops the held props and pedestrian models coming along for the ride.
    """
    script = name[:-2]
    target = INTERACTION_TARGETS.get(script)
    if target is None:
        return []
    pattern, noun, verb = target

    models = set(re.findall(r'joaat\("([a-z_0-9]+)"\)', src))
    models |= set(re.findall(r'GET_ENTITY_MODEL\([^)]*\)\s*==\s*joaat\("([a-z_0-9]+)"\)', src))
    keep = sorted(m for m in models
                  if not m.startswith(NON_PROP_PREFIXES) and re.search(pattern, m))
    if not keep:
        raise RuntimeError(f"{script}: target pattern /{pattern}/ matched no model; the "
                           f"script changed shape and the interaction table would ship "
                           f"empty for this object class")
    dicts = sorted(set(re.findall(r'REQUEST_ANIM_DICT\("([A-Za-z_@0-9]+)"\)', src)))
    return [{
        "kind": noun, "verb": verb, "script": script, "model": m, "hash": joaat(m),
        "animDicts": dicts,
    } for m in keep]


# The Strangers & Freaks registration table. Every re_*.c embeds the same ambient-flow
# library, so re_arrests.c (already in the repo root — no download) carries all 64 rows:
#   func_118(ctx, "Abigail1", ..., 4, -1604.668f, 5239.1f, 3.01f, 66, "", 109, ...)
#              ^name                    ^x         ^y       ^z    ^blip
STRANGER_ROW = re.compile(
    r'func_118\(\s*\w+,\s*"([A-Za-z0-9_]+)"\s*,[^;]*?'
    r'%s,\s*%s,\s*%s,\s*(-?\d+),' % (FLOAT, FLOAT, FLOAT))


def parse_random_events(src):
    """The vanilla random-event registry, from two switches in the shared ambient-flow
    library: `case joaat("re_x"): return N;` gives script -> engine id, and
    `char* func_N(int id, bool) { case K: return "RE_X"; }` gives id -> GXT title.

    Ids are sparse — 8 exists as a title (RE_BIKETHIEFSTAMP) with no script behind it,
    so the registry is 33 launchable events over the id range 0..33.
    """
    ids = {}
    for m in re.finditer(r'case joaat\("(re_[a-z_]+)"\):\s*\n\s*return (\d+);', src):
        ids[m.group(1)] = int(m.group(2))
    if not ids:
        raise RuntimeError("random events: script->id switch not found in re_arrests.c")

    titles = {}
    fn = re.search(r'char\* func_\d+\(int \w+, bool \w+\)\s*\n\{\s*\n\s*switch', src)
    if fn:
        tail = src[fn.start():]
        cut = tail.find("\n}\n")
        for m in re.finditer(r'case (\d+):\s*\n\s*return "(RE_[A-Z_]+)";',
                             tail[:cut if cut > 0 else len(tail)]):
            titles[int(m.group(1))] = m.group(2)
    if not titles:
        raise RuntimeError("random events: id->GXT switch not found in re_arrests.c")

    out = [{"id": rid, "script": script, "gxt": titles.get(rid)}
           for script, rid in sorted(ids.items(), key=lambda kv: kv[1])]
    missing = [e["script"] for e in out if not e["gxt"]]
    if missing:
        raise RuntimeError(f"random events: no GXT title for {missing}")
    return out


def parse_stranger_places(src):
    """Parse the 64-row Strangers & Freaks table out of the shared ambient-flow library.
    Coordinates are Rockstar's own trigger points, so these ship as `verified` — unlike
    a heuristic scrape, the argument positions are fixed by the function signature."""
    out, seen = [], set()
    for m in STRANGER_ROW.finditer(src):
        name = m.group(1)
        x, y, z, blip = (float(m.group(2)), float(m.group(3)),
                         float(m.group(4)), int(m.group(5)))
        if abs(x) > 5000 or abs(y) > 9000 or z < -200 or z > 2000:
            continue
        if name in seen:
            continue
        seen.add(name)
        out.append({"kind": "stranger", "id": name, "name": name, "gxt": None,
                    "x": x, "y": y, "z": z, "blip": blip, "status": "verified"})
    return out


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--refresh", action="store_true",
                    help="re-download every source script instead of using the cache")
    args = ap.parse_args()

    print("fetching sources")
    src = {name: fetch(name, args.refresh) for name in SCRIPTS}
    src.update({name: read_local(name) for name in LOCAL_SOURCES})

    print("parsing")
    collectibles = []
    for setname in ("letterscraps", "spaceshipparts"):
        rows = parse_case_vec3(src[setname + ".c"], setname)
        idx = {r["index"] for r in rows}
        want = EXPECTED.get(setname)
        if want is not None and len(idx) != want:
            sys.exit(f"FATAL {setname}: parsed {len(idx)} indices, expected {want}. "
                     f"The upstream script changed shape — fix the parser, do not ship "
                     f"a short collectible list.")
        for r in rows:
            r["statSet"] = STAT_SET[setname]
            r["gxtTitle"] = GXT_TITLE[setname]
        collectibles += rows
        print(f"  {setname}: {len(idx)} indices, {len(rows)} coordinates")

    uw = parse_nested_case_assign(src["underwaterpickups.c"], "underwaterpickups")
    for r in uw:
        r["statSet"] = STAT_SET["underwaterpickups"]
        r["gxtTitle"] = GXT_TITLE["underwaterpickups"]
    collectibles += uw
    sites = len({r["site"] for r in uw})
    print(f"  underwaterpickups: {len(uw)} pickups across {sites} dive sites")

    areas = parse_indexed_boxes(src["restrictedareas.c"])
    print(f"  restrictedareas: {len(areas)} areas, "
          f"{sum(len(a['boxes']) for a in areas)} boxes")

    places = parse_stranger_places(src["re_arrests.c"])
    # Self-consistent invariant instead of a magic number: every func_118 CALL SITE must
    # have been parsed. Counting call sites beats hard-coding 63, which is itself derived
    # from this same file and silently wrong if Rockstar adds a stranger. (Note the naive
    # grep count is one higher — it also matches the function's own definition.)
    call_sites = len(re.findall(r'func_118\(\s*\w+,\s*"', src["re_arrests.c"]))
    if len(places) != call_sites:
        sys.exit(f"FATAL strangers: parsed {len(places)} of {call_sites} func_118 call "
                 f"sites. The signature changed — fix the parser rather than shipping a "
                 f"partial activity directory.")
    print(f"  re_arrests (ambient-flow library): {len(places)} stranger trigger points")

    interactions = []
    for name in SCRIPTS:
        if name.startswith("ob_"):
            interactions += parse_interactions(name, src[name])
    print(f"  ob_* family: {len(interactions)} interactable models")

    random_events = parse_random_events(src["re_arrests.c"])
    print(f"  random-event registry: {len(random_events)} launchable events "
          f"(ids {random_events[0]['id']}..{random_events[-1]['id']})")

    # ---- overrides merge (LAST, so hand fixes survive regeneration) ----
    overrides = {}
    if os.path.exists(OVERRIDES):
        with open(OVERRIDES, encoding="utf-8") as fh:
            overrides = json.load(fh)

    n_over = 0
    for sec in ("collectibles", "areas", "places", "interactions"):
        rows = {"collectibles": collectibles, "areas": areas,
                "places": places, "interactions": interactions}[sec]
        for o in overrides.get(sec, []):
            if o.get("status") == "refuted":
                key = o.get("id") or o.get("model")
                before = len(rows)
                rows[:] = [r for r in rows
                           if (r.get("id") or r.get("model")) != key]
                n_over += before - len(rows)
                continue
            match = False
            for r in rows:
                if all(r.get(k) == v for k, v in o.get("match", {}).items()):
                    r.update(o.get("set", {}))
                    r["status"] = o.get("status", "verified")
                    r["evidence"] = o.get("evidence")
                    match, n_over = True, n_over + 1
            if not match and o.get("append"):
                rows.append(dict(o["append"], status=o.get("status", "assumed"),
                                 evidence=o.get("evidence")))
                n_over += 1
    print(f"overrides applied: {n_over} rows")

    out = {
        "version": 1,
        "generated": {
            "by": "build-worlddata.py",
            "utc": datetime.datetime.now(datetime.timezone.utc)
                   .strftime("%Y-%m-%dT%H:%M:%SZ"),
            "corpus": "calamity-inc/GTA-V-Decompiled-Scripts@senpai",
            "sources": {n: src_meta(n) for n in list(SCRIPTS) + list(LOCAL_SOURCES)},
        },
        "collectibles": collectibles,
        "areas": areas,
        "places": places,
        "interactions": interactions,
        "randomEvents": random_events,
        "dispatch": DISPATCH,
    }

    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh, separators=(",", ":"))
    size = os.path.getsize(OUT)
    print(f"wrote {os.path.normpath(OUT)} ({size / 1024:.0f} KB)")

    # Envelope sanity check. A coordinate outside the map means the parser latched onto
    # something that was never a position, and a blind player would be routed into the
    # void — so this is a hard failure, not a warning.
    bad = [c for c in collectibles
           if abs(c["x"]) > 5000 or abs(c["y"]) > 9000 or c["z"] < -200 or c["z"] > 2000]
    if bad:
        sys.exit(f"FATAL {len(bad)} collectible coordinates fall outside the map "
                 f"envelope; first offender: {bad[0]}")
    print("envelope check: all coordinates inside the map")


if __name__ == "__main__":
    main()
