import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { verify } from '../src/engine/index.js';
import { FixtureProvider } from '../src/drug/index.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const goldenDir = path.join(__dirname, 'golden');
const provider = new FixtureProvider();

interface GoldenFile {
  description: string;
  source: unknown;
  entered: unknown;
  expected: Array<{ field: string; status: string; reasonCode: string }>;
  expectedSummary: { green: number; yellow: number; red: number; total: number };
}

const files = readdirSync(goldenDir).filter((f) => f.endsWith('.json'));

describe('golden vectors', () => {
  it('found at least 15 golden vector files', () => {
    expect(files.length).toBeGreaterThanOrEqual(15);
  });

  for (const file of files) {
    it(`${file}: matches full ordered verdict list`, () => {
      const raw = readFileSync(path.join(goldenDir, file), 'utf-8');
      const golden = JSON.parse(raw) as GoldenFile;

      const result = verify(golden.source as never, golden.entered as never, provider);
      const actual = result.verdicts.map((v) => ({ field: v.field, status: v.status, reasonCode: v.reasonCode }));

      expect(actual).toEqual(golden.expected);
      expect(result.summary).toEqual(golden.expectedSummary);
    });
  }
});
