import { describe, it, expect } from 'vitest';
import { decideReportKeyMerge } from '../scripts/report-key-settings-merge.js';

// This module (scripts/report-key-settings-merge.ts) is NOT part of the
// shipped engine or the overlay — see its own header doc. It exists purely
// so the settings-merge DECISION that update-and-run.ps1's
// Set-ReportKeyIfProvided function implements in PowerShell has real,
// automated test coverage: there is no PowerShell available on the Mac
// this was built on (branch brief, feat/report-key-delivery), so the exact
// branch logic (key present/absent/different/corrupt-file) is mirrored
// here in plain TypeScript and tested via the engine's existing vitest
// setup instead. Keep the two in sync if this logic ever changes — see the
// cross-reference comment in update-and-run.ps1.
describe('decideReportKeyMerge', () => {
  it('skips when no -ReportKey was passed, regardless of existing state', () => {
    expect(
      decideReportKeyMerge({ providedKey: '', fileExists: false, fileParsedOk: true, existingKey: undefined })
    ).toEqual({ kind: 'skip-no-key-provided' });

    expect(
      decideReportKeyMerge({ providedKey: '', fileExists: true, fileParsedOk: true, existingKey: 'TEST-KEY-123' })
    ).toEqual({ kind: 'skip-no-key-provided' });

    expect(
      decideReportKeyMerge({ providedKey: '', fileExists: true, fileParsedOk: false, existingKey: undefined })
    ).toEqual({ kind: 'skip-no-key-provided' });
  });

  it('skips on a whitespace-only key too, matching update-and-run.ps1\'s IsNullOrWhiteSpace check', () => {
    expect(
      decideReportKeyMerge({ providedKey: '   ', fileExists: true, fileParsedOk: true, existingKey: 'TEST-KEY-123' })
    ).toEqual({ kind: 'skip-no-key-provided' });
  });

  it('writes when a key is provided and settings.json does not exist yet (fresh install)', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileExists: false, fileParsedOk: true, existingKey: undefined })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('writes when a key is provided and the file exists but has no RxVerifyReportKey field yet', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileExists: true, fileParsedOk: true, existingKey: undefined })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('skips when the provided key already matches what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileExists: true, fileParsedOk: true, existingKey: 'TEST-KEY-123' })
    ).toEqual({ kind: 'skip-already-matches' });
  });

  it('is case-sensitive when comparing the provided key against what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileExists: true, fileParsedOk: true, existingKey: 'test-key-123' })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('writes when the provided key differs from what is stored', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-456', fileExists: true, fileParsedOk: true, existingKey: 'TEST-KEY-123' })
    ).toEqual({ kind: 'write', corruptBackup: false });
  });

  it('backs up and writes when the existing settings.json is corrupt/unparseable, even if a key was already intended', () => {
    expect(
      decideReportKeyMerge({ providedKey: 'TEST-KEY-123', fileExists: true, fileParsedOk: false, existingKey: undefined })
    ).toEqual({ kind: 'write', corruptBackup: true });
  });
});
