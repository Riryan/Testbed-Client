using System;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<GuildStateMessage> GuildStateReceived;

        private GuildStateMessage _latestGuild;
        private bool _hasGuildCache;
        private bool _guildSnapshotRequestInFlight;
        private bool _guildMutationRequestInFlight;

        public GuildStateMessage LatestGuild => _latestGuild;
        public bool HasGuildCache => _hasGuildCache;
        public bool HasGuild => _latestGuild.guildId > 0;

        private void RegisterGuildMessages()
        {
            Client.RegisterResponseHandler<EmptySocialRequestMessage, GuildStateMessage>(GuildRequestTypes.Snapshot);
            Client.RegisterResponseHandler<GuildActionRequestMessage, GuildMutationResponseMessage>(GuildRequestTypes.Action);
            RegisterClientMessage(GuildMessageTypes.State, HandleGuildState);
        }

        public async UniTask<GuildStateMessage> RequestGuildStateAsync(bool forceRefresh = false, int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected || (!forceRefresh && _hasGuildCache) || _guildSnapshotRequestInFlight)
                return _latestGuild;

            _guildSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<GuildStateMessage> response =
                    await ClientSendRequestAsync<EmptySocialRequestMessage, GuildStateMessage>(
                        GuildRequestTypes.Snapshot, new EmptySocialRequestMessage(), millisecondsTimeout);
                if (response.IsSuccess)
                    ApplyGuildState(response.Response);
                return response.IsSuccess ? response.Response : _latestGuild;
            }
            finally { _guildSnapshotRequestInFlight = false; }
        }

        public UniTask<GuildMutationResponseMessage> RequestCreateGuildAsync(string name, int millisecondsTimeout = 10000) =>
            SendGuildActionAsync(GuildActionKind.Create, 0, name, millisecondsTimeout);

        public UniTask<GuildMutationResponseMessage> RequestAcceptGuildInviteAsync(int millisecondsTimeout = 10000)
        {
            if (_hasGuildCache && _latestGuild.pendingInviterCharacterId <= 0)
                return LocalGuildFailure("guild invite is not present in the local authoritative cache");
            return SendGuildActionAsync(GuildActionKind.Accept, 0, string.Empty, millisecondsTimeout);
        }

        public UniTask<GuildMutationResponseMessage> RequestDeclineGuildInviteAsync(int millisecondsTimeout = 10000)
        {
            if (_hasGuildCache && _latestGuild.pendingInviterCharacterId <= 0)
                return LocalGuildFailure("guild invite is not present in the local authoritative cache");
            return SendGuildActionAsync(GuildActionKind.Decline, 0, string.Empty, millisecondsTimeout);
        }

        public UniTask<GuildMutationResponseMessage> RequestLeaveGuildAsync(int millisecondsTimeout = 10000)
        {
            if (_hasGuildCache && !HasGuild) return LocalGuildFailure("you are not in a guild locally");
            return SendGuildActionAsync(GuildActionKind.Leave, 0, string.Empty, millisecondsTimeout);
        }

        public UniTask<GuildMutationResponseMessage> RequestKickGuildMemberAsync(long characterId, int millisecondsTimeout = 10000)
        {
            if (characterId <= 0 || characterId == _latestGuild.ownerCharacterId)
                return LocalGuildFailure("guild kick target is invalid");
            return SendGuildActionAsync(GuildActionKind.Kick, characterId, string.Empty, millisecondsTimeout);
        }

        public UniTask<GuildMutationResponseMessage> RequestDisbandGuildAsync(int millisecondsTimeout = 10000) =>
            SendGuildActionAsync(GuildActionKind.Disband, 0, string.Empty, millisecondsTimeout);

        private async UniTask<GuildMutationResponseMessage> SendGuildActionAsync(GuildActionKind action, long targetCharacterId, string name, int millisecondsTimeout)
        {
            if (!IsClientConnected)
                return new GuildMutationResponseMessage { success = false, status = 1, detail = "client is not connected" };
            if (_guildMutationRequestInFlight)
                return new GuildMutationResponseMessage { success = false, status = 2, detail = "guild action is already pending locally" };

            _guildMutationRequestInFlight = true;
            try
            {
                AsyncResponseData<GuildMutationResponseMessage> response =
                    await ClientSendRequestAsync<GuildActionRequestMessage, GuildMutationResponseMessage>(
                        GuildRequestTypes.Action,
                        new GuildActionRequestMessage { action = (byte)action, targetCharacterId = targetCharacterId, name = name ?? string.Empty },
                        millisecondsTimeout);
                return response.IsSuccess
                    ? response.Response
                    : new GuildMutationResponseMessage { success = false, status = 2, detail = $"request failed: {response.ResponseCode}" };
            }
            finally { _guildMutationRequestInFlight = false; }
        }

        private UniTask<GuildMutationResponseMessage> LocalGuildFailure(string detail) =>
            UniTask.FromResult(new GuildMutationResponseMessage { success = false, status = 3, detail = detail ?? string.Empty });

        private void HandleGuildState(MessageHandlerData handler) => ApplyGuildState(handler.ReadMessage<GuildStateMessage>());

        private void ApplyGuildState(GuildStateMessage state)
        {
            if (_hasGuildCache && state.guildId != 0 && _latestGuild.guildId == state.guildId && state.revision < _latestGuild.revision)
                return;
            _latestGuild = state;
            _hasGuildCache = true;
            GuildStateReceived?.Invoke(state);
        }
    }
}
