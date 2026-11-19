#!/usr/bin/env python3
"""
build-modelnames.py — produces GTA/scripts/gta11y-modelnames.tsv.gz for the gta11y mod.

Replaces `External Resources/hashes.txt`, which was hand-placed on 2022-12-21 and never
regenerated. Every DLC since is missing from it, and — worse — it contains no ambient
pedestrian models at all (zero rows matching a_m_*, a_f_*, s_m_*, s_f_*, g_m_*, g_f_*),
so the "Nearest Characters" scan could never name an ordinary passer-by even in 2022.

Consumed at runtime by GTA/ModelNames.cs.

WHY A TABLE STILL EXISTS. joaat is one-way and GTA V retail keeps only the hash in
fwArchetype — the model-name strings are not in the executable, so there is nothing to
read back at runtime. Vehicles are the exception: the game names those itself through
GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, so this table deliberately carries NO vehicle rows.
VehicleList.ini is downloaded only to prove that claim in --verify.

WHY TSV AND NOT JSON. Every other bundled table in this repo is JSON, and this one is not,
for two reasons: it is ~55k rows (an order of magnitude past anything else here) and it
sits on the startup path, so a hand-rolled Split('\\t') parser beats Newtonsoft
measurably; and when a player reports "it called that thing the wrong name", a human needs
to grep one line out of it. `#` comment lines carry the version and provenance that the
JSON tables keep in a "generated" object.

Sources: DurtyFree/gta-v-data-dumps (auto-generated from the game's own files, currently
stamped GTA V v3717.0 / Online 1.72). Fetched by raw URL into tools/_cache/modelnames/;
re-run with --refresh to re-download. Do not clone that repo either — peds.json alone is
16 MB.

Hand corrections: tools/modelnames-overrides.json (merged LAST, survives regeneration),
same provenance model as tools/worlddata-overrides.json. Generator output is never
hand-edited.

Output format (version 2), tab-separated, one model per line:

    kind <TAB> signedInt32Hash <TAB> spoken name <TAB> extra <TAB> flags

    kind  p = ped, o = object/prop, d = door-or-gate prop, x = legacy row of unknown kind
    extra ped rows only: the director-mode GXT key, resolved live at runtime so a German
          player hears "Wildschwein" rather than "Boar". Empty otherwise.
    flags "m" = machine-derived, unreviewed. See below. Empty means the name is trusted.

WHY THE `m` FLAG EXISTS (v2, iter-60). v1 emitted a row for every model it had a hash
for, and the runtime preferred any table hit over its own derived description. That is
backwards when the "name" is a map archetype id: 74% of v1's object rows were strings
like "Ch3 07 Decals50" or "Ap1 01 B Ivy07 Noshad", and 848 of its 1,109 ped rows were
raw model stems like "Isldj", "Clubcust", "Bountytarget". Spoken aloud those are worse
than the fallback they were overriding — "man" is at least true.

So a row now says whether its name is defensible out loud. Flagged rows keep their hash
(coverage is unchanged, and the runtime still knows the model is KNOWN rather than
missing — it just will not read the name), and land in tools/modelnames-review.txt as
candidates for tools/modelnames-overrides.json. That file is to this table what
triage gaps.md is to the menu reader.

Sayability is decided from the MODEL name, not from the prettified output, because the
model tells you the provenance: a `prop_*`/`v_*` archetype is something a player walks
up to, whereas `ch3_07_decals50` is scenery that happens to have a hash.
"""

import argparse
import datetime
import gzip
import hashlib
import io
import json
import os
import re
import sys
import urllib.request

HERE      = os.path.dirname(os.path.abspath(__file__))
CACHE     = os.path.join(HERE, "_cache", "modelnames")
OVERRIDES = os.path.join(HERE, "modelnames-overrides.json")
LEGACY    = os.path.join(HERE, os.pardir, "External Resources", "hashes.txt")
OUT       = os.path.join(HERE, os.pardir, "GTA", "scripts", "gta11y-modelnames.tsv.gz")

RAW_BASE = "https://raw.githubusercontent.com/DurtyFree/gta-v-data-dumps/master/"

# Only these are ever downloaded.
SOURCES = [
    "peds.json",         # 16 MB: Name, SignedHash, Pedtype, TranslatedDirectorName
    "ObjectList.ini",    # prop and map-archetype names, one per line
    "VehicleList.ini",   # cross-check only — see --verify; no rows are emitted from it
]

SCHEMA_VERSION = 2

# Written next to the overrides file: every row the generator would not vouch for out
# loud, so the next hand pass has a worklist instead of a hunch.
REVIEW = os.path.join(HERE, "modelnames-review.txt")

# Round-trip anchors. If joaat ever drifts, every name in the table silently points at the
# wrong model — which is the one failure this mod must never ship. One negative and one
# positive hash, because the bug that prompted this rewrite only affected negatives.
JOAAT_ANCHORS = {
    "adder": -1216765807,
    "prop_vend_soda_01": 992069095,
    "a_c_boar": -832573324,
    "player_zero": 225514697,
}

# Models --verify expects to find IN the table, with the kind they must carry. Two of the
# four are negative on purpose: a negative hash is precisely what the old lookup could
# never match, so a table that only proves itself on positives proves nothing.
# `adder` is deliberately absent from this list — it is a vehicle, and a vehicle appearing
# here would mean the table had started competing with the game's own naming.
TABLE_ANCHORS = {
    "02gate3_l":         ("d", -1267889684),   # negative, door/gate
    "a_c_boar":          ("p", -832573324),    # negative, ped
    "prop_vend_soda_01": ("o", 992069095),     # positive, prop
    "player_zero":       ("p", 225514697),     # positive, ped
}

# ---------------------------------------------------------------------------
# Name derivation
# ---------------------------------------------------------------------------

# Ped model prefixes: <archetype>_<gender>_<age>_. a_=ambient s_=scripted g_=gang
# u_=unique ig_/cs_/csb_=story hc_=heist crew mp_=multiplayer. a_c_ is an animal.
PED_PREFIX = re.compile(r"^(?:a_c_|[asgu]_[mf]_[ymo]_|ig_|cs_|csb_|hc_|mp_[mf]_|mp_)")

# Prop prefixes. The <dlc>_prop_ family is regular enough for one pattern; the v_* map
# archetypes are not, so they are listed.
PROP_PREFIX = re.compile(
    r"^(?:[a-z0-9]{1,6}_)?prop_"
    r"|^v_(?:ilev|res|med|serv|corp|club|ret|int|proc|misc|tra)_"
    r"|^p_"
)

# Trailing variant markers: _01, _02a, _a, _l/_r on gates.
VARIANT_SUFFIX = re.compile(r"_(?:\d{1,3}[a-z]?|[a-z])$")

# The same marker glued to the end of a word rather than underscore-separated:
# `prop_roadcone01a`, `prop_streetlight05`. iter-58 could only strip the separated form,
# so a third of the props a player actually meets kept a number in their spoken name and
# iter-60's digit rule then flagged them as machine noise. "Roadcone", not "Roadcone01a".
GLUED_VARIANT = re.compile(r"(?<=[a-z]{3})\d{1,3}[a-z]?$")

# Glued compound tokens the game uses in model names. Only the ones that actually occur
# often enough to be heard — everything else is left alone and corrected in the overrides
# file if a session shows it mattering. Longest key wins.
WORD_FIXES = {
    "bevhills": "beverly hills", "airhostess": "air hostess", "hwaycop": "highway patrol",
    "dockwork": "dock worker", "constructionworker": "construction worker",
    "vinewood": "vinewood", "vindouche": "vinewood douche", "eastsa": "east",
    "downtown": "downtown", "beachvesp": "beach vespucci", "salton": "salton sea",
    "paparazzi": "paparazzi", "streetart": "street artist", "shopmask": "shop assistant",
    "fatcult": "cultist", "hasjew": "hasidic", "motox": "motocross rider",
    "runner": "runner", "sunbathe": "sunbather", "topless": "topless dancer",
    "dhill": "hill", "genstreet": "street person", "gentransport": "commuter",
    "malibu": "malibu", "polynesian": "polynesian", "prolhost": "host",
    "roadcyc": "road cyclist", "skater": "skater", "soucent": "south central",
    "stwhi": "street", "stlat": "street", "tourist": "tourist",
    "trampbeac": "beach vagrant", "tramp": "vagrant", "yoga": "yoga student",
    "ktown": "korea town", "chigoon": "goon", "chiboss": "boss",
    "armoured": "armoured guard", "autoshop": "mechanic", "barman": "barman",
    "busker": "busker", "ccrew": "crew member", "chef": "chef", "clerk": "clerk",
    "business": "office worker", "trafficwarden": "traffic warden",
    "ballasog": "ballas gang member", "methhead": "addict", "acult": "cultist",
    "cntrybar": "country bar patron", "dealer": "dealer", "devinsec": "security guard",
    "doorman": "doorman", "fatlatin": "man", "fatwhite": "man", "fibsec": "fib agent",
    "fireman": "firefighter", "gaffer": "gaffer", "gardener": "gardener",
    "garbage": "sanitation worker", "grip": "grip", "hairdress": "hairdresser",
    "highsec": "security guard", "hooker": "sex worker", "janitor": "janitor",
    "lathandy": "handyman", "lifeinvad": "lifeinvader employee", "linecook": "line cook",
    "lsmetro": "metro worker", "mariachi": "mariachi", "migrant": "migrant worker",
    "mime": "mime", "movprem": "premiere guest", "movspace": "usher",
    "paramedic": "paramedic", "pestcont": "pest controller", "pilot": "pilot",
    "postal": "postal worker", "prisguard": "prison guard", "prisoner": "prisoner",
    "prismuscl": "prisoner", "roadcyc2": "road cyclist", "sheriff": "sheriff deputy",
    "shop_high": "shop assistant", "shop_low": "shop assistant",
    "shop_mid": "shop assistant", "strperf": "street performer",
    "strpreach": "street preacher", "strvend": "street vendor", "trucker": "trucker",
    "valet": "valet", "vagrant": "vagrant", "waiter": "waiter", "winclean": "window cleaner",
    # iter-60: the ambient cores that were still falling through to a bare "man"/"woman"
    # because the model id names a job and nothing was reading it. Only entries whose
    # meaning is unambiguous from the id — a guess here is spoken as fact, so anything
    # that needed a hunch was left for tools/modelnames-overrides.json instead.
    "ammucity": "gun shop clerk", "ammucountry": "gun shop clerk",
    "autopsy": "mortuary technician", "bailoffice": "bail bondsman",
    "ballas": "Ballas gang member", "bankman": "bank clerk",
    "bankrobber": "bank robber", "bartender": "bartender",
    "beachbarstaff": "beach bar staff", "bikehire": "bike hire attendant",
    "bikerchic": "biker", "blackops": "special forces soldier", "bouncer": "bouncer",
    "breakdance": "breakdancer", "busboy": "busboy", "carclub": "car club member",
    "cartelgoons": "cartel guard", "cartelguards": "cartel guard",
    "casinocash": "casino cashier", "casinoshop": "casino shop assistant",
    "chemwork": "chemical worker", "clubbar": "club bar staff",
    "clubcust": "club customer", "cop": "police officer", "corpse": "body",
    "cyclist": "cyclist", "danceburl": "dancer", "dancelthr": "dancer",
    "dancerave": "dancer", "doa": "body", "drowned": "body",
    "drugprocess": "drug worker", "dwservice": "utility worker",
    "families": "Families gang member", "femaleagent": "agent",
    "fibarchitect": "fib employee", "fiboffice": "fib employee",
    "fibmugger": "mugger", "fieldworker": "field worker",
    "filmdirector": "film director", "fitness": "jogger", "genbiker": "biker",
    "genthug": "thug", "goons": "thug", "gunvend": "gun vendor",
    "hazmatworker": "hazmat worker", "hiker": "hiker", "hippie": "hippie",
    "hippy": "hippie", "jetski": "jet ski rider", "jewelsec": "security guard",
    "jewelthief": "thief", "juggalo": "juggalo", "mexgang": "gang member",
    "mexgoon": "thug", "mexlabor": "labourer", "pologoon": "thug",
    "proldriver": "driver", "prolsec": "security guard", "promourn": "mourner",
    "raceorg": "race organiser", "ranger": "park ranger",
    "retailstaff": "shop assistant", "rivalpap": "paparazzo", "robber": "robber",
    "rurmeth": "meth cook", "salvagoon": "thug", "scrubs": "hospital worker",
    "security": "security guard", "smugmech": "mechanic", "snowcop": "police officer",
    "stripperlite": "stripper", "stripper": "stripper", "strpunk": "punk",
    "studioassist": "studio worker", "studioprod": "studio worker",
    "studiosoueng": "studio worker", "subcrew": "crew member",
    "sweatshop": "sweatshop worker", "tattoocust": "tattoo customer",
    "tattoo": "tattoo artist", "warehouse": "warehouse worker",
    "waretech": "warehouse worker", "westsec": "security guard", "zombie": "zombie",
}

TITLE_KEEP_LOWER = {"a", "an", "and", "at", "de", "for", "in", "la", "of", "on", "the",
                    "to", "with"}

# WORD_FIXES values that name a PLACE rather than a role. These attach as an appositive
# ("woman, downtown") instead of replacing the head noun, because "a downtown" is not a
# person and "young downtown" is not English.
ZONE_WORDS = {
    "beverly hills", "vinewood", "east", "downtown", "beach vespucci", "salton sea",
    "hill", "malibu", "korea town", "south central", "street", "country bar patron",
}

# Ped model tokens that describe only what someone LOOKS like. Announcing a stranger's
# apparent ethnicity tells a blind player nothing they can navigate by, and the model id
# is a poor guide to it anyway — so these fall back to the gender the model name already
# encodes. Peds with a director name never reach here; the game's own word wins.
DEMOGRAPHIC_ONLY = {
    "afriamer", "latino", "indian", "chinese", "korean", "polynesian", "asian",
    "fatlatin", "fatwhite", "fatblack", "fatcult", "hasjew", "eastsa",
    # iter-60: same rule, cores the original pass missed.
    "socenlat", "stbla", "stwhi", "stlat", "mexcntry", "guido", "gay", "hotposh",
}

PED_GENDER = re.compile(r"^[asgu]_(m|f)_[ymo]_")

# Ambient ped ids carry archetype, sex and age: a_m_o_soucent_01 is ambient/male/old.
# v1 threw the age away and spoke the zone alone ("South Central"), which names a place
# the player is already standing in rather than the person in front of them.
PED_SEX_AGE = re.compile(r"^[asgu]_(m|f)_(y|m|o)_")

# "m" (middle-aged) is the unmarked case and deliberately costs no word — every scan
# result carries this, so an adjective that is true of most people is pure length.
AGE_WORD = {"y": "young", "o": "older", "m": ""}

# Tokens that mark a model as scenery rather than something a player meets: render
# helpers, level-of-detail stand-ins, collision shells. A name containing one of these
# is never worth speaking.
JUNK_TOKENS = {
    "lod", "slod", "proxy", "decal", "decals", "emis", "emissive", "emismr",
    "shadowmap", "noshad", "shad", "shadow", "occl", "occluder", "dtl", "ref",
    "col", "collision", "nonrender", "dummy", "overlay", "mip", "alpha",
    "blocker", "lightmesh", "nh", "ov", "vb", "frag", "cutscene", "test",
}

# The families a player actually walks up to. Everything else in ObjectList.ini and
# hashes.txt is map geometry that merely happens to own a hash.
PROP_FAMILY = re.compile(
    r"^(?:[a-z0-9]{1,6}_)?prop_"
    r"|^v_(?:ilev|res|med|serv|corp|club|ret|int|proc|misc|tra)_"
    r"|^p_"
)

# Story and cutscene peds whose model stem is genuinely the character's name. Everything
# else in the ig_/cs_/csb_ families is a compressed compound ("isldj", "clubcust",
# "helmsmanpavel", "bountytarget") that reads as noise, so it is flagged and the runtime
# says "man"/"woman" instead. Mission-only peds are rare in free roam, which is why this
# list is allowed to be short and to grow from modelnames-review.txt rather than being
# guessed at wholesale.
STORY_NAMES = {
    "michael", "franklin", "trevor", "amanda", "jimmy", "tracy", "lamar", "lester",
    "devin", "steve", "dave", "wade", "ron", "brad", "simeon", "tanisha", "denise",
    "stretch", "chop", "beverly", "nervousron", "solomon", "molly", "tao", "cheng",
    "martin", "patricia", "ortega", "floyd", "debra", "english", "dom", "abigail",
    "barry", "tonya", "mrsphillips", "maude", "josef", "manuel", "omega", "hao",
    "isaac", "andreas", "ashley", "carol", "casey", "chrisformage", "clay",
    "dreyfuss", "edtoh", "fabien", "gerald", "guadalope", "hugh", "janet", "jewelass",
    "joeminuteman", "johnnyklebitz", "josh", "karen", "lazlow", "magenta", "manuel",
    "marnie", "milton", "mrk", "natalia", "nigel", "old_man1a", "old_man2",
    "orleans", "paper", "priest", "prolsec", "russiandrunk", "siemonyetarian",
    "sol", "stevehains", "stretch", "taostranslator", "terry", "tom", "tomepsilon",
    "tracydisanto", "wade", "zimbor",
}


# Short words that are real English rather than a DLC abbreviation. Without these the
# two-letter rule below would reject the appositives this iteration composes on purpose
# ("woman, on the promenade").
SHORT_REAL_WORDS = {
    "a", "an", "at", "by", "de", "do", "go", "in", "is", "it", "la", "no", "of",
    "on", "or", "to", "up", "us", "the", "and", "for", "off", "out", "tv", "ac",
    # Honorifics, which are exactly two letters and exactly not abbreviations of a
    # DLC: "Dr Friedlander", "Cheng Jr.".
    "dr", "jr", "sr", "mr", "ms", "st",
}


def structurally_sayable(name):
    """Would this string survive being read out by a screen reader?

    Rejects the three shapes that give a machine name away: a digit anywhere (map
    archetypes are numbered, real objects are not), a one- or two-letter token that is
    not a real word (the DLC and zone abbreviations — `vw`, `hei`, `ic`, `sf`), and any
    render-helper token.
    """
    n = (name or "").strip().lower()
    if not n:
        return False
    if re.search(r"\d", n):
        return False
    tokens = [t.strip(",.") for t in n.split()]
    tokens = [t for t in tokens if t]
    if not tokens:
        return False
    if any(len(t) <= 2 and t not in SHORT_REAL_WORDS for t in tokens):
        return False
    if any(t in JUNK_TOKENS for t in tokens):
        return False
    return True


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


def to_signed32(v):
    """Normalize a hash to signed int32.

    The sources disagree: hashes.txt is signed, VehicleList.ini/ObjectList.ini are
    unsigned, peds.json carries both. Mixing the two representations is exactly the bug
    this iteration exists to fix — the runtime lookup was comparing a sign-extended
    UInt64 against a signed key and silently matching nothing.
    """
    v = int(v) & 0xFFFFFFFF
    return v - 0x100000000 if v >= 0x80000000 else v


def titlecase(s):
    words = s.split()
    out = []
    for i, w in enumerate(words):
        out.append(w if (i and w in TITLE_KEEP_LOWER) else (w[:1].upper() + w[1:]))
    return " ".join(out)


def prettify(model, prefix_re):
    """Model name -> something a screen reader can say.

    `s_m_y_marine_03` -> "Marine". `prop_vend_soda_01` -> "Vend Soda". The result is
    always non-empty: stripping must never leave nothing, or the entity would be
    announced with a blank where its name should be.
    """
    name = model.lower().strip()
    lead = name.split("_", 1)[0]          # the DLC tag, before it is stripped
    stripped = prefix_re.sub("", name, count=1)
    echoed = bool(stripped) and stripped != name
    if stripped:
        name = stripped
    # `ch_prop_ch_vault_green_05` repeats its DLC tag on both sides of `prop_`. Dropping
    # the echo turns "Ch Vault Green" into "Vault Green" — the difference between a name
    # that gets flagged as machine noise and one worth saying.
    #
    # Only when the prefix actually matched: `02gate3_l` has no prefix to echo, and
    # stripping "02gate3_" from it left the single letter "L".
    if echoed and name.startswith(lead + "_") and len(name) > len(lead) + 1:
        name = name[len(lead) + 1:]
    # Repeatedly: `prop_parasol_01_a` carries two markers and stripping one left
    # "Parasol 01". Guard against eating the whole name.
    while True:
        trimmed = VARIANT_SUFFIX.sub("", name)
        if not trimmed or trimmed == name:
            break
        name = trimmed
    glued = GLUED_VARIANT.sub("", name)
    if glued:
        name = glued
    # Longest-first so "shop_high" beats "shop".
    for token in sorted(WORD_FIXES, key=len, reverse=True):
        if token in name:
            name = name.replace(token, " " + WORD_FIXES[token] + " ")
            break
    name = re.sub(r"[_\-]+", " ", name)
    # Interior variant numbers: `prop_table_03_chr` -> "Table Chr". A bare number in the
    # middle of a spoken name is never information, only a model id leaking through.
    parts = [t for t in name.split() if not t.isdigit()]
    if parts:
        name = " ".join(parts)
    name = re.sub(r"\s+", " ", name).strip()
    if not name:
        name = model.lower().replace("_", " ").strip()
    return titlecase(name)


def ped_head_noun(model):
    """"young man" / "older woman" / "man", from the sex and age the model id encodes.

    Returns "" for anything that is not an ambient/scripted/gang/unique ped — animals and
    story characters have no such id and get their proper name instead.
    """
    g = PED_SEX_AGE.match(model.lower())
    if not g:
        return ""
    noun = "woman" if g.group(1) == "f" else "man"
    age = AGE_WORD.get(g.group(2), "")
    return (age + " " + noun).strip()


def derive_ped_name(model, director=""):
    """Ped model -> (spoken name, sayable).

    Shape, in priority order:
      occupation           `s_m_y_barman_01`      -> "young barman"
      head noun + zone     `a_f_m_downtown_01`    -> "woman, downtown"
      head noun alone      `a_m_m_afriamer_01`    -> "man"
      proper name          `cs_ashley`            -> "Ashley"

    An occupation REPLACES the head noun rather than stacking with it — "young barman"
    already says everything "young man, barman" would. A director/zone label survives as
    a trailing appositive, which is what makes "woman, on the promenade" read as a person
    in a place rather than as a place.
    """
    m = model.lower()
    core = PED_PREFIX.sub("", m, count=1) or m
    core = VARIANT_SUFFIX.sub("", core) or core
    head = ped_head_noun(m)
    g = PED_SEX_AGE.match(m)
    age = AGE_WORD.get(g.group(2), "") if g else ""

    def ok(name):
        """Sayability is judged on the FINAL string, always. Composing a name from
        trusted parts does not make the result sayable — "Man, oc balla boss" is built
        entirely from trusted parts and is still noise."""
        return name, structurally_sayable(name)

    # Appearance-only ids: say the head noun and stop. Announcing a stranger's apparent
    # ethnicity tells a blind player nothing they can navigate by, and the model id is a
    # poor guide to it anyway.
    if core in DEMOGRAPHIC_ONLY:
        return ok(titlecase(head or "person"))

    # An occupation the id names outright. Longest key first so "shop_high" beats "shop".
    for token in sorted(WORD_FIXES, key=len, reverse=True):
        if token in core:
            role = WORD_FIXES[token]
            # Only prepend the age when the role is a person rather than a place-ish
            # label ("young barman" yes; "young beverly hills" no).
            if role in ZONE_WORDS:
                return ok(titlecase(head) + ", " + role) if head else ok(titlecase(role))
            if age:
                return ok(titlecase(age + " " + role))
            return ok(titlecase(role))

    if head:
        # What the director label MEANS depends on the archetype letter:
        #
        #   s_  scripted/service ped — the label is the job ("LSPD", "NOOSE",
        #       "Paramedic"). It replaces the head noun; "young man, lspd" buries the
        #       one word that matters behind two that do not.
        #   g_  gang ped — the label is the set ("East LS Balla"). When it does not
        #       survive being read out, fall through to flagged so the runtime says
        #       "gang member", which beats a bare "young man".
        #   a_/u_  ambient — the label is a zone or a wardrobe note ("Downtown",
        #       "On the Promenade", "Suited"), so it trails as an appositive.
        if director:
            if m.startswith("s_"):
                return ok(titlecase(director))
            composed = titlecase(head) + ", " + director.lower()
            if structurally_sayable(composed):
                return composed, True
            if m.startswith("g_"):
                return composed, False
        elif m.startswith("g_"):
            return titlecase(head), False
        return ok(titlecase(head))

    # Story/cutscene/animal: the stem is usually the character's own name.
    pretty = prettify(model, PED_PREFIX)
    if not structurally_sayable(pretty):
        return pretty, False
    return pretty, (core in STORY_NAMES or len(pretty.split()) > 1)


def is_door_like(model):
    """Same test the door scan used against hashes.txt, kept deliberately identical so
    NumPad 5 does not silently lose entries it used to find. At runtime ModelNames asks
    the door system first and only falls back to this flag."""
    m = model.lower()
    return "door" in m or "gate" in m


# ---------------------------------------------------------------------------
# Fetch
# ---------------------------------------------------------------------------

def fetch(name, refresh):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    if os.path.exists(path) and not refresh:
        age = (datetime.datetime.now().timestamp() - os.path.getmtime(path)) / 86400
        print(f"  cached {name} ({os.path.getsize(path)/1024:.0f} KB, age {age:.0f} days;"
              f" --refresh to re-download)")
    else:
        url = RAW_BASE + name
        print(f"  downloading {name}")
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "gta11y-build"})
            with urllib.request.urlopen(req, timeout=180) as r:
                data = r.read()
            with open(path, "wb") as fh:
                fh.write(data)
            print(f"    {len(data)/1024:.0f} KB")
        except Exception as exc:
            print(f"  ERROR downloading {name}: {exc}")
            if os.path.exists(path):
                print(f"  keeping existing cached copy of {name}")
            else:
                raise
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def src_meta(name):
    path = os.path.join(CACHE, name) if name in SOURCES else LEGACY
    with open(path, "rb") as fh:
        data = fh.read()
    return {"sha256": hashlib.sha256(data).hexdigest()[:12], "bytes": len(data)}


# ---------------------------------------------------------------------------
# Parsers
# ---------------------------------------------------------------------------

def parse_peds(text):
    """peds.json -> {hash: (name, gxtKey, model, flags)}.

    A director name is Rockstar's own human-facing string, so it is always trusted — but
    for an AMBIENT ped it is a zone or a wardrobe note ("On the Promenade", "Suited"),
    not a person. Those become an appositive on the head noun the model id gives us; a
    proper name (animals, the director-mode roster) stands alone.
    """
    rows = json.loads(text)
    out = {}
    named, derived, flagged = 0, 0, 0
    for r in rows:
        model = (r.get("Name") or "").strip()
        if not model:
            continue
        h = r.get("SignedHash", r.get("Hash"))
        h = to_signed32(h) if h is not None else joaat(model)
        director = r.get("TranslatedDirectorName") or {}
        gxt = (director.get("Name") or "").strip()
        english = titlecase((director.get("English") or "").strip())

        if english and not PED_SEX_AGE.match(model.lower()):
            # Animals and the story roster: the director name IS the name.
            name, flag, named = english, "", named + 1
        else:
            # Ambient: build from the id, keeping the director label as context.
            name, sayable = derive_ped_name(model, english)
            flag = "" if sayable else "m"
            if english:
                named += 1
            else:
                derived += 1
            if flag:
                flagged += 1
            # The GXT key resolves to the director label alone, which is exactly the
            # bare zone word this iteration stopped speaking — so it must not be
            # allowed to win over the composed name at runtime.
            gxt = ""
        out[h] = (name, gxt, model, flag)
    return out, named, derived, flagged


def parse_name_list(text):
    """ObjectList.ini / VehicleList.ini. Both are one entry per line; ObjectList is bare
    names, VehicleList is Name=SignedHash. Handles either."""
    out = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith((";", "#", "[")):
            continue
        if "=" in line:
            model, _, raw = line.partition("=")
            model = model.strip()
            try:
                h = to_signed32(raw.strip())
            except ValueError:
                h = joaat(model)
        else:
            model, h = line, joaat(line)
        if model:
            out[h] = model
    return out


def parse_legacy(path):
    """hashes.txt: `displayName=signedHash`. The names are NOT reliable joaat preimages —
    someone relabelled parts of it by hand (`boar=-832573324`, but joaat("boar") is
    -298581576; that hash is really a_c_boar). Only the hash column is authoritative, so
    the name is treated as a display string and never re-hashed."""
    out = {}
    malformed = 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            name, sep, raw = line.strip().partition("=")
            if not sep or not name:
                if line.strip():
                    malformed += 1
                continue
            try:
                out[to_signed32(raw)] = name
            except ValueError:
                malformed += 1
    return out, malformed


# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

def build(args):
    print("sources")
    src = {name: fetch(name, args.refresh) for name in SOURCES}

    if not os.path.exists(LEGACY):
        sys.exit(f"FATAL missing {os.path.normpath(LEGACY)}. It is still a source - the "
                 f"dumps do not carry every map/door archetype it holds, and dropping it "
                 f"would lose coverage the mod has today.")
    print(f"  local hashes.txt ({os.path.getsize(LEGACY)/1024:.0f} KB)")

    print("checking joaat")
    for name, want in JOAAT_ANCHORS.items():
        got = joaat(name)
        if got != want:
            sys.exit(f"FATAL joaat('{name}') = {got}, expected {want}. Every name in the "
                     f"table would point at the wrong model.")
    print(f"  {len(JOAAT_ANCHORS)} anchors round-trip")

    print("parsing")
    peds, ped_named, ped_derived, ped_flagged = parse_peds(src["peds.json"])
    print(f"  peds.json: {len(peds)} peds "
          f"({ped_named} with a director name, {ped_derived} derived from the model name, "
          f"{ped_flagged} flagged unsayable)")

    objects = parse_name_list(src["ObjectList.ini"])
    print(f"  ObjectList.ini: {len(objects)} objects")

    vehicles = parse_name_list(src["VehicleList.ini"])
    print(f"  VehicleList.ini: {len(vehicles)} vehicles (cross-check only, no rows emitted)")

    legacy, malformed = parse_legacy(LEGACY)
    print(f"  hashes.txt: {len(legacy)} legacy rows"
          + (f", {malformed} malformed" if malformed else ""))

    # ---- merge, lowest priority first so later writes win ----------------
    # legacy < objects < peds. Peds last because the dumps are the only source with a
    # real human-readable name for a pedestrian, and the whole point of this table.
    table = {}                                   # hash -> [kind, name, extra, flags]

    def prop_flag(model, pretty):
        """A prop name is trusted only when the MODEL says it is something a player walks
        up to AND the prettified string survives being read out."""
        return "" if (PROP_FAMILY.match(model.lower())
                      and structurally_sayable(pretty)) else "m"

    for h, name in legacy.items():
        # A legacy row whose name still looks like a model id gets prettified; one that
        # was already relabelled by hand ("boar") is left as it is.
        kind = "d" if is_door_like(name) else ("o" if "_" in name else "x")
        if "_" in name:
            pretty = prettify(name, PROP_PREFIX)
            flag = prop_flag(name, pretty)
        else:
            pretty = titlecase(name)
            flag = "" if structurally_sayable(pretty) else "m"
        table[h] = [kind, pretty, "", flag]

    for h, model in objects.items():
        pretty = prettify(model, PROP_PREFIX)
        table[h] = ["d" if is_door_like(model) else "o",
                    pretty, "", prop_flag(model, pretty)]

    for h, (name, gxt, model, flag) in peds.items():
        table[h] = ["p", name, gxt, flag]

    # Vehicles must not be in here — the game names them itself, and a stale bundled
    # name would override a correct live one.
    collisions = sorted(set(table) & set(vehicles))
    for h in collisions:
        del table[h]
    if collisions:
        print(f"  dropped {len(collisions)} rows that collide with vehicle models "
              f"(the game names those itself)")

    # ---- overrides (LAST, so hand fixes survive regeneration) -------------
    n_over = 0
    if os.path.exists(OVERRIDES):
        with open(OVERRIDES, encoding="utf-8") as fh:
            ov = json.load(fh)
        for row in ov.get("models", []):
            model = row.get("model")
            h = row.get("hash", joaat(model) if model else None)
            if h is None:
                continue
            h = to_signed32(h)
            if row.get("status") == "refuted":
                if table.pop(h, None) is not None:
                    n_over += 1
                continue
            cur = table.get(h) or ["o", "", "", ""]
            # An explicit "gxt": null CLEARS the generator's key. Without that, an
            # override name for a ped was unreachable: DescribePed resolves the GXT
            # first, so the hand-written string only surfaced if the lookup failed.
            gxt = row["gxt"] if "gxt" in row else cur[2]
            table[h] = [row.get("kind", cur[0]),
                        row.get("name", cur[1]),
                        gxt or "",
                        ""]          # hand-written: trusted by definition
            n_over += 1
    print(f"overrides applied: {n_over} rows")

    # ---- coverage guard --------------------------------------------------
    # The one regression that would be invisible in play: a regenerated table that names
    # FEWER models than the file it replaces. Every legacy hash must survive EXCEPT the
    # vehicle rows dropped above — those are not lost, they moved to a better source
    # (the game itself), and --verify proves the mod still names them.
    lost = [h for h in legacy if h not in table and h not in vehicles]
    if lost:
        sys.exit(f"FATAL {len(lost)} hashes present in hashes.txt are missing from the "
                 f"new table (first: {lost[0]}). The mod would name fewer things than it "
                 f"does today.")
    blank = [h for h, v in table.items() if not v[1].strip()]
    if blank:
        sys.exit(f"FATAL {len(blank)} rows have a blank name (first hash: {blank[0]}). "
                 f"A blank reads as a gap in the sentence.")

    # ---- write -----------------------------------------------------------
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    kinds = {}
    for v in table.values():
        kinds[v[0]] = kinds.get(v[0], 0) + 1

    buf = io.StringIO()
    buf.write(f"#gta11y-modelnames v{SCHEMA_VERSION}\n")
    buf.write(f"#generated {now} by build-modelnames.py\n")
    buf.write("#corpus DurtyFree/gta-v-data-dumps@master + External Resources/hashes.txt\n")
    for n in SOURCES:
        m = src_meta(n)
        buf.write(f"#source {n} sha256={m['sha256']} bytes={m['bytes']}\n")
    m = src_meta("hashes.txt")
    buf.write(f"#source hashes.txt sha256={m['sha256']} bytes={m['bytes']}\n")
    flagged = sum(1 for v in table.values() if v[3])
    buf.write("#counts " + " ".join(f"{k}={kinds.get(k, 0)}" for k in "podx")
              + f" total={len(table)}"
              + f" sayable={len(table) - flagged} flagged={flagged}\n")
    buf.write("#columns kind\thash\tname\textra\tflags\n")
    for h in sorted(table):
        kind, name, extra, flags = table[h]
        buf.write(f"{kind}\t{h}\t{name}\t{extra}\t{flags}\n")

    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    with gzip.open(OUT, "wt", encoding="utf-8", newline="\n") as fh:
        fh.write(buf.getvalue())

    raw_kb = len(buf.getvalue().encode("utf-8")) / 1024
    print(f"wrote {os.path.normpath(OUT)} "
          f"({os.path.getsize(OUT)/1024:.0f} KB gzipped, {raw_kb:.0f} KB raw)")
    print(f"  peds={kinds.get('p',0)} objects={kinds.get('o',0)} doors={kinds.get('d',0)} "
          f"legacy-other={kinds.get('x',0)} total={len(table)}")
    print(f"  vs hashes.txt: {len(legacy)} -> {len(table)} rows "
          f"({len(table) - len(legacy):+d})")
    print(f"  sayable={len(table) - flagged} flagged={flagged} "
          f"({100.0 * flagged / max(1, len(table)):.0f}% will be described rather than named)")

    # ---- review file -----------------------------------------------------
    write_review(table, now, flagged)

    return table, legacy, peds, vehicles


def write_review(table, generated, flagged):
    """The worklist for tools/modelnames-overrides.json.

    Sorted by kind then name so near-duplicates ("Beach Parasol", "Parasol 01") land
    together — they usually want the same correction, and fixing them as a group is the
    only way this file ever gets shorter.
    """
    rows = [(v[0], v[1], h) for h, v in table.items() if v[3]]
    rows.sort(key=lambda r: (r[0], r[1].lower(), r[2]))
    with open(REVIEW, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# gta11y model names the generator would not vouch for out loud.\n")
        fh.write(f"# generated {generated} by build-modelnames.py -- DO NOT HAND-EDIT.\n")
        fh.write("#\n")
        fh.write("# These rows ship with the 'm' flag. The mod knows the model exists but\n")
        fh.write("# will NOT read this name: a ped falls back to its type and sex, a prop\n")
        fh.write("# to its size, a door to 'door'/'gate'. Nothing here is a missing model --\n")
        fh.write("# for those see gta11y-modelgaps-<date>.log at the GTA V root.\n")
        fh.write("#\n")
        fh.write("# To fix one, add it to tools/modelnames-overrides.json with a name and\n")
        fh.write("# evidence; overrides are merged last and are trusted by definition.\n")
        fh.write(f"# {flagged} rows\n")
        fh.write("kind\thash\tderived name\n")
        for kind, name, h in rows:
            fh.write(f"{kind}\t{h}\t{name}\n")
    print(f"  review list: {os.path.normpath(REVIEW)} ({flagged} rows)")


def verify(table, legacy, peds, vehicles):
    """Checks that a passing build does not guarantee. Exit code is the result."""
    print("\nverify")
    bad = 0

    moved = [h for h in legacy if h in vehicles]
    missing = [h for h in legacy if h not in table and h not in vehicles]
    print(f"  legacy coverage: {len(legacy) - len(missing) - len(moved)}/{len(legacy)} "
          f"retained, {len(moved)} handed to the game's own vehicle naming")
    if missing:
        print(f"    FAIL {len(missing)} lost"); bad += 1

    ped_missing = [h for h in peds if h not in table]
    print(f"  ped coverage: {len(peds) - len(ped_missing)}/{len(peds)} present")
    if ped_missing:
        print(f"    FAIL {len(ped_missing)} peds absent"); bad += 1

    overlap = set(table) & set(vehicles)
    print(f"  vehicle rows emitted: {len(overlap)} (must be 0 - the game names vehicles)")
    if overlap:
        print(f"    FAIL"); bad += 1

    for name, (want_kind, h) in TABLE_ANCHORS.items():
        hit = table.get(h)
        sign = "negative" if h < 0 else "positive"
        if not hit:
            print(f"  {name} ({sign} {h}): MISSING"); bad += 1
        elif hit[0] != want_kind:
            print(f'  {name} ({sign} {h}): "{hit[1]}" kind [{hit[0]}], expected '
                  f"[{want_kind}]"); bad += 1
        else:
            print(f'  {name} ({sign} {h}): "{hit[1]}" [{hit[0]}]')

    adder = JOAAT_ANCHORS["adder"]
    print(f"  adder (negative {adder}) absent, named by the game: "
          f"{'yes' if adder not in table else 'NO — the table is shadowing it'}")
    if adder in table:
        bad += 1

    blank = [h for h, v in table.items() if not v[1].strip()]
    print(f"  blank names: {len(blank)}")
    if blank:
        bad += 1

    ungated_gxt = [h for h, v in table.items() if v[2] and v[0] != "p"]
    print(f"  non-ped rows carrying a GXT key: {len(ungated_gxt)} (must be 0)")
    if ungated_gxt:
        bad += 1

    # iter-60: nothing unflagged may still look like a machine name. This is the check
    # that keeps the sayability rules honest as WORD_FIXES and the overrides grow —
    # loosening a rule without meaning to shows up here rather than in the player's ear.
    leaked = [(h, v) for h, v in table.items()
              if not v[3] and not structurally_sayable(v[1])]
    print(f"  unflagged rows that still read as machine names: {len(leaked)} (must be 0)")
    if leaked:
        for h, v in leaked[:5]:
            print(f"    {h} [{v[0]}] \"{v[1]}\"")
        bad += 1

    flagged = sum(1 for v in table.values() if v[3])
    say = len(table) - flagged
    print(f"  sayable rows: {say}/{len(table)} ({100.0 * say / max(1, len(table)):.0f}%); "
          f"{flagged} described rather than named")

    print("verify: " + ("PASS" if bad == 0 else f"FAIL ({bad} checks)"))
    return bad


API_BASE = "https://api.github.com/repos/DurtyFree/gta-v-data-dumps/commits"


def table_generated_stamp():
    """The `#generated` line out of the shipped table, or None if there is no table."""
    if not os.path.exists(OUT):
        return None
    try:
        with gzip.open(OUT, "rt", encoding="utf-8") as fh:
            for line in fh:
                if not line.startswith("#"):
                    break
                if line.startswith("#generated "):
                    return line[len("#generated "):].split(" by ")[0].strip()
    except Exception:
        return None
    return None


def check():
    """Has a DLC landed since this table was built?

    Rockstar ships a GTA Online DLC roughly twice a year and DurtyFree re-dumps within
    days, naming the build in the commit subject ("... mp2025_02 / b3717 / A Safehouse
    In The Hills"). That subject is the only machine-readable statement of which game
    version this data describes — the repo carries no version file — so it is what gets
    reported here.

    Read-only: no download, no auth, no cache write. Exit 1 when stale so a scheduled
    caller can act on it.
    """
    stamp = table_generated_stamp()
    if not stamp:
        print(f"no table at {os.path.normpath(OUT)} - run without --check to build one.")
        return 1
    print(f"table generated {stamp}")

    try:
        built = datetime.datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=datetime.timezone.utc)
    except ValueError:
        print(f"  cannot parse that stamp; treating the table as stale.")
        return 1

    stale, failed = [], []
    for name in SOURCES:
        url = f"{API_BASE}?path={name}&per_page=1"
        try:
            req = urllib.request.Request(
                url, headers={"User-Agent": "gta11y-build",
                              "Accept": "application/vnd.github+json"})
            with urllib.request.urlopen(req, timeout=60) as r:
                commits = json.loads(r.read().decode("utf-8"))
        except Exception as exc:
            # An unreachable API is not evidence of freshness. Say so rather than
            # printing a clean bill of health nobody checked.
            print(f"  {name}: CHECK FAILED ({exc})")
            failed.append(name)
            continue
        if not commits:
            print(f"  {name}: no commits reported")
            continue
        c = commits[0]["commit"]
        when = c["committer"]["date"]
        subject = c["message"].split("\n")[0].strip()
        try:
            upstream = datetime.datetime.strptime(
                when, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
        except ValueError:
            upstream = None
        mark = "STALE" if (upstream and upstream > built) else "current"
        if mark == "STALE":
            stale.append(name)
        print(f"  {name}: {mark} - upstream {when}")
        print(f"      {subject}")

    if failed:
        print(f"\nCOULD NOT CHECK {len(failed)} source(s): {', '.join(failed)}")
        return 1
    if stale:
        print(f"\nSTALE: {', '.join(stale)} moved since this table was built.")
        print("Regenerate with:  python tools\\build-modelnames.py --refresh --verify")
        return 1
    print("\nup to date - no regeneration needed.")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    ap.add_argument("--refresh", action="store_true",
                    help="re-download the dumps instead of using tools/_cache/modelnames")
    ap.add_argument("--verify", action="store_true",
                    help="run the post-build checks and exit non-zero on failure")
    ap.add_argument("--check", action="store_true",
                    help="ask GitHub whether a DLC dump landed after this table was "
                         "generated, and exit non-zero if so. Builds nothing.")
    args = ap.parse_args()

    if args.check:
        sys.exit(check())

    table, legacy, peds, vehicles = build(args)
    if args.verify:
        sys.exit(1 if verify(table, legacy, peds, vehicles) else 0)


if __name__ == "__main__":
    main()
