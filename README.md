# rx-verify

Deterministic matching engine that compares an incoming e-prescription
against what a pharmacy technician entered into PioneerRx, and produces a
per-field verdict a technician can act on in seconds.

**This is P0a: the core matching engine.** It is a standalone TypeScript
library today. P0b will embed it in a Windows overlay app that watches
PioneerRx and shows these verdicts live during data entry (see "Next
steps" below).

## ⚠️ SYNTHETIC DATA ONLY (patient/prescriber) — with one explicit exception

Every name, DOB, address, NPI anywhere in this repository — source,
fixtures, tests, golden vectors — is fabricated. **No real patient,
prescriber, or prescription data may ever be committed here**, in a
test, in a comment, or in a fixture. If you're adding a test case,
invent the data; don't copy it from anywhere real.

**Exception, deliberate and scoped:** `data/ndc-data.json.gz` and the
real NDCs referenced in `tests/local-ndc-provider.test.ts` are **public
FDA drug-reference data** (product/ingredient/strength/dosage-form —
openFDA NDC directory, public domain, no patient involved anywhere).
This is drug catalog data, not PHI, and is what lets the engine
identify real drugs offline (see "Drug data: LocalNdcProvider" below).
It does not relax the rule above for patient/prescriber/prescription
data — that rule still applies to everything else in this repo.

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
| Drug identity | `src/drug/index.ts` | `RxNormProvider` interface, `LocalNdcProvider` (real, local, offline — see below), `FixtureProvider` (20 synthetic concepts, tests only), NDC parser (10/11-digit) |
| Quantity / days supply / refills / prescriber | `src/quantity/index.ts` | Unit normalization, sig-math reconciliation for quantity splits, NPI-based prescriber compare |
| Engine | `src/engine/index.ts` | `verify(source, entered, provider)` → ordered `FieldVerdict[]` + summary counts |
| Types | `src/types.ts` | Shared, JSON-serializable data shapes; `FIELD_ORDER` |
| CLI wrapper | `src/cli.ts` | stdin/stdout JSON wrapper for non-Node hosts (see `overlay/`) — the one sanctioned Node-specific file in this repo |

Golden end-to-end scenarios live in `tests/golden/*.json` and are
exercised by `tests/golden.test.ts`. `scripts/gen-golden.ts` is a dev-only
generator used to produce those fixtures from scenario definitions run
through the real engine (not part of the published package).

## Drug data: LocalNdcProvider (real, local, offline)

`src/cli.ts` wires in `LocalNdcProvider` (`src/drug/index.ts`) — a real
drug dataset derived from the public **openFDA NDC directory**
(~134k products / ~251k package NDCs), bundled as
`data/ndc-data.json.gz` (~4MB, committed to the repo) and loaded into an
in-memory `Map` once per process. **Zero network calls happen at lookup
time** — this preserves the HIPAA local-only guarantee for verification.

`FixtureProvider` (also in `src/drug/index.ts`) still exists as a small
~20-concept synthetic stand-in, used only by `tests/drug.test.ts` and
`scripts/gen-golden.ts` for deterministic golden-vector generation.

### Refreshing the dataset

`scripts/build-drug-data.ts` is a **build-time-only, maintainer-run**
script — the one place in this repo that's allowed to touch the
network. It downloads the openFDA NDC bulk file, extracts it (needs
`unzip` on PATH), transforms it into the compact `LocalConcept` shape
(see `src/drug/local-data-format.ts`), and writes
`data/ndc-data.json.gz`. Run it with:

```bash
npx tsx scripts/build-drug-data.ts
```

Re-run it periodically to pick up new/changed NDCs from openFDA; commit
the regenerated `data/ndc-data.json.gz`.

### Generic-equivalence approximation (documented limitation)

openFDA's NDC directory doesn't carry one reliable RxNorm CUI per
product, so `LocalNdcProvider` derives an approximate equivalence key
(`deriveRxcui` in `src/drug/local-data-format.ts`) from the normalized
ingredient-set + per-ingredient strength + dosage form, and puts it in
`RxConcept.rxcui`. This drives the same `generic_substitution`/
`pack_size` logic in `compareDrugs` that the fixture's real-ish `rxcui`
values did. It's coarser than real RxNorm (e.g. it treats
"atorvastatin" and "atorvastatin calcium trihydrate" as different
ingredients, since they're different strings, even though they're the
same drug via different salt forms) — but it can only ever fail toward
*more* yellow/red, never a false green, so it's safe under this
engine's verdict philosophy.

### Precise RxNorm equivalence — owner follow-on, not done here

1. Create a free NLM UTS (UMLS Terminology Services) account —
   https://uts.nlm.nih.gov/uts/signup-login. **This is an owner task**
   (requires an individual sign-up), not something a subagent can do.
2. Pull the RxNorm data files (RXNCONSO.RRF, RXNSAT.RRF, etc.) or use the
   RxNorm REST API (build-time-only fetch, same offline-at-runtime rule
   as above).
3. Implement `RxNormProvider` (`getConcept(ndcOrName): RxConcept | null`)
   against that real data, with a real per-product rxcui.
4. Pass the new provider into `verify(source, entered, provider)` in
   place of `LocalNdcProvider`. No other engine code changes.

## Portability

This library is written to be portable to a future C#/.NET host (either
ported directly or run behind a sidecar process):

- Zero runtime dependencies.
- Pure functions only — no Node-specific APIs (`fs`, `process`, etc.) in
  any comparison/normalization logic. `LocalNdcProvider` is the one
  deliberate exception (like `src/cli.ts`) — it uses `node:fs`/
  `node:zlib` to load the bundled dataset; a future non-Node host would
  reimplement that one class against the same `data/ndc-data.json.gz`
  file, not the comparison logic.
- All inputs and outputs (`ScriptData`, `EnteredData`, `FieldVerdict[]`)
  are plain, JSON-serializable objects.
- The one deliberate exception is `src/cli.ts`, which *does* use
  Node's stdin/stdout (`process.stdin`/`process.stdout`) — that's the
  sanctioned integration seam for non-Node hosts (see `overlay/`) and
  is intentionally kept separate from the pure comparison/normalization
  modules above.

## Development

```bash
npm install
npm test          # vitest run
npm run typecheck  # tsc --noEmit
npm run build      # emit dist/
```

## Status / what's stubbed

- `LocalNdcProvider` (drug identity) is real, local, offline openFDA
  data, but its generic-equivalence key is an approximation, not real
  RxNorm — see "Generic-equivalence approximation" above. It also only
  resolves via NDC, not free-text drug name (a name-only side falls
  through to `unknown_drug` yellow rather than guessing — see the class
  comment in `src/drug/index.ts`).
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

## CLI entrypoint for non-Node hosts

`src/cli.ts` (built to `dist/cli.js`) is a thin stdin/stdout JSON
wrapper around `verify()`: send `{ "source": ScriptData, "entered":
EnteredData }` as JSON on stdin, get a `VerifyResult` JSON back on
stdout. It exists so a host written in another language (see
`overlay/`) can call this engine as a local subprocess without
reimplementing any of its logic. See `overlay/README.md` "Why the
engine is a subprocess, not a port" for the reasoning, and
`tests/cli.test.ts` for the contract it's tested against.

## P0b: the Windows overlay — now underway

`overlay/` contains a first-draft .NET/WPF + FlaUI Windows app that
reads PioneerRx's fields via UI Automation and renders this engine's
verdicts always-on-top, in the fixed field order, per the phase-0 spec.
**It has not been run against a live PioneerRx window** (built without
Windows/UIA access) — see `overlay/README.md` for what's implemented,
what's known-uncertain (the UIA label/geometry guesses), and the
"Dump UIA Tree" debug workflow for validating/adjusting it on a real
workstation.

### Rapid update/deploy workflow (Windows) — one double-click

**Important: `dotnet build` alone does NOT launch the app.** It only
compiles. To actually run the overlay you must run the built `.exe` (or
`dotnet run`) — or, easier, use `update-and-run.ps1` below, which builds
*and* launches in one step.

Pulling, rebuilding, and relaunching by hand every time a change lands
gets old fast. Two scripts at the repo root turn that into a single
double-click. Both are **self-locating** — they figure out the repo
root from their own folder (`$PSScriptRoot`), so they work no matter
where you've cloned the repo. The canonical location on a fresh PC is
`%USERPROFILE%\claude\rx-verify`.

- **`update-and-run.ps1`** — pulls the latest code (`git pull
  --ff-only` — never merges/rebases, so it can never clobber a local
  edit), runs `npm install` only if `package-lock.json` changed since
  the last successful install, then **always** runs `npm run build`
  (the TypeScript engine) and `dotnet build` (the overlay) fresh —
  every single run, no staleness guesswork — and launches the built
  `RxVerifyOverlay.exe`. Both builds are incremental under the hood
  (a warm `dotnet build` is well under a second), so always rebuilding
  costs nothing and guarantees you're never looking at a stale binary.
  If any step fails (pull, either build, or the `.exe` not being
  found), it prints exactly which step failed and the exact path it
  looked for, then holds the window open with "Press Enter to close"
  so you can read it.
- **`install-shortcut.ps1`** — one-time setup. Makes sure the repo
  exists at `%USERPROFILE%\claude\rx-verify` (cloning it, creating the
  `claude` parent folder if needed, if this is the very first run on
  this machine) and creates a Desktop shortcut named **"Rx Verify"**
  that runs `update-and-run.ps1`. This is a convenience — the primary
  flow is running `update-and-run.ps1` directly (see below).

**Fresh PC (bootstrap — clones to `\claude\rx-verify` then runs):**

```powershell
powershell -ExecutionPolicy Bypass -Command "if(!(Test-Path \"$env:USERPROFILE\claude\rx-verify\")){ New-Item -ItemType Directory -Force -Path \"$env:USERPROFILE\claude\" | Out-Null; git clone https://github.com/elevatedev4/rx-verify.git \"$env:USERPROFILE\claude\rx-verify\" }; powershell -ExecutionPolicy Bypass -File \"$env:USERPROFILE\claude\rx-verify\update-and-run.ps1\""
```

**Every run after (pull + fresh build + launch):**

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\claude\rx-verify\update-and-run.ps1"
```

**Optional one-time shortcut setup**, from inside an already-cloned repo:

```powershell
powershell -ExecutionPolicy Bypass -File install-shortcut.ps1
```

Then double-click **"Rx Verify"** on the Desktop any time — before a
shift, or whenever told a fix shipped.

If `git pull` ever fails (diverged history, a stray local edit, no
network), the script stops immediately, changes nothing, and tells you
to copy the error and send it back — it will never try to stash,
merge, or discard anything on its own.

Both scripts are plain Windows PowerShell 5.1 (the version already on
every Windows 10/11 box — no PS7 install needed) and are safe to
re-run any time; nothing they do is destructive. Neither creates a
scheduled task or a background service — they only run when you
double-click the shortcut or run them directly.

Remaining suggested next steps:

1. Validate/adjust `overlay/RxVerifyOverlay/Uia/FieldMap.cs` and
   `PioneerRxWindow.cs` against a live PioneerRx window (see
   `overlay/README.md` "If fields read wrong").
2. Wire a precise RxNorm provider (owner: create UTS account) — swap it
   into `src/cli.ts`'s `LocalNdcProvider` construction; no other engine
   or overlay code changes.
3. Add telemetry/logging (synthetic-safe — no PHI) to see which reason
   codes fire most often in real use, to prioritize round 2 of the
   nickname table, sig abbreviations, and address suffix table.
4. Decide on an audit trail: does a pharmacist's override of a red/yellow
   verdict need to be recorded? (Likely yes, for compliance — worth a
   product conversation before a real pilot starts.)
5. OCR for the faxed/scanned-script slice (small % of volume, deferred
   per the phase-0 spec) — see `overlay/README.md` "Deferred".
6. Installer/signing/packaging once the overlay is validated live.
