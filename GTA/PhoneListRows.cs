using System;
using System.Collections.Generic;
using System.Text;
using GTA;
using GTA.Native;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// In-app row labels for the phone's LIST apps — Email, Messages, Contacts and
    /// Internet — read from the shared row buffer the scaleform is actually fed from.
    ///
    /// Why this exists, and why it is not another page probe:
    ///
    /// PhoneRowText walks `Global_10324[char][page].f_7[]`, which is the right array
    /// for the SETTINGS-shaped screens, and it has to guess which page an app owns.
    /// On 08-11-182121 that guess picked the same page — the phone's theme-colour
    /// list — for appemail, appsettings AND appcontacts, so Email read out "Blue,
    /// Blue, Green, Red, Orange, Gray, Purple, Pink, Orange Herringbone" and phone
    /// Settings read the same. Messages had no page at all and spoke "row 0, row 1,
    /// row 2, row 3". Contacts only sounded right because PhoneGlobals rescued it.
    ///
    /// The decompiled scripts show the list apps do not use that array at all. They
    /// use a separate, shared row buffer:
    ///
    ///   apptextmessage.c:854-864  (func_14, the row writer)
    ///     StringCopy(&amp;Global_9509[slot /*15*/],      sParam1, 16);   // primary key
    ///     Global_9509[slot /*15*/].f_4  = uParam2;                    // icon/colour
    ///     StringCopy(&amp;Global_9509[slot /*15*/].f_5, sParam3, 16);   // secondary key
    ///     Global_9509[slot /*15*/].f_9  = GET_HASH_KEY(sParam3);
    ///     Global_9509[slot /*15*/].f_10 = uParam4;
    ///     Global_9509[slot /*15*/].f_11 = iParam5;                    // row id
    ///
    ///   apptextmessage.c:840-851  (the row is then handed straight to the display)
    ///     BEGIN_SCALEFORM_MOVIE_METHOD(movie, "SET_DATA_SLOT");
    ///     ... ADD_PARAM_INT(Global_9509[slot].f_4);
    ///     ... ADD_PARAM_INT(Global_9509[slot].f_10);
    ///     func_10(&amp;Global_9509[slot]);
    ///
    /// So `Global_9509[slot]` IS what the phone is displaying — not a table that
    /// happens to look like it. Reference counts in the corpus: appemail.c 44,
    /// appcontacts.c 44, appinternet.c 25, apptextmessage.c (where func_14 lives),
    /// and appsettings.c ZERO — which is exactly the split we observe in the field,
    /// and is why Settings stays with PhoneRowText.
    ///
    /// TWO THINGS ARE NOT CONFIRMED on this build, and both are settled by one play
    /// session rather than by guessing:
    ///
    ///   1. The display-row -&gt; slot mapping. The buffer is written by slot id and
    ///      `.f_11` looks like the row id (apptextmessage.c:969 matches against it).
    ///      Identity is assumed until the dump says otherwise.
    ///   2. Whether every row's string resolves. The fields are 16-byte StringCopy
    ///      targets — a GXT key's shape — and `.f_9` being GET_HASH_KEY(.f_5)
    ///      supports it. But a message list carries player-specific text, and
    ///      PhoneGlobals.cs:9-14 records that sender/subject text is not exposed by
    ///      any native. Honest expectation: row labels yes, message bodies no.
    ///
    /// Hence Dump(): it writes the raw key, the resolved text and .f_11 for every
    /// slot on each app open, so the next menulog answers both questions at once.
    /// Ships in the same build as the reader deliberately — a session that still
    /// mis-reads but dumps cleanly is a successful session.
    ///
    /// Same fail-closed discipline as PhoneGlobals and PhoneRowText: every walk is
    /// bounds-checked, nothing unresolvable is ever spoken, no exception reaches
    /// OnTick, and a read fuse stands the whole class down after repeated failures.
    /// </summary>
    static class PhoneListRows
    {
        // Decompiled-corpus layout, overridable as data through the phoneGlobals block
        // in menulabels-overrides.json so a moved base is a JSON edit and a redeploy
        // rather than a rebuild — same contract PhoneRowText uses.
        private static int rowBase = 9509;     // Global_9509
        private static int rowStride = 15;     // array element size
        private static int secondField = 5;    // .f_5, the secondary key
        private static int idField = 11;       // .f_11, the row id

        private const int MaxRow = 20;         // deepest list we have seen, plus slack

        private static bool configured = false;
        private static int errors = 0;

        /// <summary>The apps whose rows live in this buffer. appsettings is absent on
        /// purpose: it has zero references to Global_9509 in the corpus and is served
        /// by PhoneRowText's page array instead.</summary>
        private static readonly HashSet<string> ListApps = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "appemail", "apptextmessage", "apptext", "appsms",
            "appcontacts", "appinternet",
        };

        public static bool Ok { get { return errors < 10; } }

        public static bool HandlesApp(string script)
        {
            return script != null && ListApps.Contains(script);
        }

        private static void Configure()
        {
            if (configured) return;
            configured = true;
            int v;
            if (MenuLabelDb.TryPhoneGlobal("listRowBase", out v)) rowBase = v;
            if (MenuLabelDb.TryPhoneGlobal("listRowStride", out v)) rowStride = v;
            if (MenuLabelDb.TryPhoneGlobal("listRowSecondField", out v)) secondField = v;
            if (MenuLabelDb.TryPhoneGlobal("listRowIdField", out v)) idField = v;
        }

        public static void Reset()
        {
            configured = false;
            errors = 0;
        }

        /// <summary>Spoken label for a list-app row, or false to fall back. Speaks
        /// "&lt;primary&gt;, &lt;secondary&gt;" when the secondary resolves to something
        /// different — on a message list that is the sender and the preview, and
        /// hearing only one of them is the difference between a usable inbox and a
        /// list of names.</summary>
        public static bool TryRowLabel(int row, out string label)
        {
            label = null;
            Configure();
            if (!Ok || row < 0 || row >= MaxRow) return false;
            try
            {
                string primary = ResolveAt(row, -1);
                if (primary == null) return false;
                string secondary = ResolveAt(row, secondField);
                label = (secondary != null && !string.Equals(secondary, primary,
                            StringComparison.OrdinalIgnoreCase))
                        ? primary + ", " + secondary
                        : primary;
                return true;
            }
            catch { errors++; return false; }
        }

        /// <summary>Reads one 16-byte TEXT_LABEL out of the row and resolves it.
        /// `field` &lt; 0 means the row's own base offset (the primary key).</summary>
        private static string ResolveAt(int row, int field)
        {
            string key = RawAt(row, field);
            if (key == null) return null;
            return MenuLabelDb.ResolveGxt(key);
        }

        private static string RawAt(int row, int field)
        {
            int depth = (field < 0 ? 0 : field) + 4;   // TEXT_LABEL_15 = 4 script words
            if (!RangeAllocated(row, depth)) return null;
            GlobalVariable g = Row(row);
            string s = (field < 0 ? g : g.GetStructField(field)).Read<string>();
            return LooksLikeLabelKey(s) ? s : null;
        }

        private static GlobalVariable Row(int row)
        {
            return GlobalVariable.Get(rowBase).GetArrayItem(row, rowStride);
        }

        /// <summary>Same guard PhoneRowText uses: prove the highest word this read will
        /// touch is inside allocated script-global space before touching any of it.
        /// A wrong base index must degrade to silence, never to a crash.</summary>
        private static bool RangeAllocated(int row, int fieldDepth)
        {
            try
            {
                int highest = rowBase + (rowStride * (row + 1)) + fieldDepth + 1;
                return GlobalVariable.Get(highest).MemoryAddress != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// <summary>GXT-key shape test, identical to the one PhoneGlobals and
        /// PhoneRowText apply: upper case, digits and underscores, at least one
        /// underscore. Rejects the zeroed and numeric-garbage regions.</summary>
        private static bool LooksLikeLabelKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3 || s.Length > 24) return false;
            bool underscore = false;
            foreach (char c in s)
            {
                if (c == '_') { underscore = true; continue; }
                if (c >= 'A' && c <= 'Z') continue;
                if (c >= '0' && c <= '9') continue;
                return false;
            }
            return underscore;
        }

        /// <summary>One dump per app open: raw key, resolved text and .f_11 per slot.
        /// This is the whole verification plan for both open questions — the row-to-slot
        /// mapping and whether the strings resolve — so it runs whenever the menu log is
        /// on, gate or no gate, exactly as PhoneRowText's probe does.</summary>
        public static void Dump(string script, Action<string> log)
        {
            if (log == null) return;
            Configure();
            try
            {
                int resolved = 0;
                StringBuilder sb = new StringBuilder();
                for (int r = 0; r < MaxRow; r++)
                {
                    string key = RawAt(r, -1);
                    if (key == null) continue;
                    string txt = MenuLabelDb.ResolveGxt(key);
                    string key2 = RawAt(r, secondField);
                    string txt2 = key2 == null ? null : MenuLabelDb.ResolveGxt(key2);
                    int id = ReadId(r);
                    if (txt != null) resolved++;
                    sb.Length = 0;
                    sb.Append("  slot ").Append(r)
                      .Append(" f11=").Append(id)
                      .Append(" key=\"").Append(key).Append("\"")
                      .Append(" text=\"").Append(txt ?? "").Append("\"")
                      .Append(" key2=\"").Append(key2 ?? "").Append("\"")
                      .Append(" text2=\"").Append(txt2 ?? "").Append("\"");
                    log(sb.ToString());
                }
                log("EVENT phone-listrows-dump: script=" + (script ?? "?")
                    + " base=" + rowBase + " stride=" + rowStride
                    + " resolved=" + resolved + " scanned=" + MaxRow
                    + " errors=" + errors);
            }
            catch { errors++; }
        }

        private static int ReadId(int row)
        {
            try
            {
                if (!RangeAllocated(row, idField + 1)) return int.MinValue;
                return Row(row).GetStructField(idField).Read<int>();
            }
            catch { return int.MinValue; }
        }
    }
}
