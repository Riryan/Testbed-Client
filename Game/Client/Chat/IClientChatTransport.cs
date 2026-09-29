using System;
using Game.Shared.Chat;
using Player.Networking;

namespace Game.Client.Chat
{
    public readonly struct ClientChatMessage
    {
        public readonly ChatChannel Channel;
        public readonly string Sender;
        public readonly string Target;
        public readonly string Message;
        public readonly long ServerUtcTicks;

        public ClientChatMessage(
            ChatChannel channel,
            string sender,
            string target,
            string message,
            long serverUtcTicks)
        {
            Channel = channel;
            Sender = sender ?? string.Empty;
            Target = target ?? string.Empty;
            Message = message ?? string.Empty;
            ServerUtcTicks = serverUtcTicks;
        }
    }

    /// <summary>
    /// Client chat presentation boundary. The current adapter uses PlayerEntity/GameServer;
    /// CommunicationsServer can replace the adapter without changing the chat UI/state.
    /// </summary>
    public interface IClientChatTransport : IDisposable
    {
        event Action<ClientChatMessage> MessageReceived;
        bool TrySend(ChatChannel channel, string target, string message, out string error);
    }

    public sealed class PlayerEntityChatTransport : IClientChatTransport
    {
        private PlayerEntityGameManager _manager;

        public event Action<ClientChatMessage> MessageReceived;

        public PlayerEntityChatTransport(PlayerEntityGameManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _manager.ClientChatMessageReceived += OnMessage;
        }

        public bool TrySend(ChatChannel channel, string target, string message, out string error)
        {
            if (_manager == null)
            {
                error = "Chat transport is unavailable.";
                return false;
            }
            return _manager.TrySendChatMessage(channel, target, message, out error);
        }

        public void Dispose()
        {
            if (_manager != null)
                _manager.ClientChatMessageReceived -= OnMessage;
            _manager = null;
            MessageReceived = null;
        }

        private void OnMessage(PlayerChatDeliveryMessage message)
        {
            MessageReceived?.Invoke(new ClientChatMessage(
                message.Channel,
                message.sender,
                message.target,
                message.message,
                message.serverUtcTicks));
        }
    }
}
