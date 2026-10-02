using System.Globalization;
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Logs;

/// <summary>Writes one service's output to a bounded set of rotating UTF-8 log files.</summary>
public sealed class ServiceLogWriter : IDisposable
{
    private const int LinesPerFile = 1000;
    private const int CharacterBufferSize = 4096;
    private static readonly UTF8Encoding Utf8Encoding = new(false, false);

    private readonly string _logDirectory;
    private readonly string _currentPath;
    private readonly string[] _rotatedPaths;
    private readonly char[] _characterBuffer = new char[CharacterBufferSize];
    private readonly StreamBuffer _stdout = new(Utf8Encoding.GetDecoder());
    private readonly StreamBuffer _stderr = new(Utf8Encoding.GetDecoder());

    private StreamWriter? _writer;
    private int _currentLineCount;
    private bool _needsLineSeparator;
    private bool _disposed;

    /// <summary>Creates a writer that appends to the current service log when first used.</summary>
    public ServiceLogWriter(string logDirectory, string serviceName)
    {
        _logDirectory = logDirectory;
        _currentPath = Path.Combine(logDirectory, $"{serviceName}.log");
        _rotatedPaths = new string[4];
        for (var index = 0; index < _rotatedPaths.Length; index++)
        {
            _rotatedPaths[index] = Path.Combine(logDirectory, $"{serviceName}.{index + 1}.log");
        }

        CountExistingLines();
    }

    /// <summary>Reports each formatted log line after it has been written.</summary>
    public Action<string>? LineWritten { get; set; }

    /// <summary>Appends a chunk, preserving any unfinished line separately for each stream.</summary>
    public void Append(ExtensionServiceOutputStream stream, DateTimeOffset timestamp, ReadOnlySpan<byte> data)
    {
        if (_disposed)
        {
            return;
        }

        var buffer = GetBuffer(stream);
        if (buffer is null)
        {
            return;
        }

        try
        {
            buffer.LastTimestamp = timestamp;
            buffer.HasLastTimestamp = true;

            var remaining = data;
            while (!remaining.IsEmpty)
            {
                buffer.Decoder.Convert(
                    remaining,
                    _characterBuffer,
                    flush: false,
                    out var bytesUsed,
                    out var charactersUsed,
                    out _);

                if (charactersUsed > 0)
                {
                    AppendCharacters(buffer, stream, timestamp, _characterBuffer.AsSpan(0, charactersUsed));
                }

                remaining = remaining[bytesUsed..];
                if (bytesUsed == 0 && charactersUsed == 0)
                {
                    break;
                }
            }
        }
        catch
        {
            ResetWriter();
        }
    }

    /// <summary>Writes a marker describing bytes of output dropped by the host or recorder.</summary>
    public void AppendDropped(long byteCount)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var text = $"... dropped {byteCount.ToString(CultureInfo.InvariantCulture)} bytes of output ...";
            AppendMarker(text);
        }
        catch
        {
            ResetWriter();
        }
    }

    /// <summary>Writes an svchost marker line.</summary>
    internal void AppendMarker(string text)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            WriteLine(DateTimeOffset.UtcNow, "svchost", text);
        }
        catch
        {
            ResetWriter();
        }
    }

    /// <summary>Writes buffered partial lines and finalizes incomplete UTF-8 sequences.</summary>
    public void FlushPartial()
    {
        if (_disposed)
        {
            return;
        }

        FlushStream(_stdout, ExtensionServiceOutputStream.Stdout);
        FlushStream(_stderr, ExtensionServiceOutputStream.Stderr);

        try
        {
            _writer?.Flush();
        }
        catch
        {
            ResetWriter();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPartial();
        _disposed = true;
        CloseWriter();
    }

    private void CountExistingLines()
    {
        try
        {
            using var stream = new FileStream(
                _currentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                var lastByte = stream.ReadByte();
                _needsLineSeparator = lastByte is not '\n' and not '\r';
                stream.Position = 0;
            }

            using var reader = new StreamReader(stream, Utf8Encoding, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is not null)
            {
                _currentLineCount++;
            }
        }
        catch
        {
            _currentLineCount = 0;
            _needsLineSeparator = false;
        }
    }

    private StreamBuffer? GetBuffer(ExtensionServiceOutputStream stream) => stream switch
    {
        ExtensionServiceOutputStream.Stdout => _stdout,
        ExtensionServiceOutputStream.Stderr => _stderr,
        _ => null
    };

    private void AppendCharacters(
        StreamBuffer buffer,
        ExtensionServiceOutputStream stream,
        DateTimeOffset timestamp,
        ReadOnlySpan<char> characters)
    {
        foreach (var character in characters)
        {
            if (character == '\n')
            {
                var lineTimestamp = buffer.HasLineTimestamp ? buffer.LineTimestamp : timestamp;
                TrimTrailingCarriageReturn(buffer.Line);
                WriteLine(lineTimestamp, GetStreamName(stream), buffer.Line);
                buffer.Line.Clear();
                buffer.HasLineTimestamp = false;
                continue;
            }

            if (!buffer.HasLineTimestamp)
            {
                buffer.LineTimestamp = timestamp;
                buffer.HasLineTimestamp = true;
            }

            buffer.Line.Append(character);
        }
    }

    private void FlushStream(StreamBuffer buffer, ExtensionServiceOutputStream stream)
    {
        var timestamp = buffer.HasLineTimestamp
            ? buffer.LineTimestamp
            : buffer.HasLastTimestamp
                ? buffer.LastTimestamp
                : DateTimeOffset.UtcNow;

        try
        {
            buffer.Decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                _characterBuffer,
                flush: true,
                out _,
                out var charactersUsed,
                out _);

            if (charactersUsed > 0)
            {
                AppendCharacters(buffer, stream, timestamp, _characterBuffer.AsSpan(0, charactersUsed));
            }

            buffer.Decoder.Reset();

            if (buffer.Line.Length > 0)
            {
                TrimTrailingCarriageReturn(buffer.Line);
                if (buffer.Line.Length > 0)
                {
                    WriteLine(
                        buffer.HasLineTimestamp ? buffer.LineTimestamp : timestamp,
                        GetStreamName(stream),
                        buffer.Line);
                }

                buffer.Line.Clear();
            }

            buffer.HasLineTimestamp = false;
            buffer.HasLastTimestamp = false;
        }
        catch
        {
            buffer.Decoder.Reset();
            buffer.Line.Clear();
            buffer.HasLineTimestamp = false;
            buffer.HasLastTimestamp = false;
            ResetWriter();
        }
    }

    private void WriteLine(DateTimeOffset timestamp, string streamName, string text)
    {
        try
        {
            if (!PrepareForLine())
            {
                return;
            }

            EnsureWriter();

            var lineWritten = LineWritten;
            string? formattedLine = null;
            if (lineWritten is null)
            {
                _writer!.Write(FormatTimestamp(timestamp));
                _writer.Write(" [");
                _writer.Write(streamName);
                _writer.Write("] ");
                _writer.Write(text);
                _writer.WriteLine();
            }
            else
            {
                formattedLine = FormatLine(timestamp, streamName, text);
                _writer!.WriteLine(formattedLine);
            }

            _writer.Flush();
            _currentLineCount++;

            if (lineWritten is not null)
            {
                InvokeLineWritten(lineWritten, formattedLine!);
            }

            if (_currentLineCount >= LinesPerFile)
            {
                RotateFiles();
            }
        }
        catch
        {
            ResetWriter();
        }
    }

    private void WriteLine(DateTimeOffset timestamp, string streamName, StringBuilder text)
    {
        try
        {
            if (!PrepareForLine())
            {
                return;
            }

            EnsureWriter();

            var lineWritten = LineWritten;
            string? formattedLine = null;
            if (lineWritten is null)
            {
                _writer!.Write(FormatTimestamp(timestamp));
                _writer.Write(" [");
                _writer.Write(streamName);
                _writer.Write("] ");
                foreach (var chunk in text.GetChunks())
                {
                    _writer.Write(chunk.Span);
                }

                _writer.WriteLine();
            }
            else
            {
                formattedLine = FormatLine(timestamp, streamName, text);
                _writer!.WriteLine(formattedLine);
            }

            _writer.Flush();
            _currentLineCount++;

            if (lineWritten is not null)
            {
                InvokeLineWritten(lineWritten, formattedLine!);
            }

            if (_currentLineCount >= LinesPerFile)
            {
                RotateFiles();
            }
        }
        catch
        {
            ResetWriter();
        }
    }

    private static string FormatLine(DateTimeOffset timestamp, string streamName, string text)
    {
        var timestampText = FormatTimestamp(timestamp);
        return string.Create(
            timestampText.Length + streamName.Length + text.Length + 4,
            (Timestamp: timestampText, StreamName: streamName, Text: text),
            static (destination, state) =>
            {
                var offset = 0;
                state.Timestamp.AsSpan().CopyTo(destination[offset..]);
                offset += state.Timestamp.Length;
                " [".AsSpan().CopyTo(destination[offset..]);
                offset += 2;
                state.StreamName.AsSpan().CopyTo(destination[offset..]);
                offset += state.StreamName.Length;
                "] ".AsSpan().CopyTo(destination[offset..]);
                offset += 2;
                state.Text.AsSpan().CopyTo(destination[offset..]);
            });
    }

    private static string FormatLine(DateTimeOffset timestamp, string streamName, StringBuilder text)
    {
        var timestampText = FormatTimestamp(timestamp);
        return string.Create(
            timestampText.Length + streamName.Length + text.Length + 4,
            (Timestamp: timestampText, StreamName: streamName, Text: text),
            static (destination, state) =>
            {
                var offset = 0;
                state.Timestamp.AsSpan().CopyTo(destination[offset..]);
                offset += state.Timestamp.Length;
                " [".AsSpan().CopyTo(destination[offset..]);
                offset += 2;
                state.StreamName.AsSpan().CopyTo(destination[offset..]);
                offset += state.StreamName.Length;
                "] ".AsSpan().CopyTo(destination[offset..]);
                offset += 2;
                foreach (var chunk in state.Text.GetChunks())
                {
                    chunk.Span.CopyTo(destination[offset..]);
                    offset += chunk.Length;
                }
            });
    }

    private static void InvokeLineWritten(Action<string> lineWritten, string formattedLine)
    {
        try
        {
            lineWritten(formattedLine);
        }
        catch
        {
        }
    }

    private bool PrepareForLine() => _currentLineCount < LinesPerFile || RotateFiles();

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_logDirectory);
        var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        try
        {
            _writer = new StreamWriter(stream, Utf8Encoding, CharacterBufferSize);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        if (_needsLineSeparator)
        {
            _writer.WriteLine();
            _writer.Flush();
            _needsLineSeparator = false;
        }
    }

    private bool RotateFiles()
    {
        try
        {
            CloseWriter();
            File.Delete(_rotatedPaths[3]);
            for (var index = _rotatedPaths.Length - 1; index > 0; index--)
            {
                if (File.Exists(_rotatedPaths[index - 1]))
                {
                    File.Move(_rotatedPaths[index - 1], _rotatedPaths[index], overwrite: true);
                }
            }

            if (File.Exists(_currentPath))
            {
                File.Move(_currentPath, _rotatedPaths[0], overwrite: true);
            }

            _currentLineCount = 0;
            _needsLineSeparator = false;
            EnsureWriter();
            return true;
        }
        catch
        {
            CloseWriter();
            return false;
        }
    }

    private void ResetWriter() => CloseWriter();

    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Flush();
        }
        catch
        {
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
        }
    }

    private static void TrimTrailingCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }
    }

    private static string GetStreamName(ExtensionServiceOutputStream stream) => stream switch
    {
        ExtensionServiceOutputStream.Stdout => "stdout",
        ExtensionServiceOutputStream.Stderr => "stderr",
        _ => "stderr"
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class StreamBuffer(Decoder decoder)
    {
        public Decoder Decoder { get; } = decoder;
        public StringBuilder Line { get; } = new();
        public DateTimeOffset LineTimestamp { get; set; }
        public DateTimeOffset LastTimestamp { get; set; }
        public bool HasLineTimestamp { get; set; }
        public bool HasLastTimestamp { get; set; }
    }
}
