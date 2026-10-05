using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Logs;

/// <summary>Subscribes to managed service log feeds and serializes them through one logging pump.</summary>
public sealed class ServiceLogRecorder : IDisposable
{
    private const int ChannelCapacity = 4096;
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(1);

    private readonly IExtensionServiceOutputApi? _outputApi;
    private readonly IExtensionLogger? _logger;
    private readonly IExtensionLogWriter? _logWriter;
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
    private readonly ConcurrentQueue<ServiceLogSink> _overflowSinks = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<Guid, WriterState> _writers = new();
    private readonly Task _pump;

    private bool _nullApiLogged;
    private bool _unsupportedLogged;
    private bool _featureDisabled;
    private int _disposeRequested;

    /// <summary>Creates a recorder for the host's optional service output API.</summary>
    public ServiceLogRecorder(
        IExtensionServiceOutputApi? outputApi,
        IExtensionLogger? logger,
        IExtensionLogWriter? logWriter = null)
    {
        _outputApi = outputApi;
        _logger = logger;
        _logWriter = logWriter;
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
                    DisposeSubscription(target.Slot);
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

            if (!state.Slot.IsOpen)
            {
                Subscribe(state);
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

    private void Subscribe(TargetState target)
    {
        DisposeSubscription(target.Slot);
        var generation = target.Slot.BeginSubscription();
        var sink = new ServiceLogSink(this, target, target.Slot, generation);

        try
        {
            SubscribeAsync(target, sink, generation).GetAwaiter().GetResult();
        }
        catch
        {
            target.Slot.Subscription = null;
        }
    }

    private async Task SubscribeAsync(TargetState target, ServiceLogSink sink, int generation)
    {
        // Resume after the last processed sequence when possible; an InvalidCursor
        // response means the replay window moved past it, so retry once from latest.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var cursor = target.LastSequence >= 0 ? target.LastSequence : (long?)null;
            var result = await _outputApi!.SubscribeAsync(
                target.Target.ServiceId,
                sink,
                cursor,
                _lifetimeCancellation.Token).ConfigureAwait(false);

            if (Volatile.Read(ref _disposeRequested) != 0 || !target.IsActive)
            {
                DisposeHandle(result.Subscription);
                return;
            }

            if (!result.Succeeded)
            {
                ReportSubscriptionFailure(result);
            }

            if (result.Code == ExtensionServiceLogCode.Unsupported)
            {
                DisposeHandle(result.Subscription);
                DisableFeature("service-output-unsupported");
                return;
            }

            if (result.Code == ExtensionServiceLogCode.InvalidCursor && cursor.HasValue)
            {
                DisposeHandle(result.Subscription);
                target.ResetSequence();
                _ = EnqueueMarkerEvent(new PumpEvent(
                    PumpEventKind.Marker,
                    target,
                    MarkerText: "log history no longer available; resuming at latest"));
                continue;
            }

            if (result.Code == ExtensionServiceLogCode.Subscribed && result.Subscription is not null)
            {
                target.Slot.Subscription = result.Subscription;
                target.Slot.MarkOpened(generation);
                _ = EnqueueMarkerEvent(new PumpEvent(
                    PumpEventKind.Marker,
                    target,
                    MarkerText: "service log feed attached"));
                return;
            }

            DisposeHandle(result.Subscription);
            target.Slot.Subscription = null;
            return;
        }
    }

    private void ReportSubscriptionFailure(ExtensionServiceLogSubscriptionResult result)
    {
        try
        {
            _logWriter?.WriteText(
                ExtensionLogLevel.Warning,
                $"Service log subscription for '{result.ServiceId}' failed with {result.Code}: {result.Detail!.Message}");
        }
        catch
        {
            // Failure diagnostics must not interrupt subscription cleanup or retries.
        }
    }

    private void DisableFeature(string reason)
    {
        _featureDisabled = true;
        if (!_unsupportedLogged)
        {
            _unsupportedLogged = true;
            try
            {
                _logger?.Report(ExtensionLogLevel.Warning, reason);
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
        DisposeSubscription(target.Slot);
        _targets.Remove(target.Target.ServiceId);
        EnqueueControlEvent(new PumpEvent(PumpEventKind.RemoveTarget, target));
    }

    private static void DisposeSubscription(SubscriptionSlot slot)
    {
        var subscription = slot.Subscription;
        slot.Subscription = null;
        DisposeHandle(subscription);
    }

    private static void DisposeHandle(IExtensionServiceLogSubscription? subscription)
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

    private void RecordOverflow(ServiceLogSink sink, long droppedByteCount)
    {
        if (droppedByteCount > 0)
        {
            Interlocked.Add(ref sink.PendingDroppedBytes, droppedByteCount);
        }

        EnqueueOverflowSink(sink);
    }

    private void RecordOverflowCompletion(ServiceLogSink sink)
    {
        Volatile.Write(ref sink.PendingCompletion, 1);
        EnqueueOverflowSink(sink);
    }

    private void EnqueueOverflowSink(ServiceLogSink sink)
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
            Interlocked.Exchange(ref sink.OverflowQueued, 0);

            if (droppedByteCount > 0 && !sink.Target.IsRemoved)
            {
                GetOrCreateWriter(sink.Target)?.AppendDropped(droppedByteCount);
            }

            if (hasCompletion)
            {
                ProcessCompletion(sink.Target, sink.Slot, sink.Generation);
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
                    if (!pumpEvent.Target.IsRemoved && !pumpEvent.Data.IsDefaultOrEmpty)
                    {
                        GetOrCreateWriter(pumpEvent.Target)?.Append(
                            pumpEvent.Stream,
                            pumpEvent.Timestamp,
                            pumpEvent.Data.AsSpan());
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
                        pumpEvent.Sink.Generation);
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

    private void ProcessCompletion(TargetState target, SubscriptionSlot slot, int generation)
    {
        // The feed spans process generations, so completion only detaches the
        // subscription; SyncTargets re-subscribes (with cursor resume) on the
        // next reconcile pass if the target is still desired.
        if (target.IsRemoved)
        {
            return;
        }

        slot.MarkCompleted(generation);
        GetOrCreateWriter(target)?.FlushPartial();
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

    private static string TerminationReasonName(ExtensionServiceLogTerminationReason? reason) => reason switch
    {
        ExtensionServiceLogTerminationReason.HostShutdown => "host-shutdown",
        ExtensionServiceLogTerminationReason.ExtensionUnloaded => "extension-unloaded",
        ExtensionServiceLogTerminationReason.ServiceDisabled => "service-disabled",
        ExtensionServiceLogTerminationReason.ServiceRemoved => "service-removed",
        _ => "unknown"
    };

    private void OnFeedEntry(ServiceLogSink sink, ExtensionServiceLogEntry entry)
    {
        if (entry.Sequence is { } sequence)
        {
            sink.Target.AdvanceSequence(sequence);
        }

        switch (entry.Kind)
        {
            case ExtensionServiceLogEntryKind.Output:
                if (entry.Data.IsDefaultOrEmpty)
                {
                    return;
                }

                if (!TryEnqueue(new PumpEvent(
                    PumpEventKind.Chunk,
                    sink.Target,
                    entry.Stream ?? ExtensionServiceOutputStream.Stdout,
                    entry.Timestamp,
                    entry.Data,
                    Sink: sink)))
                {
                    RecordOverflow(sink, entry.Data.Length);
                }

                return;
            case ExtensionServiceLogEntryKind.GenerationStarted:
                EnqueueFeedMarker(
                    sink,
                    entry.AttemptNumber is { } attempt
                        ? $"process started (attempt {attempt})"
                        : "process started");
                return;
            case ExtensionServiceLogEntryKind.ProcessExited:
                EnqueueFeedMarker(
                    sink,
                    entry.ProcessExitCode is { } exitCode
                        ? $"process exited (code {exitCode})"
                        : "process exited");
                return;
            case ExtensionServiceLogEntryKind.StartupFailed:
                var detail = string.IsNullOrWhiteSpace(entry.FailureReason)
                    ? entry.FailureCode.ToString()
                    : $"{entry.FailureCode}: {entry.FailureReason}";
                EnqueueFeedMarker(sink, $"startup failed: {detail}");
                return;
            case ExtensionServiceLogEntryKind.CurrentState:
                if (entry.LifecycleState is { } state)
                {
                    EnqueueFeedMarker(sink, $"lifecycle state: {state}");
                }

                return;
            case ExtensionServiceLogEntryKind.Gap:
                EnqueueFeedMarker(
                    sink,
                    $"log entries lost (sequences {entry.FirstMissingSequence}..{entry.LastMissingSequence})");
                return;
            case ExtensionServiceLogEntryKind.Termination:
                EnqueueFeedMarker(sink, $"log feed ended: {TerminationReasonName(entry.TerminationReason)}");
                return;
        }
    }

    private void EnqueueFeedMarker(ServiceLogSink sink, string text)
    {
        if (!TryEnqueue(new PumpEvent(PumpEventKind.Marker, sink.Target, MarkerText: text, Sink: sink)))
        {
            // Marker loss under pressure is acceptable; output bytes are accounted separately.
        }
    }

    private sealed class TargetState(ServiceLogTarget target)
    {
        private int _active = 1;
        private int _removed;
        private long _lastSequence = -1;

        public ServiceLogTarget Target { get; } = target;
        public SubscriptionSlot Slot { get; } = new();
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public bool IsRemoved => Volatile.Read(ref _removed) != 0;
        public long LastSequence => Interlocked.Read(ref _lastSequence);

        public void Deactivate() => Volatile.Write(ref _active, 0);

        public void MarkRemoved() => Volatile.Write(ref _removed, 1);

        public void AdvanceSequence(long sequence)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _lastSequence);
                if (sequence <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _lastSequence, sequence, current) == current)
                {
                    return;
                }
            }
        }

        public void ResetSequence() => Interlocked.Exchange(ref _lastSequence, -1);
    }

    private sealed class SubscriptionSlot
    {
        private const long StatusMask = 3;
        private const int Pending = 0;
        private const int Open = 1;
        private const int Dead = 2;

        private long _state;

        public IExtensionServiceLogSubscription? Subscription { get; set; }

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

    private sealed class ServiceLogSink(
        ServiceLogRecorder recorder,
        TargetState target,
        SubscriptionSlot slot,
        int generation) : IExtensionServiceLogSink
    {
        public long PendingDroppedBytes;
        public int PendingCompletion;
        public int OverflowQueued;

        public TargetState Target { get; } = target;
        public SubscriptionSlot Slot { get; } = slot;
        public int Generation { get; } = generation;
        public bool HasPendingEvents =>
            Volatile.Read(ref PendingDroppedBytes) > 0 || Volatile.Read(ref PendingCompletion) != 0;

        public void OnEntry(ExtensionServiceLogEntry entry) => recorder.OnFeedEntry(this, entry);

        public void OnCompleted()
        {
            if (recorder.TryEnqueue(new PumpEvent(
                PumpEventKind.Completed,
                Target,
                Sink: this)))
            {
                return;
            }

            recorder.RecordOverflowCompletion(this);
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
        ImmutableArray<byte> Data = default,
        long ByteCount = 0,
        ServiceLogSink? Sink = null,
        string? MarkerText = null);
}
