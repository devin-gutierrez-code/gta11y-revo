using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using GTA;
using GTA.Math;
using Newtonsoft.Json;

namespace GTA
{
    /// <summary>
    /// In-memory static map data shipped as scripts/gta11y-map.json. Provides
    /// road-type classification (freeway/highway/surface/alley) and POI lookup
    /// (gas stations, garages) for drive-assist features that need world context.
    ///
    /// Loaded once at startup from the generated JSON (see tools/build-map-data.py).
    /// All lookups are O(N) over small static lists (~225 highways, ~190 services)
    /// with single-call caching to amortize per-tick callers.
    /// </summary>
    public static class MapDb
    {
        public class Highway
        {
            public string name;
            public string type; // "freeway" | "highway" | "surface" | "alley"
            public float[][] polygon; // [[x, y], ...]
        }

        public class Service
        {
            public string kind; // "gas" | "garage" | "lot"
            public float x, y, z;
            public string name;
        }

        private static List<Highway> highways = new List<Highway>();
        private static List<Service> services = new List<Service>();
        private static bool loaded = false;
        private static string loadStatus = "notLoaded";
        public static bool IsLoaded { get { return loaded; } }
        public static string LoadStatus { get { return loadStatus; } }
        public static int HighwayCount { get { return highways.Count; } }
        public static int ServiceCount { get { return services.Count; } }

        /// <summary>Formats a load-failure exception into a single space-free
        /// token so it can live inside a space-delimited log line.</summary>
        internal static string FmtLoadError(Exception ex)
        {
            string msg = ex.Message ?? "";
            if (msg.Length > 60) msg = msg.Substring(0, 60);
            return ex.GetType().Name + ":" + msg.Replace(' ', '_').Replace('\r', '_').Replace('\n', '_');
        }

        /// <summary>size+mtime token for a data file, or "missing".</summary>
        internal static string FmtFileInfo(string path)
        {
            try
            {
                if (!File.Exists(path)) return "missing";
                var fi = new FileInfo(path);
                return fi.Length + "B@" + fi.LastWriteTime.ToString("yyyy-MM-dd_HH:mm");
            }
            catch { return "statFailed"; }
        }

        /// <summary>Loads scripts/gta11y-map.json. Safe to call repeatedly; no-op
        /// once loaded. On failure, MapDb stays empty and all lookups return
        /// fallback values (so the mod still works without the data file).</summary>
        public static void Load()
        {
            if (loaded) return;
            string path = "scripts/gta11y-map.json";
            try
            {
                if (!File.Exists(path))
                {
                    // Not an error — mod works without map data, just with fewer
                    // features. Caller (GTA11Y constructor) chooses whether to
                    // surface this via Tolk.
                    loadStatus = "fileNotFound";
                    loaded = true;
                    return;
                }
                string json = File.ReadAllText(path);
                var root = JsonConvert.DeserializeObject<MapJsonRoot>(json);
                if (root != null)
                {
                    if (root.highways != null) highways = root.highways;
                    if (root.services != null) services = root.services;
                    loadStatus = "ok";
                }
                else
                {
                    loadStatus = "nullData";
                }
                loaded = true;
            }
            catch (Exception ex)
            {
                // Fallback must never crash the mod, but record WHY it failed
                // so the map-data banner can print it (a parse error and a
                // missing file were previously indistinguishable in the log).
                loadStatus = FmtLoadError(ex);
                loaded = true;
            }
        }

        // Newtonsoft populates these via reflection; using auto-properties (vs.
        // public fields) avoids the spurious "never assigned" compiler warning.
        private class MapJsonRoot
        {
            public int version { get; set; }
            public List<Highway> highways { get; set; }
            public List<Service> services { get; set; }
        }

        // ===================================================================
        // Road-type classification — point-in-polygon over the named highways
        // ===================================================================

        // Cache: most callers (lane keeping, autodrive announcements) repeat the
        // same query each tick from nearby positions. Skip the polygon scan if
        // the player hasn't moved more than 3 m since the last lookup.
        private static Vector3 cachedRoadPos = Vector3.Zero;
        private static string cachedRoadType = "unknown";
        private static string cachedRoadName = "";

        /// <summary>Returns the type of road containing this position, or
        /// "unknown" if not inside any classified highway polygon.</summary>
        public static string GetRoadTypeAt(Vector3 pos)
        {
            GetRoadInfoAt(pos, out string name);
            return cachedRoadType;
        }

        /// <summary>Returns the name of the road at this position, or empty
        /// string if not inside any classified polygon.</summary>
        public static string GetRoadNameAt(Vector3 pos)
        {
            GetRoadInfoAt(pos, out string name);
            return name;
        }

        public static void GetRoadInfoAt(Vector3 pos, out string name)
        {
            // 3m positional cache — drive-assist tick can call this multiple times
            // per frame across nearby positions.
            if ((pos - cachedRoadPos).LengthSquared() < 9f && cachedRoadType != "unknown")
            {
                name = cachedRoadName;
                return;
            }
            cachedRoadPos = pos;
            cachedRoadType = "unknown";
            cachedRoadName = "";

            for (int i = 0; i < highways.Count; i++)
            {
                Highway h = highways[i];
                if (h.polygon == null || h.polygon.Length < 3) continue;
                if (PointInPolygon(pos.X, pos.Y, h.polygon))
                {
                    cachedRoadType = h.type;
                    cachedRoadName = h.name ?? "";
                    name = cachedRoadName;
                    return;
                }
            }
            name = "";
        }

        // Ray-cast point-in-polygon. Standard algorithm, works on the 2D footprint.
        private static bool PointInPolygon(float x, float y, float[][] poly)
        {
            bool inside = false;
            int j = poly.Length - 1;
            for (int i = 0; i < poly.Length; i++)
            {
                float[] pi = poly[i];
                float[] pj = poly[j];
                if (pi.Length < 2 || pj.Length < 2) { j = i; continue; }
                if (((pi[1] > y) != (pj[1] > y)) &&
                    (x < (pj[0] - pi[0]) * (y - pi[1]) / (pj[1] - pi[1] + 1e-9f) + pi[0]))
                {
                    inside = !inside;
                }
                j = i;
            }
            return inside;
        }

        // ===================================================================
        // Service POI lookup
        // ===================================================================

        /// <summary>Returns the nearest service of the given kind within maxDist
        /// meters of pos, or null. Uses 3D distance.</summary>
        public static Service FindNearestService(Vector3 pos, string kind, float maxDist)
        {
            Service best = null;
            float bestSq = maxDist * maxDist;
            for (int i = 0; i < services.Count; i++)
            {
                Service s = services[i];
                if (s.kind != kind) continue;
                float dx = s.x - pos.X;
                float dy = s.y - pos.Y;
                float dz = s.z - pos.Z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>Tiered best-parking lookup: garage > lot > gas > null.
        /// Pass a comfortable maxDist (~250m) so a user near any service finds
        /// one without snapping to one across the map.</summary>
        public static Service FindBestParkingService(Vector3 pos, float maxDist)
        {
            Service s = FindNearestService(pos, "garage", maxDist);
            if (s != null) return s;
            s = FindNearestService(pos, "lot", maxDist);
            if (s != null) return s;
            s = FindNearestService(pos, "gas", maxDist);
            return s;
        }
    }

    /// <summary>
    /// Complete static dump of the GTA V vehicle path-node graph (67k+ nodes,
    /// 145k links). Loaded once at startup from scripts/gta11y-nodes.json.gz
    /// (produced by tools/build-map-data.py from DurtyFree/gta-v-data-dumps).
    ///
    /// This is the primary source of road-graph topology for the drive assist:
    /// SHVDN 3.6.0 does NOT ship a managed PathFind/PathNode API, and the raw
    /// natives do not expose per-node-id link enumeration. Without this offline
    /// graph we cannot walk neighbours — we can only sample the nearest node at
    /// a coord, which is exactly the noisy behaviour the previous iteration
    /// failed at.
    ///
    /// Use the runtime natives (GET_VEHICLE_NODE_PROPERTIES, IS_POINT_ON_ROAD,
    /// GET_POS_ALONG_GPS_TYPE_ROUTE) for live state — but use NodeGraph for
    /// "what connects to what" and for off-road recovery, since this dump has
    /// every node whether the streaming region currently has it or not.
    /// </summary>
    public static class NodeGraph
    {
        [Flags]
        public enum NodeFlags
        {
            None              = 0,
            ValidForGps       = 1 << 0,
            Junction          = 1 << 1,
            Freeway           = 1 << 2,
            GravelRoad        = 1 << 3,
            Backroad          = 1 << 4,
            OnWater           = 1 << 5,
            PedCrossway       = 1 << 6,
            TrafficLight      = 1 << 7,
            LeftTurnNoReturn  = 1 << 8,
            RightTurnNoReturn = 1 << 9,
        }

        /// <summary>Extended per-node flags joined from the CodeWalker
        /// paths.xml extraction (schema v2 `e` field). Different provenance
        /// than NodeFlags: these are source-scene attributes position-matched
        /// onto the runtime graph; absence means "unknown", so consumers must
        /// treat 0 as "behave as before", never as a positive assertion.
        /// Bit values must match EXT_FLAG_BITS in tools/build-map-data.py.</summary>
        [Flags]
        public enum ExtFlags
        {
            None              = 0,
            Disabled          = 1 << 0,
            DontUseForNav     = 1 << 1,
            NoGps             = 1 << 2,
            HighwaySrc        = 1 << 3,
            OffRoad           = 1 << 4,
            WaterSrc          = 1 << 5,
            CannotGoLeft      = 1 << 6,
            CannotGoRight     = 1 << 7,
            LeftTurnsOnly     = 1 << 8,
            SlipLane          = 1 << 9,
            IndicateKeepLeft  = 1 << 10,
            IndicateKeepRight = 1 << 11,
            Special           = 1 << 12,
            NoBigVehicles     = 1 << 13,
            Tunnel            = 1 << 14,
            GpsBothWays       = 1 << 15,
            BlockIfNoLanes    = 1 << 16,
        }

        /// <summary>Link flag bits (schema v2 `l[4]`). Must match
        /// LINK_FLAG_BITS in tools/build-map-data.py.</summary>
        [Flags]
        public enum LinkFlags
        {
            None       = 0,
            NarrowRoad = 1 << 0,
            Shortcut   = 1 << 1,
        }

        // ---- Storage (parallel arrays — cache-friendly for the hot path) ----
        private static int nodeCount = 0;
        private static float[] posX = new float[0];
        private static float[] posY = new float[0];
        private static float[] posZ = new float[0];
        private static int[]   nodeFlags = new int[0];
        private static int[]   linkStart = new int[0]; // index into linkTarget
        private static int[]   linkCount = new int[0];
        private static int[]   linkTarget   = new int[0];
        private static byte[]  linkFwdLanes = new byte[0];
        private static byte[]  linkBwdLanes = new byte[0];

        // ---- Schema-v2 enrichment (all-zero when loading a v1 file) ----
        private static int[]    extFlags     = new int[0];  // ExtFlags bits
        private static ushort[] streetIdx    = new ushort[0];
        private static sbyte[]  speedCat     = new sbyte[0]; // -1 = unknown
        private static byte[]   linkWidthM   = new byte[0];  // 0 = unknown; extra/shoulder width, NOT total road width
        private static byte[]   linkFlagBits = new byte[0];
        private static string[] streetTable  = new string[] { "" };
        private static int schemaVersion = 0;
        public static int SchemaVersion { get { return schemaVersion; } }
        public static int StreetTableCount { get { return streetTable.Length; } }
        public static int NodesWithExtFlags { get; private set; }
        public static int NodesWithStreet { get; private set; }
        public static int LinksWithWidth { get; private set; }

        // ---- 2D spatial index ----
        // 64 m bins keyed by (binX, binY). 16k^2 m world -> ~256 bins per axis;
        // typical bin has 0-20 nodes. Dictionary is plenty fast for this size.
        private const float BIN_SIZE = 64f;
        private static Dictionary<long, List<int>> spatial = new Dictionary<long, List<int>>();

        private static bool loaded = false;
        private static string loadStatus = "notLoaded";
        private static string loadedFileInfo = "missing";
        public static bool IsLoaded { get { return loaded && nodeCount > 0; } }
        public static int NodeCount { get { return nodeCount; } }
        public static string LoadStatus { get { return loadStatus; } }
        public static string LoadedFileInfo { get { return loadedFileInfo; } }

        // -------------------------------------------------------------------
        // Load
        // -------------------------------------------------------------------

        /// <summary>Loads scripts/gta11y-nodes.json.gz. Safe to call repeatedly;
        /// no-op once loaded. On failure leaves NodeGraph.IsLoaded=false and the
        /// caller (drive assist) falls back to runtime-native queries.</summary>
        public static void Load()
        {
            if (loaded) return;
            loaded = true; // set first so a parse failure doesn't retry forever
            string path = "scripts/gta11y-nodes.json.gz";
            loadedFileInfo = MapDb.FmtFileInfo(path);
            try
            {
                if (!File.Exists(path)) { loadStatus = "fileNotFound"; return; }
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz))
                using (var jr = new JsonTextReader(sr))
                {
                    var ser = new JsonSerializer();
                    var dump = ser.Deserialize<GraphDump>(jr);
                    if (dump == null || dump.nodes == null) { loadStatus = "nullData"; return; }
                    LoadFromDump(dump);
                    loadStatus = "ok";
                }
            }
            catch (Exception ex)
            {
                // Degrade, never crash — but record why for the banner (a stale
                // v1 file, a corrupt gz, and a missing file were previously
                // indistinguishable in the log).
                nodeCount = 0;
                loadStatus = MapDb.FmtLoadError(ex);
            }
        }

        private static void LoadFromDump(GraphDump dump)
        {
            int n = dump.nodes.Count;
            nodeCount = n;
            schemaVersion = dump.version;
            posX = new float[n];
            posY = new float[n];
            posZ = new float[n];
            nodeFlags = new int[n];
            linkStart = new int[n];
            linkCount = new int[n];
            extFlags  = new int[n];
            streetIdx = new ushort[n];
            speedCat  = new sbyte[n];

            streetTable = (dump.streets != null && dump.streets.Count > 0)
                ? dump.streets.ToArray()
                : new string[] { "" };

            // Pre-count total links so we can size linkTarget/lanes once.
            int totalLinks = 0;
            for (int i = 0; i < n; i++)
            {
                var nr = dump.nodes[i];
                totalLinks += (nr.l != null ? nr.l.Count : 0);
            }
            linkTarget   = new int[totalLinks];
            linkFwdLanes = new byte[totalLinks];
            linkBwdLanes = new byte[totalLinks];
            linkWidthM   = new byte[totalLinks];
            linkFlagBits = new byte[totalLinks];

            int extNodes = 0, streetNodes = 0, widthLinks = 0;
            int linkIdx = 0;
            for (int i = 0; i < n; i++)
            {
                var nr = dump.nodes[i];
                posX[i] = nr.p[0];
                posY[i] = nr.p[1];
                posZ[i] = nr.p[2];
                nodeFlags[i] = nr.f;
                extFlags[i] = nr.e;
                if (nr.e != 0) extNodes++;
                // Street index: clamp against the table so a malformed file
                // can't cause an out-of-range read later.
                streetIdx[i] = (ushort)((nr.s > 0 && nr.s < streetTable.Length) ? nr.s : 0);
                if (streetIdx[i] != 0) streetNodes++;
                // v: speed category; the converter omits it when unknown, and
                // Newtonsoft leaves the int? null — map to -1.
                speedCat[i] = (sbyte)(nr.v.HasValue
                    ? System.Math.Min(127, System.Math.Max(-1, nr.v.Value)) : -1);
                linkStart[i] = linkIdx;
                int lc = nr.l != null ? nr.l.Count : 0;
                linkCount[i] = lc;
                for (int j = 0; j < lc; j++)
                {
                    var link = nr.l[j];
                    linkTarget[linkIdx]   = link[0];
                    // Clamp lane counts to byte range (0-255). YND stores 3 bits
                    // each (0-7) so this never actually clips.
                    int fwd = link.Length > 1 ? link[1] : 1;
                    int bwd = link.Length > 2 ? link[2] : 1;
                    linkFwdLanes[linkIdx] = (byte)System.Math.Min(255, System.Math.Max(0, fwd));
                    linkBwdLanes[linkIdx] = (byte)System.Math.Min(255, System.Math.Max(0, bwd));
                    // v2 extras — v1 files have length-3 entries, leave zeros.
                    int wm = link.Length > 3 ? link[3] : 0;
                    int lf = link.Length > 4 ? link[4] : 0;
                    linkWidthM[linkIdx]   = (byte)System.Math.Min(255, System.Math.Max(0, wm));
                    linkFlagBits[linkIdx] = (byte)System.Math.Min(255, System.Math.Max(0, lf));
                    if (wm > 0) widthLinks++;
                    linkIdx++;
                }
                AddToSpatial(i, posX[i], posY[i]);
            }
            NodesWithExtFlags = extNodes;
            NodesWithStreet = streetNodes;
            LinksWithWidth = widthLinks;
        }

        private static void AddToSpatial(int idx, float x, float y)
        {
            long key = BinKey((int)System.Math.Floor(x / BIN_SIZE), (int)System.Math.Floor(y / BIN_SIZE));
            List<int> bin;
            if (!spatial.TryGetValue(key, out bin))
            {
                bin = new List<int>(8);
                spatial[key] = bin;
            }
            bin.Add(idx);
        }

        private static long BinKey(int bx, int by)
        {
            return ((long)(uint)bx << 32) | (uint)by;
        }

        // -------------------------------------------------------------------
        // Queries
        // -------------------------------------------------------------------

        public static Vector3 GetPosition(int nodeIdx)
        {
            return new Vector3(posX[nodeIdx], posY[nodeIdx], posZ[nodeIdx]);
        }

        public static NodeFlags GetFlags(int nodeIdx)
        {
            return (NodeFlags)nodeFlags[nodeIdx];
        }

        public static int GetLinkCount(int nodeIdx)
        {
            return linkCount[nodeIdx];
        }

        /// <summary>Returns (targetIdx, fwdLanes, bwdLanes) for the j-th link
        /// of node nodeIdx. j must be in [0, GetLinkCount).</summary>
        public static void GetLink(int nodeIdx, int j, out int targetIdx, out int fwdLanes, out int bwdLanes)
        {
            int li = linkStart[nodeIdx] + j;
            targetIdx = linkTarget[li];
            fwdLanes  = linkFwdLanes[li];
            bwdLanes  = linkBwdLanes[li];
        }

        /// <summary>GetLink plus the v2 extras. widthM is EXTRA/shoulder width
        /// in meters (0 = unknown), not total road width — add it to
        /// lanes*LANE_WIDTH when estimating road extent, never divide by
        /// lane count.</summary>
        public static void GetLinkEx(int nodeIdx, int j, out int targetIdx,
            out int fwdLanes, out int bwdLanes, out int widthM, out LinkFlags lflags)
        {
            int li = linkStart[nodeIdx] + j;
            targetIdx = linkTarget[li];
            fwdLanes  = linkFwdLanes[li];
            bwdLanes  = linkBwdLanes[li];
            widthM    = linkWidthM[li];
            lflags    = (LinkFlags)linkFlagBits[li];
        }

        public static ExtFlags GetExtFlags(int nodeIdx)
        {
            return (ExtFlags)extFlags[nodeIdx];
        }

        /// <summary>Street code for a node ("" when unknown). Codes are the
        /// game's short street ids (e.g. "DelPFwy"), not display names.</summary>
        public static string GetStreetName(int nodeIdx)
        {
            return streetTable[streetIdx[nodeIdx]];
        }

        /// <summary>Speed category from the source path data (0=slow..3=fast),
        /// -1 when unknown.</summary>
        public static int GetSpeedCategory(int nodeIdx)
        {
            return speedCat[nodeIdx];
        }

        /// <summary>Nearest node within maxRadius 2D distance. Returns -1 if
        /// none. Ignores Z (this is intentional — flying-bridge / underpass
        /// duplicates are common and the 2D-closest is almost always the one
        /// we want for the assist).</summary>
        public static int FindNearestNode(Vector3 pos, float maxRadius)
        {
            return FindNearestNode(pos, maxRadius, NodeFlags.None, NodeFlags.None);
        }

        /// <summary>Nearest node within maxRadius that has all `require` bits
        /// set and none of the `forbid` bits set. -1 if none.</summary>
        public static int FindNearestNode(Vector3 pos, float maxRadius,
            NodeFlags require, NodeFlags forbid)
        {
            return FindNearestNode(pos, maxRadius, require, forbid, ExtFlags.None);
        }

        /// <summary>Nearest node additionally excluding nodes carrying any of
        /// the `extForbid` ExtFlags bits (e.g. Disabled|DontUseForNav so
        /// recovery never targets a node the game's own AI refuses to use).
        /// With a v1 data file all ext flags are 0, so extForbid never
        /// excludes anything — behavior is identical to the old overload.</summary>
        public static int FindNearestNode(Vector3 pos, float maxRadius,
            NodeFlags require, NodeFlags forbid, ExtFlags extForbid)
        {
            if (!IsLoaded) return -1;
            int reqI = (int)require, forI = (int)forbid, extForI = (int)extForbid;
            int bx = (int)System.Math.Floor(pos.X / BIN_SIZE);
            int by = (int)System.Math.Floor(pos.Y / BIN_SIZE);
            int span = (int)System.Math.Ceiling(maxRadius / BIN_SIZE);
            float bestSq = maxRadius * maxRadius;
            int best = -1;
            for (int dx = -span; dx <= span; dx++)
            for (int dy = -span; dy <= span; dy++)
            {
                List<int> bin;
                if (!spatial.TryGetValue(BinKey(bx + dx, by + dy), out bin)) continue;
                for (int i = 0; i < bin.Count; i++)
                {
                    int idx = bin[i];
                    if (reqI != 0 && (nodeFlags[idx] & reqI) != reqI) continue;
                    if (forI != 0 && (nodeFlags[idx] & forI) != 0) continue;
                    if (extForI != 0 && (extFlags[idx] & extForI) != 0) continue;
                    float ddx = posX[idx] - pos.X;
                    float ddy = posY[idx] - pos.Y;
                    float dsq = ddx * ddx + ddy * ddy;
                    if (dsq < bestSq) { bestSq = dsq; best = idx; }
                }
            }
            return best;
        }

        /// <summary>Walk the graph starting at `startIdx`, choosing at each
        /// junction the neighbour whose bearing is closest to `forward`.
        /// Returns up to maxHops successor node positions (not including the
        /// start). One-way links are respected via `LaneCountForward` — a link
        /// with fwdLanes=0 is impassable in the start->target direction.
        ///
        /// Used by the drive assist to build a lookahead polyline of where the
        /// AI driver would go from here.</summary>
        public static List<Vector3> WalkAlongDirection(int startIdx, Vector3 forward, int maxHops)
        {
            return WalkAlongDirection(startIdx, forward, maxHops, false, null);
        }

        /// <summary>Counts how many times the restriction filter changed the
        /// chosen neighbour during the most recent restricted walk. Used for
        /// counterfactual logging while the feature is dark-launched.</summary>
        public static int LastWalkRestrictedCount { get; private set; }

        /// <summary>Walk variant that can honor the v2 source restrictions:
        /// skips Disabled/DontUseForNav targets and, at nodes flagged
        /// CannotGoLeft / CannotGoRight / LeftTurnsOnly (treated as
        /// CannotGoRight), excludes candidates in the forbidden turn
        /// direction. If filtering empties the candidate set the walk falls
        /// back to the unrestricted choice — it must never produce a worse
        /// polyline than the plain walk. Pass `outIndices` to also collect
        /// the node indices of the returned positions (for flag/speed
        /// lookups along the path).</summary>
        public static List<Vector3> WalkAlongDirection(int startIdx, Vector3 forward, int maxHops,
            bool honorRestrictions, List<int> outIndices)
        {
            var result = new List<Vector3>(maxHops);
            LastWalkRestrictedCount = 0;
            if (outIndices != null) outIndices.Clear();
            if (!IsLoaded || startIdx < 0 || startIdx >= nodeCount) return result;
            int cur = startIdx;
            int prev = -1;
            Vector3 curFwd = forward; if (curFwd.LengthSquared() < 0.0001f) return result;
            curFwd = Normalize2D(curFwd);
            for (int h = 0; h < maxHops; h++)
            {
                int lc = linkCount[cur];
                int bestNext = -1;
                float bestDot = -2f;
                int bestUnres = -1;         // best ignoring restrictions
                float bestUnresDot = -2f;
                int ls = linkStart[cur];
                int curExt = extFlags[cur];
                bool noLeft  = honorRestrictions && (curExt & (int)ExtFlags.CannotGoLeft) != 0;
                bool noRight = honorRestrictions && (curExt & (int)(ExtFlags.CannotGoRight | ExtFlags.LeftTurnsOnly)) != 0;
                for (int j = 0; j < lc; j++)
                {
                    int tgt = linkTarget[ls + j];
                    if (tgt == prev) continue; // don't double back
                    if (linkFwdLanes[ls + j] == 0) continue; // one-way against us
                    Vector3 dir = new Vector3(posX[tgt] - posX[cur], posY[tgt] - posY[cur], 0f);
                    if (dir.LengthSquared() < 0.01f) continue;
                    dir = Normalize2D(dir);
                    float dot = dir.X * curFwd.X + dir.Y * curFwd.Y;
                    if (dot > bestUnresDot) { bestUnresDot = dot; bestUnres = tgt; }
                    if (honorRestrictions)
                    {
                        if ((extFlags[tgt] & (int)(ExtFlags.Disabled | ExtFlags.DontUseForNav)) != 0)
                            continue;
                        // Signed 2D cross: positive = candidate veers left of
                        // travel, negative = right. |cross| <= 0.45 (~26 deg)
                        // counts as straight and is never restricted.
                        float cross = curFwd.X * dir.Y - curFwd.Y * dir.X;
                        if (noLeft && cross > 0.45f) continue;
                        if (noRight && cross < -0.45f) continue;
                    }
                    if (dot > bestDot) { bestDot = dot; bestNext = tgt; }
                }
                if (bestNext < 0 && bestUnres >= 0)
                {
                    // Restrictions excluded everything — fall back rather than
                    // truncating the polyline.
                    bestNext = bestUnres;
                    bestDot = bestUnresDot;
                    LastWalkRestrictedCount++;
                }
                else if (honorRestrictions && bestUnres >= 0 && bestNext != bestUnres)
                {
                    LastWalkRestrictedCount++;
                }
                if (bestNext < 0) break;
                // Only follow when the chosen link is at least vaguely in the
                // intended direction. Below cos(75°)=0.26 we'd be doubling back
                // at a stub end — bail rather than oscillate.
                if (bestDot < 0.26f) break;
                Vector3 nextPos = new Vector3(posX[bestNext], posY[bestNext], posZ[bestNext]);
                result.Add(nextPos);
                if (outIndices != null) outIndices.Add(bestNext);
                Vector3 step = new Vector3(posX[bestNext] - posX[cur], posY[bestNext] - posY[cur], 0f);
                if (step.LengthSquared() > 0.01f) curFwd = Normalize2D(step);
                prev = cur;
                cur = bestNext;
            }
            return result;
        }

        private static Vector3 Normalize2D(Vector3 v)
        {
            float l = (float)System.Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (l < 1e-5f) return new Vector3(0, 0, 0);
            return new Vector3(v.X / l, v.Y / l, 0f);
        }

        // -------------------------------------------------------------------
        // JSON DTOs — match the schema in tools/build-map-data.py.
        // -------------------------------------------------------------------

        private class GraphDump
        {
            public int version { get; set; }
            public int n { get; set; }
            public List<string> streets { get; set; } // v2; null in v1 files
            public List<NodeRec> nodes { get; set; }
        }

        private class NodeRec
        {
            public float[] p { get; set; }
            public int f { get; set; }
            public int e { get; set; }  // v2 ExtFlags bits (0 when absent)
            public int s { get; set; }  // v2 street-table index (0 when absent)
            public int? v { get; set; } // v2 speed category (null when absent)
            // v1: [[targetIdx, fwdLanes, bwdLanes], ...]
            // v2: [[targetIdx, fwdLanes, bwdLanes, widthM, linkFlags], ...]
            public List<int[]> l { get; set; }
        }
    }

    /// <summary>
    /// Signalized-junction templates extracted from the game's
    /// CJunctionTemplateArray (scripts/gta11y-junctions.json.gz, built by
    /// tools/build-map-data.py from CodeWalker's junctions.xml). GTA V only
    /// hand-authors these templates for major intersections (~82 usable), so
    /// this is a sparse overlay: drive assist arms junction behavior on
    /// NodeGraph.NodeFlags.TrafficLight generally, and consults JunctionDb
    /// for per-entrance turn rules and stop positions where a template exists.
    /// </summary>
    public static class JunctionDb
    {
        public struct EntranceInfo
        {
            public Vector3 Pos;
            public int NodeIdx;          // pre-resolved NodeGraph index, -1 if none
            public int Phase;
            public float StopDist;       // fStoppingDistance, sign convention unconfirmed — informational only
            public float Orientation;    // approach bearing, radians
            public bool RightOnRed;
            public bool LeftLaneAheadOnly;
            public bool RightLaneRightOnly;
            public int JunctionIdx;
        }

        // ---- Junction storage (parallel arrays) ----
        private static int junctionCount = 0;
        private static float[] jMinX = new float[0], jMinY = new float[0], jMinZ = new float[0];
        private static float[] jMaxX = new float[0], jMaxY = new float[0], jMaxZ = new float[0];
        private static int[] jPhases = new int[0];
        private static int[] jLights = new int[0];
        private static int[] jEntranceStart = new int[0];
        private static int[] jEntranceCount = new int[0];

        // ---- Entrance storage ----
        private static EntranceInfo[] entrances = new EntranceInfo[0];

        // ---- Spatial index over entrance positions (64 m bins, same scheme
        // as NodeGraph) ----
        private const float BIN_SIZE = 64f;
        private static Dictionary<long, List<int>> spatial = new Dictionary<long, List<int>>();

        private static bool loaded = false;
        private static string loadStatus = "notLoaded";
        private static string loadedFileInfo = "missing";
        public static bool IsLoaded { get { return loaded && junctionCount > 0; } }
        public static string LoadStatus { get { return loadStatus; } }
        public static string LoadedFileInfo { get { return loadedFileInfo; } }
        public static int JunctionCount { get { return junctionCount; } }
        public static int EntranceCount { get { return entrances.Length; } }
        public static int SnappedEntranceCount { get; private set; }

        /// <summary>Loads scripts/gta11y-junctions.json.gz. Safe to call
        /// repeatedly; silent fallback on failure (drive assist simply gets
        /// no junction templates).</summary>
        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            string path = "scripts/gta11y-junctions.json.gz";
            loadedFileInfo = MapDb.FmtFileInfo(path);
            try
            {
                if (!File.Exists(path)) { loadStatus = "fileNotFound"; return; }
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz))
                using (var jr = new JsonTextReader(sr))
                {
                    var ser = new JsonSerializer();
                    var dump = ser.Deserialize<JunctionDump>(jr);
                    if (dump == null || dump.junctions == null) { loadStatus = "nullData"; return; }
                    LoadFromDump(dump);
                    loadStatus = "ok";
                }
            }
            catch (Exception ex)
            {
                junctionCount = 0;
                loadStatus = MapDb.FmtLoadError(ex);
            }
        }

        private static void LoadFromDump(JunctionDump dump)
        {
            int n = dump.junctions.Count;
            junctionCount = n;
            jMinX = new float[n]; jMinY = new float[n]; jMinZ = new float[n];
            jMaxX = new float[n]; jMaxY = new float[n]; jMaxZ = new float[n];
            jPhases = new int[n];
            jLights = new int[n];
            jEntranceStart = new int[n];
            jEntranceCount = new int[n];

            int totalE = 0;
            for (int i = 0; i < n; i++)
                totalE += (dump.junctions[i].e != null ? dump.junctions[i].e.Count : 0);
            entrances = new EntranceInfo[totalE];

            int snapped = 0;
            int ei = 0;
            for (int i = 0; i < n; i++)
            {
                var j = dump.junctions[i];
                jMinX[i] = j.min[0]; jMinY[i] = j.min[1]; jMinZ[i] = j.min[2];
                jMaxX[i] = j.max[0]; jMaxY[i] = j.max[1]; jMaxZ[i] = j.max[2];
                jPhases[i] = j.phases;
                jLights[i] = j.lights;
                jEntranceStart[i] = ei;
                int ec = j.e != null ? j.e.Count : 0;
                jEntranceCount[i] = ec;
                for (int k = 0; k < ec; k++)
                {
                    var e = j.e[k];
                    var info = new EntranceInfo
                    {
                        Pos = new Vector3(e.p[0], e.p[1], e.p[2]),
                        NodeIdx = e.ni,
                        Phase = e.ph,
                        StopDist = e.sd,
                        Orientation = e.o,
                        RightOnRed = e.rr != 0,
                        LeftLaneAheadOnly = e.la != 0,
                        RightLaneRightOnly = e.rl != 0,
                        JunctionIdx = i,
                    };
                    if (e.ni >= 0) snapped++;
                    entrances[ei] = info;
                    long key = BinKey((int)System.Math.Floor(info.Pos.X / BIN_SIZE),
                                      (int)System.Math.Floor(info.Pos.Y / BIN_SIZE));
                    List<int> bin;
                    if (!spatial.TryGetValue(key, out bin))
                    {
                        bin = new List<int>(4);
                        spatial[key] = bin;
                    }
                    bin.Add(ei);
                    ei++;
                }
            }
            SnappedEntranceCount = snapped;
        }

        private static long BinKey(int bx, int by)
        {
            return ((long)(uint)bx << 32) | (uint)by;
        }

        /// <summary>True when the junction has traffic lights (or multiple
        /// signal phases, which implies lights).</summary>
        public static bool IsSignalized(int junctionIdx)
        {
            return jLights[junctionIdx] > 0 || jPhases[junctionIdx] > 1;
        }

        public static int GetPhaseCount(int junctionIdx) { return jPhases[junctionIdx]; }
        public static int GetLightCount(int junctionIdx) { return jLights[junctionIdx]; }

        /// <summary>Center of the junction's bounding box.</summary>
        public static Vector3 GetJunctionCenter(int junctionIdx)
        {
            return new Vector3(
                (jMinX[junctionIdx] + jMaxX[junctionIdx]) * 0.5f,
                (jMinY[junctionIdx] + jMaxY[junctionIdx]) * 0.5f,
                (jMinZ[junctionIdx] + jMaxZ[junctionIdx]) * 0.5f);
        }

        /// <summary>Nearest junction entrance within maxRadius (2D, with a
        /// 10 m Z sanity gate against stacked roads). Returns the entrance
        /// index or -1; details via `info`.</summary>
        public static int FindEntranceNear(Vector3 pos, float maxRadius, out EntranceInfo info)
        {
            info = default(EntranceInfo);
            if (!IsLoaded) return -1;
            int bx = (int)System.Math.Floor(pos.X / BIN_SIZE);
            int by = (int)System.Math.Floor(pos.Y / BIN_SIZE);
            int span = (int)System.Math.Ceiling(maxRadius / BIN_SIZE);
            float bestSq = maxRadius * maxRadius;
            int best = -1;
            for (int dx = -span; dx <= span; dx++)
            for (int dy = -span; dy <= span; dy++)
            {
                List<int> bin;
                if (!spatial.TryGetValue(BinKey(bx + dx, by + dy), out bin)) continue;
                for (int i = 0; i < bin.Count; i++)
                {
                    int idx = bin[i];
                    if (System.Math.Abs(entrances[idx].Pos.Z - pos.Z) > 10f) continue;
                    float ddx = entrances[idx].Pos.X - pos.X;
                    float ddy = entrances[idx].Pos.Y - pos.Y;
                    float dsq = ddx * ddx + ddy * ddy;
                    if (dsq < bestSq) { bestSq = dsq; best = idx; }
                }
            }
            if (best >= 0) info = entrances[best];
            return best;
        }

        /// <summary>True when pos lies inside a junction template's bbox
        /// (expanded by 2 m laterally, 5 m vertically). Junction bboxes are
        /// small (~50 m), so scanning the entrance bins around pos finds any
        /// containing junction.</summary>
        public static bool TryGetJunctionContaining(Vector3 pos, out int junctionIdx)
        {
            junctionIdx = -1;
            if (!IsLoaded) return false;
            int bx = (int)System.Math.Floor(pos.X / BIN_SIZE);
            int by = (int)System.Math.Floor(pos.Y / BIN_SIZE);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                List<int> bin;
                if (!spatial.TryGetValue(BinKey(bx + dx, by + dy), out bin)) continue;
                for (int i = 0; i < bin.Count; i++)
                {
                    int j = entrances[bin[i]].JunctionIdx;
                    if (pos.X >= jMinX[j] - 2f && pos.X <= jMaxX[j] + 2f
                        && pos.Y >= jMinY[j] - 2f && pos.Y <= jMaxY[j] + 2f
                        && pos.Z >= jMinZ[j] - 5f && pos.Z <= jMaxZ[j] + 5f)
                    {
                        junctionIdx = j;
                        return true;
                    }
                }
            }
            return false;
        }

        // ---- JSON DTOs — match the schema in tools/build-map-data.py ----

        private class JunctionDump
        {
            public int version { get; set; }
            public int n { get; set; }
            public List<JunctionRec> junctions { get; set; }
        }

        private class JunctionRec
        {
            public float[] min { get; set; }
            public float[] max { get; set; }
            public float[][] jp { get; set; }
            public int phases { get; set; }
            public int lights { get; set; }
            public int flags { get; set; }
            public List<EntranceRec> e { get; set; }
        }

        private class EntranceRec
        {
            public float[] p { get; set; }
            public int ni { get; set; }
            public int ph { get; set; }
            public float sd { get; set; }
            public float o { get; set; }
            public int rr { get; set; }
            public int la { get; set; }
            public int rl { get; set; }
            public int lf { get; set; }
        }
    }
}
