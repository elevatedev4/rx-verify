// One-off dev script: builds golden vector JSON files from scenario
// definitions by running them through the real engine, so the fixture
// files always start from actual engine output (which we then hand
// review against the spec). Not part of the shipped package.
import { writeFileSync } from 'node:fs';
import { verify } from '../src/engine/index.js';
import { FixtureProvider } from '../src/drug/index.js';
import type { ScriptData, EnteredData } from '../src/types.js';

const provider = new FixtureProvider();

interface Scenario {
  file: string;
  description: string;
  source: ScriptData;
  entered: EnteredData;
}

const scenarios: Scenario[] = [
  {
    file: '01-all-green.json',
    description: 'Everything matches exactly — all-green baseline.',
    source: {
      patientName: 'John Smith',
      patientDOB: '07/02/1980',
      patientAddress: { street: '123 N Main St', city: 'Springfield', state: 'IL', zip: '62704' },
      prescriber: { name: 'Dr. Jane Doe', npi: '1234567890' },
      dateWritten: '07/01/2026',
      drug: { ndc: '00071015523' },
      sig: 'take 1 tab po bid',
      quantity: 60,
      quantityUnit: 'tab',
      daysSupply: 30,
      refills: 2
    },
    entered: {
      patientName: 'John Smith',
      patientDOB: '07/02/1980',
      patientAddress: { street: '123 N Main St', city: 'Springfield', state: 'IL', zip: '62704' },
      prescriber: { name: 'Dr. Jane Doe', npi: '1234567890' },
      dateWritten: '07/01/2026',
      drug: { ndc: '00071015523' },
      sig: 'take 1 tab po bid',
      quantity: 60,
      quantityUnit: 'tab',
      daysSupply: 30,
      refills: 2
    }
  },
  {
    file: '02-generic-substitution.json',
    description: 'Brand prescribed, generic entered — same ingredient/strength/form.',
    source: {
      patientName: 'Mary Johnson',
      patientDOB: '01/15/1975',
      drug: { ndc: '00071015523' }, // Zestril 10mg
      sig: 'take 1 tab po qd',
      quantity: 30,
      daysSupply: 30,
      refills: 3
    },
    entered: {
      patientName: 'Mary Johnson',
      patientDOB: '01/15/1975',
      drug: { ndc: '00093715601' }, // generic lisinopril 10mg
      sig: 'take 1 tab po qd',
      quantity: 30,
      daysSupply: 30,
      refills: 3
    }
  },
  {
    file: '03-insurance-split-90-to-30.json',
    description: '90-day quantity on source, entered as a 30-day insurance-limited fill; sig math reconciles.',
    source: {
      patientName: 'Carlos Ramirez',
      drug: { ndc: '00093715801' },
      sig: 'take 1 tab po bid',
      quantity: 90,
      quantityUnit: 'tab',
      daysSupply: 45,
      refills: 1
    },
    entered: {
      patientName: 'Carlos Ramirez',
      drug: { ndc: '00093715801' },
      sig: 'take 1 tab po bid',
      quantity: 30,
      quantityUnit: 'tab',
      daysSupply: 15,
      refills: 1
    }
  },
  {
    file: '04-nickname.json',
    description: 'Patient first name is a common nickname variant.',
    source: { patientName: 'William Turner', patientDOB: '03/03/1990' },
    entered: { patientName: 'Bill Turner', patientDOB: '03/03/1990' }
  },
  {
    file: '05-transposed-name.json',
    description: 'Name order transposed (Last, First vs First Last) — still a match.',
    source: { patientName: 'Nguyen, Anh' },
    entered: { patientName: 'Anh Nguyen' }
  },
  {
    file: '06-wrong-dob.json',
    description: 'DOB present on both sides and contradicts — hard stop.',
    source: { patientName: 'Sarah Lee', patientDOB: '05/05/1985' },
    entered: { patientName: 'Sarah Lee', patientDOB: '05/05/1986' }
  },
  {
    file: '07-wrong-quantity-no-reconciliation.json',
    description: 'Quantity differs and does not reconcile with sig-based dosing math.',
    source: {
      drug: { ndc: '00071015523' },
      sig: 'take 1 tab po bid',
      quantity: 60,
      quantityUnit: 'tab'
    },
    entered: {
      drug: { ndc: '00071015523' },
      sig: 'take 1 tab po bid',
      quantity: 47,
      quantityUnit: 'tab'
    }
  },
  {
    file: '08-sig-expansion-equality.json',
    description: 'Sig written in full-word form on one side, abbreviated on the other — semantically equal.',
    source: { sig: 'take 1 tablet by mouth twice daily' },
    entered: { sig: 'take 1 tab po bid' }
  },
  {
    file: '09-ambiguous-sig.json',
    description: 'One side has an unparseable/ambiguous sig — needs human review.',
    source: { sig: 'use as directed' },
    entered: { sig: 'take 1 tab po bid' }
  },
  {
    file: '10-missing-days-supply.json',
    description: 'Source e-prescription omits days supply (normal NCPDP-optional field).',
    source: { quantity: 30, refills: 1 },
    entered: { quantity: 30, daysSupply: 30, refills: 1 }
  },
  {
    file: '11-prescriber-npi-match-name-variant.json',
    description: 'Prescriber NPI matches exactly but the name is spelled/abbreviated differently.',
    source: { prescriber: { name: 'Jonathan A. Reyes, MD', npi: '1720345678' } },
    entered: { prescriber: { name: 'Jon Reyes', npi: '1720345678' } }
  },
  {
    file: '12-ndc-pack-size.json',
    description: 'Same product, different package size NDC only.',
    source: { drug: { ndc: '00071015523' } }, // Zestril 10mg, bottle of 30
    entered: { drug: { ndc: '00071015590' } } // Zestril 10mg, bottle of 90
  },
  {
    file: '13-wrong-drug-strength.json',
    description: 'Same ingredient, different strength — a real contradiction.',
    source: { drug: { name: 'Synthroid 50mcg tablet' } },
    entered: { drug: { name: 'Amlodipine 5mg tablet' } }
  },
  {
    file: '14-address-unit-difference.json',
    description: 'Street/city/zip match; only the unit number differs.',
    source: { patientAddress: { street: '789 Elm St Apt 2', city: 'Metropolis', state: 'NY', zip: '10001' } },
    entered: { patientAddress: { street: '789 Elm St Apt 3', city: 'Metropolis', state: 'NY', zip: '10001' } }
  },
  {
    file: '15-empty-source-field-handling.json',
    description: 'Source is missing several fields entirely; every corresponding verdict is yellow not_provided, never a mismatch.',
    source: { patientName: 'Ava Brooks' },
    entered: {
      patientName: 'Ava Brooks',
      patientDOB: '02/02/1995',
      patientAddress: { street: '1 Test Way', city: 'Nowhere', state: 'TX', zip: '75001' },
      prescriber: { name: 'Dr. Kim', npi: '1112223330' },
      dateWritten: '07/01/2026',
      drug: { ndc: '00071015523' },
      sig: 'take 1 tab po bid',
      quantity: 60,
      daysSupply: 30,
      refills: 2
    }
  }
];

for (const scenario of scenarios) {
  const result = verify(scenario.source, scenario.entered, provider);
  const golden = {
    description: scenario.description,
    source: scenario.source,
    entered: scenario.entered,
    expected: result.verdicts.map((v) => ({
      field: v.field,
      status: v.status,
      reasonCode: v.reasonCode
    })),
    expectedSummary: result.summary
  };
  writeFileSync(`tests/golden/${scenario.file}`, JSON.stringify(golden, null, 2) + '\n');
  // eslint-disable-next-line no-console
  console.log(scenario.file, JSON.stringify(result.summary), result.verdicts.map((v) => `${v.field}:${v.status}/${v.reasonCode}`).join(' '));
}
