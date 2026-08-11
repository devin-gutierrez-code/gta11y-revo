#!/usr/bin/env python3
"""Build GTA/scripts/gta11y-menulabels.json (schema v4) for the Menu Reader.

Reference sources (fetched once into tools/_cache/):
  pausemenu.xml     citizenfx/fivem mirror of the game's frontend data
  commands_hud.lua  ImBaphomettt/lua-gtav-enums (MENU_UNIQUE_ID_* numbers)
  eMenuPref.hpp     calamity-inc/Stand-OSS (eMenuPref enum -> PREF_* numbers)
Hand overrides: tools/menulabels-overrides.json (merged LAST; survives regen).

ROW JOIN MODEL (v1.4, runtime-validated 2026-07-18/07-20):
  - kind=pref rows: runtime uniqueId == eMenuPref enum VALUE of the row's
    <MenuPref>, SHIFTED for this game build (see runtime_uid_and_conf). The
    cached eMenuPref.hpp is build 2944 (static_assert PREF_CURRENT_LANGUAGE==35);
    Devin's newer Legacy build inserted exactly 4 prefs, and the 2026-07-20
    calibration walk (menulog-2026-07-20-134947 pane-chain events) pinned WHERE:
      header <= 96  -> +0   pane 137 replays the real Graphics pane exactly at
                            offset 0 (DXVersion, Screen Type, Resolution, Aspect,
                            Refresh, Monitor, FXAA, MSAA, ...); pane 138 gives
                            Long/Ultra Shadows, HD Flight, Max LOD, Shadow Dist,
                            Frame Scaling; pane 24 confirms headers 47 and 48.
      header >= 108 -> +4   pane 140 is FORCED: uids 179/120/144/145/147 have no
                            offset-0 candidate, and all 20 rows then match XML
                            order exactly. Pane 22 confirms 169-171.
      header 97..107 (the PREF_VOICE_* block) -> the 4 insertions live in here;
                            still UNKNOWN -> provisional +4 + keyConfidence=
                            unpinned (spoken with a cue). Pane 139 fits both
                            offsets, so only a targeted session can pin it.
    v1.3 used a provisional +4 across 47..120; that mis-keyed every Graphics /
    Advanced Graphics / Display row by four header slots (uid 79 "FXAA" spoke
    "Grass Quality", uid 84 "Anisotropic Filtering" spoke "MSAA").
    tools/align-panes.py + a calibration sweep pin the rest.
  - trigger/hashed rows: runtime d1 is the row's joaat MenuUniqueIdHash
    (labeled via the menus table); its d2 is the VISIBLE POSITION -> ignored.
  - The old uid==xmlOrder join is DEAD (corrupt regex + refuted by logs).

Parsing uses xml.etree.ElementTree — the old flat regex could not match
'<Item platform="...">' openings and mis-indexed 120/151 rows.
"""
import datetime
import hashlib
import json
import os
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "_cache")
OUT = os.path.join(HERE, "..", "GTA", "scripts", "gta11y-menulabels.json")
OVERRIDES = os.path.join(HERE, "menulabels-overrides.json")

SOURCES = {
    "pausemenu.xml": "https://raw.githubusercontent.com/citizenfx/fivem/master/data/client/citizen/common/data/ui/pausemenu.xml",
    "commands_hud.lua": "https://raw.githubusercontent.com/ImBaphomettt/lua-gtav-enums/main/commands_hud.lua",
    "eMenuPref.hpp": "https://raw.githubusercontent.com/calamity-inc/Stand-OSS/master/Stand/eMenuPref.hpp",
}

# PC Legacy SP tab order (GTAVFrontendMenu rebuild + wrap arithmetic).
TABS = [
    {"index": 0, "gxt": "PM_SCR_MAP", "label": "Map", "leakMenuId": None},
    {"index": 1, "gxt": "PM_SCR_BRF", "label": "Brief", "leakMenuId": 15},
    {"index": 2, "gxt": "PM_SCR_STA", "label": "Stats", "leakMenuId": None},
    {"index": 3, "gxt": "PM_SCR_SET", "label": "Settings", "leakMenuId": 24},
    {"index": 4, "gxt": "PM_SCR_GAM", "label": "Game", "leakMenuId": 75},
    {"index": 5, "gxt": "PM_SCR_ONL", "label": "Online", "leakMenuId": None},
    {"index": 6, "gxt": "PM_SCR_FRI", "label": "Friends", "leakMenuId": None},
    {"index": 7, "gxt": "PM_SCR_GAL", "label": "Gallery", "leakMenuId": None},
    {"index": 8, "gxt": "PM_SCR_STO", "label": "Store", "leakMenuId": None},
    {"index": 9, "gxt": "PM_SCR_RPL", "label": "Rockstar Editor", "leakMenuId": None},
]

# SP phone home grid (decompiled cellphone_flashhand sub_5c67). v1.2: the
# CELL_32/CELL_16 keys for slots 3/5 were paired BACKWARDS in v1.1 — runtime
# GXT resolution proved CELL_16->'Settings' and CELL_32->'Quick Save'.
# Quick Save's script is unconfirmed (both decomp slots said appsettings);
# null until an activation ground-truths it.
PHONE_HOME_SLOTS = [
    {"slot": 0, "gxt": "CELL_5", "label": "Email", "script": "appemail",
     "overrides": [{"gxt": "CELL_25", "script": "appcontacts",
                    "note": "mission-context override (flag _f58)"}]},
    {"slot": 1, "gxt": "CELL_1", "label": "Texts", "script": "apptextmessage"},
    {"slot": 2, "gxt": "CELL_23", "label": "Checklist", "script": "appchecklist"},
    {"slot": 3, "gxt": "CELL_16", "label": "Settings", "script": "appsettings"},
    {"slot": 4, "gxt": "CELL_0", "label": "Contacts", "script": "appcontacts"},
    {"slot": 5, "gxt": "CELL_32", "label": "Quick Save", "script": None,
     "note": "script unconfirmed; decomp listed appsettings for both 3 and 5"},
    {"slot": 6, "gxt": "CELL_7", "label": "Snapmatic", "script": "appcamera"},
    {"slot": 7, "gxt": "CELL_2", "label": "Internet", "script": "appinternet"},
    {"slot": 8, "gxt": "CELL_28", "label": "Trackify", "script": "apptrackify",
     "note": "mission-only slot (flag _f59)"},
]

PHONE_APPS = {
    "appcontacts": "Contacts", "appcamera": "Camera", "appsettings": "Phone Settings",
    "appemail": "Email", "appchecklist": "Checklist", "apptext": "Messages",
    "appsms": "Messages", "apptextmessage": "Messages", "appinternet": "Internet",
    "appmedia": "Media", "apporganiser": "Organizer", "apptrackify": "Trackify",
    "appsidetask": "Side Task", "appzit": "Zit",
}

OBSERVED_HASHED = {-925456543, -1265285960, -1418582884}
HASHED_LABELS = {
    "UR_QUICKSCAN": "Quick Scan for Music",
    "UR_COMPLETESCAN": "Complete Scan for Music",
    "LINK_FACEBOOK": "Facebook",
    "PREF_PCGAMEPAD": "Gamepad Type",
    "PREF_SAFEZONE_SIZE": "Safezone Size",
}
# Screens with no GXT that must never be spoken (runtime transition artifacts).
INTERNAL_IDS = {"MENU_UNIQUE_ID_INCEPT_TRIGGER", "MENU_UNIQUE_ID_INVALID"}

# --- eMenuPref runtime-shift model (build > 2944) --------------------------
# See the module docstring. header<=96 -> +0 and header>=108 -> +4 are both
# SETTLED by the 2026-07-20 pane-chain calibration; only the PREF_VOICE_* block
# (97..107) still rides a provisional +4 with keyConfidence=unpinned.
SHIFT = 4
SHIFT_BOUNDARY = 108
UNPINNED_LO, UNPINNED_HI = 97, 107
# Runtime uids actually observed across all menulog sessions. A row whose runtime
# uid lands in this set is keyConfidence=confirmed. The second and third blocks
# are the 2026-07-20 calibration walk (panes 22/24/137/138/140; pane 139's
# 101-106 are deliberately EXCLUDED because their offset is still ambiguous).
OBSERVED_RUNTIME_UIDS = {
    8, 9, 11, 13, 17, 30, 31, 32, 33, 34, 43, 45, 46,
    125, 126, 133, 134, 135, 136, 137, 138, 139, 173, 174, 175, 180,
    # pane 24 (Controls) + pane 22 (Audio)
    0, 1, 3, 4, 5, 6, 47, 48, 127, 128, 129, 130, 131, 132, 140, 170, 171,
    # pane 137 (Graphics)
    56, 57, 58, 60, 61, 62, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76,
    77, 78, 79, 80, 82, 83, 84, 85, 86, 88, 89, 96,
    # pane 138 (Advanced Graphics)
    81, 90, 91, 92, 93, 94,
    # pane 140 (Keyboard / Mouse)
    112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123,
    141, 142, 143, 144, 145, 146, 147, 179,
}


def runtime_uid_and_conf(header_val):
    """Map a build-2944 eMenuPref value to this build's runtime uniqueId + a
    key-confidence tag. Returns (None, None) for prefs unknown to the enum.
    The +4 for the 97..107 voice band is a provisional, order-preserving,
    collision-free guess (runtime range 101..111, disjoint from the settled
    0..96 and 112..180 bands)."""
    if header_val is None:
        return None, None
    if header_val <= UNPINNED_LO - 1:
        ru = header_val
    elif header_val >= SHIFT_BOUNDARY:
        ru = header_val + SHIFT
    else:  # 97..107 unpinned voice band
        return header_val + SHIFT, "unpinned"
    return ru, ("confirmed" if ru in OBSERVED_RUNTIME_UIDS else "derived")


def src_meta(name):
    path = os.path.join(CACHE, name)
    data = open(path, "rb").read()
    return {
        "sha256": hashlib.sha256(data).hexdigest()[:12],
        "mtime": datetime.datetime.fromtimestamp(os.path.getmtime(path),
                 datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "bytes": len(data),
    }


def joaat(s):
    h = 0
    for c in s.lower():
        h = (h + ord(c)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h ^= h >> 6
    h = (h + (h << 3)) & 0xFFFFFFFF
    h ^= h >> 11
    h = (h + (h << 15)) & 0xFFFFFFFF
    return h - 0x100000000 if h >= 0x80000000 else h


def fetch(name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    if not os.path.exists(path):
        print("fetching", name)
        urllib.request.urlretrieve(SOURCES[name], path)
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def parse_pref_enum(hpp):
    prefs = {}
    val = 0
    for m in re.finditer(r"^\s*(PREF_\w+)(?:\s*=\s*(-?\d+))?\s*,", hpp, re.M):
        name, explicit = m.group(1), m.group(2)
        if explicit is not None:
            val = int(explicit)
        prefs[name] = val
        val += 1
    return prefs


def kind_of(action):
    if action is None:
        return "unknown"
    if "SEPARATOR" in action:
        return "separator"
    if "PREF_CHANGE" in action:
        return "pref"
    if "LINK" in action:
        return "link"
    if "TRIGGER" in action:
        return "trigger"
    if "FILL_CONTENT" in action:
        return "fillContent"
    if "INCEPT" in action:
        return "incept"
    return "other"


def text_of(elem, tag):
    child = elem.find(tag)
    return child.text.strip() if child is not None and child.text else None


def main():
    lua = fetch("commands_hud.lua")
    xml_text = fetch("pausemenu.xml")
    hpp = fetch("eMenuPref.hpp")

    enum = {}
    for name, val in re.findall(r"(MENU_UNIQUE_ID_\w+)\s*=\s*(-?\d+)", lua):
        enum[name] = int(val)
    if enum.get("MENU_UNIQUE_ID_SETTINGS_LIST") != 51:
        sys.exit("enum sanity failed: SETTINGS_LIST != 51")

    prefs = parse_pref_enum(hpp)
    for name, want in [("PREF_SFX_VOLUME", 8), ("PREF_MUSIC_VOLUME", 9),
                       ("PREF_SPEAKER_OUTPUT", 11), ("PREF_DIAG_BOOST", 43)]:
        if prefs.get(name) != want:
            sys.exit(f"pref enum sanity failed: {name}={prefs.get(name)} want {want}")
    # Shift-model sanity: every observed runtime uid must de-shift to a real pref
    # (uid<=96 -> header uid ; uid>=108 -> header uid-4). Fails the build loudly
    # if a future enum source breaks the s(<=96)=0 / s(>=108)=+4 anchors.
    val_to_name = {v: k for k, v in prefs.items()}
    for u in OBSERVED_RUNTIME_UIDS:
        hv = u if u <= UNPINNED_LO - 1 else u - SHIFT
        if hv not in val_to_name:
            sys.exit(f"shift-model sanity failed: runtime uid {u} -> header {hv} not in enum")
    # Anchor spot-checks straight from the 2026-07-20 calibration: these three
    # were the loudest v1.3 lies and must now resolve to the right pref.
    for u, want in [(79, "PREF_GFX_FXAA"), (83, "PREF_GFX_ANISOTROPIC_FILTERING"),
                    (84, "PREF_GFX_AMBIENT_OCCLUSION"), (89, "PREF_GFX_DIST_SCALE"),
                    (96, "PREF_GFX_VID_OVERRIDE"), (112, "PREF_MOUSE_TYPE"),
                    (175, "PREF_AUDIO_MUTE_ON_FOCUS_LOSS")]:
        hv = u if u <= UNPINNED_LO - 1 else u - SHIFT
        if val_to_name.get(hv) != want:
            sys.exit(f"shift-model anchor failed: uid {u} -> {val_to_name.get(hv)}, want {want}")

    root = ET.fromstring(xml_text)

    # ---- walk MenuScreens with containment ----
    link_labels = {}      # MENU_UNIQUE_ID_* / hash-name -> best cTextId
    rows = []             # settings rows (kind=pref keyed by RUNTIME uid)
    screen_items = []     # hashed-trigger / non-list items on settings panes
    seen_pref_uids = set()
    screen_ord = {}       # snum -> next pref-row ordinal within that pane
    dropped_dups = []     # (pref, runtimeUid, snum) dropped as context variants

    def note_link(name, text):
        if not (name and text):
            return
        if name not in link_labels:
            link_labels[name] = text
        elif text.startswith("PM_") and not link_labels[name].startswith("PM_"):
            link_labels[name] = text

    # Actual hierarchy: CMenuArray/MenuScreens/Item, each with a <MenuScreen>
    # NAME leaf (enum name, or a raw hash-name for hashed screens) plus a
    # <MenuItems> list holding the rows.
    menuscreens = root.find("MenuScreens")
    for wrap in (menuscreens if menuscreens is not None else []):
        sname = text_of(wrap, "MenuScreen")
        if sname in enum:
            snum = enum[sname]
        elif sname:
            snum = joaat(sname)
        else:
            snum = None
        menuitems = wrap.find("MenuItems")
        if menuitems is None:
            continue
        for item in menuitems.findall("Item"):
            ct = text_of(item, "cTextId")
            action = text_of(item, "MenuAction")
            pref = text_of(item, "MenuPref")
            option = text_of(item, "MenuOption")
            link = text_of(item, "MenuUniqueId")
            linkh = text_of(item, "MenuUniqueIdHash")
            ctx = text_of(item, "Contexts")
            platform = item.get("platform")
            kind = kind_of(action)
            note_link(link, ct)
            note_link(linkh, ct)

            if link == "MENU_UNIQUE_ID_SETTINGS_LIST" and kind == "pref" and pref:
                header_val = prefs.get(pref)
                ru, conf = runtime_uid_and_conf(header_val)
                if ru is not None and ru in seen_pref_uids:
                    # duplicate runtime uid (context variants of the same row,
                    # e.g. PREF_CONTROLLER_LIGHT_EFFECT on panes 24 and 140) —
                    # keep first, record the drop so it is not silent.
                    dropped_dups.append((pref, ru, snum))
                    continue
                if ru is not None:
                    seen_pref_uids.add(ru)
                ordv = screen_ord.get(snum, 0)
                screen_ord[snum] = ordv + 1
                rows.append({
                    "menuId": 51,
                    "uniqueId": ru,            # RUNTIME uid (shift-corrected)
                    "headerValue": header_val, # build-2944 enum value (align tool)
                    "screenMenuId": snum,
                    "kind": kind,
                    "optionType": option,
                    "gxt": ct,
                    "pref": pref,
                    "contexts": ctx,
                    "platform": platform,
                    "ord": ordv,               # 0-based pref-row index within pane
                    "conditional": bool(ctx is not None or platform is not None),
                    "basis": "prefValue",
                    "status": "assumed",
                    "keyConfidence": conf,     # confirmed | derived | unpinned
                    "label": None,
                })
            elif linkh:
                is_rdf = linkh.startswith("RESTORE_DEFAULTS")
                screen_items.append({
                    "menuId": joaat(linkh),
                    "hashName": linkh,
                    "screenMenuId": snum,
                    "kind": "hashedTrigger",
                    # RDF triggers all share gxt MO_RDF -> they spoke one identical
                    # string. Drop the shared gxt so the per-pane label wins.
                    "gxt": None if is_rdf else ct,
                    "label": (linkh.replace("_", " ").title() if is_rdf
                              else HASHED_LABELS.get(linkh)),
                })

    # SETTINGS_LIST must never carry a misleading screen label.
    link_labels["MENU_UNIQUE_ID_SETTINGS_LIST"] = None

    # ---- menus table ----
    menus = []
    for name, num in sorted(enum.items(), key=lambda kv: kv[1]):
        if name == "MENU_UNIQUE_ID_START":
            continue
        label = name.replace("MENU_UNIQUE_ID_", "").replace("_", " ").title()
        kind = "internal" if name in INTERNAL_IDS or not link_labels.get(name) and "HEADER" in name else "screen"
        if name in INTERNAL_IDS:
            kind, label = "internal", None
        if name == "MENU_UNIQUE_ID_SETTINGS_LIST":
            label = None
        menus.append({
            "menuId": num, "uniqueName": name, "kind": kind,
            "gxt": link_labels.get(name), "label": label,
        })
    # transition sentinel seen at runtime
    menus.append({"menuId": -1000, "uniqueName": "SENTINEL_MINUS_1000",
                  "kind": "internal", "gxt": None, "label": None})
    hashed_names = sorted({s["hashName"] for s in screen_items})
    matched = sum(1 for hn in hashed_names if joaat(hn) in OBSERVED_HASHED)
    for hn in hashed_names:
        is_rdf = hn.startswith("RESTORE_DEFAULTS")
        menus.append({
            "menuId": joaat(hn), "uniqueName": "HASH:" + hn, "kind": "hashedTrigger",
            "gxt": None if is_rdf else link_labels.get(hn),
            "label": HASHED_LABELS.get(hn, hn.replace("_", " ").title()),
        })
    print(f"hashed screens: {len(hashed_names)}, matched observed: {matched}/3")
    if dropped_dups:
        print(f"dropped {len(dropped_dups)} duplicate-uid rows (context variants):")
        for pref, ru, snum in dropped_dups:
            print(f"    {pref} uid={ru} pane={snum}")

    # ---- optionValues from the DisplayValues block ----
    option_values = {}
    for dv in root.iter("DisplayValues"):
        for entry in dv:
            opt = text_of(entry, "MenuOption")
            if not opt:
                continue
            vals = [c.text.strip() for c in entry.iter("cTextId") if c.text]
            if vals:
                option_values[opt] = vals
    # Recover option lists R* left inside XML comments ("filled out by code" —
    # e.g. MENU_OPTION_DISPLAY_SPEAKER_OUTPUT). ElementTree discards comments, so
    # the reader spoke raw ints ("Output, 0"). Parse each comment as a fragment;
    # values here are the game's own fallback order (calibration may refine it).
    recovered = 0
    for body in re.findall(r"<!--(.*?)-->", xml_text, re.S):
        try:
            frag = ET.fromstring("<c>" + body + "</c>")
        except ET.ParseError:
            continue
        for entry in frag.iter():
            opt = text_of(entry, "MenuOption")
            if not opt or opt in option_values:
                continue
            vals = [c.text.strip() for c in entry.iter("cTextId") if c.text]
            if vals:
                option_values[opt] = vals
                recovered += 1
    print(f"optionValues: {len(option_values)} option types ({recovered} recovered from comments)")

    # ---- overrides merge (LAST) ----
    overrides = {}
    if os.path.exists(OVERRIDES):
        with open(OVERRIDES, encoding="utf-8") as fh:
            overrides = json.load(fh)
    n_over = 0
    by_key = {(r["menuId"], r["uniqueId"]): r for r in rows if r["uniqueId"] is not None}
    for o in overrides.get("rows", []):
        key = (o["menuId"], o["uniqueId"])
        if key in by_key:
            by_key[key].update({k: v for k, v in o.items() if k not in ("menuId", "uniqueId", "evidence")})
            n_over += 1
        else:
            entry = dict(o)
            entry.setdefault("basis", "override")
            entry.setdefault("status", "verified")
            rows.append(entry)
            n_over += 1
    # optionValues override channel (A4): pin value orders confirmed in-game
    # (e.g. Speaker Output) so they survive regeneration.
    n_ov_opt = 0
    for opt, vals in overrides.get("optionValues", {}).items():
        if opt.startswith("_"):
            continue  # comment/metadata keys, not option types
        option_values[opt] = vals
        n_ov_opt += 1
    print(f"overrides applied: {n_over} rows, "
          f"{len(overrides.get('profilePrefs', {}))} profilePrefs, {n_ov_opt} optionValues")

    valid_rows = [r for r in rows if r.get("uniqueId") is not None]
    print(f"pref rows: {len(valid_rows)} (dropped {len(rows) - len(valid_rows)} with unknown pref)")
    conf_hist = {}
    for r in valid_rows:
        conf_hist[r.get("keyConfidence")] = conf_hist.get(r.get("keyConfidence"), 0) + 1
    print(f"key confidence: {conf_hist}")

    # v4/v5 data channels that are pure hand-maintained overrides (no XML source):
    #   settingsXmlMap - PREF_* -> settings.xml section/key + value decoding mode
    #   phoneAppRows   - phone app script -> ordered in-app row labels
    #   phoneGlobals   - pinned script-global offsets for PhoneGlobals/PhoneRowText
    #   paneRowCount   - v5: observed visible row count per settings pane, for the
    #                    reader's "2 of 17" cue. Written by align-panes.py --write
    #                    from real traversals; outranks the reader's own count of
    #                    unconditional rows, because a row that pausemenu.xml marks
    #                    conditional may simply not be on screen on this build.
    # Each is optional; the reader fails closed when a table is absent.
    settings_xml_map = {k: v for k, v in overrides.get("settingsXmlMap", {}).items()
                        if not k.startswith("_")}
    phone_app_rows = {k: v for k, v in overrides.get("phoneAppRows", {}).items()
                      if not k.startswith("_")}
    phone_globals = {k: v for k, v in overrides.get("phoneGlobals", {}).items()
                     if not k.startswith("_")}
    pane_row_count = {k: v for k, v in overrides.get("paneRowCount", {}).items()
                      if not k.startswith("_")}
    print(f"v5 tables: settingsXmlMap={len(settings_xml_map)} prefs, "
          f"phoneAppRows={len(phone_app_rows)} apps, phoneGlobals={len(phone_globals)} keys, "
          f"paneRowCount={len(pane_row_count)} panes")

    out = {
        "version": 5,
        "generated": {
            "by": "build-menulabels.py",
            "sources": {n: src_meta(n) for n in SOURCES},
        },
        "tabCount": 10,
        "tabs": TABS,
        "menus": menus,
        "rows": valid_rows,
        "screenItems": screen_items,
        "optionValues": option_values,
        "profilePrefs": overrides.get("profilePrefs", {}),
        "phoneHome": {
            "cellphone_ifruit": PHONE_HOME_SLOTS,
            "cellphone_badger": PHONE_HOME_SLOTS,
            "cellphone_facade": PHONE_HOME_SLOTS,
        },
        "phoneApps": PHONE_APPS,
        "phoneAppRows": phone_app_rows,
        "phoneGlobals": phone_globals,
        "paneRowCount": pane_row_count,
        "settingsXmlMap": settings_xml_map,
    }
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=1)
    print(f"wrote {os.path.normpath(OUT)} ({os.path.getsize(OUT)} bytes, "
          f"{len(menus)} menus, {len(valid_rows)} rows)")


if __name__ == "__main__":
    main()
