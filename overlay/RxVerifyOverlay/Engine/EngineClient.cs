using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Engine;

/// <summary>
/// Calls the EXISTING, heavily-tested rx-verify TypeScript engine as a
/// PERSISTENT local subprocess: `node dist/cli.js --serve`, one
/// long-lived process handling many requests as line-delimited JSON over
/// stdin/stdout (one request line in, one response line out, correlated
/// by an "id" each side echoes back) — see rx-verify/src/cli.ts's
/// --serve header doc for the full wire contract.
///
/// LATENCY FIX (field report: verdicts noticeably slower than the OCR
/// pipeline suggested): this used to spawn a BRAND NEW node.exe for
/// every single verify call (~100-300ms of pure process-start overhead,
/// paid twice per refresh — once for phase 1, once for phase 2's drug
/// lookup). One process is now started lazily on first use and reused
/// for the life of the app, cutting that overhead to (effectively) zero
/// after the first call. See RunOnPersistentProcessAsync for the
/// restart-on-failure/timeout logic that keeps this safe if the process
/// ever dies or hangs.
///
/// WHY A SUBPROCESS INSTEAD OF PORTING THE ENGINE TO C#: the engine's
/// value is in its rules (name/nickname/date/address normalization, sig
/// abbreviation expansion, NDC/RxNorm comparison, quantity/days-supply
/// reconciliation, and — as of VerifyOCR v1 — OCR label/value
/// association, see src/ocr/parseEscriptOcr.ts) which already have 200+
/// passing vitest tests and real production nuance (see
/// rx-verify/README.md "Status / what's
/// stubbed"). Porting that logic to C# would mean re-deriving and
/// re-testing all of it in a second language for zero behavior change —
/// pure risk, no reward, for a v0.
///
/// LOCAL-ONLY: this spawns a LOCAL child process and talks to it over
/// stdin/stdout pipes only. No sockets, no network calls, nothing
/// transmitted off the workstation — see README "Local-only, by
/// construction" for the full audit trail of this claim.
/// </summary>
public sealed class EngineClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Per-request ceiling for the persistent process, purely to DETECT
    /// a stuck/hung process and trigger a restart — this class had no
    /// timeout of its own before (each call spawned a fresh node.exe, so
    /// a hang could only ever block that one call for as long as the
    /// caller's own CancellationToken allowed — in practice every real
    /// call site passes `default`, i.e. no bound at all; see
    /// ViewModels/OverlayViewModel.cs). With ONE process now shared
    /// across every refresh, an unbounded hang would freeze every FUTURE
    /// verify too, not just the one in flight, so a finite ceiling is
    /// needed. 10s is far above every observed engine latency (100-300ms
    /// warm, plus a one-time LocalNdcProvider dataset load on the
    /// process's first real drug lookup — see rx-verify src/drug/index.ts)
    /// with generous headroom for a slow workstation; it is not meant to
    /// be a tight UX-facing SLA.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Serializes access to the persistent process: one in-flight request at a time is fine (phase 1 and phase 2 of a refresh simply queue) — see RunAsync.</summary>
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private readonly object _stderrLock = new();
    private string _lastStderrLine = "";

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private long _nextRequestId;
    private bool _disposed;

    /// <summary>
    /// Engine build stamp captured from --serve's one-time ready
    /// handshake (src/cli.ts — see that file's readBuildInfo doc for
    /// why this exists: RXVERIFY-TROUBLESHOOT 2026-08-13). Null until
    /// the persistent process has started at least once, or if the
    /// handshake line was missing/malformed/unparseable (best-effort
    /// only — see EnsureProcessStarted). Read by
    /// OverlayViewModel.BuildCurrentLogBlob for RxLogFormatter's
    /// "Engine build: &lt;sha&gt; &lt;builtAt&gt;" header line.
    /// </summary>
    public string? EngineBuildSha { get; private set; }

    /// <summary>See EngineBuildSha.</summary>
    public string? EngineBuildBuiltAt { get; private set; }

    /// <summary>
    /// Re-armed to true every time EnsureProcessStarted starts a fresh
    /// process; flips to false the first time the response-reading loop
    /// in RunOnPersistentProcessAsync inspects a non-blank line, whether
    /// or not that line turned out to be the ready handshake — see
    /// TryParseHandshakeLine and BLOCKER 2's doc in EnsureProcessStarted.
    /// This is what makes the handshake check happen AT MOST ONCE per
    /// process lifetime, never on every request.
    /// </summary>
    private bool _awaitingHandshake;

    /// <summary>
    /// Path to the compiled CLI entrypoint, e.g.
    /// "C:\Users\will\claude\rx-verify\dist\cli.js". Configurable because
    /// the overlay and the engine repo are checked out independently —
    /// see README "Configuration" for how Will points this at his real
    /// checkout.
    /// </summary>
    public string CliScriptPath { get; }

    /// <summary>Path to node.exe, or just "node" if it's on PATH.</summary>
    public string NodeExecutable { get; }

    public EngineClient(string cliScriptPath, string nodeExecutable = "node")
    {
        CliScriptPath = cliScriptPath;
        NodeExecutable = nodeExecutable;
    }

    /// <param name="skipDrugLookup">
    /// See VerifyCliRequest.SkipDrugLookup. Pass true for the fast,
    /// immediate-render pass over every field except drug; pass false
    /// (the default) for the real drug verdict — see
    /// ViewModels/OverlayViewModel.cs RefreshAsync for how the two calls
    /// are sequenced so the UI never blocks on the drug lookup.
    /// </param>
    public Task<VerifyResult> VerifyAsync(PrescriptionRecord source, PrescriptionRecord entered, bool skipDrugLookup = false, CancellationToken cancellationToken = default)
    {
        return RunAsync(id => new VerifyCliRequest { Id = id, Source = source, Entered = entered, SkipDrugLookup = skipDrugLookup }, cancellationToken);
    }

    /// <summary>
    /// VerifyOCR v1: same contract as the PrescriptionRecord overload
    /// above, but for the OCR source path — sends the RAW OCR words
    /// straight to verify-cli ({ id, ocr, entered, skipDrugLookup }, see
    /// rx-verify src/cli.ts) instead of a pre-parsed source record.
    /// Label/value association now happens entirely inside the tested TS
    /// engine (src/ocr/parseEscriptOcr.ts) — see Uia/OcrFieldReader.cs,
    /// which no longer parses OCR output itself. Same two-phase
    /// (skipDrugLookup true then false) call pattern as
    /// OverlayViewModel.RefreshAsync already used for the
    /// PrescriptionRecord path.
    /// </summary>
    public Task<VerifyResult> VerifyAsync(IReadOnlyList<OcrWord> ocr, PrescriptionRecord entered, bool skipDrugLookup = false, CancellationToken cancellationToken = default)
    {
        var ocrCopy = new List<OcrWord>(ocr);
        return RunAsync(id => new VerifyOcrCliRequest { Id = id, Ocr = ocrCopy, Entered = entered, SkipDrugLookup = skipDrugLookup }, cancellationToken);
    }

    /// <summary>
    /// Entry point shared by both VerifyAsync overloads: validates the
    /// CLI is where it's supposed to be, then serializes access to the
    /// one persistent process via _requestLock — see class doc.
    /// <paramref name="buildRequest"/> is called with the id this call
    /// should use, so the id assignment lives in ONE place
    /// (RunOnPersistentProcessAsync) regardless of which overload/request
    /// shape is being sent.
    /// </summary>
    private async Task<VerifyResult> RunAsync(Func<string, object> buildRequest, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EngineClient));

        if (!File.Exists(CliScriptPath))
        {
            return new VerifyResult
            {
                Error = $"Engine CLI not found at '{CliScriptPath}'. Build rx-verify first: cd rx-verify && npm install && npm run build. " +
                        "Then point EngineClient at the resulting dist/cli.js (see README 'Configuration')."
            };
        }

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOnPersistentProcessAsync(buildRequest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Sends one request to the persistent --serve process and awaits its
    /// matching response line. Only ever called with _requestLock held
    /// (RunAsync), so there is never more than one outstanding
    /// request/response round-trip at a time — the process itself only
    /// promises to handle one line at a time anyway (see src/cli.ts
    /// serve()'s synchronous per-line handling).
    ///
    /// RESTART ON FAILURE: if the process is dead (never started, or
    /// exited since the last call), a write/read fails (broken pipe,
    /// stdout EOF), the response is corrupt/desynchronized, or the
    /// request times out (RequestTimeout), the process is killed and ONE
    /// retry is made against a freshly-started replacement — a single
    /// bad round-trip should self-heal rather than permanently wedge
    /// verification for the rest of the shift. A caller-driven
    /// cancellation (the CancellationToken passed in from outside, as
    /// opposed to this method's OWN RequestTimeout firing) is NOT
    /// retried — it's propagated immediately, matching the old spawn-
    /// per-call behavior where a cancelled WaitForExitAsync simply threw.
    /// </summary>
    private async Task<VerifyResult> RunOnPersistentProcessAsync(Func<string, object> buildRequest, CancellationToken callerToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                EnsureProcessStarted();
            }
            catch (Exception ex)
            {
                return new VerifyResult
                {
                    Error = $"Failed to start node ('{NodeExecutable}' \"{CliScriptPath}\" --serve). Is Node.js installed and on PATH? " +
                            $"See README 'Prerequisites'. Underlying error: {ex.Message}"
                };
            }

            var id = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
            var requestJson = JsonSerializer.Serialize(buildRequest(id), JsonOptions);

            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutCts.Token);

            try
            {
                var stdin = _stdin ?? throw new IOException("Persistent engine process has no stdin (not started).");
                var stdout = _stdout ?? throw new IOException("Persistent engine process has no stdout (not started).");

                await stdin.WriteLineAsync(requestJson.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                await stdin.FlushAsync(linkedCts.Token).ConfigureAwait(false);

                string? line;
                do
                {
                    line = await stdout.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);

                    // Reviewer round 2, BLOCKER 2 (deadlock fix): the
                    // one-time ready handshake (src/cli.ts serve()'s
                    // header doc) is checked for LAZILY here, on the
                    // first non-blank line of a process's life, instead
                    // of via a dedicated blocking read in
                    // EnsureProcessStarted (which hung forever against an
                    // old dist that never sends one at all). Correct for
                    // both: an old dist's first line already IS the real
                    // response, so TryParseHandshakeLine returns false
                    // and `line` is used as-is below; a new dist's first
                    // line IS the handshake, so it's consumed here and
                    // the NEXT line (the actual response) is read in its
                    // place — still inside this same timeout-guarded
                    // request/response round-trip.
                    if (line is not null && line.Trim().Length > 0 && _awaitingHandshake)
                    {
                        _awaitingHandshake = false; // at most once per process lifetime
                        var handshake = TryParseHandshakeLine(line);
                        if (handshake is not null)
                        {
                            EngineBuildSha = handshake.Value.Sha;
                            EngineBuildBuiltAt = handshake.Value.BuiltAt;
                            line = await stdout.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                        }
                    }
                }
                while (line is not null && line.Trim().Length == 0); // skip stray blank lines, same tolerance as src/cli.ts serve()

                if (line is null)
                {
                    throw new IOException("Persistent engine process's stdout closed unexpectedly (the process likely exited).");
                }

                return ParseResponseLine(line, id);
            }
            catch (Exception ex)
            {
                lastError = ex;
                var timedOut = timeoutCts.IsCancellationRequested;

                KillProcess();

                if (callerToken.IsCancellationRequested && !timedOut)
                {
                    // Genuine caller cancellation (not our own internal
                    // timeout) — propagate immediately rather than
                    // retrying or masking it as an engine error.
                    throw new OperationCanceledException("Verify request was cancelled.", ex, callerToken);
                }

                if (attempt == 0) continue; // one retry against a freshly-restarted process
            }
        }

        var stderrSuffix = GetLastStderrSuffix();
        return new VerifyResult
        {
            Error = $"Persistent engine process failed and the automatic restart+retry also failed. Last error: {lastError?.Message}{stderrSuffix}"
        };
    }

    /// <summary>Starts the persistent --serve process if one isn't already running. No-op if _process is alive.</summary>
    private void EnsureProcessStarted()
    {
        if (_process is { HasExited: false }) return;

        KillProcess(); // clean up a dead process's handles/streams, if any, before replacing it

        var psi = new ProcessStartInfo
        {
            FileName = NodeExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        psi.ArgumentList.Add(CliScriptPath);
        psi.ArgumentList.Add("--serve");

        var process = new Process { StartInfo = psi };
        process.Start();

        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;

        // Reviewer round 2, BLOCKER 2 (deadlock fix): there used to be a
        // dedicated, synchronous, UNBOUNDED _stdout.ReadLine() right here
        // to consume --serve's one-time ready handshake (RXVERIFY-
        // TROUBLESHOOT 2026-08-13 — src/cli.ts serve()'s header doc). That
        // hangs FOREVER against an OLD dist/cli.js built before the
        // handshake existed: old serve() writes NOTHING to stdout until
        // it actually receives a request line, so this read would block
        // indefinitely waiting for a line that will never come — and
        // machines running a stale, not-yet-rebuilt dist/ are exactly the
        // population this feature exists to help. There is now NO
        // dedicated startup read at all. Instead, re-arm the one-shot
        // handshake check for the response-reading loop in
        // RunOnPersistentProcessAsync (see TryParseHandshakeLine) — it
        // lazily inspects the FIRST line of the first request's normal,
        // already-timeout-guarded read, which is correct either way: an
        // old dist's first line IS the real response (no ready line ever
        // sent), and a new dist's ready line precedes it. Also clear any
        // stale stamp from a previous process — a freshly (re)started
        // process may be running different dist/ content than whatever
        // just died.
        _awaitingHandshake = true;
        EngineBuildSha = null;
        EngineBuildBuiltAt = null;

        // Drain stderr continuously for the life of the process. This is
        // NOT optional: an unread stderr pipe can fill its OS buffer and
        // deadlock the child once it blocks trying to write to it — the
        // old per-call code avoided this by awaiting
        // StandardError.ReadToEndAsync() alongside stdout for each call
        // (see git history); a persistent process never reaches "done",
        // so that one-shot capture doesn't apply here — this is the
        // equivalent ongoing pump instead. Fire-and-forget by design:
        // it runs for exactly as long as the process's stderr stream is
        // open and needs no caller to await it.
        _ = DrainStderrAsync(process);
    }

    /// <summary>Reads and discards (keeping only the most recent line, for error messages) the process's stderr for as long as it's open — see EnsureProcessStarted's doc for why this must run continuously.</summary>
    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Trim().Length == 0) continue;
                lock (_stderrLock) { _lastStderrLine = line; }
            }
        }
        catch
        {
            // Best-effort only — losing stderr detail must never take
            // down anything that actually matters (the request/response
            // path itself doesn't depend on this).
        }
    }

    private string GetLastStderrSuffix()
    {
        lock (_stderrLock)
        {
            return string.IsNullOrWhiteSpace(_lastStderrLine) ? "" : $" stderr: {_lastStderrLine}";
        }
    }

    /// <summary>Force-kills the current process (if any) and clears its handles/streams. Safe to call when there is no process, or when it has already exited on its own.</summary>
    private void KillProcess()
    {
        var process = _process;
        _process = null;
        _stdin = null;
        _stdout = null;

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort — if it already exited out from under us, or
            // Kill races with a natural exit, there's nothing more to do.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Wire shape of one --serve response line: { id, verdicts, summary }
    /// on success, or { id, error } on failure — mirrors rx-verify
    /// src/cli.ts's ServeRequest/response doc exactly, plus the same
    /// error-prefixing RunCliAsync always applied (kept here as
    /// "Engine error: " below) so a Node-side failure still reads
    /// distinctly from a transport-level one (mismatched id, dead
    /// process, etc.).
    /// </summary>
    private sealed class ServeResponseEnvelope
    {
        public string? Id { get; set; }
        public List<FieldVerdict>? Verdicts { get; set; }
        public VerifySummary? Summary { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Engine build stamp extracted from a --serve ready handshake line
    /// — see TryParseHandshakeLine. Either field may be null if the
    /// handshake's own "engineBuild" object was missing that key (should
    /// never happen against src/cli.ts's own readBuildInfo, which always
    /// supplies both as at least "unknown", but this stays defensive
    /// rather than assuming a well-formed shape from another process).
    /// </summary>
    public readonly record struct EngineBuildStamp(string? Sha, string? BuiltAt);

    /// <summary>
    /// Reviewer round 2, BLOCKER 2 (deadlock fix — see the doc in
    /// EnsureProcessStarted for the full story): pure parse of whether
    /// <paramref name="line"/> is --serve's one-time ready handshake
    /// (src/cli.ts serve()'s header doc) rather than a real response
    /// line. Returns null for anything that doesn't match — including a
    /// genuine response line, which never has a top-level "ready": true
    /// key at all (every response either echoes back the required "id"
    /// key, or is a parse/shape error — see ParseResponseLine/
    /// ServeResponseEnvelope), and any JSON-parse failure — so a real
    /// verdict line can never be misread as a handshake, and this can
    /// never throw. Public and static, same "pure logic pulled out for
    /// direct testing" pattern as ParseResponseLine — see
    /// RxVerifyOverlay.Tests/EngineClientServeProtocolTests.cs. Called
    /// from the response-reading loop in RunOnPersistentProcessAsync,
    /// gated by _awaitingHandshake so it only ever runs on the first
    /// non-blank line of a process's life.
    /// </summary>
    public static EngineBuildStamp? TryParseHandshakeLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("ready", out var readyEl) || readyEl.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            string? sha = null;
            string? builtAt = null;
            if (doc.RootElement.TryGetProperty("engineBuild", out var buildEl))
            {
                if (buildEl.TryGetProperty("sha", out var shaEl)) sha = shaEl.GetString();
                if (buildEl.TryGetProperty("builtAt", out var builtAtEl)) builtAt = builtAtEl.GetString();
            }

            return new EngineBuildStamp(sha, builtAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pure parse of one --serve response line into a VerifyResult (or a
    /// thrown IOException for a transport-level problem — dead process
    /// treatment, see RunOnPersistentProcessAsync's catch). Public
    /// (rather than private, like the rest of this transport-plumbing
    /// class) specifically so it's directly unit-testable without a live
    /// node process or a Windows runtime — see
    /// RxVerifyOverlay.Tests/EngineClientServeProtocolTests.cs, same
    /// "pure logic pulled out for direct testing" pattern as
    /// ViewModels/OverlayViewModel.cs's CategoryRollup.
    /// </summary>
    public static VerifyResult ParseResponseLine(string line, string expectedId)
    {
        ServeResponseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ServeResponseEnvelope>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Thrown out to RunOnPersistentProcessAsync's catch, which
            // treats it the same as a dead process: kill + retry once.
            throw new IOException($"Could not parse verify-cli --serve response as JSON: {ex.Message}. Raw line (first 500 chars): " +
                                   line[..Math.Min(500, line.Length)]);
        }

        if (envelope is null)
        {
            throw new IOException("verify-cli --serve returned a null/unparseable response line.");
        }

        if (envelope.Id != expectedId)
        {
            // Protocol desync (e.g. a leftover response from a previous,
            // supposedly-abandoned request) — safer to restart the
            // process than trust anything else it says after this.
            throw new IOException($"verify-cli --serve response id mismatch: expected '{expectedId}', got '{envelope.Id ?? "(null)"}'.");
        }

        if (!string.IsNullOrEmpty(envelope.Error))
        {
            // cli.ts's own error path — see src/cli.ts serve()'s per-line error responses.
            return new VerifyResult { Error = $"Engine error: {envelope.Error}" };
        }

        return new VerifyResult
        {
            Verdicts = envelope.Verdicts ?? new List<FieldVerdict>(),
            Summary = envelope.Summary ?? new VerifySummary()
        };
    }

    /// <summary>
    /// Stops the persistent process, if one is running. Callers that
    /// replace an EngineClient instance (e.g. MainWindow.xaml.cs
    /// OnSaveSettingsClick, after the CLI path/node executable changes)
    /// MUST dispose the old one, or its node.exe would otherwise keep
    /// running as an orphaned process for the rest of the session.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillProcess();
        _requestLock.Dispose();
    }
}
