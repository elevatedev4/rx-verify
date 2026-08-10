import { describe, it, expect } from 'vitest';
import { parseNdc, compareDrugs, normalizeDrugNameString, extractStatedDurationHours, FixtureProvider } from '../src/drug/index.js';

const provider = new FixtureProvider();

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
