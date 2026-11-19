#!/usr/bin/env python3
"""triage-menulog.py — pre-digestion of gta11y menu/phone probe logs.

The drive-assist side has had a triage pipeline for twenty iterations. The menu
side has had none: ~30 menulog-*.log files accumulate in ModSettings and get read
by hand, and tools/build-menulabels.py carries comments like "observed in
menulog-2026-08-11-182121" because that reading was done by eye.

This closes that. It turns a menulog into:

  triage/<log-stem>/digest.md     session overview, coverage table, health
  triage/<log-stem>/gaps.md       every row the reader could not name, ranked
  triage/<log-stem>/snaps/        screenshots joined to the gap that produced them
  tools/menulog-index.json        one trend row per session, KEYED BY READER VERSION

`gaps.md` is the point. It is the list build-menulabels.py needs, in the order it
should be worked, with the screenshot to look at beside each entry — replacing a
manual transcription step that has been the bottleneck on menu-label coverage.

VERSION SEGMENTATION: rows are keyed on the `menulog-version` banner. Reader v1.4
and v1.5 do not name the same things, so counting unmapped rows across a version
boundary makes an improvement look like a regression. The trend table only ever
compares like with like, and says so when it cannot.

Usage:
  python tools\\triage-menulog.py                # newest menulog
  python tools\\triage-menulog.py <log> [...]    # specific log(s)
  python tools\\triage-menulog.py --selftest
"""

import argparse
import json
import sys
from collections import Counter, OrderedDict
from datetime import datetime
from pathlib import Path

from logfmt import (kv, qkv, inum_of, fnum_of, fmt_t, fmt_dur, time_of,
                    parse_events, newest_log, collect_snapshots,
                    build_contact_sheet, capture_geometry_faults, REPO_ROOT)

# Events that mean "the reader had nothing correct to say". These are the product
# backlog, so they are named in one place rather than scattered through the
# reporting — a new gap kind added in C# only has to be added here.
#
# The value is (short label, what fixes it) — the second half is what turns a
# digest line into an action, and it is the question every one of these events
# used to leave unanswered.
GAP_KINDS = OrderedDict([
    ("reader-unmapped-phone", ("phone row unnamed",
                               "add to the phone row map (MenuLabelDb / PhoneListRows)")),
    ("settingsxml-unmapped",  ("settings pref unnamed",
                               "add a pref -> label mapping (tools/menulabels-overrides.json)")),
    ("reader-unmapped",       ("menu item unnamed",
                               "pin the uniqueId (tools/build-menulabels.py)")),
    ("reader-gxt-fail",       ("GXT lookup failed",
                               "the label key resolved to nothing - check the GXT key")),
    ("reader-position-nodata", ("pane has no row data",
                                "walk the pane fully, then tools/align-panes.py --write")),
    ("accept-noop",           ("row did nothing when activated",
                               "check whether the row is dead or the submenu failed to open")),
    ("reader-pane-mismatch",  ("pane disagreed with item",
                               "the pane<->item binding is wrong for this row")),
])

# Snapshot tags that are COVERAGE rather than a gap (see MSnap call sites). Ranked
# below gaps in the manifest: a picture of a pane that worked is useful for
# building label tables, but it is not a defect.
COVERAGE_TAGS = {"PANE-FIRST-VISIT"}


class Session:
    def __init__(self, path):
        self.path = path
        self.version = None          # menulog-version banner (reader1.5, ...)
        self.labeldb = None
        self.frames = 0
        self.t0 = self.t1 = None
        self.events = []
        self.gaps = []               # dicts: kind, f, key, ctx, rest
        self.rows = OrderedDict()    # (scope, a, b) -> row record
        self.latencies = []          # input -> speech, ms
        self.phone_latencies = []    # phone-row latencyMs=, straight from the log
        self.errors = Counter()      # probe-error by probe
        self.disabled = []           # probes that blew their 20-strike fuse
        self.flicker = 0
        self.captures = {}           # seq -> {tag, key, rest}
        self.speech = []             # (f, src, text)
        self.panes = OrderedDict()   # pane -> visits


def parse(path):
    """Single pass over the log. Menulogs are 3-140 KB, so unlike the drive logs
    there is no streaming requirement and no read-wholesale prohibition — but the
    digest still becomes the entry point, because a coverage table is a different
    artifact from a transcript."""
    s = Session(path)
    lines = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    s.events = parse_events(lines)

    # Running context. A gap event says what failed but not where the user was;
    # the surrounding state is what makes it actionable, so it is carried forward
    # from whichever event last established it.
    pane = menu = item = None
    phone_script = phone_row = None
    last_input = None            # (frame, ctrl, wall-clock t) awaiting a speech

    for line in lines:
        if line.startswith("[F") and " FRAME " in line:
            s.frames += 1
            t = time_of(line)
            if t is not None:
                if s.t0 is None:
                    s.t0 = t
                s.t1 = t

    for ev in s.events:
        k, rest, f = ev["kind"], ev["rest"], ev["f"]

        if k == "menulog-version":
            s.version = rest.strip() or None
        elif k == "labeldb-version":
            s.labeldb = rest.strip()
        elif k == "reader-pane":
            pane = inum_of(rest, "pane")
            if pane is not None:
                s.panes[pane] = s.panes.get(pane, 0) + 1
        elif k == "pause-sel":
            cur = kv(rest, "cur")
            if cur and "/" in cur:
                try:
                    menu, item = (int(x) for x in cur.split("/", 1))
                except ValueError:
                    pass
        elif k == "row-visit":
            menu = inum_of(rest, "menu")
            item = inum_of(rest, "item")
            key = ("menu", menu, item)
            r = s.rows.setdefault(key, {"scope": "menu", "a": menu, "b": item,
                                        "pane": pane, "visits": 0, "dwell": 0.0,
                                        "lr": 0, "text": None, "mapped": None})
            r["visits"] += 1
            r["dwell"] += (fnum_of(rest, "dwellMs") or 0.0)
            r["lr"] += (inum_of(rest, "lr") or 0)
            if r["pane"] is None:
                r["pane"] = pane
        elif k == "phone-app":
            on = kv(rest, "on")
            if on and on != "none":
                phone_script = on.split(":", 1)[0]
        elif k == "phone-row":
            phone_row = inum_of(rest, "row")
            ms = fnum_of(rest, "latencyMs")
            if ms is not None:
                s.phone_latencies.append(ms)
            key = ("phone", phone_script, phone_row)
            r = s.rows.setdefault(key, {"scope": "phone", "a": phone_script, "b": phone_row,
                                        "pane": None, "visits": 0, "dwell": 0.0,
                                        "lr": 0, "text": None, "mapped": None})
            r["visits"] += 1
        elif k == "input":
            last_input = (f, kv(rest, "ctrl"))
        elif k == "reader-speak":
            src = kv(rest, "src")
            text = qkv(rest, "text")
            s.speech.append((f, src, text))
            # Attribute the utterance to whatever row is focused, so the coverage
            # table can show what each row actually SAID rather than only that it
            # was visited.
            key = (("phone", phone_script, phone_row) if src == "phone"
                   else ("menu", menu, item))
            if key in s.rows:
                s.rows[key]["text"] = text
                s.rows[key]["mapped"] = True
            if last_input is not None:
                # Frames, not milliseconds: the menulog has no per-event wall clock
                # and the probe runs once per tick, so frame delta is the honest
                # unit. Converting it to ms would invent a frame rate.
                s.latencies.append(max(0, f - last_input[0]))
                last_input = None
        elif k in GAP_KINDS:
            gap_key = _gap_key(k, rest)
            s.gaps.append({"kind": k, "f": f, "key": gap_key, "rest": rest,
                           "pane": pane, "menu": menu, "item": item,
                           "script": phone_script, "row": phone_row})
            # Mark the row unmapped ONLY for gap kinds that actually identify a
            # row, and identify it from the EVENT rather than from ambient state.
            #
            # settingsxml-unmapped is keyed by pref and reader-position-nodata by
            # pane; neither names the focused row. Attributing them to whatever row
            # happened to be current marks working rows as broken — in the selftest
            # sample it flipped "Music Volume, 9 of 10", a row that had just been
            # spoken correctly, to unmapped.
            row_key = None
            if k in ("reader-unmapped", "reader-gxt-fail"):
                row_key = ("menu", inum_of(rest, "menu"), inum_of(rest, "item"))
            elif k == "reader-unmapped-phone":
                row_key = ("phone", kv(rest, "script") or phone_script,
                           inum_of(rest, "row"))
            if row_key is not None and row_key in s.rows:
                s.rows[row_key]["mapped"] = False
        elif k == "probe-error":
            s.errors[kv(rest, "probe") or "?"] += 1
        elif k == "probe-disabled":
            s.disabled.append(kv(rest, "probe") or "?")
        elif k == "reader-open-flicker":
            s.flicker += 1
        elif k == "capture":
            seq = inum_of(rest, "seq")
            if seq is not None:
                s.captures[seq] = {"tag": kv(rest, "tag"), "key": kv(rest, "key"),
                                   "rest": rest, "f": f}

    # A row that could not be named often never produced a coverage event either:
    # phone-row keys off the app script reported by phone-app, and an app the probe
    # did not recognise emits the gap without ever registering the row. Counting
    # only registered rows therefore reported "50 of 50 named (100%)" for a session
    # that had nine distinct gaps — a headline that contradicted its own gap list.
    # Fold every row-identifying gap back in, so the percentage is over everything
    # actually encountered.
    for g in s.gaps:
        rk = None
        if g["kind"] in ("reader-unmapped", "reader-gxt-fail"):
            rk = ("menu", inum_of(g["rest"], "menu"), inum_of(g["rest"], "item"))
        elif g["kind"] == "reader-unmapped-phone":
            rk = ("phone", kv(g["rest"], "script") or kv(g["rest"], "movie"),
                  inum_of(g["rest"], "row"))
        if rk is None or rk[1] is None or rk[2] is None:
            continue
        r = s.rows.setdefault(rk, {"scope": rk[0], "a": rk[1], "b": rk[2],
                                   "pane": g["pane"], "visits": 0, "dwell": 0.0,
                                   "lr": 0, "text": None, "mapped": None})
        r["mapped"] = False

    return s


def _gap_key(kind, rest):
    """The identity of the thing that failed — matches the MSnap dedup key on the
    C# side so a gap and its screenshot can be joined without a seq lookup."""
    if kind == "reader-unmapped-phone":
        if kv(rest, "script"):
            return "{}:{}".format(kv(rest, "script"), kv(rest, "row"))
        return "{}:{}".format(kv(rest, "movie"), kv(rest, "row"))
    if kind == "settingsxml-unmapped":
        return kv(rest, "pref") or "?"
    if kind == "reader-position-nodata":
        return kv(rest, "pane") or "?"
    if kind in ("reader-unmapped", "reader-gxt-fail"):
        return "{}:{}".format(kv(rest, "menu"), kv(rest, "item"))
    if kind == "reader-pane-mismatch":
        return kv(rest, "item") or "?"
    if kind == "accept-noop":
        # Pre-iter-54 logs emit this bare, with no fields at all, so every dead row
        # in the session collapses to one entry. Fall through to "-" for those
        # rather than inventing a distinction the log cannot support.
        m, i = kv(rest, "menu"), kv(rest, "item")
        return "{}:{}".format(m, i) if m and i else "-"
    return rest.strip()[:40] or "-"


def build_gaps_md(s, snap_by_key):
    """The actionable list, ranked by how often the user hit it.

    Frequency is the right ranking here and cost is not available: every gap costs
    the same thing — a row that says nothing — so the one worth fixing first is
    simply the one encountered most.
    """
    out = ["# Menu/phone coverage gaps — {}".format(Path(s.path).name), ""]
    out.append("Reader version: **{}**. {} gap event(s) across {} distinct item(s)."
               .format(s.version or "unknown", len(s.gaps),
                       len({(g["kind"], g["key"]) for g in s.gaps})))
    out.append("")
    if not s.gaps:
        out.append("_No gaps recorded. Either coverage is complete for everything visited,")
        out.append("or not much was visited — check the coverage table in digest.md before")
        out.append("reading this as success._")
        return "\n".join(out) + "\n"

    grouped = OrderedDict()
    for g in s.gaps:
        grouped.setdefault((g["kind"], g["key"]), []).append(g)
    ranked = sorted(grouped.items(), key=lambda kvp: -len(kvp[1]))

    for kind in GAP_KINDS:
        rows = [(k, v) for k, v in ranked if k[0] == kind]
        if not rows:
            continue
        label, fix = GAP_KINDS[kind]
        out.append("## {} — {}".format(kind, label))
        out.append("")
        out.append("_Fix: {}_".format(fix))
        out.append("")
        out.append("| item | hits | where | screenshot |")
        out.append("|------|------|-------|------------|")
        for (_, key), gs in rows:
            g = gs[0]
            where = []
            if g["pane"] is not None:
                where.append("pane {}".format(g["pane"]))
            if g["script"]:
                where.append("app {}".format(g["script"]))
            if g["menu"] is not None and kind not in ("reader-unmapped-phone",):
                where.append("menu {}/{}".format(g["menu"], g["item"]))
            shot = snap_by_key.get("{}|{}".format(_tag_for(kind), key))
            out.append("| `{}` | {} | {} | {} |".format(
                key, len(gs), ", ".join(where) or "-",
                "`snaps/{}`".format(shot) if shot else "-"))
        out.append("")

    if not snap_by_key:
        out.append("_No screenshots in this session. Turn on `menuSnapshotImage` (on by")
        out.append("default from iter-54) and run the game borderless windowed — the frame")
        out.append("at an unmapped row shows the label that the log can only refer to by hash._")
        out.append("")
    return "\n".join(out) + "\n"


def _tag_for(kind):
    """The MSnap tag paired with each gap kind, so gaps.md can find the image."""
    return {"reader-unmapped-phone": "PHONE-UNMAPPED-ROW",
            "settingsxml-unmapped": "SETTINGS-UNMAPPED",
            "reader-position-nodata": "PANE-NODATA",
            "accept-noop": "ACCEPT-NOOP"}.get(kind, kind.upper())


def build_digest(s, snaps, snap_by_key, index_rows):
    out = ["# Menulog digest — {}".format(Path(s.path).name), ""]
    out.append("Generated {:%Y-%m-%d %H:%M} by tools/triage-menulog.py; {} KB / {} events."
               .format(datetime.now(), Path(s.path).stat().st_size // 1024, len(s.events)))
    out.append("")

    # ---- lead ----
    out.append("## Read this first")
    out.append("")
    distinct = len({(g["kind"], g["key"]) for g in s.gaps})
    mapped = sum(1 for r in s.rows.values() if r["mapped"] is True)
    unmapped = sum(1 for r in s.rows.values() if r["mapped"] is False)
    known = mapped + unmapped
    out.append("- **reader {}**, labeldb `{}`".format(s.version or "unknown", s.labeldb or "?"))
    if known:
        out.append("- **{} of {} visited rows were named** ({:.0f}%); {} distinct gap(s) — see `gaps.md`"
                   .format(mapped, known, 100.0 * mapped / known, distinct))
    else:
        out.append("- **no rows visited** — nothing was walked, so this session says nothing "
                   "about coverage either way")
    if snaps:
        out.append("- **{} screenshot(s)** across {} capture point(s) — see `snaps/manifest.md`"
                   .format(sum(len(v) for v in snaps.values()), len(snaps)))
    else:
        out.append("- **no screenshots** (menuSnapshotImage off, exclusive fullscreen, or a "
                   "build predating iter-54)")
    health = []
    # What the frames CONTAIN, not just how many there are. A menu screenshot exists to
    # show the label that failed to map; one that is 40% desktop shows it smaller and
    # buys a reader-version comparison that is not like-for-like.
    health.extend(capture_geometry_faults(
        [e for e in s.events if e["kind"] == "capture-selftest"]))
    if s.disabled:
        health.append("**probe(s) DISABLED by the 20-strike fuse: {}**".format(", ".join(s.disabled)))
    if s.errors:
        health.append("probe errors " + ", ".join("{}x{}".format(p, n) for p, n in s.errors.items()))
    if s.flicker:
        health.append("{} open-flicker event(s)".format(s.flicker))
    if health:
        out.append("- **health:** " + "; ".join(health))
    out.append("")

    # ---- session ----
    out.append("## Session")
    out.append("- frames: {}".format(s.frames))
    if s.t0 is not None and s.t1 is not None:
        out.append("- span: {} -> {} ({})".format(fmt_t(s.t0), fmt_t(s.t1), fmt_dur(s.t1 - s.t0)))
    out.append("- panes visited: {}".format(
        ", ".join("{} (x{})".format(p, n) for p, n in s.panes.items()) or "none"))
    out.append("- utterances: {}".format(len(s.speech)))
    if s.latencies:
        lat = sorted(s.latencies)
        out.append("- input -> speech: median {} frame(s), p95 {} (n={})".format(
            lat[len(lat) // 2], lat[min(len(lat) - 1, int(len(lat) * 0.95))], len(lat)))
    if s.phone_latencies:
        pl = sorted(s.phone_latencies)
        out.append("- phone row latency (logged): median {:.0f} ms, worst {:.0f} ms (n={})".format(
            pl[len(pl) // 2], pl[-1], len(pl)))
    out.append("")

    # ---- coverage ----
    out.append("## Coverage (every row visited, and what it said)")
    out.append("")
    out.append("_`mapped` is the verdict for THIS session: yes = the reader spoke a real label, "
               "no = a gap event fired for it, blank = visited but never spoken (usually passed "
               "through too fast to trigger the reader). A blank is not a defect._")
    out.append("")
    if s.rows:
        out.append("| scope | id | pane | visits | dwell | mapped | spoken |")
        out.append("|-------|-----|------|--------|-------|--------|--------|")
        for r in s.rows.values():
            ident = ("{}/{}".format(r["a"], r["b"]) if r["scope"] == "menu"
                     else "{} row {}".format(r["a"] or "?", r["b"]))
            txt = (r["text"] or "")[:52]
            out.append("| {} | {} | {} | {} | {} | {} | {} |".format(
                r["scope"], ident, r["pane"] if r["pane"] is not None else "-",
                r["visits"],
                "{:.0f}ms".format(r["dwell"]) if r["dwell"] else "-",
                {True: "yes", False: "**no**"}.get(r["mapped"], "-"),
                '"{}"'.format(txt) if txt else "-"))
    else:
        out.append("_No rows visited._")
    out.append("")

    # ---- gaps summary ----
    out.append("## Gap summary (detail + fixes in gaps.md)")
    out.append("")
    counts = Counter(g["kind"] for g in s.gaps)
    if counts:
        out.append("| kind | events | distinct | means |")
        out.append("|------|--------|----------|-------|")
        for kind, n in counts.most_common():
            d = len({g["key"] for g in s.gaps if g["kind"] == kind})
            out.append("| {} | {} | {} | {} |".format(
                kind, n, d, GAP_KINDS.get(kind, ("?", ""))[0]))
    else:
        out.append("_None._")
    out.append("")

    # ---- speech histogram ----
    if s.speech:
        out.append("## What was spoken (top 20)")
        out.append("")
        for text, n in Counter(t for _, _, t in s.speech if t).most_common(20):
            out.append("- x{} `{}`".format(n, text[:100]))
        out.append("")

    # ---- trend, version-segmented ----
    same = [r for r in index_rows if r.get("version") == s.version
            and r.get("log") != Path(s.path).name]
    out.append("## Trend (tools/menulog-index.json — reader {} only)".format(s.version or "?"))
    out.append("")
    out.append("_Rows from other reader versions are EXCLUDED, not merged. v1.4 and v1.5 do not "
               "name the same things, so a mixed table makes a coverage improvement look like a "
               "regression._")
    out.append("")
    if same:
        out.append("| log | rows | named | gaps | distinct | shots |")
        out.append("|-----|------|-------|------|----------|-------|")
        for r in (same + [_index_row(s, snaps)])[-8:]:
            out.append("| {} | {} | {} | {} | {} | {} |".format(
                r.get("log"), r.get("rows"), r.get("mapped"), r.get("gapEvents"),
                r.get("gapsDistinct"), r.get("shots")))
    else:
        out.append("_First session on reader {} — no comparison available yet._"
                   .format(s.version or "?"))
    out.append("")
    return "\n".join(out) + "\n"


def _index_row(s, snaps):
    mapped = sum(1 for r in s.rows.values() if r["mapped"] is True)
    return {
        "log": Path(s.path).name,
        "version": s.version,
        "labeldb": s.labeldb,
        "frames": s.frames,
        "rows": len(s.rows),
        "mapped": mapped,
        "gapEvents": len(s.gaps),
        "gapsDistinct": len({(g["kind"], g["key"]) for g in s.gaps}),
        "shots": sum(len(v) for v in (snaps or {}).values()),
        "panes": len(s.panes),
        "probeDisabled": s.disabled or None,
    }


def write_manifest(dst, s, snaps, sheets):
    """Ranks the screenshots: real gaps before coverage frames."""
    man = ["# Menu snapshot manifest — {}".format(Path(s.path).name), ""]
    if not snaps:
        man.append("_No screenshots in this session._")
        (dst / "manifest.md").write_text("\n".join(man) + "\n", encoding="utf-8")
        return
    rows = []
    for seq, names in sorted(snaps.items()):
        cap = s.captures.get(seq, {})
        tag = cap.get("tag") or "?"
        rows.append({"seq": seq, "tag": tag, "key": cap.get("key") or "-",
                     "names": names, "sheet": sheets.get(seq),
                     "coverage": tag in COVERAGE_TAGS})
    rows.sort(key=lambda r: (r["coverage"], r["seq"]))

    man.append("Gaps first, then coverage frames. A gap frame answers a question the log")
    man.append("could only ask; a coverage frame is reference material for building label")
    man.append("tables and can wait.")
    man.append("")
    man.append("| # | open | tag | what failed | kind |")
    man.append("|---|------|-----|-------------|------|")
    for r in rows:
        opener = r["sheet"] or r["names"][-1]
        man.append("| {} | `snaps/{}` | {} | `{}` | {} |".format(
            r["seq"], opener, r["tag"], r["key"],
            "coverage" if r["coverage"] else "**gap**"))
    man.append("")
    (dst / "manifest.md").write_text("\n".join(man) + "\n", encoding="utf-8")


def process(path, args, index):
    print("== {}".format(path))
    s = parse(path)
    out_dir = args.out_dir / Path(path).stem
    out_dir.mkdir(parents=True, exist_ok=True)

    snaps = collect_snapshots(path, out_dir)
    sheets = {}
    if snaps:
        dst = out_dir / "snaps"
        for seq, names in snaps.items():
            tag = (s.captures.get(seq) or {}).get("tag") or "UNTAGGED"
            sh = build_contact_sheet(dst, seq, tag, names)
            if sh:
                sheets[seq] = sh
        write_manifest(dst, s, snaps, sheets)

    # tag|key -> best image, so gaps.md can name the picture beside each entry.
    snap_by_key = {}
    for seq, names in (snaps or {}).items():
        cap = s.captures.get(seq) or {}
        if cap.get("tag"):
            snap_by_key["{}|{}".format(cap["tag"], cap.get("key"))] = \
                sheets.get(seq) or names[-1]

    (out_dir / "gaps.md").write_text(build_gaps_md(s, snap_by_key), encoding="utf-8")
    row = _index_row(s, snaps)
    index[:] = [r for r in index if r.get("log") != row["log"]]
    index.append(row)
    index.sort(key=lambda r: r.get("log", ""))

    digest = build_digest(s, snaps, snap_by_key, index)
    (out_dir / "digest.md").write_text(digest, encoding="utf-8")

    print("   reader={} rows={} named={} gaps={} ({} distinct) shots={}".format(
        s.version, len(s.rows), row["mapped"], len(s.gaps), row["gapsDistinct"],
        row["shots"]))
    print("   wrote {}/digest.md + gaps.md".format(out_dir))


def selftest():
    """Reproduces the shapes this parser has to survive, from real 08-11 lines."""
    import tempfile
    sample = "\n".join([
        "# GTA11Y Menu/Phone Probe Debug Log",
        "[F0] EVENT menulog-version: reader1.5",
        "[F1] FRAME t=18:21:21.491 paused=0 state=0",
        "[F10] EVENT labeldb-version: status=ok(v5) overrides=82",
        "[F20] EVENT reader-pane: pane=22 via=activate",
        "[F21] EVENT input: ctrl=right src=key",
        "[F24] EVENT row-visit: menu=140 item=1 dwellMs=265 lr=0",
        "[F25] EVENT reader-speak: src=pause text=\"Music Volume, 9 of 10\"",
        "[F30] EVENT settingsxml-unmapped: pref=PREF_AUDIO_MUTE_ON_FOCUS_LOSS "
        "optionType=MENU_OPTION_DISPLAY_ON_OFF pane=22",
        "[F31] EVENT capture: seq=1 tag=SETTINGS-UNMAPPED key=PREF_AUDIO_MUTE_ON_FOCUS_LOSS",
        "[F40] EVENT phone-app: on=appcontacts:1 off=none",
        "[F41] EVENT phone-row: row=4 prev=-2147483648 latencyMs=32",
        "[F42] EVENT reader-unmapped-phone: script=apptextmessage row=0 hasTable=0 globalsOk=1",
        "[F50] EVENT probe-error: probe=phone n=1 msg=boom",
        "[F51] EVENT reader-open-flicker: aliveMs=14 state=0",
        "[F99] FRAME t=18:25:00.000 paused=1 state=1",
    ])
    ok = True

    def check(label, cond):
        nonlocal ok
        print("  {}  {}".format("PASS" if cond else "FAIL", label))
        ok = ok and bool(cond)

    with tempfile.TemporaryDirectory() as td:
        p = Path(td) / "menulog-2026-01-01-000000.log"
        p.write_text(sample, encoding="utf-8")
        s = parse(p)

    check("version banner is read — got {}".format(s.version), s.version == "reader1.5")
    check("labeldb banner is read", s.labeldb and s.labeldb.startswith("status=ok"))
    check("FRAME lines counted — got {}".format(s.frames), s.frames == 2)
    check("session span measured", s.t0 is not None and s.t1 is not None and s.t1 > s.t0)
    check("pane visit recorded", s.panes.get(22) == 1)
    check("row-visit becomes a coverage row", ("menu", 140, 1) in s.rows)
    check("quoted speech survives the space — got {!r}".format(s.rows[("menu", 140, 1)]["text"]),
          s.rows[("menu", 140, 1)]["text"] == "Music Volume, 9 of 10")
    check("a spoken row is marked mapped", s.rows[("menu", 140, 1)]["mapped"] is True)
    check("input->speech latency captured — got {}".format(s.latencies), s.latencies == [4])
    check("phone latency read from the log", s.phone_latencies == [32.0])
    check("two gap kinds collected — got {}".format(len(s.gaps)), len(s.gaps) == 2)
    keys = {(g["kind"], g["key"]) for g in s.gaps}
    check("settings gap keyed by pref",
          ("settingsxml-unmapped", "PREF_AUDIO_MUTE_ON_FOCUS_LOSS") in keys)
    check("phone gap keyed script:row",
          ("reader-unmapped-phone", "apptextmessage:0") in keys)
    check("gap carries the pane it happened in",
          any(g["pane"] == 22 for g in s.gaps if g["kind"] == "settingsxml-unmapped"))
    check("capture seq indexed", s.captures.get(1, {}).get("tag") == "SETTINGS-UNMAPPED")
    check("capture key matches the gap key — this is the join",
          s.captures[1]["key"] == "PREF_AUDIO_MUTE_ON_FOCUS_LOSS")
    check("probe error counted", s.errors.get("phone") == 1)
    check("flicker counted", s.flicker == 1)
    check("phone row became a coverage row", ("phone", "appcontacts", 4) in s.rows)

    # The gap must be able to find its screenshot by tag|key alone.
    tagged = "{}|{}".format(_tag_for("settingsxml-unmapped"), "PREF_AUDIO_MUTE_ON_FOCUS_LOSS")
    md = build_gaps_md(s, {tagged: "sheet-0001-SETTINGS-UNMAPPED.jpg"})
    check("gaps.md joins the screenshot to the gap",
          "sheet-0001-SETTINGS-UNMAPPED.jpg" in md)
    check("gaps.md states the fix, not just the fault",
          "menulabels-overrides.json" in md)

    print("\n" + ("SELFTEST PASSED" if ok else "SELFTEST FAILED"))
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*", help="menulog file(s); default = newest")
    ap.add_argument("--out-dir", type=Path, default=REPO_ROOT / "triage")
    ap.add_argument("--index", type=Path, default=REPO_ROOT / "tools" / "menulog-index.json")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()

    if args.selftest:
        sys.exit(selftest())

    paths = [Path(p) for p in args.logs]
    if not paths:
        n = newest_log("menulog")
        if not n:
            print("No menulog-*.log found in ModSettings or the repo root.")
            sys.exit(1)
        paths = [n]

    index = []
    if args.index.exists():
        try:
            index = json.loads(args.index.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            index = []

    for p in paths:
        if not p.exists():
            print("!! missing: {}".format(p))
            continue
        process(p, args, index)

    args.index.parent.mkdir(parents=True, exist_ok=True)
    args.index.write_text(json.dumps(index, indent=1), encoding="utf-8")
    print("== index updated: {} ({} sessions)".format(args.index, len(index)))


if __name__ == "__main__":
    main()
