using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using GTA;
using GTA.Native;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// Turns a model hash into something worth hearing.
    ///
    /// Replaces the raw <c>Dictionary&lt;string,string&gt; hashes</c> read out of
    /// <c>scripts/hashes.txt</c>, which had three problems that compounded into silence:
    ///
    ///   1. It was keyed by <c>Model.NativeValue.ToString()</c>. <see cref="Model.NativeValue"/>
    ///      is a <c>UInt64</c>, so a negative model hash sign-extended — <c>adder</c>,
    ///      -1216765807, arrived as "18446744072492785809" and could never match the
    ///      "-1216765807" row. Half the table (26,825 of 53,859 rows) was unreachable.
    ///   2. The file held no ambient pedestrian models at all — zero rows matching
    ///      a_m_*, a_f_*, s_m_*, s_f_*, g_m_* or g_f_* — so no passer-by was ever nameable.
    ///   3. <c>hashes.ContainsKey(...)</c> was a term in the scans' <c>valid</c> predicate,
    ///      so an unnamed entity was not merely unnamed, it was DELETED from the readout.
    ///      A crowded pavement and an empty one sounded identical.
    ///
    /// The rule this class enforces instead: <b>naming never decides whether an entity is
    /// announced.</b> Every Describe* method returns a non-empty string on every path.
    /// Worst case the player hears "unknown object, about two metres wide" — which is
    /// still navigable information, and which the gap log then turns into a real name on
    /// the next <c>tools/build-modelnames.py</c> run.
    ///
    /// Vehicles never consult the table: the game names them itself through
    /// GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, localized, for every DLC ever shipped.
    /// </summary>
    public static class ModelNames
    {
        // ===================================================================
        // Table
        // ===================================================================

        private struct Row
        {
            public char kind;        // p ped, o object, d door/gate, x legacy other
            public string name;
            public string gxt;       // ped rows only; resolved live so the name localizes
            // v2: the generator could not vouch for `name` out loud — it is a map
            // archetype id ("Ch3 07 Decals50") or a raw model stem ("Isldj"). The model
            // is KNOWN, so this is not a gap; we simply describe it instead of reading
            // a string that means nothing to a listener. See build-modelnames.py.
            public bool machine;
        }

        private const int SCHEMA_VERSION = 2;
        private const string PATH = "scripts/gta11y-modelnames.tsv.gz";

        private static Dictionary<int, Row> rows = new Dictionary<int, Row>();
        private static bool loaded = false;
        private static string loadStatus = "notLoaded";
        private static string generatedStamp = "";

        public static bool IsLoaded { get { return loaded && loadStatus == "ok"; } }
        public static string LoadStatus { get { return loadStatus; } }
        public static int RowCount { get { return rows.Count; } }
        public static string GeneratedStamp { get { return generatedStamp; } }

        /// <summary>Loads scripts/gta11y-modelnames.tsv.gz. Safe to call repeatedly.
        /// On failure the table stays empty and every lookup falls through to a derived
        /// description, matching MapDb.Load's contract — a data problem degrades the mod,
        /// it never stops it starting.</summary>
        public static void Load()
        {
            if (loaded) return;
            try
            {
                if (!File.Exists(PATH))
                {
                    loadStatus = "fileNotFound";
                    loaded = true;
                    return;
                }

                int version = 0, parsed = 0, malformed = 0;
                using (var fs = File.OpenRead(PATH))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (line[0] == '#')
                        {
                            // "#gta11y-modelnames v1" / "#generated <utc> by ..."
                            if (version == 0 && line.StartsWith("#gta11y-modelnames v"))
                                int.TryParse(line.Substring(20).Trim(), out version);
                            else if (line.StartsWith("#generated "))
                                generatedStamp = line.Substring(11).Trim();
                            continue;
                        }

                        // kind \t hash \t name \t extra \t flags. Split with a cap so a
                        // name that somehow contains a tab cannot shift the columns.
                        string[] f = line.Split(new[] { '\t' }, 5);
                        int hash;
                        if (f.Length < 3 || f[0].Length != 1
                            || !int.TryParse(f[1], NumberStyles.AllowLeadingSign,
                                             CultureInfo.InvariantCulture, out hash)
                            || f[2].Length == 0)
                        {
                            malformed++;
                            continue;
                        }
                        rows[hash] = new Row
                        {
                            kind = f[0][0],
                            name = f[2],
                            gxt = (f.Length > 3 && f[3].Length > 0) ? f[3] : null,
                            machine = f.Length > 4 && f[4].IndexOf('m') >= 0,
                        };
                        parsed++;
                    }
                }

                if (version != SCHEMA_VERSION)
                {
                    // A table from a different generator is worse than none: its rows
                    // could mean something else entirely, and this text is spoken.
                    rows.Clear();
                    loadStatus = "schemaV" + version + "expectedV" + SCHEMA_VERSION;
                    loaded = true;
                    return;
                }
                if (parsed == 0)
                {
                    loadStatus = "empty";
                    loaded = true;
                    return;
                }

                loadStatus = malformed > 0 ? "ok(" + malformed + "malformed)" : "ok";
                loaded = true;
            }
            catch (Exception ex)
            {
                loadStatus = MapDb.FmtLoadError(ex);
                loaded = true;
            }
        }

        /// <summary>The one correct way to key this table. Model.Hash is Int32; going
        /// through NativeValue (UInt64) sign-extends and silently matches nothing —
        /// that bug is the reason this class exists.</summary>
        private static int HashOf(Entity e)
        {
            try { return e.Model.Hash; }
            catch { return 0; }
        }

        private static bool TryRow(int hash, out Row row)
        {
            return rows.TryGetValue(hash, out row);
        }

        // ===================================================================
        // Vehicles — the game names these, always
        // ===================================================================

        /// <summary>"Tornado", "Police Cruiser". Never consults the table: a bundled name
        /// would be stale the day a DLC ships, while the native is correct forever and
        /// already localized. Falls back to the vehicle CLASS, then to the bare word,
        /// so this is never empty.</summary>
        public static string DescribeVehicle(Vehicle v)
        {
            if (v == null) return "vehicle";
            string n = Clean(TryGet(() => v.LocalizedName));
            if (n != null) return n;
            n = Clean(TryGet(() => v.DisplayName));
            if (n != null) return n;
            n = Clean(TryGet(() => v.ClassLocalizedName));
            if (n != null) return n;
            n = Clean(TryGet(() => v.ClassDisplayName));
            if (n != null) return n;
            return "vehicle";
        }

        /// <summary>The same three rejections MenuLabelDb.ResolveGxt applies to a GXT
        /// lookup: empty, the literal "NULL", and a GTA formatting token. A vehicle with
        /// no label hands back "CARNOTFOUND" or the key itself, and speaking either is
        /// worse than saying "vehicle".</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s == "NULL" || s == "CARNOTFOUND") return null;
            if (s.IndexOf('~') >= 0) return null;
            return s;
        }

        private static string TryGet(Func<string> f)
        {
            // A despawning entity is exactly when a property read throws — the same
            // reasoning as SafeIsVehicleOnFire/SafePetrolTankHealth in GTA11Y.cs.
            try { return f(); } catch { return null; }
        }

        // ===================================================================
        // Peds
        // ===================================================================

        /// <summary>"Marine", "Paramedic", "Coyote", "Man". Table first (with the ped's
        /// director-mode GXT key resolved live so a German player hears "Wildschwein"),
        /// then everything the game will tell us about the ped directly.</summary>
        public static string DescribePed(Ped p)
        {
            if (p == null) return "person";
            int hash = HashOf(p);

            Row row;
            if (TryRow(hash, out row))
            {
                // A machine row means the model is known but its name is an internal
                // stem. Describe the ped instead — and do NOT log a gap, because
                // nothing is missing and a gaps log full of models we chose not to
                // name is a gaps log nobody reads.
                if (row.machine) return DerivePed(p);

                if (row.gxt != null)
                {
                    string localized = Clean(MenuLabelDb.ResolveGxt(row.gxt));
                    if (localized != null) return localized;
                }
                if (!string.IsNullOrEmpty(row.name)) return row.name;
            }

            NoteGap(hash, 'p', p);
            return DerivePed(p);
        }

        /// <summary>What the game itself knows about a ped when the table does not know
        /// its model — which is the only state a brand-new DLC ped can be in.</summary>
        private static string DerivePed(Ped p)
        {
            try
            {
                if (p.Model.IsAnimalPed) return "animal";

                switch (p.PedType)
                {
                    // iter-60: Model.IsAnimalPed above catches most animals, but it is a
                    // model-flag test and a DLC creature that misses it fell all the way
                    // through to Gender and came out "man".
                    case PedType.Animal:    return "animal";
                    case PedType.Cop:
                    case PedType.Swat:      return "police officer";
                    case PedType.Medic:     return "paramedic";
                    case PedType.Fire:      return "firefighter";
                    case PedType.Army:      return "soldier";
                    case PedType.Dealer:    return "dealer";
                    case PedType.Criminal:  return "criminal";
                    case PedType.Bum:       return "vagrant";
                    case PedType.Prostitute: return "sex worker";
                    case PedType.Player0:   return "Michael";
                    case PedType.Player1:   return "Franklin";
                    case PedType.Player2:   return "Trevor";
                }

                // GangAlbanian(7) .. GangPuertoRican(18) is one contiguous run; naming
                // the specific gang from the enum would be guessing at a hash the table
                // is the authority on, so say the useful part and stop.
                int pt = (int)p.PedType;
                if (pt >= (int)PedType.GangAlbanian && pt <= (int)PedType.GangPuertoRican)
                    return "gang member";

                if (p.Gender == Gender.Female) return "woman";
                if (p.Gender == Gender.Male) return "man";
            }
            catch { }
            return "person";
        }

        // ===================================================================
        // Props, doors and gates
        // ===================================================================

        /// <summary>"Soda Machine", "Wooden Gate". Never empty — an unnamed prop is
        /// described by its size rather than dropped.</summary>
        public static string DescribeProp(Prop p)
        {
            if (p == null) return "object";
            int hash = HashOf(p);

            Row row;
            bool isDoor = false;
            if (TryRow(hash, out row))
            {
                isDoor = row.kind == 'd';
                // See DescribePed: known model, unsayable name, so describe rather
                // than read it, and log no gap.
                if (row.machine) return DeriveProp(p, isDoor);
                if (!string.IsNullOrEmpty(row.name)) return row.name;
            }

            NoteGap(hash, 'o', p);
            return DeriveProp(p, isDoor);
        }

        /// <summary>Generic lookup for logs, the weapon path and anything holding a bare
        /// hash. Returns null when nothing is known, so callers can choose their own
        /// fallback — unlike the entity-taking overloads, which must never return null.
        /// </summary>
        public static string DescribeModel(int hash)
        {
            Row row;
            if (TryRow(hash, out row) && !string.IsNullOrEmpty(row.name))
            {
                // Machine rows return null like an outright miss: this overload exists
                // so callers can pick their own fallback, and an internal stem is not
                // a name any of them want.
                if (row.machine) return null;
                if (row.gxt != null)
                {
                    string localized = Clean(MenuLabelDb.ResolveGxt(row.gxt));
                    if (localized != null) return localized;
                }
                return row.name;
            }
            return null;
        }

        /// <summary>Size, not silence. A door we cannot name is still a door — saying
        /// "unknown object" of a thing the player can walk through is a worse answer
        /// than the category alone.</summary>
        private static string DeriveProp(Prop p, bool isDoor)
        {
            string noun = isDoor ? "door or gate" : "unknown object";
            try
            {
                // GetDimensions(out, out) rather than the Dimensions property: the
                // property returns a ValueTuple, which net48 only has via a package
                // reference this project does not carry.
                //
                // X/Y — the FOOTPRINT, matching PropDetailClause. iter-58 measured X/Z
                // here and X/Y there, so a detailed scan of an unnamed prop spoke two
                // different sizes for the same object in one breath.
                GTA.Math.Vector3 lo, hi;
                p.Model.GetDimensions(out lo, out hi);
                float span = Math.Max(Math.Abs(hi.X - lo.X), Math.Abs(hi.Y - lo.Y));
                if (span > 0.1f)
                    return noun + ", about " + SpokenMetres(span) + " across";
            }
            catch { }
            return noun;
        }

        /// <summary>True when DescribeProp already put a size in the sentence, so the
        /// detailed-scan clause does not say it a second time.</summary>
        public static bool NameCarriesSize(Prop p)
        {
            if (p == null) return false;
            Row row;
            if (!TryRow(HashOf(p), out row)) return true;   // unknown -> derived -> sized
            return row.machine;
        }

        /// <summary>Spoken, not printed — "half a metre" beats "0.5 metres" out loud, and
        /// a decimal on a rough size estimate implies a precision that is not there.</summary>
        private static string SpokenMetres(float m)
        {
            if (m < 0.75f) return "half a metre";
            int n = (int)Math.Round(m);
            if (n <= 1) return "a metre";
            return n + " metres";
        }

        /// <summary>Is this prop a door or a gate?
        ///
        /// Asks the game first: DOOR_SYSTEM_FIND_EXISTING_DOOR returns non-zero for
        /// anything the door system owns, whatever the model happens to be called. That
        /// matters because the previous test was a substring match on the model name, so
        /// a DLC gate whose name was not in hashes.txt was invisible to the door scan.
        /// The table's isDoor flag is the fallback for doors the system does not own.</summary>
        public static bool IsDoorLike(Prop p)
        {
            if (p == null) return false;
            int doorHash;
            if (TryFindDoor(p, out doorHash)) return true;

            Row row;
            return TryRow(HashOf(p), out row) && row.kind == 'd';
        }

        /// <summary>Locate this prop in the door system. The native is
        /// <c>BOOL DOOR_SYSTEM_FIND_EXISTING_DOOR(x, y, z, modelHash, Hash* doorOut)</c> —
        /// the out argument is only meaningful when it returns true, so the bool is
        /// checked before the result is read. Reading an OutputArgument the native never
        /// wrote gives garbage, and garbage here would become a confident wrong statement
        /// about whether a door is locked.</summary>
        private static bool TryFindDoor(Prop p, out int doorHash)
        {
            doorHash = 0;
            try
            {
                var pos = p.Position;
                var outDoor = new OutputArgument();
                bool found = Function.Call<bool>(Hash.DOOR_SYSTEM_FIND_EXISTING_DOOR,
                                                 pos.X, pos.Y, pos.Z, HashOf(p), outDoor);
                if (!found) return false;
                doorHash = outDoor.GetResult<int>();
                return doorHash != 0;
            }
            catch { doorHash = 0; return false; }
        }

        /// <summary>Door state for the detailed scan: "open", "closed", "locked".
        /// Returns null when the door system does not own this door — an unregistered
        /// door's state cannot be read, and guessing it is exactly the kind of confident
        /// wrong answer this mod must not give.</summary>
        public static string DoorState(Prop p)
        {
            if (p == null) return null;
            int door;
            if (!TryFindDoor(p, out door)) return null;
            try
            {
                // 0 = unlocked/free; every other state is a script lock or a forced
                // open/shut hold, none of which the player can simply walk through, so
                // they all report as locked. The open ratio is signed — a door swung the
                // other way reads negative — hence the absolute value.
                int state = Function.Call<int>(Hash.DOOR_SYSTEM_GET_DOOR_STATE, door);
                float ratio = Function.Call<float>(Hash.DOOR_SYSTEM_GET_OPEN_RATIO, door);
                if (state != 0) return "locked";
                if (Math.Abs(ratio) > 0.05f) return "open";
                return "closed";
            }
            catch { return null; }
        }

        // ===================================================================
        // Gap log
        // ===================================================================
        //
        // Every hash that falls through to a derived description is recorded once. This
        // is the input to the next build-modelnames.py run — the same role gaps.md plays
        // for the menu reader, and the only way anyone finds out WHICH models a player
        // actually meets and cannot have named. Strictly bounded and entirely silent on
        // failure: an instrumentation file must never be able to disturb a session.

        private const int GAP_CAP = 500;
        private const long GAP_FLUSH_TICKS = 60L * 1000L * 10000L;   // 60 s

        private static readonly Dictionary<int, int> gapCounts = new Dictionary<int, int>();
        private static readonly Dictionary<int, char> gapKinds = new Dictionary<int, char>();
        // iter-63: WHERE it was first seen. Without this the gap file is a list of
        // bare hashes — 08-17 logged two unnamed props, neither resolvable against the
        // shipped table, and there was no way to go and look at them. A hash you
        // cannot locate is a hash you cannot name.
        private static readonly Dictionary<int, string> gapWhere = new Dictionary<int, string>();
        private static bool gapDirty = false;
        private static bool gapWriteFailed = false;
        private static long gapNextFlushTicks = 0;

        public static int GapCount { get { return gapCounts.Count; } }

        private static void NoteGap(int hash, char kind, Entity where)
        {
            if (hash == 0) return;
            int n;
            if (gapCounts.TryGetValue(hash, out n))
            {
                gapCounts[hash] = n + 1;
                gapDirty = true;
                return;
            }
            if (gapCounts.Count >= GAP_CAP) return;   // bounded; never grows without limit
            gapCounts[hash] = 1;
            gapKinds[hash] = kind;
            // First sighting only, so the native call is bounded by GAP_CAP for the
            // whole session. Naming must never be able to throw (see the class note).
            string loc = "-\t-";
            try
            {
                if (where != null && where.Exists())
                {
                    var pos = where.Position;
                    string street = World.GetStreetName(pos);
                    loc = pos.X.ToString("F1", CultureInfo.InvariantCulture) + ","
                        + pos.Y.ToString("F1", CultureInfo.InvariantCulture) + ","
                        + pos.Z.ToString("F1", CultureInfo.InvariantCulture)
                        + "\t" + (string.IsNullOrEmpty(street) ? "-" : street);
                }
            }
            catch { }
            gapWhere[hash] = loc;
            gapDirty = true;
        }

        /// <summary>Call once per onTick. Rewrites the gap file at most once a minute
        /// (rewrite, not append, so no abort hook is needed to get a complete file).</summary>
        public static void Tick(long nowTicks)
        {
            if (!gapDirty || gapWriteFailed) return;
            if (nowTicks < gapNextFlushTicks) return;
            gapNextFlushTicks = nowTicks + GAP_FLUSH_TICKS;
            Flush();
        }

        /// <summary>Write the gap file now. Safe to call from an abort handler.</summary>
        public static void Flush()
        {
            if (!gapDirty || gapWriteFailed) return;
            gapDirty = false;
            try
            {
                var sb = new StringBuilder();
                sb.Append("# gta11y unnamed model hashes — ")
                  .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .AppendLine();
                sb.AppendLine("# Every model this session met that the name table could not");
                sb.AppendLine("# name. Feed these to tools/build-modelnames.py; correct the");
                sb.AppendLine("# ones it still cannot name in tools/modelnames-overrides.json.");
                sb.Append("# table: ").Append(loadStatus)
                  .Append(", ").Append(rows.Count).Append(" rows, generated ")
                  .Append(generatedStamp.Length > 0 ? generatedStamp : "unknown")
                  .AppendLine();
                sb.Append("# ").Append(gapCounts.Count).Append(" distinct");
                if (gapCounts.Count >= GAP_CAP)
                    sb.Append(" (CAP REACHED — later gaps were not recorded)");
                sb.AppendLine();
                sb.AppendLine("# firstSeenAt/street are where the model was FIRST met —");
                sb.AppendLine("# go there and look at it before naming it.");
                sb.AppendLine("kind\thash\ttimesSeen\tfirstSeenAt\tstreet");
                foreach (var kv in gapCounts)
                {
                    char k;
                    if (!gapKinds.TryGetValue(kv.Key, out k)) k = '?';
                    string w;
                    if (!gapWhere.TryGetValue(kv.Key, out w)) w = "-\t-";
                    sb.Append(k).Append('\t').Append(kv.Key).Append('\t')
                      .Append(kv.Value).Append('\t').Append(w).AppendLine();
                }
                File.WriteAllText(
                    "gta11y-modelgaps-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log",
                    sb.ToString());
            }
            catch
            {
                // Disk full, AV lock, read-only install — stop trying rather than
                // retrying every minute for the rest of the session.
                gapWriteFailed = true;
            }
        }
    }
}
