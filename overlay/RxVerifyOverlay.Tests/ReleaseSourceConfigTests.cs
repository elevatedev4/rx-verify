using System;
using System.Collections.Generic;
using System.IO;
using RxVerifyOverlay.VelopackIntegration;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// ReleaseSourceConfig.Load backs VelopackUpdater's "no config, no update
/// checks" gate — every failure mode must return null rather than throw,
/// and log exactly once so a misconfigured workstation is diagnosable from
/// the log. Mirrors vaccine-assist's ReleaseSourceConfigTests.cs.
/// </summary>
public class ReleaseSourceConfigTests
{
    [Fact]
    public void LoadReturnsNullAndLogsWhenFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"release-source-missing-{Guid.NewGuid()}.json");
        var logs = new List<string>();

        var result = ReleaseSourceConfig.Load(path, logs.Add);

        Assert.Null(result);
        Assert.Single(logs);
        Assert.Contains("no config", logs[0]);
    }

    [Fact]
    public void LoadReturnsNullAndLogsWhenJsonIsMalformed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"release-source-malformed-{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{ this is not valid json");
        var logs = new List<string>();

        try
        {
            var result = ReleaseSourceConfig.Load(path, logs.Add);

            Assert.Null(result);
            Assert.Single(logs);
            Assert.Contains("malformed", logs[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadReturnsNullAndLogsWhenARequiredFieldIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"release-source-incomplete-{Guid.NewGuid()}.json");
        // Valid JSON, but no accessKeyId/secretAccessKey.
        File.WriteAllText(path, "{ \"bucket\": \"my-bucket\", \"region\": \"us-east-1\" }");
        var logs = new List<string>();

        try
        {
            var result = ReleaseSourceConfig.Load(path, logs.Add);

            Assert.Null(result);
            Assert.Single(logs);
            Assert.Contains("missing a required field", logs[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadReturnsPopulatedOptionsForValidJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"release-source-valid-{Guid.NewGuid()}.json");
        File.WriteAllText(path, """
            {
              "bucket": "elevatedev4-desktop-releases",
              "region": "us-east-1",
              "prefix": "releases/rx-verify",
              "accessKeyId": "AKIAEXAMPLE",
              "secretAccessKey": "secret-example"
            }
            """);
        var logs = new List<string>();

        try
        {
            var result = ReleaseSourceConfig.Load(path, logs.Add);

            Assert.NotNull(result);
            Assert.Empty(logs);
            Assert.Equal("elevatedev4-desktop-releases", result!.Bucket);
            Assert.Equal("us-east-1", result.Region);
            Assert.Equal("releases/rx-verify", result.Prefix);
            Assert.Equal("AKIAEXAMPLE", result.AccessKeyId);
            Assert.Equal("secret-example", result.SecretAccessKey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
