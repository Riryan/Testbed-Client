using System;
using Cysharp.Threading.Tasks;
using Game.Shared.Chat;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<FriendsStateMessage> FriendsStateReceived;
        public event Action<TradeStateMessage> TradeStateReceived;
        public event Action<StorageStateMessage> StorageStateReceived;
        public event Action<PartyStateMessage> PartyStateReceived;

        private bool _hasFriendsCache;
        private bool _friendSnapshotRequestInFlight;
        private bool _tradeSnapshotRequestInFlight;
        private bool _storageSnapshotRequestInFlight;
        private bool _socialEconomyMutationRequestInFlight;
        private bool _ownerLiveInterestRequestInFlight;
        private OwnerLiveInterestKind _ownerLiveInterests;
        private FriendsStateMessage _latestFriends;
        private TradeStateMessage _latestTrade;
        private StorageStateMessage _latestStorage;
        private PartyStateMessage _latestParty;

        public bool HasFriendsCache => _hasFriendsCache;
        public FriendsStateMessage LatestFriends => _latestFriends;
        public TradeStateMessage LatestTrade => _latestTrade;

        // Storage server slots stay private packing metadata. Consumers receive
        // the owner-local visual ordering.
        public StorageStateMessage LatestStorage =>
            BuildPresentedStorageState(_latestStorage);

        public PartyStateMessage LatestParty => _latestParty;
        public bool HasParty => _latestParty.partyId != 0;
        public bool HasActiveTrade => _latestTrade.sessionId != 0;
        public bool HasStorageCache => _latestStorage.capacity > 0;
        public OwnerLiveInterestKind OwnerLiveInterests => _ownerLiveInterests;

        private void RegisterSocialEconomyMessages()
        {
            Client.RegisterResponseHandler<EmptySocialRequestMessage, FriendsStateMessage>(
                FriendRequestTypes.Snapshot);
            Client.RegisterResponseHandler<FriendActionRequestMessage, SocialEconomyMutationResponseMessage>(
                FriendRequestTypes.Action);
            Client.RegisterResponseHandler<OwnerLiveInterestRequestMessage, SocialEconomyMutationResponseMessage>(
                FriendRequestTypes.OwnerLiveInterest);
            Client.RegisterResponseHandler<TradeActionRequestMessage, SocialEconomyMutationResponseMessage>(
                EconomyRequestTypes.TradeAction);
            Client.RegisterResponseHandler<EmptySocialRequestMessage, TradeStateMessage>(
                EconomyRequestTypes.TradeSnapshot);
            Client.RegisterResponseHandler<EmptySocialRequestMessage, StorageStateMessage>(
                EconomyRequestTypes.StorageSnapshot);
            Client.RegisterResponseHandler<StorageTransferRequestMessage, SocialEconomyMutationResponseMessage>(
                EconomyRequestTypes.StorageTransfer);

            RegisterClientMessage(
                SocialEconomyMessageTypes.FriendsState,
                HandleFriendsState);
            RegisterClientMessage(
                SocialEconomyMessageTypes.TradeState,
                HandleTradeState);
            RegisterClientMessage(
                SocialEconomyMessageTypes.StorageState,
                HandleStorageState);
            RegisterClientMessage(
                PartyMessageTypes.State,
                HandlePartyState);
        }

        public UniTask<SocialEconomyMutationResponseMessage> SetOwnerLiveInterestAsync(
            OwnerLiveInterestKind interest,
            bool enabled,
            int millisecondsTimeout = 10000)
        {
            OwnerLiveInterestKind desired = enabled
                ? _ownerLiveInterests | interest
                : _ownerLiveInterests & ~interest;
            return SetOwnerLiveInterestsAsync(
                desired,
                millisecondsTimeout);
        }

        public async UniTask<SocialEconomyMutationResponseMessage> SetOwnerLiveInterestsAsync(
            OwnerLiveInterestKind interests,
            int millisecondsTimeout = 10000)
        {
            OwnerLiveInterestKind supported =
                OwnerLiveInterestKind.FriendsPresence |
                OwnerLiveInterestKind.GuildPresence;
            interests &= supported;

            if (interests == _ownerLiveInterests)
                return SocialEconomyMutationResponseMessage.Ok();

            if (!IsClientConnected)
                return SocialEconomyMutationResponseMessage.Failed(
                    1,
                    "client is not connected");

            if (_ownerLiveInterestRequestInFlight)
                return SocialEconomyMutationResponseMessage.Failed(
                    2,
                    "live-interest update is already pending locally");

            _ownerLiveInterestRequestInFlight = true;
            try
            {
                AsyncResponseData<SocialEconomyMutationResponseMessage> response =
                    await ClientSendRequestAsync<
                        OwnerLiveInterestRequestMessage,
                        SocialEconomyMutationResponseMessage>(
                        FriendRequestTypes.OwnerLiveInterest,
                        new OwnerLiveInterestRequestMessage
                        {
                            interests = (uint)interests,
                        },
                        millisecondsTimeout);

                if (!response.IsSuccess)
                    return SocialEconomyMutationResponseMessage.Failed(
                        2,
                        $"request failed: {response.ResponseCode}");

                if (response.Response.success)
                    _ownerLiveInterests = interests;

                return response.Response;
            }
            finally
            {
                _ownerLiveInterestRequestInFlight = false;
            }
        }

        public async UniTask<FriendsStateMessage> RequestFriendsAsync(
            bool forceRefresh = false,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return _latestFriends;
            if (!forceRefresh && _hasFriendsCache)
                return _latestFriends;
            if (_friendSnapshotRequestInFlight)
                return _latestFriends;

            _friendSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<FriendsStateMessage> response =
                    await ClientSendRequestAsync<
                        EmptySocialRequestMessage,
                        FriendsStateMessage>(
                        FriendRequestTypes.Snapshot,
                        new EmptySocialRequestMessage(),
                        millisecondsTimeout);

                if (response.IsSuccess)
                    ApplyFriendsState(response.Response);

                return response.IsSuccess
                    ? response.Response
                    : _latestFriends;
            }
            finally
            {
                _friendSnapshotRequestInFlight = false;
            }
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestAcceptFriendAsync(
            long inviterCharacterId = 0,
            int millisecondsTimeout = 10000)
        {
            long target = inviterCharacterId > 0
                ? inviterCharacterId
                : _latestFriends.pendingInviterCharacterId;

            if (_hasFriendsCache &&
                (_latestFriends.pendingInviterCharacterId <= 0 ||
                 (target > 0 &&
                  _latestFriends.pendingInviterCharacterId != target)))
                return LocalSocialFailure(
                    "friend invite is not present in the local authoritative cache");

            return SendFriendActionAsync(
                FriendActionKind.Accept,
                target,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestDeclineFriendAsync(
            long inviterCharacterId = 0,
            int millisecondsTimeout = 10000)
        {
            long target = inviterCharacterId > 0
                ? inviterCharacterId
                : _latestFriends.pendingInviterCharacterId;

            if (_hasFriendsCache &&
                (_latestFriends.pendingInviterCharacterId <= 0 ||
                 (target > 0 &&
                  _latestFriends.pendingInviterCharacterId != target)))
                return LocalSocialFailure(
                    "friend invite is not present in the local authoritative cache");

            return SendFriendActionAsync(
                FriendActionKind.Decline,
                target,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestRemoveFriendAsync(
            long characterId,
            int millisecondsTimeout = 10000)
        {
            if (characterId <= 0)
                return LocalSocialFailure(
                    "friend target is invalid");

            if (_hasFriendsCache &&
                !ContainsFriend(characterId))
                return LocalSocialFailure(
                    "player is not in the local friend cache");

            return SendFriendActionAsync(
                FriendActionKind.Remove,
                characterId,
                millisecondsTimeout);
        }

        public async UniTask<TradeStateMessage> RequestTradeStateAsync(
            bool forceRefresh = false,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return _latestTrade;
            if (!forceRefresh &&
                _latestTrade.sessionId != 0)
                return _latestTrade;
            if (_tradeSnapshotRequestInFlight)
                return _latestTrade;

            _tradeSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<TradeStateMessage> response =
                    await ClientSendRequestAsync<
                        EmptySocialRequestMessage,
                        TradeStateMessage>(
                        EconomyRequestTypes.TradeSnapshot,
                        new EmptySocialRequestMessage(),
                        millisecondsTimeout);

                if (response.IsSuccess)
                    ApplyTradeState(response.Response);

                return response.IsSuccess
                    ? response.Response
                    : _latestTrade;
            }
            finally
            {
                _tradeSnapshotRequestInFlight = false;
            }
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestAcceptTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 1)
                return LocalSocialFailure(
                    "there is no pending trade invitation");

            return SendTradeActionAsync(
                TradeActionKind.Accept,
                -1,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestDeclineTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 1)
                return LocalSocialFailure(
                    "there is no pending trade invitation");

            return SendTradeActionAsync(
                TradeActionKind.Decline,
                -1,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestOfferTradeItemAsync(
            int inventorySlot,
            int quantity,
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 2)
                return LocalSocialFailure(
                    "trade is not active");

            if (_latestTrade.ownLocked)
                return LocalSocialFailure(
                    "unlock the trade before changing the offer");

            if (quantity <= 0)
                return LocalSocialFailure(
                    "trade quantity must be positive");

            int authoritativePackingSlot = inventorySlot;
            if (HasPlayerItemsCache)
            {
                if (!TryGetCachedInventoryItem(
                        inventorySlot,
                        out PlayerItemWire item))
                    return LocalSocialFailure(
                        "inventory visual slot is empty locally");

                if (quantity > item.quantity)
                    return LocalSocialFailure(
                        "trade quantity exceeds the locally known stack");

                // The trade wire keeps its existing shape, but the number sent is the
                // authoritative cached packing locator, not client presentation order.
                authoritativePackingSlot = item.inventorySlot;
            }

            return SendTradeActionAsync(
                TradeActionKind.Offer,
                authoritativePackingSlot,
                quantity,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestRemoveTradeOfferAsync(
            int inventorySlot,
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 2)
                return LocalSocialFailure(
                    "trade is not active");

            if (_latestTrade.ownLocked)
                return LocalSocialFailure(
                    "unlock the trade before changing the offer");

            if (!ContainsOwnTradeOffer(inventorySlot))
                return LocalSocialFailure(
                    "that inventory slot is not in the local trade offer");

            return SendTradeActionAsync(
                TradeActionKind.RemoveOffer,
                inventorySlot,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestLockTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 2)
                return LocalSocialFailure(
                    "trade is not active");

            if (_latestTrade.ownLocked)
                return LocalSocialFailure(
                    "trade offer is already locked locally");

            return SendTradeActionAsync(
                TradeActionKind.Lock,
                -1,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestUnlockTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 2)
                return LocalSocialFailure(
                    "trade is not active");

            if (!_latestTrade.ownLocked)
                return LocalSocialFailure(
                    "trade offer is already unlocked locally");

            return SendTradeActionAsync(
                TradeActionKind.Unlock,
                -1,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestConfirmTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0 ||
                _latestTrade.phase != 2)
                return LocalSocialFailure(
                    "trade is not active");

            if (!_latestTrade.ownLocked ||
                !_latestTrade.partnerLocked)
                return LocalSocialFailure(
                    "both trade offers must be locked before confirming");

            if (_latestTrade.ownConfirmed)
                return LocalSocialFailure(
                    "trade is already confirmed locally");

            return SendTradeActionAsync(
                TradeActionKind.Confirm,
                -1,
                0,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestCancelTradeAsync(
            int millisecondsTimeout = 10000)
        {
            if (_latestTrade.sessionId == 0)
                return LocalSocialFailure(
                    "there is no active trade");

            if (_latestTrade.phase == 3)
                return LocalSocialFailure(
                    "trade is already committing");

            return SendTradeActionAsync(
                TradeActionKind.Cancel,
                -1,
                0,
                millisecondsTimeout);
        }

        public async UniTask<StorageStateMessage> RequestStorageAsync(
            bool forceRefresh = false,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return LatestStorage;

            // OpenStorage pushes the authoritative state. Normal calls are local cache reads.
            if (!forceRefresh)
                return LatestStorage;

            if (_storageSnapshotRequestInFlight)
                return LatestStorage;

            _storageSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<StorageStateMessage> response =
                    await ClientSendRequestAsync<
                        EmptySocialRequestMessage,
                        StorageStateMessage>(
                        EconomyRequestTypes.StorageSnapshot,
                        new EmptySocialRequestMessage(),
                        millisecondsTimeout);

                if (response.IsSuccess)
                    ApplyStorageState(response.Response);

                return response.IsSuccess
                    ? LatestStorage
                    : LatestStorage;
            }
            finally
            {
                _storageSnapshotRequestInFlight = false;
            }
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestDepositToStorageAsync(
            int inventorySlot,
            int quantity,
            int millisecondsTimeout = 10000)
        {
            if (!HasStorageCache)
                return LocalSocialFailure(
                    "storage is not open locally");

            if (!HasPlayerItemsCache)
                return LocalSocialFailure(
                    "inventory state has not hydrated locally");

            if (quantity <= 0)
                return LocalSocialFailure(
                    "storage quantity must be positive");

            // inventorySlot is a CLIENT VISUAL slot here. Resolve the selected item,
            // then send only the authoritative cached packing locator + stable identity.
            if (!TryGetCachedInventoryItem(
                    inventorySlot,
                    out PlayerItemWire item))
                return LocalSocialFailure(
                    "inventory visual slot is empty locally");

            if (quantity > item.quantity)
                return LocalSocialFailure(
                    "storage quantity exceeds the locally known stack");

            return SendStorageTransferAsync(
                StorageTransferKind.Deposit,
                item.inventorySlot,
                quantity,
                item.itemInstanceId,
                _latestStorage.revision,
                millisecondsTimeout);
        }

        public UniTask<SocialEconomyMutationResponseMessage> RequestWithdrawFromStorageAsync(
            int storageSlot,
            int quantity,
            int millisecondsTimeout = 10000)
        {
            if (!HasStorageCache)
                return LocalSocialFailure(
                    "storage is not open locally");

            if (!HasPlayerItemsCache)
                return LocalSocialFailure(
                    "inventory state has not hydrated locally");

            if (quantity <= 0)
                return LocalSocialFailure(
                    "storage quantity must be positive");

            // storageSlot is presentation-only. Resolve back to the raw authoritative
            // storage item before reusing the existing transfer request.
            if (!TryGetCachedStorageItem(
                    storageSlot,
                    out PlayerItemWire item))
                return LocalSocialFailure(
                    "storage visual slot is empty locally");

            if (quantity > item.quantity)
                return LocalSocialFailure(
                    "withdraw quantity exceeds the locally known stack");

            return SendStorageTransferAsync(
                StorageTransferKind.Withdraw,
                item.inventorySlot,
                quantity,
                item.itemInstanceId,
                _latestStorage.revision,
                millisecondsTimeout);
        }

        private async UniTask<SocialEconomyMutationResponseMessage> SendFriendActionAsync(
            FriendActionKind action,
            long targetCharacterId,
            int millisecondsTimeout)
        {
            return await SendSocialMutationAsync(
                FriendRequestTypes.Action,
                new FriendActionRequestMessage
                {
                    action = (byte)action,
                    targetCharacterId = targetCharacterId,
                },
                millisecondsTimeout);
        }

        private async UniTask<SocialEconomyMutationResponseMessage> SendTradeActionAsync(
            TradeActionKind action,
            int inventorySlot,
            int quantity,
            int millisecondsTimeout)
        {
            return await SendSocialMutationAsync(
                EconomyRequestTypes.TradeAction,
                new TradeActionRequestMessage
                {
                    action = (byte)action,
                    partnerCharacterId =
                        _latestTrade.partnerCharacterId,
                    inventorySlot = inventorySlot,
                    quantity = quantity,
                },
                millisecondsTimeout);
        }

        private async UniTask<SocialEconomyMutationResponseMessage> SendStorageTransferAsync(
            StorageTransferKind action,
            int sourceSlot,
            int quantity,
            long expectedItemInstanceId,
            long knownStorageRevision,
            int millisecondsTimeout)
        {
            return await SendSocialMutationAsync(
                EconomyRequestTypes.StorageTransfer,
                new StorageTransferRequestMessage
                {
                    action = (byte)action,
                    sourceSlot = sourceSlot,
                    quantity = quantity,
                    expectedItemInstanceId =
                        expectedItemInstanceId,
                    knownStorageRevision =
                        knownStorageRevision,
                },
                millisecondsTimeout);
        }

        private async UniTask<SocialEconomyMutationResponseMessage> SendSocialMutationAsync<TRequest>(
            ushort requestType,
            TRequest request,
            int millisecondsTimeout)
            where TRequest : struct, LiteNetLib.Utils.INetSerializable
        {
            if (!IsClientConnected)
                return SocialEconomyMutationResponseMessage.Failed(
                    1,
                    "client is not connected");

            if (_socialEconomyMutationRequestInFlight)
                return SocialEconomyMutationResponseMessage.Failed(
                    2,
                    "another social/economy request is already pending locally");

            _socialEconomyMutationRequestInFlight = true;
            try
            {
                AsyncResponseData<SocialEconomyMutationResponseMessage> response =
                    await ClientSendRequestAsync<
                        TRequest,
                        SocialEconomyMutationResponseMessage>(
                        requestType,
                        request,
                        millisecondsTimeout);

                return response.IsSuccess
                    ? response.Response
                    : SocialEconomyMutationResponseMessage.Failed(
                        2,
                        $"request failed: {response.ResponseCode}");
            }
            finally
            {
                _socialEconomyMutationRequestInFlight = false;
            }
        }

        public bool TrySubmitPartyCommand(
            string arguments,
            out string error)
        {
            string prepared =
                (arguments ?? string.Empty).Trim();

            if (prepared.Length == 0)
            {
                error = "party command is empty";
                return false;
            }

            return TrySendChatMessage(
                ChatChannel.Local,
                string.Empty,
                $"/party {prepared}",
                out error);
        }

        private UniTask<SocialEconomyMutationResponseMessage> LocalSocialFailure(
            string error) =>
            UniTask.FromResult(
                SocialEconomyMutationResponseMessage.Failed(
                    3,
                    error));

        private void HandleFriendsState(
            MessageHandlerData handler) =>
            ApplyFriendsState(
                handler.ReadMessage<FriendsStateMessage>());

        private void HandleTradeState(
            MessageHandlerData handler) =>
            ApplyTradeState(
                handler.ReadMessage<TradeStateMessage>());

        private void HandleStorageState(
            MessageHandlerData handler) =>
            ApplyStorageState(
                handler.ReadMessage<StorageStateMessage>());

        private void HandlePartyState(
            MessageHandlerData handler) =>
            ApplyPartyState(
                handler.ReadMessage<PartyStateMessage>());

        private void ApplyFriendsState(
            FriendsStateMessage state)
        {
            if (state.updateKind == FriendsStateUpdateKind.PresenceDelta)
            {
                if (!_hasFriendsCache)
                {
                    RequestFriendsAsync(forceRefresh: true).Forget();
                    return;
                }

                FriendEntryWire[] current = _latestFriends.friends ?? Array.Empty<FriendEntryWire>();
                FriendPresenceWire[] presence = state.presence ?? Array.Empty<FriendPresenceWire>();
                var next = (FriendEntryWire[])current.Clone();
                for (int i = 0; i < presence.Length; ++i)
                {
                    int index = Array.FindIndex(next, x => x.characterId == presence[i].characterId);
                    if (index < 0)
                    {
                        RequestFriendsAsync(forceRefresh: true).Forget();
                        return;
                    }
                    next[index].online = presence[i].online;
                }

                _latestFriends.friends = next;
                // Presence is live state only. Never change the durable membership fingerprint.
                FriendsStateReceived?.Invoke(_latestFriends);
                return;
            }

            state.updateKind = FriendsStateUpdateKind.Full;
            _latestFriends = state;
            _hasFriendsCache = true;
            _friendsCacheRevision = FriendsStateRevision.Compute(state.friends);
            FriendsStateReceived?.Invoke(state);
        }

        private void ApplyTradeState(TradeStateMessage state)
        {
            if (state.IsFull)
            {
                _latestTrade = state;
                TradeStateReceived?.Invoke(state);
                return;
            }

            if (_latestTrade.sessionId == 0 || state.sessionId != _latestTrade.sessionId)
            {
                RequestTradeStateAsync(forceRefresh: true).Forget();
                return;
            }

            TradeStateChangeMask mask = state.changeMask;
            if ((mask & TradeStateChangeMask.Metadata) != 0)
            {
                _latestTrade.partnerCharacterId = state.partnerCharacterId;
                _latestTrade.partnerName = state.partnerName;
                _latestTrade.phase = state.phase;
                _latestTrade.ownLocked = state.ownLocked;
                _latestTrade.partnerLocked = state.partnerLocked;
                _latestTrade.ownConfirmed = state.ownConfirmed;
                _latestTrade.partnerConfirmed = state.partnerConfirmed;
            }
            if ((mask & TradeStateChangeMask.OwnOffers) != 0)
                _latestTrade.ownOffers = state.ownOffers ?? Array.Empty<TradeOfferWire>();
            if ((mask & TradeStateChangeMask.PartnerOffers) != 0)
                _latestTrade.partnerOffers = state.partnerOffers ?? Array.Empty<TradeOfferWire>();
            if ((mask & TradeStateChangeMask.Detail) != 0)
                _latestTrade.detail = state.detail ?? string.Empty;

            _latestTrade.changeMask = TradeStateChangeMask.Full;
            TradeStateReceived?.Invoke(_latestTrade);
        }

        private void ApplyPartyState(
            PartyStateMessage state)
        {
            _latestParty = state;
            PartyStateReceived?.Invoke(state);
        }

        private void ApplyStorageState(StorageStateMessage state)
        {
            if (state.updateKind == StorageStateUpdateKind.Delta)
            {
                if (!HasStorageCache ||
                    state.capacity != _latestStorage.capacity ||
                    state.baseRevision != _latestStorage.revision ||
                    state.revision != state.baseRevision + 1)
                {
                    RequestStorageAsync(forceRefresh: true).Forget();
                    return;
                }

                var byId = new System.Collections.Generic.Dictionary<long, PlayerItemWire>();
                PlayerItemWire[] existing = _latestStorage.items ?? Array.Empty<PlayerItemWire>();
                for (int i = 0; i < existing.Length; ++i)
                    if (existing[i].itemInstanceId > 0)
                        byId[existing[i].itemInstanceId] = existing[i];

                long[] removed = state.removedItemInstanceIds ?? Array.Empty<long>();
                for (int i = 0; i < removed.Length; ++i)
                    byId.Remove(removed[i]);

                PlayerItemWire[] changed = state.items ?? Array.Empty<PlayerItemWire>();
                for (int i = 0; i < changed.Length; ++i)
                {
                    if (changed[i].itemInstanceId <= 0)
                    {
                        RequestStorageAsync(forceRefresh: true).Forget();
                        return;
                    }
                    byId[changed[i].itemInstanceId] = changed[i];
                }

                var next = new PlayerItemWire[byId.Count];
                int n = 0;
                foreach (PlayerItemWire item in byId.Values) next[n++] = item;
                Array.Sort(next, (a, b) => a.inventorySlot.CompareTo(b.inventorySlot));

                _latestStorage = new StorageStateMessage
                {
                    updateKind = StorageStateUpdateKind.Full,
                    capacity = state.capacity,
                    revision = state.revision,
                    items = next,
                    removedItemInstanceIds = Array.Empty<long>(),
                    detail = state.detail ?? string.Empty,
                };
                StorageStateReceived?.Invoke(LatestStorage);
                return;
            }

            if (state.capacity <= 0)
            {
                _latestStorage = state;
                StorageStateReceived?.Invoke(state);
                return;
            }

            if (_latestStorage.capacity > 0 && state.revision < _latestStorage.revision)
                return;

            state.updateKind = StorageStateUpdateKind.Full;
            _latestStorage = state;
            StorageStateReceived?.Invoke(LatestStorage);
        }

        private bool ContainsFriend(
            long characterId)
        {
            FriendEntryWire[] friends =
                _latestFriends.friends ??
                Array.Empty<FriendEntryWire>();

            for (int i = 0; i < friends.Length; ++i)
                if (friends[i].characterId == characterId)
                    return true;

            return false;
        }

        private bool ContainsOwnTradeOffer(
            int inventorySlot)
        {
            TradeOfferWire[] offers =
                _latestTrade.ownOffers ??
                Array.Empty<TradeOfferWire>();

            for (int i = 0; i < offers.Length; ++i)
                if (offers[i].sourceSlot == inventorySlot)
                    return true;

            return false;
        }

        private bool TryGetCachedStorageItem(
            int storageSlot,
            out PlayerItemWire item) =>
            TryGetRawStorageItemByPresentationSlot(
                storageSlot,
                out item);

        private bool HasEmptyStorageSlot()
        {
            if (_latestStorage.capacity <= 0)
                return false;

            bool[] occupied =
                new bool[_latestStorage.capacity];

            PlayerItemWire[] values =
                _latestStorage.items ??
                Array.Empty<PlayerItemWire>();

            for (int i = 0; i < values.Length; ++i)
                if (values[i].inventorySlot >= 0 &&
                    values[i].inventorySlot <
                        occupied.Length)
                    occupied[values[i].inventorySlot] =
                        true;

            for (int i = 0; i < occupied.Length; ++i)
                if (!occupied[i])
                    return true;

            return false;
        }

        private bool HasEmptyInventorySlot()
        {
            if (!HasPlayerItemsCache ||
                _latestPlayerItems.inventoryCapacity <= 0)
                return true;

            bool[] occupied =
                new bool[
                    _latestPlayerItems.inventoryCapacity];

            PlayerItemWire[] values =
                _latestPlayerItems.inventory ??
                Array.Empty<PlayerItemWire>();

            for (int i = 0; i < values.Length; ++i)
                if (values[i].inventorySlot >= 0 &&
                    values[i].inventorySlot <
                        occupied.Length)
                    occupied[values[i].inventorySlot] =
                        true;

            for (int i = 0; i < occupied.Length; ++i)
                if (!occupied[i])
                    return true;

            return false;
        }

        private void ResetClientSocialEconomy()
        {
            _hasFriendsCache = false;
            _friendSnapshotRequestInFlight = false;
            _tradeSnapshotRequestInFlight = false;
            _storageSnapshotRequestInFlight = false;
            _socialEconomyMutationRequestInFlight = false;
            _ownerLiveInterestRequestInFlight = false;
            _ownerLiveInterests = OwnerLiveInterestKind.None;
            _friendsCacheRevision = 0;
            _latestFriends = default;
            _latestTrade = default;
            _latestStorage = default;
            _latestParty = default;
            _hasGuildCache = false;
            _guildSnapshotRequestInFlight = false;
            _guildMutationRequestInFlight = false;
            _latestGuild = default;
        }
    }
}
