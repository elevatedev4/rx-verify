import { describe, it, expect } from 'vitest';
import { decideReportKeyMerge } from '../scripts/report-key-settings-merge.js';

// This module (scripts/report-key-settings-merge.ts) is NOT part of the
// shipped engine or the overlay — see its own header doc. It exists purely
// so the settings-merge DECISION that update-and-run.ps1's
// Set-ReportKeyIfProvided function implements in PowerShell has real,
// automated test coverage: there is no PowerShell available on the Mac
// this was built on (branch brief, feat/report-key-delivery), so the exact
// branch logic (key present/absent/different/missing-file/unparseable-
// file/parsed-but-non-object-file) is mirrored here in plain TypeScript
// and tested via the engine's existing vitest setup instead. Keep the two
// in sync if this logic ever changes — see the cross-reference comment in
// update-and-run.ps1.
describe('decideReportKeyMerge', () => {
  it('skips when no -ReportKey was passed, regardless of existing file state', () => {
    expect(
      decideReportKeyMerge({ providedKey: '', fileState: { kind: 'missing' } })
    ).toEqual({ kind: 'skip-no-key-provided' });

    expect(
      decideReportKeyMerge({ providedKey: '', fileState: { kind: 'parsed-object', existingKey: 'TEST-KEY-123' } })
    ).toEqual({ kind: 'skip-no-key-provided' });

    expect(
      decideReportKeyMerge({ providedKey: '', fileState: { kind: 'unreadable-or-unparseable' } })
    ).toEqual({ kind: 'skip-no-key-provided' });

    expect(
      decideReportKeyMerge({ providedKey: '', fileState: { kind: 'parsed-non-object' } })
    ).toEqual({ kind: 'skip-no-key-provided' });
  });

  it('skips on a whitespace-only key too, matching update-and-run.ps1\'s IsNullOrWhiteSpace check', () => {
    expect(
      decideReportKeyMerge({ providedKey: '   ', fileState: { kind: 'parsed-object', existingKey: 'TEST-KEY-123' } })
    ).toEqual({ kind: 'skip-no-key-provided' });
  });

  it('writes when a key is provided and settings.json does not exist yet (fresh install)', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'missing' } })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('writes when a key is provided and the file exists but has no RxVerifyReportKey field yet', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'parsed-object', existingKey: undefined } })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('skips when the provided key already matches what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'parsed-object', existingKey: 'TEST-KEY-123' } })
    ).toEqual({ kind: 'skip-already-matches' });
  });

  it('is case-sensitive when comparing the provided key against what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'parsed-object', existingKey: 'test-key-123' } })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('writes when the provided key differs from what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-456', fileState: { kind: 'parsed-object', existingKey: 'TEST-KEY-123' } })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('backs up and writes when the existing settings.json is corrupt/unreadable/unparseable, even if a key was already intended', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'unreadable-or-unparseable' } })
    ).toEqual({ kind: 'write', corruptBackup: true });
  });

  // REVIEW FIX (non-blocking finding): update-and-run.ps1's
  // Set-ReportKeyIfProvided originally only backed up settings.json in
  // the ConvertFrom-Json PARSE FAILURE branch ('unreadable-or-unparseable'
  // above) — a file that parsed fine but wasn't a JSON object (e.g. the
  // whole file is just `[1,2,3]`, `"5"`, or literal `null`) reset straight
  // to an empty object with NO backup, silently discarding whatever was
  // there. This pins that 'parsed-non-object' now gets the identical
  // corruptBackup:true treatment as an actual parse failure.
  it('backs up and writes the same way when settings.json parses as valid JSON but is not a JSON object (e.g. an array/scalar/null)', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileState: { kind: 'parsed-non-object' } })
    ).toEqual({ kind: 'write', corruptBackup: true });
  });
});
