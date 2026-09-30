using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync.Releases;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class SourceResolver
{
    private async ValueTask<SourceResolutionResult> ResolveReleaseAsync(
        ComposeSource source,
        string serviceName,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings>? releaseProviderSettings,
        ReleaseProviderRegistry releaseProviders,
        string artifactPath,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        if (!ReleaseSource.TryParse(source.Release, out var releaseSource) || releaseSource is null)
        {
            return SourceResolutionResult.Failure("The release source must use '{provider}:{spec}' with a valid provider key and non-empty spec.");
        }

        if (!releaseProviders.TryGetProvider(releaseSource.ProviderKey, out var provider) || provider is null)
        {
            return SourceResolutionResult.Failure($"The release provider '{releaseSource.ProviderKey}' is not registered.");
        }

        if (!TryGetReleaseArchitecture(out var arch, out var unsupportedArchitecture))
        {
            return SourceResolutionResult.Failure(
                $"The current process architecture '{unsupportedArchitecture}' is not supported for release assets.");
        }

        ReleaseProviderResult providerResult;
        try
        {
            providerResult = await provider.ResolveAsync(releaseSource, serviceName, arch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SourceResolutionResult.Failure(
                $"Release provider '{releaseSource.ProviderKey}' timed out while resolving '{releaseSource.Spec}': {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return SourceResolutionResult.Failure(
                $"Release provider '{releaseSource.ProviderKey}' failed while resolving '{releaseSource.Spec}': {exception.Message}");
        }

        if (!providerResult.Succeeded || providerResult.Asset is null)
        {
            return SourceResolutionResult.Failure(
                providerResult.Error ?? $"Release provider '{releaseSource.ProviderKey}' did not resolve an asset.",
                providerResult.Warnings);
        }

        var asset = providerResult.Asset;
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            return SourceResolutionResult.Failure(
                $"Release provider '{releaseSource.ProviderKey}' returned an empty asset download URL.",
                providerResult.Warnings);
        }

        var executablePath = FindEntryExecutable(artifactPath, serviceName);
        var sameIdentity = previousLock is not null &&
                           previousLock.MatchesRelease(releaseSource.ProviderKey, releaseSource.Spec) &&
                           string.Equals(previousLock.Tag, asset.Tag, StringComparison.Ordinal) &&
                           string.Equals(previousLock.Version, asset.Version, StringComparison.Ordinal) &&
                           string.Equals(previousLock.AssetName, asset.AssetName, StringComparison.Ordinal) &&
                           (source.Sha256 is null || string.Equals(previousLock.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)) &&
                           (asset.Sha256 is null || string.Equals(previousLock.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase));
        if (sameIdentity && executablePath is not null)
        {
            return SourceResolutionResult.Success(executablePath, previousLock!, true, providerResult.Warnings);
        }

        var expectedLockedDigest = sameIdentity && IsValidDigest(previousLock?.Sha256)
            ? previousLock!.Sha256
            : null;
        var expectedAssetDigest = source.Sha256 ?? asset.Sha256;
        var candidateUrls = new List<string>();
        if (releaseProviderSettings is not null &&
            releaseProviderSettings.TryGetValue(releaseSource.ProviderKey, out var providerSettings) &&
            providerSettings?.Mirrors is { } mirrors)
        {
            foreach (var mirror in mirrors)
            {
                if (mirror is not null)
                {
                    candidateUrls.Add(string.Concat(mirror, asset.DownloadUrl));
                }
            }
        }

        candidateUrls.Add(asset.DownloadUrl);
        var downloadResult = await DownloadReleaseAssetAsync(
                candidateUrls,
                expectedLockedDigest,
                expectedAssetDigest,
                temporaryDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var download = downloadResult.Download;
        if (download is null)
        {
            return SourceResolutionResult.Failure(
                downloadResult.Error ?? "Every release asset download candidate failed.",
                providerResult.Warnings);
        }
        var stagingDirectory = Path.Combine(temporaryDirectory, $"{Guid.CreateVersion7():N}.extract");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await ExtractArchiveAsync(download.Path, stagingDirectory, cancellationToken).ConfigureAwait(false);
            var stagedExecutable = FindEntryExecutable(stagingDirectory, serviceName);
            if (stagedExecutable is null)
            {
                return SourceResolutionResult.Failure(
                    $"The release archive does not contain an executable named '{serviceName}' at its extraction root.",
                    providerResult.Warnings);
            }

            SetExecutable(stagedExecutable);
            ReplaceArtifactDirectory(stagingDirectory, artifactPath);
            var installedExecutable = FindEntryExecutable(artifactPath, serviceName) ??
                                      Path.Combine(artifactPath, Path.GetFileName(stagedExecutable));
            var locked = new LockSource
            {
                Kind = "release",
                ProviderKey = releaseSource.ProviderKey,
                Spec = releaseSource.Spec,
                Tag = asset.Tag,
                Version = asset.Version,
                AssetName = asset.AssetName,
                Sha256 = download.Sha256,
                Size = download.Size,
                FetchedAt = DateTimeOffset.UtcNow
            };
            return SourceResolutionResult.Success(installedExecutable, locked, false, providerResult.Warnings);
        }
        catch (InvalidDataException exception)
        {
            return SourceResolutionResult.Failure($"The release archive is invalid: {exception.Message}", providerResult.Warnings);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return SourceResolutionResult.Failure($"The release archive contains an invalid entry path: {exception.Message}", providerResult.Warnings);
        }
        catch (IOException exception)
        {
            return SourceResolutionResult.Failure($"The release archive could not be installed: {exception.Message}", providerResult.Warnings);
        }
        catch (UnauthorizedAccessException exception)
        {
            return SourceResolutionResult.Failure($"The release archive could not be installed: {exception.Message}", providerResult.Warnings);
        }
        finally
        {
            TryDelete(download.Path);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async ValueTask<ReleaseDownloadResult> DownloadReleaseAssetAsync(
        IReadOnlyList<string> candidateUrls,
        string? expectedLockedDigest,
        string? expectedAssetDigest,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>(candidateUrls.Count);
        foreach (var candidateUrl in candidateUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DownloadReleaseCandidateAsync(
                    candidateUrl,
                    expectedLockedDigest,
                    expectedAssetDigest,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Download is not null)
            {
                return result;
            }

            failures.Add($"{candidateUrl}: {result.Error}");
        }

        return new ReleaseDownloadResult(
            null,
            $"All release asset download candidates failed. {string.Join("; ", failures)}");
    }

    private async ValueTask<ReleaseDownloadResult> DownloadReleaseCandidateAsync(
        string candidateUrl,
        string? expectedLockedDigest,
        string? expectedAssetDigest,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri))
        {
            return new ReleaseDownloadResult(null, "The candidate is not an absolute URL.");
        }

        Exception? lastException = null;
        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = CreateTemporaryPath(temporaryDirectory);
            var keepTemporaryFile = false;
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
                        $"The candidate returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                }

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > _maximumArtifactBytes)
                {
                    return new ReleaseDownloadResult(null, "The downloaded release asset exceeds the artifact size limit.");
                }

                long total = 0;
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
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), timeoutCancellation.Token)
                               .ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > _maximumArtifactBytes)
                        {
                            return new ReleaseDownloadResult(null, "The downloaded release asset exceeds the artifact size limit.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeoutCancellation.Token)
                            .ConfigureAwait(false);
                    }

                    await output.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);
                }

                var digest = await ComputeDigestAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                if (!MatchesExpectedDigest(expectedLockedDigest, digest) ||
                    !MatchesExpectedDigest(expectedAssetDigest, digest))
                {
                    return new ReleaseDownloadResult(null, "The downloaded release asset does not match the expected sha256.");
                }

                keepTemporaryFile = true;
                return new ReleaseDownloadResult(new ReleaseDownload(temporaryPath, digest, total), null);
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
                if (!keepTemporaryFile)
                {
                    TryDelete(temporaryPath);
                }
            }

            if (attempt < _retryCount)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new ReleaseDownloadResult(
            null,
            $"The candidate failed after {_retryCount + 1} attempts: {lastException?.Message ?? "unknown download error"}");
    }

    private static async ValueTask ExtractArchiveAsync(
        string archivePath,
        string extractionDirectory,
        CancellationToken cancellationToken)
    {
        await using var archiveFile = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = GetSafeArchiveEntryPath(extractionDirectory, entry.FullName);
            var isDirectory = entry.FullName.EndsWith('/') ||
                              entry.FullName.EndsWith('\\') ||
                              entry.Name.Length == 0;
            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (parentDirectory is not null)
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await using var input = entry.Open();
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetSafeArchiveEntryPath(string extractionDirectory, string entryName)
    {
        var normalizedName = entryName.Replace('\\', '/');
        if (string.IsNullOrEmpty(normalizedName) ||
            Path.IsPathRooted(normalizedName) ||
            normalizedName[0] == '/' ||
            (normalizedName.Length >= 2 && char.IsAsciiLetter(normalizedName[0]) && normalizedName[1] == ':'))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' has a rooted path.");
        }

        var root = Path.GetFullPath(extractionDirectory);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : string.Concat(root, Path.DirectorySeparatorChar);
        var relativeName = normalizedName.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, relativeName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' escapes the extraction directory.");
        }

        return destination;
    }

    private static string? FindEntryExecutable(string directory, string serviceName)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? windowsExecutableMatch = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, serviceName, StringComparison.Ordinal))
            {
                return path;
            }

            if (OperatingSystem.IsWindows() &&
                windowsExecutableMatch is null &&
                string.Equals(fileName, string.Concat(serviceName, ".exe"), StringComparison.Ordinal))
            {
                windowsExecutableMatch = path;
            }
        }

        return windowsExecutableMatch;
    }

    private static void ReplaceArtifactDirectory(string stagingDirectory, string artifactDirectory)
    {
        if (Directory.Exists(artifactDirectory))
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }

        if (File.Exists(artifactDirectory))
        {
            File.Delete(artifactDirectory);
        }

        Directory.Move(stagingDirectory, artifactDirectory);
    }

    private static bool TryGetReleaseArchitecture(out string arch, out string unsupportedArchitecture)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => string.Empty
        };
        unsupportedArchitecture = architecture.ToString();
        return arch.Length > 0;
    }

    private sealed record ReleaseDownload(string Path, string Sha256, long Size);

    private sealed record ReleaseDownloadResult(ReleaseDownload? Download, string? Error);
}
