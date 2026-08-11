using System;
using System.Collections.Generic;
using System.Text;
using GTA;
using GTA.Native;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// In-app phone row labels, read from the phone's shared row-label array.
    ///
    /// The problem this replaces: v1.4 shipped `phoneAppRows` EMPTY by design and
    /// every row inside every phone app spoke "row 0", "row 1", "row 5". Phone
    /// Settings, Checklist, Texts and Email were all just numbers. The plan had
    /// been to hand-build a static table per app, which is both a lot of data and
    /// wrong for anything whose contents change.
    ///
    /// The decompiled scripts show there is no need for per-app tables at all.
    /// Every phone app pushes its visible rows through the same call —
    ///
    ///   appsettings.c:610
    ///     SET_DATA_SLOT(view, slot, type, -1, -1,
    ///                   &Global_10324[Global_21649 /*2811*/][page /*281*/].f_7[i /*4*/])
    ///
    /// — so `Global_10324[char][page].f_7[]` is a TEXT_LABEL array holding the
    /// labels the scaleform is displaying RIGHT NOW, for whichever app owns the
    /// screen. cellphone_controller.c pins `Global_21649` as the character index
    /// (0 Michael, 1 Franklin, 2 Trevor, 3 multiplayer) and `.f_259` as the index
    /// of the header row within the same array.
    ///
    /// One provider therefore covers every app, including the dynamic ones the
    /// static-table plan could never have handled.
    ///
    /// What is NOT confirmed on this build is the base index and the page index,
    /// which is why this whole class is gated behind phoneRowTextV2 (default OFF)
    /// and why it PROBES and logs before it speaks. Same fail-closed discipline as
    /// PhoneGlobals: every walk is bounds-checked through RangeAllocated, nothing
    /// unresolvable is ever spoken, and no exception may reach OnTick.
    /// </summary>
    static class PhoneRowText
    {
        // Decompiled-corpus layout. Overridable as data via the phoneGlobals block
        // in menulabels-overrides.json, so a build whose base moved is a JSON edit
        // and a redeploy rather than a rebuild.
        private static int rowBase = 10324;      // Global_10324
        private static int charStride = 2811;    // outer array element size
        private static int pageStride = 281;     // inner array element size
        private static int labelField = 7;       // .f_7
        private static int labelStride = 4;      // TEXT_LABEL_15 = 4 script words

        private const int MaxPage = 8;    // phones carry a handful of views
        private const int MaxRow = 16;    // deepest visible list we have seen
        private const int MinHits = 2;    // a page needs this many resolvable rows

        private static bool configured = false;
        private static int page = -1;     // page chosen by the last probe
        private static int errors = 0;    // read fuse, mirrors PhoneGlobals

        public static bool Ok { get { return page >= 0 && errors < 10; } }

        /// <summary>Re-read the pinnable offsets. Cheap; called on each probe so a
        /// JSON redeploy takes effect without a script reload.</summary>
        private static void Configure()
        {
            if (configured) return;
            configured = true;
            int v;
            if (MenuLabelDb.TryPhoneGlobal("rowLabelBase", out v)) rowBase = v;
            if (MenuLabelDb.TryPhoneGlobal("rowLabelCharStride", out v)) charStride = v;
            if (MenuLabelDb.TryPhoneGlobal("rowLabelPageStride", out v)) pageStride = v;
            if (MenuLabelDb.TryPhoneGlobal("rowLabelField", out v)) labelField = v;
            if (MenuLabelDb.TryPhoneGlobal("rowLabelItemStride", out v)) labelStride = v;
        }

        /// <summary>Pick the page whose label array actually holds this app's rows,
        /// and log every candidate so the choice is auditable offline. Called once
        /// per app change — the active page moves with the app, so a page pinned at
        /// phone-open would be stale the moment the user launched anything.
        ///
        /// A pinned `rowLabelPage` short-circuits the search.</summary>
        public static void Probe(int charIdx, string script, Action<string> log)
        {
            Configure();
            page = -1;
            if (charIdx < 0) return;
            try
            {
                int pinned;
                if (MenuLabelDb.TryPhoneGlobal("rowLabelPage", out pinned))
                {
                    page = pinned;
                    if (log != null) log("EVENT phone-page-probe: script=" + (script ?? "?")
                        + " page=" + pinned + " src=pinned");
                    return;
                }

                // ---- iter-51: the probe is now APP-AWARE ----
                //
                // It used to score every page by "how many rows resolve to a GXT
                // label" and take the winner. That is not a test of which page
                // belongs to the app that just opened, and on 08-11 it chose the
                // SAME page 4 with the SAME perfect 16/16 for all four apps — so
                // Contacts and Messages both read out the phone's ringtone and
                // wallpaper list ("Default, Badger, Whiz, Tinkle, Purple Glow...").
                //
                // Two tells separate a global table from an app's own row array:
                //
                //  1. A full house. A real app list is shorter than MaxRow and has
                //     empty tail rows; a page where EVERY row resolves is a static
                //     table. This works on the first app of a session, when there is
                //     nothing yet to compare against.
                //  2. An identical signature under two different apps. Row arrays
                //     are rewritten when an app opens; a page whose contents are
                //     byte-identical across two apps cannot be either app's list.
                //     This is the conclusive one, and it needs a second app opened
                //     before it can fire.
                int bestPage = -1, bestHits = 0;
                int fallbackPage = -1, fallbackHits = 0;
                for (int p = 0; p < MaxPage; p++)
                {
                    int hits = CountResolvable(charIdx, p);
                    bool global = NoteSignature(charIdx, p, script);
                    bool full = hits >= MaxRow;
                    if (log != null && hits > 0)
                        log("EVENT phone-page-probe: script=" + (script ?? "?")
                            + " char=" + charIdx + " page=" + p + " hits=" + hits
                            + " global=" + (global ? 1 : 0) + " full=" + (full ? 1 : 0)
                            + " sample=" + Sample(charIdx, p, 4));
                    // Every page stays eligible as a last resort, so a build whose
                    // heuristics misfire degrades to the old behaviour rather than
                    // to silence.
                    if (hits > fallbackHits) { fallbackHits = hits; fallbackPage = p; }
                    if (global || full) continue;
                    if (hits > bestHits) { bestHits = hits; bestPage = p; }
                }

                if (bestHits >= MinHits)
                {
                    page = bestPage;
                    if (log != null) log("EVENT phone-page-probe: script=" + (script ?? "?")
                        + " chose page=" + bestPage + " hits=" + bestHits + " src=app-specific");
                }
                else
                {
                    // Nothing app-specific. Refusing here is deliberate and is the
                    // actual fix for the reported bug: PhoneAppRowText falls through
                    // to PhoneGlobals.TryContactName (which was already working and
                    // was only ever being shadowed), and to "row N" for the apps
                    // that have no other source — both of which are honest, where
                    // reading somebody's ringtone list as their contacts is not.
                    page = -1;
                    if (log != null)
                        log("EVENT phone-page-probe: script=" + (script ?? "?")
                            + " no app-specific page (bestGlobalPage=" + fallbackPage
                            + " hits=" + fallbackHits + " base=" + rowBase
                            + " char=" + charIdx + ")");
                }
            }
            catch { errors++; page = -1; }
        }

        /// <summary>Spoken label for an in-app row, or false to fall back to
        /// "row N". Never speaks an unresolved key.</summary>
        public static bool TryRowLabel(int charIdx, int row, out string label)
        {
            label = null;
            if (!Ok || charIdx < 0 || row < 0 || row >= MaxRow) return false;
            try
            {
                string key = RawLabel(charIdx, page, row);
                if (key == null) return false;
                string txt = MenuLabelDb.ResolveGxt(key);
                if (txt == null) return false;
                label = txt;
                return true;
            }
            catch { errors++; return false; }
        }

        /// <summary>Header row of the current page (.f_259 indexes into .f_7), which
        /// is what the phone shows as the screen title.</summary>
        public static bool TryHeader(int charIdx, out string header)
        {
            header = null;
            if (!Ok || charIdx < 0) return false;
            try
            {
                if (!RangeAllocated(charIdx, page, 260)) return false;
                int idx = Page(charIdx, page).GetStructField(259).Read<int>();
                if (idx < 0 || idx >= MaxRow) return false;
                string key = RawLabel(charIdx, page, idx);
                if (key == null) return false;
                header = MenuLabelDb.ResolveGxt(key);
                return header != null;
            }
            catch { errors++; return false; }
        }

        public static void Reset()
        {
            page = -1;
            // iter-51: the signature cache describes THIS phone session's globals.
            // The page verdicts are cheap to rebuild (one probe per app open) and
            // keeping a stale "page 4 is global" across a script reload would be a
            // permanent, invisible refusal.
            pageSig.Clear();
            pageSigOwner.Clear();
            pageGlobal.Clear();
        }

        // ---- home grid --------------------------------------------------------
        // cellphone_flashhand.c builds the home screen out of three arrays:
        //
        //   func_42:  Global_9509[app /*15*/]      = the app descriptor —
        //             offset 0 is its GXT label ("CELL_16"), .f_4 the grid slot,
        //             .f_5 the script name ("appSettings"), .f_9 its joaat.
        //   func_41:  Global_10087[slot] = 1       when a real app occupies a slot
        //             Global_10050[slot] = app     index of the app that occupies it
        //
        // and the ALPHA it passes to SET_DATA_SLOT is the availability tell: 255 for
        // a live app (func_41) against 225 for the placeholder fill (func_37), which
        // is precisely the transparency CELL_36 describes to sighted players and
        // which a blind one could previously only discover by pressing Accept and
        // hearing nothing happen.
        //
        // Reading this beats the shipped phoneHome table on its own terms: the grid
        // is DYNAMIC by mission context, which is what every phone-slot-corrected
        // event in the logs has been reporting.

        private static int appArrayBase = 9509;   // Global_9509
        private static int appArrayStride = 15;
        private static int slotAppBase = 10050;   // Global_10050[slot] -> app index
        private static int slotUsedBase = 10087;  // Global_10087[slot] -> occupied
        private const int HomeSlots = 9;

        /// <summary>Live home-grid tile: its label and whether it can be launched.
        /// False when the arrays do not validate, in which case the caller keeps
        /// the shipped static table and claims nothing about availability.</summary>
        public static bool TryHomeTile(int slot, out string label, out bool available)
        {
            label = null;
            available = true;
            if (slot < 0 || slot >= HomeSlots || errors >= 10) return false;
            Configure();
            try
            {
                if (!FlatAllocated(slotUsedBase, HomeSlots)) return false;
                if (!FlatAllocated(slotAppBase, HomeSlots)) return false;
                int used = GlobalVariable.Get(slotUsedBase).GetArrayItem(slot, 1).Read<int>();
                int app = GlobalVariable.Get(slotAppBase).GetArrayItem(slot, 1).Read<int>();
                // used is a strict 0/1 flag and app indexes a small table; anything
                // else means these are not the arrays we think they are.
                if (used < 0 || used > 1 || app < 0 || app > 64) return false;
                available = used == 1;
                if (!available) return true;   // an empty slot has no label to give

                if (!AppAllocated(app)) return false;
                string key = GlobalVariable.Get(appArrayBase)
                                           .GetArrayItem(app, appArrayStride)
                                           .Read<string>();
                if (!LooksLikeLabelKey(key)) return false;
                label = MenuLabelDb.ResolveGxt(key);
                return label != null;
            }
            catch { errors++; return false; }
        }

        private static bool FlatAllocated(int b, int n)
        {
            try { return GlobalVariable.Get(b + n + 1).MemoryAddress != IntPtr.Zero; }
            catch { return false; }
        }

        private static bool AppAllocated(int app)
        {
            try
            {
                int highest = appArrayBase + (appArrayStride * (app + 1)) + appArrayStride + 1;
                return GlobalVariable.Get(highest).MemoryAddress != IntPtr.Zero;
            }
            catch { return false; }
        }

        // ---- internals --------------------------------------------------------

        private static GlobalVariable Page(int charIdx, int p)
        {
            return GlobalVariable.Get(rowBase)
                                 .GetArrayItem(charIdx, charStride)
                                 .GetArrayItem(p, pageStride);
        }

        private static string RawLabel(int charIdx, int p, int row)
        {
            if (!RangeAllocated(charIdx, p, labelField + (labelStride * (row + 1)) + 1)) return null;
            string s = Page(charIdx, p).GetStructField(labelField)
                                       .GetArrayItem(row, labelStride)
                                       .Read<string>();
            return LooksLikeLabelKey(s) ? s : null;
        }

        // ---- iter-51: cross-app page signatures --------------------------------
        // What a page's raw keys were the last time it was probed, and which app was
        // open then. A page whose keys are unchanged across two DIFFERENT apps is a
        // global table (ringtones, wallpapers) rather than that app's row array.
        // Cleared by Reset() when the phone closes, so a stale signature from an
        // earlier session cannot outlive the globals it describes.
        private static readonly Dictionary<int, string> pageSig = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> pageSigOwner = new Dictionary<int, string>();
        private static readonly HashSet<int> pageGlobal = new HashSet<int>();

        /// <summary>Record this page's contents for the current app and report
        /// whether it is a known global table. A page whose signature CHANGES for a
        /// new app is app-sensitive and loses any earlier global verdict.</summary>
        private static bool NoteSignature(int charIdx, int p, string script)
        {
            string sig;
            try { sig = Sample(charIdx, p, MaxRow); }
            catch { return false; }
            string app = script ?? "?";

            string prevSig, prevOwner;
            bool hadSig = pageSig.TryGetValue(p, out prevSig);
            pageSigOwner.TryGetValue(p, out prevOwner);
            pageSig[p] = sig;
            pageSigOwner[p] = app;

            if (!hadSig) return pageGlobal.Contains(p);
            if (sig != prevSig)
            {
                // Contents moved with the app — this is a row array, not a table.
                pageGlobal.Remove(p);
                return false;
            }
            // Identical contents. Only damning if a DIFFERENT app was open last time;
            // re-opening the same app should of course look the same.
            if (prevOwner != null && prevOwner != app) pageGlobal.Add(p);
            return pageGlobal.Contains(p);
        }

        private static int CountResolvable(int charIdx, int p)
        {
            int hits = 0;
            for (int row = 0; row < MaxRow; row++)
            {
                string key = RawLabel(charIdx, p, row);
                if (key == null) continue;
                if (MenuLabelDb.ResolveGxt(key) != null) hits++;
            }
            return hits;
        }

        /// <summary>Bounds guard for the blind walk, same contract as
        /// PhoneGlobals.RangeAllocated: GetArrayItem/GetStructField are raw pointer
        /// arithmetic with no bounds check of their own, and an access violation
        /// past the end of the global block is not a catchable managed exception —
        /// it takes the game down, which for this mod's users is far worse than a
        /// phone row that says "row 3". Resolving the highest index we are about to
        /// touch through Get() proves the whole walk is in-bounds.</summary>
        private static bool RangeAllocated(int charIdx, int p, int fieldDepth)
        {
            try
            {
                int highest = rowBase + (charStride * (charIdx + 1))
                            + (pageStride * (p + 1)) + fieldDepth + 1;
                return GlobalVariable.Get(highest).MemoryAddress != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// <summary>Same GXT-key shape test PhoneGlobals uses: upper case, digits
        /// and underscores, at least one underscore. Rejects the zeroed and
        /// numeric-garbage regions a blind page scan walks through.</summary>
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

        private static string Sample(int charIdx, int p, int n)
        {
            var sb = new StringBuilder();
            for (int row = 0; row < n; row++)
            {
                if (sb.Length > 0) sb.Append('|');
                string key = null;
                try { key = RawLabel(charIdx, p, row); } catch { }
                sb.Append(key ?? ".");
            }
            return "\"" + sb + "\"";
        }
    }
}
