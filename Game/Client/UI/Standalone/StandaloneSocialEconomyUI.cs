using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Gameplay;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.SocialEconomy;
using Game.Shared.Interactions;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Standalone-client presentation for the existing Party, Friends, and Storage state.
    /// Stable owner state is read from PlayerEntityGameManager caches and updated by the
    /// existing pushed-state events. Only Friends presence is interest-gated while its tab
    /// is visible. Storage is hydrated only after the authoritative OpenStorage interaction.
    /// No polling or parallel social/economy protocol is introduced here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneSocialEconomyUI : MonoBehaviour
    {
        [Header("Social window")]
        [SerializeField] private GameObject socialWindowRoot;
        [SerializeField] private Button friendsTabButton;
        [SerializeField] private Button partyTabButton;
        [SerializeField] private Button guildTabButton;
        [SerializeField] private GameObject friendsTabRoot;
        [SerializeField] private GameObject partyTabRoot;
        [SerializeField] private GameObject guildTabRoot;

        [Header("Friends")]
        [SerializeField] private Text friendsStatusText;
        [SerializeField] private Transform friendsContent;
        [SerializeField] private ClientSocialEconomyRowView friendsRowTemplate;

        [Header("Party")]
        [SerializeField] private Text partyStatusText;
        [SerializeField] private Transform partyContent;
        [SerializeField] private ClientSocialEconomyRowView partyRowTemplate;
        [SerializeField] private Button partyLeaveButton;
        [SerializeField] private Button partyDisbandButton;

        [Header("Guild")]
        [SerializeField] private Text guildStatusText;
        [SerializeField] private Transform guildContent;
        [SerializeField] private ClientSocialEconomyRowView guildRowTemplate;
        [SerializeField] private InputField guildNameInput;
        [SerializeField] private Button guildCreateButton;
        [SerializeField] private Button guildLeaveButton;
        [SerializeField] private Button guildDisbandButton;

        [Header("Guild request")]
        [SerializeField] private GameObject guildInvitePopupRoot;
        [SerializeField] private Text guildInviteText;
        [SerializeField] private Button guildInviteAcceptButton;
        [SerializeField] private Button guildInviteDeclineButton;

        [Header("Party HUD")]
        [SerializeField] private GameObject partyHudRoot;
        [SerializeField] private Text partyHudText;
        [SerializeField] private Button partyHudOpenButton;

        [Header("Friend request")]
        [SerializeField] private GameObject friendInvitePopupRoot;
        [SerializeField] private Text friendInviteText;
        [SerializeField] private Button friendInviteAcceptButton;
        [SerializeField] private Button friendInviteDeclineButton;

        [Header("Party request")]
        [SerializeField] private GameObject partyInvitePopupRoot;
        [SerializeField] private Text partyInviteText;
        [SerializeField] private Button partyInviteAcceptButton;
        [SerializeField] private Button partyInviteDeclineButton;

        [Header("Storage")]
        [SerializeField] private GameObject storageWindowRoot;
        [SerializeField] private Text storageStatusText;
        [SerializeField] private Text storageCapacityText;
        [SerializeField] private Button storageCloseButton;
        [SerializeField] private Transform storageInventoryContent;
        [SerializeField] private ClientSocialEconomyRowView storageInventoryRowTemplate;
        [SerializeField] private Transform storageContent;
        [SerializeField] private ClientSocialEconomyRowView storageRowTemplate;

        private readonly List<ClientSocialEconomyRowView> _friendRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _partyRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _guildRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _storageInventoryRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _storageRows = new List<ClientSocialEconomyRowView>();

        private PlayerEntityGameManager _manager;
        private int _socialTab; // 0 friends, 1 party, 2 guild
        private bool _socialVisible;
        private bool _friendsPresenceDesired;
        private bool _friendsPresenceUpdatePending;
        private bool _storageMutationBusy;
        private StandaloneDragGhost _dragGhost;

        public bool StorageOpen => storageWindowRoot != null && storageWindowRoot.activeSelf;
        public bool InvitePopupOpen =>
            (friendInvitePopupRoot != null && friendInvitePopupRoot.activeSelf) ||
            (partyInvitePopupRoot != null && partyInvitePopupRoot.activeSelf) ||
            (guildInvitePopupRoot != null && guildInvitePopupRoot.activeSelf);
        public GameObject StorageWindowRoot => storageWindowRoot;

        public bool HasAuthoredBindings =>
            socialWindowRoot != null && friendsTabButton != null && partyTabButton != null && guildTabButton != null &&
            friendsTabRoot != null && partyTabRoot != null && guildTabRoot != null &&
            friendsStatusText != null && friendsContent != null && friendsRowTemplate != null &&
            partyStatusText != null && partyContent != null && partyRowTemplate != null &&
            partyLeaveButton != null && partyDisbandButton != null &&
            guildStatusText != null && guildContent != null && guildRowTemplate != null && guildNameInput != null &&
            guildCreateButton != null && guildLeaveButton != null && guildDisbandButton != null &&
            guildInvitePopupRoot != null && guildInviteText != null && guildInviteAcceptButton != null && guildInviteDeclineButton != null &&
            partyHudRoot != null && partyHudText != null && partyHudOpenButton != null &&
            friendInvitePopupRoot != null && friendInviteText != null && friendInviteAcceptButton != null && friendInviteDeclineButton != null &&
            partyInvitePopupRoot != null && partyInviteText != null && partyInviteAcceptButton != null && partyInviteDeclineButton != null &&
            storageWindowRoot != null && storageStatusText != null && storageCapacityText != null && storageCloseButton != null &&
            storageInventoryContent != null && storageInventoryRowTemplate != null && storageContent != null && storageRowTemplate != null;

        private void Start()
        {
#if !UNITY_SERVER
            HideTemplates();
            WireButtons();
            BindManager();
            SelectFriendsTabInternal();
            RenderAll();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            CancelInvoke(nameof(CloseExpiredPartyInvite));
            UnbindManager();
#endif
        }

        private void WireButtons()
        {
            friendsTabButton?.onClick.RemoveAllListeners();
            friendsTabButton?.onClick.AddListener(SelectFriendsTab);
            partyTabButton?.onClick.RemoveAllListeners();
            partyTabButton?.onClick.AddListener(SelectPartyTab);
            guildTabButton?.onClick.RemoveAllListeners();
            guildTabButton?.onClick.AddListener(SelectGuildTab);

            partyLeaveButton?.onClick.RemoveAllListeners();
            partyLeaveButton?.onClick.AddListener(() => SubmitPartyCommand("leave"));
            partyDisbandButton?.onClick.RemoveAllListeners();
            partyDisbandButton?.onClick.AddListener(() => SubmitPartyCommand("disband"));
            partyHudOpenButton?.onClick.RemoveAllListeners();
            partyHudOpenButton?.onClick.AddListener(OpenPartyFromHud);

            friendInviteAcceptButton?.onClick.RemoveAllListeners();
            friendInviteAcceptButton?.onClick.AddListener(() => RespondToFriendInviteAsync(true).Forget());
            friendInviteDeclineButton?.onClick.RemoveAllListeners();
            friendInviteDeclineButton?.onClick.AddListener(() => RespondToFriendInviteAsync(false).Forget());

            partyInviteAcceptButton?.onClick.RemoveAllListeners();
            partyInviteAcceptButton?.onClick.AddListener(() => RespondToPartyInvite(true));
            partyInviteDeclineButton?.onClick.RemoveAllListeners();
            partyInviteDeclineButton?.onClick.AddListener(() => RespondToPartyInvite(false));

            guildCreateButton?.onClick.RemoveAllListeners();
            guildCreateButton?.onClick.AddListener(() => CreateGuildAsync().Forget());
            guildLeaveButton?.onClick.RemoveAllListeners();
            guildLeaveButton?.onClick.AddListener(() => LeaveGuildAsync().Forget());
            guildDisbandButton?.onClick.RemoveAllListeners();
            guildDisbandButton?.onClick.AddListener(() => DisbandGuildAsync().Forget());
            guildInviteAcceptButton?.onClick.RemoveAllListeners();
            guildInviteAcceptButton?.onClick.AddListener(() => RespondToGuildInviteAsync(true).Forget());
            guildInviteDeclineButton?.onClick.RemoveAllListeners();
            guildInviteDeclineButton?.onClick.AddListener(() => RespondToGuildInviteAsync(false).Forget());

            storageCloseButton?.onClick.RemoveAllListeners();
            storageCloseButton?.onClick.AddListener(CloseStorageLocal);
        }

        private void BindManager()
        {
            PlayerEntityGameManager manager = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == manager)
                return;

            UnbindManager();
            _manager = manager;
            if (_manager == null)
                return;

            _manager.FriendsStateReceived += OnFriendsStateReceived;
            _manager.PartyStateReceived += OnPartyStateReceived;
            _manager.GuildStateReceived += OnGuildStateReceived;
            _manager.StorageStateReceived += OnStorageStateReceived;
            _manager.PlayerItemsChangedReceived += OnPlayerItemsChanged;
            _manager.ContextInteractionResultReceived += OnContextInteractionResultReceived;
        }

        private void UnbindManager()
        {
            if (_manager == null)
                return;
            _manager.FriendsStateReceived -= OnFriendsStateReceived;
            _manager.PartyStateReceived -= OnPartyStateReceived;
            _manager.GuildStateReceived -= OnGuildStateReceived;
            _manager.StorageStateReceived -= OnStorageStateReceived;
            _manager.PlayerItemsChangedReceived -= OnPlayerItemsChanged;
            _manager.ContextInteractionResultReceived -= OnContextInteractionResultReceived;
            _manager = null;
        }

        public void SetSocialVisible(bool visible)
        {
#if !UNITY_SERVER
            _socialVisible = visible;
            if (visible && _manager == null)
                BindManager();
            if (visible)
                RenderSocial();
            ReconcileFriendsPresenceInterest();
#endif
        }

        public void ResetPresentation()
        {
#if !UNITY_SERVER
            _socialVisible = false;
            _friendsPresenceDesired = false;
            CancelInvoke(nameof(CloseExpiredPartyInvite));
            if (storageWindowRoot != null) storageWindowRoot.SetActive(false);
            if (friendInvitePopupRoot != null) friendInvitePopupRoot.SetActive(false);
            if (partyInvitePopupRoot != null) partyInvitePopupRoot.SetActive(false);
            if (guildInvitePopupRoot != null) guildInvitePopupRoot.SetActive(false);
            if (partyHudRoot != null) partyHudRoot.SetActive(false);
            ClearRows(_friendRows);
            ClearRows(_partyRows);
            ClearRows(_guildRows);
            ClearRows(_storageInventoryRows);
            ClearRows(_storageRows);
            ReconcileFriendsPresenceInterest();
#endif
        }

        public void CloseStorageLocal()
        {
#if !UNITY_SERVER
            if (storageWindowRoot != null)
                storageWindowRoot.SetActive(false);
#endif
        }

        private void SelectFriendsTab()
        {
            SelectFriendsTabInternal();
            RenderFriends();
            ReconcileFriendsPresenceInterest();
        }

        private void SelectFriendsTabInternal()
        {
            _socialTab = 0;
            if (friendsTabRoot != null) friendsTabRoot.SetActive(true);
            if (partyTabRoot != null) partyTabRoot.SetActive(false);
            if (guildTabRoot != null) guildTabRoot.SetActive(false);
        }

        private void SelectPartyTab()
        {
            _socialTab = 1;
            if (friendsTabRoot != null) friendsTabRoot.SetActive(false);
            if (partyTabRoot != null) partyTabRoot.SetActive(true);
            if (guildTabRoot != null) guildTabRoot.SetActive(false);
            RenderParty();
            ReconcileFriendsPresenceInterest();
        }

        private void SelectGuildTab()
        {
            _socialTab = 2;
            if (friendsTabRoot != null) friendsTabRoot.SetActive(false);
            if (partyTabRoot != null) partyTabRoot.SetActive(false);
            if (guildTabRoot != null) guildTabRoot.SetActive(true);
            RenderGuild();
            ReconcileFriendsPresenceInterest();
        }

        private void OpenPartyFromHud()
        {
            StandaloneClientUIRoot root = StandaloneClientUIRoot.Instance;
            root?.OpenSocial();
            if (root == null || !root.SocialOpen)
                return;
            SelectPartyTab();
        }

        private void RenderAll()
        {
            RenderSocial();
            RenderParty();
            if (_manager != null)
            {
                PresentFriendInvite(_manager.LatestFriends);
                PresentPartyInvite(_manager.LatestParty);
                PresentGuildInvite(_manager.LatestGuild);
                if (StorageOpen)
                    RenderStorage(_manager.LatestStorage);
            }
        }

        private void RenderSocial()
        {
            if (_socialTab == 1) RenderParty(); else if (_socialTab == 2) RenderGuild(); else RenderFriends();
        }

        private void OnFriendsStateReceived(FriendsStateMessage state)
        {
            if (_socialTab == 0)
                RenderFriends();
            PresentFriendInvite(state);
        }

        private void OnPartyStateReceived(PartyStateMessage state)
        {
            RenderParty();
            PresentPartyInvite(state);
        }

        private void OnGuildStateReceived(GuildStateMessage state)
        {
            if (_socialTab == 2) RenderGuild();
            PresentGuildInvite(state);
        }

        private void OnStorageStateReceived(StorageStateMessage state)
        {
            if (state.capacity <= 0)
            {
                if (StorageOpen && !string.IsNullOrWhiteSpace(state.detail))
                    PresentLocalMessage("STORAGE: " + state.detail);
                CloseStorageLocal();
                return;
            }
            if (StorageOpen)
                RenderStorage(state);
        }

        private void OnPlayerItemsChanged(PlayerItemsResponseMessage _)
        {
            if (StorageOpen && _manager != null)
                RenderStorage(_manager.LatestStorage);
        }

        private void OnContextInteractionResultReceived(ContextInteractionResponseMessage response)
        {
            if (!response.success || response.ActionId != InteractionActionId.OpenStorage)
                return;
            OpenStorageAfterAuthorityAsync().Forget();
        }

        private void RenderFriends()
        {
            if (_manager == null || friendsStatusText == null)
                return;

            ClearRows(_friendRows);
            FriendEntryWire[] friends = _manager.LatestFriends.friends ?? Array.Empty<FriendEntryWire>();
            bool livePresence = (_manager.OwnerLiveInterests & OwnerLiveInterestKind.FriendsPresence) != 0;
            int online = 0;
            if (livePresence)
            {
                for (int i = 0; i < friends.Length; ++i)
                    if (friends[i].online) online++;
            }

            if (!_manager.HasFriendsCache)
                friendsStatusText.text = "Friends state has not hydrated yet.";
            else if (livePresence)
                friendsStatusText.text = $"Friends {friends.Length}   Online {online}";
            else
                friendsStatusText.text = $"Friends {friends.Length}   Presence updates resume while this tab is open.";

            for (int i = 0; i < friends.Length; ++i)
            {
                FriendEntryWire friend = friends[i];
                ClientSocialEconomyRowView row = CreateRow(friendsContent, friendsRowTemplate, _friendRows);
                if (row == null)
                    continue;
                long characterId = friend.characterId;
                string displayName = string.IsNullOrWhiteSpace(friend.name) ? $"Character {characterId}" : friend.name;
                string presence = livePresence ? (friend.online ? "Online" : "Offline") : "Presence paused";
                row.Configure(displayName, presence, "REMOVE", () => RemoveFriendAsync(characterId).Forget());
            }
        }

        private void RenderParty()
        {
            if (_manager == null)
                return;

            ClearRows(_partyRows);
            PartyStateMessage state = _manager.LatestParty;
            PartyMemberWire[] members = state.members ?? Array.Empty<PartyMemberWire>();
            bool inParty = state.partyId != 0 && members.Length > 0;
            bool ownerIsLeader = inParty && state.ownerCharacterId > 0 && state.ownerCharacterId == state.leaderCharacterId;

            if (partyHudRoot != null)
                partyHudRoot.SetActive(inParty);

            if (!inParty)
            {
                if (partyStatusText != null)
                    partyStatusText.text = "Not in a party. Invite another player through the interaction menu.";
                if (partyHudText != null)
                    partyHudText.text = string.Empty;
                if (partyLeaveButton != null) partyLeaveButton.gameObject.SetActive(false);
                if (partyDisbandButton != null) partyDisbandButton.gameObject.SetActive(false);
                return;
            }

            if (partyStatusText != null)
                partyStatusText.text = $"Party {members.Length}/8" + (ownerIsLeader ? "   You are leader" : string.Empty);

            if (partyHudText != null)
            {
                var hud = new System.Text.StringBuilder(96);
                hud.Append("PARTY ").Append(members.Length).Append("/8");
                for (int i = 0; i < members.Length; ++i)
                {
                    PartyMemberWire member = members[i];
                    hud.Append('\n');
                    if (member.isLeader) hud.Append("★ ");
                    hud.Append(DisplayPartyMemberName(member));
                }
                partyHudText.text = hud.ToString();
            }

            for (int i = 0; i < members.Length; ++i)
            {
                PartyMemberWire member = members[i];
                string memberName = DisplayPartyMemberName(member);
                bool canKick = ownerIsLeader && member.characterId > 0 && member.characterId != state.ownerCharacterId;
                ClientSocialEconomyRowView row = CreateRow(partyContent, partyRowTemplate, _partyRows);
                if (row == null)
                    continue;
                row.Configure(
                    memberName,
                    member.isLeader ? "Leader" : "Member",
                    canKick ? "KICK" : null,
                    canKick ? (Action)(() => SubmitPartyCommand($"kick {memberName}")) : null);
            }

            if (partyLeaveButton != null) partyLeaveButton.gameObject.SetActive(true);
            if (partyDisbandButton != null) partyDisbandButton.gameObject.SetActive(ownerIsLeader);
        }

        private void RenderGuild()
        {
            if (_manager == null || guildStatusText == null) return;
            ClearRows(_guildRows);
            GuildStateMessage state = _manager.LatestGuild;
            GuildMemberWire[] members = state.members ?? Array.Empty<GuildMemberWire>();
            bool inGuild = state.guildId > 0;
            bool owner = inGuild && state.ownerCharacterId > 0 && Array.Exists(members, m => m.characterId == state.ownerCharacterId && m.role == 1);

            guildStatusText.text = !_manager.HasGuildCache ? "Guild state has not hydrated yet." :
                !inGuild ? "Not in a guild. Create one here or accept an invite." : $"{state.guildName}   Members {members.Length}";
            if (guildNameInput != null) guildNameInput.gameObject.SetActive(!inGuild);
            if (guildCreateButton != null) guildCreateButton.gameObject.SetActive(!inGuild);
            if (guildLeaveButton != null) guildLeaveButton.gameObject.SetActive(inGuild && !owner);
            if (guildDisbandButton != null) guildDisbandButton.gameObject.SetActive(owner);

            for (int i = 0; i < members.Length; ++i)
            {
                GuildMemberWire member = members[i];
                ClientSocialEconomyRowView row = CreateRow(guildContent, guildRowTemplate, _guildRows);
                if (row == null) continue;
                string display = string.IsNullOrWhiteSpace(member.name) ? $"Character {member.characterId}" : member.name;
                bool canKick = owner && member.characterId != state.ownerCharacterId;
                long targetId = member.characterId;
                row.Configure(display, member.role == 1 ? "Owner" : "Member", canKick ? "KICK" : null,
                    canKick ? (Action)(() => KickGuildMemberAsync(targetId).Forget()) : null);
            }
        }

        private async UniTaskVoid CreateGuildAsync()
        {
            if (_manager == null) return;
            GuildMutationResponseMessage result = await _manager.RequestCreateGuildAsync(guildNameInput != null ? guildNameInput.text : string.Empty);
            await UniTask.SwitchToMainThread();
            PresentLocalMessage(result.success ? "GUILD: Guild created." : "GUILD: " + ErrorOrFallback(result.detail));
        }

        private async UniTaskVoid LeaveGuildAsync()
        {
            if (_manager == null) return;
            GuildMutationResponseMessage result = await _manager.RequestLeaveGuildAsync();
            await UniTask.SwitchToMainThread();
            PresentLocalMessage(result.success ? "GUILD: Left guild." : "GUILD: " + ErrorOrFallback(result.detail));
        }

        private async UniTaskVoid DisbandGuildAsync()
        {
            if (_manager == null) return;
            GuildMutationResponseMessage result = await _manager.RequestDisbandGuildAsync();
            await UniTask.SwitchToMainThread();
            PresentLocalMessage(result.success ? "GUILD: Guild disbanded." : "GUILD: " + ErrorOrFallback(result.detail));
        }

        private async UniTaskVoid KickGuildMemberAsync(long characterId)
        {
            if (_manager == null) return;
            GuildMutationResponseMessage result = await _manager.RequestKickGuildMemberAsync(characterId);
            await UniTask.SwitchToMainThread();
            PresentLocalMessage(result.success ? "GUILD: Member removed." : "GUILD: " + ErrorOrFallback(result.detail));
        }

        private async UniTaskVoid RemoveFriendAsync(long characterId)
        {
            if (_manager == null || characterId <= 0)
                return;
            SocialEconomyMutationResponseMessage result = await _manager.RequestRemoveFriendAsync(characterId);
            await UniTask.SwitchToMainThread();
            PresentLocalMessage(result.success ? "FRIENDS: Friend removal submitted." : "FRIENDS: " + ErrorOrFallback(result.error));
        }

        private void SubmitPartyCommand(string arguments)
        {
            if (_manager == null)
                return;
            if (!_manager.TrySubmitPartyCommand(arguments, out string error))
                PresentLocalMessage("PARTY: " + ErrorOrFallback(error));
        }

        private void PresentFriendInvite(FriendsStateMessage state)
        {
            if (friendInvitePopupRoot == null)
                return;
            if (state.pendingInviterCharacterId <= 0)
            {
                friendInvitePopupRoot.SetActive(false);
                return;
            }

            if (friendInviteText != null)
                friendInviteText.text = string.IsNullOrWhiteSpace(state.pendingInviterName)
                    ? $"Character {state.pendingInviterCharacterId} wants to add you as a friend."
                    : $"{state.pendingInviterName} wants to add you as a friend.";
            friendInvitePopupRoot.SetActive(true);
        }

        private void PresentPartyInvite(PartyStateMessage state)
        {
            CancelInvoke(nameof(CloseExpiredPartyInvite));
            if (partyInvitePopupRoot == null)
                return;
            if (state.pendingInviterCharacterId <= 0 || state.pendingInviteSecondsRemaining == 0)
            {
                partyInvitePopupRoot.SetActive(false);
                return;
            }

            if (partyInviteText != null)
                partyInviteText.text = string.IsNullOrWhiteSpace(state.pendingInviterName)
                    ? $"Character {state.pendingInviterCharacterId} invited you to a party."
                    : $"{state.pendingInviterName} invited you to a party.";
            partyInvitePopupRoot.SetActive(true);
            Invoke(nameof(CloseExpiredPartyInvite), state.pendingInviteSecondsRemaining);
        }

        private async UniTaskVoid RespondToFriendInviteAsync(bool accept)
        {
            if (_manager == null)
                return;
            long inviterId = _manager.LatestFriends.pendingInviterCharacterId;
            if (inviterId <= 0)
            {
                if (friendInvitePopupRoot != null) friendInvitePopupRoot.SetActive(false);
                return;
            }

            SocialEconomyMutationResponseMessage result = accept
                ? await _manager.RequestAcceptFriendAsync(inviterId)
                : await _manager.RequestDeclineFriendAsync(inviterId);
            await UniTask.SwitchToMainThread();
            if (result.success)
            {
                if (friendInvitePopupRoot != null) friendInvitePopupRoot.SetActive(false);
                PresentLocalMessage(accept ? "FRIENDS: Friend request accepted." : "FRIENDS: Friend request declined.");
            }
            else
            {
                PresentLocalMessage("FRIENDS: " + ErrorOrFallback(result.error));
            }
        }

        private void RespondToPartyInvite(bool accept)
        {
            if (_manager == null)
                return;
            if (!_manager.TrySubmitPartyCommand(accept ? "accept" : "decline", out string error))
            {
                PresentLocalMessage("PARTY: " + ErrorOrFallback(error));
                return;
            }
            if (partyInvitePopupRoot != null) partyInvitePopupRoot.SetActive(false);
            CancelInvoke(nameof(CloseExpiredPartyInvite));
        }

        private void PresentGuildInvite(GuildStateMessage state)
        {
            CancelInvoke(nameof(CloseExpiredGuildInvite));
            if (guildInvitePopupRoot == null) return;
            if (state.pendingInviterCharacterId <= 0 || state.pendingInviteSecondsRemaining == 0)
            { guildInvitePopupRoot.SetActive(false); return; }
            if (guildInviteText != null)
                guildInviteText.text = $"{(string.IsNullOrWhiteSpace(state.pendingInviterName) ? $"Character {state.pendingInviterCharacterId}" : state.pendingInviterName)} invited you to {(string.IsNullOrWhiteSpace(state.pendingGuildName) ? "a guild" : state.pendingGuildName)}.";
            guildInvitePopupRoot.SetActive(true);
            Invoke(nameof(CloseExpiredGuildInvite), state.pendingInviteSecondsRemaining);
        }

        private async UniTaskVoid RespondToGuildInviteAsync(bool accept)
        {
            if (_manager == null) return;
            GuildMutationResponseMessage result = accept ? await _manager.RequestAcceptGuildInviteAsync() : await _manager.RequestDeclineGuildInviteAsync();
            await UniTask.SwitchToMainThread();
            if (result.success && guildInvitePopupRoot != null) guildInvitePopupRoot.SetActive(false);
            PresentLocalMessage(result.success ? (accept ? "GUILD: Invite accepted." : "GUILD: Invite declined.") : "GUILD: " + ErrorOrFallback(result.detail));
        }

        private void CloseExpiredGuildInvite() { if (guildInvitePopupRoot != null) guildInvitePopupRoot.SetActive(false); }

        private void CloseExpiredPartyInvite()
        {
            if (partyInvitePopupRoot != null)
                partyInvitePopupRoot.SetActive(false);
        }

        private void ReconcileFriendsPresenceInterest()
        {
            bool desired = _socialVisible && _socialTab == 0;
            _friendsPresenceDesired = desired;
            ApplyFriendsPresenceInterestAsync().Forget();
        }

        private async UniTaskVoid ApplyFriendsPresenceInterestAsync()
        {
            if (_manager == null || _friendsPresenceUpdatePending)
                return;

            bool actual = (_manager.OwnerLiveInterests & OwnerLiveInterestKind.FriendsPresence) != 0;
            if (actual == _friendsPresenceDesired)
                return;

            _friendsPresenceUpdatePending = true;
            bool applied = false;
            try
            {
                SocialEconomyMutationResponseMessage result = await _manager.SetOwnerLiveInterestAsync(
                    OwnerLiveInterestKind.FriendsPresence,
                    _friendsPresenceDesired);
                await UniTask.SwitchToMainThread();
                applied = result.success;
                if (!result.success)
                    PresentLocalMessage("FRIENDS: presence interest update failed: " + ErrorOrFallback(result.error));
                if (_socialTab == 0)
                    RenderFriends();
            }
            finally
            {
                _friendsPresenceUpdatePending = false;
                // Retry only when a successful request completed while desired visibility changed.
                // A transport/backend failure must not turn into a request loop.
                if (applied && _manager != null)
                {
                    bool now = (_manager.OwnerLiveInterests & OwnerLiveInterestKind.FriendsPresence) != 0;
                    if (now != _friendsPresenceDesired)
                        ApplyFriendsPresenceInterestAsync().Forget();
                }
            }
        }

        private async UniTaskVoid OpenStorageAfterAuthorityAsync()
        {
            await UniTask.SwitchToMainThread();
            if (_manager == null)
                BindManager();
            if (_manager == null || storageWindowRoot == null)
                return;

            StandaloneClientUIRoot root = StandaloneClientUIRoot.Instance;
            if (root == null || !root.PrepareForStorageWindow(storageWindowRoot))
                return;

            RenderStorage(_manager.LatestStorage);
            if (!_manager.HasPlayerItemsCache)
                await _manager.RequestPlayerItemsAsync(false);
            StorageStateMessage state = await _manager.RequestStorageAsync(false);
            await UniTask.SwitchToMainThread();
            if (storageWindowRoot != null && storageWindowRoot.activeSelf)
                RenderStorage(state);
        }

        private void RenderStorage(StorageStateMessage state)
        {
            if (_manager == null)
                return;

            PlayerItemWire[] stored = state.items ?? Array.Empty<PlayerItemWire>();
            if (storageStatusText != null)
                storageStatusText.text = state.capacity > 0
                    ? (string.IsNullOrWhiteSpace(state.detail) ? "Storage ready." : state.detail)
                    : "Loading authorized storage state...";
            if (storageCapacityText != null)
                storageCapacityText.text = state.capacity > 0
                    ? $"Storage {stored.Length}/{state.capacity}"
                    : "Storage capacity pending";

            ClearRows(_storageInventoryRows);
            PlayerItemWire[] inventory = _manager.LatestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            bool canDeposit = state.capacity > 0 && !_storageMutationBusy;
            for (int i = 0; i < inventory.Length; ++i)
            {
                PlayerItemWire item = inventory[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;
                int slot = item.inventorySlot;
                int quantity = item.quantity;
                ClientSocialEconomyRowView row = CreateRow(storageInventoryContent, storageInventoryRowTemplate, _storageInventoryRows);
                if (row == null)
                    continue;
                row.Configure(
                    $"{ItemName(item)} x{quantity}",
                    $"Inventory slot {slot}",
                    canDeposit ? "STORE 1" : null,
                    canDeposit ? (Action)(() => StorageTransferAsync(true, slot, 1).Forget()) : null,
                    canDeposit && quantity > 1 ? "STORE ALL" : null,
                    canDeposit && quantity > 1 ? (Action)(() => StorageTransferAsync(true, slot, quantity).Forget()) : null,
                    canDeposit ? (Action)(() => BeginStorageInventoryDrag(slot, item)) : null,
                    EndStorageDrag,
                    DropOnStorageInventoryRow);
            }

            ClearRows(_storageRows);
            bool canWithdraw = state.capacity > 0 && !_storageMutationBusy;
            for (int i = 0; i < stored.Length; ++i)
            {
                PlayerItemWire item = stored[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;
                int slot = item.inventorySlot;
                int quantity = item.quantity;
                ClientSocialEconomyRowView row = CreateRow(storageContent, storageRowTemplate, _storageRows);
                if (row == null)
                    continue;
                row.Configure(
                    $"{ItemName(item)} x{quantity}",
                    $"Storage slot {slot}",
                    canWithdraw ? "TAKE 1" : null,
                    canWithdraw ? (Action)(() => StorageTransferAsync(false, slot, 1).Forget()) : null,
                    canWithdraw && quantity > 1 ? "TAKE ALL" : null,
                    canWithdraw && quantity > 1 ? (Action)(() => StorageTransferAsync(false, slot, quantity).Forget()) : null,
                    canWithdraw ? (Action)(() => BeginStoredItemDrag(slot, item)) : null,
                    EndStorageDrag,
                    DropOnStoredItemRow);
            }
        }

        private void BeginStorageInventoryDrag(int inventorySlot, PlayerItemWire item)
        {
            if (_storageMutationBusy || !StorageOpen)
                return;
            StandaloneItemDragContext.BeginInventory(inventorySlot, item);
            GetDragGhost()?.Show(ClientItemIconResolver.Resolve(item));
        }

        private void BeginStoredItemDrag(int storageSlot, PlayerItemWire item)
        {
            if (_storageMutationBusy || !StorageOpen)
                return;
            StandaloneItemDragContext.BeginStorage(storageSlot, item);
            GetDragGhost()?.Show(ClientItemIconResolver.Resolve(item));
        }

        private void DropOnStorageInventoryRow()
        {
            if (_storageMutationBusy || StandaloneItemDragContext.Kind != StandaloneItemDragContext.SourceKind.Storage)
                return;
            int slot = StandaloneItemDragContext.SlotIndex;
            int quantity = Math.Max(1, StandaloneItemDragContext.Item.quantity);
            EndStorageDrag();
            StorageTransferAsync(false, slot, quantity).Forget();
        }

        private void DropOnStoredItemRow()
        {
            if (_storageMutationBusy || StandaloneItemDragContext.Kind != StandaloneItemDragContext.SourceKind.Inventory)
                return;
            int slot = StandaloneItemDragContext.SlotIndex;
            int quantity = Math.Max(1, StandaloneItemDragContext.Item.quantity);
            EndStorageDrag();
            StorageTransferAsync(true, slot, quantity).Forget();
        }

        private void EndStorageDrag()
        {
            StandaloneItemDragContext.Clear();
            GetDragGhost()?.Hide();
        }

        private StandaloneDragGhost GetDragGhost()
        {
            if (_dragGhost == null)
                _dragGhost = FindFirstObjectByType<StandaloneDragGhost>();
            return _dragGhost;
        }

        private async UniTaskVoid StorageTransferAsync(bool deposit, int sourceSlot, int quantity)
        {
            if (_manager == null || _storageMutationBusy)
                return;
            _storageMutationBusy = true;
            RenderStorage(_manager.LatestStorage);
            if (storageStatusText != null)
                storageStatusText.text = deposit ? "Depositing item..." : "Withdrawing item...";
            try
            {
                SocialEconomyMutationResponseMessage result = deposit
                    ? await _manager.RequestDepositToStorageAsync(sourceSlot, quantity)
                    : await _manager.RequestWithdrawFromStorageAsync(sourceSlot, quantity);
                await UniTask.SwitchToMainThread();
                if (!result.success)
                {
                    PresentLocalMessage("STORAGE: " + ErrorOrFallback(result.error));
                    if (storageStatusText != null)
                        storageStatusText.text = ErrorOrFallback(result.error);
                }
                else if (storageStatusText != null)
                {
                    storageStatusText.text = "Storage transfer submitted. Waiting for authoritative state...";
                }
            }
            finally
            {
                _storageMutationBusy = false;
                if (_manager != null && StorageOpen)
                    RenderStorage(_manager.LatestStorage);
            }
        }

        private static ClientSocialEconomyRowView CreateRow(
            Transform parent,
            ClientSocialEconomyRowView template,
            List<ClientSocialEconomyRowView> rows)
        {
            if (parent == null || template == null)
                return null;
            ClientSocialEconomyRowView row = Instantiate(template, parent, false);
            row.name = "Row";
            row.gameObject.SetActive(true);
            rows.Add(row);
            return row;
        }

        private static void ClearRows(List<ClientSocialEconomyRowView> rows)
        {
            for (int i = 0; i < rows.Count; ++i)
                if (rows[i] != null)
                    Destroy(rows[i].gameObject);
            rows.Clear();
        }

        private void HideTemplates()
        {
            friendsRowTemplate?.gameObject.SetActive(false);
            partyRowTemplate?.gameObject.SetActive(false);
            storageInventoryRowTemplate?.gameObject.SetActive(false);
            storageRowTemplate?.gameObject.SetActive(false);
        }

        private static string DisplayPartyMemberName(PartyMemberWire member) =>
            string.IsNullOrWhiteSpace(member.name) ? $"Character {member.characterId}" : member.name;

        private static string ItemName(PlayerItemWire item)
        {
            if (!string.IsNullOrWhiteSpace(item.displayName)) return item.displayName;
            if (!string.IsNullOrWhiteSpace(item.definitionId)) return item.definitionId;
            return item.itemDataId != 0 ? $"Item {item.itemDataId}" : "Item";
        }

        private static string ErrorOrFallback(string error) =>
            string.IsNullOrWhiteSpace(error) ? "Request was rejected." : error;

        private static void PresentLocalMessage(string message)
        {
            if (ClientGameplayUIRoot.Instance != null)
                ClientGameplayUIRoot.Instance.PresentLocalSystemMessage(message ?? string.Empty);
            else if (!string.IsNullOrWhiteSpace(message))
                Debug.Log("[StandaloneUI] " + message);
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject authoredSocialWindow,
            Button authoredFriendsTabButton,
            Button authoredPartyTabButton,
            Button authoredGuildTabButton,
            GameObject authoredFriendsTabRoot,
            GameObject authoredPartyTabRoot,
            GameObject authoredGuildTabRoot,
            Text authoredFriendsStatusText,
            Transform authoredFriendsContent,
            ClientSocialEconomyRowView authoredFriendsRowTemplate,
            Text authoredPartyStatusText,
            Transform authoredPartyContent,
            ClientSocialEconomyRowView authoredPartyRowTemplate,
            Button authoredPartyLeaveButton,
            Button authoredPartyDisbandButton,
            Text authoredGuildStatusText,
            Transform authoredGuildContent,
            ClientSocialEconomyRowView authoredGuildRowTemplate,
            InputField authoredGuildNameInput,
            Button authoredGuildCreateButton,
            Button authoredGuildLeaveButton,
            Button authoredGuildDisbandButton,
            GameObject authoredGuildInvitePopupRoot,
            Text authoredGuildInviteText,
            Button authoredGuildInviteAcceptButton,
            Button authoredGuildInviteDeclineButton,
            GameObject authoredPartyHudRoot,
            Text authoredPartyHudText,
            Button authoredPartyHudOpenButton,
            GameObject authoredFriendInvitePopupRoot,
            Text authoredFriendInviteText,
            Button authoredFriendInviteAcceptButton,
            Button authoredFriendInviteDeclineButton,
            GameObject authoredPartyInvitePopupRoot,
            Text authoredPartyInviteText,
            Button authoredPartyInviteAcceptButton,
            Button authoredPartyInviteDeclineButton,
            GameObject authoredStorageWindowRoot,
            Text authoredStorageStatusText,
            Text authoredStorageCapacityText,
            Button authoredStorageCloseButton,
            Transform authoredStorageInventoryContent,
            ClientSocialEconomyRowView authoredStorageInventoryRowTemplate,
            Transform authoredStorageContent,
            ClientSocialEconomyRowView authoredStorageRowTemplate)
        {
            socialWindowRoot = authoredSocialWindow;
            friendsTabButton = authoredFriendsTabButton;
            partyTabButton = authoredPartyTabButton;
            guildTabButton = authoredGuildTabButton;
            friendsTabRoot = authoredFriendsTabRoot;
            partyTabRoot = authoredPartyTabRoot;
            guildTabRoot = authoredGuildTabRoot;
            friendsStatusText = authoredFriendsStatusText;
            friendsContent = authoredFriendsContent;
            friendsRowTemplate = authoredFriendsRowTemplate;
            partyStatusText = authoredPartyStatusText;
            partyContent = authoredPartyContent;
            partyRowTemplate = authoredPartyRowTemplate;
            partyLeaveButton = authoredPartyLeaveButton;
            partyDisbandButton = authoredPartyDisbandButton;
            guildStatusText = authoredGuildStatusText;
            guildContent = authoredGuildContent;
            guildRowTemplate = authoredGuildRowTemplate;
            guildNameInput = authoredGuildNameInput;
            guildCreateButton = authoredGuildCreateButton;
            guildLeaveButton = authoredGuildLeaveButton;
            guildDisbandButton = authoredGuildDisbandButton;
            guildInvitePopupRoot = authoredGuildInvitePopupRoot;
            guildInviteText = authoredGuildInviteText;
            guildInviteAcceptButton = authoredGuildInviteAcceptButton;
            guildInviteDeclineButton = authoredGuildInviteDeclineButton;
            partyHudRoot = authoredPartyHudRoot;
            partyHudText = authoredPartyHudText;
            partyHudOpenButton = authoredPartyHudOpenButton;
            friendInvitePopupRoot = authoredFriendInvitePopupRoot;
            friendInviteText = authoredFriendInviteText;
            friendInviteAcceptButton = authoredFriendInviteAcceptButton;
            friendInviteDeclineButton = authoredFriendInviteDeclineButton;
            partyInvitePopupRoot = authoredPartyInvitePopupRoot;
            partyInviteText = authoredPartyInviteText;
            partyInviteAcceptButton = authoredPartyInviteAcceptButton;
            partyInviteDeclineButton = authoredPartyInviteDeclineButton;
            storageWindowRoot = authoredStorageWindowRoot;
            storageStatusText = authoredStorageStatusText;
            storageCapacityText = authoredStorageCapacityText;
            storageCloseButton = authoredStorageCloseButton;
            storageInventoryContent = authoredStorageInventoryContent;
            storageInventoryRowTemplate = authoredStorageInventoryRowTemplate;
            storageContent = authoredStorageContent;
            storageRowTemplate = authoredStorageRowTemplate;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
