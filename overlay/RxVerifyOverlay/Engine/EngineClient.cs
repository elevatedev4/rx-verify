using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// local subprocess: `node dist/cli.js`, JSON in on stdin, JSON out on
/// stdout. See rx-verify/src/cli.ts for the Node-side half of this
/// contract.
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
/// pure risk, no reward, for a v0. A subprocess call costs a few
/// milliseconds of process-start overhead per verification pass (this
/// runs once per script review, not in a hot loop) in exchange for
/// reusing the tested engine completely unchanged. Revisit only if
/// Node's process-start latency turns out to matter in practice, or if
/// distributing Node alongside the overlay becomes an installer
/// headache (see README "Deferred" — a bundled/pkg'd single .exe is the
/// natural next step, not a rewrite).
///
/// LOCAL-ONLY: this spawns a LOCAL child process and talks to it over
/// stdin/stdout pipes only. No sockets, no network calls, nothing
/// transmitted off the workstation — see README "Local-only, by
/// construction" for the full audit trail of this claim.
/// </summary>
public sealed class EngineClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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
        var request = new VerifyCliRequest { Source = source, Entered = entered, SkipDrugLookup = skipDrugLookup };
        return RunCliAsync(request, cancellationToken);
    }

    /// <summary>
    /// VerifyOCR v1: same contract as the PrescriptionRecord overload
    /// above, but for the OCR source path — sends the RAW OCR words
    /// straight to verify-cli ({ ocr, entered, skipDrugLookup }, see
    /// src/cli.ts) instead of a pre-parsed source record. Label/value
    /// association now happens entirely inside the tested TS engine
    /// (src/ocr/parseEscriptOcr.ts) — see Uia/OcrFieldReader.cs, which no
    /// longer parses OCR output itself. Same two-phase (skipDrugLookup
    /// true then false) call pattern as OverlayViewModel.RefreshAsync
    /// already used for the PrescriptionRecord path.
    /// </summary>
    public Task<VerifyResult> VerifyAsync(IReadOnlyList<OcrWord> ocr, PrescriptionRecord entered, bool skipDrugLookup = false, CancellationToken cancellationToken = default)
    {
        var request = new VerifyOcrCliRequest { Ocr = new List<OcrWord>(ocr), Entered = entered, SkipDrugLookup = skipDrugLookup };
        return RunCliAsync(request, cancellationToken);
    }

    /// <summary>
    /// The actual `node dist/cli.js` subprocess call, shared by both
    /// VerifyAsync overloads above — everything past "serialize this
    /// request object to stdin" is identical regardless of whether the
    /// request is a VerifyCliRequest (source) or VerifyOcrCliRequest
    /// (ocr), so this is generic over the request type rather than
    /// duplicated per overload.
    /// </summary>
    private async Task<VerifyResult> RunCliAsync<TRequest>(TRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(CliScriptPath))
        {
            return new VerifyResult
            {
                Error = $"Engine CLI not found at '{CliScriptPath}'. Build rx-verify first: cd rx-verify && npm install && npm run build. " +
                        "Then point EngineClient at the resulting dist/cli.js (see README 'Configuration')."
            };
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        var psi = new ProcessStartInfo
        {
            FileName = NodeExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(CliScriptPath);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new VerifyResult
            {
                Error = $"Failed to start node ('{NodeExecutable}'). Is Node.js installed and on PATH? " +
                        $"See README 'Prerequisites'. Underlying error: {ex.Message}"
            };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.WriteAsync(requestJson);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new VerifyResult
            {
                Error = $"verify-cli produced no output (exit code {process.ExitCode}). stderr: {stderr}"
            };
        }

        try
        {
            var result = JsonSerializer.Deserialize<VerifyResult>(stdout, JsonOptions);
            if (result is null)
            {
                return new VerifyResult { Error = "verify-cli returned null/unparseable JSON." };
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                // cli.ts's own error path — see src/cli.ts main().catch().
                return new VerifyResult { Error = $"Engine error: {result.Error}" };
            }

            return result;
        }
        catch (JsonException ex)
        {
            return new VerifyResult
            {
                Error = $"Could not parse verify-cli output as JSON: {ex.Message}. Raw stdout (first 500 chars): " +
                        stdout[..Math.Min(500, stdout.Length)]
            };
        }
    }
}
