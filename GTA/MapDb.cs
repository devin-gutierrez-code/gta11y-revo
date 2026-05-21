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
        public static bool IsLoaded { get { return loaded; } }
        public static int HighwayCount { get { return highways.Count; } }
        public static int ServiceCount { get { return services.Count; } }

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
                    loaded = true;
                    return;
                }
                string json = File.ReadAllText(path);
                var root = JsonConvert.DeserializeObject<MapJsonRoot>(json);
                if (root != null)
                {
                    if (root.highways != null) highways = root.highways;
                    if (root.services != null) services = root.services;
                }
                loaded = true;
            }
            catch
            {
                // Silent fallback — Load() failure must never crash the mod.
                // The caller can check IsLoaded if it wants to know.
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

        // ---- 2D spatial index ----
        // 64 m bins keyed by (binX, binY). 16k^2 m world -> ~256 bins per axis;
        // typical bin has 0-20 nodes. Dictionary is plenty fast for this size.
        private const float BIN_SIZE = 64f;
        private static Dictionary<long, List<int>> spatial = new Dictionary<long, List<int>>();

        private static bool loaded = false;
        public static bool IsLoaded { get { return loaded && nodeCount > 0; } }
        public static int NodeCount { get { return nodeCount; } }

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
            try
            {
                if (!File.Exists(path)) return;
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz))
                using (var jr = new JsonTextReader(sr))
                {
                    var ser = new JsonSerializer();
                    var dump = ser.Deserialize<GraphDump>(jr);
                    if (dump == null || dump.nodes == null) return;
                    LoadFromDump(dump);
                }
            }
            catch
            {
                // Silent — drive assist degrades but mod must not crash.
                nodeCount = 0;
            }
        }

        private static void LoadFromDump(GraphDump dump)
        {
            int n = dump.nodes.Count;
            nodeCount = n;
            posX = new float[n];
            posY = new float[n];
            posZ = new float[n];
            nodeFlags = new int[n];
            linkStart = new int[n];
            linkCount = new int[n];

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

            int linkIdx = 0;
            for (int i = 0; i < n; i++)
            {
                var nr = dump.nodes[i];
                posX[i] = nr.p[0];
                posY[i] = nr.p[1];
                posZ[i] = nr.p[2];
                nodeFlags[i] = nr.f;
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
                    linkIdx++;
                }
                AddToSpatial(i, posX[i], posY[i]);
            }
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
            if (!IsLoaded) return -1;
            int reqI = (int)require, forI = (int)forbid;
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
            var result = new List<Vector3>(maxHops);
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
                int ls = linkStart[cur];
                for (int j = 0; j < lc; j++)
                {
                    int tgt = linkTarget[ls + j];
                    if (tgt == prev) continue; // don't double back
                    if (linkFwdLanes[ls + j] == 0) continue; // one-way against us
                    Vector3 dir = new Vector3(posX[tgt] - posX[cur], posY[tgt] - posY[cur], 0f);
                    if (dir.LengthSquared() < 0.01f) continue;
                    dir = Normalize2D(dir);
                    float dot = dir.X * curFwd.X + dir.Y * curFwd.Y;
                    if (dot > bestDot) { bestDot = dot; bestNext = tgt; }
                }
                if (bestNext < 0) break;
                // Only follow when the chosen link is at least vaguely in the
                // intended direction. Below cos(75°)=0.26 we'd be doubling back
                // at a stub end — bail rather than oscillate.
                if (bestDot < 0.26f) break;
                Vector3 nextPos = new Vector3(posX[bestNext], posY[bestNext], posZ[bestNext]);
                result.Add(nextPos);
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
            public List<NodeRec> nodes { get; set; }
        }

        private class NodeRec
        {
            public float[] p { get; set; }
            public int f { get; set; }
            public List<int[]> l { get; set; } // [[targetIdx, fwdLanes, bwdLanes], ...]
        }
    }
}
