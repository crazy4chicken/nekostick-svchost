using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SourceResolverTests
{
    [Fact]
    public async Task Resolve_local_path_first_copies_artifact_and_creates_lock()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.bin");
            var dataDirectory = Path.Combine(root, "data");
            await File.WriteAllTextAsync(sourcePath, "version-one");
            var resolver = new SourceResolver();

            var result = await resolver.ResolveAsync(
                dataDirectory,
                "demo",
                "api",
                new ComposeSource(null, sourcePath),
                null);

            Assert.True(result.Succeeded);
            Assert.False(result.Reused);
            Assert.NotNull(result.ArtifactPath);
            Assert.True(File.Exists(result.ArtifactPath));
            Assert.Equal("path", result.Source!.Kind);
            Assert.Equal(Path.GetFullPath(sourcePath), result.Source.Path);
            Assert.Equal(await File.ReadAllTextAsync(sourcePath), await File.ReadAllTextAsync(result.ArtifactPath));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_local_path_installs_artifact_with_executable_bit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.bin");
            var dataDirectory = Path.Combine(root, "data");
            await File.WriteAllTextAsync(sourcePath, "binary");
            var resolver = new SourceResolver();

            var result = await resolver.ResolveAsync(
                dataDirectory,
                "demo",
                "api",
                new ComposeSource(null, sourcePath),
                null);

            Assert.True(result.Succeeded);
            var mode = File.GetUnixFileMode(result.ArtifactPath!);
            Assert.True(
                mode.HasFlag(UnixFileMode.UserExecute),
                $"artifact must be executable, actual mode: {mode}");
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_local_path_with_unchanged_file_reuses_existing_artifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.bin");
            var dataDirectory = Path.Combine(root, "data");
            await File.WriteAllTextAsync(sourcePath, "version-one");
            var resolver = new SourceResolver();
            var source = new ComposeSource(null, sourcePath);
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, null);

            var second = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, first.Source);

            Assert.True(second.Succeeded);
            Assert.True(second.Reused);
            Assert.Equal(first.ArtifactPath, second.ArtifactPath);
            Assert.Equal(first.Source!.Sha256, second.Source!.Sha256);
            Assert.Equal(first.Source.FetchedAt, second.Source.FetchedAt);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_local_path_drift_copies_new_artifact_and_relocks()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.bin");
            var dataDirectory = Path.Combine(root, "data");
            await File.WriteAllTextAsync(sourcePath, "version-one");
            var resolver = new SourceResolver();
            var source = new ComposeSource(null, sourcePath);
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, null);
            await File.WriteAllTextAsync(sourcePath, "version-two");

            var drifted = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, first.Source);

            Assert.True(drifted.Succeeded);
            Assert.False(drifted.Reused);
            Assert.NotEqual(first.Source!.Sha256, drifted.Source!.Sha256);
            Assert.Equal("version-two", await File.ReadAllTextAsync(drifted.ArtifactPath!));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_local_path_rejects_declared_sha256_mismatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.bin");
            await File.WriteAllTextAsync(sourcePath, "version-one");
            var resolver = new SourceResolver();
            var wrongDigest = new string('0', 64);

            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(null, sourcePath, wrongDigest),
                null);

            Assert.False(result.Succeeded);
            Assert.Contains("declared sha256", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.ArtifactPath);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nekostick-svchost-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
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
