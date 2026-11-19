#!/usr/bin/env python3
"""Promote runtime-learned PREF_* -> profile-setting-id bindings into the shipped
label data.

Why this exists
---------------
110 of the 149 settings rows the menu reader can NAME had no way to say what they
were SET TO, because nothing mapped a row's pref to a profile-setting id. No
offline source carries that mapping and the ids are build-specific, so the reader
LEARNS it: press Left/Right on a row, watch which single profile id moves, bind.

Those learnings live in a user file (ModSettings/gta11y-menubind.json) and are
also logged as `EVENT reader-bind` lines. This script folds both into
tools/menulabels-overrides.json so the mapping ships with the mod instead of
having to be re-learned by every user on every fresh profile.

It also reports what the reader could NOT settle:

  * `reader-bind-ambiguous` — more than one id moved inside the attribution
    window, so the reader deliberately bound nothing. Re-walk that row on its own
    and the next session usually resolves it.
  * `settingsxml-unmapped`  — rows that still have no value source at all. This
    is the worklist: it should shrink session over session.

Usage
-----
    python tools/bind-profile-ids.py                 # newest menulog, report only
    python tools/bind-profile-ids.py LOG [LOG ...]   # specific logs
    python tools/bind-profile-ids.py --write         # merge into the overrides

Rerun tools/build-menulabels.py afterwards, then redeploy the JSON. No rebuild
is needed — this is data.
"""

import glob
import json
import os
import re
import sys
from collections import Counter, OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
OVERRIDES = os.path.join(HERE, "menulabels-overrides.json")
LABELS = os.path.join(HERE, os.pardir, "GTA", "scripts", "gta11y-menulabels.json")
MODSETTINGS = os.path.join(
    os.path.expanduser("~"), "Documents", "Rockstar Games", "GTA V", "ModSettings")
BINDFILE = os.path.join(MODSETTINGS, "gta11y-menubind.json")

RE_BIND = re.compile(
    r"EVENT reader-bind: pref=(\S+) id=(\d+) new=(-?\d+) pane=(-?\d+)")
RE_AMBIG = re.compile(
    r"EVENT reader-bind-ambiguous: pref=(\S+) pane=(-?\d+) ids=(\S+)")
RE_UNMAPPED = re.compile(
    r"EVENT settingsxml-unmapped: pref=(\S+) optionType=(\S+) pane=(-?\d+)")


def read_logs(paths):
    binds, ambig, unmapped = {}, {}, Counter()
    for p in paths:
        with open(p, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "reader-bind" not in line and "settingsxml-unmapped" not in line:
                    continue
                m = RE_BIND.search(line)
                if m:
                    pref, sid, val, pane = m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4))
                    # A pref seen bound to two different ids across a session is
                    # the reader having attributed a change wrongly once. Keep
                    # every observation so the disagreement is reportable rather
                    # than silently last-wins.
                    binds.setdefault(pref, Counter())[sid] += 1
                    continue
                m = RE_AMBIG.search(line)
                if m:
                    ambig.setdefault(m.group(1), Counter())[m.group(3)] += 1
                    continue
                m = RE_UNMAPPED.search(line)
                if m:
                    unmapped[(m.group(1), m.group(2), int(m.group(3)))] += 1
    return binds, ambig, unmapped


def read_bindfile():
    """The reader's own persisted map. Same evidence as the log lines, but it
    survives a session whose log was not kept."""
    try:
        with open(BINDFILE, encoding="utf-8") as fh:
            return (json.load(fh) or {}).get("bindings") or {}
    except (OSError, ValueError):
        return {}


def load_shipped():
    """pref -> profile id already pinned in the generated labels, so the report
    only ever asks for work that is actually outstanding."""
    try:
        with open(LABELS, encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return {}, {}
    shipped = {}
    for sid, entry in (data.get("profilePrefs") or {}).items():
        pref = (entry or {}).get("pref")
        if pref:
            shipped[pref] = int(sid)
    opt = {r.get("pref"): r.get("optionType")
           for r in (data.get("rows") or []) if r.get("pref")}
    return shipped, opt


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    write = "--write" in sys.argv
    logs = args or sorted(glob.glob(os.path.join(MODSETTINGS, "menulog-*.log")))[-1:]
    if not logs:
        sys.exit("no menulog found; pass a path")
    print("reading:", ", ".join(os.path.basename(x) for x in logs))

    binds, ambig, unmapped = read_logs(logs)
    persisted = read_bindfile()
    shipped, opt_by_pref = load_shipped()
    print(f"shipped profilePrefs with a pref name: {len(shipped)}")
    print(f"persisted bindings in {os.path.basename(BINDFILE)}: {len(persisted)}")

    # Fold the persisted file in as one more observation per pref. The log is the
    # richer source (it carries panes and disagreements), the file is the durable
    # one; a pref in either is a pref we know.
    for pref, sid in persisted.items():
        binds.setdefault(pref, Counter())[int(sid)] += 1

    settled, conflicted = OrderedDict(), OrderedDict()
    for pref in sorted(binds):
        seen = binds[pref]
        if len(seen) == 1:
            settled[pref] = next(iter(seen))
        else:
            conflicted[pref] = dict(seen)

    print(f"\n=== {len(settled)} settled bindings ===")
    new = 0
    for pref, sid in settled.items():
        mark = ""
        if pref in shipped:
            mark = "  (already shipped)" if shipped[pref] == sid else \
                   f"  !! DISAGREES with shipped id {shipped[pref]}"
        else:
            new += 1
        print(f"  {pref:<44} id={sid}{mark}")
    print(f"  {new} of these are new")

    if conflicted:
        print(f"\n=== {len(conflicted)} CONFLICTED — bound to more than one id ===")
        print("  Re-walk each of these alone; the reader attributed at least one")
        print("  change to the wrong row. Nothing is written for them.")
        for pref, seen in conflicted.items():
            print(f"  {pref:<44} {seen}")

    if ambig:
        print(f"\n=== {len(ambig)} ambiguous (several ids moved at once) ===")
        for pref, seen in sorted(ambig.items()):
            best = seen.most_common(3)
            print(f"  {pref:<44} {best}")

    if unmapped:
        print(f"\n=== {len(unmapped)} rows still with NO value source ===")
        print("  This is the worklist. Press Left/Right once on each and rerun.")
        for (pref, otype, pane), n in sorted(unmapped.items(), key=lambda kv: -kv[1]):
            print(f"  pane {pane:>4}  {pref:<40} {otype}  (x{n})")

    if not write:
        print("\nrerun with --write to merge the settled bindings into "
              f"{os.path.basename(OVERRIDES)}")
        return

    with open(OVERRIDES, encoding="utf-8") as fh:
        ov = json.load(fh, object_pairs_hook=OrderedDict)
    pp = ov.setdefault("profilePrefs", OrderedDict())
    added = updated = 0
    for pref, sid in settled.items():
        # Never overwrite a shipped pin that disagrees: a hand-verified entry
        # outranks a runtime observation, and a disagreement is a bug to look at
        # rather than a value to average.
        if pref in shipped and shipped[pref] != sid:
            continue
        key = str(sid)
        entry = pp.get(key)
        if entry is None:
            pp[key] = OrderedDict([
                ("name", pref.replace("PREF_", "").replace("_", " ").title()),
                ("optionType", opt_by_pref.get(pref)),
                ("pref", pref),
            ])
            added += 1
        elif entry.get("pref") != pref:
            entry["pref"] = pref
            if not entry.get("optionType"):
                entry["optionType"] = opt_by_pref.get(pref)
            updated += 1

    with open(OVERRIDES, "w", encoding="utf-8") as fh:
        json.dump(ov, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"\n--write: {added} new profilePrefs, {updated} updated into "
          f"{os.path.basename(OVERRIDES)} (rerun build-menulabels.py)")


if __name__ == "__main__":
    main()
