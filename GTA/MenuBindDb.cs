using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GTA
{
    /// <summary>
    /// Learned PREF_* -> profile-setting-id bindings, discovered at runtime and
    /// persisted across sessions.
    ///
    /// Why this exists: 110 of the 149 settings rows the reader can NAME had no
    /// way to say what they were SET to. Two thirds of the pause menu was
    /// navigable but not operable for a blind user. The values are all sitting in
    /// the profile store — ScanProfileSettings has read ids 0-1023 since v1.1 —
    /// but nothing connected a row to its id. gta11y-menulabels.json ships only 19
    /// profilePrefs entries, of which 5 carry a pref name, because the only way to
    /// establish the mapping is to observe a change: no offline source pairs
    /// eMenuPref with a profile-setting id, and the ids are build-specific anyway.
    ///
    /// So the binding is LEARNED. When the user presses Left/Right on a focused
    /// row and exactly one profile id moves, that id belongs to that row's pref.
    /// One press per row, once ever, and the row speaks its value forever after —
    /// including in future sessions, which is what this file is for.
    ///
    /// Trust order (GTA11Y.TryProfileIdFor): a pin shipped in
    /// gta11y-menulabels.json always wins; this file only ever fills gaps. That
    /// keeps a bad learned binding recoverable by data, and lets the offline
    /// tools/bind-profile-ids.py promote confirmed learnings into the shipped
    /// table without the two sources ever fighting.
    ///
    /// MapDb / MenuLabelDb / SettingsXmlDb contract: Load() never throws, lookups
    /// return false on a miss, LoadStatus is a short diagnostic string for the log.
    /// </summary>
    public static class MenuBindDb
    {
        private static Dictionary<string, int> bindings = new Dictionary<string, int>(StringComparer.Ordinal);
        private static string loadStatus = "notLoaded";
        private static bool loaded = false;
        private static bool dirty = false;
        private static int learnedThisSession = 0;

        public static string LoadStatus { get { return loadStatus; } }
        public static int Count { get { return bindings.Count; } }
        public static int LearnedThisSession { get { return learnedThisSession; } }

        private static string DefaultPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                   + "/Rockstar Games/GTA V/ModSettings/gta11y-menubind.json";
        }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string path = DefaultPath();
                if (!File.Exists(path))
                {
                    loadStatus = "none(0)";
                    return;
                }
                var root = JsonConvert.DeserializeObject<BindJsonRoot>(File.ReadAllText(path));
                if (root == null || root.bindings == null)
                {
                    loadStatus = "nullData";
                    return;
                }
                foreach (var kv in root.bindings)
                {
                    // Defend against a hand-edited file: ids outside the scanned
                    // window would index past mlProfileSnap.
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value < 0 || kv.Value > 1023) continue;
                    bindings[kv.Key] = kv.Value;
                }
                loadStatus = "ok(" + bindings.Count + ")";
            }
            catch (Exception ex)
            {
                loadStatus = MapDb.FmtLoadError(ex);
            }
        }

        public static bool TryGet(string pref, out int id)
        {
            id = 0;
            return !string.IsNullOrEmpty(pref) && bindings.TryGetValue(pref, out id);
        }

        public static bool Has(string pref)
        {
            return !string.IsNullOrEmpty(pref) && bindings.ContainsKey(pref);
        }

        /// <summary>Record a newly observed binding. Idempotent; re-binding the
        /// same pref to a different id wins (the newer observation is the live
        /// build's), and marks the file dirty for the next Save().</summary>
        public static void Bind(string pref, int id)
        {
            if (string.IsNullOrEmpty(pref) || id < 0 || id > 1023) return;
            int existing;
            if (bindings.TryGetValue(pref, out existing) && existing == id) return;
            bindings[pref] = id;
            learnedThisSession++;
            dirty = true;
        }

        /// <summary>Write the file if anything changed. Called on menu close, not
        /// per binding — a settings walk can produce a dozen bindings and the
        /// pause menu is the one moment we know the player is not driving.
        /// Never throws: losing a learned binding costs one keypress next
        /// session, while an exception out of OnTick costs the session.</summary>
        public static void Save(Action<string> log)
        {
            if (!dirty) return;
            try
            {
                string path = DefaultPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var root = new BindJsonRoot
                {
                    version = 1,
                    generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    note = "Learned by GTA11Y menu reader v1.5. Safe to delete: it re-learns "
                         + "on the next Left/Right press per row. Promote confirmed entries into "
                         + "tools/menulabels-overrides.json with tools/bind-profile-ids.py.",
                    bindings = bindings
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(root, Formatting.Indented));
                dirty = false;
                if (log != null) log("EVENT reader-bind-save: count=" + bindings.Count
                    + " new=" + learnedThisSession);
            }
            catch (Exception ex)
            {
                if (log != null) log("EVENT reader-bind-save: ERR " + MapDb.FmtLoadError(ex));
            }
        }

        private class BindJsonRoot
        {
            public int version = 0;
            public string generated = null;
            public string note = null;
            public Dictionary<string, int> bindings = null;
        }
    }
}
