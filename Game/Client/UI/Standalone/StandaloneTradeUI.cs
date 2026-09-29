using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.SocialEconomy;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Single-panel Standalone presentation for the existing authoritative player trade service.
    /// The same TradeWindow renders both invitation and active-trade states. It consumes the
    /// existing TradeState cache/event and existing mutation requests only; no polling loop,
    /// second trade authority, or additional wire contract lives here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneTradeUI : MonoBehaviour
    {
        [Header("Single Trade Window")]
        [SerializeField] private GameObject tradeWindowRoot;
        [SerializeField] private GameObject activeStateRoot;
        [SerializeField] private GameObject requestStateRoot;
        [SerializeField] private Text requestMessageText;
        [SerializeField] private Button acceptButton;
        [SerializeField] private Button declineButton;

        [Header("Active Trade")]
        [SerializeField] private Text partnerText;
        [SerializeField] private Text statusText;
        [SerializeField] private Text ownStateText;
        [SerializeField] private Text partnerStateText;
        [SerializeField] private Transform inventoryContent;
        [SerializeField] private ClientSocialEconomyRowView inventoryRowTemplate;
        [SerializeField] private Transform ownOfferContent;
        [SerializeField] private ClientSocialEconomyRowView ownOfferRowTemplate;
        [SerializeField] private Transform partnerOfferContent;
        [SerializeField] private ClientSocialEconomyRowView partnerOfferRowTemplate;
        [SerializeField] private Button lockButton;
        [SerializeField] private Text lockButtonLabel;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;

        private readonly List<ClientSocialEconomyRowView> _inventoryRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _ownOfferRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _partnerOfferRows = new List<ClientSocialEconomyRowView>();

        private PlayerEntityGameManager _manager;
        private bool _subscribed;
        private bool _mutationBusy;

        public bool IsOpen => tradeWindowRoot != null && tradeWindowRoot.activeInHierarchy;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            inventoryRowTemplate?.gameObject.SetActive(false);
            ownOfferRowTemplate?.gameObject.SetActive(false);
            partnerOfferRowTemplate?.gameObject.SetActive(false);

            // Do not deactivate tradeWindowRoot here. The authored TradeWindow starts
            // inactive and the always-active StandaloneClientUIRoot owns state-driven
            // visibility. When ApplyAuthoritativeState activates this GameObject for the
            // first authoritative TradeState, Awake runs synchronously; deactivating the
            // same root from Awake immediately cancels that first presentation.
            lockButton?.onClick.AddListener(OnLockClicked);
            confirmButton?.onClick.AddListener(OnConfirmClicked);
            cancelButton?.onClick.AddListener(OnCancelClicked);
            acceptButton?.onClick.AddListener(OnAcceptClicked);
            declineButton?.onClick.AddListener(OnDeclineClicked);
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindManager();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            lockButton?.onClick.RemoveListener(OnLockClicked);
            confirmButton?.onClick.RemoveListener(OnConfirmClicked);
            cancelButton?.onClick.RemoveListener(OnCancelClicked);
            acceptButton?.onClick.RemoveListener(OnAcceptClicked);
            declineButton?.onClick.RemoveListener(OnDeclineClicked);
            UnbindManager();
#endif
        }

        private void BindManager()
        {
            PlayerEntityGameManager resolved = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == resolved && _subscribed)
                return;

            UnbindManager();
            _manager = resolved;
            if (_manager == null)
            {
                SetStatus("Game server connection is unavailable.");
                return;
            }

            _manager.TradeStateReceived += OnTradeStateReceived;
            _manager.PlayerItemsChangedReceived += OnPlayerItemsChanged;
            _manager.ClientTransportStopped += OnTransportStopped;
            _subscribed = true;

            ApplyState(_manager.LatestTrade);
        }

        private void UnbindManager()
        {
            if (_subscribed && _manager != null)
            {
                _manager.TradeStateReceived -= OnTradeStateReceived;
                _manager.PlayerItemsChangedReceived -= OnPlayerItemsChanged;
                _manager.ClientTransportStopped -= OnTransportStopped;
            }
            _subscribed = false;
            _manager = null;
        }

        private void OnTradeStateReceived(TradeStateMessage state) => ApplyAuthoritativeState(state);

        /// <summary>
        /// Applies already-received authoritative trade state to the authored TradeWindow.
        /// This may be called while the TradeWindow GameObject is inactive; activating the
        /// authored window is presentation only and does not create/request trade state.
        /// </summary>
        public void ApplyAuthoritativeState(TradeStateMessage state) => ApplyState(state);

        private void OnPlayerItemsChanged(PlayerItemsResponseMessage _)
        {
            if (_manager != null && IsOpen && _manager.LatestTrade.phase == 2)
                RenderActiveState(_manager.LatestTrade);
        }

        private void OnTransportStopped() => HideTrade();

        private void ApplyState(TradeStateMessage state)
        {
            if (state.sessionId == 0)
            {
                HideTrade();
                return;
            }

            StandaloneClientUIRoot.Instance?.PrepareForTradeModal();
            if (tradeWindowRoot != null && !tradeWindowRoot.activeSelf)
                tradeWindowRoot.SetActive(true);

            bool invitation = state.phase == 1;
            if (requestStateRoot != null)
                requestStateRoot.SetActive(invitation);
            if (activeStateRoot != null)
                activeStateRoot.SetActive(!invitation);

            if (invitation)
            {
                string partner = PartnerName(state);
                if (requestMessageText != null)
                    requestMessageText.text = $"{partner} wants to trade with you.";
                RefreshActionButtons(state);
                return;
            }

            RenderActiveState(state);
            if (state.phase == 2)
                EnsurePlayerItemsAsync().Forget();
        }

        private void HideTrade()
        {
            if (tradeWindowRoot != null)
                tradeWindowRoot.SetActive(false);
            ClearRows(_inventoryRows);
            ClearRows(_ownOfferRows);
            ClearRows(_partnerOfferRows);
            _mutationBusy = false;
        }

        private void RenderActiveState(TradeStateMessage state)
        {
            if (partnerText != null)
                partnerText.text = $"Trading with {PartnerName(state)}";

            SetStatus(TradeStatusText(state));

            if (ownStateText != null)
                ownStateText.text = $"YOU: {(state.ownLocked ? "LOCKED" : "EDITING")}{(state.ownConfirmed ? " / CONFIRMED" : string.Empty)}";
            if (partnerStateText != null)
                partnerStateText.text = $"PARTNER: {(state.partnerLocked ? "LOCKED" : "EDITING")}{(state.partnerConfirmed ? " / CONFIRMED" : string.Empty)}";

            RenderInventory(state);
            RenderOffers(state);
            RefreshActionButtons(state);
        }

        private void RenderInventory(TradeStateMessage state)
        {
            ClearRows(_inventoryRows);
            if (_manager == null || inventoryContent == null || inventoryRowTemplate == null)
                return;

            PlayerItemWire[] inventory = _manager.LatestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            bool canEdit = state.sessionId != 0 && state.phase == 2 && !state.ownLocked && !_mutationBusy;
            for (int i = 0; i < inventory.Length; ++i)
            {
                PlayerItemWire item = inventory[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;

                int slot = item.inventorySlot;
                int quantity = item.quantity;
                ClientSocialEconomyRowView row = CreateRow(inventoryContent, inventoryRowTemplate, _inventoryRows);
                if (row == null)
                    continue;
                row.Configure(
                    $"{ItemName(item)} x{quantity}",
                    $"Inventory slot {slot}",
                    canEdit ? "OFFER 1" : null,
                    canEdit ? (Action)(() => OfferAsync(slot, 1).Forget()) : null,
                    canEdit && quantity > 1 ? "OFFER ALL" : null,
                    canEdit && quantity > 1 ? (Action)(() => OfferAsync(slot, quantity).Forget()) : null);
            }
        }

        private void RenderOffers(TradeStateMessage state)
        {
            ClearRows(_ownOfferRows);
            ClearRows(_partnerOfferRows);

            bool canEdit = state.sessionId != 0 && state.phase == 2 && !state.ownLocked && !_mutationBusy;
            TradeOfferWire[] own = state.ownOffers ?? Array.Empty<TradeOfferWire>();
            for (int i = 0; i < own.Length; ++i)
            {
                TradeOfferWire offer = own[i];
                int slot = offer.sourceSlot;
                ClientSocialEconomyRowView row = CreateRow(ownOfferContent, ownOfferRowTemplate, _ownOfferRows);
                if (row == null)
                    continue;
                row.Configure(
                    $"{ItemName(offer.item)} x{offer.quantity}",
                    $"Inventory slot {slot}",
                    canEdit ? "REMOVE" : null,
                    canEdit ? (Action)(() => RemoveOfferAsync(slot).Forget()) : null);
            }

            TradeOfferWire[] partner = state.partnerOffers ?? Array.Empty<TradeOfferWire>();
            for (int i = 0; i < partner.Length; ++i)
            {
                TradeOfferWire offer = partner[i];
                ClientSocialEconomyRowView row = CreateRow(partnerOfferContent, partnerOfferRowTemplate, _partnerOfferRows);
                if (row != null)
                    row.Configure($"{ItemName(offer.item)} x{offer.quantity}", "Partner offer", null, null);
            }
        }

        private void RefreshActionButtons(TradeStateMessage state)
        {
            bool active = state.sessionId != 0 && state.phase == 2;
            if (lockButtonLabel != null)
                lockButtonLabel.text = state.ownLocked ? "UNLOCK" : "LOCK OFFER";
            if (lockButton != null)
                lockButton.interactable = active && !_mutationBusy;
            if (confirmButton != null)
                confirmButton.interactable = active && state.ownLocked && state.partnerLocked && !state.ownConfirmed && !_mutationBusy;
            if (cancelButton != null)
                cancelButton.interactable = state.sessionId != 0 && state.phase != 3 && !_mutationBusy;
            if (acceptButton != null)
                acceptButton.interactable = state.sessionId != 0 && state.phase == 1 && !_mutationBusy;
            if (declineButton != null)
                declineButton.interactable = state.sessionId != 0 && state.phase == 1 && !_mutationBusy;
        }

        private void OnAcceptClicked() => InviteResponseAsync(true).Forget();
        private void OnDeclineClicked() => InviteResponseAsync(false).Forget();
        private void OnLockClicked() => LockAsync().Forget();
        private void OnConfirmClicked() => ConfirmAsync().Forget();
        private void OnCancelClicked() => CancelAsync().Forget();

        private async UniTaskVoid InviteResponseAsync(bool accept)
        {
            if (!TryBeginMutation()) return;
            SetStatus(accept ? "Accepting trade request..." : "Declining trade request...");
            SocialEconomyMutationResponseMessage result = accept
                ? await _manager.RequestAcceptTradeAsync()
                : await _manager.RequestDeclineTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation(result, accept ? "Trade request accepted." : "Trade request declined.");
        }

        private async UniTaskVoid LockAsync()
        {
            if (!TryBeginMutation()) return;
            bool unlock = _manager.LatestTrade.ownLocked;
            SetStatus(unlock ? "Unlocking offer..." : "Locking offer...");
            SocialEconomyMutationResponseMessage result = unlock
                ? await _manager.RequestUnlockTradeAsync()
                : await _manager.RequestLockTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation(result, unlock ? "Offer unlocked." : "Offer locked.");
        }

        private async UniTaskVoid ConfirmAsync()
        {
            if (!TryBeginMutation()) return;
            SetStatus("Confirming trade...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestConfirmTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation(result, "Trade confirmation submitted.");
        }

        private async UniTaskVoid CancelAsync()
        {
            if (!TryBeginMutation()) return;
            SetStatus("Cancelling trade...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestCancelTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation(result, "Trade cancellation submitted.");
        }

        private async UniTaskVoid OfferAsync(int slot, int quantity)
        {
            if (!TryBeginMutation()) return;
            SetStatus("Updating your offer...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestOfferTradeItemAsync(slot, quantity);
            await UniTask.SwitchToMainThread();
            EndMutation(result, "Offer updated.");
        }

        private async UniTaskVoid RemoveOfferAsync(int slot)
        {
            if (!TryBeginMutation()) return;
            SetStatus("Removing offered item...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestRemoveTradeOfferAsync(slot);
            await UniTask.SwitchToMainThread();
            EndMutation(result, "Offer updated.");
        }

        private bool TryBeginMutation()
        {
            if (_mutationBusy)
                return false;
            if (_manager == null)
            {
                BindManager();
                if (_manager == null)
                    return false;
            }
            _mutationBusy = true;
            RefreshActionButtons(_manager.LatestTrade);
            return true;
        }

        private void EndMutation(SocialEconomyMutationResponseMessage result, string successText)
        {
            _mutationBusy = false;
            SetStatus(result.success ? successText : ErrorOrFallback(result.error));
            if (_manager != null)
                RefreshActionButtons(_manager.LatestTrade);
        }

        private async UniTaskVoid EnsurePlayerItemsAsync()
        {
            if (_manager == null || _manager.HasPlayerItemsCache)
                return;
            await _manager.RequestPlayerItemsAsync(false);
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

        private static string PartnerName(TradeStateMessage state) =>
            string.IsNullOrWhiteSpace(state.partnerName) ? $"Character {state.partnerCharacterId}" : state.partnerName;

        private static string ItemName(PlayerItemWire item)
        {
            if (!string.IsNullOrWhiteSpace(item.displayName)) return item.displayName;
            if (!string.IsNullOrWhiteSpace(item.definitionId)) return item.definitionId;
            return item.itemDataId != 0 ? $"Item {item.itemDataId}" : "Item";
        }

        private static string TradeStatusText(TradeStateMessage state)
        {
            if (!string.IsNullOrWhiteSpace(state.detail))
                return state.detail;
            switch (state.phase)
            {
                case 2: return "Add items, lock both offers, then confirm.";
                case 3: return "Committing trade...";
                default: return $"Trade phase {state.phase}.";
            }
        }

        private static string ErrorOrFallback(string error) =>
            string.IsNullOrWhiteSpace(error) ? "Request was rejected." : error;

        private void SetStatus(string value)
        {
            if (statusText != null)
                statusText.text = value ?? string.Empty;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject authoredTradeWindowRoot,
            GameObject authoredActiveStateRoot,
            GameObject authoredRequestStateRoot,
            Text authoredRequestMessageText,
            Button authoredAcceptButton,
            Button authoredDeclineButton,
            Text authoredPartnerText,
            Text authoredStatusText,
            Text authoredOwnStateText,
            Text authoredPartnerStateText,
            Transform authoredInventoryContent,
            ClientSocialEconomyRowView authoredInventoryRowTemplate,
            Transform authoredOwnOfferContent,
            ClientSocialEconomyRowView authoredOwnOfferRowTemplate,
            Transform authoredPartnerOfferContent,
            ClientSocialEconomyRowView authoredPartnerOfferRowTemplate,
            Button authoredLockButton,
            Text authoredLockButtonLabel,
            Button authoredConfirmButton,
            Button authoredCancelButton)
        {
            tradeWindowRoot = authoredTradeWindowRoot;
            activeStateRoot = authoredActiveStateRoot;
            requestStateRoot = authoredRequestStateRoot;
            requestMessageText = authoredRequestMessageText;
            acceptButton = authoredAcceptButton;
            declineButton = authoredDeclineButton;
            partnerText = authoredPartnerText;
            statusText = authoredStatusText;
            ownStateText = authoredOwnStateText;
            partnerStateText = authoredPartnerStateText;
            inventoryContent = authoredInventoryContent;
            inventoryRowTemplate = authoredInventoryRowTemplate;
            ownOfferContent = authoredOwnOfferContent;
            ownOfferRowTemplate = authoredOwnOfferRowTemplate;
            partnerOfferContent = authoredPartnerOfferContent;
            partnerOfferRowTemplate = authoredPartnerOfferRowTemplate;
            lockButton = authoredLockButton;
            lockButtonLabel = authoredLockButtonLabel;
            confirmButton = authoredConfirmButton;
            cancelButton = authoredCancelButton;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
