using System;
using System.Collections.Generic;
using System.IO;
using GTA.Math;
using Newtonsoft.Json;

namespace GTA
{
    /// <summary>
    /// Static world knowledge harvested from Rockstar's own decompiled scripts and
    /// shipped as scripts/gta11y-worlddata.json (see tools/build-worlddata.py).
    ///
    /// This is the data no native can tell you at runtime: where the 50 letter scraps
    /// and 50 spaceship parts are, the 135 underwater loot caches, the angled boxes
    /// that hand you a wanted level for trespassing, the Strangers &amp; Freaks trigger
    /// points, which props are interactable, and the random-event id registry. All of
    /// it is content a sighted player finds by looking at the map or the screen.
    ///
    /// Loaded once at startup. Every lookup is O(N) over small static lists, except
    /// interactions which are hash-indexed because interactable detection calls them
    /// per scanned entity. Follows MapDb's contract exactly: a missing or broken file
    /// leaves the DB empty and every lookup returns a null/empty fallback, so the mod
    /// keeps working with fewer features rather than failing to start.
    /// </summary>
    public static class WorldDataDb
    {
        // ===================================================================
        // Row types (Newtonsoft populates these by reflection)
        // ===================================================================

        public class Collectible
        {
            public string set;        // "letterscraps" | "spaceshipparts" | "underwaterpickups"
            public int index;         // 0..49 for the two hidden-package sets
            public int variant;       // 0/1 — some indices carry an interior/exterior pair
            public float x, y, z;
            public string statSet;    // packed-stat family, or null if the set is not scored
            public string gxtTitle;
            public string pickup;     // underwater sets only: pickup_weapon_smg etc.
            public int site;          // underwater sets only: dive-site group

            public Vector3 Position { get { return new Vector3(x, y, z); } }
        }

        public class Box
        {
            public float[] a;         // corner A (x, y, z)
            public float[] b;         // corner B — b[2] may be null when parameterised
            public float width;
            public float? ceilingBase;
        }

        public class Area
        {
            public string id;
            public int group;
            public string name;       // null when the harvester could not identify it
            public string gxt;
            public int wanted;
            public bool named;
            public List<Box> boxes;

            // Precomputed 2D bounds, filled at load. Lets callers reject the 7 areas
            // you are nowhere near without paying IS_POINT_IN_ANGLED_AREA per box —
            // this runs every tick when restrictedAreaWarn is on.
            internal float minX, minY, maxX, maxY;
        }

        public class Place
        {
            public string kind;       // "stranger" for now
            public string id;
            public string name;
            public string gxt;
            public float x, y, z;
            public int? blip;
            public string status;     // "verified" | "assumed"

            public Vector3 Position { get { return new Vector3(x, y, z); } }
        }

        public class Interaction
        {
            public string kind;       // spoken noun: "vending machine"
            public string verb;       // spoken verb phrase: "buy a drink or snack"
            public string script;
            public string model;
            public int hash;
            public List<string> animDicts;
        }

        public class RandomEvent
        {
            public int id;            // engine random-event id; sparse (8 is unused)
            public string script;     // "re_gangfight"
            public string gxt;        // "RE_GANGFIGHT"
        }

        public class DispatchService
        {
            public int service;       // 7 police, 5 ambulance, 3 fire
            public string id;
            public string name;
            public int units;
            public float radius;
        }

        // ===================================================================
        // State
        // ===================================================================

        private static List<Collectible> collectibles = new List<Collectible>();
        private static List<Area> areas = new List<Area>();
        private static List<Place> places = new List<Place>();
        private static List<RandomEvent> randomEvents = new List<RandomEvent>();
        private static List<DispatchService> dispatch = new List<DispatchService>();
        private static Dictionary<int, Interaction> interactionsByHash
            = new Dictionary<int, Interaction>();
        private static readonly List<string> collectibleSets = new List<string>();

        private static bool loaded = false;
        private static string loadStatus = "notLoaded";

        public static bool IsLoaded { get { return loaded && loadStatus == "ok"; } }
        public static string LoadStatus { get { return loadStatus; } }
        public static int CollectibleCount { get { return collectibles.Count; } }
        public static int AreaCount { get { return areas.Count; } }
        public static int PlaceCount { get { return places.Count; } }
        public static int InteractionCount { get { return interactionsByHash.Count; } }
        public static int RandomEventCount { get { return randomEvents.Count; } }
        public static IList<Area> Areas { get { return areas; } }
        public static IList<Place> Places { get { return places; } }
        public static IList<RandomEvent> RandomEvents { get { return randomEvents; } }
        public static IList<DispatchService> Dispatch { get { return dispatch; } }
        public static IList<string> CollectibleSets { get { return collectibleSets; } }

        /// <summary>Loads scripts/gta11y-worlddata.json. Safe to call repeatedly.
        /// On failure the DB stays empty and every lookup returns a fallback, matching
        /// MapDb.Load's contract — a data problem must never stop the mod starting,
        /// because the mod IS the user's ability to play.</summary>
        public static void Load()
        {
            if (loaded) return;
            string path = "scripts/gta11y-worlddata.json";
            try
            {
                if (!File.Exists(path))
                {
                    loadStatus = "fileNotFound";
                    loaded = true;
                    return;
                }
                var root = JsonConvert.DeserializeObject<WorldDataRoot>(File.ReadAllText(path));
                if (root == null)
                {
                    loadStatus = "nullData";
                    loaded = true;
                    return;
                }

                if (root.collectibles != null) collectibles = root.collectibles;
                if (root.areas != null) areas = root.areas;
                if (root.places != null) places = root.places;
                if (root.randomEvents != null) randomEvents = root.randomEvents;
                if (root.dispatch != null) dispatch = root.dispatch;

                if (root.interactions != null)
                {
                    foreach (var i in root.interactions)
                    {
                        if (i == null) continue;
                        interactionsByHash[i.hash] = i;   // last wins; duplicates are benign
                    }
                }

                foreach (var c in collectibles)
                {
                    if (c != null && c.set != null && !collectibleSets.Contains(c.set))
                        collectibleSets.Add(c.set);
                }

                PrecomputeAreaBounds();
                loadStatus = "ok";
                loaded = true;
            }
            catch (Exception ex)
            {
                // Same reasoning as MapDb: record WHY, because a parse error and a
                // missing file are otherwise indistinguishable in the log.
                loadStatus = MapDb.FmtLoadError(ex);
                loaded = true;
            }
        }

        private static void PrecomputeAreaBounds()
        {
            foreach (var a in areas)
            {
                if (a == null || a.boxes == null || a.boxes.Count == 0) continue;
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var b in a.boxes)
                {
                    if (b == null || b.a == null || b.b == null) continue;
                    if (b.a.Length < 2 || b.b.Length < 2) continue;
                    // The angled area is a box of `width` about the A-B axis, so the
                    // half-width has to be added on both axes or the prefilter would
                    // reject points that ARE inside — a false all-clear on a hazard
                    // warning is the one failure mode that must not happen here.
                    float half = b.width * 0.5f;
                    minX = System.Math.Min(minX, System.Math.Min(b.a[0], b.b[0]) - half);
                    maxX = System.Math.Max(maxX, System.Math.Max(b.a[0], b.b[0]) + half);
                    minY = System.Math.Min(minY, System.Math.Min(b.a[1], b.b[1]) - half);
                    maxY = System.Math.Max(maxY, System.Math.Max(b.a[1], b.b[1]) + half);
                }
                a.minX = minX; a.maxX = maxX; a.minY = minY; a.maxY = maxY;
            }
        }

        private class WorldDataRoot
        {
            public int version { get; set; }
            public List<Collectible> collectibles { get; set; }
            public List<Area> areas { get; set; }
            public List<Place> places { get; set; }
            public List<Interaction> interactions { get; set; }
            public List<RandomEvent> randomEvents { get; set; }
            public List<DispatchService> dispatch { get; set; }
        }

        // ===================================================================
        // Lookups
        // ===================================================================

        /// <summary>Areas whose 2D bounds contain the point expanded by `margin` metres.
        /// A cheap prefilter so the caller only pays IS_POINT_IN_ANGLED_AREA for the
        /// one or two areas that could possibly match.</summary>
        public static void CandidateAreas(Vector3 pos, float margin, List<Area> into)
        {
            into.Clear();
            foreach (var a in areas)
            {
                if (a == null || a.boxes == null || a.boxes.Count == 0) continue;
                if (pos.X < a.minX - margin || pos.X > a.maxX + margin) continue;
                if (pos.Y < a.minY - margin || pos.Y > a.maxY + margin) continue;
                into.Add(a);
            }
        }

        /// <summary>The interaction catalogue entry for a model hash, or null. Called
        /// per scanned entity, hence the dictionary.</summary>
        public static Interaction InteractionFor(int modelHash)
        {
            Interaction i;
            return interactionsByHash.TryGetValue(modelHash, out i) ? i : null;
        }

        public static RandomEvent RandomEventById(int id)
        {
            foreach (var e in randomEvents) if (e != null && e.id == id) return e;
            return null;
        }

        public static DispatchService DispatchById(string id)
        {
            foreach (var d in dispatch)
                if (d != null && string.Equals(d.id, id, StringComparison.Ordinal)) return d;
            return null;
        }

        /// <summary>Distinct collectible indices in a set, ascending. The caller pairs
        /// these with the packed stat to work out which are still outstanding.</summary>
        public static List<int> IndicesIn(string set)
        {
            var seen = new List<int>();
            foreach (var c in collectibles)
            {
                if (c == null || c.set != set) continue;
                if (!seen.Contains(c.index)) seen.Add(c.index);
            }
            seen.Sort();
            return seen;
        }

        /// <summary>The position of collectible `index` in `set` that is nearest to
        /// `from`, and its distance.
        ///
        /// Some indices carry two coordinates — Rockstar's dispatch returns a different
        /// one depending on an undocumented second argument, and which is "the" position
        /// is genuinely ambiguous (one is often an interior or underwater variant). Both
        /// describe the SAME collectible, so returning whichever is nearer is correct
        /// under either reading, and sidesteps having to guess. Guessing wrong would
        /// route a blind player into solid geometry.</summary>
        public static bool TryNearestVariant(string set, int index, Vector3 from,
                                             out Vector3 pos, out float dist)
        {
            pos = Vector3.Zero;
            dist = float.MaxValue;
            bool found = false;
            foreach (var c in collectibles)
            {
                if (c == null || c.set != set || c.index != index) continue;
                Vector3 p = c.Position;
                float dx = p.X - from.X, dy = p.Y - from.Y, dz = p.Z - from.Z;
                float d = (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (!found || d < dist) { pos = p; dist = d; found = true; }
            }
            return found;
        }

        /// <summary>Every row in a set, in file order. Used by the underwater caches,
        /// where each row is its own pickup rather than a variant of one.</summary>
        public static List<Collectible> RowsIn(string set)
        {
            var outRows = new List<Collectible>();
            foreach (var c in collectibles)
                if (c != null && c.set == set) outRows.Add(c);
            return outRows;
        }

        /// <summary>Spoken name for an area: the harvested name when it is known,
        /// otherwise null so the caller can fall back to GET_NAME_OF_ZONE. Deliberately
        /// does not invent a name — a wrong label on a hazard warning is worse than a
        /// vague one.</summary>
        public static string AreaDisplayName(Area a)
        {
            if (a == null) return null;
            string gxt = MenuLabelDb.ResolveGxt(a.gxt);
            if (!string.IsNullOrEmpty(gxt)) return gxt;
            return string.IsNullOrEmpty(a.name) ? null : a.name;
        }

        /// <summary>One-line census for the startup log line.</summary>
        public static string Census()
        {
            return "status=" + loadStatus
                 + " collectibles=" + collectibles.Count
                 + " sets=" + collectibleSets.Count
                 + " areas=" + areas.Count
                 + " places=" + places.Count
                 + " interactions=" + interactionsByHash.Count
                 + " randomEvents=" + randomEvents.Count
                 + " dispatch=" + dispatch.Count
                 + " file=" + MapDb.FmtFileInfo("scripts/gta11y-worlddata.json");
        }
    }
}
