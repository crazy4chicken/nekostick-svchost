using System.Net.Http;
using Nekostick.ServiceHost.Compose;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class SourceResolver
{
    private async ValueTask<SourceResolutionResult> ResolveUrlAsync(
        ComposeSource source,
        string serviceName,
        LockSource? previousLock,
        string serviceArtifactDirectory,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        var url = source.Url!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceResolutionResult.Failure("Source URLs must be absolute HTTP or HTTPS URLs.");
        }

        var sameSource = previousLock?.MatchesUrl(url) == true &&
                         IsValidDigest(previousLock.Sha256);
        var existingArtifactPath = sameSource
            ? GetContentAddressedFilePath(serviceArtifactDirectory, serviceName, previousLock!.Sha256!)
            : string.Empty;
        if (sameSource && File.Exists(existingArtifactPath))
        {
            var digest = await ComputeDigestAsync(existingArtifactPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(digest, previousLock!.Sha256, StringComparison.OrdinalIgnoreCase) &&
                MatchesExplicitDigest(source.Sha256, digest))
            {
                return SourceResolutionResult.Success(existingArtifactPath, previousLock, true);
            }
        }

        return await DownloadAsync(
                uri,
                serviceName,
                serviceArtifactDirectory,
                temporaryDirectory,
                sameSource ? previousLock!.Sha256 : null,
                source.Sha256,
                url,
                previousLock,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SourceResolutionResult> DownloadAsync(
        Uri uri,
        string serviceName,
        string serviceArtifactDirectory,
        string temporaryDirectory,
        string? expectedLockedDigest,
        string? expectedDeclaredDigest,
        string url,
        LockSource? previousLock,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = CreateTemporaryPath(temporaryDirectory);
            try
            {
                using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCancellation.CancelAfter(_timeout);
                using var response = await _httpClient.GetAsync(
                        uri,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"The source returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                }

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > _maximumArtifactBytes)
                {
                    return SourceResolutionResult.Failure("The downloaded source exceeds the artifact size limit.");
                }

                await using (var input = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token)
                                 .ConfigureAwait(false))
                await using (var output = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), timeoutCancellation.Token)
                               .ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > _maximumArtifactBytes)
                        {
                            return SourceResolutionResult.Failure("The downloaded source exceeds the artifact size limit.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeoutCancellation.Token)
                            .ConfigureAwait(false);
                    }

                    await output.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);
                }

                var digest = await ComputeDigestAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                if (!MatchesExpectedDigest(expectedLockedDigest, digest) ||
                    !MatchesExplicitDigest(expectedDeclaredDigest, digest))
                {
                    return SourceResolutionResult.Failure(
                        "The downloaded source does not match the locked or declared sha256.");
                }

                var artifactPath = GetContentAddressedFilePath(serviceArtifactDirectory, serviceName, digest);
                await InstallContentAddressedFileAsync(
                        temporaryPath,
                        artifactPath,
                        digest,
                        cancellationToken)
                    .ConfigureAwait(false);
                var fileInfo = new FileInfo(artifactPath);
                var locked = PreserveUnchangedSourceLock(previousLock, new LockSource
                {
                    Kind = "url",
                    Url = url,
                    Sha256 = digest,
                    Size = fileInfo.Length,
                    FetchedAt = DateTimeOffset.UtcNow
                });
                return SourceResolutionResult.Success(artifactPath, locked, false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            if (attempt < _retryCount)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return SourceResolutionResult.Failure(
            $"The source could not be downloaded after {_retryCount + 1} attempts: {lastException?.Message}");
    }
}
