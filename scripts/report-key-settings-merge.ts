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
 * built on to unit-test that script directly. The branch logic here
 * (key present/absent/different/corrupt-file) is copied by hand into
 * update-and-run.ps1 — see that script's own comment pointing back here.
 * If this decision table ever changes, update BOTH places.
 */

export type ReportKeyMergeDecision =
  | { kind: 'skip-no-key-provided' }
  | { kind: 'skip-already-matches' }
  | { kind: 'write'; corruptBackup: boolean };

export interface ReportKeyMergeInput {
  /** The -ReportKey value passed on the command line; '' means the parameter was not supplied (PowerShell's own default). */
  providedKey: string;
  /** Whether settings.json exists on disk at all. */
  fileExists: boolean;
  /** Whether the existing file's content parsed as valid JSON (irrelevant when fileExists is false). */
  fileParsedOk: boolean;
  /** The RxVerifyReportKey value read from the parsed file, or undefined if the file doesn't exist, didn't parse, or simply has no such field yet (an old settings.json predating this feature). */
  existingKey: string | undefined;
}

/**
 * Decides what update-and-run.ps1 should do to settings.json for a given
 * -ReportKey invocation. Never itself reads/writes a file — see the class
 * doc above for why this stays pure.
 */
export function decideReportKeyMerge(input: ReportKeyMergeInput): ReportKeyMergeDecision {
  const { providedKey, fileExists, fileParsedOk, existingKey } = input;

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

  const corrupt = fileExists && !fileParsedOk;

  // A corrupt file can never be trusted to tell us the "existing" value,
  // so it always counts as a write — regardless of what existingKey
  // happens to be (it will be undefined for a corrupt file in practice,
  // but this check does not rely on that).
  if (!corrupt && existingKey === providedKey) {
    return { kind: 'skip-already-matches' };
  }

  return { kind: 'write', corruptBackup: corrupt };
}
