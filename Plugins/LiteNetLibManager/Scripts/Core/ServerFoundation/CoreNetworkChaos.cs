using System;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreNetworkChaosSettings
    {
        public bool enabled = false;
        public bool simulatePacketLoss = true;
        [Range(0, 100)] public int packetLossPercent = 5;
        public bool simulateLatency = true;
        [Min(0)] public int minimumRoundTripLatencyMilliseconds = 50;
        [Min(0)] public int maximumRoundTripLatencyMilliseconds = 150;

        internal void ClampUnsafeValues()
        {
            packetLossPercent = Mathf.Clamp(packetLossPercent, 0, 100);
            minimumRoundTripLatencyMilliseconds = Math.Max(0, minimumRoundTripLatencyMilliseconds);
            maximumRoundTripLatencyMilliseconds = Math.Max(minimumRoundTripLatencyMilliseconds, maximumRoundTripLatencyMilliseconds);
        }
    }

    public static class CoreNetworkChaos
    {
        public static void Apply(ITransport transport, CoreNetworkChaosSettings settings)
        {
            if (!(transport is LiteNetLibTransport liteNetTransport))
                return;
            settings ??= new CoreNetworkChaosSettings();
            settings.ClampUnsafeValues();

            Apply(liteNetTransport.Server, settings);
            Apply(liteNetTransport.Client, settings);
        }

        private static void Apply(LiteNetLib.NetManager manager, CoreNetworkChaosSettings settings)
        {
            if (manager == null)
                return;

            manager.SimulatePacketLoss = settings.enabled && settings.simulatePacketLoss;
            manager.SimulationPacketLossChance = settings.packetLossPercent;
            manager.SimulateLatency = settings.enabled && settings.simulateLatency;
            manager.SimulationMinLatency = settings.minimumRoundTripLatencyMilliseconds;
            manager.SimulationMaxLatency = settings.maximumRoundTripLatencyMilliseconds;
        }
    }
}
