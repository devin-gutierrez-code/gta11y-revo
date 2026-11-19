using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// San Andreas Wildfires. A chaos feature, not a simulation: fire burns on everything
    /// except water, spreads cell to cell with the wind and uphill, throws embers far
    /// downwind, and keeps going until it runs out of budget or the player leaves it behind.
    ///
    /// Model. The world is a grid of CELL-metre squares, shared by every outbreak, so two
    /// fires that meet simply merge. A cell is unburnt (absent from the dictionary),
    /// burning (owns one script fire), burnt, doused, or water. Burning cells try to light
    /// their eight neighbours each spread tick; a burnt or doused cell never relights, which
    /// is what makes the front move outward instead of smouldering in place.
    ///
    /// Budget. The engine has one fixed-size fire pool shared by script fires, their
    /// children, and every fire the game starts itself; START_SCRIPT_FIRE returns 0 when it
    /// is full. No source consulted states the pool size, so it is not hard-coded: the mod
    /// owns at most <see cref="ceiling"/> fires, and a failed start drops the ceiling to what
    /// is burning now and holds it, then it creeps back up. At the ceiling a new cell may
    /// displace only a burning cell that is well FARTHER from the player; otherwise the
    /// fire waits for burn-out. Retiring the oldest instead (the first version) turned
    /// every fire into a band that raced away downwind.
    ///
    /// Origin. Folded in from the standalone san_andreas_wildfires.cs, which never loaded:
    /// SHVDN's runtime .cs compiler is C# 5 and rejected its auto-property initialisers
    /// (ScriptHookVDotNet.log, "Failed to compile" under both APIs), and it removed fires
    /// through 0x790125633649A4B3, which is not REMOVE_SCRIPT_FIRE (0x7FF548385680673F).
    /// It also passed maxChildren 25, the boundary where SHVDN's docs ("less than 25") and
    /// CitizenFX's ("25 or less") disagree; this uses a small value well inside both.
    ///
    /// No spoken per-fire announcements: the fire is loud in-game. The one line this speaks
    /// is the fail-closed one — a feature that is on and can create no fire says so.
    /// </summary>
    internal sealed class Wildfires
    {
        private const byte BURNING = 1, BURNT = 2, DOUSED = 3, WATER = 4;

        private sealed class Cell
        {
            public int Ix, Iy;
            public Vector3 Pos;
            public byte State;
            public ScriptFire Fire;
            public int IgnitedAt;
            public int BurnMs;
        }

        private struct Candidate
        {
            public int Ix, Iy;
            public float RefZ;
            public bool Ember;
        }

        private struct Incident
        {
            public int Id;
            public int CreatedAt;
            public Vector3 Pos;
        }

        private const byte F_NONE = 0, F_LEAVE = 1, F_GOTO = 2, F_SPRAY = 3, F_CANNON = 4;

        private sealed class Fighter
        {
            public Ped Ped;
            public VehicleSeat Seat;
            public byte Mode;
            public long Target;
            public int TaskedAt;
        }

        /// <summary>A fire truck the mod has taken off the engine's dispatch AI.</summary>
        private sealed class Crew
        {
            public Vehicle Truck;
            public readonly List<Fighter> Fighters = new List<Fighter>();
            public int ClaimedAt, ClearSince, ReturnAt, ReturnTaskedAt;
            public bool Returning;
            public string Reason;
        }

        // ---- grid and spread ----
        // Field log 2026-09-18 (3 outbreaks, 1490 cells): the ceiling was reached ~15 s
        // after each outbreak, then ~7 cells/s lit and ~7 cells/s were retired oldest-first;
        // burnedOut stayed 0, so no cell ever lived its 14-30 s. The fire was a 70-cell band
        // sliding downwind at several m/s, not an area. Spread is now slow and rounder,
        // cells burn long enough to overlap, and the budget displaces the cell FARTHEST from
        // the player rather than the oldest, so a fire at its cap holds ground around you.
        private const float CELL = 6f;
        private const int SPREAD_MS = 1100;
        private const float SPREAD_P = 0.05f;          // per neighbour per tick, before wind/slope/rain
        private const float WIND_GAIN = 1.2f;          // downwind factor 1 + gain*wind*dot
        private const float WIND_FLOOR = 0.3f;         // upwind still backs slowly
        private const float EMBER_P = 0.008f;          // per burning cell per tick
        private const float EMBER_MIN = 12f, EMBER_MAX = 25f;
        private const float EMBER_WIND_M = 1.5f;       // extra reach per m/s of wind
        private const float EMBER_HEADROOM = 0.75f;    // no spot fires above this share of the ceiling
        private const float SLOPE_MAX_BOOST = 2.2f;
        private const int BURN_MIN_MS = 35000, BURN_MAX_MS = 70000;
        private const int MIN_BURN_BEFORE_RETIRE_MS = 20000;
        private const float DISPLACE_MARGIN = 20f;     // a new cell must be this much nearer the player
        private const int MAX_ATTEMPTS_PER_TICK = 6;
        private const int MAX_CHILDREN = 3;

        // ---- budget ----
        // CEILING_MAX is deliberately above any plausible pool size: the engine refuses
        // what it cannot hold, every refusal is logged with the world fire count at that
        // moment, and the ceiling then drops to what is burning. The log learns the pool.
        private const int CEILING_MAX = 1024, CEILING_MIN = 20, CEILING_STEP = 4;
        private const int CEILING_HOLD_MS = 15000;

        // ---- ignition detection ----
        private const int DETECT_MS = 1000;
        private const float DETECT_RADIUS = 150f;
        private const float OUTBREAK_EXCLUSION = 12f;

        // ---- housekeeping ----
        private const int HOUSEKEEP_MS = 2000;
        private const int CENSUS_MS = 5000;
        private const float DESPAWN_DISTANCE = 350f;
        private const float PRUNE_DISTANCE = 500f;
        private const int PRUNE_THRESHOLD = 20000;
        private const int DOUSE_MIN_AGE_MS = 4000;

        // ---- world reaction ----
        private const float FLEE_RADIUS = 35f;
        private const int FLEE_RETASK_MS = 12000;
        private const int MAX_FLEE_PER_TICK = 8;
        private const int FIRE_SERVICE = 3, FIRE_UNITS = 4;   // worlddata dispatch "fire"
        private const int MAX_INCIDENTS = 2;
        // Model.Hash is Int32; the enum value is above int.MaxValue.
        private static readonly int FIREMAN_HASH = unchecked((int)(uint)PedHash.Fireman01SMY);
        private const int INCIDENT_GAP_MS = 45000, INCIDENT_LIFE_MS = 150000;

        // ---- fire crews ----
        // Field report 2026-09-18: dispatched trucks reached the scene and then sat there
        // with sirens blaring. Nobody got out, nobody sprayed, and the trucks stayed until
        // the player walked away. CREATE_INCIDENT only gets a truck to the location. The
        // engine's crew does nothing with fires this mod starts, so the mod takes the
        // truck over when it arrives. It mutes the siren, gives the crew extinguishers,
        // has the driver use the water cannon, then loads the crew back up and drives off.
        private const float CREW_CLAIM_RADIUS = 60f;    // truck this near a burning cell
        private const float CREW_ARRIVED_SPEED = 2f;    // ...and stopped, or nearer than:
        private const float CREW_ARRIVED_DIST = 30f;
        private const float CREW_WORK_RADIUS = 80f;     // cells the crew will walk to
        private const float CREW_STANDOFF = 3.5f;       // extinguisher reach is short
        private const float CANNON_RANGE = 30f;
        private const int CREW_RETASK_MS = 5000;
        private const int CREW_CLEAR_MS = 10000;        // nothing near for this long: job done
        private const int CREW_JOB_MS = 300000;
        private const int CREW_RETURN_MS = 30000;       // then leave without stragglers
        private const float CREW_DEPART_SPEED = 15f;
        private const int MAX_CREWS = 2;
        private const int RELEASED_MEMORY_MS = 600000;

        private readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
        private readonly List<Cell> burning = new List<Cell>();   // oldest first
        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly HashSet<long> candidateKeys = new HashSet<long>();
        private readonly Dictionary<int, int> fleeTasked = new Dictionary<int, int>();
        private readonly List<Incident> incidents = new List<Incident>();
        private readonly List<Crew> crews = new List<Crew>();
        private readonly Dictionary<int, int> releasedTrucks = new Dictionary<int, int>();
        private readonly Random rnd = new Random();
        private readonly Action<string> log;

        private bool wasEnabled;
        private int lastDetect, lastSpread, lastHousekeep, lastCensus;
        private int ceiling = CEILING_MAX;
        private int ceilingHoldUntil;
        private int lastIncidentAt;
        private bool douseProbeDisabled;
        private bool failureSpoken;

        // Session counters, for the census line.
        private int nStarted, nFailed, nOutbreaks, nBurnedOut, nRetired, nDoused, nWater,
                    nFled, nIncidents, nDespawned, nCrewsClaimed, nCrewsReleased;

        public Wildfires(Action<string> log)
        {
            this.log = log;
        }

        public int BurningCount { get { return burning.Count; } }

        // ================================================================
        // Tick
        // ================================================================

        public void Tick(bool enabled)
        {
            if (!enabled)
            {
                if (wasEnabled)
                {
                    int n = burning.Count;
                    ExtinguishAll();
                    log("wildfire: act=disable removed=" + n);
                }
                wasEnabled = false;
                return;
            }

            int now = Game.GameTime;
            if (!wasEnabled)
            {
                wasEnabled = true;
                lastDetect = lastSpread = lastHousekeep = lastCensus = now;
                ceiling = CEILING_MAX;
                failureSpoken = false;
                douseProbeDisabled = false;
                log("wildfire: act=enable ceiling=" + ceiling + " children=" + MAX_CHILDREN);
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            Vector3 playerPos = player.Position;

            if (now - lastDetect >= DETECT_MS)
            {
                lastDetect = now;
                DetectIgnitions(playerPos, now);
            }
            if (now - lastSpread >= SPREAD_MS)
            {
                lastSpread = now;
                Spread(now, playerPos);
            }
            if (now - lastHousekeep >= HOUSEKEEP_MS)
            {
                lastHousekeep = now;
                Housekeep(playerPos, now);
                MakePedsFlee(playerPos, now);
                ManageCrews(playerPos, now);
            }
            if (now - lastCensus >= CENSUS_MS)
            {
                lastCensus = now;
                ManageDispatch(playerPos, now);
                RecoverCeiling(now);
                Census(playerPos);
            }
        }

        // ================================================================
        // Ignition
        // ================================================================

        private void DetectIgnitions(Vector3 playerPos, int now)
        {
            foreach (Vehicle v in World.GetNearbyVehicles(playerPos, DETECT_RADIUS))
            {
                if (v != null && v.IsOnFire && !NearBurning(v.Position, OUTBREAK_EXCLUSION))
                    Outbreak(v.Position, "vehicle", now);
            }
            foreach (Ped p in World.GetNearbyPeds(playerPos, DETECT_RADIUS))
            {
                if (p != null && p.IsOnFire && !NearBurning(p.Position, OUTBREAK_EXCLUSION))
                    Outbreak(p.Position, "ped", now);
            }
            Ped me = Game.Player.Character;
            if (me.IsOnFire && !NearBurning(me.Position, OUTBREAK_EXCLUSION))
                Outbreak(me.Position, "player", now);

            // Molotovs, explosions, fire ammo, burning wrecks: any fire this mod did not
            // start. Children of our own fires wander a little, so anything close to a
            // cell we have ever touched (burning or burnt) is ours, not a new outbreak.
            Vector3 fp;
            if (FireManager.TryGetClosestFirePosition(playerPos, out fp)
                && fp.DistanceTo(playerPos) < DETECT_RADIUS
                && !NearAnyCell(fp, OUTBREAK_EXCLUSION))
                Outbreak(fp, "fire", now);
        }

        private void Outbreak(Vector3 at, string source, int now)
        {
            int ix = GridOf(at.X), iy = GridOf(at.Y);
            if (cells.ContainsKey(Key(ix, iy))) return;
            bool lit = TryIgnite(ix, iy, at.Z, now, false, true);
            // Two neighbours for free so a single cell does not burn out before the
            // spread roll ever favours it.
            for (int i = 0; i < 2; i++)
                TryIgnite(ix + rnd.Next(-1, 2), iy + rnd.Next(-1, 2), at.Z, now, false, true);
            if (!lit) return;
            nOutbreaks++;
            log("wildfire: act=outbreak source=" + source + " at=" + Fmt(at)
                + " burning=" + burning.Count);
        }

        // ================================================================
        // Spread
        // ================================================================

        private void Spread(int now, Vector3 playerPos)
        {
            Vector3 wind = World.WindDirection;
            float windSpeed = World.WindSpeed;                  // 0..12 m/s
            float windK = Math.Min(1f, windSpeed / 12f);
            float rain = Clamp01(World.RainLevel);
            float rainK = 1f - 0.6f * rain;

            candidates.Clear();
            candidateKeys.Clear();
            bool emberRoom = burning.Count < ceiling * EMBER_HEADROOM;

            for (int b = burning.Count - 1; b >= 0; b--)
            {
                Cell c = burning[b];
                // Rain shortens burn time as well as slowing spread.
                int life = (int)(c.BurnMs * (1f - 0.4f * rain));
                if (now - c.IgnitedAt > life)
                {
                    Extinguish(c, BURNT);
                    burning.RemoveAt(b);
                    nBurnedOut++;
                    continue;
                }

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = c.Ix + dx, ny = c.Iy + dy;
                    long k = Key(nx, ny);
                    if (cells.ContainsKey(k) || candidateKeys.Contains(k)) continue;

                    float len = (dx != 0 && dy != 0) ? 1.41421356f : 1f;
                    float dot = (dx * wind.X + dy * wind.Y) / len;
                    float windF = Math.Max(WIND_FLOOR, 1f + WIND_GAIN * windK * dot);
                    // Slope is folded in after the ground probe; roll against its maximum
                    // here and scale back down in TryIgnite.
                    float p = SPREAD_P * windF * rainK * SLOPE_MAX_BOOST;
                    if (rnd.NextDouble() < p)
                        AddCandidate(nx, ny, c.Pos.Z, false);
                }

                // Spot fires only with budget to spare: at the cap they would take slots
                // from the main fire and carry it off downwind.
                if (emberRoom && rnd.NextDouble() < EMBER_P * (1f + windSpeed / 6f) * rainK)
                {
                    double heading = windK > 0.1f
                        ? Math.Atan2(wind.Y, wind.X) + Gauss() * 0.7
                        : rnd.NextDouble() * Math.PI * 2;
                    float dist = EMBER_MIN + (float)rnd.NextDouble() * (EMBER_MAX + windSpeed * EMBER_WIND_M - EMBER_MIN);
                    float ex = c.Pos.X + (float)Math.Cos(heading) * dist;
                    float ey = c.Pos.Y + (float)Math.Sin(heading) * dist;
                    int gx = GridOf(ex), gy = GridOf(ey);
                    long k = Key(gx, gy);
                    if (!cells.ContainsKey(k) && !candidateKeys.Contains(k))
                        AddCandidate(gx, gy, c.Pos.Z, true);
                }
            }

            if (candidates.Count == 0) return;
            Shuffle(candidates);
            int attempts = Math.Min(candidates.Count, MAX_ATTEMPTS_PER_TICK);

            // Near the cap, spend the budget on the side of the fire nearest the player.
            if (burning.Count + attempts > ceiling)
                candidates.Sort((a, b) => CandDist(a, playerPos).CompareTo(CandDist(b, playerPos)));

            for (int i = 0; i < attempts; i++)
            {
                Candidate cand = candidates[i];
                if (burning.Count >= ceiling)
                {
                    // At the cap a new cell may only displace one that has burned a while
                    // and is well farther from the player. Otherwise the fire waits for
                    // cells to burn out: it holds its ground instead of travelling.
                    int far = FarthestRetirable(playerPos, now, CandDist(cand, playerPos) + DISPLACE_MARGIN);
                    if (far < 0) break;
                    Extinguish(burning[far], BURNT);
                    burning.RemoveAt(far);
                    nRetired++;
                }
                TryIgnite(cand.Ix, cand.Iy, cand.RefZ, now, !cand.Ember, false);
            }
        }

        private static float CandDist(Candidate c, Vector3 playerPos)
        {
            float dx = (c.Ix + 0.5f) * CELL - playerPos.X, dy = (c.Iy + 0.5f) * CELL - playerPos.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Index of the burning cell farthest from the player, beyond minDist and
        /// old enough to retire; -1 if none.</summary>
        private int FarthestRetirable(Vector3 playerPos, int now, float minDist)
        {
            int best = -1;
            float bestD = minDist;
            for (int b = 0; b < burning.Count; b++)
            {
                Cell c = burning[b];
                if (now - c.IgnitedAt < MIN_BURN_BEFORE_RETIRE_MS) continue;
                float d = Dist2D(c.Pos, playerPos);
                if (d > bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private void AddCandidate(int ix, int iy, float refZ, bool ember)
        {
            long k = Key(ix, iy);
            candidateKeys.Add(k);
            candidates.Add(new Candidate { Ix = ix, Iy = iy, RefZ = refZ, Ember = ember });
        }

        /// <summary>
        /// Probe ground and water for one cell and light it. applySlope scales the
        /// pre-boosted spread roll back by the real uphill/downhill factor. force skips
        /// the budget ceiling (an outbreak must always be allowed to start).
        /// </summary>
        private bool TryIgnite(int ix, int iy, float refZ, int now, bool applySlope, bool force)
        {
            long k = Key(ix, iy);
            if (cells.ContainsKey(k)) return false;
            if (!force && burning.Count >= ceiling) return false;

            float x = (ix + 0.5f) * CELL + Jitter(), y = (iy + 0.5f) * CELL + Jitter();

            // Probe from a little above the source so a fire under a bridge or beside a
            // tower finds the ground it is spreading across, not the roof above it; fall
            // back to a high probe for steep hillsides. Everything but water burns.
            float groundZ;
            if (!World.GetGroundHeight(new Vector3(x, y, refZ + 30f), out groundZ, GetGroundHeightMode.Normal)
                && !World.GetGroundHeight(new Vector3(x, y, refZ + 250f), out groundZ, GetGroundHeightMode.Normal))
                return false;   // collision not streamed in here; try again another tick

            float waterZ;
            if (Water.GetWaterLevelNoWaves(new Vector3(x, y, groundZ + 50f), out waterZ) && waterZ > groundZ - 0.1f)
            {
                cells[k] = new Cell { Ix = ix, Iy = iy, Pos = new Vector3(x, y, waterZ), State = WATER };
                nWater++;
                return false;
            }

            if (applySlope)
            {
                float slope = (groundZ - refZ) / CELL;
                float slopeF = 1f + Math.Max(-0.6f, Math.Min(1f, slope)) * 1.2f;
                if (rnd.NextDouble() > slopeF / SLOPE_MAX_BOOST) return false;
            }

            Vector3 pos = new Vector3(x, y, groundZ);
            ScriptFire fire = null;
            try { fire = FireManager.StartScriptFire(pos, MAX_CHILDREN, false); } catch { }
            if (fire == null || fire.Handle == 0)
            {
                OnStartFailed(now, pos);
                return false;
            }

            cells[k] = new Cell
            {
                Ix = ix, Iy = iy, Pos = pos, State = BURNING, Fire = fire, IgnitedAt = now,
                BurnMs = BURN_MIN_MS + rnd.Next(BURN_MAX_MS - BURN_MIN_MS)
            };
            burning.Add(cells[k]);
            nStarted++;
            return true;
        }

        private void OnStartFailed(int now, Vector3 pos)
        {
            nFailed++;
            int was = ceiling;
            ceiling = Math.Max(CEILING_MIN, burning.Count - CEILING_STEP);
            ceilingHoldUntil = now + CEILING_HOLD_MS;

            // Every refusal, not just ceiling changes. worldFires counts every fire in the
            // map (ours, their children, the game's own): at a refusal that is the pool size.
            int world = -1;
            try { world = FireManager.GetNumberOfFiresInRadius(pos, 10000f); } catch { }
            log("wildfire: act=refused n=" + nFailed + " burning=" + burning.Count
                + " worldFires=" + world + " ceiling=" + was + "->" + ceiling + " at=" + Fmt(pos));

            // Fail closed and say so: on, asked to burn, and nothing has ever lit.
            if (nStarted == 0 && nFailed >= 8 && !failureSpoken)
            {
                failureSpoken = true;
                log("wildfire: act=fail started=0 failed=" + nFailed);
                Speech.Critical("Wildfire could not create any fire.");
            }
        }

        private void RecoverCeiling(int now)
        {
            if (ceiling < CEILING_MAX && now > ceilingHoldUntil)
                ceiling = Math.Min(CEILING_MAX, ceiling + CEILING_STEP);
        }

        // ================================================================
        // Housekeeping: firefighters, despawn, prune
        // ================================================================

        private void Housekeep(Vector3 playerPos, int now)
        {
            int checkedN = 0, zeroN = 0;
            List<Cell> dousedNow = null;

            for (int b = burning.Count - 1; b >= 0; b--)
            {
                Cell c = burning[b];
                if (Dist2D(c.Pos, playerPos) > DESPAWN_DISTANCE)
                {
                    Extinguish(c, BURNT);
                    burning.RemoveAt(b);
                    nDespawned++;
                    continue;
                }

                // Put out by something other than us: fire crews, rain, a player's
                // extinguisher. A doused cell never relights, so a crew that holds a line
                // actually builds a firebreak — which the embers then jump.
                if (douseProbeDisabled || now - c.IgnitedAt < DOUSE_MIN_AGE_MS) continue;
                checkedN++;
                if (FireManager.GetNumberOfFiresInRadius(c.Pos, 2f) == 0)
                {
                    zeroN++;
                    if (dousedNow == null) dousedNow = new List<Cell>();
                    dousedNow.Add(c);
                }
            }

            if (dousedNow != null)
            {
                // Tripwire. If every cell we own reads as out in the same pass and there is
                // no fire crew anywhere near, the probe is lying, not the fire — trusting it
                // would silently kill the whole feature. Stop asking for this session.
                if (checkedN >= 8 && zeroN == checkedN && !FirefightersNear(dousedNow[0].Pos, 80f))
                {
                    douseProbeDisabled = true;
                    log("wildfire: act=douse-probe-disabled checked=" + checkedN + " zero=" + zeroN);
                }
                else
                {
                    foreach (Cell c in dousedNow)
                    {
                        Extinguish(c, DOUSED);
                        burning.Remove(c);
                        nDoused++;
                    }
                }
            }

            if (cells.Count > PRUNE_THRESHOLD)
            {
                List<long> drop = new List<long>();
                foreach (KeyValuePair<long, Cell> kv in cells)
                {
                    if (kv.Value.State != BURNING && Dist2D(kv.Value.Pos, playerPos) > PRUNE_DISTANCE)
                        drop.Add(kv.Key);
                }
                foreach (long k in drop) cells.Remove(k);
                log("wildfire: act=prune removed=" + drop.Count + " kept=" + cells.Count);
            }
        }

        private static bool FirefightersNear(Vector3 at, float radius)
        {
            foreach (Ped p in World.GetNearbyPeds(at, radius))
            {
                if (p != null && p.Model.Hash == FIREMAN_HASH) return true;
            }
            return false;
        }

        // ================================================================
        // World reaction: fleeing and fire crews
        // ================================================================

        private void MakePedsFlee(Vector3 playerPos, int now)
        {
            if (burning.Count == 0) return;
            Ped me = Game.Player.Character;
            int tasked = 0;
            foreach (Ped p in World.GetNearbyPeds(playerPos, DETECT_RADIUS))
            {
                if (tasked >= MAX_FLEE_PER_TICK) break;
                if (p == null || p == me || !p.Exists() || p.IsDead) continue;
                if (!IsAmbient(p)) continue;
                if (p.Model.Hash == FIREMAN_HASH) continue;
                Vehicle veh = p.CurrentVehicle;
                if (veh != null)
                {
                    if (veh.Model.Hash == (int)VehicleHash.FireTruck) continue;
                    if (veh.Driver != p) continue;   // passengers go where the driver goes
                }

                int last;
                if (fleeTasked.TryGetValue(p.Handle, out last) && now - last < FLEE_RETASK_MS) continue;

                Cell c = NearestBurning(p.Position, FLEE_RADIUS);
                if (c == null) continue;
                try
                {
                    // A driver fleeing a coordinate drives away from it: that is the
                    // traffic "rerouting" around the fire.
                    p.Task.FleeFrom(c.Pos, FLEE_RETASK_MS);
                    fleeTasked[p.Handle] = now;
                    tasked++;
                    nFled++;
                }
                catch { }
            }

            if (fleeTasked.Count > 400)
            {
                List<int> old = new List<int>();
                foreach (KeyValuePair<int, int> kv in fleeTasked)
                    if (now - kv.Value > FLEE_RETASK_MS) old.Add(kv.Key);
                foreach (int h in old) fleeTasked.Remove(h);
            }
        }

        private static bool IsAmbient(Ped p)
        {
            // Mission peds, the player's bodyguards and anything persistent are never
            // touched: population type is the one test that excludes all three.
            EntityPopulationType t = p.PopulationType;
            return t == EntityPopulationType.RandomAmbient
                || t == EntityPopulationType.RandomScenario
                || t == EntityPopulationType.RandomParked
                || t == EntityPopulationType.RandomPatrol;
        }

        /// <summary>
        /// Calls the fire brigade to the front nearest the player, so the player is
        /// present for the struggle. Same CREATE_INCIDENT contract as the 911 menu
        /// (CallEmergencyService): nine arguments, and the out id is only read when the
        /// call returned true, because SHVDN does not zero OutputArgument buffers.
        /// </summary>
        private void ManageDispatch(Vector3 playerPos, int now)
        {
            for (int i = incidents.Count - 1; i >= 0; i--)
            {
                if (burning.Count == 0 || now - incidents[i].CreatedAt > INCIDENT_LIFE_MS)
                {
                    DeleteIncident(incidents[i].Id);
                    incidents.RemoveAt(i);
                }
            }

            if (burning.Count < 3 || incidents.Count >= MAX_INCIDENTS || crews.Count >= MAX_CREWS) return;
            if (lastIncidentAt != 0 && now - lastIncidentAt < INCIDENT_GAP_MS) return;

            Cell front = NearestBurning(playerPos, DESPAWN_DISTANCE);
            if (front == null) return;
            lastIncidentAt = now;

            Vector3 at = World.GetNextPositionOnStreet(front.Pos, true);
            if (at == Vector3.Zero || at.DistanceTo(front.Pos) > 120f) at = front.Pos;

            try
            {
                OutputArgument outId = new OutputArgument();
                bool created = Function.Call<bool>(Hash.CREATE_INCIDENT,
                    FIRE_SERVICE, at.X, at.Y, at.Z, FIRE_UNITS, 3f, outId, 0, 0);
                int id = created ? outId.GetResult<int>() : -1;
                bool valid = id >= 0 && Function.Call<bool>(Hash.IS_INCIDENT_VALID, id);
                if (valid)
                {
                    incidents.Add(new Incident { Id = id, CreatedAt = now, Pos = at });
                    nIncidents++;
                }
                log("wildfire: act=dispatch ok=" + (valid ? 1 : 0) + " id=" + id + " at=" + Fmt(at)
                    + " burning=" + burning.Count);
            }
            catch (Exception ex)
            {
                log("wildfire: act=dispatch ok=0 err=" + ex.Message);
            }
        }

        private static void DeleteIncident(int id)
        {
            // DELETE_INCIDENT on an id the engine no longer knows can crash; check first.
            try
            {
                if (Function.Call<bool>(Hash.IS_INCIDENT_VALID, id))
                    Function.Call(Hash.DELETE_INCIDENT, id);
            }
            catch { }
        }

        // ================================================================
        // Fire crews: claim on arrival, fight, load up, drive away
        // ================================================================

        private void ManageCrews(Vector3 playerPos, int now)
        {
            for (int i = crews.Count - 1; i >= 0; i--)
            {
                Crew cr = crews[i];
                string lost = null;
                if (cr.Truck == null || !cr.Truck.Exists() || cr.Truck.IsDead) lost = "truck-lost";
                else if (Dist2D(cr.Truck.Position, playerPos) > DESPAWN_DISTANCE) lost = "far";
                else if (!AnyAlive(cr)) lost = "crew-lost";
                if (lost != null)
                {
                    Release(cr, lost, now, false);
                    crews.RemoveAt(i);
                    continue;
                }

                if (cr.Returning)
                {
                    if (StepReturn(cr, now)) crews.RemoveAt(i);
                    continue;
                }

                Cell near = NearestBurning(cr.Truck.Position, CREW_WORK_RADIUS);
                if (near != null) cr.ClearSince = 0;
                else if (cr.ClearSince == 0) cr.ClearSince = now;

                if (cr.ClearSince != 0 && now - cr.ClearSince >= CREW_CLEAR_MS) BeginReturn(cr, "clear", now);
                else if (now - cr.ClaimedAt >= CREW_JOB_MS) BeginReturn(cr, "timeout", now);
                else if (near != null) Fight(cr, now);
            }

            if (releasedTrucks.Count > 0)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, int> kv in releasedTrucks)
                    if (now - kv.Value > RELEASED_MEMORY_MS) (stale ?? (stale = new List<int>())).Add(kv.Key);
                if (stale != null) foreach (int h in stale) releasedTrucks.Remove(h);
            }

            if (burning.Count == 0 || crews.Count >= MAX_CREWS) return;

            Vehicle mine = Game.Player.Character.CurrentVehicle;
            foreach (Vehicle v in World.GetNearbyVehicles(playerPos, DESPAWN_DISTANCE, new Model(VehicleHash.FireTruck)))
            {
                if (crews.Count >= MAX_CREWS) break;
                if (v == null || !v.Exists() || v.IsDead || v == mine) continue;
                if (releasedTrucks.ContainsKey(v.Handle) || IsClaimed(v)) continue;
                // Never take a truck a mission or another script owns.
                EntityPopulationType pop = v.PopulationType;
                if (pop == EntityPopulationType.Mission || pop == EntityPopulationType.Permanent
                    || pop == EntityPopulationType.Replay || pop == EntityPopulationType.Tool) continue;
                Ped driver = v.Driver;
                if (driver == null || !driver.Exists() || driver.IsDead) continue;

                Cell c = NearestBurning(v.Position, CREW_CLAIM_RADIUS);
                if (c == null) continue;
                float d = Dist2D(c.Pos, v.Position);
                if (v.Speed > CREW_ARRIVED_SPEED && d > CREW_ARRIVED_DIST) continue;   // still en route
                Claim(v, pop, d, now);
            }
        }

        private bool IsClaimed(Vehicle v)
        {
            foreach (Crew cr in crews) if (cr.Truck == v) return true;
            return false;
        }

        private static bool AnyAlive(Crew cr)
        {
            foreach (Fighter f in cr.Fighters)
                if (f.Ped != null && f.Ped.Exists() && !f.Ped.IsDead) return true;
            return false;
        }

        private void Claim(Vehicle truck, EntityPopulationType pop, float dist, int now)
        {
            Crew cr = new Crew { Truck = truck, ClaimedAt = now };
            // Persistence takes the truck and crew off the engine's dispatch AI. Without it
            // the engine keeps giving them tasks, and they stay parked at the scene doing nothing.
            truck.IsPersistent = true;
            try { truck.IsSirenSilent = true; } catch { }   // lights on, no siren sound at the scene
            foreach (Ped p in truck.Occupants)
            {
                if (p == null || !p.Exists() || p.IsDead) continue;
                p.IsPersistent = true;
                p.BlockPermanentEvents = true;   // no fleeing the fire they came to fight
                p.IsFireProof = true;            // a burning fireman would start a fresh outbreak
                try { p.Weapons.Give(WeaponHash.FireExtinguisher, 9999, true, true); } catch { }
                cr.Fighters.Add(new Fighter { Ped = p, Seat = p.SeatIndex });
            }
            crews.Add(cr);
            nCrewsClaimed++;

            // The incident has done its job, which was getting a truck here. Delete it, or
            // the engine keeps trying to manage the scene.
            int best = -1;
            float bestD = 150f;
            for (int i = 0; i < incidents.Count; i++)
            {
                float di = incidents[i].Pos.DistanceTo(truck.Position);
                if (di < bestD) { bestD = di; best = i; }
            }
            if (best >= 0)
            {
                DeleteIncident(incidents[best].Id);
                incidents.RemoveAt(best);
            }

            log("wildfire: act=crew-claim truck=" + truck.Handle + " pop=" + pop + " crew=" + cr.Fighters.Count
                + " fireDist=" + dist.ToString("F0") + " speed=" + truck.Speed.ToString("F1")
                + " incidentFreed=" + (best >= 0 ? 1 : 0) + " at=" + Fmt(truck.Position));
        }

        private void Fight(Crew cr, int now)
        {
            Vehicle truck = cr.Truck;
            foreach (Fighter f in cr.Fighters)
            {
                Ped p = f.Ped;
                if (p == null || !p.Exists() || p.IsDead) continue;
                bool retaskDue = now - f.TaskedAt >= CREW_RETASK_MS;

                if (p.IsInVehicle())
                {
                    // The driver works the water cannon while fire is in reach of it.
                    if (f.Seat == VehicleSeat.Driver && f.Mode != F_LEAVE)
                    {
                        Cell tc = NearestBurning(truck.Position, CANNON_RANGE);
                        if (tc != null)
                        {
                            if (f.Mode != F_CANNON || f.Target != Key(tc.Ix, tc.Iy) || retaskDue)
                            {
                                bool armed = false;
                                try { armed = p.TrySetVehicleWeapon(VehicleWeaponHash.WaterCannon); } catch { }
                                if (armed)
                                {
                                    Function.Call(Hash.TASK_VEHICLE_SHOOT_AT_COORD, p, tc.Pos.X, tc.Pos.Y, tc.Pos.Z, 0f);
                                    if (f.Mode != F_CANNON)
                                        log("wildfire: act=crew-cannon truck=" + truck.Handle + " fireDist="
                                            + Dist2D(tc.Pos, truck.Position).ToString("F0"));
                                    f.Mode = F_CANNON; f.Target = Key(tc.Ix, tc.Iy); f.TaskedAt = now;
                                    continue;
                                }
                                log("wildfire: act=crew-cannon-refused truck=" + truck.Handle);
                            }
                            else continue;
                        }
                    }
                    if (f.Mode != F_LEAVE || retaskDue)
                    {
                        p.Task.LeaveVehicle(truck, false);
                        f.Mode = F_LEAVE; f.TaskedAt = now;
                    }
                    continue;
                }

                // On foot: the burning cell nearest this fireman, kept to the truck's patch.
                Cell t = NearestBurning(p.Position, CREW_WORK_RADIUS);
                if (t == null || Dist2D(t.Pos, truck.Position) > CREW_WORK_RADIUS + CELL)
                    t = NearestBurning(truck.Position, CREW_WORK_RADIUS);
                if (t == null) continue;
                long key = Key(t.Ix, t.Iy);
                float dx = p.Position.X - t.Pos.X, dy = p.Position.Y - t.Pos.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);

                if (d > CREW_STANDOFF + 1.5f)
                {
                    if (f.Mode != F_GOTO || f.Target != key || retaskDue)
                    {
                        float k = d > 0.01f ? CREW_STANDOFF / d : 0f;
                        Vector3 stand = new Vector3(t.Pos.X + dx * k, t.Pos.Y + dy * k, t.Pos.Z);
                        // Run (2.0), no timeout, 1 m arrival radius, default flags, any heading.
                        Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, p, stand.X, stand.Y, stand.Z,
                            2.0f, -1, 1.0f, 0, 40000.0f);
                        f.Mode = F_GOTO; f.Target = key; f.TaskedAt = now;
                    }
                }
                else if (f.Mode != F_SPRAY || f.Target != key || retaskDue)
                {
                    p.Task.ShootAt(t.Pos, CREW_RETASK_MS + 1000, FiringPattern.FullAuto);
                    f.Mode = F_SPRAY; f.Target = key; f.TaskedAt = now;
                }
            }
        }

        private void BeginReturn(Crew cr, string reason, int now)
        {
            cr.Returning = true;
            cr.Reason = reason;
            cr.ReturnAt = now;
            TaskBoarding(cr, now);
            log("wildfire: act=crew-return truck=" + cr.Truck.Handle + " reason=" + reason
                + " fought=" + ((now - cr.ClaimedAt) / 1000) + "s");
        }

        private void TaskBoarding(Crew cr, int now)
        {
            cr.ReturnTaskedAt = now;
            foreach (Fighter f in cr.Fighters)
            {
                Ped p = f.Ped;
                if (p == null || !p.Exists() || p.IsDead) continue;
                if (p.IsInVehicle(cr.Truck))
                {
                    if (f.Mode == F_CANNON) p.Task.ClearAll();   // stop spraying
                    f.Mode = F_NONE;
                    continue;
                }
                p.Task.ClearAll();
                p.Task.EnterVehicle(cr.Truck, f.Seat, CREW_RETURN_MS, 2f, EnterVehicleFlags.None);
                f.Mode = F_NONE;
            }
        }

        /// <summary>True once the crew has been released.</summary>
        private bool StepReturn(Crew cr, int now)
        {
            bool allAboard = true;
            foreach (Fighter f in cr.Fighters)
            {
                Ped p = f.Ped;
                if (p == null || !p.Exists() || p.IsDead) continue;
                if (!p.IsInVehicle(cr.Truck)) { allAboard = false; break; }
            }
            if (!allAboard && now - cr.ReturnAt < CREW_RETURN_MS)
            {
                if (now - cr.ReturnTaskedAt >= 2 * CREW_RETASK_MS) TaskBoarding(cr, now);
                return false;
            }
            Release(cr, cr.Reason + (allAboard ? "" : "-stragglers"), now, true);
            return true;
        }

        /// <summary>
        /// Gives the truck and crew back to the engine. With drive=true the driver heads
        /// off with traffic first. A truck left parked in view is never culled, which is why
        /// crews used to stay until the player walked away.
        /// </summary>
        private void Release(Crew cr, string reason, int now, bool drive)
        {
            int alive = 0, aboard = 0;
            bool driving = false;
            Vehicle truck = cr.Truck;
            bool truckOk = truck != null && truck.Exists();
            foreach (Fighter f in cr.Fighters)
            {
                Ped p = f.Ped;
                if (p == null || !p.Exists()) continue;
                try
                {
                    if (!p.IsDead)
                    {
                        alive++;
                        if (truckOk && p.IsInVehicle(truck)) aboard++;
                    }
                    p.BlockPermanentEvents = false;
                    p.IsFireProof = false;
                    if (drive && truckOk && !truck.IsDead && !p.IsDead && truck.Driver == p)
                    {
                        p.Task.CruiseWithVehicle(truck, CREW_DEPART_SPEED, VehicleDrivingFlags.DrivingModeStopForVehicles);
                        driving = true;
                    }
                    p.MarkAsNoLongerNeeded();
                }
                catch { }
            }
            if (truckOk)
            {
                try
                {
                    releasedTrucks[truck.Handle] = now;
                    truck.IsSirenActive = false;
                    truck.IsSirenSilent = false;
                    truck.MarkAsNoLongerNeeded();
                }
                catch { }
            }
            nCrewsReleased++;
            log("wildfire: act=crew-release truck=" + (truckOk ? truck.Handle : 0) + " reason=" + reason
                + " alive=" + alive + " aboard=" + aboard + " driving=" + (driving ? 1 : 0)
                + " held=" + ((now - cr.ClaimedAt) / 1000) + "s");
        }

        // ================================================================
        // Queries — drive assist reads these
        // ================================================================

        /// <summary>Distance to the nearest burning cell within radius, or -1.</summary>
        public float NearestBurningDistance(Vector3 p, float radius)
        {
            Cell c = NearestBurning(p, radius);
            return c == null ? -1f : Dist2D(c.Pos, p);
        }

        private Cell NearestBurning(Vector3 p, float radius)
        {
            if (burning.Count == 0) return null;
            int r = (int)Math.Ceiling(radius / CELL);
            int cx = GridOf(p.X), cy = GridOf(p.Y);
            Cell best = null;
            float bestD = radius;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                Cell c;
                if (!cells.TryGetValue(Key(cx + dx, cy + dy), out c) || c.State != BURNING) continue;
                float d = Dist2D(c.Pos, p);
                if (d <= bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private bool NearBurning(Vector3 p, float radius)
        {
            return NearestBurning(p, radius) != null;
        }

        private bool NearAnyCell(Vector3 p, float radius)
        {
            if (cells.Count == 0) return false;
            int r = (int)Math.Ceiling(radius / CELL);
            int cx = GridOf(p.X), cy = GridOf(p.Y);
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                Cell c;
                if (cells.TryGetValue(Key(cx + dx, cy + dy), out c) && c.State != WATER) return true;
            }
            return false;
        }

        // ================================================================
        // Teardown and census
        // ================================================================

        private static void Extinguish(Cell c, byte newState)
        {
            if (c.Fire != null)
            {
                try { c.Fire.Delete(); } catch { }
                c.Fire = null;
            }
            c.State = newState;
        }

        public void ExtinguishAll()
        {
            foreach (Cell c in burning) Extinguish(c, BURNT);
            burning.Clear();
            cells.Clear();
            foreach (Incident i in incidents) DeleteIncident(i.Id);
            incidents.Clear();
            fleeTasked.Clear();
            int now = Game.GameTime;
            foreach (Crew cr in crews) Release(cr, "disable", now, false);
            crews.Clear();
            releasedTrucks.Clear();
        }

        private void Census(Vector3 playerPos)
        {
            if (burning.Count == 0 && nStarted == 0) return;
            int world = 0;
            try { world = FireManager.GetNumberOfFiresInRadius(playerPos, 500f); } catch { }
            log("wildfire-census: burning=" + burning.Count + " ceiling=" + ceiling
                + " worldFires500=" + world + " cells=" + cells.Count
                + " started=" + nStarted + " failed=" + nFailed + " outbreaks=" + nOutbreaks
                + " burnedOut=" + nBurnedOut + " retired=" + nRetired + " doused=" + nDoused
                + " despawned=" + nDespawned + " water=" + nWater + " fled=" + nFled
                + " incidents=" + incidents.Count + "/" + nIncidents
                + " crews=" + crews.Count + " claimed=" + nCrewsClaimed + " released=" + nCrewsReleased
                + " wind=" + World.WindSpeed.ToString("F1") + " rain=" + World.RainLevel.ToString("F2")
                + (douseProbeDisabled ? " douseProbe=off" : ""));
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static int GridOf(float v) { return (int)Math.Floor(v / CELL); }

        private static long Key(int ix, int iy) { return ((long)ix << 32) | (uint)iy; }

        private static float Dist2D(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        private float Jitter() { return (float)(rnd.NextDouble() - 0.5) * CELL * 0.6f; }

        private double Gauss()
        {
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private void Shuffle(List<Candidate> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                Candidate t = list[i]; list[i] = list[j]; list[j] = t;
            }
        }

        private static string Fmt(Vector3 v)
        {
            return v.X.ToString("F0") + "," + v.Y.ToString("F0") + "," + v.Z.ToString("F0");
        }
    }
}
