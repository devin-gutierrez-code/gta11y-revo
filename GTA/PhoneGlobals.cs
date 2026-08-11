using System;
using System.Collections.Generic;
using System.Text;
using GTA;
using GTA.Native;

namespace GrandTheftAccessibility
{
    // Phone dynamic-text reader via script globals (channel B). Contact names,
    // message senders and email subjects are NOT exposed by any native or
    // scaleform getter; they live in script global arrays whose offsets are
    // build-specific. Devin's Legacy build reports Game.Version == Unknown, so we
    // CANNOT key by version — the structures are located by runtime signature and
    // every read is validated.
    //
    // v1.4 rewrite. The v1.3 detector keyed off a count sentinel (a global equal
    // to 222, the char-sheet slot count) and it FAILED on this build: the
    // 2026-07-20 log has `phone-globals: ok=False csBase=-1 rowMapBase=-1` and,
    // decisively, an EMPTY `count==222 bases:` sweep — no global in 2000..4000
    // holds 222 at all, so the premise was wrong, not just the window. Three
    // changes follow from that:
    //   1. The signature is now the DATA (a run of distinct printable names at a
    //      fixed stride), not a count. The count is only a confidence bonus.
    //   2. The stride is searched too ({29,30,31,32,33}) instead of assumed 29.
    //   3. The scan is frame-budgeted and resumable, because the search space is
    //      far too large to sweep in one tick without stalling the game.
    // Offsets can also simply be PINNED as data (menulabels-overrides.json ->
    // phoneGlobals), so a session that identifies them needs a JSON redeploy, not
    // a rebuild.
    //
    // Fail-closed contract, unchanged: any read that throws or fails validation
    // disables the channel and the reader falls back to "row N". This class NEVER
    // lets an exception escape into OnTick, and never speaks a non-printable
    // string.
    static class PhoneGlobals
    {
        private const int NameField = 3;          // .f_3 display name TEXT_LABEL
        private const int ExpectedCount = 222;    // char-sheet slot count (1.59-1.73); confidence hint only
        private static readonly int[] StrideCandidates = { 29, 30, 31, 32, 33 };

        // Known bases (iFruitJailbreak ini): 1.72 / 1.73. Tried first, then the
        // pinned overrides, then the signature scan.
        //
        // v1.5: the row-map order is REVERSED. appcontacts.c line 9298 in the
        // decompiled corpus reads `Global_8817 = Global_21655[iLocal_95];` — the
        // selection index goes through 21655, not 21616. v1.4 tried 21616 first
        // and LooksLikeRowMap was too weak to reject it, so the reader locked onto
        // the wrong array: the 08-01 dump shows rows 0 and 2 both mapping to slot
        // 177 and rows 3 and 4 both to 179, and the 08-03 walk duly spoke the same
        // contact twice in a row on a monotone descent.
        private static readonly int[] CharSheetCandidates = { 2339, 2349 };
        private static readonly int[] RowMapCandidates = { 21655, 21616 };
        // Scan windows. GTA allocates script globals in blocks of 0x40000 entries,
        // so everything below ~262000 lives in block 0 and is always mapped —
        // which is why these sweeps neither throw (an exception per probe would
        // cost more than the scan itself) nor risk reading unmapped memory. The
        // RangeAllocated guard below is defence in depth for the pathological case.
        private const int CsScanLo = 1000, CsScanHi = 12000;
        private const int RmScanLo = 18000, RmScanHi = 26000;
        // Per-frame work budget. The scan runs from OnTick, so it must never cost
        // a visible hitch; it resumes where it left off on the next frame. Each
        // probe is up to 8 struct-field string reads, so keep this modest — the
        // full sweep still finishes in a few seconds of phone time.
        private const int ProbesPerTick = 200;
        private const int SigSlots = 8;   // slots sampled per signature probe

        private static bool resolved = false;  // detection finished (success or exhausted)
        private static bool ok = false;        // structures located + validated
        private static int charSheetBase = -1;
        private static int rowMapBase = -1;
        private static int stride = 29;
        private static int errors = 0;         // read fuse

        // Resumable scan cursor.
        private static bool scanning = false;
        private static int scanBase = CsScanLo;
        private static int scanStrideIdx = 0;
        private static int scanPhase = 0;      // 0 = char sheet, 1 = row map
        private static Action<string> scanLog = null;
        private static int candidatesLogged = 0;

        // Row-map trust guard (v1.5). A correct map never returns the same contact
        // for two ADJACENT rows; a mis-located one does it constantly. One hit is
        // logged and tolerated (two contacts really can share a name), the second
        // demotes the whole channel to "row N" — an honest index beats a confident
        // wrong name.
        private static int lastRow = int.MinValue;
        private static string lastName = null;
        private static int adjacentDupes = 0;
        private static bool rowMapDistrusted = false;

        public static bool Ok { get { return ok && !rowMapDistrusted; } }

        /// <summary>Locate + validate the char sheet and row map. Idempotent;
        /// starts a resumable scan when nothing is pinned. Safe reads only.</summary>
        public static void Detect()
        {
            if (resolved || scanning) return;
            try
            {
                // 1. Pinned by data? Trust but still validate.
                int pinnedCs, pinnedRm, pinnedStride;
                if (MenuLabelDb.TryPhoneGlobal("charSheetBase", out pinnedCs)
                    && MenuLabelDb.TryPhoneGlobal("rowMapBase", out pinnedRm))
                {
                    stride = MenuLabelDb.TryPhoneGlobal("stride", out pinnedStride) ? pinnedStride : 29;
                    if (LooksLikeCharSheet(pinnedCs, stride))
                    {
                        charSheetBase = pinnedCs;
                        if (LooksLikeRowMap(pinnedRm))
                        {
                            rowMapBase = pinnedRm;
                            ok = true;
                            resolved = true;
                            return;
                        }
                    }
                }

                // 2. Known bases from other tools, across every stride candidate.
                foreach (int s in StrideCandidates)
                {
                    foreach (int b in CharSheetCandidates)
                    {
                        if (!LooksLikeCharSheet(b, s)) continue;
                        charSheetBase = b;
                        stride = s;
                        foreach (int r in RowMapCandidates)
                        {
                            if (!LooksLikeRowMap(r)) continue;
                            rowMapBase = r;
                            ok = true;
                            resolved = true;
                            return;
                        }
                    }
                }

                // 3. Nothing known — start the frame-budgeted signature scan.
                scanning = true;
                scanBase = CsScanLo;
                scanStrideIdx = 0;
                scanPhase = 0;
                charSheetBase = -1;
                rowMapBase = -1;
            }
            catch
            {
                ok = false;
                resolved = true;
            }
        }

        /// <summary>Advance the resumable detection scan by one frame's budget.
        /// No-op unless a scan is in flight. Never throws.</summary>
        public static void DetectTick(Action<string> log)
        {
            if (!scanning) return;
            scanLog = log;
            try
            {
                int budget = ProbesPerTick;
                while (budget-- > 0)
                {
                    if (scanPhase == 0)
                    {
                        if (scanBase > CsScanHi)
                        {
                            scanStrideIdx++;
                            scanBase = CsScanLo;
                            if (scanStrideIdx >= StrideCandidates.Length)
                            {
                                // Exhausted: give up cleanly, stay fail-closed.
                                scanning = false;
                                resolved = true;
                                ok = false;
                                if (log != null)
                                    log("EVENT phone-globals: scan exhausted, no char sheet found"
                                        + " (candidates logged=" + candidatesLogged + ")");
                                return;
                            }
                        }
                        int s = StrideCandidates[scanStrideIdx];
                        if (LooksLikeCharSheet(scanBase, s))
                        {
                            charSheetBase = scanBase;
                            stride = s;
                            if (log != null && candidatesLogged < 12)
                            {
                                candidatesLogged++;
                                log("EVENT phone-globals-candidate: base=" + scanBase + " stride=" + s
                                    + " names=" + SampleNames(scanBase, s, 4));
                            }
                            scanPhase = 1;
                            scanBase = RmScanLo;
                            continue;
                        }
                        scanBase++;
                    }
                    else
                    {
                        if (scanBase > RmScanHi)
                        {
                            // This char-sheet candidate has no row map — reject it
                            // and resume the char-sheet sweep after it.
                            scanPhase = 0;
                            scanBase = charSheetBase + 1;
                            charSheetBase = -1;
                            continue;
                        }
                        if (LooksLikeRowMap(scanBase))
                        {
                            rowMapBase = scanBase;
                            ok = true;
                            scanning = false;
                            resolved = true;
                            if (log != null)
                                log("EVENT phone-globals: LOCATED csBase=" + charSheetBase
                                    + " rowMapBase=" + rowMapBase + " stride=" + stride
                                    + " -- pin these in menulabels-overrides.json phoneGlobals");
                            return;
                        }
                        scanBase++;
                    }
                }
            }
            catch
            {
                scanning = false;
                resolved = true;
                ok = false;
            }
        }

        /// <summary>Spoken name for a contacts-list row, or false to fall back to
        /// "row N". The char sheet stores a TEXT_LABEL — a GXT KEY, not display
        /// text (appcontacts.c reads it as `Global_2349[i].f_3` and hands it to the
        /// scaleform, which localizes on the way in). v1.4 returned the key and the
        /// reader said "CELL_FRANKLIN_N" out loud 59 times across three sessions.
        /// </summary>
        public static bool TryContactName(int row, out string name, Action<string> log)
        {
            name = null;
            if (!Ok || errors >= 10 || row < 0) return false;
            try
            {
                if (!RangeAllocated(rowMapBase, 1, row + 1)) return false;
                int slot = GlobalVariable.Get(rowMapBase).GetArrayItem(row, 1).Read<int>();
                if (slot < 0 || slot >= ExpectedCount) return false;
                string raw = SlotName(charSheetBase, stride, slot);
                if (!IsPrintableName(raw)) return false;

                string spoken;
                if (LooksLikeLabelKey(raw))
                {
                    // A key that does not resolve is never spoken: it is an id, and
                    // an id tells a blind user strictly less than "row 3" does.
                    spoken = MenuLabelDb.ResolveGxt(raw);
                    if (spoken == null)
                    {
                        if (log != null) log("EVENT phone-name-unresolved: row=" + row
                            + " slot=" + slot + " key=" + raw);
                        return false;
                    }
                }
                else
                {
                    // Some build could store the literal. Accept it only when it
                    // actually reads as words rather than as an identifier.
                    spoken = raw;
                }

                if (!TrustRow(row, spoken, slot, log)) return false;
                name = spoken;
                return true;
            }
            catch { errors++; return false; }
        }

        /// <summary>Adjacent-duplicate detector for the row map. Returns false once
        /// the channel has been demoted.</summary>
        private static bool TrustRow(int row, string spoken, int slot, Action<string> log)
        {
            if (lastName != null && Math.Abs(row - lastRow) == 1
                && string.Equals(spoken, lastName, StringComparison.Ordinal))
            {
                adjacentDupes++;
                if (log != null) log("EVENT phone-rowmap-suspect: row=" + row
                    + " prevRow=" + lastRow + " slot=" + slot
                    + " name=\"" + spoken + "\" n=" + adjacentDupes
                    + " rowMapBase=" + rowMapBase);
                if (adjacentDupes >= 2)
                {
                    rowMapDistrusted = true;
                    if (log != null) log("EVENT phone-rowmap-distrusted: rowMapBase=" + rowMapBase
                        + " -- falling back to row N");
                    return false;
                }
            }
            lastRow = row;
            lastName = spoken;
            return true;
        }

        /// <summary>GXT-key shape: upper-case letters, digits and underscores only,
        /// and at least one underscore. Distinguishes "CELL_FRANKLIN_N" (an id to
        /// localize) from "Franklin" (already display text).</summary>
        private static bool LooksLikeLabelKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
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

        /// <summary>Signature: a run of DISTINCT printable names at this stride.
        /// v1.3 required the count slot to equal 222 first, which this build does
        /// not satisfy anywhere. Distinctness is what rejects the constant/zeroed
        /// regions that a printability test alone would accept.</summary>
        private static bool LooksLikeCharSheet(int b, int s)
        {
            try
            {
                if (!RangeAllocated(b, s, SigSlots)) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int good = 0;
                for (int slot = 0; slot < SigSlots; slot++)
                {
                    string n = SlotName(b, s, slot);
                    if (!IsPrintableName(n)) continue;
                    if (!seen.Add(n)) continue;   // repeats are not a name table
                    good++;
                }
                return good >= 6;
            }
            catch { return false; }
        }

        /// <summary>The first eight map entries must be in-range slot indices whose
        /// names are printable in the already-located char sheet, AND mostly
        /// DISTINCT.
        ///
        /// v1.4 checked four entries for range and printability only, which any
        /// array of small ints passes — that is how it accepted 21616, whose first
        /// entries are 177,176,177,179,179,178: a contacts list cannot show the
        /// same person twice in a row. Distinctness is the same signature that
        /// already makes LooksLikeCharSheet reliable.</summary>
        private static bool LooksLikeRowMap(int b)
        {
            try
            {
                if (charSheetBase < 0) return false;
                if (!RangeAllocated(b, 1, SigSlots)) return false;
                var seen = new HashSet<int>();
                for (int r = 0; r < SigSlots; r++)
                {
                    int slot = GlobalVariable.Get(b).GetArrayItem(r, 1).Read<int>();
                    if (slot < 0 || slot >= ExpectedCount) return false;
                    if (!IsPrintableName(SlotName(charSheetBase, stride, slot))) return false;
                    seen.Add(slot);
                }
                return seen.Count >= 6;
            }
            catch { return false; }
        }

        private static string SlotName(int csBase, int csStride, int slot)
        {
            return GlobalVariable.Get(csBase).GetArrayItem(slot, csStride)
                   .GetStructField(NameField).Read<string>();
        }

        /// <summary>Guard for the blind scan: confirm the FAR END of the struct
        /// walk is still inside allocated script globals before reading it.
        ///
        /// GlobalVariable.Get throws for an index with no allocated block, but
        /// GetArrayItem/GetStructField are raw pointer arithmetic with no bounds
        /// check of their own — so probing base b at stride s would happily walk
        /// ~s*slots*8 bytes past the end of the last block and read unmapped
        /// memory. An access violation there is not a catchable managed exception
        /// and would take the game down with it, which for this mod's users is a
        /// far worse outcome than never resolving a contact name. Resolving the
        /// highest index we are about to touch through Get() makes the whole walk
        /// provably in-bounds.</summary>
        private static bool RangeAllocated(int b, int s, int slots)
        {
            try
            {
                int highest = b + (s * (slots - 1)) + NameField + 1;
                return GlobalVariable.Get(highest).MemoryAddress != IntPtr.Zero;
            }
            catch { return false; }
        }

        private static bool IsPrintableName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 24 || s.Length < 2) return false;
            foreach (char c in s)
                if (c < 0x20 || c > 0x7E) return false;
            if (s.IndexOf('~') >= 0) return false; // GXT formatting token, not a literal name
            return true;
        }

        private static string SampleNames(int b, int s, int n)
        {
            var sb = new StringBuilder();
            for (int slot = 0; slot < n; slot++)
            {
                if (sb.Length > 0) sb.Append('|');
                try { sb.Append(Sanitize(SlotName(b, s, slot))); } catch { sb.Append("<err>"); }
            }
            return "\"" + sb + "\"";
        }

        // ---- Texts / Email store discovery: RETIRED in v1.5 -------------------
        // v1.4 armed a bounded 1000..40000 signature sweep whenever Texts or Email
        // opened, on the theory that each app owned a private name store. It ran in
        // four sessions and returned the SAME five candidates every time, of which
        // 2349 is the char sheet we already had and the rest never resolved into
        // anything speakable. The premise was wrong: apptextmessage.c and
        // appemail.c read senders straight out of `Global_2349[i].f_3` (the char
        // sheet) and push their list rows through the shared row-label array that
        // PhoneRowText now reads. So the sweep was paying ~39k probes per app open
        // to rediscover a base that is pinned as data.

        /// <summary>Per-frame pump while the phone is open. Only advances the
        /// resumable detection scan; never speaks, never throws.</summary>
        public static void Tick(Action<string> log)
        {
            DetectTick(log);
        }

        // Log-only diagnostic dump. One call per contacts-app open — never per
        // frame. This is the artefact that pins the offsets offline when
        // auto-detection fails.
        public static void Dump(Action<string> log)
        {
            try
            {
                string ver = "?";
                try { ver = Function.Call<string>(Hash.GET_ONLINE_VERSION); } catch { }
                log("EVENT phone-globals: onlineVer=" + ver + " ok=" + ok
                    + " csBase=" + charSheetBase + " rowMapBase=" + rowMapBase
                    + " stride=" + stride + " scanning=" + scanning
                    + " distrusted=" + (rowMapDistrusted ? 1 : 0));
                if (ok)
                {
                    // v1.5: dump the LOCALIZED name beside the raw key. The raw
                    // column is what pins offsets offline; the localized column is
                    // what the user actually hears, so a key that fails to resolve
                    // is visible on the same line instead of being inferred.
                    for (int slot = 0; slot < 24; slot++)
                    {
                        string nm = "<err>", loc = "<err>";
                        try
                        {
                            nm = Sanitize(SlotName(charSheetBase, stride, slot));
                            loc = MenuLabelDb.ResolveGxt(nm) ?? "<unresolved>";
                        }
                        catch { }
                        log("  slot " + slot + " f3=\"" + nm + "\" text=\"" + loc + "\"");
                    }
                    for (int r = 0; r < 20; r++)
                    {
                        try
                        {
                            int slot = GlobalVariable.Get(rowMapBase).GetArrayItem(r, 1).Read<int>();
                            log("  rowmap " + r + " -> " + slot);
                        }
                        catch { break; }
                    }
                }
                else
                {
                    log("  signature scan in flight; phone-globals-candidate lines follow if any hit");
                }
            }
            catch (Exception ex) { log("EVENT phone-globals: dump ERR " + ex.GetType().Name); }
        }

        private static string Sanitize(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c >= 0x20 && c <= 0x7E ? c : '.');
            return sb.ToString();
        }
    }
}
