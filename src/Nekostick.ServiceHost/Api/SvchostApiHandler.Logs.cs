using Nekolla.Nekostick.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Logs;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.Api;

public sealed partial class SvchostApiHandler
{
    private const int LiveLogChannelCapacity = 1024;
    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EventStreamHeaders =
        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new[] { "text/event-stream; charset=utf-8" },
            ["Cache-Control"] = new[] { "no-cache" }
        };
    private readonly ServiceLogReader _serviceLogReader = new();
    private readonly ServiceLogRecorder? _logRecorder;

    private async ValueTask<ExtensionStreamingResponse> HandleServiceLogPageAsync(
        string configName,
        string serviceName,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveServiceLogTargetAsync(configName, serviceName, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryReadQueryInteger(pathAndQuery, "file", defaultValue: 0, out var fileIndex))
        {
            return Error(400, "invalid_query", "The file query parameter must be a non-negative integer.");
        }

        var target = resolution.Target!;
        var page = _serviceLogReader.ReadPage(target.LogDirectory, target.ServiceName, fileIndex);
        if (page is null)
        {
            return Error(404, "not_found", "The requested service log file was not found.");
        }

        return JsonResponse(
            200,
            new
            {
                service = target.ServiceName,
                file = page.FileIndex,
                fileCount = page.FileCount,
                lineCount = page.LineCount,
                lines = page.Lines
            });
    }

    private async ValueTask<ExtensionStreamingResponse> HandleServiceLogTailAsync(
        string configName,
        string serviceName,
        string pathAndQuery,
        CancellationToken cancellationToken,
        CancellationToken requestCancellationToken)
    {
        var resolution = await ResolveServiceLogTargetAsync(configName, serviceName, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryReadQueryInteger(pathAndQuery, "fromLine", defaultValue: 0, out var fromLine))
        {
            return Error(400, "invalid_query", "The fromLine query parameter must be a non-negative integer.");
        }

        var target = resolution.Target!;
        var liveLines = Channel.CreateBounded<string>(
            new BoundedChannelOptions(LiveLogChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        IDisposable? subscription = null;
        try
        {
            if (_logRecorder is not null)
            {
                subscription = _logRecorder.SubscribeLiveLines(
                    target.ServiceId,
                    line => liveLines.Writer.TryWrite(line));
            }

            var replayLines = _serviceLogReader.ReadPage(target.LogDirectory, target.ServiceName, 0)?.Lines
                ?? Array.Empty<string>();
            if (subscription is null)
            {
                liveLines.Writer.TryComplete();
            }

            var stream = new ServiceLogTailStream(
                replayLines,
                fromLine,
                liveLines,
                subscription,
                requestCancellationToken,
                _lifetimeCancellation.Token);
            subscription = null;
            return new ExtensionStreamingResponse(200, EventStreamHeaders, stream);
        }
        finally
        {
            if (subscription is not null)
            {
                liveLines.Writer.TryComplete();
                subscription.Dispose();
            }
        }
    }

    private async ValueTask<(ServiceLogTarget? Target, ExtensionStreamingResponse? Error)> ResolveServiceLogTargetAsync(
        string configName,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (!IsValidConfigName(configName) || !IsValidName(serviceName))
        {
            return (null, Error(404, "not_found", "The service was not found."));
        }

        var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return (null, read.Error);
        }

        if (!read.Settings!.Configs.TryGetValue(configName, out var config) || config is null)
        {
            return (null, Error(404, "not_found", "The configuration was not found."));
        }

        var lockedServices = config.Lock?.Services;
        if (lockedServices is null ||
            !lockedServices.TryGetValue(serviceName, out var lockEntry) ||
            lockEntry is null)
        {
            return (null, Error(404, "not_found", "The service was not found."));
        }

        ComposeFile compose;
        try
        {
            compose = _composeFileParser.Parse(config.Yaml ?? string.Empty);
        }
        catch (ComposeValidationException exception)
        {
            return (null, Error(422, "validation", exception.Message));
        }

        var logDirectory = string.IsNullOrWhiteSpace(_bridge.DataDirectory)
            ? string.Empty
            : Path.Combine(
                ServiceRootPath.Resolve(_bridge.DataDirectory, compose.ServiceScope, configName),
                "logs");
        return (new ServiceLogTarget(serviceName, lockEntry.ServiceId, logDirectory), null);
    }

    private static bool TryReadQueryInteger(
        string pathAndQuery,
        string parameterName,
        int defaultValue,
        out int value)
    {
        value = defaultValue;
        var queryStart = pathAndQuery.IndexOf('?');
        if (queryStart < 0)
        {
            return true;
        }

        var query = pathAndQuery.AsSpan(queryStart + 1);
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
        {
            query = query[..fragmentStart];
        }

        while (!query.IsEmpty)
        {
            var separator = query.IndexOf('&');
            var component = separator < 0 ? query : query[..separator];
            var equals = component.IndexOf('=');
            var encodedName = equals < 0 ? component : component[..equals];
            string decodedName;
            try
            {
                decodedName = Uri.UnescapeDataString(encodedName.ToString().Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (decodedName.Equals(parameterName, StringComparison.Ordinal))
            {
                if (equals < 0)
                {
                    return false;
                }

                string decodedValue;
                try
                {
                    decodedValue = Uri.UnescapeDataString(component[(equals + 1)..].ToString().Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return false;
                }

                return int.TryParse(
                           decodedValue,
                           NumberStyles.None,
                           CultureInfo.InvariantCulture,
                           out value) &&
                       value >= 0;
            }

            if (separator < 0)
            {
                break;
            }

            query = query[(separator + 1)..];
        }

        return true;
    }

    private sealed record ServiceLogTarget(string ServiceName, Guid ServiceId, string LogDirectory);

    private sealed class ServiceLogTailStream : Stream
    {
        private static readonly byte[] LinePrefix = Encoding.UTF8.GetBytes("data: {\"line\":");
        private static readonly byte[] LineSuffix = Encoding.UTF8.GetBytes("}\n\n");
        private static readonly byte[] Heartbeat = Encoding.UTF8.GetBytes(": ping\n\n");
        private readonly IReadOnlyList<string> _replayLines;
        private readonly Channel<string> _liveLines;
        private readonly ChannelReader<string> _liveReader;
        private readonly CancellationTokenSource _streamCancellation;
        private readonly PeriodicTimer? _heartbeatTimer;
        private IDisposable? _subscription;
        private CancellationTokenRegistration _cancellationRegistration;
        private Task<bool>? _channelReady;
        private Task<bool>? _heartbeatReady;
        private byte[]? _linePayload;
        private int _replayIndex;
        private int _linePrefixOffset;
        private int _linePayloadOffset;
        private int _lineSuffixOffset;
        private int _heartbeatOffset = -1;
        private bool _hasLive;
        private int _disposed;

        public ServiceLogTailStream(
            IReadOnlyList<string> replayLines,
            int fromLine,
            Channel<string> liveLines,
            IDisposable? subscription,
            CancellationToken requestCancellationToken,
            CancellationToken handlerCancellationToken)
        {
            _replayLines = replayLines;
            _replayIndex = Math.Min(fromLine, replayLines.Count);
            _liveLines = liveLines;
            _liveReader = liveLines.Reader;
            _subscription = subscription;
            _hasLive = subscription is not null;
            _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellationToken,
                handlerCancellationToken);
            if (_hasLive)
            {
                _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                _cancellationRegistration = _streamCancellation.Token.Register(
                    static state => ((ServiceLogTailStream)state!).StopLiveSubscription(),
                    this);
            }
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) 
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            try
            {
                while (true)
                {
                    if (_streamCancellation.IsCancellationRequested)
                    {
                        StopLiveSubscription();
                        return 0;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var copied = CopyPendingEvent(buffer.Span);
                    if (copied > 0)
                    {
                        return copied;
                    }

                    if (_replayIndex < _replayLines.Count)
                    {
                        _linePayload = JsonSerializer.SerializeToUtf8Bytes(
                            _replayLines[_replayIndex++],
                            JsonOptions);
                        continue;
                    }

                    if (!_hasLive || !await WaitForLiveEventAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return 0;
                    }
                }
            }
            catch (OperationCanceledException) when (_streamCancellation.IsCancellationRequested)
            {
                StopLiveSubscription();
                return 0;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellationRegistration.Dispose();
                StopLiveSubscription();
                _streamCancellation.Cancel();
                _heartbeatTimer?.Dispose();
                _streamCancellation.Dispose();
            }

            base.Dispose(disposing);
        }

        private async ValueTask<bool> WaitForLiveEventAsync(CancellationToken readCancellationToken)
        {
            while (true)
            {
                if (TryReadLiveLine())
                {
                    return true;
                }

                _channelReady ??= _liveReader.WaitToReadAsync(_streamCancellation.Token).AsTask();
                _heartbeatReady ??= _heartbeatTimer!.WaitForNextTickAsync(_streamCancellation.Token).AsTask();
                var pendingRead = Task.WhenAny(_channelReady, _heartbeatReady);
                var ready = readCancellationToken.CanBeCanceled &&
                            readCancellationToken != _streamCancellation.Token
                    ? await pendingRead.WaitAsync(readCancellationToken).ConfigureAwait(false)
                    : await pendingRead.ConfigureAwait(false);

                if (ready == _heartbeatReady)
                {
                    if (!await _heartbeatReady.ConfigureAwait(false))
                    {
                        _hasLive = false;
                        return false;
                    }

                    _heartbeatReady = _heartbeatTimer!.WaitForNextTickAsync(_streamCancellation.Token).AsTask();
                    if (TryReadLiveLine())
                    {
                        return true;
                    }

                    _heartbeatOffset = 0;
                    return true;
                }

                if (!await _channelReady.ConfigureAwait(false))
                {
                    _channelReady = null;
                    _hasLive = false;
                    return false;
                }

                _channelReady = null;
            }
        }

        private bool TryReadLiveLine()
        {
            if (!_liveReader.TryRead(out var line))
            {
                return false;
            }

            _linePrefixOffset = 0;
            _linePayloadOffset = 0;
            _lineSuffixOffset = 0;
            _linePayload = JsonSerializer.SerializeToUtf8Bytes(line, JsonOptions);
            return true;
        }

        private int CopyPendingEvent(Span<byte> destination)
        {
            if (_heartbeatOffset >= 0)
            {
                var copied = CopySegment(Heartbeat, ref _heartbeatOffset, destination, 0);
                if (_heartbeatOffset == Heartbeat.Length)
                {
                    _heartbeatOffset = -1;
                }

                return copied;
            }

            if (_linePayload is null)
            {
                return 0;
            }

            var written = 0;
            written += CopySegment(LinePrefix, ref _linePrefixOffset, destination, written);
            if (written < destination.Length && _linePrefixOffset == LinePrefix.Length)
            {
                written += CopySegment(_linePayload, ref _linePayloadOffset, destination, written);
            }

            if (written < destination.Length && _linePayloadOffset == _linePayload.Length)
            {
                written += CopySegment(LineSuffix, ref _lineSuffixOffset, destination, written);
            }

            if (_lineSuffixOffset == LineSuffix.Length)
            {
                _linePayload = null;
                _linePrefixOffset = 0;
                _linePayloadOffset = 0;
                _lineSuffixOffset = 0;
            }

            return written;
        }

        private static int CopySegment(
            byte[] source,
            ref int sourceOffset,
            Span<byte> destination,
            int destinationOffset)
        {
            var count = Math.Min(source.Length - sourceOffset, destination.Length - destinationOffset);
            if (count <= 0)
            {
                return 0;
            }

            source.AsSpan(sourceOffset, count).CopyTo(destination[destinationOffset..]);
            sourceOffset += count;
            return count;
        }

        private void StopLiveSubscription()
        {
            _liveLines.Writer.TryComplete();
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
        }
    }
}
