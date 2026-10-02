using System.Globalization;
using System.Text;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Logs;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class ServiceLogWriterTests
{
    private static readonly DateTimeOffset Timestamp = new(2025, 6, 7, 8, 9, 10, 321, TimeSpan.FromHours(-4));

    [Fact]
    public void Append_assembles_lines_across_split_chunks()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes("hello "));
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes("world\nnext"));
            }

            var lines = File.ReadAllLines(Path.Combine(logDirectory, "worker.log"));
            Assert.Equal(
                new[]
                {
                    FormatLine("stdout", "hello world"),
                    FormatLine("stdout", "next")
                },
                lines);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Append_notifies_for_each_completed_line_with_formatted_content()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            var notified = new List<string>();
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.LineWritten = notified.Add;
                writer.Append(
                    ExtensionServiceOutputStream.Stdout,
                    Timestamp,
                    Encoding.UTF8.GetBytes("first\nsecond\n"));
            }

            var expected = new[]
            {
                FormatLine("stdout", "first"),
                FormatLine("stdout", "second")
            };
            Assert.Equal(expected, notified);
            Assert.Equal(expected, File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void FlushPartial_notifies_with_formatted_content()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            var notified = new List<string>();
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.LineWritten = notified.Add;
                writer.Append(ExtensionServiceOutputStream.Stderr, Timestamp, Encoding.UTF8.GetBytes("partial"));
                writer.FlushPartial();
            }

            var expected = new[] { FormatLine("stderr", "partial") };
            Assert.Equal(expected, notified);
            Assert.Equal(expected, File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Append_throwing_line_callback_does_not_break_writing()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.LineWritten = _ => throw new InvalidOperationException("callback failed");
                writer.Append(
                    ExtensionServiceOutputStream.Stdout,
                    Timestamp,
                    Encoding.UTF8.GetBytes("first\nsecond\n"));
            }

            Assert.Equal(
                new[]
                {
                    FormatLine("stdout", "first"),
                    FormatLine("stdout", "second")
                },
                File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void AppendMarker_writes_lifecycle_markers_with_svchost_format()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            var notified = new List<string>();
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.LineWritten = notified.Add;
                writer.AppendMarker("output stream attached (stdout)");
                writer.AppendMarker("output stream ended: process-exited (stdout)");
            }

            var lines = File.ReadAllLines(Path.Combine(logDirectory, "worker.log"));
            Assert.Equal(2, lines.Length);
            Assert.Equal(lines, notified);
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[svchost\] output stream attached \(stdout\)$",
                lines[0]);
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[svchost\] output stream ended: process-exited \(stdout\)$",
                lines[1]);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Append_keeps_stdout_and_stderr_partial_lines_separate()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes("out"));
                writer.Append(ExtensionServiceOutputStream.Stderr, Timestamp, Encoding.UTF8.GetBytes("err"));
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes("put\n"));
                writer.Append(ExtensionServiceOutputStream.Stderr, Timestamp, Encoding.UTF8.GetBytes("or\n"));
            }

            var lines = File.ReadAllLines(Path.Combine(logDirectory, "worker.log"));
            Assert.Equal(
                new[]
                {
                    FormatLine("stdout", "output"),
                    FormatLine("stderr", "error")
                },
                lines);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Append_rotates_when_the_current_file_reaches_1000_lines()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                var text = string.Concat(Enumerable.Range(1, 1000).Select(index => $"line-{index}\n"));
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes(text));
            }

            Assert.Equal(1000, File.ReadAllLines(Path.Combine(logDirectory, "worker.1.log")).Length);
            Assert.Empty(File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));
            Assert.False(File.Exists(Path.Combine(logDirectory, "worker.2.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Append_caps_rotation_at_five_files_and_deletes_the_oldest()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                var text = string.Concat(Enumerable.Range(1, 6000).Select(index => $"line-{index}\n"));
                writer.Append(ExtensionServiceOutputStream.Stdout, Timestamp, Encoding.UTF8.GetBytes(text));
            }

            Assert.Equal(5, Directory.GetFiles(logDirectory).Length);
            Assert.Empty(File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));

            var oldestRetained = File.ReadAllLines(Path.Combine(logDirectory, "worker.4.log"));
            Assert.True(oldestRetained[0].EndsWith("line-2001", StringComparison.Ordinal));
            Assert.True(oldestRetained[^1].EndsWith("line-3000", StringComparison.Ordinal));
            Assert.DoesNotContain("line-1000", File.ReadAllText(Path.Combine(logDirectory, "worker.4.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Constructor_counts_lines_in_an_existing_current_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllLines(
                Path.Combine(logDirectory, "worker.log"),
                Enumerable.Range(1, 999).Select(index => FormatLine("stdout", $"line-{index}")));

            using (var writer = new ServiceLogWriter(logDirectory, "worker"))
            {
                writer.Append(
                    ExtensionServiceOutputStream.Stdout,
                    Timestamp,
                    Encoding.UTF8.GetBytes("line-1000\n"));
            }

            var rotated = File.ReadAllLines(Path.Combine(logDirectory, "worker.1.log"));
            Assert.Equal(1000, rotated.Length);
            Assert.True(rotated[0].EndsWith("line-1", StringComparison.Ordinal));
            Assert.True(rotated[^1].EndsWith("line-1000", StringComparison.Ordinal));
            Assert.Empty(File.ReadAllLines(Path.Combine(logDirectory, "worker.log")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string FormatLine(string stream, string text) =>
        $"{Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} [{stream}] {text}";

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
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
