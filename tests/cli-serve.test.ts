import { describe, it, expect, afterEach } from 'vitest';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const cliPath = path.join(__dirname, '..', 'src', 'cli.ts');
const tsxBin = path.join(__dirname, '..', 'node_modules', '.bin', 'tsx');

/**
 * Minimal harness for --serve mode (src/cli.ts): spawns ONE persistent
 * CLI process, writes one JSON-per-line request, and resolves each
 * caller's promise from the matching response line's "id" — mirrors how
 * overlay/RxVerifyOverlay/Engine/EngineClient.cs correlates
 * request/response pairs over the same persistent process. See
 * src/cli.ts's --serve header doc for the full wire contract this
 * exercises.
 */
class ServeHarness {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<string, (line: Record<string, unknown>) => void>();
  readonly responses: Record<string, unknown>[] = [];
  private closed = false;

  constructor() {
    this.child = spawn(tsxBin, [cliPath, '--serve'], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.on('close', () => {
      this.closed = true;
    });

    const rl = createInterface({ input: this.child.stdout });
    rl.on('line', (line) => {
      if (!line.trim()) return;
      const parsed = JSON.parse(line) as Record<string, unknown>;
      this.responses.push(parsed);
      const id = parsed.id as string | null;
      if (id !== null) {
        const resolver = this.pending.get(id);
        if (resolver) {
          this.pending.delete(id);
          resolver(parsed);
        }
      }
    });
  }

  /** True while the child process is still running (hasn't exited/crashed). */
  get isAlive(): boolean {
    return !this.closed;
  }

  /** Writes a raw line to stdin — used to send deliberately-malformed input. */
  sendRaw(raw: string): void {
    this.child.stdin.write(raw + '\n');
  }

  /** Sends a well-formed `{ id, ...body }` request line and resolves with its matching response. */
  sendRequest(id: string, body: Record<string, unknown>): Promise<Record<string, unknown>> {
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.sendRaw(JSON.stringify({ id, ...body }));
    });
  }

  close(): void {
    this.child.stdin.end();
  }
}

describe('verify-cli --serve (persistent process, line-delimited JSON — see src/cli.ts)', () => {
  let harness: ServeHarness | undefined;

  afterEach(() => {
    harness?.close();
    harness = undefined;
  });

  it('emits a one-time ready/handshake line (engine build stamp) before any response, with no "id" key', async () => {
    harness = new ServeHarness();

    // Round-trip one real request first — guarantees the ready line
    // (written synchronously in serve(), before the readline interface
    // below it starts consuming stdin at all — see src/cli.ts) has
    // already arrived on our side too, since stdout lines are strictly
    // ordered and this test's own readline never reorders them.
    await harness.sendRequest('req-after-ready', {
      source: { patientName: 'John Smith' },
      entered: { patientName: 'John Smith' },
      skipDrugLookup: true
    });

    expect(harness.responses[0]).toMatchObject({ ready: true });
    expect(harness.responses[0].id).toBeUndefined();
    const engineBuild = harness.responses[0].engineBuild as { sha: string; builtAt: string };
    // This harness runs --serve directly against src/cli.ts via tsx, so
    // no dist/build-info.json sibling exists — see readBuildInfo's doc:
    // that's the documented "unknown" fallback, not a bug. A real
    // dist/cli.js (built via `npm run build`, which runs the prebuild
    // step first) reports the actual git sha + build timestamp instead.
    expect(engineBuild).toEqual({ sha: 'unknown', builtAt: 'unknown' });
  }, 15000);

  it('handles multiple requests over one persistent process, correlated by id', async () => {
    harness = new ServeHarness();

    const [first, second] = await Promise.all([
      harness.sendRequest('req-1', {
        source: { patientName: 'John Smith' },
        entered: { patientName: 'John Smith' },
        skipDrugLookup: true
      }),
      harness.sendRequest('req-2', {
        source: { patientName: 'Jane Doe' },
        entered: { patientName: 'Someone Else' },
        skipDrugLookup: true
      })
    ]);

    expect(first.id).toBe('req-1');
    expect(first.error).toBeUndefined();
    const firstVerdicts = first.verdicts as Array<{ field: string; status: string }>;
    expect(firstVerdicts.find((v) => v.field === 'patientName')?.status).toBe('green');

    expect(second.id).toBe('req-2');
    const secondVerdicts = second.verdicts as Array<{ field: string; status: string }>;
    expect(secondVerdicts.find((v) => v.field === 'patientName')?.status).toBe('red');
  }, 15000);

  it('survives a malformed (non-JSON) request line and keeps serving subsequent valid requests', async () => {
    harness = new ServeHarness();

    harness.sendRaw('this is not json at all');

    // The process must still be alive and still answer the NEXT request
    // correctly — one bad line must never take the whole persistent
    // process down (see src/cli.ts serve() doc "NEVER crashes").
    const result = await harness.sendRequest('req-after-malformed', {
      source: { patientName: 'John Smith' },
      entered: { patientName: 'John Smith' },
      skipDrugLookup: true
    });

    expect(harness.isAlive).toBe(true);
    expect(result.id).toBe('req-after-malformed');
    expect(result.error).toBeUndefined();
    const verdicts = result.verdicts as Array<{ field: string; status: string }>;
    expect(verdicts.find((v) => v.field === 'patientName')?.status).toBe('green');

    // The malformed line itself should have produced its own error
    // response (id: null — there was no JSON to recover an id from),
    // not silence.
    const malformedResponse = harness.responses.find((r) => r.id === null);
    expect(malformedResponse).toBeDefined();
    expect(malformedResponse?.error).toBeTypeOf('string');
  }, 15000);

  it('responds with an error (not a crash) for well-formed JSON missing required keys, then keeps serving', async () => {
    harness = new ServeHarness();

    const badShape = await harness.sendRequest('req-bad-shape', { foo: 'bar' });

    expect(harness.isAlive).toBe(true);
    expect(badShape.error).toMatch(/source.*entered/i);

    const followUp = await harness.sendRequest('req-after-bad-shape', {
      source: { patientName: 'John Smith' },
      entered: { patientName: 'John Smith' },
      skipDrugLookup: true
    });
    expect(followUp.error).toBeUndefined();
  }, 15000);

  it('derives source from "ocr" words in --serve mode too (VerifyOCR v1 path), same as one-shot mode', async () => {
    harness = new ServeHarness();

    const ocr = [
      { text: 'Patient:', x: 0, y: 0, w: 80, h: 18 },
      { text: 'Noise,', x: 90, y: 0, w: 80, h: 18 },
      { text: 'Test', x: 180, y: 0, w: 80, h: 18 }
    ];

    const result = await harness.sendRequest('req-ocr', {
      source: { patientName: 'This should be ignored' },
      ocr,
      entered: { patientName: 'Noise, Test' },
      skipDrugLookup: true
    });

    const verdicts = result.verdicts as Array<{ field: string; status: string; sourceValue: string }>;
    const nameVerdict = verdicts.find((v) => v.field === 'patientName');
    expect(nameVerdict?.status).toBe('green');
    expect(nameVerdict?.sourceValue).toBe('Noise, Test');
  }, 15000);
});
