using RxVerifyOverlay.Engine;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for EngineClient.ParseResponseLine (Engine/EngineClient.cs)
/// — the pure part of the persistent --serve process protocol (latency
/// fix): turning one raw JSON response line + the id this call expected
/// into a VerifyResult, or throwing for a transport-level problem that
/// should trigger a process restart. No live node process, no Windows
/// runtime, no WPF — pure JSON-string-in, object-out logic, same "pull
/// the pure logic out for direct testing" pattern as
/// CategoryRollupTests.cs / RxLogFormatterTests.cs.
///
/// NOTE: this deliberately does NOT cover the process-management half of
/// EngineClient (starting/killing node.exe, the stdin/stdout pipes, the
/// restart-on-timeout retry loop, stderr draining) — that needs a real
/// Windows process and a built rx-verify/dist/cli.js --serve to exercise
/// honestly, which isn't available in this sandbox (no dotnet SDK at
/// all, let alone Windows) and wouldn't be a meaningful unit test even
/// where it is. That half needs Will's live-test verification instead —
/// see the branch report.
/// </summary>
public class EngineClientServeProtocolTests
{
    [Fact]
    public void SuccessResponseWithMatchingIdReturnsVerdictsAndSummary()
    {
        const string line = """
            {"id":"1","verdicts":[{"field":"patientName","status":"green","reasonCode":"exact_match","explanation":"Name matches.","sourceValue":"John Smith","enteredValue":"John Smith"}],"summary":{"green":1,"yellow":0,"red":0,"total":1}}
            """;

        var result = EngineClient.ParseResponseLine(line, "1");

        Assert.Null(result.Error);
        var verdict = Assert.Single(result.Verdicts);
        Assert.Equal("patientName", verdict.Field);
        Assert.Equal(1, result.Summary.Green);
    }

    [Fact]
    public void ErrorResponseIsPrefixedAsAnEngineError()
    {
        const string line = """{"id":"2","error":"Input JSON must be an object with (\"source\" or \"ocr\") and \"entered\" keys."}""";

        var result = EngineClient.ParseResponseLine(line, "2");

        Assert.NotNull(result.Error);
        Assert.StartsWith("Engine error: ", result.Error);
        Assert.Contains("entered", result.Error);
    }

    [Fact]
    public void MismatchedIdThrowsIoExceptionSoTheProcessGetsRestarted()
    {
        const string line = """{"id":"99","verdicts":[],"summary":{"green":0,"yellow":0,"red":0,"total":0}}""";

        var ex = Assert.Throws<System.IO.IOException>(() => EngineClient.ParseResponseLine(line, "1"));
        Assert.Contains("id mismatch", ex.Message);
    }

    [Fact]
    public void NullIdThrowsIoException()
    {
        const string line = """{"verdicts":[],"summary":{"green":0,"yellow":0,"red":0,"total":0}}""";

        var ex = Assert.Throws<System.IO.IOException>(() => EngineClient.ParseResponseLine(line, "1"));
        Assert.Contains("id mismatch", ex.Message);
    }

    [Fact]
    public void UnparseableJsonThrowsIoExceptionRatherThanPropagatingTheRawJsonException()
    {
        const string line = "not json at all";

        var ex = Assert.Throws<System.IO.IOException>(() => EngineClient.ParseResponseLine(line, "1"));
        Assert.Contains("Could not parse", ex.Message);
    }

    [Fact]
    public void MissingVerdictsOrSummaryOnSuccessFallsBackToEmptyRatherThanNull()
    {
        // Defensive: a well-formed success envelope should always carry
        // both, but this confirms ParseResponseLine never hands back a
        // null Verdicts/Summary that would NullReferenceException deeper
        // in OverlayViewModel (see BuildRow/PopulateRows, which iterate
        // result.Verdicts unconditionally).
        const string line = """{"id":"1"}""";

        var result = EngineClient.ParseResponseLine(line, "1");

        Assert.Null(result.Error);
        Assert.NotNull(result.Verdicts);
        Assert.Empty(result.Verdicts);
        Assert.NotNull(result.Summary);
    }
}
