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

Same exception, same scope, for two more bundled files added for real
RxNorm-grade equivalence (see "RxNorm and wholesaler-catalog
equivalence" below):

- `data/rxnorm-data.json.gz` — **public**, from NLM's RxNorm "Current
  Prescribable Content" (public domain; NLM requests the release date
  be disclosed — see that file's own `generatedAt`/`source`/
  `attribution` fields). Same drug-reference-data exception as above.
- `data/catalog-data.json.gz` — **NOT public**. Derived from the
  pharmacy owner's own wholesaler catalog (GCN is First DataBank
  proprietary reference data, owner-supplied for his own tool).
  Internal use only within this private tool; never redistribute. The
  source `.xlsx` is never committed — only this derived file is.

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
| Drug identity | `src/drug/index.ts` | `RxNormProvider` interface, `LocalNdcProvider` (real, local, offline — see below), `FixtureProvider` (20 synthetic concepts, tests only), NDC parser (10/11-digit), `compareDrugs` + optional `DrugEquivalenceEvidence` (RxNorm/catalog upgrade layers — see below) |
| RxNorm equivalence (primary, public) | `src/drug/rxnorm.ts` | `RxNormDataProvider` (real, local, offline — `data/rxnorm-data.json.gz`), dose-form reconciliation (`doseFormsEquivalent`) |
| Wholesaler-catalog equivalence (secondary, internal) | `src/drug/catalog.ts` | `CatalogDataProvider` (real, local, offline — `data/catalog-data.json.gz`, NOT public data), GCN name resolution |
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
(~134k products / ~252k package NDCs), bundled as
`data/ndc-data.json.gz` (~5.4MB, committed to the repo) and loaded into
in-memory `Map`s once per process. **Zero network calls happen at lookup
time** — this preserves the HIPAA local-only guarantee for verification.

Besides the original NDC -> concept lookup, the bundle also carries a
**name index** (`nameIndex`: normalized brand/generic name -> candidate
product records, ~52k keys) and a **known-forms table**
(`formsByIngredient`: ingredient -> every distinct dosage form seen for
it, ~11k ingredient keys) — see `src/drug/local-data-format.ts` for the
shapes and `resolveConceptByName` in `src/drug/index.ts` for the
disambiguation rule. `LocalNdcProvider.getConcept` uses these to resolve
a free-text drug NAME (not just an NDC) to a single concept when it's
unambiguous, and `compareDrugs` uses a confirmed name-resolved match
(same ingredient/strength/form on both sides) to GREEN with reason
`concept_match` — e.g. "Vraylar 1.5 Mg Capsule" vs "CARIPRAZINE 1.5 MG
ORAL CAPSULE" now resolve to the same product record instead of falling
to `unknown_drug` yellow. This layer is strictly ADDITIVE and
IRON-RULE-bound: a lookup miss or an ambiguous name (maps to more than
one distinct ingredient/strength/form) always resolves to `null`, and
`compareDrugs` never lets name-based resolution alone escalate a pair to
red — worst case it's the same `unknown_drug` yellow this engine has
always fallen back to.

`FixtureProvider` (also in `src/drug/index.ts`) still exists as a small
~20-concept synthetic stand-in, used only by `tests/drug.test.ts` and
`scripts/gen-golden.ts` for deterministic golden-vector generation.

### Refreshing the dataset

`scripts/build-drug-data.ts` is a **build-time-only, maintainer-run**
script — the one place in this repo that's allowed to touch the
network. It downloads the openFDA NDC bulk file, extracts it (needs
`unzip` on PATH), transforms it into the compact `LocalConcept` shape
plus the `nameIndex`/`formsByIngredient` tables (see
`src/drug/local-data-format.ts` and `buildDataset` in this script), and
writes `data/ndc-data.json.gz`. Run it with:

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

### RxNorm and wholesaler-catalog equivalence (real RxCUI-backed upgrade layer)

The approximation above is what `LocalNdcProvider`/`compareDrugs` fall
back to on their own. `compareDrugs` also accepts an optional 4th
argument, `DrugEquivalenceEvidence`, with two independent, real-data
equivalence sources that can *upgrade* an existing `yellow` verdict to
`green` — never override a `red`, never invent one:

1. **RxNorm (primary, public)** — `RxNormDataProvider`
   (`src/drug/rxnorm.ts`), backed by `data/rxnorm-data.json.gz`, built
   from NLM's free, no-account-required **RxNorm Current Prescribable
   Content** monthly release (`scripts/build-rxnorm-data.ts`). The
   written NDC resolves to a real RXCUI-backed SCD/SBD concept
   (ingredient/strength/dose-form, plus an SBD's linked generic SCD via
   RXNREL `has_tradename`); the entered name resolves via the existing
   `LocalNdcProvider`/openFDA `resolveConceptByName`. The two don't
   share an id space (real RXCUI vs. the openFDA-derived synthetic
   `rxcui` above), so they're compared on the derived
   ingredient/strength/dose-form triple instead — see
   `rxNormMatchesLocalConcept`/`doseFormsEquivalent` in
   `src/drug/rxnorm.ts` (the latter reconciles RxNorm's "Extended
   Release Oral Tablet"-style dose-form text against openFDA's "TABLET,
   EXTENDED RELEASE"-style text via a route-word-stripped token-set
   comparison, since the two vocabularies order/word dose forms
   differently). A confirmed match is GREEN, reason `rxnorm_scd_match`.
2. **Wholesaler catalog (secondary, internal)** — `CatalogDataProvider`
   (`src/drug/catalog.ts`), backed by `data/catalog-data.json.gz`, built
   from the pharmacy's own wholesaler catalog export
   (`scripts/build-catalog-data.ts`; **not public data** — see the
   SYNTHETIC DATA section above). The written NDC's GCN (FDB Generic
   Code Number) is compared against the entered name's GCN, resolved via
   `resolveGcnByName` — a conservative, prefix-trial name matcher that
   deliberately mirrors `resolveConceptByName`'s discipline (narrow by
   stated strength, then release qualifier; any ambiguity → `null`,
   never a guess). A confirmed match is GREEN, reason `catalog_gcn_match`;
   a confirmed *mismatch* (both sides resolve, to different GCNs) is
   relabeled `catalog_gcn_mismatch` — still `yellow`, never `red` or
   `green`, from this layer alone.

Both providers are **optional and independently absent-safe**: a
missing/unbuilt data file degrades to "no evidence," and `compareDrugs`
with no `evidence` argument at all reproduces the pre-existing ladder's
behavior byte-for-byte (see `tests/drug-equivalence.test.ts`). Refresh
each dataset with:

```bash
npx tsx scripts/build-rxnorm-data.ts                                   # network (NLM), no account needed
npx tsx scripts/build-catalog-data.ts /path/to/catalog-export.xlsx     # local file, no network
```

**Still not done, follow-on if ever needed:** a real UMLS/UTS-licensed
full RxNorm release (this uses the free, license-free Prescribable
Content SUBSET instead — see `scripts/build-rxnorm-data.ts`'s header for
exactly what that subset contains); GPCK/BPCK combination-pack parsing
(deliberately out of scope for v1 — see `rxnorm-data-format.ts`'s
header).

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
  RxNorm — see "Generic-equivalence approximation" above. `compareDrugs`
  is handed real RxNorm/catalog evidence (`RxNormDataProvider` +
  `CatalogDataProvider`, constructed once at module scope in
  `src/cli.ts`, gracefully absent if either data file is missing) to
  upgrade some of those cases to a confirmed green — see "RxNorm and
  wholesaler-catalog equivalence" above — live in both CLI modes
  (one-shot and `--serve`).
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

Setup on a Windows PC is two copy/paste one-liners — everything else,
including installing missing tools, is handled by the Desktop shortcut
from then on.

**1. Set up this PC** (one-time per machine, works on a totally fresh
PC with no Git/Node/.NET yet, and is also safe to re-run on a machine
that already has all of it):

```powershell
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; & ([scriptblock]::Create((irm https://raw.githubusercontent.com/elevatedev4/rx-verify/main/bootstrap-fresh.ps1))) -ReportKey '<REPORTKEY>'
```

(`-ReportKey` is optional — see below. Omit the whole `-ReportKey '...'`
part, or run the older `irm ... | iex` form, and this works exactly the
same, just without seeding a report key.)

This checks for `winget` (Windows' built-in package manager, present on
any current Windows 10/11 box — if missing, it says so and points you
at the "App Installer" Store listing, then stops); installs Git,
Node.js LTS (needs major version 20+), and the .NET 8 SDK via `winget`
— skipping whichever are already present/new enough; clones the repo
to `%USERPROFILE%\claude\rx-verify` if it isn't there yet; creates the
**"Rx Verify"** Desktop shortcut; seeds/updates
`RxVerifyReportKey` in `%AppData%\RxVerifyOverlay\settings.json` if
`-ReportKey` was passed (this is what turns on the right-click "Report
error…" affordance — see "Error reporting" below); then hands off to
`update-and-run.ps1` for a fresh build + launch. Windows may show a few
Yes/No install prompts along the way — click Yes. Safe to re-run any
time: already-installed tools, an already-cloned repo, an
already-created shortcut, and an already-matching report key are all
detected and skipped/refreshed rather than duplicated — if a fresh
install needs a new console before it's picked up on PATH, the script
says so; just close PowerShell, reopen it, and paste the same line
again.

`-ReportKey` MUST be single-quoted, not double-quoted: Windows
PowerShell expands `$` inside a double-quoted string, so a key
containing `$` would silently truncate with zero diagnostic signal.

**2. Re-create shortcut** — only needed if the Desktop shortcut is ever
deleted or stops working; step 1 already creates it, so most machines
never need this run by hand:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\claude\rx-verify\install-shortcut.ps1"
```

After either, **launching is just double-clicking "Rx Verify" on the
Desktop** — before a shift, or whenever told a fix shipped. There's no
separate "launch" one-liner to keep around: the shortcut runs
`update-and-run.ps1`, which now **self-heals its own prerequisites**
every time it's launched — before it does anything else, it checks that
Git, Node.js (20+), and the .NET 8 SDK are present, and if any is
missing (a Windows reset, an uninstalled tool, a PC that got a shortcut
without ever running step 1) it installs it via the same `winget`
packages step 1 uses, refreshes PATH, and re-checks. When everything's
already present — the normal case — this is just three fast
presence/version checks, so it doesn't slow down the everyday
double-click.

What each script does:

- **`bootstrap-fresh.ps1`** — the "Set up this PC" one-liner above.
  Installs Git/Node/.NET 8 SDK via `winget` if missing, refreshes
  PATH from the registry if anything was installed, clones the repo if
  it isn't there yet ("already cloned" is judged by `update-and-run.ps1`
  existing inside it, not just the folder existing, so an interrupted
  clone is never silently retried over — it tells you the exact command
  to remove the broken copy first), creates/refreshes the Desktop
  shortcut (non-fatal if that one step fails), forwards `-ReportKey`
  straight through to `update-and-run.ps1`, then hands off to it.
- **`update-and-run.ps1`** — what the Desktop shortcut always runs, and
  the workhorse behind every launch. Accepts its own optional
  `-ReportKey` (seeds/updates `RxVerifyReportKey` in
  `%AppData%\RxVerifyOverlay\settings.json` without touching any other
  setting already there — see "Error reporting" below; a corrupt
  settings.json is backed up alongside itself, never silently discarded;
  omitted, the default, means "leave it alone", which is what makes it
  safe for the Desktop shortcut to always call this with no `-ReportKey`
  at all). Verifies Git/Node 20+/.NET 8 SDK
  first (installing anything missing, as above), force-syncs the local
  checkout to exactly match `origin/main` (`git fetch` + `git checkout
  -f -B main origin/main` — GitHub is the source of truth on these
  deploy-and-test machines, so this discards any local drift rather
  than merging or stashing it), runs `npm install` only if
  `package-lock.json` changed since the last successful install, then
  **always** runs `npm run build` (the TypeScript engine) and `dotnet
  build` (the overlay) fresh — every single run, no staleness
  guesswork — and launches the built `RxVerifyOverlay.exe`. Both builds
  are incremental under the hood (a warm `dotnet build` is well under a
  second), so always rebuilding costs nothing. If any step fails (a
  prerequisite that still won't resolve after installing, the sync,
  either build, or the `.exe` not being found), it prints exactly which
  step failed and the exact path/command involved, then holds the
  window open with "Press Enter to close" so you can read it.
- **`install-shortcut.ps1`** — the "Re-create shortcut" one-liner
  above, and what `bootstrap-fresh.ps1` also calls automatically.
  Creates (or overwrites — safe to re-run any time, never duplicates) a
  Desktop shortcut named **"Rx Verify"** that runs `update-and-run.ps1`;
  also clones the repo first if run standalone on a machine that
  skipped step 1. It then makes a best-effort attempt to pin that
  shortcut to the taskbar; Windows has no supported way for a script to
  guarantee this (Microsoft blocked it starting Windows 10 1903, and
  Windows 11 is stricter still), so on most current PCs you'll instead
  see a one-line prompt to right-click the Desktop shortcut and choose
  "Pin to taskbar" yourself, once.

### Error reporting

Right-clicking a verdict bar in Integrated mode opens a "Report error…"
dialog — but only on a workstation that has `RxVerifyReportKey` set in
`%AppData%\RxVerifyOverlay\settings.json`. There is no in-app UI to set
this yet; it's delivered entirely through the install/update flow above
(`-ReportKey` on `bootstrap-fresh.ps1` or `update-and-run.ps1`). A
workstation with no key set still gets a clear on-screen message on
right-click ("Error reporting isn't set up on this PC — run the pinned
setup line from Manager HQ.") instead of doing nothing — see
`overlay/RxVerifyOverlay/Integrated/IntegratedBoxesWindow.xaml.cs`
`ShowReportingDisabledNotice`. Re-running the Desktop shortcut (which
always omits `-ReportKey`) never blanks out a key a previous
`bootstrap-fresh.ps1`/`update-and-run.ps1` run already seeded.

If the sync in `update-and-run.ps1` ever fails (no network, a
GitHub-side issue), the script stops immediately and tells you to copy
the error and send it back — it will never try to stash, merge, or
discard anything beyond the intentional force-sync to `origin/main`
described above.

All three scripts are plain Windows PowerShell 5.1 (the version already
on every Windows 10/11 box — no PS7 install needed) and are safe to
re-run any time; nothing they do is destructive. None creates a
scheduled task or a background service — they only run when you
double-click the shortcut or run one directly.

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
