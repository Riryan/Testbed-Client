using System;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Root;
using Game.Client.UI.SocialEconomy;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Social
{
    /// <summary>
    /// Thin presentation binding over the existing owner-local Social/Economy and Party caches.
    /// Party membership remains server-authoritative and event-driven; UI actions reuse the
    /// existing /party social-command path instead of introducing another request protocol.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientSocialEconomyUI : MonoBehaviour
    {
        [Header("Entry point")]
        [SerializeField] private Button openFriendsButton;

        [Header("Friends window")]
        [SerializeField] private Text friendsSummaryText;
        [SerializeField] private RectTransform friendsListRoot;
        [SerializeField] private ClientSocialEconomyFriendRow friendRowTemplate;

        [Header("Social / Friends tab")]
        [SerializeField] private GameObject socialFriendsTab;
        [SerializeField] private Text socialFriendsStatusText;
        [SerializeField] private RectTransform socialFriendsListRoot;
        [SerializeField] private ClientSocialEconomyRowView socialFriendsRowTemplate;

        [Header("Friend invite popup")]
        [SerializeField] private Text friendInviteText;
        [SerializeField] private Button friendInviteAcceptButton;
        [SerializeField] private Button friendInviteDeclineButton;

        [Header("Party HUD")]
        [SerializeField] private GameObject partyHudRoot;
        [SerializeField] private Text partyHudText;
        [SerializeField] private Button openPartyButton;

        [Header("Party window")]
        [SerializeField] private Text partySummaryText;
        [SerializeField] private RectTransform partyListRoot;
        [SerializeField] private ClientSocialEconomyRowView partyRowTemplate;
        [SerializeField] private Button partyLeaveButton;
        [SerializeField] private Button partyDisbandButton;

        [Header("Party invite popup")]
        [SerializeField] private Text partyInviteText;
        [SerializeField] private Button partyInviteAcceptButton;
        [SerializeField] private Button partyInviteDeclineButton;

        private ClientUIRoot _root;
        private PlayerEntityGameManager _manager;
        private bool _friendsInterestDesired;
        private bool _interestUpdatePending;
        private int _friendsStateReceiveCount;

        public bool HasAuthoredBindings =>
            openFriendsButton != null && friendsSummaryText != null && friendsListRoot != null && friendRowTemplate != null &&
            socialFriendsTab != null && socialFriendsStatusText != null && socialFriendsListRoot != null && socialFriendsRowTemplate != null &&
            friendInviteText != null && friendInviteAcceptButton != null && friendInviteDeclineButton != null &&
            partyHudRoot != null && partyHudText != null && openPartyButton != null &&
            partySummaryText != null && partyListRoot != null && partyRowTemplate != null &&
            partyLeaveButton != null && partyDisbandButton != null &&
            partyInviteText != null && partyInviteAcceptButton != null && partyInviteDeclineButton != null;

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Button openFriends,
            Text summary,
            RectTransform listRoot,
            ClientSocialEconomyFriendRow rowTemplate,
            GameObject socialTab,
            Text socialStatus,
            RectTransform socialListRoot,
            ClientSocialEconomyRowView socialRowTemplate,
            Text inviteText,
            Button accept,
            Button decline,
            GameObject authoredPartyHudRoot,
            Text authoredPartyHudText,
            Button authoredOpenPartyButton,
            Text authoredPartySummary,
            RectTransform authoredPartyListRoot,
            ClientSocialEconomyRowView authoredPartyRowTemplate,
            Button authoredPartyLeaveButton,
            Button authoredPartyDisbandButton,
            Text authoredPartyInviteText,
            Button authoredPartyInviteAcceptButton,
            Button authoredPartyInviteDeclineButton)
        {
            openFriendsButton = openFriends;
            friendsSummaryText = summary;
            friendsListRoot = listRoot;
            friendRowTemplate = rowTemplate;
            socialFriendsTab = socialTab;
            socialFriendsStatusText = socialStatus;
            socialFriendsListRoot = socialListRoot;
            socialFriendsRowTemplate = socialRowTemplate;
            friendInviteText = inviteText;
            friendInviteAcceptButton = accept;
            friendInviteDeclineButton = decline;

            partyHudRoot = authoredPartyHudRoot;
            partyHudText = authoredPartyHudText;
            openPartyButton = authoredOpenPartyButton;
            partySummaryText = authoredPartySummary;
            partyListRoot = authoredPartyListRoot;
            partyRowTemplate = authoredPartyRowTemplate;
            partyLeaveButton = authoredPartyLeaveButton;
            partyDisbandButton = authoredPartyDisbandButton;
            partyInviteText = authoredPartyInviteText;
            partyInviteAcceptButton = authoredPartyInviteAcceptButton;
            partyInviteDeclineButton = authoredPartyInviteDeclineButton;
        }
#endif

        public void BindRoot(ClientUIRoot root)
        {
            if (_root == root) return;
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged -= OnPanelVisibilityChanged;
            _root = root;
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged += OnPanelVisibilityChanged;

            if (openFriendsButton != null)
            {
                openFriendsButton.onClick.RemoveListener(OpenFriends);
                openFriendsButton.onClick.AddListener(OpenFriends);
            }
            if (openPartyButton != null)
            {
                openPartyButton.onClick.RemoveListener(OpenParty);
                openPartyButton.onClick.AddListener(OpenParty);
            }
            if (partyLeaveButton != null)
            {
                partyLeaveButton.onClick.RemoveAllListeners();
                partyLeaveButton.onClick.AddListener(() => SubmitPartyCommand("leave"));
            }
            if (partyDisbandButton != null)
            {
                partyDisbandButton.onClick.RemoveAllListeners();
                partyDisbandButton.onClick.AddListener(() => SubmitPartyCommand("disband"));
            }

            RenderParty();
            if (_manager != null)
                PresentPartyInvite(_manager.LatestParty);
            ReconcileFriendsInterest();
        }

        public void BindManager(PlayerEntityGameManager manager)
        {
            if (_manager == manager) return;
            if (_manager != null)
            {
                _manager.FriendsStateReceived -= OnFriendsStateReceived;
                _manager.PartyStateReceived -= OnPartyStateReceived;
            }
            _manager = manager;
            if (_manager != null)
            {
                _manager.FriendsStateReceived += OnFriendsStateReceived;
                _manager.PartyStateReceived += OnPartyStateReceived;
            }
            RenderFriends();
            RenderParty();
            if (_manager != null)
                PresentPartyInvite(_manager.LatestParty);
            ReconcileFriendsInterest();
        }

        private void OpenFriends()
        {
            if (_root?.Windows == null) return;
            _root.Windows.Close(ClientUIPanelId.MainMenuWindow);
            _root.Windows.Open(ClientUIPanelId.FriendsWindow);
        }

        private void OpenParty()
        {
            if (_root?.Windows == null) return;
            _root.Windows.Open(ClientUIPanelId.PartyWindow);
            RenderParty();
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(CloseExpiredPartyInvite));
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged -= OnPanelVisibilityChanged;
            if (_manager != null)
            {
                _manager.FriendsStateReceived -= OnFriendsStateReceived;
                _manager.PartyStateReceived -= OnPartyStateReceived;
            }
        }

        private void OnPanelVisibilityChanged(ClientUIPanelId panel, bool visible)
        {
            if (panel == ClientUIPanelId.PartyWindow)
            {
                if (visible) RenderParty();
                return;
            }

            if (panel != ClientUIPanelId.FriendsWindow && panel != ClientUIPanelId.SocialWindow)
                return;
            if (visible) RenderFriends();
            ReconcileFriendsInterest();
        }

        private void ReconcileFriendsInterest()
        {
            bool desired = _root?.Windows != null &&
                           (_root.Windows.IsOpen(ClientUIPanelId.FriendsWindow) ||
                            _root.Windows.IsOpen(ClientUIPanelId.SocialWindow));
            if (_friendsInterestDesired == desired && !_interestUpdatePending) return;
            _friendsInterestDesired = desired;
            ApplyFriendsInterestAsync(desired).Forget();
        }

        private async UniTaskVoid ApplyFriendsInterestAsync(bool desired)
        {
            if (_manager == null || _interestUpdatePending) return;
            _interestUpdatePending = true;
            try
            {
                SocialEconomyMutationResponseMessage response = await _manager.SetOwnerLiveInterestAsync(
                    OwnerLiveInterestKind.FriendsPresence, desired);
                await UniTask.SwitchToMainThread();
                if (!response.success)
                    _root?.PresentSystemMessage($"FRIENDS: live presence update failed: {response.error}");
                RenderFriends();
            }
            finally
            {
                _interestUpdatePending = false;
                bool actual = _manager != null &&
                              (_manager.OwnerLiveInterests & OwnerLiveInterestKind.FriendsPresence) != 0;
                if (actual != _friendsInterestDesired)
                    ApplyFriendsInterestAsync(_friendsInterestDesired).Forget();
            }
        }

        private void OnFriendsStateReceived(FriendsStateMessage state)
        {
            _friendsStateReceiveCount++;
            RenderFriends();
            PresentPendingInvite(state);
        }

        private void OnPartyStateReceived(PartyStateMessage state)
        {
            RenderParty();
            PresentPartyInvite(state);
        }

        private void RenderFriends()
        {
            if (!HasAuthoredBindings || _manager == null) return;
            ClearRows();

            FriendEntryWire[] friends = _manager.LatestFriends.friends ?? Array.Empty<FriendEntryWire>();
            int online = 0;
            for (int i = 0; i < friends.Length; ++i)
                if (friends[i].online) online++;

            bool live = (_manager.OwnerLiveInterests & OwnerLiveInterestKind.FriendsPresence) != 0;
            string summary = _manager.HasFriendsCache
                ? $"Friends {friends.Length}  |  Online {online}  |  Presence {(live ? "ON" : "OFF")}  |  Updates {_friendsStateReceiveCount}"
                : "Loading friends...";
            friendsSummaryText.text = summary;
            if (socialFriendsStatusText != null)
                socialFriendsStatusText.text = summary;

            ClearSocialRows();
            for (int i = 0; i < friends.Length; ++i)
            {
                FriendEntryWire friend = friends[i];
                ClientSocialEconomyFriendRow row = Instantiate(friendRowTemplate, friendsListRoot, false);
                row.gameObject.name = $"Friend_{friend.characterId}";
                row.gameObject.SetActive(true);
                row.Bind(friend.characterId, friend.name, friend.online, RemoveFriend);

                if (socialFriendsListRoot != null && socialFriendsRowTemplate != null)
                {
                    ClientSocialEconomyRowView socialRow = Instantiate(socialFriendsRowTemplate, socialFriendsListRoot, false);
                    socialRow.gameObject.name = $"Friend_{friend.characterId}";
                    socialRow.Configure(
                        string.IsNullOrWhiteSpace(friend.name) ? $"Character {friend.characterId}" : friend.name,
                        friend.online ? "Online" : "Offline",
                        "REMOVE",
                        () => RemoveFriend(friend.characterId));
                }
            }
        }

        private void RenderParty()
        {
            if (!HasAuthoredBindings || _manager == null)
                return;

            ClearPartyRows();
            PartyStateMessage state = _manager.LatestParty;
            PartyMemberWire[] members = state.members ?? Array.Empty<PartyMemberWire>();
            bool inParty = state.partyId != 0 && members.Length > 0;

            if (_root?.Windows != null)
            {
                if (inParty)
                    _root.Windows.Open(ClientUIPanelId.HudPartyFrames);
                else
                    _root.Windows.Close(ClientUIPanelId.HudPartyFrames);
            }
            else if (partyHudRoot != null)
            {
                partyHudRoot.SetActive(inParty);
            }

            if (!inParty)
            {
                partySummaryText.text = "Not in a party.";
                partyHudText.text = string.Empty;
                partyLeaveButton.gameObject.SetActive(false);
                partyDisbandButton.gameObject.SetActive(false);
                return;
            }

            bool ownerIsLeader = state.ownerCharacterId > 0 && state.ownerCharacterId == state.leaderCharacterId;
            partySummaryText.text = $"Party {members.Length}/8" + (ownerIsLeader ? "  |  You are leader" : string.Empty);

            var hud = new System.Text.StringBuilder(96);
            hud.Append("PARTY ").Append(members.Length).Append("/8");
            for (int i = 0; i < members.Length; ++i)
            {
                PartyMemberWire member = members[i];
                hud.Append('\n');
                if (member.isLeader) hud.Append('*');
                hud.Append(DisplayPartyMemberName(member));
            }
            partyHudText.text = hud.ToString();

            for (int i = 0; i < members.Length; ++i)
            {
                PartyMemberWire member = members[i];
                ClientSocialEconomyRowView row = Instantiate(partyRowTemplate, partyListRoot, false);
                row.gameObject.name = $"PartyMember_{member.characterId}";

                bool canKick = ownerIsLeader && member.characterId > 0 && member.characterId != state.ownerCharacterId;
                string memberName = DisplayPartyMemberName(member);
                row.Configure(
                    memberName,
                    member.isLeader ? "Leader" : "Member",
                    canKick ? "KICK" : null,
                    canKick ? (Action)(() => SubmitPartyCommand($"kick {memberName}")) : null);
            }

            partyLeaveButton.gameObject.SetActive(true);
            partyDisbandButton.gameObject.SetActive(ownerIsLeader);
        }

        private static string DisplayPartyMemberName(PartyMemberWire member) =>
            string.IsNullOrWhiteSpace(member.name) ? $"Character {member.characterId}" : member.name;

        private void ClearRows()
        {
            if (friendsListRoot == null || friendRowTemplate == null) return;
            for (int i = friendsListRoot.childCount - 1; i >= 0; --i)
            {
                Transform child = friendsListRoot.GetChild(i);
                if (child == friendRowTemplate.transform) continue;
                Destroy(child.gameObject);
            }
        }

        private void ClearSocialRows()
        {
            if (socialFriendsListRoot == null || socialFriendsRowTemplate == null) return;
            for (int i = socialFriendsListRoot.childCount - 1; i >= 0; --i)
            {
                Transform child = socialFriendsListRoot.GetChild(i);
                if (child == socialFriendsRowTemplate.transform) continue;
                Destroy(child.gameObject);
            }
            socialFriendsRowTemplate.gameObject.SetActive(false);
        }

        private void ClearPartyRows()
        {
            if (partyListRoot == null || partyRowTemplate == null) return;
            for (int i = partyListRoot.childCount - 1; i >= 0; --i)
            {
                Transform child = partyListRoot.GetChild(i);
                if (child == partyRowTemplate.transform) continue;
                Destroy(child.gameObject);
            }
            partyRowTemplate.gameObject.SetActive(false);
        }

        private void RemoveFriend(long characterId) => RemoveFriendAsync(characterId).Forget();

        private async UniTaskVoid RemoveFriendAsync(long characterId)
        {
            if (_manager == null || characterId <= 0) return;
            SocialEconomyMutationResponseMessage result = await _manager.RequestRemoveFriendAsync(characterId);
            await UniTask.SwitchToMainThread();
            _root?.PresentSystemMessage(result.success
                ? "FRIENDS: Friend removal submitted."
                : $"FRIENDS: {result.error}");
        }

        private void SubmitPartyCommand(string arguments)
        {
            if (_manager == null)
                return;
            if (!_manager.TrySubmitPartyCommand(arguments, out string error))
                _root?.PresentSystemMessage($"PARTY: {error}");
        }

        private void PresentPendingInvite(FriendsStateMessage state)
        {
            if (_root?.Windows == null || !HasAuthoredBindings) return;
            if (state.pendingInviterCharacterId <= 0)
            {
                _root.Windows.Close(ClientUIPanelId.PopupFriendInvite);
                return;
            }

            friendInviteText.text = string.IsNullOrWhiteSpace(state.pendingInviterName)
                ? $"Character {state.pendingInviterCharacterId} wants to add you as a friend."
                : $"{state.pendingInviterName} wants to add you as a friend.";
            friendInviteAcceptButton.onClick.RemoveAllListeners();
            friendInviteDeclineButton.onClick.RemoveAllListeners();
            long inviterId = state.pendingInviterCharacterId;
            friendInviteAcceptButton.onClick.AddListener(() => RespondToInviteAsync(true, inviterId).Forget());
            friendInviteDeclineButton.onClick.AddListener(() => RespondToInviteAsync(false, inviterId).Forget());
            _root.Windows.Open(ClientUIPanelId.PopupFriendInvite);
        }

        private void PresentPartyInvite(PartyStateMessage state)
        {
            CancelInvoke(nameof(CloseExpiredPartyInvite));
            if (_root?.Windows == null || !HasAuthoredBindings)
                return;

            if (state.pendingInviterCharacterId <= 0 || state.pendingInviteSecondsRemaining == 0)
            {
                _root.Windows.Close(ClientUIPanelId.PopupPartyInvite);
                return;
            }

            partyInviteText.text = string.IsNullOrWhiteSpace(state.pendingInviterName)
                ? $"Character {state.pendingInviterCharacterId} invited you to a party."
                : $"{state.pendingInviterName} invited you to a party.";
            partyInviteAcceptButton.onClick.RemoveAllListeners();
            partyInviteDeclineButton.onClick.RemoveAllListeners();
            partyInviteAcceptButton.onClick.AddListener(() => RespondToPartyInvite(true));
            partyInviteDeclineButton.onClick.AddListener(() => RespondToPartyInvite(false));
            _root.Windows.Open(ClientUIPanelId.PopupPartyInvite);
            Invoke(nameof(CloseExpiredPartyInvite), state.pendingInviteSecondsRemaining);
        }

        private void RespondToPartyInvite(bool accept)
        {
            if (_manager == null)
                return;
            if (!_manager.TrySubmitPartyCommand(accept ? "accept" : "decline", out string error))
            {
                _root?.PresentSystemMessage($"PARTY: {error}");
                return;
            }

            // The authoritative PartyState push will reconcile membership/invite state.
            // Closing immediately avoids leaving an already-answered modal on screen.
            _root?.Windows?.Close(ClientUIPanelId.PopupPartyInvite);
        }

        private void CloseExpiredPartyInvite()
        {
            _root?.Windows?.Close(ClientUIPanelId.PopupPartyInvite);
        }

        private async UniTaskVoid RespondToInviteAsync(bool accept, long inviterId)
        {
            if (_manager == null || inviterId <= 0) return;
            SocialEconomyMutationResponseMessage result = accept
                ? await _manager.RequestAcceptFriendAsync(inviterId)
                : await _manager.RequestDeclineFriendAsync(inviterId);
            await UniTask.SwitchToMainThread();
            if (!result.success)
            {
                _root?.PresentSystemMessage($"FRIENDS: {result.error}");
                return;
            }
            _root?.PresentSystemMessage(accept ? "FRIENDS: Friend request accepted." : "FRIENDS: Friend request declined.");
            _root?.Windows?.Close(ClientUIPanelId.PopupFriendInvite);
        }
    }
}
