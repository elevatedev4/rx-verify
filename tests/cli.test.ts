import { describe, it, expect } from 'vitest';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const cliPath = path.join(__dirname, '..', 'src', 'cli.ts');
const tsxBin = path.join(__dirname, '..', 'node_modules', '.bin', 'tsx');

function runCli(input: string): Promise<{ stdout: string; stderr: string; code: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(tsxBin, [cliPath], { stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d) => (stdout += d.toString()));
    child.stderr.on('data', (d) => (stderr += d.toString()));
    child.on('error', reject);
    child.on('close', (code) => resolve({ stdout, stderr, code }));
    child.stdin.write(input);
    child.stdin.end();
  });
}

describe('verify-cli (stdin/stdout JSON wrapper, subprocess smoke test)', () => {
  it('returns an all-green VerifyResult for identical source/entered', async () => {
    const input = JSON.stringify({
      source: {
        patientName: 'John Smith',
        patientDOB: '07/02/1980',
        dateWritten: '07/01/2026',
        drug: { ndc: '00071015523' },
        sig: 'take 1 tab po bid',
        quantity: 60,
        quantityUnit: 'tab',
        refills: 2
      },
      entered: {
        patientName: 'John Smith',
        patientDOB: '07/02/1980',
        dateWritten: '07/01/2026',
        drug: { ndc: '00071015523' },
        sig: 'take 1 tab po bid',
        quantity: 60,
        quantityUnit: 'tab',
        refills: 2
      }
    });

    const { stdout, code } = await runCli(input);
    const result = JSON.parse(stdout);

    expect(code).toBe(0);
    expect(result.summary.red).toBe(0);
    expect(result.verdicts).toHaveLength(12);
    expect(result.verdicts[0].field).toBe('patientName');
    expect(result.verdicts[0].status).toBe('green');
  }, 15000);

  it('reports an error object + non-zero exit on invalid JSON', async () => {
    const { stdout, code } = await runCli('not json');
    const result = JSON.parse(stdout);

    expect(code).toBe(1);
    expect(result.error).toBeTypeOf('string');
  }, 15000);

  it('reports an error object + non-zero exit on missing keys', async () => {
    const { stdout, code } = await runCli(JSON.stringify({ foo: 'bar' }));
    const result = JSON.parse(stdout);

    expect(code).toBe(1);
    expect(result.error).toMatch(/source.*entered/i);
  }, 15000);
});
