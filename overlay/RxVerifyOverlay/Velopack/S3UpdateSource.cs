using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace RxVerifyOverlay.VelopackIntegration;

/// <summary>
/// Velopack <see cref="IUpdateSource"/> backed by a private S3 bucket,
/// read via a read-only IAM key (see ReleaseSourceConfig). Mirrors
/// vaccine-assist's Velopack/S3UpdateSource.cs exactly.
///
/// WHY THIS CLASS EXISTS: the Velopack NuGet package (checked against
/// github.com/velopack/velopack tag 1.2.161, the version pinned in the
/// .csproj) does not ship a client-side S3 update source — its Sources/
/// folder only has GithubSource, GitlabSource, GiteaSource,
/// SimpleWebSource, SimpleFileSource, and VelopackFlowSource. S3 only
/// appears on the CLI side (`vpk upload s3` / `vpk download s3`, used by
/// this repo's desktop-release.yml workflow to PUSH a built release to
/// S3), not as something an installed app can use to PULL updates. This
/// class is that missing client-side half, implementing the same
/// "one release-feed JSON file + per-asset files under a prefix"
/// convention SimpleWebSource uses for a plain HTTP host, but read via
/// AWSSDK.S3 with the read-only credentials from ReleaseSourceConfig
/// instead of an anonymous HTTP GET (the bucket has Block Public Access
/// on).
/// </summary>
public sealed class S3UpdateSource : IUpdateSource
{
    private readonly ReleaseSourceConfig _config;

    public S3UpdateSource(ReleaseSourceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var key = CombineKey(_config.Prefix, GetReleaseIndexFileName(channel));

        using var client = CreateClient();
        try
        {
            using var response = await client.GetObjectAsync(_config.Bucket, key).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync().ConfigureAwait(false);
            return VelopackAssetFeed.FromJson(json);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No release published yet for this channel (e.g. before the
            // very first `git tag v1.0.0` cut) — treat as "no updates",
            // never as an error the caller needs to handle specially.
            logger.Info($"[S3UpdateSource] no release feed at s3://{_config.Bucket}/{key} yet.");
            return new VelopackAssetFeed();
        }
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        if (releaseEntry is null) throw new ArgumentNullException(nameof(releaseEntry));
        if (localFile is null) throw new ArgumentNullException(nameof(localFile));

        var key = CombineKey(_config.Prefix, releaseEntry.FileName);
        logger.Info($"[S3UpdateSource] downloading '{releaseEntry.FileName}' from s3://{_config.Bucket}/{key}.");

        using var client = CreateClient();
        using var response = await client.GetObjectAsync(_config.Bucket, key, cancelToken).ConfigureAwait(false);
        await using var fileStream = File.Create(localFile);
        await response.ResponseStream.CopyToAsync(fileStream, cancelToken).ConfigureAwait(false);

        // GetObjectAsync has no incremental byte-progress hook without a
        // second (HEAD + ranged-read) round trip; a single 0->100 jump on
        // completion is an accepted simplification here — Setup.exe/delta
        // packages are small enough that this isn't user-visible the way a
        // multi-minute download would be.
        progress(100);
    }

    private AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(_config.AccessKeyId, _config.SecretAccessKey);
        var region = RegionEndpoint.GetBySystemName(_config.Region);
        return new AmazonS3Client(credentials, new AmazonS3Config { RegionEndpoint = region });
    }

    /// <summary>
    /// Replicates Velopack's own release-feed naming convention
    /// (<c>releases.{channel}.json</c>, e.g. "releases.win.json") without
    /// depending on it directly — the class that implements it,
    /// <c>Velopack.Util.CoreUtil</c>, is declared <c>internal</c> to the
    /// Velopack assembly (verified against the published source at
    /// github.com/velopack/velopack, tag 1.2.161), so it's not callable
    /// from here even though its one public method looks accessible.
    /// </summary>
    private static string GetReleaseIndexFileName(string? channel)
    {
        return $"releases.{channel ?? VelopackRuntimeInfo.SystemOs.GetOsShortName()}.json";
    }

    private static string CombineKey(string prefix, string fileName)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return fileName;
        }

        return prefix.TrimEnd('/') + "/" + fileName;
    }
}
