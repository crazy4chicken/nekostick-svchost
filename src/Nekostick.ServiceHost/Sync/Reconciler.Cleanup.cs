using System.Collections.Immutable;
using System.Diagnostics;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    private sealed record ArtifactRoot(string ConfigRoot, bool IsGlobal);

    private sealed record ArtifactLocation(
        string ConfigRoot,
        string ServiceName,
        string ArtifactEntry,
        string? GenerationDirectory,
        bool IsContentAddressed);

    private async ValueTask CleanupCommittedStateBestEffortAsync(
        SvchostSettings settings,
        ImmutableArray<ServiceConfiguration> services,
        IEnumerable<RetiringServiceSettings> knownRetirements,
        IEnumerable<RetiringServiceSettings> removedRetirements,
        bool allowConfigDirectoryCleanup,
        CancellationToken cancellationToken)
    {
        try
        {
            await CleanupCommittedStateCoreAsync(
                    settings,
                    services,
                    knownRetirements,
                    removedRetirements,
                    allowConfigDirectoryCleanup,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{Owner}: post-commit cleanup failed: {exception.Message}");
        }
    }

    private async ValueTask CleanupCommittedStateCoreAsync(
        SvchostSettings settings,
        ImmutableArray<ServiceConfiguration> services,
        IEnumerable<RetiringServiceSettings> knownRetirements,
        IEnumerable<RetiringServiceSettings> removedRetirements,
        bool allowConfigDirectoryCleanup,
        CancellationToken cancellationToken)
    {
        if (_supervisor is null || string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return;
        }

        ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>> runtimeResult;
        try
        {
            runtimeResult = await _supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{Owner}: artifact cleanup skipped because runtime state could not be read: {exception.Message}");
            return;
        }

        if (!runtimeResult.IsSuccess || runtimeResult.Value.IsDefault)
        {
            return;
        }

        var runtimeById = runtimeResult.Value.ToDictionary(runtime => runtime.ServiceId);
        var knownRetirementArray = knownRetirements.Select(CloneRetirement).ToArray();
        var removedRetirementArray = removedRetirements.Select(CloneRetirement).ToArray();
        var svchostRoot = Path.GetFullPath(Path.Combine(_dataDirectory, "svchost"));
        var roots = new Dictionary<string, ArtifactRoot>(PathComparer);
        var globalConfigsByServiceName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var configPair in settings.Configs)
        {
            var compose = TryParseForCleanup(configPair.Value);
            if (compose is null)
            {
                continue;
            }

            var configRoot = ServiceRootPath.Resolve(_dataDirectory, compose.ServiceScope, configPair.Key);
            roots[configRoot] = new ArtifactRoot(configRoot, compose.ServiceScope == ComposeServiceScope.Global);
            if (compose.ServiceScope == ComposeServiceScope.Global)
            {
                foreach (var serviceName in compose.Services.Keys)
                {
                    if (!globalConfigsByServiceName.TryGetValue(serviceName, out var configNames))
                    {
                        configNames = new HashSet<string>(StringComparer.Ordinal);
                        globalConfigsByServiceName.Add(serviceName, configNames);
                    }

                    configNames.Add(configPair.Key);
                }
            }
        }

        foreach (var retirement in knownRetirementArray.Concat(removedRetirementArray))
        {
            var scope = retirement.Scope == "document"
                ? ComposeServiceScope.Document
                : ComposeServiceScope.Global;
            var configRoot = ServiceRootPath.Resolve(_dataDirectory, scope, retirement.ConfigName);
            roots[configRoot] = new ArtifactRoot(configRoot, scope == ComposeServiceScope.Global);
        }

        var referencedPaths = new HashSet<string>(PathComparer);
        var protectedLegacyEntries = new HashSet<string>(PathComparer);
        var protectedGenerationDirectories = new HashSet<string>(PathComparer);
        var protectedContentServiceDirectories = new HashSet<string>(PathComparer);
        var servicesById = services.ToDictionary(service => service.Id);
        foreach (var service in services)
        {
            if (TryGetArtifactLocation(service.FileName, svchostRoot, out var location))
            {
                roots[location.ConfigRoot] = new ArtifactRoot(
                    location.ConfigRoot,
                    string.Equals(Path.GetFileName(location.ConfigRoot), "global", StringComparison.OrdinalIgnoreCase));
            }

            AddPath(referencedPaths, service.FileName);
        }

        foreach (var configPair in settings.Configs)
        {
            var compose = TryParseForCleanup(configPair.Value);
            if (compose is null || configPair.Value?.Lock?.Services is not { } lockServices)
            {
                continue;
            }

            var configRoot = ServiceRootPath.Resolve(_dataDirectory, compose.ServiceScope, configPair.Key);
            foreach (var lockPair in lockServices)
            {
                var lockEntry = lockPair.Value;
                if (lockEntry is null || !IsValidServiceName(lockPair.Key))
                {
                    continue;
                }

                var legacyEntry = Path.GetFullPath(Path.Combine(configRoot, "artifacts", lockPair.Key));
                var serviceIsContentAddressed = servicesById.TryGetValue(lockEntry.ServiceId, out var deployedService) &&
                                                 IsContentAddressedFile(
                                                     deployedService.FileName,
                                                     configRoot,
                                                     lockPair.Key,
                                                     lockEntry.Source?.Sha256);
                if (!serviceIsContentAddressed)
                {
                    protectedLegacyEntries.Add(legacyEntry);
                }

                if (IsValidDigest(lockEntry.Source?.Sha256))
                {
                    protectedGenerationDirectories.Add(Path.GetFullPath(Path.Combine(
                        configRoot,
                        "artifacts",
                        "sha256",
                        lockPair.Key,
                        lockEntry.Source!.Sha256!.ToLowerInvariant())));
                }
                else
                {
                    protectedContentServiceDirectories.Add(Path.GetFullPath(Path.Combine(
                        configRoot,
                        "artifacts",
                        "sha256",
                        lockPair.Key)));
                }
            }
        }

        var activeArtifactServiceDirectories = new HashSet<string>(PathComparer);
        var activeConfigRoots = new HashSet<string>(PathComparer);
        foreach (var service in services)
        {
            if (!TryGetArtifactLocation(service.FileName, svchostRoot, out var location) ||
                IsKnownInactive(runtimeById, service.Id))
            {
                continue;
            }

            activeArtifactServiceDirectories.Add(Path.GetFullPath(Path.Combine(
                location.ConfigRoot,
                "artifacts",
                "sha256",
                location.ServiceName)));
            activeConfigRoots.Add(location.ConfigRoot);
        }

        foreach (var retirement in removedRetirementArray)
        {
            if (runtimeById.TryGetValue(retirement.ServiceId, out var runtime) &&
                IsInactive(runtime.LifecycleState))
            {
                continue;
            }

            var scope = retirement.Scope == "document"
                ? ComposeServiceScope.Document
                : ComposeServiceScope.Global;
            var configRoot = ServiceRootPath.Resolve(_dataDirectory, scope, retirement.ConfigName);
            activeArtifactServiceDirectories.Add(Path.GetFullPath(Path.Combine(
                configRoot,
                "artifacts",
                "sha256",
                retirement.ServiceName)));
            activeConfigRoots.Add(configRoot);
        }

        // A Running/Starting process with StartedAt at or after the commit-stamped UpdatedAt is the committed
        // generation on this node; its generation is inherently protected by referencedPaths and the persisted
        // lock digest (source-failure services keep their lock digest), so older unreferenced generation
        // directories of that service can be pruned continuously.
        var confirmedRunningServiceDirectories = new HashSet<string>(PathComparer);
        foreach (var service in services)
        {
            if (knownRetirementArray.Any(retirement => retirement.ServiceId == service.Id) ||
                removedRetirementArray.Any(retirement => retirement.ServiceId == service.Id) ||
                IsKnownInactive(runtimeById, service.Id) ||
                !TryGetArtifactLocation(service.FileName, svchostRoot, out var location) ||
                !location.IsContentAddressed ||
                !runtimeById.TryGetValue(service.Id, out var runtime) ||
                runtime.LifecycleState is not (ExtensionServiceLifecycleState.Running or
                    ExtensionServiceLifecycleState.Starting) ||
                runtime.StartedAt is not { } startedAt ||
                startedAt < service.UpdatedAt)
            {
                continue;
            }

            confirmedRunningServiceDirectories.Add(Path.GetFullPath(Path.Combine(
                location.ConfigRoot,
                "artifacts",
                "sha256",
                location.ServiceName)));
        }

        // Sweep every svchost root that holds a content-addressed store, including roots no configuration maps
        // to, so generations left behind by former roots stay reachable; referenced and lock-pinned
        // generations remain protected by the guards below.
        var sweepRoots = new HashSet<string>(roots.Keys, PathComparer);
        if (Directory.Exists(svchostRoot))
        {
            foreach (var candidateRoot in Directory.EnumerateDirectories(svchostRoot))
            {
                var fullCandidateRoot = Path.GetFullPath(candidateRoot);
                if (!IsPathWithin(svchostRoot, fullCandidateRoot) ||
                    !IsPathWithoutReparsePoints(svchostRoot, fullCandidateRoot) ||
                    !Directory.Exists(Path.Combine(fullCandidateRoot, "artifacts", "sha256")))
                {
                    continue;
                }

                sweepRoots.Add(fullCandidateRoot);
            }
        }

        foreach (var sweepRoot in sweepRoots)
        {
            if (!IsPathWithin(svchostRoot, sweepRoot) ||
                !IsPathWithoutReparsePoints(svchostRoot, sweepRoot))
            {
                continue;
            }

            var artifactRoot = Path.GetFullPath(Path.Combine(sweepRoot, "artifacts"));
            var sha256Root = Path.Combine(artifactRoot, "sha256");
            if (!IsPathWithin(sweepRoot, artifactRoot) ||
                !IsPathWithoutReparsePoints(sweepRoot, artifactRoot))
            {
                continue;
            }

            if (IsPathWithoutReparsePoints(sweepRoot, sha256Root) && Directory.Exists(sha256Root))
            {
                CollectContentAddressedArtifacts(
                    sweepRoot,
                    sha256Root,
                    referencedPaths,
                    protectedGenerationDirectories,
                    protectedContentServiceDirectories,
                    activeArtifactServiceDirectories,
                    confirmedRunningServiceDirectories);
            }

            if (!roots.TryGetValue(sweepRoot, out var root) || !Directory.Exists(artifactRoot))
            {
                continue;
            }

            foreach (var legacyEntry in Directory.EnumerateFileSystemEntries(artifactRoot))
            {
                if (string.Equals(Path.GetFileName(legacyEntry), "sha256", StringComparison.Ordinal))
                {
                    continue;
                }

                var serviceName = Path.GetFileName(legacyEntry);
                if (protectedLegacyEntries.Contains(Path.GetFullPath(legacyEntry)) ||
                    activeArtifactServiceDirectories.Contains(Path.GetFullPath(Path.Combine(
                        root.ConfigRoot,
                        "artifacts",
                        "sha256",
                        serviceName))) ||
                    referencedPaths.Any(path => IsPathWithinOrEqual(legacyEntry, path)) ||
                    (root.IsGlobal && HasGlobalSiblingDeclaration(
                        serviceName,
                        globalConfigsByServiceName,
                        removedRetirementArray)))
                {
                    continue;
                }

                TryDeletePath(legacyEntry);
            }
        }

        if (!allowConfigDirectoryCleanup)
        {
            return;
        }

        foreach (var retirement in removedRetirementArray
                     .Where(entry => entry.Scope == "document")
                     .GroupBy(entry => entry.ConfigName, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (settings.Configs.ContainsKey(retirement.ConfigName) ||
                settings.Retiring.Any(entry => string.Equals(
                    entry.ConfigName,
                    retirement.ConfigName,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            var documentRoot = ServiceRootPath.Resolve(
                _dataDirectory,
                ComposeServiceScope.Document,
                retirement.ConfigName);
            if (!IsPathWithin(svchostRoot, documentRoot) ||
                !IsPathWithoutReparsePoints(svchostRoot, documentRoot) ||
                services.Any(service => IsPathWithinOrEqual(documentRoot, service.FileName)) ||
                activeConfigRoots.Contains(documentRoot))
            {
                continue;
            }

            var artifactsDirectory = Path.Combine(documentRoot, "artifacts");
            var temporaryDirectory = Path.Combine(documentRoot, "tmp");
            if (IsPathWithin(documentRoot, artifactsDirectory))
            {
                TryDeletePath(artifactsDirectory);
            }

            if (IsPathWithin(documentRoot, temporaryDirectory))
            {
                TryDeletePath(temporaryDirectory);
            }

            try
            {
                if (Directory.Exists(documentRoot) &&
                    !Directory.EnumerateFileSystemEntries(documentRoot).Any())
                {
                    Directory.Delete(documentRoot);
                }
            }
            catch (IOException exception)
            {
                Debug.WriteLine($"{Owner}: document data cleanup failed for '{retirement.ConfigName}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine($"{Owner}: document data cleanup failed for '{retirement.ConfigName}': {exception.Message}");
            }
        }
    }

    private static void CollectContentAddressedArtifacts(
        string configRoot,
        string sha256Root,
        IReadOnlySet<string> referencedPaths,
        IReadOnlySet<string> protectedGenerationDirectories,
        IReadOnlySet<string> protectedContentServiceDirectories,
        IReadOnlySet<string> activeArtifactServiceDirectories,
        IReadOnlySet<string> confirmedRunningServiceDirectories)
    {
        foreach (var serviceDirectory in Directory.EnumerateDirectories(sha256Root))
        {
            var fullServiceDirectory = Path.GetFullPath(serviceDirectory);
            if (!IsPathWithin(sha256Root, fullServiceDirectory) ||
                !IsPathWithoutReparsePoints(sha256Root, fullServiceDirectory) ||
                protectedContentServiceDirectories.Contains(fullServiceDirectory) ||
                (activeArtifactServiceDirectories.Contains(fullServiceDirectory) &&
                    !confirmedRunningServiceDirectories.Contains(fullServiceDirectory)))
            {
                continue;
            }

            foreach (var generationDirectory in Directory.EnumerateDirectories(fullServiceDirectory))
            {
                var fullGenerationDirectory = Path.GetFullPath(generationDirectory);
                if (!IsPathWithin(fullServiceDirectory, fullGenerationDirectory) ||
                    !IsPathWithoutReparsePoints(fullServiceDirectory, fullGenerationDirectory) ||
                    protectedGenerationDirectories.Contains(fullGenerationDirectory) ||
                    (activeArtifactServiceDirectories.Contains(fullServiceDirectory) &&
                        !confirmedRunningServiceDirectories.Contains(fullServiceDirectory)) ||
                    referencedPaths.Any(path => IsPathWithinOrEqual(fullGenerationDirectory, path)))
                {
                    continue;
                }

                TryDeletePath(fullGenerationDirectory);
            }

            try
            {
                if (Directory.Exists(fullServiceDirectory) &&
                    !Directory.EnumerateFileSystemEntries(fullServiceDirectory).Any())
                {
                    Directory.Delete(fullServiceDirectory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            if (Directory.Exists(sha256Root) && !Directory.EnumerateFileSystemEntries(sha256Root).Any())
            {
                Directory.Delete(sha256Root);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ComposeFile? TryParseForCleanup(SvchostConfigSettings? config)
    {
        if (config is null)
        {
            return null;
        }

        try
        {
            return new ComposeFileParser().Parse(config.Yaml ?? string.Empty);
        }
        catch (ComposeValidationException)
        {
            return null;
        }
    }

    private static bool TryGetArtifactLocation(
        string fileName,
        string svchostRoot,
        out ArtifactLocation location)
    {
        location = null!;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(fileName);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!IsPathWithin(svchostRoot, fullPath))
        {
            return false;
        }

        var generationDirectory = Path.GetDirectoryName(fullPath);
        var serviceDirectory = generationDirectory is null ? null : Path.GetDirectoryName(generationDirectory);
        var sha256Root = serviceDirectory is null ? null : Path.GetDirectoryName(serviceDirectory);
        var artifactRoot = sha256Root is null ? null : Path.GetDirectoryName(sha256Root);
        if (generationDirectory is not null &&
            serviceDirectory is not null &&
            sha256Root is not null &&
            artifactRoot is not null &&
            string.Equals(Path.GetFileName(sha256Root), "sha256", StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(artifactRoot), "artifacts", StringComparison.Ordinal) &&
            IsValidDigest(Path.GetFileName(generationDirectory)))
        {
            var configRoot = Path.GetDirectoryName(artifactRoot);
            var serviceName = Path.GetFileName(serviceDirectory);
            if (configRoot is not null && IsValidServiceName(serviceName))
            {
                location = new ArtifactLocation(
                    configRoot,
                    serviceName,
                    generationDirectory,
                    generationDirectory,
                    true);
                return true;
            }
        }

        var parent = Path.GetDirectoryName(fullPath);
        var parentName = parent is null ? null : Path.GetFileName(parent);
        var legacyArtifactRoot = parentName == "artifacts"
            ? parent
            : parent is null ? null : Path.GetDirectoryName(parent);
        var legacyServiceName = parentName == "artifacts"
            ? Path.GetFileName(fullPath)
            : parentName;
        if (legacyArtifactRoot is null ||
            legacyServiceName is null ||
            !string.Equals(Path.GetFileName(legacyArtifactRoot), "artifacts", StringComparison.Ordinal) ||
            !IsValidServiceName(legacyServiceName))
        {
            return false;
        }

        var legacyConfigRoot = Path.GetDirectoryName(legacyArtifactRoot);
        if (legacyConfigRoot is null)
        {
            return false;
        }

        var legacyEntry = Path.Combine(legacyArtifactRoot, legacyServiceName);
        location = new ArtifactLocation(
            legacyConfigRoot,
            legacyServiceName,
            legacyEntry,
            null,
            false);
        return true;
    }

    private static bool IsContentAddressedFile(
        string fileName,
        string configRoot,
        string serviceName,
        string? digest) =>
        IsValidDigest(digest) &&
        TryGetArtifactLocation(
            fileName,
            Path.GetFullPath(Path.Combine(configRoot, "..")),
            out var location) &&
        location.IsContentAddressed &&
        PathEquals(location.ConfigRoot, configRoot) &&
        string.Equals(location.ServiceName, serviceName, StringComparison.Ordinal) &&
        string.Equals(
            Path.GetFileName(location.GenerationDirectory),
            digest,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasGlobalSiblingDeclaration(
        string serviceName,
        IReadOnlyDictionary<string, HashSet<string>> globalConfigsByServiceName,
        IReadOnlyCollection<RetiringServiceSettings> removedRetirements)
    {
        if (!globalConfigsByServiceName.TryGetValue(serviceName, out var currentConfigs) ||
            currentConfigs.Count == 0)
        {
            return false;
        }

        var removedConfigNames = removedRetirements
            .Where(retirement => retirement.Scope == "global" &&
                                 string.Equals(retirement.ServiceName, serviceName, StringComparison.Ordinal))
            .Select(retirement => retirement.ConfigName)
            .ToHashSet(StringComparer.Ordinal);
        return removedConfigNames.Count > 0
            ? currentConfigs.Any(configName => !removedConfigNames.Contains(configName))
            : currentConfigs.Count > 1;
    }

    private static bool IsKnownInactive(
        IReadOnlyDictionary<Guid, ExtensionServiceRuntimeSnapshot> runtimeById,
        Guid serviceId) =>
        runtimeById.TryGetValue(serviceId, out var runtime) && IsInactive(runtime.LifecycleState);

    private static bool IsInactive(ExtensionServiceLifecycleState state) =>
        state is ExtensionServiceLifecycleState.Disabled or
            ExtensionServiceLifecycleState.Stopped or
            ExtensionServiceLifecycleState.Failed;

    private static void AddPath(HashSet<string> paths, string path)
    {
        try
        {
            paths.Add(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
        }
    }


    private static bool IsPathWithinOrEqual(string parentDirectory, string candidate)
    {
        try
        {
            var parent = Path.GetFullPath(parentDirectory);
            var path = Path.GetFullPath(candidate);
            return PathEquals(parent, path) || IsPathWithin(parent, path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static bool IsPathWithoutReparsePoints(string rootDirectory, string candidate)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var fullPath = Path.GetFullPath(candidate);
            if (!PathEquals(root, fullPath) && !IsPathWithin(root, fullPath))
            {
                return false;
            }

            var current = root;
            if (HasReparsePoint(current))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(root, fullPath);
            if (relativePath == ".")
            {
                return true;
            }

            foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    return false;
                }

                current = Path.Combine(current, segment);
                if (HasReparsePoint(current))
                {
                    return false;
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ContainsReparsePointInTree(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Push(entry);
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (ContainsReparsePointInTree(path))
            {
                Debug.WriteLine($"{Owner}: artifact cleanup skipped for linked path '{path}'.");
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"{Owner}: artifact cleanup failed for '{path}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"{Owner}: artifact cleanup failed for '{path}': {exception.Message}");
        }
    }

    private static bool IsValidDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(Uri.IsHexDigit);


    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
