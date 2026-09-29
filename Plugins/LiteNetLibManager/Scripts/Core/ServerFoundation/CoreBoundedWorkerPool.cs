using System;
using System.Collections.Concurrent;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;

namespace LiteNetLibManager
{
    public enum CoreWorkerPriority : byte
    {
        High = 0,
        Normal = 1,
        Low = 2,
    }

    [Serializable]
    public sealed class CoreWorkerPoolSettings
    {
        [UnityEngine.Tooltip("0 selects a conservative automatic count: CPU cores - 1, capped at 4.")]
        [UnityEngine.Min(0)] public int workerCount = 0;
        [UnityEngine.Min(1)] public int maxQueuedJobs = 4096;
        [UnityEngine.Min(1)] public int maxQueuedCompletions = 4096;
        [UnityEngine.Min(1)] public int maxCompletionsPerSchedulerTick = 64;
        public string completionChannel = "Background";
        [UnityEngine.Min(0.1f)] public float slowWorkerJobWarningMilliseconds = 100f;
        [UnityEngine.Min(0.1f)] public float slowCompletionWarningMilliseconds = 4f;
        [UnityEngine.Min(250)] public int shutdownTimeoutMilliseconds = 5000;
        public bool logWorkerExceptions = true;

        internal void ClampUnsafeValues()
        {
            workerCount = Math.Max(0, workerCount);
            maxQueuedJobs = Math.Max(1, maxQueuedJobs);
            maxQueuedCompletions = Math.Max(1, maxQueuedCompletions);
            maxCompletionsPerSchedulerTick = Math.Max(1, maxCompletionsPerSchedulerTick);
            if (string.IsNullOrWhiteSpace(completionChannel))
                completionChannel = "Background";
            slowWorkerJobWarningMilliseconds = Math.Max(0.1f, slowWorkerJobWarningMilliseconds);
            slowCompletionWarningMilliseconds = Math.Max(0.1f, slowCompletionWarningMilliseconds);
            shutdownTimeoutMilliseconds = Math.Max(250, shutdownTimeoutMilliseconds);
        }
    }

    public struct CoreWorkerPoolMetrics
    {
        public bool running;
        public int workerCount;
        public int queuedJobs;
        public int queuedCompletions;
        public long acceptedJobs;
        public long rejectedJobs;
        public long completedJobs;
        public long failedJobs;
        public long expiredJobs;
        public long droppedCompletions;
        public long slowJobs;
        public long slowCompletions;
        public double maximumJobMilliseconds;
        public double maximumCompletionMilliseconds;
        public long shutdownFailures;
        public bool shutdownIncomplete;
        public string lastFailure;
    }

    /// <summary>
    /// Fixed-size worker pool with bounded input/output queues.
    /// Worker callbacks must not touch Unity objects and must honor cancellation.
    /// </summary>
    public sealed class CoreBoundedWorkerPool : ICoreBudgetedWorkSystem, IDisposable
    {
        private sealed class WorkItem
        {
            public Action<CancellationToken> Work;
            public Action Completion;
            public double DeadlineAt;
            public string Name;
        }

        private sealed class CompletionItem
        {
            public Action Completion;
            public string Name;
        }

        private readonly LiteNetLibManager _manager;
        private readonly CoreWorkerPoolSettings _settings;
        private readonly ConcurrentQueue<WorkItem> _high = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<WorkItem> _normal = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<WorkItem> _low = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<CompletionItem> _completions = new ConcurrentQueue<CompletionItem>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly object _lifecycleLock = new object();

        private Thread[] _threads;
        private CancellationTokenSource _cts;
        private ICoreScheduledSystemHandle _completionHandle;
        private volatile bool _running;
        private int _queuedJobs;
        private int _queuedCompletions;

        private long _acceptedJobs;
        private long _rejectedJobs;
        private long _completedJobs;
        private long _failedJobs;
        private long _expiredJobs;
        private long _droppedCompletions;
        private long _slowJobs;
        private long _slowCompletions;
        private long _maximumJobTicks;
        private long _maximumCompletionTicks;
        private long _shutdownFailures;
        private volatile bool _shutdownIncomplete;
        private string _lastFailure;
        private readonly object _failureLock = new object();

        public string Name => "Core Worker Completion Pump";
        public bool HasPendingWork => Volatile.Read(ref _queuedCompletions) > 0;
        public bool IsRunning => _running;

        public CoreBoundedWorkerPool(LiteNetLibManager manager, CoreWorkerPoolSettings settings, CoreServerTelemetry telemetry)
        {
            _manager = manager;
            _settings = settings ?? new CoreWorkerPoolSettings();
            _settings.ClampUnsafeValues();
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running)
                    return;
                if (_shutdownIncomplete)
                {
                    UnityEngine.Debug.LogError("[CoreBoundedWorkerPool] Cannot restart: previous workers did not terminate cleanly.");
                    return;
                }

                _settings.ClampUnsafeValues();
                ResetMetrics();
                int count = _settings.workerCount;
                if (count <= 0)
                    count = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

                _cts = new CancellationTokenSource();
                _threads = new Thread[count];
                _running = true;

                for (int i = 0; i < count; ++i)
                {
                    Thread thread = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = $"LNL-CoreWorker-{i}",
                    };
                    _threads[i] = thread;
                    thread.Start(_cts.Token);
                }

                if (_manager.CoreScheduler.IsRunning)
                {
                    _completionHandle = _manager.CoreScheduler.RegisterWorkSystem(
                        this,
                        _settings.completionChannel,
                        _settings.maxCompletionsPerSchedulerTick,
                        false,
                        Math.Max(0.1, _settings.slowCompletionWarningMilliseconds));
                }
            }
        }

        private void ResetMetrics()
        {
            Interlocked.Exchange(ref _acceptedJobs, 0);
            Interlocked.Exchange(ref _rejectedJobs, 0);
            Interlocked.Exchange(ref _completedJobs, 0);
            Interlocked.Exchange(ref _failedJobs, 0);
            Interlocked.Exchange(ref _expiredJobs, 0);
            Interlocked.Exchange(ref _droppedCompletions, 0);
            Interlocked.Exchange(ref _slowJobs, 0);
            Interlocked.Exchange(ref _slowCompletions, 0);
            Interlocked.Exchange(ref _maximumJobTicks, 0);
            Interlocked.Exchange(ref _maximumCompletionTicks, 0);
            Interlocked.Exchange(ref _shutdownFailures, 0);
            _shutdownIncomplete = false;
            lock (_failureLock) _lastFailure = null;
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_running)
                    return;

                _running = false;
                _completionHandle?.Unregister();
                _completionHandle = null;

                _cts?.Cancel();
                for (int i = 0; i < (_threads?.Length ?? 0); ++i)
                    _signal.Release();

                bool allStopped = true;
                if (_threads != null)
                {
                    int deadline = Environment.TickCount + _settings.shutdownTimeoutMilliseconds;
                    for (int i = 0; i < _threads.Length; ++i)
                    {
                        Thread thread = _threads[i];
                        if (thread == null || !thread.IsAlive)
                            continue;
                        int remaining = Math.Max(0, deadline - Environment.TickCount);
                        if (remaining <= 0 || !thread.Join(remaining))
                        {
                            allStopped = false;
                            RecordFailure($"Worker '{thread.Name}' failed to terminate within {_settings.shutdownTimeoutMilliseconds} ms.", null);
                        }
                    }
                }

                if (allStopped)
                {
                    _threads = null;
                    _cts?.Dispose();
                    _cts = null;
                    _shutdownIncomplete = false;
                }
                else
                {
                    _shutdownIncomplete = true;
                    Interlocked.Increment(ref _shutdownFailures);
                    UnityEngine.Debug.LogError("[CoreBoundedWorkerPool] Shutdown incomplete. Synchronization resources are being retained until process exit rather than disposed under live workers.");
                }

                while (_high.TryDequeue(out _)) { }
                while (_normal.TryDequeue(out _)) { }
                while (_low.TryDequeue(out _)) { }
                while (_completions.TryDequeue(out _)) { }
                Volatile.Write(ref _queuedJobs, 0);
                Volatile.Write(ref _queuedCompletions, 0);
            }
        }

        public bool TryQueue(
            Action<CancellationToken> work,
            Action completion = null,
            CoreWorkerPriority priority = CoreWorkerPriority.Normal,
            double deadlineSecondsFromNow = 0.0,
            string name = null)
        {
            if (work == null || !_running)
            {
                Interlocked.Increment(ref _rejectedJobs);
                return false;
            }

            int queued = Interlocked.Increment(ref _queuedJobs);
            if (queued > _settings.maxQueuedJobs)
            {
                Interlocked.Decrement(ref _queuedJobs);
                Interlocked.Increment(ref _rejectedJobs);
                return false;
            }

            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            WorkItem item = new WorkItem
            {
                Work = work,
                Completion = completion,
                DeadlineAt = deadlineSecondsFromNow > 0.0 ? now + deadlineSecondsFromNow : 0.0,
                Name = string.IsNullOrEmpty(name) ? "WorkerJob" : name,
            };

            switch (priority)
            {
                case CoreWorkerPriority.High:
                    _high.Enqueue(item);
                    break;
                case CoreWorkerPriority.Low:
                    _low.Enqueue(item);
                    break;
                default:
                    _normal.Enqueue(item);
                    break;
            }

            Interlocked.Increment(ref _acceptedJobs);
            _signal.Release();
            return true;
        }

        private bool TryDequeue(out WorkItem item)
        {
            if (_high.TryDequeue(out item))
                return true;
            if (_normal.TryDequeue(out item))
                return true;
            if (_low.TryDequeue(out item))
                return true;
            item = null;
            return false;
        }

        private void WorkerLoop(object state)
        {
            CancellationToken token = (CancellationToken)state;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _signal.Wait(250, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;
                if (!TryDequeue(out WorkItem item))
                    continue;

                Interlocked.Decrement(ref _queuedJobs);
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (item.DeadlineAt > 0.0 && now > item.DeadlineAt)
                {
                    Interlocked.Increment(ref _expiredJobs);
                    continue;
                }

                long start = Stopwatch.GetTimestamp();
                bool success = false;
                try
                {
                    item.Work(token);
                    success = true;
                    Interlocked.Increment(ref _completedJobs);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedJobs);
                    RecordFailure($"Worker job '{item.Name}' failed.", ex);
                }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    UpdateMaximum(ref _maximumJobTicks, elapsed);
                    if (elapsed * 1000.0 / Stopwatch.Frequency >= _settings.slowWorkerJobWarningMilliseconds)
                        Interlocked.Increment(ref _slowJobs);
                }

                if (success && item.Completion != null && _running && !token.IsCancellationRequested)
                {
                    int count = Interlocked.Increment(ref _queuedCompletions);
                    if (count > _settings.maxQueuedCompletions)
                    {
                        Interlocked.Decrement(ref _queuedCompletions);
                        Interlocked.Increment(ref _droppedCompletions);
                    }
                    else
                    {
                        _completions.Enqueue(new CompletionItem
                        {
                            Completion = item.Completion,
                            Name = item.Name,
                        });
                    }
                }
            }
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (!_completions.TryDequeue(out CompletionItem item))
                return;

            Interlocked.Decrement(ref _queuedCompletions);
            long start = Stopwatch.GetTimestamp();
            try
            {
                item.Completion?.Invoke();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedJobs);
                RecordFailure($"Worker completion '{item.Name}' failed.", ex);
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - start;
                UpdateMaximum(ref _maximumCompletionTicks, elapsed);
                if (elapsed * 1000.0 / Stopwatch.Frequency >= _settings.slowCompletionWarningMilliseconds)
                    Interlocked.Increment(ref _slowCompletions);
            }
        }

        private void RecordFailure(string context, Exception ex)
        {
            string detail = ex == null ? context : $"{context} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            lock (_failureLock) _lastFailure = detail;
            if (_settings.logWorkerExceptions)
                UnityEngine.Debug.LogError($"[CoreBoundedWorkerPool] {detail}");
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current)
                    return;
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        public CoreWorkerPoolMetrics GetMetricsSnapshot()
        {
            return new CoreWorkerPoolMetrics
            {
                running = _running,
                workerCount = _threads?.Length ?? 0,
                queuedJobs = Volatile.Read(ref _queuedJobs),
                queuedCompletions = Volatile.Read(ref _queuedCompletions),
                acceptedJobs = Interlocked.Read(ref _acceptedJobs),
                rejectedJobs = Interlocked.Read(ref _rejectedJobs),
                completedJobs = Interlocked.Read(ref _completedJobs),
                failedJobs = Interlocked.Read(ref _failedJobs),
                expiredJobs = Interlocked.Read(ref _expiredJobs),
                droppedCompletions = Interlocked.Read(ref _droppedCompletions),
                slowJobs = Interlocked.Read(ref _slowJobs),
                slowCompletions = Interlocked.Read(ref _slowCompletions),
                maximumJobMilliseconds = Interlocked.Read(ref _maximumJobTicks) * 1000.0 / Stopwatch.Frequency,
                maximumCompletionMilliseconds = Interlocked.Read(ref _maximumCompletionTicks) * 1000.0 / Stopwatch.Frequency,
                shutdownFailures = Interlocked.Read(ref _shutdownFailures),
                shutdownIncomplete = _shutdownIncomplete,
                lastFailure = GetLastFailure(),
            };
        }

        private string GetLastFailure()
        {
            lock (_failureLock) return _lastFailure;
        }

        public void Dispose()
        {
            Stop();
            if (!_shutdownIncomplete)
                _signal.Dispose();
        }
    }
}
