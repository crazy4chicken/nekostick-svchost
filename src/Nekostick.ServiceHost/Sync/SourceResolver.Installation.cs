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

    private static void AtomicInstall(string temporaryPath, string artifactPath)
    {
        // The host execs the artifact path directly (no shell), so the executable bit must be
        // present. New files default to 0666 & ~umask on POSIX; set 0755 on the temporary file
        // BEFORE the move so the artifact never exists without it.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temporaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        File.Move(temporaryPath, artifactPath, true);
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
}
