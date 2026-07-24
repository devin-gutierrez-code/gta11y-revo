using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace GTA
{
    /// <summary>
    /// Value source for the settings rows GET_PROFILE_SETTING cannot see.
    ///
    /// GTA keeps the console-style pages (Audio, Display, Gameplay) in profile
    /// settings, which the reader already speaks. The PC-only pages — Graphics,
    /// Advanced Graphics and the video half of the Graphics pane — live in
    /// Documents\Rockstar Games\GTA V\settings.xml instead, so before v1.4 every
    /// one of those rows spoke a bare name and a blind user had no way to hear
    /// what any of them was set to.
    ///
    /// The pref -> section/key/decode mapping is DATA (menulabels-overrides.json
    /// -> gta11y-menulabels.json -> MenuLabelDb.TrySettingsXml), so filling gaps
    /// after a calibration session is a JSON edit and a redeploy, not a rebuild.
    ///
    /// MapDb / MenuLabelDb contract: Load() never throws, lookups return false on
    /// a miss, LoadStatus is a short diagnostic string for the log. The file is
    /// re-read on each pause-menu open — GTA rewrites it when the menu closes, so
    /// the values are the committed state at open, exactly matching the reader's
    /// existing commit-on-close model.
    /// </summary>
    public static class SettingsXmlDb
    {
        // "section/key" -> raw attribute text, e.g. "graphics/Tessellation" -> "3".
        private static Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string loadStatus = "notLoaded";
        private static int loadCount = 0;

        public static string LoadStatus { get { return loadStatus; } }
        public static int Count { get { return values.Count; } }

        private static string DefaultPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                   + "/Rockstar Games/GTA V/settings.xml";
        }

        /// <summary>Re-read settings.xml. Safe to call on every menu open (the
        /// file is ~2.5 KB). Never throws.</summary>
        public static void Reload()
        {
            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = DefaultPath();
                if (!File.Exists(path))
                {
                    loadStatus = "fileNotFound";
                    values = fresh;
                    return;
                }
                var root = XDocument.Load(path).Root;
                if (root == null)
                {
                    loadStatus = "emptyDoc";
                    values = fresh;
                    return;
                }
                // <Settings><graphics><Tessellation value="3"/>...</graphics>...
                // Some leaves carry their text inline instead of a value attribute
                // (configSource, VideoCardDescription) — take whichever exists.
                foreach (var section in root.Elements())
                {
                    foreach (var leaf in section.Elements())
                    {
                        var attr = leaf.Attribute("value");
                        string v = attr != null ? attr.Value : leaf.Value;
                        if (v == null) continue;
                        fresh[section.Name.LocalName + "/" + leaf.Name.LocalName] = v.Trim();
                    }
                }
                values = fresh;
                loadCount++;
                loadStatus = "ok(" + values.Count + " keys)";
            }
            catch (Exception ex)
            {
                // Reuse MapDb's compact formatter so the log line matches the
                // other data loaders.
                loadStatus = MapDb.FmtLoadError(ex);
                values = fresh;
            }
        }

        public static bool TryRaw(string section, string key, out string raw)
        {
            raw = null;
            if (section == null || key == null) return false;
            return values.TryGetValue(section + "/" + key, out raw);
        }

        /// <summary>
        /// Spoken value for a settings row, or false when this pref has no mapping
        /// or the stored value cannot be decoded confidently. Fail-closed on
        /// purpose: a row that says only its name is a much smaller problem for a
        /// blind user than one that confidently states the wrong value.
        /// </summary>
        public static bool TryValueText(string pref, string optionType, out string text)
        {
            text = null;
            MenuLabelDb.SettingsXmlEntry e;
            if (!MenuLabelDb.TrySettingsXml(pref, out e)) return false;
            string raw;
            if (!TryRaw(e.section, e.key, out raw) || string.IsNullOrEmpty(raw)) return false;

            try
            {
                switch (e.mode)
                {
                    case "index":
                        {
                            int idx;
                            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                                return false;
                            return MenuLabelDb.TryOptionWord(optionType, idx, out text);
                        }

                    case "lookup":
                        {
                            // settings.xml stores real values, not indices, for some
                            // keys (AnisotropicFiltering is literally 16), so map the
                            // literal onto the option word list.
                            int idx;
                            if (e.lookup == null || !e.lookup.TryGetValue(raw, out idx)) return false;
                            return MenuLabelDb.TryOptionWord(optionType, idx, out text);
                        }

                    case "bool":
                        {
                            bool on;
                            if (!bool.TryParse(raw, out on))
                            {
                                int n;
                                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                                    return false;
                                on = n != 0;
                            }
                            // Prefer the game's own localized On/Off wording.
                            if (MenuLabelDb.TryOptionWord("MENU_OPTION_DISPLAY_ON_OFF", on ? 1 : 0, out text))
                                return true;
                            text = on ? "on" : "off";
                            return true;
                        }

                    case "pct":
                        {
                            float f;
                            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                                return false;
                            // System.Math, not GTA.Math (the SHVDN vector namespace
                            // shadows it inside `namespace GTA`).
                            text = (int)System.Math.Round(f * 100f) + " percent";
                            return true;
                        }

                    case "number":
                        {
                            int n;
                            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                                return false;
                            text = (n + e.add).ToString(CultureInfo.InvariantCulture);
                            if (!string.IsNullOrEmpty(e.unit)) text += " " + e.unit;
                            return true;
                        }

                    case "res":
                        {
                            // The row is one line but the file stores width and height
                            // separately; key names the width element.
                            string h;
                            if (!TryRaw(e.section, "ScreenHeight", out h)) return false;
                            text = raw + " by " + h;
                            return true;
                        }
                }
            }
            catch { }
            return false;
        }
    }
}
