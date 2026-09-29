using System;
using System.Collections.Concurrent;
using LiteNetLib;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        private readonly ConcurrentQueue<PlayerServiceStatusMessage> _pendingPlayerServiceStatuses =
            new ConcurrentQueue<PlayerServiceStatusMessage>();

        private bool _hasLatestBackendServiceStatus;
        private PlayerServiceStatusMessage _latestBackendServiceStatus;

        public event Action<ClientServiceStatusNotice> ClientServiceStatusChanged;

        public bool TryGetLatestBackendServiceStatus(out ClientServiceStatusNotice notice)
        {
            if (!_hasLatestBackendServiceStatus)
            {
                notice = default;
                return false;
            }

            notice = new ClientServiceStatusNotice(
                _latestBackendServiceStatus.Service,
                _latestBackendServiceStatus.available,
                _latestBackendServiceStatus.message);
            return true;
        }

        private void RegisterPlayerServiceStatusMessages()
        {
            RegisterClientMessage(PlayerServiceStatusMessageTypes.Status, HandlePlayerServiceStatus);
        }

        private void HandleBackendEventStreamAvailabilityChanged(bool available, string message)
        {
            _pendingPlayerServiceStatuses.Enqueue(new PlayerServiceStatusMessage
            {
                service = (byte)PlayerServiceKind.Backend,
                available = available,
                message = message ?? string.Empty,
            });
        }

        private void FlushPendingPlayerServiceStatuses(int maxPerTick = 16)
        {
            if (!IsServer || maxPerTick <= 0)
                return;

            int processed = 0;
            while (processed < maxPerTick &&
                   _pendingPlayerServiceStatuses.TryDequeue(out PlayerServiceStatusMessage status))
            {
                foreach (long connectionId in _playerSessionHandles.Keys)
                {
                    ServerSendPacket(
                        connectionId,
                        0,
                        DeliveryMethod.ReliableOrdered,
                        PlayerServiceStatusMessageTypes.Status,
                        status);
                }
                processed++;
            }
        }

        private void HandlePlayerServiceStatus(MessageHandlerData handler)
        {
            PlayerServiceStatusMessage status = handler.ReadMessage<PlayerServiceStatusMessage>();
            if (status.Service == PlayerServiceKind.Backend)
            {
                _latestBackendServiceStatus = status;
                _hasLatestBackendServiceStatus = true;
            }

            ClientServiceStatusChanged?.Invoke(new ClientServiceStatusNotice(
                status.Service,
                status.available,
                status.message));
        }

        private void ClearPendingPlayerServiceStatuses()
        {
            while (_pendingPlayerServiceStatuses.TryDequeue(out _)) { }
        }

        private void ResetClientServiceStatus()
        {
            _hasLatestBackendServiceStatus = false;
            _latestBackendServiceStatus = default;
        }
    }
}
