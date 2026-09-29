using System;
using System.Collections.Generic;
using System.Net.Sockets;
using Game.Server.Application.Sessions;
using Game.Shared.Chat;
using Game.Shared.Sessions;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        private sealed class ChatRateState
        {
            public double Tokens = ChatBurst;
            public double LastRefillTime;
            public double NextAllowedTime;
        }

        private const int ChatMaxCharacters = 200;
        private const double ChatMinimumIntervalSeconds = 0.35d;
        private const double ChatRefillPerSecond = 2d;
        private const double ChatBurst = 5d;

        private readonly Dictionary<long, ChatRateState> _chatRateStates =
            new Dictionary<long, ChatRateState>();

        private double _clientChatTokens = ChatBurst;
        private double _clientChatLastRefillTime;
        private double _clientChatNextAllowedTime;

        public event Action<PlayerChatDeliveryMessage> ClientChatMessageReceived;
        public event Action ClientConnectionEstablished;
        public event Action<ClientDisconnectNotice> ClientConnectionClosed;
        public event Action ClientTransportStopped;
        public event Action<long> ClientWorldEntered;

        private void RegisterPlayerChatMessages()
        {
            RegisterServerMessage(PlayerChatMessageTypes.Submit, HandlePlayerChatSubmit);
            RegisterClientMessage(PlayerChatMessageTypes.Deliver, HandlePlayerChatDelivery);
        }

        public bool TrySendChatMessage(ChatChannel channel, string target, string message, out string error)
        {
            error = string.Empty;
            if (!IsClientConnected)
            {
                error = "Game server connection is unavailable.";
                return false;
            }

            string prepared = (message ?? string.Empty).Trim();
            if (prepared.Length == 0)
            {
                error = "Message is empty.";
                return false;
            }
            if (prepared.Length > ChatMaxCharacters)
            {
                error = $"Message is too long ({ChatMaxCharacters} character maximum).";
                return false;
            }
            if (channel == ChatChannel.System)
            {
                error = "System messages cannot be submitted by clients.";
                return false;
            }
            if (channel == ChatChannel.Whisper && string.IsNullOrWhiteSpace(target))
            {
                error = "Choose a whisper target.";
                return false;
            }

            if (!TryConsumeClientChatRate(Time.realtimeSinceStartupAsDouble))
            {
                error = "You are sending messages too quickly.";
                return false;
            }

            ClientSendPacket(
                0,
                DeliveryMethod.ReliableOrdered,
                PlayerChatMessageTypes.Submit,
                new PlayerChatSubmitMessage
                {
                    channel = (byte)channel,
                    target = (target ?? string.Empty).Trim(),
                    message = prepared,
                });
            return true;
        }


        private bool TryConsumeClientChatRate(double now)
        {
            if (_clientChatLastRefillTime <= 0d)
                _clientChatLastRefillTime = now;

            double elapsed = Math.Max(0d, now - _clientChatLastRefillTime);
            _clientChatLastRefillTime = now;
            _clientChatTokens = Math.Min(ChatBurst, _clientChatTokens + elapsed * ChatRefillPerSecond);

            if (now < _clientChatNextAllowedTime || _clientChatTokens < 1d)
                return false;

            _clientChatTokens -= 1d;
            _clientChatNextAllowedTime = now + ChatMinimumIntervalSeconds;
            return true;
        }

        private void ResetClientChatRate()
        {
            _clientChatTokens = ChatBurst;
            _clientChatLastRefillTime = 0d;
            _clientChatNextAllowedTime = 0d;
        }

        private void HandlePlayerChatSubmit(MessageHandlerData handler)
        {
            PlayerChatSubmitMessage submit = handler.ReadMessage<PlayerChatSubmitMessage>();
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle senderHandle))
                return;

            string senderName = _characterSessionRuntimeHost != null
                ? _characterSessionRuntimeHost.GetPlayerCharacterName(senderHandle)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(senderName))
                return;

            string text = (submit.message ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > ChatMaxCharacters)
            {
                SendSystemChat(handler.ConnectionId, $"Chat messages must be 1-{ChatMaxCharacters} characters.");
                return;
            }

            if (!TryConsumeChatRate(handler.ConnectionId, Time.realtimeSinceStartupAsDouble))
            {
                SendSystemChat(handler.ConnectionId, "You are sending messages too quickly.");
                return;
            }

            switch (submit.Channel)
            {
                case ChatChannel.Local:
                    BroadcastLocalChat(senderName, text);
                    break;
                case ChatChannel.Whisper:
                    SendWhisperChat(handler.ConnectionId, senderName, submit.target, text);
                    break;
                case ChatChannel.Party:
                    SendSystemChat(handler.ConnectionId, "Party chat is ready in the UI but party routing has not been migrated yet.");
                    break;
                case ChatChannel.Guild:
                    SendSystemChat(handler.ConnectionId, "Guild chat is ready in the UI but guild routing has not been migrated yet.");
                    break;
                default:
                    SendSystemChat(handler.ConnectionId, "Unsupported chat channel.");
                    break;
            }
        }

        private void HandlePlayerChatDelivery(MessageHandlerData handler)
        {
            PlayerChatDeliveryMessage message = handler.ReadMessage<PlayerChatDeliveryMessage>();
            ClientChatMessageReceived?.Invoke(message);
        }

        private void BroadcastLocalChat(string senderName, string text)
        {
            var delivery = new PlayerChatDeliveryMessage
            {
                channel = (byte)ChatChannel.Local,
                sender = senderName,
                target = string.Empty,
                message = text,
                serverUtcTicks = DateTime.UtcNow.Ticks,
            };

            // Current migration-stage Local chat is world-wide on this GameServer.
            // CommunicationsServer will later replace this transport and can use the
            // authoritative GameServer AOI/membership feed for true proximity routing.
            foreach (KeyValuePair<long, PlayerSessionHandle> pair in _playerSessionHandles)
            {
                if (!TryGetInWorldPlayerSession(pair.Key, out _))
                    continue;
                ServerSendPacket(
                    pair.Key,
                    0,
                    DeliveryMethod.ReliableOrdered,
                    PlayerChatMessageTypes.Deliver,
                    delivery);
            }
        }

        private void SendWhisperChat(long senderConnectionId, string senderName, string targetName, string text)
        {
            string normalizedTarget = (targetName ?? string.Empty).Trim();
            if (normalizedTarget.Length == 0)
            {
                SendSystemChat(senderConnectionId, "Whisper target is missing.");
                return;
            }

            long targetConnectionId = 0;
            string canonicalTargetName = string.Empty;
            foreach (KeyValuePair<long, PlayerSessionHandle> pair in _playerSessionHandles)
            {
                if (!TryGetInWorldPlayerSession(pair.Key, out PlayerSessionHandle handle))
                    continue;
                string candidate = _characterSessionRuntimeHost != null
                    ? _characterSessionRuntimeHost.GetPlayerCharacterName(handle)
                    : string.Empty;
                if (!string.Equals(candidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    continue;

                targetConnectionId = pair.Key;
                canonicalTargetName = candidate;
                break;
            }

            if (targetConnectionId == 0)
            {
                SendSystemChat(senderConnectionId, $"Player '{normalizedTarget}' is not online on this GameServer.");
                return;
            }

            var delivery = new PlayerChatDeliveryMessage
            {
                channel = (byte)ChatChannel.Whisper,
                sender = senderName,
                target = canonicalTargetName,
                message = text,
                serverUtcTicks = DateTime.UtcNow.Ticks,
            };

            ServerSendPacket(
                targetConnectionId,
                0,
                DeliveryMethod.ReliableOrdered,
                PlayerChatMessageTypes.Deliver,
                delivery);

            if (targetConnectionId != senderConnectionId)
            {
                ServerSendPacket(
                    senderConnectionId,
                    0,
                    DeliveryMethod.ReliableOrdered,
                    PlayerChatMessageTypes.Deliver,
                    delivery);
            }
        }

        private void SendSystemChat(long connectionId, string text)
        {
            if (connectionId == 0)
                return;

            ServerSendPacket(
                connectionId,
                0,
                DeliveryMethod.ReliableOrdered,
                PlayerChatMessageTypes.Deliver,
                new PlayerChatDeliveryMessage
                {
                    channel = (byte)ChatChannel.System,
                    sender = string.Empty,
                    target = string.Empty,
                    message = text ?? string.Empty,
                    serverUtcTicks = DateTime.UtcNow.Ticks,
                });
        }

        private bool TryConsumeChatRate(long connectionId, double now)
        {
            if (!_chatRateStates.TryGetValue(connectionId, out ChatRateState state))
            {
                state = new ChatRateState
                {
                    Tokens = ChatBurst,
                    LastRefillTime = now,
                    NextAllowedTime = 0d,
                };
                _chatRateStates.Add(connectionId, state);
            }

            double elapsed = Math.Max(0d, now - state.LastRefillTime);
            state.LastRefillTime = now;
            state.Tokens = Math.Min(ChatBurst, state.Tokens + elapsed * ChatRefillPerSecond);

            if (now < state.NextAllowedTime || state.Tokens < 1d)
                return false;

            state.Tokens -= 1d;
            state.NextAllowedTime = now + ChatMinimumIntervalSeconds;
            return true;
        }

        private void ClearPlayerChatState() => _chatRateStates.Clear();

        private void RemovePlayerChatState(long connectionId) => _chatRateStates.Remove(connectionId);

        private void RaiseClientConnectionEstablished() => ClientConnectionEstablished?.Invoke();

        private void RaiseClientWorldEntered(long characterId) => ClientWorldEntered?.Invoke(characterId);

        public override void OnClientDisconnected(
            DisconnectReason reason,
            SocketError socketError,
            byte[] data)
        {
            bool unexpected = reason != DisconnectReason.DisconnectPeerCalled;
            string message = BuildDisconnectMessage(reason, socketError);
            var notice = new ClientDisconnectNotice(
                (byte)reason,
                (int)socketError,
                message,
                unexpected);

            // Let LiteNetLib transition its client flags first. UI/frontend listeners
            // can then react to one authoritative lifecycle event without polling
            // IsClientConnected waiting for the base callback to finish.
            base.OnClientDisconnected(reason, socketError, data);
            PlayerGameplaySettingsRuntime.Reset();
            ResetClientGameplayActions();
            ResetClientChatRate();
            ResetClientGameplaySettingsRequestAdmission();
            ResetClientStaffRequestAdmission();
            ClientConnectionClosed?.Invoke(notice);
        }

        private static string BuildDisconnectMessage(DisconnectReason reason, SocketError socketError)
        {
            switch (reason)
            {
                case DisconnectReason.Timeout:
                    return "Connection to the game server timed out.";
                case DisconnectReason.RemoteConnectionClose:
                    return "The game server closed the connection.";
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    return "The game server is unreachable.";
                case DisconnectReason.ConnectionFailed:
                    return "Connection to the game server failed.";
                case DisconnectReason.ConnectionRejected:
                    return "The game server rejected the connection.";
                case DisconnectReason.InvalidProtocol:
                    return "Client/server network protocol mismatch.";
                case DisconnectReason.DisconnectPeerCalled:
                    return "Disconnected.";
                default:
                    return socketError == SocketError.Success
                        ? $"Disconnected from the game server ({reason})."
                        : $"Disconnected from the game server ({reason}, {socketError}).";
            }
        }
    }
}
