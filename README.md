# rx-verify

Deterministic matching engine that compares an incoming e-prescription
against what a pharmacy technician entered into PioneerRx, and produces a
per-field verdict a technician can act on in seconds.

**This is P0a: the core matching engine.** It is a standalone TypeScript
library today. P0b will embed it in a Windows overlay app that watches
PioneerRx and shows these verdicts live during data entry (see "Next
steps" below).

## ⚠️ SYNTHETIC DATA ONLY

Every name, DOB, address, NPI, and NDC anywhere in this repository —
source, fixtures, tests, golden vectors — is fabricated. **No real
patient, prescriber, or prescription data may ever be committed here**,
in a test, in a comment, or in a fixture. If you're adding a test case,
invent the data; don't copy it from anywhere real.

## Verdict philosophy

Every field gets exactly one verdict: `green`, `yellow`, or `red`.

- **GREEN** is conservative. It only fires on a normalized-exact match —
  same information, expressed differently (case, punctuation, word
  order, abbreviation expansion, NDC package variant of the same
  product). GREEN never fires just because something "seems fine."
- **YELLOW** means *a human should look at this*. It covers two very
  different situations that both deserve the same visual weight:
  1. A **legitimate difference** — a nickname, a generic substitution, a
     stale address, an insurance-driven quantity split. The system isn't
     confused; it's telling you there's a normal reason for the
     difference, and a human should glance at it before moving on.
  2. **Insufficient data** — the source e-prescription simply didn't
     provide this field, or something was unparseable/ambiguous. Missing
     data is never treated as a mismatch and never escalates past
     yellow.
- **RED** means contradiction, with no legitimate-difference rule that
  explains it. A different DOB, a different drug ingredient, a
  mismatched NPI, sig math that doesn't reconcile with the dispensed
  quantity. RED is reserved for things that should stop a technician and
  make them double check before dispensing.

Every verdict carries a machine-readable `reasonCode` (e.g.
`nickname_match`, `generic_substitution`, `quantity_adjusted`,
`surname_mismatch`) plus a human-readable `explanation` — the engine
"shows its work" instead of asserting a verdict.

### Fixed field order

The output verdict array is **always** in this order — a hard
requirement from the owner, a working pharmacist, matching the order a
tech naturally checks a script:

1. patient name
2. patient DOB
3. patient address
4. prescriber
5. date written
6. drug
7. sig / directions
8. quantity
9. days supply
10. refills

The order is never re-sorted by severity or anything else. `verify()`
asserts this invariant at runtime (`src/engine/index.ts`) so a future
refactor can't silently break it.

## Module map

| Module | Path | Responsibility |
|---|---|---|
| Name normalization | `src/normalize/name.ts` | Case/punctuation folding, "Last, First" ↔ "First Last", hyphenated-surname tolerance, ~100-pair nickname table |
| Date normalization | `src/normalize/date.ts` | MM/DD/YYYY, M/D/YY, ISO, "Jul 2, 2026" → ISO; DOB/date-written comparison |
| Address normalization | `src/normalize/address.ts` | USPS street-suffix/directional/unit abbreviation tables, component compare |
| Sig parsing/comparison | `src/sig/index.ts` | Abbreviation expansion (route, frequency, PRN, duration, roman-numeral doses), structural comparison |
| Drug identity | `src/drug/index.ts` | `RxNormProvider` interface, `FixtureProvider` (20 synthetic concepts), NDC parser (10/11-digit) |
| Quantity / days supply / refills / prescriber | `src/quantity/index.ts` | Unit normalization, sig-math reconciliation for quantity splits, NPI-based prescriber compare |
| Engine | `src/engine/index.ts` | `verify(source, entered, provider)` → ordered `FieldVerdict[]` + summary counts |
| Types | `src/types.ts` | Shared, JSON-serializable data shapes; `FIELD_ORDER` |

Golden end-to-end scenarios live in `tests/golden/*.json` and are
exercised by `tests/golden.test.ts`. `scripts/gen-golden.ts` is a dev-only
generator used to produce those fixtures from scenario definitions run
through the real engine (not part of the published package).

## Swapping in real RxNorm data

`FixtureProvider` in `src/drug/index.ts` is a stand-in with ~20
synthetic-but-realistic drug concepts (brand/generic pairs like
Zestril/lisinopril, Lipitor/atorvastatin, Synthroid/levothyroxine,
Glucophage/metformin, plus amoxicillin, azithromycin, and others). It
exists purely so the drug-matching logic has something deterministic to
test against.

To go live:

1. Create a free NLM UTS (UMLS Terminology Services) account —
   https://uts.nlm.nih.gov/uts/signup-login. **This is an owner task**
   (requires an individual sign-up), not something a subagent can do.
2. Pull the RxNorm data files (RXNCONSO.RRF, RXNSAT.RRF, etc.) or use the
   RxNorm REST API.
3. Implement `RxNormProvider` (`getConcept(ndcOrName): RxConcept | null`)
   against that real data.
4. Pass the new provider into `verify(source, entered, provider)` in
   place of `FixtureProvider`. No other engine code changes.

## Portability

This library is written to be portable to a future C#/.NET host (either
ported directly or run behind a sidecar process):

- Zero runtime dependencies.
- Pure functions only — no Node-specific APIs (`fs`, `process`, etc.) in
  any comparison/normalization logic.
- All inputs and outputs (`ScriptData`, `EnteredData`, `FieldVerdict[]`)
  are plain, JSON-serializable objects.

## Development

```bash
npm install
npm test          # vitest run
npm run typecheck  # tsc --noEmit
npm run build      # emit dist/
```

## Status / what's stubbed

- `FixtureProvider` (drug identity) is intentionally a fixture, not real
  RxNorm data — see "Swapping in real RxNorm data" above. This is the
  single biggest thing standing between this engine and real-world drug
  matching.
- Nickname table covers ~100 common US first-name pairs; it is not
  exhaustive. Unrecognized nicknames fall through to a light
  prefix-based fuzzy check, and failing that, a `red` surname-based
  mismatch if surnames also differ, or a `red` first-name mismatch if
  the surname matched but the first name is unrecognized as a variant.
- Sig parsing covers a broad but not universal abbreviation set (see
  `src/sig/index.ts`); anything it can't structurally parse becomes
  `yellow sig_ambiguous` rather than a guess.
- Address comparison is component-based (street/city/state/zip/unit); it
  does not do fuzzy string distance on the street line beyond suffix/
  directional/unit normalization.

## Suggested next steps (P0b)

1. Wire the real RxNorm provider (owner: create UTS account).
2. Build the Windows overlay host: watch PioneerRx's on-screen fields
   (or its DB/API if available), assemble a `ScriptData`/`EnteredData`
   pair per field as the tech types, and call `verify()` live.
2a. Decide the source-of-truth transport for `ScriptData` (direct
    e-prescription feed vs. a queue) — this is a product decision, not an
    engine one.
3. Render the fixed-order verdict list in the overlay UI with the
   reason code + explanation surfaced per field (not just a color).
4. Add telemetry/logging (synthetic-safe — no PHI) to see which reason
   codes fire most often in real use, to prioritize round 2 of the
   nickname table, sig abbreviations, and address suffix table.
5. Decide on an audit trail: does a pharmacist's override of a red/yellow
   verdict need to be recorded? (Likely yes, for compliance — worth a
   product conversation before P0b starts.)
