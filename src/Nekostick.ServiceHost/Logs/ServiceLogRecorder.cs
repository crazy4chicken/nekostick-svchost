using System.Collections.Concurrent;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Logs;

/// <summary>Subscribes to managed service output and serializes it through one logging pump.</summary>
public sealed class ServiceLogRecorder : IDisposable
{
    private const int ChannelCapacity = 4096;
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ImmediateResubscribeCooldown = TimeSpan.FromSeconds(2);

    private readonly IExtensionServiceOutputApi? _outputApi;
    private readonly IExtensionLogger? _logger;
    private readonly object _syncGate = new();
    private readonly object _liveLineGate = new();
    private readonly Dictionary<Guid, LiveLineSubscribers> _liveLineSubscribers = new();

    private readonly Dictionary<Guid, TargetState> _targets = new();
    private readonly Channel<PumpEvent> _events = Channel.CreateBounded<PumpEvent>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentQueue<ServiceOutputSink> _overflowSinks = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<Guid, WriterState> _writers = new();
    private readonly Task _pump;

    private bool _nullApiLogged;
    private bool _unsupportedLogged;
    private bool _featureDisabled;
    private int _disposeRequested;

    /// <summary>Creates a recorder for the host's optional service output API.</summary>
    public ServiceLogRecorder(IExtensionServiceOutputApi? outputApi, IExtensionLogger? logger)
    {
        _outputApi = outputApi;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Reconciles service subscriptions with the current managed service targets.</summary>
    public void SyncTargets(IReadOnlyList<ServiceLogTarget> targets)
    {
        try
        {
            lock (_syncGate)
            {
                if (Volatile.Read(ref _disposeRequested) != 0 || _featureDisabled)
                {
                    return;
                }

                if (_outputApi is null)
                {
                    LogNullApiOnce();
                    return;
                }

                SyncTargetsLocked(targets);
            }
        }
        catch
        {
        }
    }

    /// <summary>Subscribes to formatted lines written for a managed service.</summary>
    public IDisposable? SubscribeLiveLines(Guid serviceId, Action<string> onLine)
    {
        lock (_syncGate)
        {
            if (_outputApi is null || _featureDisabled || Volatile.Read(ref _disposeRequested) != 0)
            {
                return null;
            }

            var subscription = new LiveLineSubscription(this, serviceId, onLine);
            lock (_liveLineGate)
            {
                if (!_liveLineSubscribers.TryGetValue(serviceId, out var subscribers))
                {
                    subscribers = new LiveLineSubscribers();
                    _liveLineSubscribers.Add(serviceId, subscribers);
                }

                subscribers.Items.Add(subscription);
                subscribers.Snapshot = subscribers.Items.ToArray();
            }

            if (_targets.TryGetValue(serviceId, out var target))
            {
                EnqueueControlEvent(new PumpEvent(PumpEventKind.UpdateLiveLines, target));
            }

            return subscription;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch
        {
        }

        try
        {
            lock (_syncGate)
            {
                foreach (var target in _targets.Values)
                {
                    target.Deactivate();
                    DisposeSubscriptions(target);
                    EnqueueControlEvent(new PumpEvent(PumpEventKind.RemoveTarget, target));
                }

                _targets.Clear();
                _events.Writer.TryComplete();
                WakePump();
            }
        }
        catch
        {
            _events.Writer.TryComplete();
            WakePump();
        }

        try
        {
            _pump.Wait(DisposeDrainTimeout);
        }
        catch
        {
        }

        try
        {
            _lifetimeCancellation.Dispose();
        }
        catch
        {
        }
    }

    private void SyncTargetsLocked(IReadOnlyList<ServiceLogTarget> targets)
    {
        var desiredTargets = new Dictionary<Guid, ServiceLogTarget>();
        foreach (var target in targets)
        {
            if (target is not null)
            {
                desiredTargets[target.ServiceId] = target;
            }
        }

        var removedTargets = new List<TargetState>();
        foreach (var target in _targets.Values)
        {
            if (!desiredTargets.TryGetValue(target.Target.ServiceId, out var desired) ||
                desired != target.Target)
            {
                removedTargets.Add(target);
            }
        }

        foreach (var target in removedTargets)
        {
            RemoveTarget(target);
        }

        foreach (var target in desiredTargets.Values)
        {
            if (!_targets.TryGetValue(target.ServiceId, out var state))
            {
                state = new TargetState(target);
                _targets.Add(target.ServiceId, state);
            }

            if (!state.Stdout.IsOpen)
            {
                Subscribe(state, state.Stdout, ExtensionServiceOutputStream.Stdout);
                if (_featureDisabled || Volatile.Read(ref _disposeRequested) != 0)
                {
                    return;
                }
            }

            if (!state.Stderr.IsOpen)
            {
                Subscribe(state, state.Stderr, ExtensionServiceOutputStream.Stderr);
                if (_featureDisabled || Volatile.Read(ref _disposeRequested) != 0)
                {
                    return;
                }
            }
        }
    }

    private void UnsubscribeLiveLines(Guid serviceId, LiveLineSubscription subscription)
    {
        lock (_liveLineGate)
        {
            if (!_liveLineSubscribers.TryGetValue(serviceId, out var subscribers) ||
                !subscribers.Items.Remove(subscription))
            {
                return;
            }

            if (subscribers.Items.Count == 0)
            {
                _liveLineSubscribers.Remove(serviceId);
            }
            else
            {
                subscribers.Snapshot = subscribers.Items.ToArray();
                return;
            }
        }

        lock (_syncGate)
        {
            if (Volatile.Read(ref _disposeRequested) == 0 &&
                _targets.TryGetValue(serviceId, out var target))
            {
                EnqueueControlEvent(new PumpEvent(PumpEventKind.UpdateLiveLines, target));
            }
        }
    }

    private void FanOutLiveLines(Guid serviceId, string line)
    {
        LiveLineSubscription[] subscribers;
        lock (_liveLineGate)
        {
            if (!_liveLineSubscribers.TryGetValue(serviceId, out var registered))
            {
                return;
            }

            subscribers = registered.Snapshot;
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.OnLine(line);
            }
            catch
            {
            }
        }
    }

    private void UpdateWriterLineHandler(TargetState target)
    {
        if (_writers.TryGetValue(target.Target.ServiceId, out var writerState) &&
            ReferenceEquals(writerState.Target, target))
        {
            UpdateWriterLineHandler(writerState);
        }
    }

    private void UpdateWriterLineHandler(WriterState writerState)
    {
        var serviceId = writerState.Target.Target.ServiceId;
        bool hasSubscribers;
        lock (_liveLineGate)
        {
            hasSubscribers = _liveLineSubscribers.ContainsKey(serviceId);
        }

        if (hasSubscribers)
        {
            writerState.LiveLineHandler ??= line => FanOutLiveLines(serviceId, line);
        }

        var lineWritten = hasSubscribers ? writerState.LiveLineHandler : null;
        if (!ReferenceEquals(writerState.Writer.LineWritten, lineWritten))
        {
            writerState.Writer.LineWritten = lineWritten;
        }
    }

    private void Subscribe(
        TargetState target,
        SubscriptionSlot slot,
        ExtensionServiceOutputStream stream)
    {
        DisposeSubscription(slot);
        var generation = slot.BeginSubscription();
        var sink = new ServiceOutputSink(this, target, slot, stream, generation);

        try
        {
            SubscribeAsync(target, slot, stream, generation, sink).GetAwaiter().GetResult();
        }
        catch
        {
            slot.Subscription = null;
        }
    }

    private async Task SubscribeAsync(
        TargetState target,
        SubscriptionSlot slot,
        ExtensionServiceOutputStream stream,
        int generation,
        ServiceOutputSink sink)
    {
        var result = await _outputApi!.SubscribeAsync(
            target.Target.ServiceId,
            stream,
            sink,
            _lifetimeCancellation.Token).ConfigureAwait(false);

        if (Volatile.Read(ref _disposeRequested) != 0 || !target.IsActive)
        {
            DisposeHandle(result.Subscription);
            return;
        }

        var code = result.Code.ToString();
        if (string.Equals(code, "Unsupported", StringComparison.Ordinal))
        {
            DisposeHandle(result.Subscription);
            DisableFeature();
            return;
        }

        if (string.Equals(code, "Opened", StringComparison.Ordinal) && result.Subscription is not null)
        {
            slot.Subscription = result.Subscription;
            slot.MarkOpened(generation);
            _ = EnqueueMarkerEvent(new PumpEvent(
                PumpEventKind.Marker,
                target,
                MarkerText: $"output stream attached ({GetStreamName(stream)})"));
            return;
        }

        DisposeHandle(result.Subscription);
        slot.Subscription = null;
    }

    private void DisableFeature()
    {
        _featureDisabled = true;
        if (!_unsupportedLogged)
        {
            _unsupportedLogged = true;
            try
            {
                _logger?.Report(ExtensionLogLevel.Warning, "service-output-unsupported");
            }
            catch
            {
            }
        }

        foreach (var target in _targets.Values.ToArray())
        {
            RemoveTarget(target);
        }
    }

    private void RemoveTarget(TargetState target)
    {
        target.Deactivate();
        DisposeSubscriptions(target);
        _targets.Remove(target.Target.ServiceId);
        EnqueueControlEvent(new PumpEvent(PumpEventKind.RemoveTarget, target));
    }

    private static void DisposeSubscriptions(TargetState target)
    {
        DisposeSubscription(target.Stdout);
        DisposeSubscription(target.Stderr);
    }

    private static void DisposeSubscription(SubscriptionSlot slot)
    {
        var subscription = slot.Subscription;
        slot.Subscription = null;
        DisposeHandle(subscription);
    }

    private static void DisposeHandle(IExtensionServiceOutputSubscription? subscription)
    {
        if (subscription is null)
        {
            return;
        }

        try
        {
            subscription.Dispose();
        }
        catch
        {
        }
    }

    private void LogNullApiOnce()
    {
        if (_nullApiLogged)
        {
            return;
        }

        _nullApiLogged = true;
        try
        {
            _logger?.Report(ExtensionLogLevel.Warning, "service-output-api-unavailable");
        }
        catch
        {
        }
    }

    private bool TryEnqueue(PumpEvent pumpEvent)
    {
        if (Volatile.Read(ref _disposeRequested) != 0 || !pumpEvent.Target.IsActive)
        {
            return false;
        }

        if (!_events.Writer.TryWrite(pumpEvent))
        {
            return false;
        }

        WakePump();
        return true;
    }

    private void EnqueueControlEvent(PumpEvent pumpEvent)
    {
        try
        {
            _events.Writer.WriteAsync(pumpEvent).AsTask().GetAwaiter().GetResult();
            WakePump();
        }
        catch
        {
        }
    }

    private async Task EnqueueMarkerEvent(PumpEvent pumpEvent)
    {
        try
        {
            await _events.Writer.WriteAsync(pumpEvent).ConfigureAwait(false);
            WakePump();
        }
        catch
        {
        }
    }

    private void RecordOverflow(ServiceOutputSink sink, long droppedByteCount)
    {
        if (droppedByteCount > 0)
        {
            Interlocked.Add(ref sink.PendingDroppedBytes, droppedByteCount);
        }

        EnqueueOverflowSink(sink);
    }

    private void RecordOverflowCompletion(ServiceOutputSink sink, ExtensionServiceOutputCompletionReason reason)
    {
        Volatile.Write(ref sink.PendingCompletionReason, (int)reason);
        Volatile.Write(ref sink.PendingCompletion, 1);
        EnqueueOverflowSink(sink);
    }

    private void EnqueueOverflowSink(ServiceOutputSink sink)
    {
        if (Volatile.Read(ref _disposeRequested) != 0 || !sink.Target.IsActive)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref sink.OverflowQueued, 1, 0) == 0)
        {
            _overflowSinks.Enqueue(sink);
        }

        WakePump();
    }

    private void WakePump()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var didWork = false;
                while (_events.Reader.TryRead(out var pumpEvent))
                {
                    ProcessEvent(pumpEvent);
                    DrainOverflowSinks();
                    didWork = true;
                }

                if (DrainOverflowSinks())
                {
                    didWork = true;
                }

                if (_events.Reader.Completion.IsCompleted && _overflowSinks.IsEmpty)
                {
                    break;
                }

                if (!didWork)
                {
                    await _wakeSignal.WaitAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                while (_events.Reader.TryRead(out var pumpEvent))
                {
                    ProcessEvent(pumpEvent);
                }

                DrainOverflowSinks();
            }
            catch
            {
            }

            foreach (var writerState in _writers.Values)
            {
                try
                {
                    writerState.Writer.FlushPartial();
                    writerState.Writer.Dispose();
                }
                catch
                {
                }
            }

            _writers.Clear();
        }
    }

    private bool DrainOverflowSinks()
    {
        var didWork = false;
        while (_overflowSinks.TryDequeue(out var sink))
        {
            didWork = true;
            var droppedByteCount = Interlocked.Exchange(ref sink.PendingDroppedBytes, 0);
            var hasCompletion = Interlocked.Exchange(ref sink.PendingCompletion, 0) != 0;
            var completionReason = (ExtensionServiceOutputCompletionReason)Volatile.Read(
                ref sink.PendingCompletionReason);
            Interlocked.Exchange(ref sink.OverflowQueued, 0);

            if (droppedByteCount > 0 && !sink.Target.IsRemoved)
            {
                GetOrCreateWriter(sink.Target)?.AppendDropped(droppedByteCount);
            }

            if (hasCompletion)
            {
                ProcessCompletion(sink.Target, sink.Slot, sink.Generation, completionReason);
            }

            if (sink.HasPendingEvents)
            {
                EnqueueOverflowSink(sink);
            }
        }

        return didWork;
    }

    private void ProcessEvent(PumpEvent pumpEvent)
    {
        try
        {
            switch (pumpEvent.Kind)
            {
                case PumpEventKind.Chunk:
                    if (!pumpEvent.Target.IsRemoved && pumpEvent.Data is not null)
                    {
                        GetOrCreateWriter(pumpEvent.Target)?.Append(
                            pumpEvent.Stream,
                            pumpEvent.Timestamp,
                            pumpEvent.Data);
                    }

                    break;
                case PumpEventKind.Dropped:
                    if (!pumpEvent.Target.IsRemoved)
                    {
                        GetOrCreateWriter(pumpEvent.Target)?.AppendDropped(pumpEvent.ByteCount);
                    }

                    break;
                case PumpEventKind.Marker:
                    if (!pumpEvent.Target.IsRemoved && pumpEvent.MarkerText is not null)
                    {
                        GetOrCreateWriter(pumpEvent.Target)?.AppendMarker(pumpEvent.MarkerText);
                    }

                    break;
                case PumpEventKind.Completed:
                    ProcessCompletion(
                        pumpEvent.Target,
                        pumpEvent.Sink!.Slot,
                        pumpEvent.Sink.Generation,
                        pumpEvent.CompletionReason);
                    break;
                case PumpEventKind.UpdateLiveLines:
                    UpdateWriterLineHandler(pumpEvent.Target);
                    break;
                case PumpEventKind.RemoveTarget:
                    RemoveWriter(pumpEvent.Target);
                    break;
            }
        }
        catch
        {
        }
    }

    private void ProcessCompletion(
        TargetState target,
        SubscriptionSlot slot,
        int generation,
        ExtensionServiceOutputCompletionReason reason)
    {
        if (target.IsRemoved || !slot.MarkCompleted(generation))
        {
            return;
        }

        var stream = GetStream(target, slot);
        var writer = GetOrCreateWriter(target);
        writer?.FlushPartial();
        writer?.AppendMarker(
            $"output stream ended: {GetCompletionReasonName(reason)} ({GetStreamName(stream)})");

        if (reason == ExtensionServiceOutputCompletionReason.ProcessExited &&
            target.IsActive &&
            TryBeginImmediateResubscribe(target))
        {
            Subscribe(target, slot, stream);
        }
    }

    private static ExtensionServiceOutputStream GetStream(TargetState target, SubscriptionSlot slot) =>
        ReferenceEquals(slot, target.Stdout)
            ? ExtensionServiceOutputStream.Stdout
            : ExtensionServiceOutputStream.Stderr;

    private static string GetStreamName(ExtensionServiceOutputStream stream) => stream switch
    {
        ExtensionServiceOutputStream.Stdout => "stdout",
        ExtensionServiceOutputStream.Stderr => "stderr",
        _ => "stderr"
    };

    private static string GetCompletionReasonName(ExtensionServiceOutputCompletionReason reason) => reason switch
    {
        ExtensionServiceOutputCompletionReason.ProcessExited => "process-exited",
        ExtensionServiceOutputCompletionReason.Faulted => "faulted",
        ExtensionServiceOutputCompletionReason.HostTeardown => "host-teardown",
        _ => reason.ToString().ToLowerInvariant()
    };

    private static bool TryBeginImmediateResubscribe(TargetState target)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = target.LastImmediateResubscribeAt;
        if (previous.HasValue && now - previous.Value < ImmediateResubscribeCooldown)
        {
            return false;
        }

        target.LastImmediateResubscribeAt = now;
        return true;
    }

    private ServiceLogWriter? GetOrCreateWriter(TargetState target)
    {
        if (target.IsRemoved)
        {
            return null;
        }

        if (_writers.TryGetValue(target.Target.ServiceId, out var current))
        {
            if (ReferenceEquals(current.Target, target))
            {
                return current.Writer;
            }

            current.Writer.FlushPartial();
            current.Writer.Dispose();
            _writers.Remove(target.Target.ServiceId);
        }

        try
        {
            var writer = new ServiceLogWriter(target.Target.LogDirectory, target.Target.ServiceName);
            var writerState = new WriterState(target, writer);
            _writers.Add(target.Target.ServiceId, writerState);
            UpdateWriterLineHandler(writerState);
            return writer;
        }
        catch
        {
            return null;
        }
    }

    private void RemoveWriter(TargetState target)
    {
        target.MarkRemoved();
        if (!_writers.TryGetValue(target.Target.ServiceId, out var writerState) ||
            !ReferenceEquals(writerState.Target, target))
        {
            return;
        }

        _writers.Remove(target.Target.ServiceId);
        try
        {
            writerState.Writer.FlushPartial();
            writerState.Writer.Dispose();
        }
        catch
        {
        }
    }

    private sealed class TargetState(ServiceLogTarget target)
    {
        private int _active = 1;
        private int _removed;

        public ServiceLogTarget Target { get; } = target;
        public DateTimeOffset? LastImmediateResubscribeAt { get; set; }
        public SubscriptionSlot Stdout { get; } = new();
        public SubscriptionSlot Stderr { get; } = new();
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public bool IsRemoved => Volatile.Read(ref _removed) != 0;

        public void Deactivate() => Volatile.Write(ref _active, 0);

        public void MarkRemoved() => Volatile.Write(ref _removed, 1);
    }

    private sealed class SubscriptionSlot
    {
        private const long StatusMask = 3;
        private const int Pending = 0;
        private const int Open = 1;
        private const int Dead = 2;

        private long _state;

        public IExtensionServiceOutputSubscription? Subscription { get; set; }

        public bool IsOpen => (Volatile.Read(ref _state) & StatusMask) == Open;

        public int BeginSubscription()
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                var generation = unchecked((int)(current >> 2) + 1);
                var next = Encode(generation, Pending);
                if (Interlocked.CompareExchange(ref _state, next, current) == current)
                {
                    return generation;
                }
            }
        }

        public void MarkOpened(int generation)
        {
            Interlocked.CompareExchange(ref _state, Encode(generation, Open), Encode(generation, Pending));
        }

        public bool MarkCompleted(int generation)
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if ((int)(current >> 2) != generation || (current & StatusMask) == Dead)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, Encode(generation, Dead), current) == current)
                {
                    return true;
                }
            }
        }

        private static long Encode(int generation, int status) => ((long)generation << 2) | (uint)status;
    }

    private sealed class ServiceOutputSink(
        ServiceLogRecorder recorder,
        TargetState target,
        SubscriptionSlot slot,
        ExtensionServiceOutputStream stream,
        int generation) : IExtensionServiceOutputSink
    {
        public long PendingDroppedBytes;
        public int PendingCompletion;
        public int PendingCompletionReason;
        public int OverflowQueued;

        public TargetState Target { get; } = target;
        public SubscriptionSlot Slot { get; } = slot;
        public int Generation { get; } = generation;
        public bool HasPendingEvents =>
            Volatile.Read(ref PendingDroppedBytes) > 0 || Volatile.Read(ref PendingCompletion) != 0;

        public void OnChunk(ExtensionServiceOutputChunk chunk)
        {
            var data = chunk.Data;
            if (recorder.TryEnqueue(new PumpEvent(
                PumpEventKind.Chunk,
                Target,
                chunk.Stream,
                chunk.Timestamp,
                data,
                Sink: this)))
            {
                return;
            }

            if (data is not null)
            {
                recorder.RecordOverflow(this, data.LongLength);
            }
        }

        public void OnCompleted(ExtensionServiceOutputCompletionReason reason)
        {
            if (recorder.TryEnqueue(new PumpEvent(
                PumpEventKind.Completed,
                Target,
                Stream: stream,
                CompletionReason: reason,
                Sink: this)))
            {
                return;
            }

            recorder.RecordOverflowCompletion(this, reason);
        }

        public void OnDropped(long byteCount)
        {
            if (recorder.TryEnqueue(new PumpEvent(
                PumpEventKind.Dropped,
                Target,
                Stream: stream,
                ByteCount: byteCount,
                Sink: this)))
            {
                return;
            }

            recorder.RecordOverflow(this, byteCount);
        }
    }

    private sealed class LiveLineSubscribers
    {
        public List<LiveLineSubscription> Items { get; } = new();
        public LiveLineSubscription[] Snapshot { get; set; } = Array.Empty<LiveLineSubscription>();
    }

    private sealed class LiveLineSubscription(
        ServiceLogRecorder recorder,
        Guid serviceId,
        Action<string> onLine) : IDisposable
    {
        private ServiceLogRecorder? _recorder = recorder;

        public Action<string> OnLine { get; } = onLine;

        public void Dispose()
        {
            Interlocked.Exchange(ref _recorder, null)?.UnsubscribeLiveLines(serviceId, this);
        }
    }

    private sealed class WriterState(TargetState target, ServiceLogWriter writer)
    {
        public TargetState Target { get; } = target;
        public ServiceLogWriter Writer { get; } = writer;
        public Action<string>? LiveLineHandler { get; set; }
    }

    private enum PumpEventKind : byte
    {
        Chunk,
        Dropped,
        Marker,
        Completed,
        RemoveTarget,
        UpdateLiveLines
    }

    private readonly record struct PumpEvent(
        PumpEventKind Kind,
        TargetState Target,
        ExtensionServiceOutputStream Stream = default,
        DateTimeOffset Timestamp = default,
        byte[]? Data = null,
        long ByteCount = 0,
        ExtensionServiceOutputCompletionReason CompletionReason = default,
        ServiceOutputSink? Sink = null,
        string? MarkerText = null);
}
