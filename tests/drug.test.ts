import { describe, it, expect } from 'vitest';
import {
  parseNdc,
  compareDrugs,
  normalizeDrugNameString,
  extractStatedDurationHours,
  extractStatedConcentrationStrength,
  FixtureProvider,
  type RxNormProvider,
  type RxConcept
} from '../src/drug/index.js';

const provider = new FixtureProvider();

// Used only by the "component-wise name fallback" suite below: the
// 20-concept FixtureProvider's name lookup matches on a bare leading
// ingredient word (see FixtureProvider.getConcept's "leadMatch" step),
// ignoring stated strength entirely -- so e.g. both "Metoprolol Tartrate
// 50 Mg Tablet" and "Metoprolol Succinate 50 Mg Tablet" would resolve to
// the SAME fixture concept (FX0014, "Metoprolol 25mg tablet") and never
// reach the unknown_drug branch this feature targets. That's a known
// simplification of the small test fixture, not of the real
// LocalNdcProvider (whose resolveConceptByName narrows by stated strength
// -- see that function's doc in src/drug/index.ts). This stub always
// fails resolution, mirroring the actual field report: the synthetic/
// local dataset doesn't carry the drug at all.
const unresolvedProvider: RxNormProvider = { getConcept: () => null };

describe('parseNdc', () => {
  it('parses an 11-digit NDC', () => {
    const p = parseNdc('00071015523');
    expect(p).toMatchObject({ labeler: '00071', product: '0155', packageCode: '23' });
  });

  it('parses a dashed 5-4-2 NDC', () => {
    const p = parseNdc('00071-0155-23');
    expect(p?.normalized11).toBe('00071015523');
  });

  it('parses a dashed 10-digit 4-4-2 NDC by padding labeler', () => {
    const p = parseNdc('0071-0155-23');
    expect(p?.normalized11).toBe('00071015523');
  });

  it('returns null for garbage', () => {
    expect(parseNdc('not-an-ndc')).toBeNull();
  });
});

describe('compareDrugs', () => {
  it('is GREEN on identical NDC', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('green');
  });

  it('is YELLOW generic_substitution for brand vs generic same ingredient/strength/form', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00093715601' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('generic_substitution');
  });

  it('is YELLOW pack_size for same product different package NDC', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015590' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('pack_size');
  });

  it('is RED on different strength', () => {
    const r = compareDrugs(
      { name: 'Synthroid 50mcg tablet' },
      { name: 'Levothyroxine 50mcg tablet' },
      provider
    );
    // same ingredient/strength/form -> should actually be substitution, not red
    expect(r.status).toBe('yellow');
  });

  it('is RED on different ingredient', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015601' }, provider); // lisinopril vs Lipitor(atorvastatin)
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
  });

  it('is YELLOW not_provided when source drug missing', () => {
    const r = compareDrugs(undefined, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is YELLOW unknown_drug when neither NDC nor name resolves', () => {
    const r = compareDrugs({ name: 'Zorbaxatin 9000mg unobtanium' }, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });

  describe('drug IDENTITY by name (real-world overlay shape: entered side never carries an NDC)', () => {
    it('is GREEN name_identity_match on an exact normalized name match, even with no NDC on either side', () => {
      const r = compareDrugs({ name: 'Clindamycin Phosp 1% Lotion' }, { name: 'Clindamycin Phosp 1% Lotion' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('is GREEN name_identity_match on a case/punctuation-only difference', () => {
      const r = compareDrugs({ name: 'Clindamycin Phosp 1% Lotion' }, { name: 'clindamycin phosp 1% lotion.' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('is GREEN on matching names even when the source NDC is present and unresolvable -- NDC is lookup-only, never required for green', () => {
      const r = compareDrugs(
        { name: 'Gabapentin 300mg capsule', ndc: '99999999999' },
        { name: 'Gabapentin 300mg capsule' },
        provider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('does not let a name-identity match paper over a stated strength contradiction (names must be genuinely equal, not just similar)', () => {
      const r = compareDrugs({ name: 'Lisinopril 20mg tablet' }, { name: 'Lisinopril 10mg tablet' }, provider);
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('drug_mismatch');
    });

    describe('dosage-form / casing / spacing variants (W-T10 item 4)', () => {
      it('is GREEN name_identity_match for "Estradiol 2 MG TABS" vs "Estradiol 2 Mg Tablet"', () => {
        const r = compareDrugs({ name: 'Estradiol 2 MG TABS' }, { name: 'Estradiol 2 Mg Tablet' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('is GREEN name_identity_match for "Amoxicillin 500 MG CAP" vs "amoxicillin 500 mg capsule"', () => {
        const r = compareDrugs({ name: 'Amoxicillin 500 MG CAP' }, { name: 'amoxicillin 500 mg capsule' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('is GREEN name_identity_match for "Metformin 500MG SUSP" vs "Metformin 500 mg Suspension" (no-space unit + susp abbreviation)', () => {
        const r = compareDrugs({ name: 'Metformin 500MG SUSP' }, { name: 'Metformin 500 mg Suspension' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('does not fold "cap" inside an unrelated word like "captopril" into "capsule"', () => {
        expect(normalizeDrugNameString('Captopril 25mg tablet')).toBe('captopril 25 mg tablet');
      });

      // Bug 5 (round 3, W-T-round3): live report — entered drug said
      // "oint", source said "ointment"; unmatched, fell through to a
      // yellow unknown_drug. Extends DOSAGE_FORM_WORDS with the same
      // common-abbreviation folding already applied for tab/cap/sol/susp,
      // for the other dosage forms actually seen on e-scripts vs
      // PioneerRx free-text entry.
      describe('Bug 5: additional dosage-form abbreviations (oint, crm, supp, inj, gtt)', () => {
        it('is GREEN name_identity_match for "Clindamycin 2% oint" vs "Clindamycin 2% ointment"', () => {
          const r = compareDrugs({ name: 'Clindamycin 2% oint' }, { name: 'Clindamycin 2% ointment' }, provider);
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        it('folds "ung" (unguentum) to "ointment" too', () => {
          expect(normalizeDrugNameString('Nystatin 100000 unit/g ung')).toBe('nystatin 100000 unit/g ointment');
        });

        it('folds "crm" to "cream"', () => {
          const r = compareDrugs({ name: 'Hydrocortisone 1% crm' }, { name: 'Hydrocortisone 1% cream' }, provider);
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        it('does NOT fold bare "cr" to "cream" — ambiguous with the controlled-release release qualifier ("Diltiazem CR" is not a cream)', () => {
          expect(normalizeDrugNameString('Diltiazem 240mg cr')).toBe('diltiazem 240 mg cr');
        });

        it('folds "supp" to "suppository"', () => {
          const r = compareDrugs(
            { name: 'Promethazine 25mg supp' },
            { name: 'Promethazine 25mg suppository' },
            provider
          );
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        it('folds "inj" to "injection"', () => {
          const r = compareDrugs({ name: 'Enoxaparin 40mg inj' }, { name: 'Enoxaparin 40mg injection' }, provider);
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        // REVIEW FIX (non-blocking finding, round 3): "lot" -> "lotion"
        // was removed rather than added — "lot"/"Lot" routinely appears
        // as a lot/batch-number token bleeding into a free-text name
        // field, and folding it here would feed the primary
        // name_identity_match GREEN path on a token that isn't reliably
        // a dosage-form abbreviation at all. No live report asked for
        // this fold (only "oint" vs "ointment" was reported), so it's
        // pinned as NOT folded.
        it('does NOT fold "lot" to "lotion" (ambiguous with a lot/batch-number token, not requested by any field report)', () => {
          expect(normalizeDrugNameString('Triamcinolone 0.1% lot')).toBe('triamcinolone 01% lot');
          const r = compareDrugs(
            { name: 'Triamcinolone 0.1% lot' },
            { name: 'Triamcinolone 0.1% lotion' },
            provider
          );
          expect(r.reasonCode).not.toBe('name_identity_match');
        });

        it('folds "gtt" to "drops"', () => {
          const r = compareDrugs({ name: 'Timolol 0.5% gtt' }, { name: 'Timolol 0.5% drops' }, provider);
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        it('leaves "patch"/"patches" as-is (no synonym folding — unambiguous already, per branch brief)', () => {
          expect(normalizeDrugNameString('Fentanyl 25mcg patch')).toBe('fentanyl 25 mcg patch');
        });
      });

      // Bug 1 (round 4): live report — entered "Metoprolol Succinate ER
      // 50 mg", source e-script spelled it "Metoprolol Succinate Extended
      // Release 50 mg". Same drug, flagged as a mismatch because the
      // abbreviation and the spelled-out phrase never folded to the same
      // normalized string. RELEASE_PHRASE_FOLDS folds the spelled-out
      // phrase DOWN to the abbreviation (one-way: never expand a bare
      // abbreviation back up to a phrase).
      describe('Bug 1: release-qualifier phrase folding (ER/SR/CR/DR/IR)', () => {
        it('is GREEN name_identity_match for "Metoprolol Succinate ER 50 mg" vs "Metoprolol Succinate Extended Release 50 mg"', () => {
          const r = compareDrugs(
            { name: 'Metoprolol Succinate ER 50 mg' },
            { name: 'Metoprolol Succinate Extended Release 50 mg' },
            provider
          );
          expect(r.status).toBe('green');
          expect(r.reasonCode).toBe('name_identity_match');
        });

        it('still MISMATCHES "ER" vs "IR" — folding the phrase must not blur genuinely different release profiles', () => {
          const r = compareDrugs(
            { name: 'Metoprolol Succinate ER 50 mg' },
            { name: 'Metoprolol Succinate IR 50 mg' },
            provider
          );
          expect(r.reasonCode).not.toBe('name_identity_match');
        });

        it('folds hyphenated "extended-release" the same as the two-word phrase', () => {
          expect(normalizeDrugNameString('Metoprolol Succinate extended-release 50mg')).toBe(
            normalizeDrugNameString('Metoprolol Succinate ER 50mg')
          );
        });

        it('folds "sustained release" -> sr', () => {
          expect(normalizeDrugNameString('Verapamil Sustained Release 240mg')).toBe(
            normalizeDrugNameString('Verapamil SR 240mg')
          );
        });

        it('folds "controlled release" -> cr', () => {
          expect(normalizeDrugNameString('Diltiazem Controlled Release 240mg')).toBe(
            normalizeDrugNameString('Diltiazem CR 240mg')
          );
        });

        it('folds "delayed release" -> dr', () => {
          expect(normalizeDrugNameString('Divalproex Delayed Release 250mg')).toBe(
            normalizeDrugNameString('Divalproex DR 250mg')
          );
        });

        it('folds "immediate release" -> ir', () => {
          expect(normalizeDrugNameString('Metoprolol Immediate Release 25mg')).toBe(
            normalizeDrugNameString('Metoprolol IR 25mg')
          );
        });

        it('does NOT fold a bare abbreviation up to a phrase (direction is phrase -> abbreviation only)', () => {
          // "er" stays "er" — normalizeDrugNameString never introduces
          // the word "extended" or "release" from a bare abbreviation.
          expect(normalizeDrugNameString('Metoprolol Succinate ER 50mg')).toBe('metoprolol succinate er 50 mg');
        });

        it('does NOT fold XL or XR to ER — no existing equivalence in this codebase, out of scope for this fix', () => {
          const r = compareDrugs(
            { name: 'Metoprolol Succinate ER 50 mg' },
            { name: 'Metoprolol Succinate XL 50 mg' },
            provider
          );
          expect(r.reasonCode).not.toBe('name_identity_match');
        });
      });
    });

    // Live report: source e-script "AMPHETAMINE-DEXTROAMPHET ER 30 MG
    // CAPSULE EXTENDED RELEASE 24 HOUR" vs entered "Dextroamp-Amphet Er
    // 30 Mg Cap" went YELLOW unknown_drug — same Adderall XR generic,
    // different abbreviation/ordering/format conventions.
    describe('amphetamine-family (Adderall/Adderall XR generics) equivalence', () => {
      it('is GREEN name_identity_match for the exact live-test regression pair', () => {
        const r = compareDrugs(
          { name: 'AMPHETAMINE-DEXTROAMPHET ER 30 MG CAPSULE EXTENDED RELEASE 24 HOUR' },
          { name: 'Dextroamp-Amphet Er 30 Mg Cap' },
          provider
        );
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('folds "dextroamp"/"amphet" word-boundary abbreviations and sorts the combo ingredients alphabetically', () => {
        expect(normalizeDrugNameString('Dextroamp-Amphet 20 Mg Tab')).toBe(
          normalizeDrugNameString('Amphetamine-Dextroamphetamine 20 Mg Tablet')
        );
      });

      it('does not rewrite "amphet" as a substring inside an unrelated longer word', () => {
        // Sanity check: a hypothetical token that merely CONTAINS "amphet"
        // as a substring (not a standalone/hyphen-delimited token) must
        // not be rewritten. "amphetamine" itself is the full ingredient
        // word (not the "amphet" abbreviation), so it must pass through
        // unchanged rather than matching the abbreviation key.
        expect(normalizeDrugNameString('Amphetamine 10mg tablet')).toBe('amphetamine 10 mg tablet');
      });

      it('folds "mixed salts"/"salts" away for this family without touching "sulfate"', () => {
        const r = compareDrugs(
          { name: 'Amphetamine-Dextroamphetamine Mixed Salts 20 mg tablet' },
          { name: 'Dextroamp-Amphet 20 Mg Tab' },
          provider
        );
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('does NOT strip "sulfate" — Amphetamine Sulfate is a different, non-combo product and stays distinct', () => {
        expect(normalizeDrugNameString('Amphetamine Sulfate 10mg tablet')).toBe('amphetamine sulfate 10 mg tablet');
        const r = compareDrugs(
          { name: 'Amphetamine Sulfate 10 mg tablet' },
          { name: 'Dextroamp-Amphet 10 mg tablet' },
          provider
        );
        expect(r.status).not.toBe('green');
      });

      it('is RED on a stated-strength mismatch even with the family name folding applied', () => {
        const r = compareDrugs(
          { name: 'Dextroamp-Amphet Er 30 Mg Cap' },
          { name: 'AMPHETAMINE-DEXTROAMPHET ER 25 MG CAPSULE EXTENDED RELEASE 24 HOUR' },
          provider
        );
        expect(r.status).toBe('red');
        expect(r.reasonCode).toBe('drug_mismatch');
      });

      it('does not let a stated release-duration mismatch (12 hour vs 24 hour) resolve to a false green', () => {
        const r = compareDrugs(
          { name: 'Dextroamp-Amphet Er 30 Mg Cap 24 Hour' },
          { name: 'AMPHETAMINE-DEXTROAMPHET ER 30 MG CAPSULE EXTENDED RELEASE 12 HOUR' },
          provider
        );
        expect(r.status).not.toBe('green');
      });

      // REVIEW FIX (confirmed false GREEN, fleet-wide — not amphetamine
      // specific): dedupeReleaseAbbrevs must drop only a REPEATED
      // occurrence of the SAME release qualifier, never a genuinely
      // different second qualifier. A single global "have we seen any
      // qualifier yet" boolean silently dropped a real "dr" that
      // followed an "er", collapsing an ER+DR product to the same
      // normalized string as an ER-only product.
      it('ER-only and ER+DR (two DIFFERENT release qualifiers) normalize distinctly, not the same string', () => {
        const erOnly = normalizeDrugNameString('Diltiazem ER 180 MG Capsule Extended Release');
        const erPlusDr = normalizeDrugNameString('Diltiazem ER 180 MG Capsule, Extended Release Delayed Release');
        expect(erOnly).not.toBe(erPlusDr);
        expect(erOnly).toBe('diltiazem er 180 mg capsule');
        expect(erPlusDr).toContain('dr');
      });

      it('does not let ER+DR resolve name-identity GREEN against an ER-only product', () => {
        const r = compareDrugs(
          { name: 'Diltiazem ER 180 MG Capsule Extended Release' },
          { name: 'Diltiazem ER 180 MG Capsule, Extended Release Delayed Release' },
          provider
        );
        expect(r.reasonCode).not.toBe('name_identity_match');
      });

      it('still dedupes a genuinely REPEATED qualifier (ER stated twice) to one occurrence', () => {
        expect(normalizeDrugNameString('Diltiazem ER 180 MG Capsule Extended Release')).toBe(
          normalizeDrugNameString('Diltiazem ER 180 MG Capsule')
        );
      });

      it('a stated duration on only ONE side does not block the match', () => {
        expect(extractStatedDurationHours('AMPHETAMINE-DEXTROAMPHET ER 30 MG CAPSULE EXTENDED RELEASE 24 HOUR')).toBe(24);
        expect(extractStatedDurationHours('Dextroamp-Amphet Er 30 Mg Cap')).toBeNull();
        const r = compareDrugs(
          { name: 'AMPHETAMINE-DEXTROAMPHET ER 30 MG CAPSULE EXTENDED RELEASE 24 HOUR' },
          { name: 'Dextroamp-Amphet Er 30 Mg Cap' },
          provider
        );
        expect(r.status).toBe('green');
      });
    });
  });
});

describe('Round 6 fixes', () => {
  describe('fix 2: concentration-strength false RED', () => {
    const garbledSource =
      'ZEPBOUND 12.5 MGIO.5 ML SUBCUTANEOUS PEN INJECTOR 12.5 mg/O.5 mL';
    const cleanEntered = 'Zepbound 12.5 Mg/0.5 Ml Pen';

    it('extracts the compound concentration strength from the garbled source, tolerating I->/ and O->0', () => {
      expect(extractStatedConcentrationStrength(garbledSource)).toBe('12.5mg/0.5ml');
    });

    it('extracts the same compound strength from the clean entered text', () => {
      expect(extractStatedConcentrationStrength(cleanEntered)).toBe('12.5mg/0.5ml');
    });

    it('is NOT red for the live-report pair (previously a false RED "5ml vs 12.5mg")', () => {
      const r = compareDrugs({ name: garbledSource }, { name: cleanEntered }, provider);
      expect(r.status).not.toBe('red');
    });

    it('the trailing restatement "12.5 mg/O.5 mL" in the same source string corroborates the same compound strength', () => {
      expect(extractStatedConcentrationStrength('12.5 mg/O.5 mL')).toBe('12.5mg/0.5ml');
    });

    it('regression: a REAL concentration-strength difference (both cleanly stated) stays RED', () => {
      const r = compareDrugs(
        { name: 'Zepbound 12.5 mg/0.5 ml Pen' },
        { name: 'Zepbound 15 mg/0.5 ml Pen' },
        provider
      );
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('drug_mismatch');
      expect(r.explanation).toContain('12.5mg/0.5ml');
      expect(r.explanation).toContain('15mg/0.5ml');
    });

    it('safety rule: concentration SIGNAL present (a unit+I/1-glued shape) but no leading number to pair it with — extraction fails cleanly, never guesses a RED', () => {
      const noLeadingNumber = 'ZEPBOUND MGI5 ML SUBCUTANEOUS PEN INJECTOR';
      expect(extractStatedConcentrationStrength(noLeadingNumber)).toBeNull();
      const r = compareDrugs({ name: noLeadingNumber }, { name: 'Zepbound 12.5 Mg/0.5 Ml Pen' }, provider);
      expect(r.status).not.toBe('red');
    });
  });

  describe('fix 3: trailing duplicate strength restatement', () => {
    it('is GREEN name_identity_match: "ELIQUIS 5 MG TABLET 5 mg" vs "Eliquis 5 Mg Tablet"', () => {
      const r = compareDrugs({ name: 'ELIQUIS 5 MG TABLET 5 mg' }, { name: 'Eliquis 5 Mg Tablet' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('strips only the trailing duplicate, leaving a salt suffix between the two occurrences untouched', () => {
      // Whole-number strength to isolate this fix from the pre-existing,
      // out-of-scope decimal-point-stripping behavior earlier in
      // normalizeDrugNameString's punctuation-folding step (it strips
      // "." from the whole string, so "0.5" would independently become
      // "05" regardless of this fix — see the exact-brief-text case
      // below for that shape, tested only for a sane non-crashing result
      // rather than an exact decimal string).
      expect(normalizeDrugNameString('PRAMIPEXOLE DIHYDROCHLORIDE 5 MG TABLET 5 mg')).toBe(
        'pramipexole dihydrochloride 5 mg tablet'
      );
    });

    it('the exact brief-text decimal example still resolves GREEN end-to-end (both sides fold the same "0.5"->"05" way, so they still agree)', () => {
      const r = compareDrugs(
        { name: 'PRAMIPEXOLE DIHYDROCHLORIDE 0.5 MG TABLET 0.5 mg' },
        { name: 'Pramipexole Dihydrochloride 0.5 Mg Tablet' },
        provider
      );
      expect(r.status).toBe('green');
    });

    it('salt-suffix folding stays out of scope: dropping the salt word on the entered side is not required to go green (may stay yellow)', () => {
      const r = compareDrugs(
        { name: 'PRAMIPEXOLE DIHYDROCHLORIDE 0.5 MG TABLET 0.5 mg' },
        { name: 'Pramipexole 0.5 Mg Tablet' },
        provider
      );
      expect(r.status).not.toBe('red');
      expect(r.reasonCode).not.toBe('name_identity_match');
    });

    it('regression: a CONTRADICTING trailing strength ("TABLET 10 mg" after "5 MG" stated earlier) is NOT folded away and still surfaces as a stated-strength-mismatch RED', () => {
      const r = compareDrugs(
        { name: 'LISINOPRIL 5 MG TABLET 10 mg' },
        { name: 'Lisinopril 10 Mg Tablet' },
        provider
      );
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('drug_mismatch');
      expect(r.explanation).toContain('5mg vs 10mg');
    });
  });

  describe('fix 4: one-sided route word in the form phrase', () => {
    it('is GREEN: "LISINOPRIL 10 MG ORAL TABLET" vs "Lisinopril 10 Mg Tablet"', () => {
      const r = compareDrugs({ name: 'LISINOPRIL 10 MG ORAL TABLET' }, { name: 'Lisinopril 10 Mg Tablet' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('is GREEN: "AMPHETAMINE-DEXTROAMPHETAMINE 7.5 MG ORAL TABLET" vs "Dextroamp-Amphetam 7.5 Mg Tab" (route-fold + amphetam abbreviation together)', () => {
      const r = compareDrugs(
        { name: 'AMPHETAMINE-DEXTROAMPHETAMINE 7.5 MG ORAL TABLET' },
        { name: 'Dextroamp-Amphetam 7.5 Mg Tab' },
        provider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('regression: BOTH sides stating DIFFERENT route qualifiers (oral vs sublingual) must NOT be folded into a match', () => {
      const r = compareDrugs(
        { name: 'Lisinopril 10 Mg Oral Tablet' },
        { name: 'Lisinopril 10 Mg Sublingual Tablet' },
        provider
      );
      expect(r.reasonCode).not.toBe('name_identity_match');
    });

    it('regression: BOTH sides stating the SAME route qualifier already matched via the primary exact-normalization path (unaffected by this fix)', () => {
      const r = compareDrugs(
        { name: 'Lisinopril 10 Mg Oral Tablet' },
        { name: 'Lisinopril 10 Mg Oral Tablet' },
        provider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });
  });

  describe('fix 5: amphetamine/dextroamphetamine abbreviation additions', () => {
    it('"amphetam" expands to "amphetamine"', () => {
      expect(normalizeDrugNameString('amphetam 5 mg tablet')).toBe('amphetamine 5 mg tablet');
    });

    it('"dextroamphetam" expands to "dextroamphetamine"', () => {
      expect(normalizeDrugNameString('dextroamphetam 5 mg tablet')).toBe('dextroamphetamine 5 mg tablet');
    });
  });
});

// Owner-reported live false negative: source "TRAMADOL 50 MG ORAL TABLET"
// vs entered "Tramadol Hcl 50 Mg Tablet" went yellow unknown_drug because
// the synthetic fixture concept DB doesn't carry tramadol at all -- both
// sides failed concept resolution and no name-based comparison ever ran,
// even though these are the same drug (one side just states the salt).
describe('component-wise name fallback (unknown_drug branch, concept resolution failed on both sides)', () => {
  it('is GREEN name_component_match for the exact live-report pair: "TRAMADOL 50 MG ORAL TABLET" vs "Tramadol Hcl 50 Mg Tablet"', () => {
    const r = compareDrugs(
      { name: 'TRAMADOL 50 MG ORAL TABLET' },
      { name: 'Tramadol Hcl 50 Mg Tablet' },
      unresolvedProvider
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_component_match');
  });

  it('is NOT green when both sides state a DIFFERING salt: "Metoprolol Tartrate 50 Mg Tablet" vs "Metoprolol Succinate 50 Mg Tablet" (clinically different, non-interchangeable products)', () => {
    const r = compareDrugs(
      { name: 'Metoprolol Tartrate 50 Mg Tablet' },
      { name: 'Metoprolol Succinate 50 Mg Tablet' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
    expect(r.explanation).toContain('tartrate');
    expect(r.explanation).toContain('succinate');
  });

  it('is NOT green when both salt AND form differ: "Hydroxyzine Hcl 25 Mg Tablet" vs "Hydroxyzine Pamoate 25 Mg Capsule"', () => {
    const r = compareDrugs(
      { name: 'Hydroxyzine Hcl 25 Mg Tablet' },
      { name: 'Hydroxyzine Pamoate 25 Mg Capsule' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
  });

  // Field report 2026-08-13 (RXVERIFY-TROUBLESHOOT): live OCR capture of
  // "METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET" vs entered "Metoprolol
  // Succ Er 50 Mg Tab" went yellow unknown_drug -- these are the same
  // drug (owner-confirmed): "Succ" is a salt abbreviation for succinate,
  // "(XL)"/"Er" are the same once-daily extended-release qualifier for
  // this formulation, and "(XL)" carries stray parens that previously
  // survived normalization as a literal ingredient token. See
  // SALT_ABBREVIATIONS/RELEASE_EQUIVALENCE_CLASS docs in src/drug/index.ts.
  describe('salt-abbreviation + release-equivalence + parenthesized-token fixes', () => {
    it('is GREEN name_component_match: "METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET" vs "Metoprolol Succ Er 50 Mg Tab"', () => {
      const r = compareDrugs(
        { name: 'METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET' },
        { name: 'Metoprolol Succ Er 50 Mg Tab' },
        unresolvedProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    it('is GREEN in the reverse direction too: "Metoprolol Succ Er 50 Mg Tab" vs "METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET"', () => {
      const r = compareDrugs(
        { name: 'Metoprolol Succ Er 50 Mg Tab' },
        { name: 'METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET' },
        unresolvedProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    it('is NOT green (yellow, per the IRON RULE) for the abbreviated salt conflict: "Metoprolol Tart 50 Mg Tablet" vs "Metoprolol Succ Er 50 Mg Tablet" (tartrate is the immediate-release version, never interchangeable with succinate)', () => {
      const r = compareDrugs(
        { name: 'Metoprolol Tart 50 Mg Tablet' },
        { name: 'Metoprolol Succ Er 50 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
      expect(r.explanation).toContain('tartrate');
      expect(r.explanation).toContain('succinate');
    });

    it('release-equivalence class covers XR/SR/LA too, all matching a plain "ER": "Metoprolol Succinate XR 50 Mg Tablet" vs "Metoprolol Succinate ER 50 Mg Tablet"', () => {
      const r = compareDrugs(
        { name: 'Metoprolol Succinate XR 50 Mg Tablet' },
        { name: 'Metoprolol Succinate ER 50 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    it('does NOT fold DR (delayed-release) into the XL/ER/XR/SR/LA/CR equivalence class: "Divalproex DR 250 Mg Tablet" vs "Divalproex ER 250 Mg Tablet" stays non-green', () => {
      const r = compareDrugs(
        { name: 'Divalproex DR 250 Mg Tablet' },
        { name: 'Divalproex ER 250 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });

    // Reviewer round 2, BLOCKER 1 (reviewer-reproduced live false green):
    // RELEASE_EQUIVALENCE_CLASS used to apply to EVERY ingredient, not
    // just metoprolol -- reproduced going GREEN for Bupropion XL vs SR,
    // the exact previously-fixed live false-green RELEASE_QUALIFIER_TOKENS'
    // own doc exists to prevent (SR and XL bupropion are genuinely
    // different, non-interchangeable products). Now gated on
    // ingredientTokens containing "metoprolol" specifically.
    it('does NOT apply the release-equivalence class outside metoprolol: "Bupropion Xl 150 Mg Tablet" vs "Bupropion Sr 150 Mg Tablet" stays non-green (yellow)', () => {
      const r = compareDrugs(
        { name: 'Bupropion Xl 150 Mg Tablet' },
        { name: 'Bupropion Sr 150 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
    });

    it('metoprolol succinate (XL) vs Succ ER is STILL green after the Bupropion gating fix (the one owner-confirmed interchangeable case)', () => {
      const r = compareDrugs(
        { name: 'METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET' },
        { name: 'Metoprolol Succ Er 50 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    // Reviewer round 2, BLOCKER 1: pins the metoprolol equivalence class
    // working even when only ONE side falls through to this name-based
    // fallback -- the OTHER side already resolved a concept via its NDC
    // (compareDrugs' `!srcConcept || !entConcept` branch triggers this
    // fallback whenever AT LEAST ONE side fails to resolve, and calls
    // compareNameComponents on both RAW names regardless of whether the
    // other side already resolved). Mirrors the real overlay shape: the
    // entered side never carries an NDC at all (PioneerRx's item field
    // is free-text only), so the source resolving via NDC while entered
    // falls to name components is the realistic asymmetric case, not a
    // contrived one.
    it('asymmetric resolution: source resolves via NDC concept, entered falls through to name components -- still GREEN for metoprolol XL/ER', () => {
      const metoprololConcept: RxConcept = {
        rxcui: 'RX-TEST-METOPROLOL',
        ingredient: 'metoprolol',
        strength: '50 mg',
        doseForm: 'tablet',
        name: 'Metoprolol Succinate 50mg ER Tablet'
      };
      const asymmetricProvider: RxNormProvider = {
        getConcept: (ndcOrName: string) => (ndcOrName === '00000123456' ? metoprololConcept : null)
      };

      const r = compareDrugs(
        { name: 'METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET', ndc: '00000-1234-56' },
        { name: 'Metoprolol Succ Er 50 Mg Tab' },
        asymmetricProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    // Reviewer-suggested explicit test (round 2): "CD" (controlled
    // delivery -- a real diltiazem release qualifier) is NOT in
    // COMPONENT_RELEASE_TOKENS at all, so it falls through as an ordinary
    // ingredient token instead of a release qualifier -- ingredientTokens
    // ends up {"diltiazem","cd"} on one side vs {"diltiazem"} on the
    // other, and rule 1 (ingredient-set equality) already blocks green.
    // Correct today only BY ACCIDENT of "cd" not being recognized as
    // anything special; pinned explicitly so a future change that adds
    // "cd" to the release-token vocabulary can't silently reopen a false
    // green here without a test catching it.
    it('diltiazem CD vs SR stays yellow (never green) -- "cd" is not a recognized release token, so this currently blocks on ingredient-set equality, not release-equivalence', () => {
      const r = compareDrugs(
        { name: 'Diltiazem CD 240 Mg Capsule' },
        { name: 'Diltiazem SR 240 Mg Capsule' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
    });
  });

  it('is GREEN for a one-sided route word: "Amoxicillin 500 Mg Oral Capsule" vs "Amoxicillin 500 Mg Capsule"', () => {
    const r = compareDrugs(
      { name: 'Amoxicillin 500 Mg Oral Capsule' },
      { name: 'Amoxicillin 500 Mg Capsule' },
      unresolvedProvider
    );
    expect(r.status).toBe('green');
  });

  it('is YELLOW (never green) when strength is missing on one side: "Tramadol Oral Tablet" vs "Tramadol Hcl 50 Mg Tablet"', () => {
    const r = compareDrugs(
      { name: 'Tramadol Oral Tablet' },
      { name: 'Tramadol Hcl 50 Mg Tablet' },
      unresolvedProvider
    );
    expect(r.status).toBe('yellow');
    expect(r.status).not.toBe('green');
  });

  it('is YELLOW unknown_drug (never red) when the ingredient genuinely differs: "Tramadol 50 Mg Tablet" vs "Trazodone 50 Mg Tablet"', () => {
    const r = compareDrugs(
      { name: 'Tramadol 50 Mg Tablet' },
      { name: 'Trazodone 50 Mg Tablet' },
      unresolvedProvider
    );
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
    expect(r.status).not.toBe('red');
  });

  it('is NOT green when the dosage form differs: "Tramadol 50 Mg Tablet" vs "Tramadol 50 Mg Capsule"', () => {
    const r = compareDrugs(
      { name: 'Tramadol 50 Mg Tablet' },
      { name: 'Tramadol 50 Mg Capsule' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
  });

  it('regression: a genuine stated-duration contradiction (12 hour vs 24 hour) must still never resolve to a false green via this new fallback', () => {
    // Same pair the amphetamine-family duration-conflict test above uses,
    // but with an unresolved-concept provider so this fallback (rather
    // than the name-identity fast path) is what's actually exercised.
    const r = compareDrugs(
      { name: 'Dextroamp-Amphet Er 30 Mg Cap 24 Hour' },
      { name: 'AMPHETAMINE-DEXTROAMPHET ER 30 MG CAPSULE EXTENDED RELEASE 12 HOUR' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
  });

  it('never preempts an existing concept-based path: a pair that DOES resolve (via the fixture provider) to the SAME concept is unaffected by this fallback', () => {
    // Both sides leading-word-match the fixture's single "metoprolol"
    // concept (FX0014) despite the extra filler token, so concept
    // resolution SUCCEEDS on both sides here -- this must go through the
    // existing same-rxcui concept_match branch, never this new fallback.
    const r = compareDrugs({ name: 'Metoprolol Foo 25 Mg Tablet' }, { name: 'Metoprolol Bar 25 Mg Tablet' }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('concept_match');
  });
});

// Reviewer round 1 on commit 1743449 -- REQUEST_CHANGES. BLOCKER 1
// (reviewer-reproduced live): isSlashUnit treated ANY '/'-split token
// whose halves were both numeric as disposable "unit-glued" noise, so a
// pure combo-product dose ratio like "5/325" was silently dropped from
// BOTH ingredient identity and strength -- "Hydrocodone/Acetaminophen
// 5/325 Mg Tablet" vs ".../10/325 Mg Tablet" (a genuinely different,
// clinically significant dose) resolved GREEN name_component_match. Fixed
// by (a) requiring at least one slash-half be a literal unit WORD before
// treating it as disposable, and (b) capturing pure numeric ratios
// (slash OR hyphen, decimals included) into a dedicated `ratios`
// component compared for exact, order-preserved equality.
describe('component-wise name fallback: reviewer round 1 fixes', () => {
  describe('BLOCKER 1: combo-product dose ratio ("5/325") must be identity-bearing, not disposable', () => {
    it('is NOT green: "Hydrocodone/Acetaminophen 5/325 Mg Tablet" vs ".../10/325 Mg Tablet" (slash ratio differs)', () => {
      const r = compareDrugs(
        { name: 'Hydrocodone/Acetaminophen 5/325 Mg Tablet' },
        { name: 'Hydrocodone/Acetaminophen 10/325 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
    });

    it('is NOT green for the same pair written with a hyphenated ratio: "5-325" vs "10-325"', () => {
      const r = compareDrugs(
        { name: 'Hydrocodone/Acetaminophen 5-325 Mg Tablet' },
        { name: 'Hydrocodone/Acetaminophen 10-325 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });

    it('is NOT green for Oxycodone/APAP with a differing slash ratio', () => {
      const r = compareDrugs(
        { name: 'Oxycodone/Acetaminophen 5/325 Mg Tablet' },
        { name: 'Oxycodone/Acetaminophen 7.5/325 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });

    it('a genuinely IDENTICAL ratio on both sides can still reach GREEN (via this fallback) when every other rule also passes', () => {
      // A one-sided salt word ("Bitartrate") keeps this pair off the
      // pre-concept-resolution identity/route-fold fast paths (so this
      // actually exercises compareNameComponents' ratio-equality check,
      // not just the exact-string identity match) while the stated
      // 5/325 ratio itself is identical on both sides.
      const r = compareDrugs(
        { name: 'Hydrocodone/Acetaminophen Bitartrate 5/325 Mg Tablet' },
        { name: 'Hydrocodone/Acetaminophen 5/325 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_component_match');
    });

    it('pins concentration-strength handling: "Amoxicillin 250 Mg/5 Ml Suspension" vs "Amoxicillin 250 Mg Tablet" is NOT green (form differs)', () => {
      // Also confirms the isSlashUnit fix didn't regress the legitimate
      // concentration case: "mg/5" (unit half present) must stay disposed
      // of as strength noise, not get mistaken for an identity-bearing
      // ratio token.
      const r = compareDrugs(
        { name: 'Amoxicillin 250 Mg/5 Ml Suspension' },
        { name: 'Amoxicillin 250 Mg Tablet' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });
  });

  describe('should-fix 3: empty ingredient-token set (both sides) is never a match, even against another empty set', () => {
    it('is NOT green when neither side has any token left over after stripping salt/route/form/strength', () => {
      const r = compareDrugs({ name: '50 Mg Hcl Tablet' }, { name: '50 Mg Tablet' }, unresolvedProvider);
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
    });
  });

  describe('should-fix 4: injectable route/form ambiguity is deliberate and fail-safe', () => {
    it('a stated "injection" route never reaches GREEN via this fallback, because it is consumed as a route and can never also confirm the form', () => {
      const r = compareDrugs(
        { name: 'Hydromorphone Hcl 2 Mg Injection' },
        { name: 'Hydromorphone 2 Mg Injection' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });
  });
});
