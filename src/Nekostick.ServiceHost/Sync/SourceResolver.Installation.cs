using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class SourceResolver
{
    private static async ValueTask<string> ComputeDigestAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string CreateTemporaryPath(string temporaryDirectory) =>
        Path.Combine(temporaryDirectory, $"{Guid.CreateVersion7():N}.tmp");

    internal static string GetArtifactGenerationDirectory(string serviceArtifactDirectory, string digest) =>
        Path.Combine(serviceArtifactDirectory, digest.ToLowerInvariant());

    internal static string GetContentAddressedFilePath(
        string serviceArtifactDirectory,
        string serviceName,
        string digest) =>
        Path.Combine(GetArtifactGenerationDirectory(serviceArtifactDirectory, digest), serviceName);

    private static async ValueTask InstallContentAddressedFileAsync(
        string temporaryPath,
        string artifactPath,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        SetExecutable(temporaryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        if (Directory.Exists(artifactPath))
        {
            throw new IOException("The content-addressed artifact path is a directory.");
        }

        if (File.Exists(artifactPath))
        {
            var existingDigest = await ComputeDigestAsync(artifactPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The content-addressed artifact path contains different contents.");
            }

            return;
        }

        try
        {
            File.Move(temporaryPath, artifactPath);
        }
        catch (IOException) when (File.Exists(artifactPath))
        {
            var existingDigest = await ComputeDigestAsync(artifactPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The content-addressed artifact path contains different contents.");
            }
        }
    }

    private static void SetExecutable(string artifactPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                artifactPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static bool MatchesExpectedDigest(string? expectedDigest, string actualDigest) =>
        expectedDigest is null || string.Equals(expectedDigest, actualDigest, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesExplicitDigest(string? expectedDigest, string actualDigest) =>
        expectedDigest is null || string.Equals(expectedDigest, actualDigest, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(static value => Uri.IsHexDigit(value));

    private static bool IsValidName(string? name) =>
        name is not null &&
        name.Length is >= 1 and <= 63 &&
        name[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        name.All(static value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsValidConfigName(string? name) =>
        IsValidName(name) &&
        !string.Equals(name, "global", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateSharedHttpClient() =>
        new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
