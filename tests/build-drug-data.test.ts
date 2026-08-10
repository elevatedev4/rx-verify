import { describe, it, expect } from 'vitest';
import {
  buildDataset,
  buildConcept,
  extractNameKeys,
  normalizeStrength,
  normalizeIngredientName,
  type OpenFdaProduct
} from '../scripts/build-drug-data.js';

// Small SYNTHETIC openFDA-shaped fixture — never the real 130k-record
// download (that's fetched by main() only, guarded off from module
// import — see the import.meta.url check at the bottom of
// scripts/build-drug-data.ts). Shapes deliberately mirror the fields
// buildConcept/extractNameKeys actually read (product_ndc, brand_name,
// generic_name, active_ingredients, dosage_form, packaging).
const ZYLO_5MG: OpenFdaProduct = {
  product_ndc: '11111-0001',
  brand_name: 'Zylo',
  generic_name: 'zylodrine',
  active_ingredients: [{ name: 'ZYLODRINE', strength: '5 mg/1' }],
  dosage_form: 'TABLET',
  packaging: [{ package_ndc: '11111-0001-01' }]
};

const ZYLO_10MG: OpenFdaProduct = {
  product_ndc: '11111-0002',
  brand_name: 'Zylo',
  generic_name: 'zylodrine',
  active_ingredients: [{ name: 'ZYLODRINE', strength: '10 mg/1' }],
  dosage_form: 'TABLET',
  packaging: [{ package_ndc: '11111-0002-01' }]
};

// A SECOND labeler's copy of the exact same drug (same ingredient,
// strength, form) under a different product_ndc/brand — the "many
// labelers, one distinct triple" case nameIndex/getConcept must collapse
// down to a single unambiguous result rather than treating as ambiguous.
const ZYLODRINE_GENERIC_OTHER_LABELER: OpenFdaProduct = {
  product_ndc: '22222-0001',
  generic_name: 'zylodrine',
  active_ingredients: [{ name: 'ZYLODRINE', strength: '5 mg/1' }],
  dosage_form: 'TABLET',
  packaging: [{ package_ndc: '22222-0001-01' }]
};

// Two GENUINELY DIFFERENT drugs that happen to collide on the same
// normalized brand name AND the same strength/form — the real ambiguity
// case this feature must refuse to guess through.
const CROSSTAG_FOXAMINE: OpenFdaProduct = {
  product_ndc: '33333-0001',
  brand_name: 'Crosstag',
  generic_name: 'foxamine',
  active_ingredients: [{ name: 'FOXAMINE', strength: '50 mg/1' }],
  dosage_form: 'TABLET',
  packaging: [{ package_ndc: '33333-0001-01' }]
};

const CROSSTAG_ZENTHOREL: OpenFdaProduct = {
  product_ndc: '44444-0001',
  brand_name: 'Crosstag',
  generic_name: 'zenthorel',
  active_ingredients: [{ name: 'ZENTHOREL', strength: '50 mg/1' }],
  dosage_form: 'TABLET',
  packaging: [{ package_ndc: '44444-0001-01' }]
};

// A combo product, to check the ingredient-set join/alphabetization used
// for formsByIngredient's key stays consistent with the pre-existing
// buildConcept behavior (unrelated to this branch, just re-verified).
const COMBO_CAPSULE: OpenFdaProduct = {
  product_ndc: '55555-0001',
  generic_name: 'dextroamphetamine and amphetamine',
  active_ingredients: [
    { name: 'DEXTROAMPHETAMINE', strength: '5 mg/1' },
    { name: 'AMPHETAMINE', strength: '5 mg/1' }
  ],
  dosage_form: 'CAPSULE, EXTENDED RELEASE',
  packaging: [{ package_ndc: '55555-0001-01' }]
};

// Same ingredient as Zylo above, different dosage form — exercises
// formsByIngredient collecting MULTIPLE distinct forms for one
// ingredient key.
const ZYLODRINE_LIQUID: OpenFdaProduct = {
  product_ndc: '66666-0001',
  generic_name: 'zylodrine',
  active_ingredients: [{ name: 'ZYLODRINE', strength: '5 mg/1' }],
  dosage_form: 'ORAL SOLUTION',
  packaging: [{ package_ndc: '66666-0001-01' }]
};

// Missing dosage_form -> buildConcept must skip it (pre-existing rule,
// re-verified here since buildDataset now also depends on it).
const MISSING_FORM: OpenFdaProduct = {
  product_ndc: '77777-0001',
  generic_name: 'skippium',
  active_ingredients: [{ name: 'SKIPPIUM', strength: '1 mg/1' }],
  packaging: [{ package_ndc: '77777-0001-01' }]
};

const FIXTURE_PRODUCTS: OpenFdaProduct[] = [
  ZYLO_5MG,
  ZYLO_10MG,
  ZYLODRINE_GENERIC_OTHER_LABELER,
  CROSSTAG_FOXAMINE,
  CROSSTAG_ZENTHOREL,
  COMBO_CAPSULE,
  ZYLODRINE_LIQUID,
  MISSING_FORM
];

describe('normalizeStrength / normalizeIngredientName (pre-existing, unchanged)', () => {
  it('strips the /1 unit denominator and whitespace', () => {
    expect(normalizeStrength('5 mg/1')).toBe('5mg');
  });

  it('lowercases and collapses whitespace', () => {
    expect(normalizeIngredientName('  Zylodrine  Hydrochloride ')).toBe('zylodrine hydrochloride');
  });
});

describe('buildConcept (pre-existing, unchanged)', () => {
  it('skips a product with no dosage_form rather than guessing', () => {
    expect(buildConcept(MISSING_FORM)).toBeNull();
  });

  it('joins and alphabetizes combo ingredients', () => {
    const concept = buildConcept(COMBO_CAPSULE);
    expect(concept?.ingredient).toBe('amphetamine;dextroamphetamine');
  });
});

describe('extractNameKeys', () => {
  it('emits both brand and generic name, normalized', () => {
    const keys = extractNameKeys(ZYLO_5MG);
    expect(keys).toContain('zylo');
    expect(keys).toContain('zylodrine');
  });

  it('dedupes when brand and generic normalize to the same string', () => {
    const product: OpenFdaProduct = { ...ZYLO_5MG, brand_name: 'Zylodrine', generic_name: 'Zylodrine' };
    expect(extractNameKeys(product)).toEqual(['zylodrine']);
  });

  it('returns an empty list when neither name field is present', () => {
    expect(extractNameKeys({ product_ndc: '1', dosage_form: 'TABLET' })).toEqual([]);
  });

  it('uses the SAME normalizer the runtime query side uses (shared build+runtime normalization)', () => {
    // "Extended Release" folds to "er" via normalizeDrugNameString,
    // shared by both build-time indexing and runtime query resolution
    // — see resolveConceptByName in src/drug/index.ts.
    const keys = extractNameKeys({ brand_name: 'Zylo Extended Release' });
    expect(keys).toEqual(['zylo er']);
  });
});

describe('buildDataset', () => {
  const { data, stats } = buildDataset(FIXTURE_PRODUCTS, { generatedAt: '2026-01-01T00:00:00.000Z', source: 'fixture' });

  it('skips the product missing a dosage_form and counts it', () => {
    expect(stats.skippedProducts).toBe(1);
    expect(stats.conceptCount).toBe(FIXTURE_PRODUCTS.length - 1);
  });

  it('indexes every real package NDC to its concept', () => {
    expect(Object.keys(data.ndcIndex).length).toBeGreaterThanOrEqual(7);
  });

  it('nameIndex maps a shared brand name to ALL matching product records (candidates, not pre-resolved)', () => {
    const zyloIdx = data.nameIndex?.['zylo'];
    expect(zyloIdx).toBeDefined();
    expect(zyloIdx).toHaveLength(2); // ZYLO_5MG, ZYLO_10MG
  });

  it('nameIndex maps a generic name across labelers to every record, including duplicates of the same drug', () => {
    const zylodrineIdx = data.nameIndex?.['zylodrine'];
    expect(zylodrineIdx).toBeDefined();
    // ZYLO_5MG, ZYLO_10MG, ZYLODRINE_GENERIC_OTHER_LABELER, ZYLODRINE_LIQUID
    expect(zylodrineIdx).toHaveLength(4);
  });

  it('an ambiguous shared name (two DIFFERENT ingredients) is stored with BOTH concept indices, not collapsed or dropped', () => {
    const crosstagIdx = data.nameIndex?.['crosstag'];
    expect(crosstagIdx).toBeDefined();
    expect(crosstagIdx).toHaveLength(2);
    const ingredientsSeen = new Set(crosstagIdx?.map((i) => data.concepts[i]?.ingredient));
    expect(ingredientsSeen).toEqual(new Set(['foxamine', 'zenthorel'])); // genuinely distinct ingredient sets
  });

  it('formsByIngredient collects every distinct dosage form seen for an ingredient key', () => {
    const forms = data.formsByIngredient?.['zylodrine'];
    expect(forms).toBeDefined();
    expect(forms).toEqual(['oral solution', 'tablet']); // sorted, deduplicated
  });

  it('formsByIngredient keys the combo product by its joined ingredient set, consistent with LocalConcept.ingredient', () => {
    const forms = data.formsByIngredient?.['amphetamine;dextroamphetamine'];
    expect(forms).toEqual(['capsule, extended release']);
  });

  it('stats report the shapes actually produced', () => {
    expect(stats.nameKeyCount).toBe(Object.keys(data.nameIndex ?? {}).length);
    expect(stats.ingredientCount).toBe(Object.keys(data.formsByIngredient ?? {}).length);
  });
});
