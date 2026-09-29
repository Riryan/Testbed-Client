using System;
using System.Buffers;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace LiteNetLibManager
{
    [Serializable]
    public sealed class CoreGlobalBroadcastSettings
    {
        [Tooltip("Configured scheduler channel used to drain global broadcasts.")]
        public string schedulerChannel = "Gameplay";
        [Min(1)] public int maxQueuedBroadcasts = 1024;
        [Min(64)] public int maxPayloadBytes = 65535;
        [Range(1, 1024)] public int recipientsPerWorkUnit = 64;
        [Range(1, 512)] public int maxWorkUnitsPerSchedulerTick = 64;

        internal void ClampUnsafeValues()
        {
            if (string.IsNullOrWhiteSpace(schedulerChannel))
                schedulerChannel = "Gameplay";
            maxQueuedBroadcasts = Math.Max(1, maxQueuedBroadcasts);
            maxPayloadBytes = Math.Max(64, maxPayloadBytes);
            recipientsPerWorkUnit = Mathf.Clamp(recipientsPerWorkUnit, 1, 1024);
            maxWorkUnitsPerSchedulerTick = Mathf.Clamp(maxWorkUnitsPerSchedulerTick, 1, 512);
        }
    }

    public struct CoreGlobalBroadcastMetrics
    {
        public bool running;
        public int queuedBroadcasts;
        public long acceptedBroadcasts;
        public long rejectedBroadcasts;
        public long completedBroadcasts;
        public long recipientSends;
        public long skippedDisconnectedRecipients;
    }

    /// <summary>
    /// Bounded non-spatial/global replication path. Use this for small global state that
    /// genuinely needs fan-out beyond AOI. It snapshots recipients at admission time,
    /// then spreads sends across scheduler work units. Per-connection bandwidth governance
    /// still applies because delivery goes through LiteNetLibServer.SendMessage().
    /// </summary>
    public sealed class CoreGlobalBroadcaster : ICoreBudgetedWorkSystem, IDisposable
    {
        private sealed class BroadcastItem
        {
            public byte[] Payload;
            public int PayloadLength;
            public long[] Recipients;
            public int RecipientCount;
            public int Cursor;
            public byte DataChannel;
            public DeliveryMethod DeliveryMethod;
        }

        private readonly LiteNetLibManager _manager;
        private readonly CoreGlobalBroadcastSettings _settings;
        private readonly Queue<BroadcastItem> _queue = new Queue<BroadcastItem>();
        private readonly NetDataWriter _buildWriter = new NetDataWriter(true, 256);
        private readonly NetDataWriter _sendWriter = new NetDataWriter(true, 256);
        private ICoreScheduledSystemHandle _handle;
        private BroadcastItem _active;

        private long _accepted;
        private long _rejected;
        private long _completed;
        private long _recipientSends;
        private long _skippedDisconnected;

        public string Name => "Core Global Broadcaster";
        public bool HasPendingWork => _active != null || _queue.Count > 0;

        public CoreGlobalBroadcaster(LiteNetLibManager manager, CoreGlobalBroadcastSettings settings)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _settings = settings ?? new CoreGlobalBroadcastSettings();
            _settings.ClampUnsafeValues();
        }

        public void Start()
        {
            if (_handle != null && _handle.IsRegistered)
                return;
            _settings.ClampUnsafeValues();
            _accepted = 0;
            _rejected = 0;
            _completed = 0;
            _recipientSends = 0;
            _skippedDisconnected = 0;
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
            ReleaseActive();
            while (_queue.Count > 0)
                Release(_queue.Dequeue());
        }

        public bool TryBroadcastPacket(
            byte dataChannel,
            DeliveryMethod deliveryMethod,
            ushort messageType,
            SerializerDelegate serializer = null)
        {
            if (!_manager.IsServer || _manager.Server == null)
            {
                _rejected++;
                return false;
            }
            if (_queue.Count + (_active != null ? 1 : 0) >= _settings.maxQueuedBroadcasts)
            {
                _rejected++;
                return false;
            }

            TransportHandler.WritePacket(_buildWriter, messageType, serializer);
            return TryEnqueueWriter(dataChannel, deliveryMethod, _buildWriter);
        }

        public bool TryBroadcastPacket<T>(
            byte dataChannel,
            DeliveryMethod deliveryMethod,
            ushort messageType,
            T messageData,
            SerializerDelegate extraSerializer = null)
            where T : INetSerializable
        {
            if (!_manager.IsServer || _manager.Server == null)
            {
                _rejected++;
                return false;
            }
            if (_queue.Count + (_active != null ? 1 : 0) >= _settings.maxQueuedBroadcasts)
            {
                _rejected++;
                return false;
            }

            TransportHandler.WritePacket(_buildWriter, messageType);
            messageData.Serialize(_buildWriter);
            extraSerializer?.Invoke(_buildWriter);
            return TryEnqueueWriter(dataChannel, deliveryMethod, _buildWriter);
        }

        private bool TryEnqueueWriter(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            int length = writer?.Length ?? 0;
            if (length <= 0 || length > _settings.maxPayloadBytes)
            {
                _rejected++;
                return false;
            }

            HashSet<long> connections = _manager.Server.ConnectionIds;
            int recipientCount = connections.Count;
            if (recipientCount <= 0)
            {
                _accepted++;
                _completed++;
                return true;
            }

            byte[] payload = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(writer.Data, 0, payload, 0, length);
            long[] recipients = ArrayPool<long>.Shared.Rent(recipientCount);
            int cursor = 0;
            foreach (long connectionId in connections)
            {
                if (cursor >= recipientCount)
                    break;
                recipients[cursor++] = connectionId;
            }

            _queue.Enqueue(new BroadcastItem
            {
                Payload = payload,
                PayloadLength = length,
                Recipients = recipients,
                RecipientCount = cursor,
                Cursor = 0,
                DataChannel = dataChannel,
                DeliveryMethod = deliveryMethod,
            });
            _accepted++;
            return true;
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (_active == null)
            {
                if (_queue.Count <= 0)
                    return;
                _active = _queue.Dequeue();
                _sendWriter.Reset(_active.PayloadLength);
                _sendWriter.Put(_active.Payload, 0, _active.PayloadLength);
            }

            int count = 0;
            while (count < _settings.recipientsPerWorkUnit && _active.Cursor < _active.RecipientCount)
            {
                long connectionId = _active.Recipients[_active.Cursor++];
                if (_manager.Server.ConnectionIds.Contains(connectionId))
                {
                    _manager.Server.SendMessage(connectionId, _active.DataChannel, _active.DeliveryMethod, _sendWriter);
                    _recipientSends++;
                }
                else
                {
                    _skippedDisconnected++;
                }
                count++;
            }

            if (_active.Cursor >= _active.RecipientCount)
            {
                _completed++;
                ReleaseActive();
            }
        }

        private void ReleaseActive()
        {
            if (_active == null)
                return;
            Release(_active);
            _active = null;
        }

        private static void Release(BroadcastItem item)
        {
            if (item == null)
                return;
            if (item.Payload != null)
                ArrayPool<byte>.Shared.Return(item.Payload, false);
            if (item.Recipients != null)
                ArrayPool<long>.Shared.Return(item.Recipients, false);
            item.Payload = null;
            item.Recipients = null;
        }

        public CoreGlobalBroadcastMetrics GetMetricsSnapshot()
        {
            return new CoreGlobalBroadcastMetrics
            {
                running = _handle != null && _handle.IsRegistered,
                queuedBroadcasts = _queue.Count + (_active != null ? 1 : 0),
                acceptedBroadcasts = _accepted,
                rejectedBroadcasts = _rejected,
                completedBroadcasts = _completed,
                recipientSends = _recipientSends,
                skippedDisconnectedRecipients = _skippedDisconnected,
            };
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
