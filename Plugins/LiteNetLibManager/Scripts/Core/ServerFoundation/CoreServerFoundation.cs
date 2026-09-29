using System;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreServerFoundationSettings
    {
        public CoreNetworkGovernanceSettings network = new CoreNetworkGovernanceSettings();
        public CoreWorkerPoolSettings workers = new CoreWorkerPoolSettings();
        public CoreTelemetrySettings telemetry = new CoreTelemetrySettings();
        public CoreSoakSettings soak = new CoreSoakSettings();
        public CorePerformanceGateSettings performanceGate = new CorePerformanceGateSettings();
        public CoreNetworkChaosSettings chaos = new CoreNetworkChaosSettings();
        public CoreSyntheticLoadSettings syntheticLoad = new CoreSyntheticLoadSettings();
        public CoreReplicationLodSettings replicationLod = new CoreReplicationLodSettings();
        public CoreGlobalBroadcastSettings globalBroadcast = new CoreGlobalBroadcastSettings();

        internal void ClampUnsafeValues()
        {
            network ??= new CoreNetworkGovernanceSettings();
            workers ??= new CoreWorkerPoolSettings();
            telemetry ??= new CoreTelemetrySettings();
            soak ??= new CoreSoakSettings();
            performanceGate ??= new CorePerformanceGateSettings();
            chaos ??= new CoreNetworkChaosSettings();
            syntheticLoad ??= new CoreSyntheticLoadSettings();
            replicationLod ??= new CoreReplicationLodSettings();
            globalBroadcast ??= new CoreGlobalBroadcastSettings();

            network.ClampUnsafeValues();
            workers.ClampUnsafeValues();
            telemetry.ClampUnsafeValues();
            soak.ClampUnsafeValues();
            performanceGate.ClampUnsafeValues();
            chaos.ClampUnsafeValues();
            syntheticLoad.ClampUnsafeValues();
            replicationLod.ClampUnsafeValues();
            globalBroadcast.ClampUnsafeValues();
        }
    }

    public sealed class CoreServerFoundation : IDisposable
    {
        private readonly LiteNetLibManager _manager;
        private readonly CoreServerFoundationSettings _settings;
        private bool _started;

        public CoreConnectionGovernor NetworkGovernor { get; }
        public CoreBoundedWorkerPool WorkerPool { get; }
        public CoreServerTelemetry Telemetry { get; }
        public CoreSoakMonitor SoakMonitor { get; }
        public CorePerformanceGate PerformanceGate { get; }
        public CoreSyntheticLoadHarness SyntheticLoad { get; }
        public CoreReplicationLodPolicy ReplicationLod { get; }
        public CoreGlobalBroadcaster GlobalBroadcast { get; }

        public bool IsStarted => _started;

        public CoreServerFoundation(LiteNetLibManager manager, CoreServerFoundationSettings settings)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _settings = settings ?? new CoreServerFoundationSettings();
            _settings.ClampUnsafeValues();

            Telemetry = new CoreServerTelemetry(_manager, _settings.telemetry);
            NetworkGovernor = new CoreConnectionGovernor(_manager, _settings.network, Telemetry);
            WorkerPool = new CoreBoundedWorkerPool(_manager, _settings.workers, Telemetry);
            SoakMonitor = new CoreSoakMonitor(_manager, _settings.soak, Telemetry);
            PerformanceGate = new CorePerformanceGate(_manager, _settings.performanceGate, Telemetry, SoakMonitor, WorkerPool);
            SyntheticLoad = new CoreSyntheticLoadHarness(_manager, _settings.syntheticLoad, Telemetry, WorkerPool);
            ReplicationLod = new CoreReplicationLodPolicy(_settings.replicationLod);
            GlobalBroadcast = new CoreGlobalBroadcaster(_manager, _settings.globalBroadcast);
        }

        public void Start()
        {
            if (_started)
                return;

            _settings.ClampUnsafeValues();
            Telemetry.Reset();
            NetworkGovernor.Reset();
            WorkerPool.Start();
            GlobalBroadcast.Start();
            SoakMonitor.Start();
            PerformanceGate.Start();
            if (_settings.syntheticLoad.enabled)
                SyntheticLoad.Start();
            _started = true;
        }

        public void Stop()
        {
            if (!_started)
                return;

            SyntheticLoad.Stop();
            PerformanceGate.Stop();
            SoakMonitor.Stop();
            GlobalBroadcast.Stop();
            WorkerPool.Stop();
            NetworkGovernor.Reset();
            _started = false;
        }

        public void CaptureFrame()
        {
            if (_started)
                Telemetry.CaptureFrame();
        }

        public void ApplyNetworkSimulation(ITransport transport)
        {
            CoreNetworkChaos.Apply(transport, _settings.chaos);
        }

        public void Dispose()
        {
            Stop();
            GlobalBroadcast.Dispose();
            WorkerPool.Dispose();
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class CoreRuntimeBypassAllowedAttribute : Attribute
    {
        public string Reason { get; }

        public CoreRuntimeBypassAllowedAttribute(string reason)
        {
            Reason = reason ?? string.Empty;
        }
    }
}
