using Nekostick.ServiceHost.Compose;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class SourceResolver
{
    private async ValueTask<SourceResolutionResult> ResolvePathAsync(
        ComposeSource source,
        LockSource? previousLock,
        string artifactPath,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(source.Path!);
        if (!File.Exists(sourcePath))
        {
            return SourceResolutionResult.Failure("The local source file does not exist.");
        }

        var info = new FileInfo(sourcePath);
        if (info.Length > _maximumArtifactBytes)
        {
            return SourceResolutionResult.Failure("The source file exceeds the artifact size limit.");
        }

        var digest = await ComputeDigestAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!MatchesExplicitDigest(source.Sha256, digest))
        {
            return SourceResolutionResult.Failure("The local source does not match its declared sha256.");
        }

        var sameSource = previousLock?.MatchesPath(sourcePath) == true &&
                         IsValidDigest(previousLock.Sha256) &&
                         string.Equals(previousLock.Sha256, digest, StringComparison.OrdinalIgnoreCase);
        if (sameSource && File.Exists(artifactPath))
        {
            var artifactDigest = await ComputeDigestAsync(artifactPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(artifactDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                return SourceResolutionResult.Success(artifactPath, previousLock!, true);
            }
        }

        var temporaryPath = CreateTemporaryPath(temporaryDirectory);
        try
        {
            await CopyToTemporaryAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            var copiedDigest = await ComputeDigestAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(copiedDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                return SourceResolutionResult.Failure("The local source changed while it was copied.");
            }

            AtomicInstall(temporaryPath, artifactPath);
            var locked = new LockSource
            {
                Kind = "path",
                Path = sourcePath,
                Sha256 = digest,
                Size = info.Length,
                FetchedAt = DateTimeOffset.UtcNow
            };
            return SourceResolutionResult.Success(artifactPath, locked, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SourceResolutionResult.Failure("The local source copy timed out.");
        }
        catch (IOException exception)
        {
            return SourceResolutionResult.Failure($"The local source could not be copied: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return SourceResolutionResult.Failure($"The local source could not be copied: {exception.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async ValueTask CopyToTemporaryAsync(
        string sourcePath,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
