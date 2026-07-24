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
        private static readonly int[] CharSheetCandidates = { 2339, 2349 };
        private static readonly int[] RowMapCandidates = { 21616, 21655 };
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

        // Store-sweep (Texts / Email) state — log-only, never spoken.
        private static string sweepScript = null;
        private static int sweepBase = 0;
        private static int sweepHits = 0;
        private const int SweepLo = 1000, SweepHi = 40000;

        public static bool Ok { get { return ok; } }

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

        public static bool TryContactName(int row, out string name)
        {
            name = null;
            if (!ok || errors >= 10 || row < 0) return false;
            try
            {
                if (!RangeAllocated(rowMapBase, 1, row + 1)) return false;
                int slot = GlobalVariable.Get(rowMapBase).GetArrayItem(row, 1).Read<int>();
                if (slot < 0 || slot >= ExpectedCount) return false;
                string s = SlotName(charSheetBase, stride, slot);
                if (!IsPrintableName(s)) return false;
                name = s;
                return true;
            }
            catch { errors++; return false; }
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

        // First four map entries must be in-range slot indices whose names are
        // printable in the already-located char sheet.
        private static bool LooksLikeRowMap(int b)
        {
            try
            {
                if (charSheetBase < 0) return false;
                if (!RangeAllocated(b, 1, 4)) return false;
                for (int r = 0; r < 4; r++)
                {
                    int slot = GlobalVariable.Get(b).GetArrayItem(r, 1).Read<int>();
                    if (slot < 0 || slot >= ExpectedCount) return false;
                    if (!IsPrintableName(SlotName(charSheetBase, stride, slot))) return false;
                }
                return true;
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

        // ---- Texts / Email store discovery (log-only) -------------------------
        // We have no runtime evidence for the apptextmessage / appemail stores, so
        // this iteration collects it instead of guessing. Nothing found here is
        // ever spoken; the next session's log is the input that pins the offsets,
        // which then land in phoneGlobals as data.

        /// <summary>Arm a bounded store sweep for a dynamic-text app.</summary>
        public static void ArmStoreSweep(string script)
        {
            if (sweepScript != null) return;   // one app at a time
            sweepScript = script;
            sweepBase = SweepLo;
            sweepHits = 0;
        }

        /// <summary>Advance the store sweep by one frame's budget. No-op unless
        /// armed. Never throws, never speaks.</summary>
        public static void StoreSweepTick(Action<string> log)
        {
            DetectTick(log);
            if (sweepScript == null) return;
            try
            {
                int budget = ProbesPerTick;
                while (budget-- > 0)
                {
                    if (sweepBase > SweepHi || sweepHits >= 24)
                    {
                        if (log != null)
                            log("EVENT phone-store-sweep: script=" + sweepScript
                                + " done hits=" + sweepHits + " lastBase=" + sweepBase);
                        sweepScript = null;
                        return;
                    }
                    foreach (int s in StrideCandidates)
                    {
                        if (!LooksLikeCharSheet(sweepBase, s)) continue;
                        sweepHits++;
                        if (log != null)
                            log("EVENT phone-store-candidate: script=" + sweepScript
                                + " base=" + sweepBase + " stride=" + s
                                + " sample=" + SampleNames(sweepBase, s, 4));
                        break;
                    }
                    sweepBase++;
                }
            }
            catch
            {
                sweepScript = null;
            }
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
                    + " stride=" + stride + " scanning=" + scanning);
                if (ok)
                {
                    for (int slot = 0; slot < 24; slot++)
                    {
                        string nm = "<err>";
                        try { nm = Sanitize(SlotName(charSheetBase, stride, slot)); } catch { }
                        log("  slot " + slot + " f3=\"" + nm + "\"");
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
                    // v1.3 reported only "count==222 bases", which came back empty
                    // and told us nothing actionable. Report the count sweep (still
                    // useful if the sentinel exists elsewhere) AND note that the
                    // resumable data-signature scan is what actually decides.
                    var hits = new StringBuilder();
                    for (int b = 1000; b <= 12000; b++)
                    {
                        try { if (GlobalVariable.Get(b).Read<int>() == ExpectedCount) hits.Append(b).Append(' '); }
                        catch { }
                    }
                    log("  count==222 bases: " + hits.ToString().Trim());
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
