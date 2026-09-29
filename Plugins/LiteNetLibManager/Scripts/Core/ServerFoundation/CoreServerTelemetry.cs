using System;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreTelemetrySettings
    {
        [Range(128, 8192)] public int sampleWindow = 2048;
        [Min(1)] public int memorySampleEveryFrames = 60;

        internal void ClampUnsafeValues()
        {
            sampleWindow = Mathf.Clamp(sampleWindow, 128, 8192);
            memorySampleEveryFrames = Math.Max(1, memorySampleEveryFrames);
        }
    }

    public struct CoreServerTelemetrySnapshot
    {
        public long frameSamples;
        public double coreFrameP50Ms;
        public double coreFrameP95Ms;
        public double coreFrameP99Ms;
        public double networkTickP50Ms;
        public double networkTickP95Ms;
        public double networkTickP99Ms;
        public double maxObservedCoreFrameMs;
        public long managedMemoryBytes;
        public int connectedPeers;

        public long inboundBytes;
        public long inboundEvents;
        public long inboundDrops;
        public long messageRateDrops;
        public long malformedPackets;
        public long outboundBytes;
        public long outboundMessages;
        public long outboundDrops;
        public long rejectedConnectionAttempts;
        public long abuseDisconnects;

        public CoreRuntimeSchedulerMetrics scheduler;
        public CoreNetworkGovernanceMetrics network;
        public CoreWorkerPoolMetrics workers;
        public CoreSoakMetrics soak;
        public CoreSyntheticLoadMetrics synthetic;
        public CoreGlobalBroadcastMetrics globalBroadcast;
        public int performanceHealth;
        public long performanceWarningEvaluations;
        public long performanceFailingEvaluations;

        public int aoiTargets;
        public int aoiObservers;
        public int aoiGridCells;
        public long aoiCandidateChecks;
        public long aoiCandidateLimitHits;
        public long aoiSubscriptionAdds;
        public long aoiSubscriptionRemoves;
        public int aoiMaximumSubscribers;
    }

    internal sealed class CoreFixedSampleWindow
    {
        private readonly double[] _samples;
        private readonly double[] _scratch;
        private int _count;
        private int _cursor;
        private long _totalSamples;
        private double _maximum;

        public CoreFixedSampleWindow(int capacity)
        {
            capacity = Math.Max(16, capacity);
            _samples = new double[capacity];
            _scratch = new double[capacity];
        }

        public long TotalSamples => _totalSamples;
        public double Maximum => _maximum;

        public void Clear()
        {
            Array.Clear(_samples, 0, _samples.Length);
            _count = 0;
            _cursor = 0;
            _totalSamples = 0;
            _maximum = 0.0;
        }

        public void Add(double value)
        {
            if (value < 0.0)
                value = 0.0;
            _samples[_cursor] = value;
            _cursor = (_cursor + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
            _totalSamples++;
            if (value > _maximum)
                _maximum = value;
        }

        public void GetCommonPercentiles(out double p50, out double p95, out double p99)
        {
            if (_count <= 0)
            {
                p50 = 0.0;
                p95 = 0.0;
                p99 = 0.0;
                return;
            }

            Array.Copy(_samples, _scratch, _count);
            Array.Sort(_scratch, 0, _count);
            p50 = _scratch[(int)Math.Ceiling((_count - 1) * 0.50)];
            p95 = _scratch[(int)Math.Ceiling((_count - 1) * 0.95)];
            p99 = _scratch[(int)Math.Ceiling((_count - 1) * 0.99)];
        }
    }

    public sealed class CoreServerTelemetry
    {
        private readonly LiteNetLibManager _manager;
        private readonly CoreTelemetrySettings _settings;
        private readonly CoreFixedSampleWindow _coreFrameSamples;
        private readonly CoreFixedSampleWindow _networkTickSamples;
        private int _frameCounter;
        private long _managedMemoryBytes;

        private long _inboundBytes;
        private long _inboundEvents;
        private long _inboundDrops;
        private long _messageRateDrops;
        private long _malformedPackets;
        private long _outboundBytes;
        private long _outboundMessages;
        private long _outboundDrops;
        private long _rejectedConnectionAttempts;
        private long _abuseDisconnects;

        public CoreServerTelemetry(LiteNetLibManager manager, CoreTelemetrySettings settings)
        {
            _manager = manager;
            _settings = settings ?? new CoreTelemetrySettings();
            _settings.ClampUnsafeValues();
            _coreFrameSamples = new CoreFixedSampleWindow(_settings.sampleWindow);
            _networkTickSamples = new CoreFixedSampleWindow(_settings.sampleWindow);
        }

        public void Reset()
        {
            _coreFrameSamples.Clear();
            _networkTickSamples.Clear();
            _frameCounter = 0;
            _managedMemoryBytes = GC.GetTotalMemory(false);
            Interlocked.Exchange(ref _inboundBytes, 0);
            Interlocked.Exchange(ref _inboundEvents, 0);
            Interlocked.Exchange(ref _inboundDrops, 0);
            Interlocked.Exchange(ref _messageRateDrops, 0);
            Interlocked.Exchange(ref _malformedPackets, 0);
            Interlocked.Exchange(ref _outboundBytes, 0);
            Interlocked.Exchange(ref _outboundMessages, 0);
            Interlocked.Exchange(ref _outboundDrops, 0);
            Interlocked.Exchange(ref _rejectedConnectionAttempts, 0);
            Interlocked.Exchange(ref _abuseDisconnects, 0);
        }

        public void CaptureFrame()
        {
            CoreRuntimeSchedulerMetrics scheduler = _manager.CoreScheduler.GetMetricsSnapshot();
            _coreFrameSamples.Add(scheduler.lastFrameMilliseconds);
            if (_manager.LogicUpdater != null)
                _networkTickSamples.Add(_manager.LogicUpdater.LastUpdateMilliseconds);

            _frameCounter++;
            if (_frameCounter >= _settings.memorySampleEveryFrames)
            {
                _frameCounter = 0;
                _managedMemoryBytes = GC.GetTotalMemory(false);
            }
        }

        public void RecordInbound(int bytes)
        {
            Interlocked.Increment(ref _inboundEvents);
            Interlocked.Add(ref _inboundBytes, Math.Max(0, bytes));
        }

        public void RecordInboundDrop() => Interlocked.Increment(ref _inboundDrops);
        public void RecordMessageRateDrop() => Interlocked.Increment(ref _messageRateDrops);
        public void RecordMalformedPacket() => Interlocked.Increment(ref _malformedPackets);

        public void RecordOutbound(int bytes)
        {
            Interlocked.Increment(ref _outboundMessages);
            Interlocked.Add(ref _outboundBytes, Math.Max(0, bytes));
        }

        public void RecordOutboundDrop(int bytes) => Interlocked.Increment(ref _outboundDrops);
        public void RecordRejectedConnectionAttempt() => Interlocked.Increment(ref _rejectedConnectionAttempts);
        public void RecordAbuseDisconnect() => Interlocked.Increment(ref _abuseDisconnects);

        public CoreServerTelemetrySnapshot GetSnapshot()
        {
            _coreFrameSamples.GetCommonPercentiles(out double coreP50, out double coreP95, out double coreP99);
            _networkTickSamples.GetCommonPercentiles(out double tickP50, out double tickP95, out double tickP99);

            CoreServerTelemetrySnapshot result = new CoreServerTelemetrySnapshot
            {
                frameSamples = _coreFrameSamples.TotalSamples,
                coreFrameP50Ms = coreP50,
                coreFrameP95Ms = coreP95,
                coreFrameP99Ms = coreP99,
                networkTickP50Ms = tickP50,
                networkTickP95Ms = tickP95,
                networkTickP99Ms = tickP99,
                maxObservedCoreFrameMs = _coreFrameSamples.Maximum,
                managedMemoryBytes = _managedMemoryBytes,
                connectedPeers = _manager.ServerTransport?.ServerPeersCount ?? 0,
                inboundBytes = Interlocked.Read(ref _inboundBytes),
                inboundEvents = Interlocked.Read(ref _inboundEvents),
                inboundDrops = Interlocked.Read(ref _inboundDrops),
                messageRateDrops = Interlocked.Read(ref _messageRateDrops),
                malformedPackets = Interlocked.Read(ref _malformedPackets),
                outboundBytes = Interlocked.Read(ref _outboundBytes),
                outboundMessages = Interlocked.Read(ref _outboundMessages),
                outboundDrops = Interlocked.Read(ref _outboundDrops),
                rejectedConnectionAttempts = Interlocked.Read(ref _rejectedConnectionAttempts),
                abuseDisconnects = Interlocked.Read(ref _abuseDisconnects),
                scheduler = _manager.CoreScheduler.GetMetricsSnapshot(),
            };

            CoreServerFoundation foundation = _manager.ServerFoundation;
            if (foundation != null)
            {
                result.network = foundation.NetworkGovernor.GetMetricsSnapshot();
                result.workers = foundation.WorkerPool.GetMetricsSnapshot();
                result.soak = foundation.SoakMonitor.GetMetricsSnapshot();
                result.synthetic = foundation.SyntheticLoad.GetMetricsSnapshot();
                result.globalBroadcast = foundation.GlobalBroadcast.GetMetricsSnapshot();
                result.performanceHealth = (int)foundation.PerformanceGate.Health;
                result.performanceWarningEvaluations = foundation.PerformanceGate.WarningEvaluations;
                result.performanceFailingEvaluations = foundation.PerformanceGate.FailingEvaluations;
            }

            if (_manager is LiteNetLibGameManager gameManager &&
                gameManager.InterestManager is SpatialInterestManager aoi)
            {
                result.aoiTargets = aoi.RuntimeTargetCount;
                result.aoiObservers = aoi.RuntimeObserverCount;
                result.aoiGridCells = aoi.RuntimeGridCellCount;
                result.aoiCandidateChecks = aoi.CandidateChecks;
                result.aoiCandidateLimitHits = aoi.CandidateLimitHits;
                result.aoiSubscriptionAdds = aoi.SubscriptionAdds;
                result.aoiSubscriptionRemoves = aoi.SubscriptionRemoves;
                result.aoiMaximumSubscribers = aoi.MaximumObservedSubscribers;
            }

            return result;
        }

        public string GetPrometheusText()
        {
            CoreServerTelemetrySnapshot s = GetSnapshot();
            StringBuilder b = new StringBuilder(2048);
            Append(b, "lnlm_connected_peers", s.connectedPeers);
            Append(b, "lnlm_managed_memory_bytes", s.managedMemoryBytes);
            Append(b, "lnlm_core_frame_p50_ms", s.coreFrameP50Ms);
            Append(b, "lnlm_core_frame_p95_ms", s.coreFrameP95Ms);
            Append(b, "lnlm_core_frame_p99_ms", s.coreFrameP99Ms);
            Append(b, "lnlm_network_tick_p50_ms", s.networkTickP50Ms);
            Append(b, "lnlm_network_tick_p95_ms", s.networkTickP95Ms);
            Append(b, "lnlm_network_tick_p99_ms", s.networkTickP99Ms);
            Append(b, "lnlm_network_inbound_bytes_total", s.inboundBytes);
            Append(b, "lnlm_network_inbound_drops_total", s.inboundDrops);
            Append(b, "lnlm_network_malformed_packets_total", s.malformedPackets);
            Append(b, "lnlm_network_outbound_bytes_total", s.outboundBytes);
            Append(b, "lnlm_network_outbound_drops_total", s.outboundDrops);
            Append(b, "lnlm_network_message_rate_drops_total", s.messageRateDrops);
            Append(b, "lnlm_network_rejected_connection_attempts_total", s.rejectedConnectionAttempts);
            Append(b, "lnlm_network_abuse_disconnects_total", s.abuseDisconnects);
            Append(b, "lnlm_scheduler_frame_budget_hits_total", s.scheduler.frameBudgetHits);
            Append(b, "lnlm_scheduler_quarantines_total", s.scheduler.quarantineEvents);
            Append(b, "lnlm_worker_queue_depth", s.workers.queuedJobs);
            Append(b, "lnlm_worker_completion_queue_depth", s.workers.queuedCompletions);
            Append(b, "lnlm_global_broadcast_queue_depth", s.globalBroadcast.queuedBroadcasts);
            Append(b, "lnlm_global_broadcast_rejected_total", s.globalBroadcast.rejectedBroadcasts);
            Append(b, "lnlm_soak_memory_slope_bytes_per_hour", s.soak.memorySlopeBytesPerHour);
            Append(b, "lnlm_performance_health", s.performanceHealth);
            Append(b, "lnlm_performance_warning_evaluations_total", s.performanceWarningEvaluations);
            Append(b, "lnlm_performance_failing_evaluations_total", s.performanceFailingEvaluations);
            Append(b, "lnlm_synthetic_completed_passes_total", s.synthetic.completedPasses);
            Append(b, "lnlm_synthetic_simulated_bytes_total", s.synthetic.simulatedBytes);
            Append(b, "lnlm_aoi_targets", s.aoiTargets);
            Append(b, "lnlm_aoi_observers", s.aoiObservers);
            Append(b, "lnlm_aoi_grid_cells", s.aoiGridCells);
            Append(b, "lnlm_aoi_candidate_checks_total", s.aoiCandidateChecks);
            Append(b, "lnlm_aoi_candidate_limit_hits_total", s.aoiCandidateLimitHits);
            return b.ToString();
        }

        private static void Append(StringBuilder b, string name, double value)
        {
            b.Append(name).Append(' ').Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void Append(StringBuilder b, string name, long value)
        {
            b.Append(name).Append(' ').Append(value).Append('\n');
        }

        private static void Append(StringBuilder b, string name, int value)
        {
            b.Append(name).Append(' ').Append(value).Append('\n');
        }
    }
}
