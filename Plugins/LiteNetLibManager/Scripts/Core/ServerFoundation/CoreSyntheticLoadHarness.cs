using System;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using LiteNetLib.Utils;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreSyntheticLoadSettings
    {
        public bool enabled = false;
        public string schedulerChannel = "Background";
        [Min(1)] public int syntheticConnections = 500;
        [Min(1)] public int syntheticEntities = 5000;
        [Range(1, 512)] public int averageSubscribersPerDirtyEntity = 80;
        [Range(0f, 1f)] public float dirtyFractionPerPass = 0.10f;
        [Range(0f, 1f)] public float movingFractionPerPass = 0.20f;
        [Range(8, 2048)] public int payloadBytesPerState = 64;
        [Range(1, 1024)] public int entitiesPerWorkUnit = 64;
        [Range(1, 512)] public int maxWorkUnitsPerSchedulerTick = 64;
        [Range(0, 128)] public int workerJobsPerPass = 0;

        internal void ClampUnsafeValues()
        {
            if (string.IsNullOrWhiteSpace(schedulerChannel))
                schedulerChannel = "Background";
            syntheticConnections = Math.Max(1, syntheticConnections);
            syntheticEntities = Math.Max(1, syntheticEntities);
            averageSubscribersPerDirtyEntity = Mathf.Clamp(averageSubscribersPerDirtyEntity, 1, 512);
            dirtyFractionPerPass = Mathf.Clamp01(dirtyFractionPerPass);
            movingFractionPerPass = Mathf.Clamp01(movingFractionPerPass);
            payloadBytesPerState = Mathf.Clamp(payloadBytesPerState, 8, 2048);
            entitiesPerWorkUnit = Mathf.Clamp(entitiesPerWorkUnit, 1, 1024);
            maxWorkUnitsPerSchedulerTick = Mathf.Clamp(maxWorkUnitsPerSchedulerTick, 1, 512);
            workerJobsPerPass = Mathf.Clamp(workerJobsPerPass, 0, 128);
        }
    }

    public struct CoreSyntheticLoadMetrics
    {
        public bool running;
        public long completedPasses;
        public long processedEntities;
        public long dirtyEntities;
        public long subscriberIterations;
        public long simulatedBytes;
        public long workerJobsSubmitted;
        public long workerJobsRejected;
        public double maximumWorkUnitMilliseconds;
    }

    public sealed class CoreSyntheticLoadHarness : ICoreBudgetedWorkSystem
    {
        private readonly LiteNetLibManager _manager;
        private readonly CoreSyntheticLoadSettings _settings;
        private readonly CoreBoundedWorkerPool _workers;
        private readonly NetDataWriter _writer = new NetDataWriter(true, 2048);

        private ICoreScheduledSystemHandle _handle;
        private Vector3[] _positions;
        private uint[] _state;
        private byte[] _payload;
        private int _cursor;
        private uint _randomState = 0xA341316Cu;

        private long _completedPasses;
        private long _processedEntities;
        private long _dirtyEntities;
        private long _subscriberIterations;
        private long _simulatedBytes;
        private long _workerJobsSubmitted;
        private long _workerJobsRejected;
        private long _maximumWorkUnitTicks;

        public string Name => "Core Synthetic Load Harness";
        public bool HasPendingWork => _settings.enabled;

        public CoreSyntheticLoadHarness(
            LiteNetLibManager manager,
            CoreSyntheticLoadSettings settings,
            CoreServerTelemetry telemetry,
            CoreBoundedWorkerPool workers)
        {
            _manager = manager;
            _settings = settings ?? new CoreSyntheticLoadSettings();
            _workers = workers;
            _settings.ClampUnsafeValues();
        }

        public void Start()
        {
            if (!_settings.enabled)
                return;
            if (_handle != null && _handle.IsRegistered)
                return;

            EnsureBuffers();
            _cursor = 0;
            _completedPasses = 0;
            _processedEntities = 0;
            _dirtyEntities = 0;
            _subscriberIterations = 0;
            _simulatedBytes = 0;
            _workerJobsSubmitted = 0;
            _workerJobsRejected = 0;
            Interlocked.Exchange(ref _maximumWorkUnitTicks, 0);
            _handle = _manager.CoreScheduler.RegisterWorkSystem(
                this,
                _settings.schedulerChannel,
                _settings.maxWorkUnitsPerSchedulerTick,
                true,
                2.0);
        }

        public void Stop()
        {
            _handle?.Unregister();
            _handle = null;
            _positions = null;
            _state = null;
            _payload = null;
            _cursor = 0;
        }

        private void EnsureBuffers()
        {
            if (_positions == null || _positions.Length != _settings.syntheticEntities)
            {
                _positions = new Vector3[_settings.syntheticEntities];
                _state = new uint[_settings.syntheticEntities];
                for (int i = 0; i < _positions.Length; ++i)
                {
                    _positions[i] = new Vector3(i % 128, 0f, i / 128);
                    _state[i] = (uint)i;
                }
            }
            if (_payload == null || _payload.Length != _settings.payloadBytesPerState)
                _payload = new byte[_settings.payloadBytesPerState];
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            EnsureBuffers();
            long start = Stopwatch.GetTimestamp();

            int end = Math.Min(_positions.Length, _cursor + _settings.entitiesPerWorkUnit);
            int subscriberCount = Math.Min(_settings.syntheticConnections, _settings.averageSubscribersPerDirtyEntity);

            for (int i = _cursor; i < end; ++i)
            {
                uint r = NextRandom();
                bool moving = (r & 0xFFFF) < (uint)(_settings.movingFractionPerPass * 65535f);
                bool dirty = ((r >> 16) & 0xFFFF) < (uint)(_settings.dirtyFractionPerPass * 65535f);

                if (moving)
                {
                    Vector3 p = _positions[i];
                    p.x += ((r & 1u) == 0u ? 0.1f : -0.1f);
                    p.z += ((r & 2u) == 0u ? 0.1f : -0.1f);
                    _positions[i] = p;
                }

                _state[i] = _state[i] * 1664525u + 1013904223u;
                _processedEntities++;

                if (!dirty)
                    continue;

                _dirtyEntities++;
                _writer.Reset();
                _writer.PutPackedUInt((uint)i);
                _writer.Put(_positions[i].x);
                _writer.Put(_positions[i].z);
                _writer.Put(_state[i]);
                _writer.Put(_payload);

                int bytes = _writer.Length;
                _subscriberIterations += subscriberCount;
                _simulatedBytes += (long)bytes * subscriberCount;
            }

            _cursor = end;
            if (_cursor >= _positions.Length)
            {
                _cursor = 0;
                _completedPasses++;
                SubmitWorkerLoad();
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            UpdateMaximum(ref _maximumWorkUnitTicks, elapsed);
        }

        private void SubmitWorkerLoad()
        {
            for (int i = 0; i < _settings.workerJobsPerPass; ++i)
            {
                bool accepted = _workers.TryQueue(
                    token =>
                    {
                        ulong value = 1469598103934665603UL;
                        for (int j = 0; j < 20000 && !token.IsCancellationRequested; ++j)
                            value = (value ^ (uint)j) * 1099511628211UL;
                        if (value == 0UL)
                            throw new InvalidOperationException();
                    },
                    null,
                    CoreWorkerPriority.Low,
                    2.0,
                    "SyntheticCPU");

                if (accepted)
                    _workerJobsSubmitted++;
                else
                    _workerJobsRejected++;
            }
        }

        private uint NextRandom()
        {
            uint x = _randomState;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _randomState = x;
            return x;
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

        public CoreSyntheticLoadMetrics GetMetricsSnapshot()
        {
            return new CoreSyntheticLoadMetrics
            {
                running = _handle != null && _handle.IsRegistered,
                completedPasses = _completedPasses,
                processedEntities = _processedEntities,
                dirtyEntities = _dirtyEntities,
                subscriberIterations = _subscriberIterations,
                simulatedBytes = _simulatedBytes,
                workerJobsSubmitted = _workerJobsSubmitted,
                workerJobsRejected = _workerJobsRejected,
                maximumWorkUnitMilliseconds = Interlocked.Read(ref _maximumWorkUnitTicks) * 1000.0 / Stopwatch.Frequency,
            };
        }
    }
}
