using System.Collections.Generic;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RetryingFieldRead.Read (Uia/RetryingFieldRead.cs) —
/// the retry-on-suspicion orchestration behind FieldReader.ReadWithRetry/
/// ReadBoolWithRetry, pulled out into its own generic, delegate-driven
/// algorithm specifically so it has real test coverage (post-review fix:
/// previously verified only by manual code trace, the highest-stakes new
/// logic in the uia-read-latency branch's element cache — branch brief
/// item 4: "NEVER serve stale entered-field VALUES"). Uses a plain dummy
/// element type and fake delegates — no FlaUI/UIA/Windows dependency.
/// </summary>
public class RetryingFieldReadTests
{
    private sealed class DummyElement { }

    private static readonly DummyElement Element = new();

    /// <summary>
    /// Drives RetryingFieldRead.Read with a scripted sequence of resolve
    /// results and read attempts, and counts how many times each
    /// delegate actually ran — the thing manual tracing can't pin down
    /// as reliably as an assertion can.
    /// </summary>
    private sealed class Script
    {
        private readonly Queue<DummyElement?> _resolves;
        private readonly Queue<RetryingFieldRead.Attempt<string?>> _reads;

        public int ResolveCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int SuspiciousCalls { get; private set; }

        public Script(IEnumerable<DummyElement?> resolves, IEnumerable<RetryingFieldRead.Attempt<string?>> reads)
        {
            _resolves = new Queue<DummyElement?>(resolves);
            _reads = new Queue<RetryingFieldRead.Attempt<string?>>(reads);
        }

        public DummyElement? Resolve()
        {
            ResolveCalls++;
            return _resolves.Count > 0 ? _resolves.Dequeue() : Element;
        }

        public RetryingFieldRead.Attempt<string?> ReadValue(DummyElement element)
        {
            ReadCalls++;
            return _reads.Count > 0
                ? _reads.Dequeue()
                : new RetryingFieldRead.Attempt<string?>(null, Threw: false);
        }

        public void OnSuspicious() => SuspiciousCalls++;

        public string? Run(bool hasEverReadNonBlank)
        {
            return RetryingFieldRead.Read<DummyElement, string?>(
                Resolve,
                ReadValue,
                hasEverReadNonBlank,
                isBlank: string.IsNullOrWhiteSpace,
                OnSuspicious);
        }
    }

    [Fact]
    public void FirstAttemptThrows_RetriesExactlyOnceAndReturnsSecondAttemptsValue()
    {
        var script = new Script(
            resolves: new DummyElement?[] { Element, Element },
            reads: new[]
            {
                new RetryingFieldRead.Attempt<string?>(null, Threw: true),
                new RetryingFieldRead.Attempt<string?>("Amoxicillin", Threw: false)
            });

        var result = script.Run(hasEverReadNonBlank: false);

        Assert.Equal("Amoxicillin", result);
        Assert.Equal(2, script.ResolveCalls);
        Assert.Equal(2, script.ReadCalls);
        Assert.Equal(1, script.SuspiciousCalls);
    }

    [Fact]
    public void FirstAttemptThrowsAgainOnRetry_ReturnsNullAfterExactlyOneRetryNoMore()
    {
        var script = new Script(
            resolves: new DummyElement?[] { Element, Element },
            reads: new[]
            {
                new RetryingFieldRead.Attempt<string?>(null, Threw: true),
                new RetryingFieldRead.Attempt<string?>(null, Threw: true)
            });

        var result = script.Run(hasEverReadNonBlank: false);

        Assert.Null(result);
        Assert.Equal(2, script.ResolveCalls);
        Assert.Equal(2, script.ReadCalls);
        Assert.Equal(1, script.SuspiciousCalls);
    }

    [Fact]
    public void NewlyBlank_AfterPriorNonBlank_RetriesOnceAndTheBlankIsAccepted()
    {
        // "Newly blank" = hasEverReadNonBlank is true (this field read
        // real data before, for this window) but THIS read came back
        // blank. Must retry once — but if the field is genuinely blank
        // now (e.g. the pharmacist really did clear it), that blank must
        // be ACCEPTED after the retry, never papered over with something
        // else or silently discarded.
        var script = new Script(
            resolves: new DummyElement?[] { Element, Element },
            reads: new[]
            {
                new RetryingFieldRead.Attempt<string?>("", Threw: false),
                new RetryingFieldRead.Attempt<string?>("", Threw: false)
            });

        var result = script.Run(hasEverReadNonBlank: true);

        Assert.Equal("", result);
        Assert.Equal(2, script.ResolveCalls);
        Assert.Equal(2, script.ReadCalls);
        Assert.Equal(1, script.SuspiciousCalls);
    }

    [Fact]
    public void NewlyBlank_RetryComesBackWithARealValue_ReturnsTheRealValue()
    {
        // The more common "suspicious" case in practice: the cached
        // element WAS stale, and re-resolving finds the real, current
        // value.
        var script = new Script(
            resolves: new DummyElement?[] { Element, Element },
            reads: new[]
            {
                new RetryingFieldRead.Attempt<string?>(null, Threw: false),
                new RetryingFieldRead.Attempt<string?>("Testperson, Jamie", Threw: false)
            });

        var result = script.Run(hasEverReadNonBlank: true);

        Assert.Equal("Testperson, Jamie", result);
        Assert.Equal(1, script.SuspiciousCalls);
    }

    [Fact]
    public void FirstTimeBlank_HasEverReadNonBlankFalse_TrustedImmediatelyNoRetry()
    {
        // A genuinely empty field the FIRST time it's ever read for this
        // window is normal, not suspicious — must not cost a retry.
        var script = new Script(
            resolves: new DummyElement?[] { Element },
            reads: new[] { new RetryingFieldRead.Attempt<string?>(null, Threw: false) });

        var result = script.Run(hasEverReadNonBlank: false);

        Assert.Null(result);
        Assert.Equal(1, script.ResolveCalls);
        Assert.Equal(1, script.ReadCalls);
        Assert.Equal(0, script.SuspiciousCalls);
    }

    [Fact]
    public void NonBlankFirstAttempt_NeverRetriesRegardlessOfHasEverReadNonBlank()
    {
        var script = new Script(
            resolves: new DummyElement?[] { Element },
            reads: new[] { new RetryingFieldRead.Attempt<string?>("12345", Threw: false) });

        var result = script.Run(hasEverReadNonBlank: true);

        Assert.Equal("12345", result);
        Assert.Equal(1, script.ReadCalls);
        Assert.Equal(0, script.SuspiciousCalls);
    }

    [Fact]
    public void ElementNotFoundOnFirstResolve_ReturnsDefaultWithoutCallingReadValue()
    {
        var script = new Script(
            resolves: new DummyElement?[] { null },
            reads: new RetryingFieldRead.Attempt<string?>[0]);

        var result = script.Run(hasEverReadNonBlank: false);

        Assert.Null(result);
        Assert.Equal(1, script.ResolveCalls);
        Assert.Equal(0, script.ReadCalls);
        Assert.Equal(0, script.SuspiciousCalls);
    }

    [Fact]
    public void ElementNotFoundOnRetryResolve_ReturnsDefaultAfterExactlyOneReadCall()
    {
        var script = new Script(
            resolves: new DummyElement?[] { Element, null },
            reads: new[] { new RetryingFieldRead.Attempt<string?>(null, Threw: true) });

        var result = script.Run(hasEverReadNonBlank: false);

        Assert.Null(result);
        Assert.Equal(2, script.ResolveCalls);
        Assert.Equal(1, script.ReadCalls); // the retry never got to call readValue — resolve failed first
        Assert.Equal(1, script.SuspiciousCalls);
    }

    [Fact]
    public void BoolValueType_ThrowRetriesOnceAndReturnsSecondAttempt()
    {
        // Same algorithm, TValue = bool? (a value type) — proves the
        // generic works for the checkbox field's shape too, not just
        // string?.
        var resolves = new Queue<DummyElement?>(new DummyElement?[] { Element, Element });
        var reads = new Queue<RetryingFieldRead.Attempt<bool?>>(new[]
        {
            new RetryingFieldRead.Attempt<bool?>(null, Threw: true),
            new RetryingFieldRead.Attempt<bool?>(true, Threw: false)
        });
        var suspiciousCalls = 0;

        var result = RetryingFieldRead.Read<DummyElement, bool?>(
            resolveElement: () => resolves.Count > 0 ? resolves.Dequeue() : Element,
            readValue: _ => reads.Count > 0 ? reads.Dequeue() : new RetryingFieldRead.Attempt<bool?>(null, Threw: false),
            hasEverReadNonBlank: false,
            isBlank: v => v is null,
            onSuspicious: () => suspiciousCalls++);

        Assert.True(result);
        Assert.Equal(1, suspiciousCalls);
    }
}
