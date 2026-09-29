using System;
using System.Collections.Generic;
using System.Net;
using Stopwatch = System.Diagnostics.Stopwatch;
using LiteNetLib;
using UnityEngine;

namespace LiteNetLibManager
{
    public enum CoreNetworkTrafficClass : byte
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3,
    }

    public enum CoreRateLimitAction : byte
    {
        Drop = 0,
        Strike = 1,
        Disconnect = 2,
    }

    [Serializable]
    public sealed class CoreNetworkChannelPolicy
    {
        [Range(0, 63)] public int channelId = 0;
        public CoreNetworkTrafficClass trafficClass = CoreNetworkTrafficClass.Normal;
    }

    [Serializable]
    public sealed class CoreMessageRatePolicy
    {
        [Range(0, 65535)] public int messageType = 0;
        [Min(1)] public int maxPerSecond = 60;
        [Min(1)] public int burst = 20;
        public CoreRateLimitAction action = CoreRateLimitAction.Strike;
    }

    [Serializable]
    public sealed class CoreNetworkGovernanceSettings
    {
        [Header("Inbound Per Connection")]
        [Min(1)] public int maxInboundEventsPerSecond = 512;
        [Min(1024)] public int maxInboundBytesPerSecond = 262144;
        [Range(1f, 10f)] public float inboundBurstSeconds = 2f;

        [Header("Outbound Per Connection")]
        [Min(1024)] public int maxOutboundBytesPerSecond = 524288;
        [Range(1f, 10f)] public float outboundBurstSeconds = 2f;
        [Range(1f, 8f)] public float reliableHardRateMultiplier = 2f;

        [Header("Abuse")]
        [Min(1)] public int strikesBeforeDisconnect = 8;
        [Min(0.1f)] public float strikeDecaySeconds = 10f;
        public bool disconnectPersistentAbusers = true;
        public bool exemptOfflineHost = true;

        [Header("Connection Admission")]
        [Min(1)] public int maxConnectionAttemptsPerSecond = 200;
        [Min(1)] public int maxAttemptsPerAddressPerWindow = 20;
        [Min(1f)] public float addressAttemptWindowSeconds = 10f;
        [Min(16)] public int maxTrackedAdmissionAddresses = 4096;

        [Header("Outstanding Request Cap")]
        [Min(1)] public int maxOutstandingRequests = 4096;

        [Header("Channel Classes")]
        public List<CoreNetworkChannelPolicy> channelPolicies = new List<CoreNetworkChannelPolicy>
        {
            new CoreNetworkChannelPolicy { channelId = 0, trafficClass = CoreNetworkTrafficClass.Critical },
            new CoreNetworkChannelPolicy { channelId = 1, trafficClass = CoreNetworkTrafficClass.High },
            new CoreNetworkChannelPolicy { channelId = 2, trafficClass = CoreNetworkTrafficClass.Normal },
            new CoreNetworkChannelPolicy { channelId = 3, trafficClass = CoreNetworkTrafficClass.Low },
        };

        [Header("Optional Message-Specific Limits")]
        public List<CoreMessageRatePolicy> messagePolicies = new List<CoreMessageRatePolicy>();

        internal void ClampUnsafeValues()
        {
            maxInboundEventsPerSecond = Math.Max(1, maxInboundEventsPerSecond);
            maxInboundBytesPerSecond = Math.Max(1024, maxInboundBytesPerSecond);
            inboundBurstSeconds = Mathf.Clamp(inboundBurstSeconds, 1f, 10f);
            maxOutboundBytesPerSecond = Math.Max(1024, maxOutboundBytesPerSecond);
            outboundBurstSeconds = Mathf.Clamp(outboundBurstSeconds, 1f, 10f);
            reliableHardRateMultiplier = Mathf.Clamp(reliableHardRateMultiplier, 1f, 8f);
            strikesBeforeDisconnect = Math.Max(1, strikesBeforeDisconnect);
            strikeDecaySeconds = Math.Max(0.1f, strikeDecaySeconds);
            maxConnectionAttemptsPerSecond = Math.Max(1, maxConnectionAttemptsPerSecond);
            maxAttemptsPerAddressPerWindow = Math.Max(1, maxAttemptsPerAddressPerWindow);
            addressAttemptWindowSeconds = Math.Max(1f, addressAttemptWindowSeconds);
            maxTrackedAdmissionAddresses = Math.Max(16, maxTrackedAdmissionAddresses);
            maxOutstandingRequests = Math.Max(1, maxOutstandingRequests);
            channelPolicies ??= new List<CoreNetworkChannelPolicy>();
            messagePolicies ??= new List<CoreMessageRatePolicy>();
            if (messagePolicies.Count > 256)
                messagePolicies.RemoveRange(256, messagePolicies.Count - 256);
            for (int i = 0; i < messagePolicies.Count; ++i)
            {
                CoreMessageRatePolicy p = messagePolicies[i];
                if (p == null)
                    continue;
                p.messageType = Mathf.Clamp(p.messageType, 0, ushort.MaxValue);
                p.maxPerSecond = Math.Max(1, p.maxPerSecond);
                p.burst = Math.Max(1, p.burst);
            }
        }
    }

    public struct CoreNetworkGovernanceMetrics
    {
        public int trackedConnections;
        public int trackedAdmissionAddresses;
        public long connectionAttempts;
        public long rejectedConnectionAttempts;
        public long inboundEvents;
        public long inboundBytes;
        public long inboundDrops;
        public long messageRateDrops;
        public long malformedPackets;
        public long outboundMessages;
        public long outboundBytes;
        public long outboundDrops;
        public long hardOutboundLimitHits;
        public long abuseDisconnects;
    }

    public sealed class CoreConnectionGovernor
    {
        private sealed class MessageBucket
        {
            public double Tokens;
            public double LastRefill;
        }

        private sealed class ConnectionState
        {
            public double LastRefill;
            public double InboundEventTokens;
            public double InboundByteTokens;
            public double OutboundSoftTokens;
            public double OutboundHardTokens;
            public int Strikes;
            public double LastStrikeTime;
            public MessageBucket[] MessageBuckets;
        }

        private sealed class AdmissionState
        {
            public int Count;
            public double WindowStart;
            public double LastSeen;
        }

        private readonly LiteNetLibManager _manager;
        private readonly CoreNetworkGovernanceSettings _settings;
        private readonly CoreServerTelemetry _telemetry;
        private readonly Dictionary<long, ConnectionState> _connections = new Dictionary<long, ConnectionState>();
        private readonly Dictionary<string, AdmissionState> _admissionByAddress = new Dictionary<string, AdmissionState>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, int> _messagePolicyIndices = new Dictionary<ushort, int>();
        private readonly CoreNetworkTrafficClass[] _channelClasses = new CoreNetworkTrafficClass[64];

        private double _globalAdmissionTokens;
        private double _globalAdmissionLastRefill;
        private CoreNetworkGovernanceMetrics _metrics;

        public CoreConnectionGovernor(LiteNetLibManager manager, CoreNetworkGovernanceSettings settings, CoreServerTelemetry telemetry)
        {
            _manager = manager;
            _settings = settings ?? new CoreNetworkGovernanceSettings();
            _telemetry = telemetry;
            BuildPolicies();
            Reset();
        }

        private static double Now()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private void BuildPolicies()
        {
            _settings.ClampUnsafeValues();
            for (int i = 0; i < _channelClasses.Length; ++i)
                _channelClasses[i] = CoreNetworkTrafficClass.Normal;

            for (int i = 0; i < _settings.channelPolicies.Count; ++i)
            {
                CoreNetworkChannelPolicy p = _settings.channelPolicies[i];
                if (p == null || p.channelId < 0 || p.channelId >= _channelClasses.Length)
                    continue;
                _channelClasses[p.channelId] = p.trafficClass;
            }

            _messagePolicyIndices.Clear();
            for (int i = 0; i < _settings.messagePolicies.Count; ++i)
            {
                CoreMessageRatePolicy p = _settings.messagePolicies[i];
                if (p != null)
                    _messagePolicyIndices[(ushort)p.messageType] = i;
            }
        }

        public void Reset()
        {
            BuildPolicies();
            _connections.Clear();
            _admissionByAddress.Clear();
            _metrics = default;
            double now = Now();
            _globalAdmissionTokens = _settings.maxConnectionAttemptsPerSecond;
            _globalAdmissionLastRefill = now;
        }

        private ConnectionState CreateState(double now)
        {
            ConnectionState state = new ConnectionState
            {
                LastRefill = now,
                InboundEventTokens = _settings.maxInboundEventsPerSecond * _settings.inboundBurstSeconds,
                InboundByteTokens = _settings.maxInboundBytesPerSecond * _settings.inboundBurstSeconds,
                OutboundSoftTokens = _settings.maxOutboundBytesPerSecond * _settings.outboundBurstSeconds,
                OutboundHardTokens = _settings.maxOutboundBytesPerSecond * _settings.reliableHardRateMultiplier * _settings.outboundBurstSeconds,
                LastStrikeTime = now,
                MessageBuckets = new MessageBucket[_settings.messagePolicies.Count],
            };

            for (int i = 0; i < state.MessageBuckets.Length; ++i)
            {
                CoreMessageRatePolicy p = _settings.messagePolicies[i];
                state.MessageBuckets[i] = new MessageBucket
                {
                    Tokens = p != null ? p.burst : 1,
                    LastRefill = now,
                };
            }
            return state;
        }

        private ConnectionState GetOrCreate(long connectionId, double now)
        {
            if (!_connections.TryGetValue(connectionId, out ConnectionState state))
            {
                state = CreateState(now);
                _connections[connectionId] = state;
            }
            return state;
        }

        public void OnConnected(long connectionId)
        {
            if (_settings.exemptOfflineHost && _manager.IsOfflineConnection)
                return;
            _connections[connectionId] = CreateState(Now());
        }

        public void OnDisconnected(long connectionId)
        {
            _connections.Remove(connectionId);
        }

        private void Refill(ConnectionState state, double now)
        {
            double delta = Math.Max(0.0, now - state.LastRefill);
            if (delta <= 0.0)
                return;

            state.InboundEventTokens = Math.Min(
                _settings.maxInboundEventsPerSecond * _settings.inboundBurstSeconds,
                state.InboundEventTokens + delta * _settings.maxInboundEventsPerSecond);
            state.InboundByteTokens = Math.Min(
                _settings.maxInboundBytesPerSecond * _settings.inboundBurstSeconds,
                state.InboundByteTokens + delta * _settings.maxInboundBytesPerSecond);
            state.OutboundSoftTokens = Math.Min(
                _settings.maxOutboundBytesPerSecond * _settings.outboundBurstSeconds,
                state.OutboundSoftTokens + delta * _settings.maxOutboundBytesPerSecond);

            double hardRate = _settings.maxOutboundBytesPerSecond * _settings.reliableHardRateMultiplier;
            state.OutboundHardTokens = Math.Min(
                hardRate * _settings.outboundBurstSeconds,
                state.OutboundHardTokens + delta * hardRate);

            if (state.Strikes > 0 && now - state.LastStrikeTime >= _settings.strikeDecaySeconds)
            {
                int decay = Math.Max(1, (int)((now - state.LastStrikeTime) / _settings.strikeDecaySeconds));
                state.Strikes = Math.Max(0, state.Strikes - decay);
                state.LastStrikeTime = now;
            }
            state.LastRefill = now;
        }

        private void Strike(long connectionId, ConnectionState state, double now)
        {
            state.Strikes++;
            state.LastStrikeTime = now;
            if (_settings.disconnectPersistentAbusers && state.Strikes >= _settings.strikesBeforeDisconnect)
            {
                _metrics.abuseDisconnects++;
                _telemetry?.RecordAbuseDisconnect();
                _manager.ServerTransport?.ServerDisconnect(connectionId);
                state.Strikes = 0;
            }
        }

        public bool AllowInboundPacket(long connectionId, int bytes)
        {
            _metrics.inboundEvents++;
            _metrics.inboundBytes += Math.Max(0, bytes);
            _telemetry?.RecordInbound(Math.Max(0, bytes));

            if (_settings.exemptOfflineHost && _manager.IsOfflineConnection)
                return true;

            double now = Now();
            ConnectionState state = GetOrCreate(connectionId, now);
            Refill(state, now);

            double byteCost = Math.Max(0, bytes);
            if (state.InboundEventTokens < 1.0 || state.InboundByteTokens < byteCost)
            {
                _metrics.inboundDrops++;
                _telemetry?.RecordInboundDrop();
                Strike(connectionId, state, now);
                return false;
            }

            state.InboundEventTokens -= 1.0;
            state.InboundByteTokens -= byteCost;
            return true;
        }

        public void ReportMalformedPacket(long connectionId)
        {
            _metrics.malformedPackets++;
            _metrics.inboundDrops++;
            _telemetry?.RecordMalformedPacket();
            _telemetry?.RecordInboundDrop();
            if (_settings.exemptOfflineHost && _manager.IsOfflineConnection)
                return;
            double now = Now();
            ConnectionState state = GetOrCreate(connectionId, now);
            Refill(state, now);
            Strike(connectionId, state, now);
        }

        public bool AllowMessage(long connectionId, ushort messageType, int remainingBytes)
        {
            if (_settings.exemptOfflineHost && _manager.IsOfflineConnection)
                return true;
            if (!_messagePolicyIndices.TryGetValue(messageType, out int index))
                return true;
            if (index < 0 || index >= _settings.messagePolicies.Count)
                return true;

            CoreMessageRatePolicy policy = _settings.messagePolicies[index];
            if (policy == null)
                return true;

            double now = Now();
            ConnectionState state = GetOrCreate(connectionId, now);
            MessageBucket bucket = state.MessageBuckets[index];
            double delta = Math.Max(0.0, now - bucket.LastRefill);
            bucket.Tokens = Math.Min(policy.burst, bucket.Tokens + delta * policy.maxPerSecond);
            bucket.LastRefill = now;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return true;
            }

            _metrics.messageRateDrops++;
            _telemetry?.RecordMessageRateDrop();

            switch (policy.action)
            {
                case CoreRateLimitAction.Disconnect:
                    _manager.ServerTransport?.ServerDisconnect(connectionId);
                    _metrics.abuseDisconnects++;
                    _telemetry?.RecordAbuseDisconnect();
                    break;
                case CoreRateLimitAction.Strike:
                    Strike(connectionId, state, now);
                    break;
            }
            return false;
        }

        public bool AllowOutbound(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, int bytes)
        {
            bytes = Math.Max(0, bytes);
            if (_settings.exemptOfflineHost && _manager.IsOfflineConnection)
            {
                _metrics.outboundMessages++;
                _metrics.outboundBytes += bytes;
                _telemetry?.RecordOutbound(bytes);
                return true;
            }

            double now = Now();
            ConnectionState state = GetOrCreate(connectionId, now);
            Refill(state, now);

            bool isReliable =
                deliveryMethod == DeliveryMethod.ReliableOrdered ||
                deliveryMethod == DeliveryMethod.ReliableUnordered;

            CoreNetworkTrafficClass trafficClass =
                dataChannel < _channelClasses.Length ? _channelClasses[dataChannel] : CoreNetworkTrafficClass.Normal;

            if (state.OutboundHardTokens < bytes)
            {
                _metrics.hardOutboundLimitHits++;
                _metrics.outboundDrops++;
                _telemetry?.RecordOutboundDrop(bytes);
                Strike(connectionId, state, now);
                if (isReliable && _settings.disconnectPersistentAbusers)
                    _manager.ServerTransport?.ServerDisconnect(connectionId);
                return false;
            }

            if (state.OutboundSoftTokens < bytes)
            {
                if (!isReliable || trafficClass == CoreNetworkTrafficClass.Low)
                {
                    _metrics.outboundDrops++;
                    _telemetry?.RecordOutboundDrop(bytes);
                    return false;
                }
            }
            else
            {
                state.OutboundSoftTokens -= bytes;
            }

            state.OutboundHardTokens -= bytes;
            _metrics.outboundMessages++;
            _metrics.outboundBytes += bytes;
            _telemetry?.RecordOutbound(bytes);
            return true;
        }

        public bool AllowConnectionAttempt(IPEndPoint endPoint)
        {
            _metrics.connectionAttempts++;
            double now = Now();

            double globalDelta = Math.Max(0.0, now - _globalAdmissionLastRefill);
            _globalAdmissionTokens = Math.Min(
                _settings.maxConnectionAttemptsPerSecond,
                _globalAdmissionTokens + globalDelta * _settings.maxConnectionAttemptsPerSecond);
            _globalAdmissionLastRefill = now;

            if (_globalAdmissionTokens < 1.0)
            {
                _metrics.rejectedConnectionAttempts++;
                _telemetry?.RecordRejectedConnectionAttempt();
                return false;
            }
            _globalAdmissionTokens -= 1.0;

            if (endPoint?.Address == null)
                return true;

            string key = endPoint.Address.ToString();
            if (!_admissionByAddress.TryGetValue(key, out AdmissionState state))
            {
                if (_admissionByAddress.Count >= _settings.maxTrackedAdmissionAddresses)
                    EvictOldestAdmissionEntry();

                state = new AdmissionState
                {
                    WindowStart = now,
                    LastSeen = now,
                };
                _admissionByAddress[key] = state;
            }

            if (now - state.WindowStart >= _settings.addressAttemptWindowSeconds)
            {
                state.WindowStart = now;
                state.Count = 0;
            }
            state.LastSeen = now;
            state.Count++;

            if (state.Count > _settings.maxAttemptsPerAddressPerWindow)
            {
                _metrics.rejectedConnectionAttempts++;
                _telemetry?.RecordRejectedConnectionAttempt();
                return false;
            }
            return true;
        }

        private void EvictOldestAdmissionEntry()
        {
            string oldestKey = null;
            double oldest = double.MaxValue;
            foreach (KeyValuePair<string, AdmissionState> kvp in _admissionByAddress)
            {
                if (kvp.Value.LastSeen < oldest)
                {
                    oldest = kvp.Value.LastSeen;
                    oldestKey = kvp.Key;
                }
            }
            if (oldestKey != null)
                _admissionByAddress.Remove(oldestKey);
        }

        public CoreNetworkGovernanceMetrics GetMetricsSnapshot()
        {
            CoreNetworkGovernanceMetrics result = _metrics;
            result.trackedConnections = _connections.Count;
            result.trackedAdmissionAddresses = _admissionByAddress.Count;
            return result;
        }
    }
}
