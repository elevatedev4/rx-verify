/**
 * Pure mirror of the settings-merge DECISION that
 * update-and-run.ps1's Set-ReportKeyIfProvided function implements in
 * PowerShell to seed/update RxVerifyReportKey in
 * %AppData%\RxVerifyOverlay\settings.json (feat/report-key-delivery).
 *
 * NOT invoked by the engine, the CLI, or the overlay at runtime — this
 * file is excluded from `npm run build` (tsconfig.json only includes
 * "src"), same as every other one-off file already in scripts/. Its only
 * purpose is testability: the actual fix ships as PowerShell (it has to —
 * it edits a file on a Windows workstation before/without the .NET app
 * running), and there is no PowerShell available on the machine this was
 * built on to unit-test that script directly. The branch logic here is
 * copied by hand into update-and-run.ps1 — see that script's own comment
 * pointing back here. If this decision table ever changes, update BOTH
 * places.
 */

export type ReportKeyMergeDecision =
  | { kind: 'skip-no-key-provided' }
  | { kind: 'skip-already-matches' }
  | { kind: 'write'; corruptBackup: boolean };

/**
 * Every shape update-and-run.ps1's Set-ReportKeyIfProvided can find
 * settings.json in, modeled as distinct cases rather than one collapsed
 * boolean — REVIEW FIX: an earlier version used a single fileParsedOk
 * boolean, which could not distinguish "ConvertFrom-Json threw" from
 * "ConvertFrom-Json succeeded but returned something that isn't a usable
 * object" even though the PS script itself treats those as two separate
 * code paths (and, before that same review, only backed up the file in
 * the first one — see 'parsed-non-object' below). Modeling them
 * separately here means a test can actually target each PS branch
 * individually instead of two tests with identical inputs.
 */
export type ExistingSettingsFileState =
  /** No settings.json on disk at all — fresh install. */
  | { kind: 'missing' }
  /** File exists but Get-Content/ConvertFrom-Json threw (hand-edited and broken, truncated mid-write, unreadable, etc). */
  | { kind: 'unreadable-or-unparseable' }
  /** ConvertFrom-Json succeeded but the result isn't a usable settings object — literal `null`, or valid-but-non-object JSON like `"5"` or `[1,2,3]`. */
  | { kind: 'parsed-non-object' }
  /** ConvertFrom-Json succeeded and returned an object — existingKey is its RxVerifyReportKey value, or undefined if that field simply isn't present yet (an old settings.json predating this feature). */
  | { kind: 'parsed-object'; existingKey: string | undefined };

export interface ReportKeyMergeInput {
  /** The -ReportKey value passed on the command line; '' means the parameter was not supplied (PowerShell's own default). */
  providedKey: string;
  fileState: ExistingSettingsFileState;
}

/**
 * Decides what update-and-run.ps1 should do to settings.json for a given
 * -ReportKey invocation. Never itself reads/writes a file — see the class
 * doc above for why this stays pure.
 */
export function decideReportKeyMerge(input: ReportKeyMergeInput): ReportKeyMergeDecision {
  const { providedKey, fileState } = input;

  // No -ReportKey passed (the shortcut's own re-run, or a bootstrap
  // one-liner without a key baked in) — never touch an existing key.
  // This is what guarantees the Desktop shortcut (which always omits
  // -ReportKey) can never blank out a key a previous run already seeded.
  // Whitespace-only counts as "not provided" too — mirrors
  // update-and-run.ps1's own [string]::IsNullOrWhiteSpace check, not a
  // plain '' comparison, so an accidentally-blank -ReportKey '' (or
  // -ReportKey ' ') argument can never overwrite an existing key with
  // whitespace.
  if (providedKey.trim() === '') {
    return { kind: 'skip-no-key-provided' };
  }

  switch (fileState.kind) {
    case 'missing':
      return { kind: 'write', corruptBackup: false };

    // Both of these get the SAME backup-and-start-fresh treatment in
    // update-and-run.ps1 — a corrupt/non-object file can never be
    // trusted to tell us the "existing" value, so it always counts as a
    // write, and the file is backed up (not silently discarded) either
    // way.
    case 'unreadable-or-unparseable':
    case 'parsed-non-object':
      return { kind: 'write', corruptBackup: true };

    case 'parsed-object':
      if (fileState.existingKey === providedKey) {
        return { kind: 'skip-already-matches' };
      }
      return { kind: 'write', corruptBackup: false };
  }
}
