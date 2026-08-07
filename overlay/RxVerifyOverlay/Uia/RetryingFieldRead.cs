using System;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Pure retry orchestration behind FieldReader.ReadWithRetry/
/// ReadBoolWithRetry — the highest-stakes new logic in the
/// uia-read-latency branch's element cache (branch brief item 4 safety:
/// "NEVER serve stale entered-field VALUES"). Extracted into its own
/// generic, delegate-driven class (no FlaUI/UIA/Windows dependency at
/// all) specifically so this exact algorithm is covered by fast xUnit
/// tests using fake delegates instead of only a manual code trace — see
/// RxVerifyOverlay.Tests/RetryingFieldReadTests.cs. FieldReader is the
/// only production caller, instantiating TElement=AutomationElement and
/// TValue=string?/bool? per field type.
/// </summary>
public static class RetryingFieldRead
{
    /// <summary>One read attempt's outcome: either a value (possibly blank/null) or Threw=true (value is meaningless in that case).</summary>
    public readonly record struct Attempt<TValue>(TValue Value, bool Threw);

    /// <summary>
    /// Algorithm: resolve an element (<paramref name="resolveElement"/>),
    /// read its value (<paramref name="readValue"/>). If that FIRST
    /// attempt threw, OR read blank (<paramref name="isBlank"/>) while
    /// <paramref name="hasEverReadNonBlank"/> is true (this field has
    /// read real data before, for the window this call belongs to),
    /// the read is treated as suspicious: <paramref name="onSuspicious"/>
    /// runs (in production, evicts the cached element) and the element
    /// is resolved + read EXACTLY ONE more time. Whatever that second
    /// attempt produces — a value, a blank, or another throw (-&gt;
    /// default) — is FINAL and returned as-is; there is no third
    /// attempt. A genuinely first-time blank (hasEverReadNonBlank is
    /// false) is trusted immediately with no retry at all: a real, empty
    /// field is a normal state, not a sign of staleness.
    ///
    /// resolveElement returning null (element not found at all) short-
    /// circuits to default at whichever attempt it happens on — a
    /// missing element is never itself a reason to retry (only a
    /// FOUND-but-suspicious read is).
    /// </summary>
    public static TValue Read<TElement, TValue>(
        Func<TElement?> resolveElement,
        Func<TElement, Attempt<TValue>> readValue,
        bool hasEverReadNonBlank,
        Func<TValue, bool> isBlank,
        Action onSuspicious)
        where TElement : class
    {
        var element = resolveElement();
        if (element is null) return default!;

        var attempt = readValue(element);

        var suspicious = attempt.Threw || (isBlank(attempt.Value) && hasEverReadNonBlank);
        if (suspicious)
        {
            onSuspicious();

            element = resolveElement();
            if (element is null) return default!;

            attempt = readValue(element);
        }

        return attempt.Value;
    }
}
