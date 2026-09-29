using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Root;
using Game.Client.UI.Standalone;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.SocialEconomy;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0414

namespace Game.Client.UI.Social
{
    /// <summary>
    /// Canonical client binder for Trade and Storage authored panels. Friends are owned by
    /// Game.Client.UI.Social.ClientSocialEconomyUI so there is no duplicate Friends owner.
    /// This binder consumes existing caches/events and existing request methods only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientTradeStorageUI : MonoBehaviour
    {
        [Header("Framework")]
        [SerializeField] private ClientWindowManager windowManager;

        [Header("Trade Window")]
        [SerializeField] private GameObject tradeWindow;
        [SerializeField] private Text tradePartnerText;
        [SerializeField] private Text tradeStatusText;
        [SerializeField] private Button tradeRefreshButton;
        [SerializeField] private Transform tradeInventoryContent;
        [SerializeField] private ClientSocialEconomyRowView tradeInventoryRowTemplate;
        [SerializeField] private Transform tradeOwnOfferContent;
        [SerializeField] private ClientSocialEconomyRowView tradeOwnOfferRowTemplate;
        [SerializeField] private Transform tradePartnerOfferContent;
        [SerializeField] private ClientSocialEconomyRowView tradePartnerOfferRowTemplate;
        [SerializeField] private Button tradeLockButton;
        [SerializeField] private Text tradeLockButtonLabel;
        [SerializeField] private Button tradeConfirmButton;
        [SerializeField] private Button tradeCancelButton;

        [Header("Trade Request Popup")]
        [SerializeField] private GameObject tradeRequestPopup;
        [SerializeField] private Text tradeRequestMessageText;
        [SerializeField] private Button tradeRequestAcceptButton;
        [SerializeField] private Button tradeRequestDeclineButton;

        [Header("Storage Window")]
        [SerializeField] private GameObject storageWindow;
        [SerializeField] private Text storageStatusText;
        [SerializeField] private Text storageCapacityText;
        [SerializeField] private Button storageRefreshButton;
        [SerializeField] private Transform storageInventoryContent;
        [SerializeField] private ClientSocialEconomyRowView storageInventoryRowTemplate;
        [SerializeField] private Transform storageContent;
        [SerializeField] private ClientSocialEconomyRowView storageRowTemplate;

        private readonly List<ClientSocialEconomyRowView> _tradeInventoryRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _tradeOwnOfferRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _tradePartnerOfferRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _storageInventoryRows = new List<ClientSocialEconomyRowView>();
        private readonly List<ClientSocialEconomyRowView> _storageRows = new List<ClientSocialEconomyRowView>();

        private PlayerEntityGameManager _manager;
        private ClientUIRoot _root;
        private bool _subscribed;
        private bool _mutationBusy;
        private StandaloneDragGhost _dragGhost;

        public bool HasAuthoredBindings =>
            windowManager != null &&
            tradeWindow != null && tradePartnerText != null && tradeStatusText != null && tradeRefreshButton != null && tradeInventoryContent != null && tradeInventoryRowTemplate != null &&
            tradeOwnOfferContent != null && tradeOwnOfferRowTemplate != null && tradePartnerOfferContent != null && tradePartnerOfferRowTemplate != null &&
            tradeLockButton != null && tradeLockButtonLabel != null && tradeConfirmButton != null && tradeCancelButton != null &&
            tradeRequestPopup != null && tradeRequestMessageText != null && tradeRequestAcceptButton != null && tradeRequestDeclineButton != null &&
            storageWindow != null && storageStatusText != null && storageCapacityText != null && storageRefreshButton != null &&
            storageInventoryContent != null && storageInventoryRowTemplate != null && storageContent != null && storageRowTemplate != null;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            _root = GetComponent<ClientUIRoot>();
            if (!HasAuthoredBindings)
                Debug.LogError("[TradeStorageUI] ClientUIRoot is missing authored Trade/Storage bindings. Restore the authored Trade/Storage bindings on ClientUIRoot.prefab.", this);

            HideTemplates();
            tradeRefreshButton?.onClick.AddListener(OnTradeRefreshClicked);
            tradeLockButton?.onClick.AddListener(OnTradeLockClicked);
            tradeConfirmButton?.onClick.AddListener(OnTradeConfirmClicked);
            tradeCancelButton?.onClick.AddListener(OnTradeCancelClicked);
            tradeRequestAcceptButton?.onClick.AddListener(OnTradeAcceptClicked);
            tradeRequestDeclineButton?.onClick.AddListener(OnTradeDeclineClicked);
            storageRefreshButton?.onClick.AddListener(OnStorageRefreshClicked);
            if (windowManager != null)
                windowManager.PanelVisibilityChanged += OnPanelVisibilityChanged;
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
            tradeRefreshButton?.onClick.RemoveListener(OnTradeRefreshClicked);
            tradeLockButton?.onClick.RemoveListener(OnTradeLockClicked);
            tradeConfirmButton?.onClick.RemoveListener(OnTradeConfirmClicked);
            tradeCancelButton?.onClick.RemoveListener(OnTradeCancelClicked);
            tradeRequestAcceptButton?.onClick.RemoveListener(OnTradeAcceptClicked);
            tradeRequestDeclineButton?.onClick.RemoveListener(OnTradeDeclineClicked);
            storageRefreshButton?.onClick.RemoveListener(OnStorageRefreshClicked);
            if (windowManager != null)
                windowManager.PanelVisibilityChanged -= OnPanelVisibilityChanged;
            UnbindManager();
#endif
        }

        public void BindManager(PlayerEntityGameManager manager = null)
        {
#if !UNITY_SERVER
            PlayerEntityGameManager resolved = manager != null ? manager : FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == resolved && _subscribed)
                return;

            UnbindManager();
            _manager = resolved;
            if (_manager == null)
            {
                SetTradeStatus("Game server connection is unavailable.");
                SetStorageStatus("Game server connection is unavailable.");
                return;
            }

            _manager.TradeStateReceived += OnTradeStateReceived;
            _manager.StorageStateReceived += OnStorageStateReceived;
            _manager.PlayerItemsChangedReceived += OnPlayerItemsChanged;
            _subscribed = true;

            RenderTrade(_manager.LatestTrade);
            RenderStorage(_manager.LatestStorage);
            SyncTradeWindows(_manager.LatestTrade);
#endif
        }

        public void UnbindManager()
        {
#if !UNITY_SERVER
            if (_subscribed && _manager != null)
            {
                _manager.TradeStateReceived -= OnTradeStateReceived;
                _manager.StorageStateReceived -= OnStorageStateReceived;
                _manager.PlayerItemsChangedReceived -= OnPlayerItemsChanged;
            }
            _subscribed = false;
            _manager = null;
#endif
        }

        private void OnPanelVisibilityChanged(ClientUIPanelId id, bool visible)
        {
            if (!visible)
                return;

            switch (id)
            {
                case ClientUIPanelId.TradeWindow:
                    RenderTrade(_manager != null ? _manager.LatestTrade : default);
                    RefreshTradeAsync(false).Forget();
                    EnsurePlayerItemsAsync().Forget();
                    break;
                case ClientUIPanelId.StorageWindow:
                    RenderStorage(_manager != null ? _manager.LatestStorage : default);
                    RefreshStorageAsync(false).Forget();
                    EnsurePlayerItemsAsync().Forget();
                    break;
            }
        }

        private void OnTradeStateReceived(TradeStateMessage state)
        {
            RenderTrade(state);
            SyncTradeWindows(state);
        }

        private void OnStorageStateReceived(StorageStateMessage state)
        {
            RenderStorage(state);
            // State updates refresh the cache/view only. They never open a window on the
            // player's behalf. Storage visibility is owned by explicit UI/interaction actions.
        }

        private void OnPlayerItemsChanged(PlayerItemsResponseMessage _)
        {
            if (_manager == null)
                return;
            if (windowManager != null && windowManager.IsOpen(ClientUIPanelId.TradeWindow))
                RenderTrade(_manager.LatestTrade);
            if (windowManager != null && windowManager.IsOpen(ClientUIPanelId.StorageWindow))
                RenderStorage(_manager.LatestStorage);
        }

        private void OnTradeRefreshClicked() => RefreshTradeAsync(true).Forget();
        private void OnStorageRefreshClicked() => RefreshStorageAsync(true).Forget();
        private void OnTradeAcceptClicked() => TradeInviteMutationAsync(true).Forget();
        private void OnTradeDeclineClicked() => TradeInviteMutationAsync(false).Forget();
        private void OnTradeLockClicked() => TradeLockMutationAsync().Forget();
        private void OnTradeConfirmClicked() => TradeSimpleMutationAsync(TradeMutation.Confirm).Forget();
        private void OnTradeCancelClicked() => TradeSimpleMutationAsync(TradeMutation.Cancel).Forget();

        private async UniTask RefreshTradeAsync(bool force)
        {
            if (_manager == null)
                BindManager();
            if (_manager == null)
                return;
            SetTradeStatus(force ? "Refreshing trade state..." : "Loading trade state...");
            TradeStateMessage state = await _manager.RequestTradeStateAsync(force);
            await UniTask.SwitchToMainThread();
            RenderTrade(state);
            SyncTradeWindows(state);
        }

        private async UniTask RefreshStorageAsync(bool force)
        {
            if (_manager == null)
                BindManager();
            if (_manager == null)
                return;
            SetStorageStatus(force ? "Refreshing storage..." : "Loading storage...");
            StorageStateMessage state = await _manager.RequestStorageAsync(force);
            await UniTask.SwitchToMainThread();
            RenderStorage(state);
        }

        private async UniTask EnsurePlayerItemsAsync()
        {
            if (_manager == null || _manager.LatestPlayerItems.inventoryCapacity > 0)
                return;
            await _manager.RequestPlayerItemsAsync(false);
        }

        private async UniTask TradeInviteMutationAsync(bool accept)
        {
            if (!TryBeginMutation()) return;
            SetTradeStatus(accept ? "Accepting trade request..." : "Declining trade request...");
            SocialEconomyMutationResponseMessage result = accept
                ? await _manager.RequestAcceptTradeAsync()
                : await _manager.RequestDeclineTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentTradeFeedback(result.success ? "Trade response submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private async UniTask TradeLockMutationAsync()
        {
            if (!TryBeginMutation()) return;
            bool unlock = _manager.LatestTrade.ownLocked;
            SetTradeStatus(unlock ? "Unlocking trade offer..." : "Locking trade offer...");
            SocialEconomyMutationResponseMessage result = unlock
                ? await _manager.RequestUnlockTradeAsync()
                : await _manager.RequestLockTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentTradeFeedback(result.success ? "Trade lock change submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private enum TradeMutation { Confirm, Cancel }

        private async UniTask TradeSimpleMutationAsync(TradeMutation mutation)
        {
            if (!TryBeginMutation()) return;
            SetTradeStatus(mutation == TradeMutation.Confirm ? "Confirming trade..." : "Cancelling trade...");
            SocialEconomyMutationResponseMessage result = mutation == TradeMutation.Confirm
                ? await _manager.RequestConfirmTradeAsync()
                : await _manager.RequestCancelTradeAsync();
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentTradeFeedback(result.success ? "Trade action submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private async UniTask OfferTradeItemAsync(int inventorySlot, int quantity)
        {
            if (!TryBeginMutation()) return;
            SetTradeStatus("Updating trade offer...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestOfferTradeItemAsync(inventorySlot, quantity);
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentTradeFeedback(result.success ? "Offer submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private async UniTask RemoveTradeOfferAsync(int inventorySlot)
        {
            if (!TryBeginMutation()) return;
            SetTradeStatus("Removing trade offer...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestRemoveTradeOfferAsync(inventorySlot);
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentTradeFeedback(result.success ? "Offer removal submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private async UniTask StorageTransferAsync(bool deposit, int sourceSlot, int quantity)
        {
            if (!TryBeginMutation()) return;
            SetStorageStatus(deposit ? "Depositing item..." : "Withdrawing item...");
            SocialEconomyMutationResponseMessage result = deposit
                ? await _manager.RequestDepositToStorageAsync(sourceSlot, quantity)
                : await _manager.RequestWithdrawFromStorageAsync(sourceSlot, quantity);
            await UniTask.SwitchToMainThread();
            EndMutation();
            PresentStorageFeedback(result.success ? "Storage transfer submitted. Waiting for authoritative state..." : ErrorOrFallback(result.error));
        }

        private bool TryBeginMutation()
        {
            if (_mutationBusy || _manager == null)
                return false;
            _mutationBusy = true;
            RefreshActionButtons();
            return true;
        }

        private void EndMutation()
        {
            _mutationBusy = false;
            if (_manager != null)
            {
                    RenderTrade(_manager.LatestTrade);
                RenderStorage(_manager.LatestStorage);
            }
        }

        private void RenderTrade(TradeStateMessage state)
        {
            if (tradePartnerText != null)
                tradePartnerText.text = state.sessionId == 0
                    ? "No active trade"
                    : $"Partner: {(string.IsNullOrWhiteSpace(state.partnerName) ? $"Character {state.partnerCharacterId}" : state.partnerName)}";

            string phase = TradePhaseText(state.phase, state.sessionId);
            string detail = string.IsNullOrWhiteSpace(state.detail) ? string.Empty : $"  {state.detail}";
            SetTradeStatus($"{phase}{detail}");

            RenderTradeInventory(state);
            RenderTradeOffers(state);
            RefreshActionButtons();
        }

        private void RenderTradeInventory(TradeStateMessage state)
        {
            ClearRows(_tradeInventoryRows);
            if (tradeInventoryContent == null || tradeInventoryRowTemplate == null || _manager == null)
                return;

            PlayerItemWire[] inventory = _manager.LatestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            bool canEdit = state.sessionId != 0 && state.phase == 2 && !state.ownLocked && !_mutationBusy;
            for (int i = 0; i < inventory.Length; ++i)
            {
                PlayerItemWire item = inventory[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;
                ClientSocialEconomyRowView row = CreateRow(tradeInventoryContent, tradeInventoryRowTemplate, _tradeInventoryRows);
                int slot = item.inventorySlot;
                int quantity = item.quantity;
                row.Configure(
                    $"{ItemName(item)} x{quantity}",
                    $"Inventory slot {slot}",
                    canEdit ? "OFFER 1" : null,
                    canEdit ? (Action)(() => OfferTradeItemAsync(slot, 1).Forget()) : null,
                    canEdit && quantity > 1 ? "OFFER ALL" : null,
                    canEdit && quantity > 1 ? (Action)(() => OfferTradeItemAsync(slot, quantity).Forget()) : null);
            }
        }

        private void RenderTradeOffers(TradeStateMessage state)
        {
            ClearRows(_tradeOwnOfferRows);
            ClearRows(_tradePartnerOfferRows);
            TradeOfferWire[] own = state.ownOffers ?? Array.Empty<TradeOfferWire>();
            TradeOfferWire[] partner = state.partnerOffers ?? Array.Empty<TradeOfferWire>();
            bool canEdit = state.sessionId != 0 && state.phase == 2 && !state.ownLocked && !_mutationBusy;

            for (int i = 0; i < own.Length; ++i)
            {
                TradeOfferWire offer = own[i];
                ClientSocialEconomyRowView row = CreateRow(tradeOwnOfferContent, tradeOwnOfferRowTemplate, _tradeOwnOfferRows);
                int slot = offer.sourceSlot;
                row.Configure(
                    $"{ItemName(offer.item)} x{offer.quantity}",
                    $"Inventory slot {slot}",
                    canEdit ? "REMOVE" : null,
                    canEdit ? (Action)(() => RemoveTradeOfferAsync(slot).Forget()) : null);
            }

            for (int i = 0; i < partner.Length; ++i)
            {
                TradeOfferWire offer = partner[i];
                ClientSocialEconomyRowView row = CreateRow(tradePartnerOfferContent, tradePartnerOfferRowTemplate, _tradePartnerOfferRows);
                row.Configure($"{ItemName(offer.item)} x{offer.quantity}", "Partner offer", null, null);
            }
        }

        private void SyncTradeWindows(TradeStateMessage state)
        {
            if (windowManager == null)
                return;

            if (state.sessionId == 0)
            {
                if (windowManager.IsOpen(ClientUIPanelId.PopupTradeRequest))
                    windowManager.Close(ClientUIPanelId.PopupTradeRequest);
                if (windowManager.IsOpen(ClientUIPanelId.TradeWindow))
                    windowManager.Close(ClientUIPanelId.TradeWindow);
                return;
            }

            if (state.phase == 1)
            {
                string partner = string.IsNullOrWhiteSpace(state.partnerName)
                    ? $"Character {state.partnerCharacterId}"
                    : state.partnerName;
                if (tradeRequestMessageText != null)
                    tradeRequestMessageText.text = $"{partner} wants to trade with you.";
                if (tradeRequestAcceptButton != null) tradeRequestAcceptButton.interactable = !_mutationBusy;
                if (tradeRequestDeclineButton != null) tradeRequestDeclineButton.interactable = !_mutationBusy;
                if (!windowManager.IsOpen(ClientUIPanelId.PopupTradeRequest))
                    windowManager.Open(ClientUIPanelId.PopupTradeRequest);
                return;
            }

            if (windowManager.IsOpen(ClientUIPanelId.PopupTradeRequest))
                windowManager.Close(ClientUIPanelId.PopupTradeRequest);
            if (!windowManager.IsOpen(ClientUIPanelId.TradeWindow))
                windowManager.Open(ClientUIPanelId.TradeWindow);
            EnsurePlayerItemsAsync().Forget();
        }

        private void RenderStorage(StorageStateMessage state)
        {
            PlayerItemWire[] stored = state.items ?? Array.Empty<PlayerItemWire>();
            SetStorageStatus(state.capacity > 0
                ? (string.IsNullOrWhiteSpace(state.detail) ? "Storage ready." : state.detail)
                : "Storage is not currently authorized/open.");
            if (storageCapacityText != null)
                storageCapacityText.text = state.capacity > 0
                    ? $"Capacity: {stored.Length}/{state.capacity}  Revision: {state.revision}"
                    : "Capacity unavailable";

            RenderStorageInventory(state);
            ClearRows(_storageRows);
            bool canWithdraw = state.capacity > 0 && !_mutationBusy;
            for (int i = 0; i < stored.Length; ++i)
            {
                PlayerItemWire item = stored[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;
                ClientSocialEconomyRowView row = CreateRow(storageContent, storageRowTemplate, _storageRows);
                int slot = item.inventorySlot;
                int quantity = item.quantity;
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
            RefreshActionButtons();
        }

        private void RenderStorageInventory(StorageStateMessage state)
        {
            ClearRows(_storageInventoryRows);
            if (storageInventoryContent == null || storageInventoryRowTemplate == null || _manager == null)
                return;

            PlayerItemWire[] inventory = _manager.LatestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            bool canDeposit = state.capacity > 0 && !_mutationBusy;
            for (int i = 0; i < inventory.Length; ++i)
            {
                PlayerItemWire item = inventory[i];
                if (item.itemInstanceId <= 0 || item.quantity <= 0)
                    continue;
                ClientSocialEconomyRowView row = CreateRow(storageInventoryContent, storageInventoryRowTemplate, _storageInventoryRows);
                int slot = item.inventorySlot;
                int quantity = item.quantity;
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
        }

        private void BeginStorageInventoryDrag(int inventorySlot, PlayerItemWire item)
        {
            if (_mutationBusy)
                return;
            StandaloneItemDragContext.BeginInventory(inventorySlot, item);
            GetDragGhost()?.Show(ClientItemIconResolver.Resolve(item));
        }

        private void BeginStoredItemDrag(int storageSlot, PlayerItemWire item)
        {
            if (_mutationBusy)
                return;
            StandaloneItemDragContext.BeginStorage(storageSlot, item);
            GetDragGhost()?.Show(ClientItemIconResolver.Resolve(item));
        }

        private void DropOnStorageInventoryRow()
        {
            if (_mutationBusy || StandaloneItemDragContext.Kind != StandaloneItemDragContext.SourceKind.Storage)
                return;
            int slot = StandaloneItemDragContext.SlotIndex;
            int quantity = Math.Max(1, StandaloneItemDragContext.Item.quantity);
            EndStorageDrag();
            StorageTransferAsync(false, slot, quantity).Forget();
        }

        private void DropOnStoredItemRow()
        {
            if (_mutationBusy || StandaloneItemDragContext.Kind != StandaloneItemDragContext.SourceKind.Inventory)
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

        private void RefreshActionButtons()
        {
            if (_manager == null)
                return;

            TradeStateMessage trade = _manager.LatestTrade;
            bool active = trade.sessionId != 0 && trade.phase == 2;
            if (tradeLockButtonLabel != null)
                tradeLockButtonLabel.text = trade.ownLocked ? "UNLOCK" : "LOCK";
            if (tradeLockButton != null)
                tradeLockButton.interactable = active && !_mutationBusy;
            if (tradeConfirmButton != null)
                tradeConfirmButton.interactable = active && trade.ownLocked && trade.partnerLocked && !trade.ownConfirmed && !_mutationBusy;
            if (tradeCancelButton != null)
                tradeCancelButton.interactable = trade.sessionId != 0 && trade.phase != 3 && !_mutationBusy;
            if (tradeRequestAcceptButton != null)
                tradeRequestAcceptButton.interactable = trade.sessionId != 0 && trade.phase == 1 && !_mutationBusy;
            if (tradeRequestDeclineButton != null)
                tradeRequestDeclineButton.interactable = trade.sessionId != 0 && trade.phase == 1 && !_mutationBusy;
        }

        private static ClientSocialEconomyRowView CreateRow(
            Transform content,
            ClientSocialEconomyRowView template,
            List<ClientSocialEconomyRowView> rows)
        {
            ClientSocialEconomyRowView row = Instantiate(template, content, false);
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
            tradeInventoryRowTemplate?.gameObject.SetActive(false);
            tradeOwnOfferRowTemplate?.gameObject.SetActive(false);
            tradePartnerOfferRowTemplate?.gameObject.SetActive(false);
            storageInventoryRowTemplate?.gameObject.SetActive(false);
            storageRowTemplate?.gameObject.SetActive(false);
        }

        private void SetTradeStatus(string value)
        {
            if (tradeStatusText != null) tradeStatusText.text = value ?? string.Empty;
        }

        private void SetStorageStatus(string value)
        {
            if (storageStatusText != null) storageStatusText.text = value ?? string.Empty;
        }

        private void PresentTradeFeedback(string value)
        {
            SetTradeStatus(value);
            _root?.PresentSystemMessage("TRADE: " + (value ?? string.Empty));
        }

        private void PresentStorageFeedback(string value)
        {
            SetStorageStatus(value);
            _root?.PresentSystemMessage("STORAGE: " + (value ?? string.Empty));
        }

        private static string ErrorOrFallback(string error) =>
            string.IsNullOrWhiteSpace(error) ? "Request was rejected." : error;

        private static string ItemName(PlayerItemWire item)
        {
            if (!string.IsNullOrWhiteSpace(item.displayName)) return item.displayName;
            if (!string.IsNullOrWhiteSpace(item.definitionId)) return item.definitionId;
            return item.itemDataId != 0 ? $"Item {item.itemDataId}" : "Item";
        }

        private static string TradePhaseText(byte phase, ulong sessionId)
        {
            if (sessionId == 0) return "No active trade.";
            switch (phase)
            {
                case 1: return "Trade request pending.";
                case 2: return "Trade active.";
                case 3: return "Trade committing.";
                default: return $"Trade phase {phase}.";
            }
        }

#if UNITY_EDITOR
        public void CaptureAuthoredReferencesForEditor()
        {
            windowManager = GetComponent<ClientWindowManager>();

            tradeWindow = FindPanel(ClientUIPanelId.TradeWindow);
            tradePartnerText = FindText(tradeWindow, "Body/SocialEconomyBinding/PartnerText");
            tradeStatusText = FindText(tradeWindow, "Body/SocialEconomyBinding/StatusText");
            tradeRefreshButton = FindButton(tradeWindow, "Body/SocialEconomyBinding/RefreshButton");
            tradeInventoryContent = FindTransform(tradeWindow, "Body/SocialEconomyBinding/InventoryList/Viewport/Content");
            tradeInventoryRowTemplate = FindRow(tradeWindow, "Body/SocialEconomyBinding/InventoryList/Viewport/Content/RowTemplate");
            tradeOwnOfferContent = FindTransform(tradeWindow, "Body/SocialEconomyBinding/OwnOfferList/Viewport/Content");
            tradeOwnOfferRowTemplate = FindRow(tradeWindow, "Body/SocialEconomyBinding/OwnOfferList/Viewport/Content/RowTemplate");
            tradePartnerOfferContent = FindTransform(tradeWindow, "Body/SocialEconomyBinding/PartnerOfferList/Viewport/Content");
            tradePartnerOfferRowTemplate = FindRow(tradeWindow, "Body/SocialEconomyBinding/PartnerOfferList/Viewport/Content/RowTemplate");
            tradeLockButton = FindButton(tradeWindow, "Body/SocialEconomyBinding/LockButton");
            tradeLockButtonLabel = tradeLockButton != null ? tradeLockButton.GetComponentInChildren<Text>(true) : null;
            tradeConfirmButton = FindButton(tradeWindow, "Body/SocialEconomyBinding/ConfirmButton");
            tradeCancelButton = FindButton(tradeWindow, "Body/SocialEconomyBinding/CancelButton");

            tradeRequestPopup = FindPanel(ClientUIPanelId.PopupTradeRequest);
            tradeRequestMessageText = FindText(tradeRequestPopup, "Body/SocialEconomyBinding/MessageText");
            tradeRequestAcceptButton = FindButton(tradeRequestPopup, "Body/SocialEconomyBinding/AcceptButton");
            tradeRequestDeclineButton = FindButton(tradeRequestPopup, "Body/SocialEconomyBinding/DeclineButton");

            storageWindow = FindPanel(ClientUIPanelId.StorageWindow);
            storageStatusText = FindText(storageWindow, "Body/SocialEconomyBinding/StatusText");
            storageCapacityText = FindText(storageWindow, "Body/SocialEconomyBinding/CapacityText");
            storageRefreshButton = FindButton(storageWindow, "Body/SocialEconomyBinding/RefreshButton");
            storageInventoryContent = FindTransform(storageWindow, "Body/SocialEconomyBinding/InventoryList/Viewport/Content");
            storageInventoryRowTemplate = FindRow(storageWindow, "Body/SocialEconomyBinding/InventoryList/Viewport/Content/RowTemplate");
            storageContent = FindTransform(storageWindow, "Body/SocialEconomyBinding/StorageList/Viewport/Content");
            storageRowTemplate = FindRow(storageWindow, "Body/SocialEconomyBinding/StorageList/Viewport/Content/RowTemplate");
        }

        private GameObject FindPanel(ClientUIPanelId id)
        {
            ClientUIPanelMarker[] markers = GetComponentsInChildren<ClientUIPanelMarker>(true);
            for (int i = 0; i < markers.Length; ++i)
                if (markers[i] != null && markers[i].PanelId == id)
                    return markers[i].gameObject;
            return null;
        }

        private static Transform FindTransform(GameObject root, string path) => root != null ? root.transform.Find(path) : null;
        private static Text FindText(GameObject root, string path) => FindTransform(root, path)?.GetComponent<Text>();
        private static Button FindButton(GameObject root, string path) => FindTransform(root, path)?.GetComponent<Button>();
        private static ClientSocialEconomyRowView FindRow(GameObject root, string path)
        {
            ClientSocialEconomyRowView row = FindTransform(root, path)?.GetComponent<ClientSocialEconomyRowView>();
            row?.CaptureAuthoredReferencesForEditor();
            return row;
        }
#endif
    }
}
