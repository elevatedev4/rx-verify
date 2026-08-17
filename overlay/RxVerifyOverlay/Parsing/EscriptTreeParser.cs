using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Parsing;

/// <summary>
/// Pure function: EscriptNode tree (see EscriptNode.cs) -> PrescriptionRecord.
/// No FlaUI/UIA dependency, so this is unit-testable with synthetic
/// in-memory trees. Mirrors the real NCPDP SCRIPT NewRx shape confirmed
/// against a live PioneerRx Escript-tab UIA dump (see Uia/FieldMap.cs
/// header for the source note) — Message > Body > NewRx >
/// {Patient, Prescriber, MedicationPrescribed, ...}.
///
/// Every lookup is defensive: a missing container or leaf simply yields a
/// null field, never an exception — the engine treats "not provided" as
/// "not comparable" (yellow), never a mismatch (red). See Uia/FieldReader.cs.
/// </summary>
public static class EscriptTreeParser
{
    /// <param name="message">
    /// The top-level "Message" node (the Escript Tree's single top-level
    /// TreeItem in the real dump). Passing a message with no NewRx
    /// container AND no other child holding a MedicationPrescribed
    /// subtree (see FindPrescriptionContainer) simply yields an empty
    /// PrescriptionRecord.
    /// </param>
    public static PrescriptionRecord Parse(EscriptNode message)
    {
        var body = Child(message, FieldMap.NodeBody);
        var newRx = body is null ? null : FindPrescriptionContainer(body);
        if (newRx is null)
        {
            return new PrescriptionRecord();
        }

        var (refills, refillsFromTotalFills, _) = ParseRefills(newRx);

        return new PrescriptionRecord
        {
            PatientName = ParsePatientName(newRx),
            PatientDOB = ParsePatientDob(newRx),
            PatientAddress = ParsePatientAddress(newRx),
            Prescriber = ParsePrescriber(newRx),
            DateWritten = ParseWrittenDate(newRx),
            Drug = ParseDrug(newRx),
            Sig = ParseSig(newRx),
            Quantity = ParseQuantityValue(newRx),
            QuantityUnit = ParseQuantityUnit(newRx),
            // DaysSupply removed entirely per Will's live-test feedback —
            // no longer read, compared, or displayed.
            Refills = refills,
            RefillsFromTotalFills = refillsFromTotalFills ? true : (bool?)null,
            SubstitutionsNotAllowed = ParseSubstitutionsNotAllowed(newRx)
        };
    }

    /// <summary>
    /// Locates the Body child that holds the prescription data. The
    /// confirmed real-dump shape (see class doc) is a child literally
    /// named "NewRx" — that exact-name match is tried FIRST and, when
    /// found, wins outright regardless of what it contains, so every
    /// existing NewRx tree (including ones missing MedicationPrescribed
    /// entirely, e.g. a message with only Patient data) keeps parsing
    /// byte-for-byte identically to before this method existed.
    ///
    /// A RESPONDED renewal/refill-request message uses some other NCPDP
    /// SCRIPT container name entirely (the exact name is unconfirmed
    /// against a real dump — RxRenewalResponse is a plausible guess, not
    /// a verified one). Rather than hardcoding a guessed list of every
    /// possible NCPDP message-type container name, fall back to
    /// STRUCTURAL detection: any Body child that directly holds a
    /// MedicationPrescribed child — the same anchor node every other
    /// Rx-level parse (ParseDrug/ParseSig/ParseQuantityValue/
    /// ParseQuantityUnit/ParseWrittenDate/ParseRefills) already requires
    /// to read anything at all — is accepted as the prescription
    /// container. Returns null (empty record) when neither check finds
    /// anything.
    ///
    /// DETERMINISM (round 2 reviewer should-fix): a real NCPDP renewal
    /// response can carry more than one Body child that itself holds a
    /// MedicationPrescribed subtree (e.g. a MedicationPrescribed section
    /// alongside a separate MedicationDispensed section) — FirstOrDefault
    /// alone would pick whichever happens to come first from
    /// body.Children with no visibility into that being an ambiguous
    /// choice. The first match (in body.Children order) still wins here
    /// — this parser needs to return exactly one container either way —
    /// but a multi-match is flagged via Debug.WriteLine so a wrong pick
    /// is diagnosable. There is no structured logging anywhere in this
    /// codebase to route this through instead (grepped Uia/ViewModels/
    /// Integrated/Parsing for ILogger/Trace/Debug — none found);
    /// Debug.WriteLine is the smallest addition that surfaces this in a
    /// debugger/Output window without introducing a new dependency for a
    /// case that, per the NCPDP shapes actually confirmed against a real
    /// dump so far (see FieldMap.cs header), has never been observed.
    /// </summary>
    private static EscriptNode? FindPrescriptionContainer(EscriptNode body)
    {
        var namedNewRx = Child(body, FieldMap.NodeNewRx);
        if (namedNewRx is not null) return namedNewRx;

        var candidates = body.Children.Where(c => Child(c, FieldMap.NodeMedicationPrescribed) is not null).ToList();
        if (candidates.Count > 1)
        {
            Debug.WriteLine(
                $"EscriptTreeParser.FindPrescriptionContainer: {candidates.Count} Body children each hold a " +
                $"MedicationPrescribed subtree; using the first one found ('{candidates[0].Name}').");
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Best-effort search for e-script free-text notes (NCPDP SCRIPT
    /// "Note" element), at both the message level (sibling of Body,
    /// pharmacy-directed) and the NewRx/MedicationPrescribed level
    /// (medication-directed) — per Will's item 6 feedback ("message-level
    /// or medication-level"). UNCONFIRMED against a real dump: no
    /// captured e-script (escript-249.txt) has a Note present at all, so
    /// this searches every plausible location defensively rather than
    /// assuming one, and returns an empty list (never throws) if none is
    /// found — which is also the expected/common case. FLAG: a fresh
    /// "Dump UIA Tree" capture on an e-script that actually carries a
    /// note is needed to confirm the exact node shape before trusting
    /// this in production (see FieldMap.NodeNote/KeyNoteText doc).
    /// Called separately from Parse() (not part of PrescriptionRecord)
    /// since notes are a source-only, display-only concern, not part of
    /// the engine's field-by-field comparison contract — see
    /// Uia/FieldReader.cs SourceNotes.
    ///
    /// BLOCKER FIX (round 2 reviewer): this used to look up the
    /// prescription container via a direct Child(body, "NewRx") call,
    /// independent of Parse()'s own FindPrescriptionContainer fallback —
    /// so once Parse() started structurally detecting non-NewRx-named
    /// containers (a renewal response, say), this method still silently
    /// found nothing and dropped every NewRx-level/MedicationPrescribed-
    /// level note for exactly the messages that fallback exists for. Now
    /// routed through the SAME FindPrescriptionContainer used by Parse(),
    /// so the two can never disagree on which container is "the"
    /// prescription data for a given message.
    /// </summary>
    public static IReadOnlyList<string> ParseNotes(EscriptNode message)
    {
        var notes = new List<string>();

        // Message-level: a Note that's a direct sibling of Body.
        CollectNotesFrom(message, notes);

        var body = Child(message, FieldMap.NodeBody);
        var newRx = body is null ? null : FindPrescriptionContainer(body);
        if (newRx is null) return notes;

        // NewRx-level (medication-directed notes not nested under
        // MedicationPrescribed in some NCPDP variants).
        CollectNotesFrom(newRx, notes);

        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        if (med is not null)
        {
            CollectNotesFrom(med, notes);
        }

        return notes;
    }

    /// <summary>
    /// Looks for a direct child of <paramref name="container"/> named
    /// "Note" (FieldMap.NodeNote) and appends its text to
    /// <paramref name="notes"/> if found and non-blank. Handles both
    /// shapes defensively: a container with a nested "NoteText: ..." leaf
    /// (or any leaf child at all, joined), or a bare "Note: &lt;text&gt;"
    /// leaf directly (SplitKeyValue would then parse it as Key="Note",
    /// Value=&lt;text&gt; — Child() only matches by exact whole-Name
    /// equality though, so a bare leaf is found via the same
    /// prefix-scan Leaf() uses instead).
    /// </summary>
    private static void CollectNotesFrom(EscriptNode container, List<string> notes)
    {
        // Case 1: "Note" as a CONTAINER (no ": " in its own Name).
        var noteContainer = Child(container, FieldMap.NodeNote);
        if (noteContainer is not null)
        {
            var text = Leaf(noteContainer, FieldMap.KeyNoteText);
            if (text is null && noteContainer.Children.Count > 0)
            {
                // Unknown leaf key — fall back to joining every leaf
                // value found directly under it rather than dropping the
                // note entirely.
                var parts = new List<string>();
                foreach (var child in noteContainer.Children)
                {
                    var (_, v) = SplitKeyValue(child.Name);
                    var trimmed = NullIfEmpty(v);
                    if (trimmed is not null) parts.Add(trimmed);
                }
                text = parts.Count > 0 ? string.Join(' ', parts) : null;
            }
            if (text is not null) notes.Add(text);
        }

        // Case 2: "Note: <text>" as a bare LEAF directly on container.
        var bareLeaf = Leaf(container, FieldMap.NodeNote);
        if (bareLeaf is not null) notes.Add(bareLeaf);
    }

    /// <summary>
    /// Parses MedicationPrescribed &gt; Substitutions (FieldMap.KeySubstitutions),
    /// e.g. "0 (No Product Selection Indicated)" or "1 (Substitution Not
    /// Allowed by Prescriber)" — confirmed leaf shape against
    /// escript-249.txt (code 0 case only; code 1's exact description text
    /// is inferred from the NCPDP SCRIPT code table, not confirmed
    /// against a real code-1 dump). Only code "1" means substitutions are
    /// NOT allowed (DAW required); every other numeric code (0 and the
    /// patient-requested-brand codes 2-9) means substitution IS permitted.
    /// Returns null (not comparable) if the leaf is missing or its
    /// leading code isn't parseable as an integer.
    /// </summary>
    private static bool? ParseSubstitutionsNotAllowed(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var raw = med is null ? null : Leaf(med, FieldMap.KeySubstitutions);
        if (raw is null) return null;

        var codeText = raw.Split(' ', 2)[0].Trim();
        return int.TryParse(codeText, out var code) ? code == 1 : null;
    }

    // ------------------------------------------------------------------
    // NewRx > Patient
    // ------------------------------------------------------------------

    private static string? ParsePatientName(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var name = patient is null ? null : Child(patient, FieldMap.NodeName);
        if (name is null) return null;

        return JoinNameLastFirstMiddle(
            Leaf(name, FieldMap.KeyFirstName),
            Leaf(name, FieldMap.KeyMiddleName),
            Leaf(name, FieldMap.KeyLastName));
    }

    private static string? ParsePatientDob(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var dob = patient is null ? null : Child(patient, FieldMap.NodeDateOfBirth);
        // DateOfBirth is a CONTAINER with a single nested "Date: <value>"
        // leaf one level down — NOT a direct leaf on Patient itself.
        var raw = dob is null ? null : Leaf(dob, FieldMap.KeyDate);
        // Reformat from the e-script's ISO "yyyy-MM-dd" to the same
        // M/d/yyyy style PioneerRx's uxPatientDOB control displays (see
        // Uia/FieldMap.cs EnteredPatientDobId), so the source/entered
        // columns line up visually instead of looking like a mismatch at
        // a glance. The engine compares dates format-agnostically either
        // way (see rx-verify src/normalize/date.ts) — this is display
        // formatting only.
        return FormatIsoDateForDisplay(raw);
    }

    private static Address? ParsePatientAddress(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var address = patient is null ? null : Child(patient, FieldMap.NodeAddress);
        return ParseAddressContainer(address);
    }

    // ------------------------------------------------------------------
    // NewRx > Prescriber
    // ------------------------------------------------------------------

    private static Prescriber? ParsePrescriber(EscriptNode newRx)
    {
        var prescriber = Child(newRx, FieldMap.NodePrescriber);
        if (prescriber is null) return null;

        // NPI is nested under Prescriber > Identification > NPI, NOT a
        // direct leaf on Prescriber.
        var identification = Child(prescriber, FieldMap.NodeIdentification);
        var npi = identification is null ? null : Leaf(identification, FieldMap.KeyNpi);

        var name = Child(prescriber, FieldMap.NodeName);
        var prescriberName = name is null
            ? null
            : JoinNameLastFirstMiddle(Leaf(name, FieldMap.KeyFirstName), null, Leaf(name, FieldMap.KeyLastName));

        // Phone (CommunicationNumbers > PrimaryTelephone > Number) and
        // Address (same AddressLine1/City/StateProvince/PostalCode shape
        // as Patient > Address) added per Will's live-test feedback so
        // the engine can compare all four prescriber fields separately —
        // see FieldMap.cs NodeCommunicationNumbers/NodePrimaryTelephone
        // and Models/EngineModels.cs Prescriber.
        var phone = ParsePrimaryTelephone(prescriber);
        var address = ParseAddressContainer(Child(prescriber, FieldMap.NodeAddress));

        if (npi is null && prescriberName is null && phone is null && address is null) return null;
        return new Prescriber { Name = prescriberName, Npi = npi, Phone = phone, Address = address };
    }

    /// <summary>Shared by ParsePrescriber (and any future caller) for a Prescriber/Patient/Pharmacy-shaped &lt;container&gt; &gt; Address leaf group.</summary>
    private static Address? ParseAddressContainer(EscriptNode? address)
    {
        if (address is null) return null;
        return new Address
        {
            Street = Leaf(address, FieldMap.KeyAddressLine1),
            City = Leaf(address, FieldMap.KeyCity),
            State = Leaf(address, FieldMap.KeyStateProvince),
            Zip = Leaf(address, FieldMap.KeyPostalCode)
        };
    }

    /// <summary>CommunicationNumbers &gt; PrimaryTelephone &gt; "Number: &lt;digits&gt;", confirmed against escript-249.txt's Prescriber section.</summary>
    private static string? ParsePrimaryTelephone(EscriptNode container)
    {
        var comm = Child(container, FieldMap.NodeCommunicationNumbers);
        var primary = comm is null ? null : Child(comm, FieldMap.NodePrimaryTelephone);
        return primary is null ? null : Leaf(primary, FieldMap.KeyNumber);
    }

    // ------------------------------------------------------------------
    // NewRx > MedicationPrescribed
    // ------------------------------------------------------------------

    private static DrugDescriptor? ParseDrug(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        if (med is null) return null;

        // DrugDescription is a direct leaf on MedicationPrescribed.
        var name = Leaf(med, FieldMap.KeyDrugDescription);

        // NDC is nested MedicationPrescribed > DrugCoded > ProductCode > Code.
        var drugCoded = Child(med, FieldMap.NodeDrugCoded);
        var productCode = drugCoded is null ? null : Child(drugCoded, FieldMap.NodeProductCode);
        var ndc = productCode is null ? null : Leaf(productCode, FieldMap.KeyCode);

        // Note: DrugCoded > DrugDBCode > Code is the RxCUI. There is no
        // field for it on DrugDescriptor (engine's local NDC dataset only
        // needs the NDC to resolve the drug) so it is deliberately not
        // read here — see FieldReader.cs class doc for the full drug
        // comparison discussion.
        if (name is null && ndc is null) return null;
        return new DrugDescriptor { Name = name, Ndc = ndc };
    }

    private static string? ParseSig(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var sig = med is null ? null : Child(med, FieldMap.NodeSig);
        return sig is null ? null : Leaf(sig, FieldMap.KeySigText);
    }

    private static string? ParseQuantityValue(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var quantity = med is null ? null : Child(med, FieldMap.NodeQuantity);
        return quantity is null ? null : Leaf(quantity, FieldMap.KeyValue);
    }

    private static string? ParseQuantityUnit(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var quantity = med is null ? null : Child(med, FieldMap.NodeQuantity);
        var raw = quantity is null ? null : Leaf(quantity, FieldMap.KeyQuantityUnitOfMeasure);
        // Real value looks like "C38046 (Unspecified)" — surface just the
        // human-readable parenthetical ("Unspecified") as the unit.
        return ExtractParenthetical(raw);
    }

    private static string? ParseWrittenDate(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var writtenDate = med is null ? null : Child(med, FieldMap.NodeWrittenDate);
        var raw = writtenDate is null ? null : Leaf(writtenDate, FieldMap.KeyDate);
        // Same display-alignment reformat as ParsePatientDob — the
        // entered uxWrittenDate control shows M/d/yyyy.
        return FormatIsoDateForDisplay(raw);
    }

    /// <summary>
    /// Returns the raw refill-count text, whether it came from a
    /// Total-fills-style key rather than an ordinary Refills key, and
    /// (diagnostic-only, report-payload plumbing — see FieldReader.ReadSource
    /// and Reporting/RxReportBuilder.cs) which TotalFillsKeyPrefixes entry
    /// was seen ANYWHERE relevant, even on the branch where the ordinary
    /// Refills key ends up winning. Both keys have the same "colon inside
    /// a parenthetical" hazard, e.g. "Refills (NewRx: One dispense, plus
    /// (Quantity) refills): 1" — find each directly (by prefix) rather
    /// than via Leaf()'s normal exact-key lookup, since Leaf()/
    /// SplitKeyValue always does the FIRST-": " split — that would land
    /// right after "NewRx" here and misparse this one specifically. Split
    /// on the LAST ": " instead, which lands right after the closing
    /// paren (or immediately, for a plain "Total fills: 4" with no
    /// parenthetical) and before the integer count.
    ///
    /// "Refills (" wins when BOTH keys are somehow present on the same
    /// MedicationPrescribed node — mirrors the OCR path's documented
    /// both-labels precedence (src/ocr/parseEscriptOcr.ts). The raw value
    /// is returned UNTRANSFORMED either way — the engine
    /// (rx-verify src/quantity/index.ts compareRefills) is the single
    /// source of truth for subtracting 1 off a Total-fills count; this
    /// parser never does that math itself.
    ///
    /// ROUND 3 FIX (Will's 2026-08-17 live false-yellow — a "Refill
    /// ApprovedWithChanges" renewal response read refills as "not
    /// provided" even though PioneerRx showed "Total Fills: 3 (including
    /// this fill)"): TWO compounding gaps, both defensible without a live
    /// dump to confirm the exact real nesting shape (this whole Total-fills
    /// path is still documented UNCONFIRMED — see FieldMap.
    /// TotalFillsKeyPrefixes doc):
    ///
    /// (1) VALUE EXTRACTION: the ONLY real-dump-confirmed shape this split
    /// logic was ever tested against embeds its parenthetical INSIDE the
    /// key, before the final ": " (e.g. "Total fills (renewal: total incl.
    /// initial fill): 4" — see Parse_TotalFillsParentheticalStyle...Test).
    /// Will's literal reported string has the parenthetical AFTER the
    /// value instead ("...: 3 (including this fill)") — there is only ONE
    /// ": " in that string, so SplitOnLastColonSpace returned "3
    /// (including this fill)" as the "value", which the engine's
    /// Number()-based parse can't read as a count at all.
    /// TrimTrailingParenthetical strips that suffix so this shape parses
    /// down to a clean "3", without touching the confirmed-correct
    /// leading-parenthetical shape (nothing trailing to strip there).
    ///
    /// (2) LOCATION: previously only med.Children (MedicationPrescribed's
    /// DIRECT children) was searched. FindTotalFillsLeaf now searches
    /// MedicationPrescribed's FULL subtree (a real vendor could nest this
    /// summary line one level deeper than the confirmed "Refills (" shape
    /// does), and if still not found there, newRx's own direct children
    /// are checked too (in case the line is a SIBLING of MedicationPrescribed
    /// under the response container rather than nested inside it — see
    /// Uia/UiaTreeWalker.cs BuildPrunedMessageNode's matching widening,
    /// needed so a sibling leaf isn't pruned out of the live tree before
    /// this parser ever runs). Purely additive: the confirmed NewRx-message
    /// "Refills (" behavior and every existing Total-fills-inside-
    /// MedicationPrescribed test are unaffected.
    ///
    /// Neither widening is confirmed against a real renewal-response UIA
    /// dump — flagged here same as FieldMap.cs/UiaTreeWalker.cs already
    /// flag the rest of this path; a live spot-check remains the way to
    /// close this out for certain. See item 2 of this same fix round:
    /// RxReportBuilder.Build now also plumbs TotalFillsLabelSeen/Prefix
    /// onto the NEXT error report so a real occurrence self-diagnoses
    /// instead of needing another guess.
    /// </summary>
    private static (string? Value, bool FromTotalFills, string? TotalFillsPrefixSeen) ParseRefills(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        if (med is null) return (null, false, null);

        var refillsLeaf = med.Children.FirstOrDefault(c => c.Name.StartsWith(FieldMap.RefillsKeyPrefix, StringComparison.Ordinal));

        var totalFillsLeaf = FindTotalFillsLeaf(med) ?? newRx.Children.FirstOrDefault(IsTotalFillsLeaf);
        var totalFillsPrefixSeen = totalFillsLeaf is null ? null : MatchedTotalFillsPrefix(totalFillsLeaf.Name);

        if (refillsLeaf is not null)
        {
            return (NullIfEmpty(SplitOnLastColonSpace(refillsLeaf.Name)), false, totalFillsPrefixSeen);
        }

        if (totalFillsLeaf is not null)
        {
            var rawValue = SplitOnLastColonSpace(totalFillsLeaf.Name);
            return (NullIfEmpty(TrimTrailingParenthetical(rawValue)), true, totalFillsPrefixSeen);
        }

        return (null, false, null);
    }

    /// <summary>True when <paramref name="node"/>'s Name starts with one of FieldMap.TotalFillsKeyPrefixes (case-insensitive — see that field's doc).</summary>
    private static bool IsTotalFillsLeaf(EscriptNode node) =>
        FieldMap.TotalFillsKeyPrefixes.Any(prefix => node.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Which TotalFillsKeyPrefixes entry matched — for diagnostics (RxReportBuilder) as well as ParseRefills itself. Returns null if none match (should only be called on a node IsTotalFillsLeaf already confirmed true for).</summary>
    private static string? MatchedTotalFillsPrefix(string leafName) =>
        FieldMap.TotalFillsKeyPrefixes.FirstOrDefault(prefix => leafName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Depth-first search of <paramref name="node"/>'s ENTIRE subtree (not
    /// just direct children) for the first Total-fills-shaped leaf — see
    /// ParseRefills's ROUND 3 FIX doc, gap (2). Small tree (MedicationPrescribed
    /// typically has well under 20 descendants total), so a full walk here
    /// costs nothing measurable.
    /// </summary>
    private static EscriptNode? FindTotalFillsLeaf(EscriptNode node)
    {
        foreach (var child in node.Children)
        {
            if (IsTotalFillsLeaf(child)) return child;
            var nested = FindTotalFillsLeaf(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Diagnostic-only (Reporting/RxReportBuilder.cs, item 2 of the
    /// 2026-08-17 fix round): independent of whether ParseRefills actually
    /// USED a Total-fills value as Refills (it may have lost to an
    /// ordinary "Refills (" key, or not have been found by either search),
    /// this answers "was ANY Total-fills-shaped label seen on this
    /// message's prescription container at all, and which prefix matched"
    /// — so the report payload can tell "the label genuinely wasn't there"
    /// apart from "it was there but something else in the pipeline dropped
    /// it," without ever including the label's VALUE (only the constant
    /// prefix string, e.g. "Total fills: " — never PHI, never the refill
    /// count itself).
    /// </summary>
    public static (bool Seen, string? Prefix) DetectTotalFillsLabel(EscriptNode message)
    {
        var body = Child(message, FieldMap.NodeBody);
        var newRx = body is null ? null : FindPrescriptionContainer(body);
        if (newRx is null) return (false, null);

        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var leaf = (med is null ? null : FindTotalFillsLeaf(med)) ?? newRx.Children.FirstOrDefault(IsTotalFillsLeaf);
        return leaf is null ? (false, null) : (true, MatchedTotalFillsPrefix(leaf.Name));
    }

    /// <summary>
    /// Returns everything AFTER the LAST occurrence of ": " in the text.
    /// Used only for the Refills/Total-fills leaf (see ParseRefills, and
    /// FieldMap.RefillsKeyPrefix/TotalFillsKeyPrefixes) — every other leaf
    /// in the tree uses the general first-": "-split via
    /// SplitKeyValue/Leaf().
    /// </summary>
    private static string SplitOnLastColonSpace(string text)
    {
        var splitIndex = text.LastIndexOf(": ", StringComparison.Ordinal);
        return splitIndex < 0 ? "" : text[(splitIndex + 2)..];
    }

    /// <summary>
    /// Strips a trailing " (...)" annotation off a Total-fills VALUE, e.g.
    /// "3 (including this fill)" -&gt; "3" — see ParseRefills's ROUND 3 FIX
    /// doc, gap (1). Only strips a SUFFIX that starts with " (": a bare
    /// "4" (no parenthetical at all, or the already-clean value left by
    /// the confirmed leading-parenthetical-in-the-KEY shape, e.g. "Total
    /// fills (renewal: ...): 4") passes through unchanged, since neither
    /// has anything to strip. Never applied to the ordinary RefillsKeyPrefix
    /// branch — that shape is real-dump-confirmed to never need this.
    /// </summary>
    private static string TrimTrailingParenthetical(string value)
    {
        var parenIndex = value.IndexOf(" (", StringComparison.Ordinal);
        return parenIndex < 0 ? value : value[..parenIndex].TrimEnd();
    }

    // ------------------------------------------------------------------
    // Tree-walk primitives
    // ------------------------------------------------------------------

    /// <summary>Finds a direct child CONTAINER by exact name (e.g. "Patient"). Container nodes' whole Name IS the container name (no ": " in it).</summary>
    private static EscriptNode? Child(EscriptNode node, string name) =>
        node.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a direct child LEAF ("Key: Value") by key, split on the FIRST ": " (values may themselves contain colons, e.g. a sig time "10:00" — splitting on the first occurrence only is always correct as long as the value's colon isn't immediately followed by a space, which holds for every leaf here except Refills, handled separately in ParseRefills).</summary>
    private static string? Leaf(EscriptNode node, string key)
    {
        foreach (var child in node.Children)
        {
            var (k, v) = SplitKeyValue(child.Name);
            if (string.Equals(k, key, StringComparison.Ordinal)) return NullIfEmpty(v);
        }
        return null;
    }

    /// <summary>
    /// Splits a leaf's raw Name into (Key, Value) on the FIRST occurrence
    /// of ": ". This is the general rule for every leaf in the tree
    /// (container names never contain ": ", and no observed value in the
    /// real dump has a colon-followed-by-space inside it except the
    /// Refills key text itself, which callers must route through
    /// ParseRefills's prefix-based lookup instead of Leaf()).
    /// </summary>
    private static (string Key, string Value) SplitKeyValue(string text)
    {
        var splitIndex = text.IndexOf(": ", StringComparison.Ordinal);
        if (splitIndex < 0) return (text, "");
        return (text[..splitIndex], text[(splitIndex + 2)..]);
    }

    /// <summary>
    /// Builds "Last, First Middle" (comma format) instead of "First
    /// Middle Last" — matches the style PioneerRx's own quick-search
    /// fields display (uxPatientQuickSearch/uxPrescriberQuickSearch,
    /// e.g. "Rivera, Jordan Alex"), so the source/entered columns look
    /// visually aligned side by side instead of looking like a mismatch
    /// at a glance. Purely a display-format choice — the engine's name
    /// comparator normalizes both orderings to the same canonical token
    /// set either way (see rx-verify src/normalize/name.ts
    /// wholeNameTokens), so this has no effect on the actual verdict.
    /// </summary>
    private static string? JoinNameLastFirstMiddle(string? first, string? middle, string? last)
    {
        var firstMiddle = string.Join(" ", new[] { first, middle }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var lastTrimmed = string.IsNullOrWhiteSpace(last) ? null : last!.Trim();

        if (lastTrimmed is null) return NullIfEmpty(firstMiddle);
        if (string.IsNullOrWhiteSpace(firstMiddle)) return lastTrimmed;
        return $"{lastTrimmed}, {firstMiddle}";
    }

    /// <summary>
    /// "yyyy-MM-dd" (the e-script's ISO date format) -&gt; "M/d/yyyy" (the
    /// format PioneerRx's own DOB/Written-date controls display). Falls
    /// back to the raw string unreformatted if it isn't a recognized ISO
    /// date, rather than guessing — display-only, never affects the
    /// engine's format-agnostic date comparison.
    /// </summary>
    private static string? FormatIsoDateForDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        return DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? $"{parsed.Month}/{parsed.Day}/{parsed.Year}"
            : raw;
    }

    private static string? ExtractParenthetical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var open = raw.IndexOf('(');
        var close = raw.IndexOf(')', open + 1);
        if (open >= 0 && close > open) return raw[(open + 1)..close].Trim();
        return raw.Trim();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
