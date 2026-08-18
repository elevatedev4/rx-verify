using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for UpdateLauncher.BuildBootstrapCommand (Update/UpdateLauncher.cs)
/// — the pure PowerShell command-string builder behind the "Update ready"
/// banner's Update button (branch fix/rightclick-all-feedback-compact,
/// task 4). LaunchBootstrapAndExit itself (Process.Start + app shutdown)
/// is deliberately not exercised here — same "pure logic tested, OS-level
/// side effects aren't" split as every other *Launcher/*Submitter class in
/// this app. Report keys below are synthetic placeholders, not real
/// secrets.
/// </summary>
public class UpdateLauncherTests
{
    private const string ExpectedScriptBlock =
        "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; & ([scriptblock]::Create((irm https://raw.githubusercontent.com/elevatedev4/rx-verify/main/bootstrap-fresh.ps1)))";

    [Fact]
    public void OmitsReportKeyEntirelyWhenNull()
    {
        // bootstrap-fresh.ps1's own -ReportKey parameter defaults to ''
        // (see that script's own doc) — omitting it entirely must remain
        // valid, not append an empty -ReportKey ''.
        var command = UpdateLauncher.BuildBootstrapCommand(null);

        Assert.Equal(ExpectedScriptBlock, command);
        Assert.DoesNotContain("-ReportKey", command);
    }

    [Fact]
    public void OmitsReportKeyEntirelyWhenEmpty()
    {
        var command = UpdateLauncher.BuildBootstrapCommand("");

        Assert.Equal(ExpectedScriptBlock, command);
        Assert.DoesNotContain("-ReportKey", command);
    }

    [Fact]
    public void OmitsReportKeyEntirelyWhenWhitespace()
    {
        var command = UpdateLauncher.BuildBootstrapCommand("   ");

        Assert.Equal(ExpectedScriptBlock, command);
        Assert.DoesNotContain("-ReportKey", command);
    }

    [Fact]
    public void AppendsSingleQuotedReportKeyWhenSet()
    {
        var command = UpdateLauncher.BuildBootstrapCommand("SYNTHETIC-KEY-abc123");

        Assert.Equal(ExpectedScriptBlock + " -ReportKey 'SYNTHETIC-KEY-abc123'", command);
    }

    [Fact]
    public void DoublesEmbeddedSingleQuotesInTheReportKey()
    {
        // PowerShell's own escape for a literal ' inside a single-quoted
        // string is '' — bootstrap-fresh.ps1's own doc is explicit that
        // this must be single-quoted (not double-quoted, which would
        // instead expand a $ in the key) — see BuildBootstrapCommand's doc.
        var command = UpdateLauncher.BuildBootstrapCommand("SYNTHETIC-KEY-o'brien");

        Assert.Equal(ExpectedScriptBlock + " -ReportKey 'SYNTHETIC-KEY-o''brien'", command);
    }

    [Fact]
    public void DoesNotExpandDollarSignsInTheReportKey()
    {
        // Single-quoting (not double-quoting) is what makes this safe —
        // PowerShell never interpolates inside single quotes, so the
        // literal text must survive verbatim in the built command string.
        var command = UpdateLauncher.BuildBootstrapCommand("SYNTHETIC$KEY$WITH$DOLLARS");

        Assert.Equal(ExpectedScriptBlock + " -ReportKey 'SYNTHETIC$KEY$WITH$DOLLARS'", command);
    }
}
