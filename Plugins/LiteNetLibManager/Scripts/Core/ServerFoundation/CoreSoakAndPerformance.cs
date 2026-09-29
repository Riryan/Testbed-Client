using System;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreSoakSettings
    {
        [Min(1f)] public float sampleIntervalSeconds = 60f;
        [Range(8, 512)] public int sampleCapacity = 120;
        [Min(0f)] public float warningMemorySlopeMegabytesPerHour = 128f;
        [Min(4)] public int minimumSamplesForSlope = 10;

        internal void ClampUnsafeValues()
        {
            sampleIntervalSeconds = Math.Max(1f, sampleIntervalSeconds);
            sampleCapacity = Mathf.Clamp(sampleCapacity, 8, 512);
            warningMemorySlopeMegabytesPerHour = Math.Max(0f, warningMemorySlopeMegabytesPerHour);
            minimumSamplesForSlope = Mathf.Clamp(minimumSamplesForSlope, 4, sampleCapacity);
        }
    }

    public struct CoreSoakMetrics
    {
        public int sampleCount;
        public long currentManagedMemoryBytes;
        public long minimumManagedMemoryBytes;
        public long maximumManagedMemoryBytes;
        public double memorySlopeBytesPerHour;
        public long warningCount;
    }

    public sealed class CoreSoakMonitor : ICoreTickSystem
    {
        private readonly LiteNetLibManager _manager;
        private readonly CoreSoakSettings _settings;
        private ICoreScheduledSystemHandle _handle;

        private readonly long[] _bytes;
        private readonly double[] _times;
        private int _count;
        private int _cursor;
        private double _nextSampleAt;
        private long _minimum = long.MaxValue;
        private long _maximum;
        private long _warnings;
        private double _slopeBytesPerHour;

        public string Name => "Core Soak Monitor";

        public CoreSoakMonitor(LiteNetLibManager manager, CoreSoakSettings settings, CoreServerTelemetry telemetry)
        {
            _manager = manager;
            _settings = settings ?? new CoreSoakSettings();
            _settings.ClampUnsafeValues();
            _bytes = new long[_settings.sampleCapacity];
            _times = new double[_settings.sampleCapacity];
        }

        public void Start()
        {
            if (_handle != null && _handle.IsRegistered)
                return;
            Array.Clear(_bytes, 0, _bytes.Length);
            Array.Clear(_times, 0, _times.Length);
            _count = 0;
            _cursor = 0;
            _minimum = long.MaxValue;
            _maximum = 0;
            _warnings = 0;
            _slopeBytesPerHour = 0.0;
            _nextSampleAt = 0.0;
            _handle = _manager.CoreScheduler.RegisterSystem(this, "Maintenance", true, 1.0);
        }

        public void Stop()
        {
            _handle?.Unregister();
            _handle = null;
        }

        public void Prepare(in CoreTickContext context) { }

        public void Execute(in CoreTickContext context)
        {
            if (context.Now < _nextSampleAt)
                return;
            _nextSampleAt = context.Now + _settings.sampleIntervalSeconds;

            long memory = GC.GetTotalMemory(false);
            _bytes[_cursor] = memory;
            _times[_cursor] = context.Now;
            _cursor = (_cursor + 1) % _bytes.Length;
            if (_count < _bytes.Length)
                _count++;

            if (memory < _minimum)
                _minimum = memory;
            if (memory > _maximum)
                _maximum = memory;

            RecalculateSlope();
            if (_count >= _settings.minimumSamplesForSlope &&
                _settings.warningMemorySlopeMegabytesPerHour > 0f &&
                _slopeBytesPerHour > _settings.warningMemorySlopeMegabytesPerHour * 1024.0 * 1024.0)
            {
                _warnings++;
            }
        }

        public void Commit(in CoreTickContext context) { }

        private void RecalculateSlope()
        {
            if (_count < 2)
            {
                _slopeBytesPerHour = 0.0;
                return;
            }

            int oldest = _count == _bytes.Length ? _cursor : 0;
            int newest = (_cursor - 1 + _bytes.Length) % _bytes.Length;
            double seconds = _times[newest] - _times[oldest];
            if (seconds <= 0.0)
            {
                _slopeBytesPerHour = 0.0;
                return;
            }
            _slopeBytesPerHour = (_bytes[newest] - _bytes[oldest]) * 3600.0 / seconds;
        }

        public CoreSoakMetrics GetMetricsSnapshot()
        {
            long current = _count > 0 ? _bytes[(_cursor - 1 + _bytes.Length) % _bytes.Length] : GC.GetTotalMemory(false);
            return new CoreSoakMetrics
            {
                sampleCount = _count,
                currentManagedMemoryBytes = current,
                minimumManagedMemoryBytes = _minimum == long.MaxValue ? current : _minimum,
                maximumManagedMemoryBytes = Math.Max(_maximum, current),
                memorySlopeBytesPerHour = _slopeBytesPerHour,
                warningCount = _warnings,
            };
        }
    }

    public enum CorePerformanceHealth : byte
    {
        Unknown = 0,
        Healthy = 1,
        Warning = 2,
        Failing = 3,
    }

    [Serializable]
    public sealed class CorePerformanceGateSettings
    {
        [Min(1f)] public float evaluationIntervalSeconds = 10f;
        [Min(0.1f)] public float warningCoreP99Milliseconds = 10f;
        [Min(0.1f)] public float failingCoreP99Milliseconds = 12f;
        [Range(0f, 1f)] public float warningWorkerQueueUtilization = 0.75f;
        [Range(0f, 1f)] public float failingWorkerQueueUtilization = 0.95f;
        [Min(0f)] public float warningMemorySlopeMegabytesPerHour = 128f;
        [Min(0f)] public float failingMemorySlopeMegabytesPerHour = 512f;

        internal void ClampUnsafeValues()
        {
            evaluationIntervalSeconds = Math.Max(1f, evaluationIntervalSeconds);
            warningCoreP99Milliseconds = Math.Max(0.1f, warningCoreP99Milliseconds);
            failingCoreP99Milliseconds = Math.Max(warningCoreP99Milliseconds, failingCoreP99Milliseconds);
            warningWorkerQueueUtilization = Mathf.Clamp01(warningWorkerQueueUtilization);
            failingWorkerQueueUtilization = Mathf.Clamp(failingWorkerQueueUtilization, warningWorkerQueueUtilization, 1f);
            warningMemorySlopeMegabytesPerHour = Math.Max(0f, warningMemorySlopeMegabytesPerHour);
            failingMemorySlopeMegabytesPerHour = Math.Max(warningMemorySlopeMegabytesPerHour, failingMemorySlopeMegabytesPerHour);
        }
    }

    public sealed class CorePerformanceGate : ICoreTickSystem
    {
        private readonly LiteNetLibManager _manager;
        private readonly CorePerformanceGateSettings _settings;
        private readonly CoreServerTelemetry _telemetry;
        private readonly CoreSoakMonitor _soak;
        private readonly CoreBoundedWorkerPool _workers;
        private ICoreScheduledSystemHandle _handle;
        private double _nextEvaluation;

        public string Name => "Core Performance Gate";
        public CorePerformanceHealth Health { get; private set; } = CorePerformanceHealth.Unknown;
        public string LastReason { get; private set; } = "Not evaluated";
        public long WarningEvaluations { get; private set; }
        public long FailingEvaluations { get; private set; }

        public CorePerformanceGate(
            LiteNetLibManager manager,
            CorePerformanceGateSettings settings,
            CoreServerTelemetry telemetry,
            CoreSoakMonitor soak,
            CoreBoundedWorkerPool workers)
        {
            _manager = manager;
            _settings = settings ?? new CorePerformanceGateSettings();
            _telemetry = telemetry;
            _soak = soak;
            _workers = workers;
            _settings.ClampUnsafeValues();
        }

        public void Start()
        {
            if (_handle != null && _handle.IsRegistered)
                return;
            Health = CorePerformanceHealth.Unknown;
            LastReason = "Not evaluated";
            WarningEvaluations = 0;
            FailingEvaluations = 0;
            _nextEvaluation = 0.0;
            _handle = _manager.CoreScheduler.RegisterSystem(this, "Maintenance", false, 2.0);
        }

        public void Stop()
        {
            _handle?.Unregister();
            _handle = null;
            Health = CorePerformanceHealth.Unknown;
            LastReason = "Stopped";
        }

        public void Prepare(in CoreTickContext context) { }

        public void Execute(in CoreTickContext context)
        {
            if (context.Now < _nextEvaluation)
                return;
            _nextEvaluation = context.Now + _settings.evaluationIntervalSeconds;

            CoreServerTelemetrySnapshot telemetry = _telemetry.GetSnapshot();
            CoreSoakMetrics soak = _soak.GetMetricsSnapshot();
            CoreWorkerPoolMetrics workers = _workers.GetMetricsSnapshot();

            int maxJobs = Math.Max(1, _manager.serverFoundationSettings.workers.maxQueuedJobs);
            double queueUtil = workers.queuedJobs / (double)maxJobs;
            double slopeMb = soak.memorySlopeBytesPerHour / (1024.0 * 1024.0);

            if (telemetry.coreFrameP99Ms >= _settings.failingCoreP99Milliseconds)
            {
                SetFail($"Core p99 {telemetry.coreFrameP99Ms:F2} ms");
                return;
            }
            if (queueUtil >= _settings.failingWorkerQueueUtilization)
            {
                SetFail($"Worker queue {queueUtil:P0}");
                return;
            }
            if (_settings.failingMemorySlopeMegabytesPerHour > 0f &&
                slopeMb >= _settings.failingMemorySlopeMegabytesPerHour)
            {
                SetFail($"Memory slope {slopeMb:F1} MB/hour");
                return;
            }

            if (telemetry.coreFrameP99Ms >= _settings.warningCoreP99Milliseconds)
            {
                SetWarning($"Core p99 {telemetry.coreFrameP99Ms:F2} ms");
                return;
            }
            if (queueUtil >= _settings.warningWorkerQueueUtilization)
            {
                SetWarning($"Worker queue {queueUtil:P0}");
                return;
            }
            if (_settings.warningMemorySlopeMegabytesPerHour > 0f &&
                slopeMb >= _settings.warningMemorySlopeMegabytesPerHour)
            {
                SetWarning($"Memory slope {slopeMb:F1} MB/hour");
                return;
            }

            Health = CorePerformanceHealth.Healthy;
            LastReason = "Within configured budgets";
        }

        private void SetWarning(string reason)
        {
            Health = CorePerformanceHealth.Warning;
            LastReason = reason;
            WarningEvaluations++;
        }

        private void SetFail(string reason)
        {
            Health = CorePerformanceHealth.Failing;
            LastReason = reason;
            FailingEvaluations++;
        }

        public void Commit(in CoreTickContext context) { }
    }
}
