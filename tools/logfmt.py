#!/usr/bin/env python3
"""logfmt.py — parsing shared by the gta11y log triage tools.

The drive-assist log and the menu/phone log are written by different loggers but
speak one grammar:

    [F<n>] EVENT <kind>: <key>=<value> <key>=<value> ...
    [F<n>] FRAME t=HH:MM:SS.mmm <key>=<value> ...

so the field readers, the clock helpers and the screenshot join are identical for
both. They lived in triage-log.py first; this module exists so triage-menulog.py
can use them without copying, and so a fix to `kv` lands in both tools at once.

Deliberately small. Everything here is either pure text parsing or filesystem
plumbing that has no opinion about what the log MEANS — the drive-assist model
(frames, markers, incidents, gates) stays in triage-log.py, and the menu model
(panes, rows, coverage gaps) stays in triage-menulog.py. If something needs to
know what a field signifies, it does not belong here.
"""

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MODSETTINGS = Path.home() / "Documents" / "Rockstar Games" / "GTA V" / "ModSettings"

# Mirrors SnapshotWriter.SanitizeTag on the C# side: anything not alphanumeric or a
# hyphen became an underscore in the on-disk filename.
SAFE_TAG_RE = re.compile(r"[^A-Za-z0-9-]")

TIME_RE = re.compile(r"t=(\d{2}):(\d{2}):(\d{2})\.(\d{3})")
FRAME_PREFIX_RE = re.compile(r"^\[F(\d+)\]\s*")
EVENT_RE = re.compile(r"^\[F(\d+)\]\s+EVENT\s+([a-zA-Z0-9-]+):\s*(.*)$")


def kv(line, key):
    """Value of key= in line, or None. Empty string is a valid value."""
    m = re.search(re.escape(key) + r"=(\S*)", line)
    return m.group(1) if m else None


def qkv(line, key):
    """Value of key="..." in line, or None.

    Menu logs quote anything that can contain spaces — spoken text, GXT strings,
    row labels — so `kv` would return only the first word of every one of them.
    Falls back to the unquoted reading so a caller does not have to know which
    form a given field uses.
    """
    m = re.search(re.escape(key) + r'="([^"]*)"', line)
    if m:
        return m.group(1)
    return kv(line, key)


def fnum_of(line, key):
    v = kv(line, key)
    if v is None:
        return None
    try:
        return float(v.rstrip(",)"))
    except ValueError:
        return None


def inum_of(line, key):
    v = fnum_of(line, key)
    return None if v is None else int(v)


def hms_to_s(h, m, s, ms):
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0


def time_of(line):
    """Seconds-since-midnight from a t=HH:MM:SS.mmm field, or None."""
    m = TIME_RE.search(line)
    return hms_to_s(*m.groups()) if m else None


def fmt_t(t):
    if t is None:
        return "--:--:--"
    t = t % 86400
    return "%02d:%02d:%06.3f" % (t // 3600, (t % 3600) // 60, t % 60)


def fmt_dur(sec):
    """Compact duration: 45s, 3m12s, 1h04m."""
    if sec is None:
        return "-"
    sec = int(round(sec))
    if sec < 60:
        return "%ds" % sec
    if sec < 3600:
        return "%dm%02ds" % (sec // 60, sec % 60)
    return "%dh%02dm" % (sec // 3600, (sec % 3600) // 60)


def parse_events(lines):
    """Every `[F<n>] EVENT kind: rest` line as a dict.

    Returns dicts with f (frame), kind, rest, line, i (0-based index in `lines`).
    Order is preserved, which is what lets a caller pair an event with the frame
    that follows it without re-scanning.
    """
    out = []
    for i, line in enumerate(lines):
        m = EVENT_RE.match(line)
        if m:
            out.append({"f": int(m.group(1)), "kind": m.group(2),
                        "rest": m.group(3), "line": line, "i": i})
    return out


def newest_log(prefix, extra_dirs=()):
    """Newest <prefix>-*.log across ModSettings and the repo root.

    Both tools default to "the log you just recorded", and both have to look in
    two places because the mod writes to ModSettings while older logs were kept
    beside the repo.
    """
    cands = []
    for d in (MODSETTINGS, REPO_ROOT) + tuple(extra_dirs):
        try:
            cands.extend(Path(d).glob(prefix + "-*.log"))
        except OSError:
            continue
    if not cands:
        return None
    return max(cands, key=lambda p: p.stat().st_mtime)


def image_order_key(name):
    """Chronological order within one marker's frames.

    Preroll frames are named by how many grabs AGO they were, so -pre4 is the
    OLDEST and -pre1 the newest, and the failure frame carries no suffix. Sorting
    the filenames as plain strings therefore produces exactly the wrong order and
    puts the failure frame in the middle.
    """
    m = re.search(r"-pre(\d+)\.jpg$", name)
    if m:
        return (0, -int(m.group(1)))
    return (1, 0)


def frame_label(name):
    """Human label for a contact-sheet cell. Preroll grabs are ~1 Hz."""
    m = re.search(r"-pre(\d+)\.jpg$", name)
    return "t-{}s".format(m.group(1)) if m else "FAILURE"


def capture_geometry_faults(selftest_events):
    """Caveats about WHAT the screenshots contain, from the iter-55 census fields.

    A capture can succeed, pass the blank-frame self-test, write every frame it was
    asked for, and still be wrong: on 2026-08-12 the writer blitted the whole primary
    screen while the game sat in a window, so all 120 images carried ~40% Windows
    desktop and the game itself was encoded at 1015 px inside a 1280 px frame. Nothing
    in the numbers said so — it was found by opening an image.

    The mod now measures and logs the region it blits, so the digest can say it. These
    land in the digest's caveat line rather than anywhere lower down, because an image
    set that is 40% desktop makes every visual conclusion below it weaker.

    Returns a list of strings; empty when the geometry is clean or absent (older logs
    have no such fields, and their silence must not read as a fault).
    """
    if not selftest_events:
        return []
    line = selftest_events[-1].get("line") or ""
    out = []
    src = kv(line, "src")
    if src == "screenFallback":
        out.append("**screenshots contain the whole desktop, not just the game** "
                   "(`src=screenFallback` — the game window could not be found)")
    # Read with a regex, not inum_of: the value carries a trailing '%'.
    m = re.search(r"clientFrac=(\d+)%", line)
    frac = int(m.group(1)) if m else None
    if frac is not None and frac < 90 and src != "screenFallback":
        out.append(f"the game window covered only {frac}% of the screen, so every "
                   f"frame is that much lower-resolution than it looks")
    if kv(line, "foreground") == "false":
        out.append("the game window was NOT in the foreground when the writer started "
                   "(`foreground=false`) — frames may show whatever was on top of it")
    return out


def collect_snapshots(log_path, out_dir):
    """Copies <log-stem>-snaps/*.jpg into <out_dir>/snaps/ and indexes them by seq.

    Returns {seq: [filename, ...]} with each list in chronological order, or {}
    when the directory does not exist. Silent in that case: snapshots are gated,
    so their absence is the normal outcome and not a fault worth warning about.
    """
    import shutil
    src = Path(log_path).parent / (Path(log_path).stem + "-snaps")
    if not src.is_dir():
        return {}
    by_seq = {}
    for img in sorted(src.glob("snap-*.jpg")):
        m = re.match(r"snap-(\d+)-", img.name)
        if not m:
            continue
        by_seq.setdefault(int(m.group(1)), []).append(img)
    if not by_seq:
        return {}

    dst = Path(out_dir) / "snaps"
    dst.mkdir(parents=True, exist_ok=True)
    out = {}
    for seq, imgs in by_seq.items():
        names = []
        for img in imgs:
            target = dst / img.name
            try:
                if not target.exists() or target.stat().st_size != img.stat().st_size:
                    shutil.copy2(img, target)
                names.append(img.name)
            except OSError:
                pass
        if names:
            out[seq] = sorted(names, key=image_order_key)
    return out


def build_contact_sheet(dst_dir, seq, tag, image_names):
    """Composites one event's frames into a single sheet; returns its filename.

    A failure with preroll is five separate JPEGs, and reading five images costs
    five times the context of reading one. Five stills also do not read as motion
    — the run-up only becomes legible with the frames side by side, in order.

    Returns None (silently) if Pillow is missing or anything fails. The frames
    themselves are still copied, so the degraded case is "no sheet", never "no
    evidence".
    """
    dst_dir = Path(dst_dir)
    if len(image_names) < 2:
        return None
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        return None

    try:
        cells = []
        for n in sorted(image_names, key=image_order_key):
            p = dst_dir / n
            if not p.exists():
                continue
            im = Image.open(p)
            im.load()
            cells.append((n, im.convert("RGB")))
        if len(cells) < 2:
            for _, im in cells:
                im.close()
            return None

        cols = min(3, len(cells))
        rows = (len(cells) + cols - 1) // cols
        cw = 800
        ch = max(1, int(round(cw * cells[0][1].height / cells[0][1].width)))
        bar = 34

        sheet = Image.new("RGB", (cw * cols, (ch + bar) * rows), (16, 16, 16))
        draw = ImageDraw.Draw(sheet)
        try:
            font = ImageFont.truetype("arial.ttf", 24)
        except OSError:
            font = ImageFont.load_default()

        for idx, (n, im) in enumerate(cells):
            r, c = divmod(idx, cols)
            x, y = c * cw, r * (ch + bar)
            sheet.paste(im.resize((cw, ch), Image.LANCZOS), (x, y))
            label = frame_label(n)
            draw.text((x + 8, y + ch + 5), label, font=font,
                      fill=(255, 90, 90) if label == "FAILURE" else (200, 200, 200))
            im.close()

        out_name = "sheet-{:04d}-{}.jpg".format(seq, SAFE_TAG_RE.sub("_", tag or "UNTAGGED"))
        sheet.save(dst_dir / out_name, "JPEG", quality=72)
        sheet.close()
        return out_name
    except (OSError, ValueError):
        return None
