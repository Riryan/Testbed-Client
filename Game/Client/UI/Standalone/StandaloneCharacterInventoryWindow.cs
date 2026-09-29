using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.PlayerItems;
using Game.Shared.Protocol;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Combined Character + Inventory window backed by the existing owner-local PlayerEntity caches.
    /// Opening the window normally produces zero inventory network traffic. Existing authoritative
    /// item mutation requests are reused for use/equip/unequip/move and drag/drop.
    /// </summary>
    public sealed class StandaloneCharacterInventoryWindow : MonoBehaviour
    {
        [Header("Window")]
        [SerializeField] private GameObject windowRoot;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text characterNameText;
        [SerializeField] private Text levelText;
        [SerializeField] private Text summaryText;
        [SerializeField] private Text feedbackText;

        [Header("Inventory")]
        [SerializeField] private ScrollRect inventoryScroll;
        [SerializeField] private Transform inventoryContent;
        [SerializeField] private StandaloneInventorySlotView inventorySlotTemplate;

        // Legacy serialized references are intentionally retained so the original V1 authoring
        // utility continues to compile. V2.04 does not use paging: the first 30 slots fit without
        // scrolling and vertical scrolling is enabled only when capacity exceeds 30.
        [SerializeField] private Button previousPageButton;
        [SerializeField] private Button nextPageButton;
        [SerializeField] private Text pageText;
        [SerializeField, Min(5)] private int slotsPerPage = 30;

        [Header("Equipment")]
        [SerializeField] private Transform equipmentContent;
        [SerializeField] private StandaloneEquipmentSlotView equipmentSlotTemplate;

        [Header("Shared HUD")]
        [SerializeField] private StandaloneHudTooltipPanel tooltip;
        [SerializeField] private StandaloneDragGhost dragGhost;

        private readonly List<StandaloneInventorySlotView> _inventoryViews = new List<StandaloneInventorySlotView>();
        private readonly List<StandaloneEquipmentSlotView> _equipmentViews = new List<StandaloneEquipmentSlotView>();
        private PlayerEntityGameManager _manager;
        private PlayerEntityClient _owner;
        private PlayerItemsResponseMessage _snapshot;
        private ProgressionSnapshotMessage _progression;
        private int _selectedInventorySlot = -1;
        private int _dragInventorySlot = -1;
        private string _dragEquipmentSlotId = string.Empty;
        private bool _busy;
        private bool _initialRequestAttempted;

        public bool IsOpen => windowRoot != null && windowRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            closeButton?.onClick.AddListener(Close);
            if (inventorySlotTemplate != null) inventorySlotTemplate.gameObject.SetActive(false);
            if (equipmentSlotTemplate != null) equipmentSlotTemplate.gameObject.SetActive(false);
            if (previousPageButton != null) previousPageButton.gameObject.SetActive(false);
            if (nextPageButton != null) nextPageButton.gameObject.SetActive(false);
            if (pageText != null) pageText.gameObject.SetActive(false);
            windowRoot?.SetActive(false);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            closeButton?.onClick.RemoveListener(Close);
            UnbindManager();
            UnbindOwner();
#endif
        }

        public void Toggle()
        {
#if !UNITY_SERVER
            if (IsOpen) Close(); else Open();
#endif
        }

        public void Open()
        {
#if !UNITY_SERVER
            if (windowRoot == null)
                return;

            BindManager(FindFirstObjectByType<PlayerEntityGameManager>());
            BindOwner();
            windowRoot.SetActive(true);
            _selectedInventorySlot = -1;
            ClearDragSource();
            tooltip?.Hide();

            if (_manager == null || !_manager.IsClientConnected)
            {
                SetFeedback("Game server connection is unavailable.");
                RefreshHeader();
                return;
            }

            _snapshot = _manager.LatestPlayerItems;
            _progression = _manager.LatestProgression;
            RefreshHeader();
            RenderSnapshot();

            // Normal window-open path consumes the already hydrated local cache. The one request
            // below is recovery only when no inventory baseline exists at all.
            if (_snapshot.inventoryCapacity <= 0 && !_manager.PlayerItemsSnapshotRequestInFlight && !_initialRequestAttempted)
            {
                _initialRequestAttempted = true;
                SetFeedback("Waiting for authoritative inventory baseline...");
                RequestMissingBaselineAsync().Forget();
            }
            else
            {
                SetFeedback(string.Empty);
            }
#endif
        }

        public void Close()
        {
#if !UNITY_SERVER
            _selectedInventorySlot = -1;
            ClearDragSource();
            tooltip?.Hide();
            windowRoot?.SetActive(false);
#endif
        }

        private async UniTaskVoid RequestMissingBaselineAsync()
        {
            if (_manager == null || !_manager.IsClientConnected)
                return;
            PlayerItemsResponseMessage result = await _manager.RequestPlayerItemsAsync(false);
            await UniTask.SwitchToMainThread();
            if (result.inventoryCapacity > 0)
            {
                _snapshot = result;
                if (IsOpen)
                {
                    SetFeedback(string.Empty);
                    RenderSnapshot();
                }
            }
            else if (IsOpen)
            {
                SetFeedback(string.IsNullOrWhiteSpace(result.error) ? "Inventory baseline is not available yet." : result.error);
            }
        }

        private void BindManager(PlayerEntityGameManager manager)
        {
            if (ReferenceEquals(_manager, manager))
                return;
            UnbindManager();
            _manager = manager;
            if (_manager == null)
                return;
            _manager.PlayerItemsChangedReceived += OnPlayerItemsChanged;
            _manager.ProgressionSnapshotReceived += OnProgressionChanged;
        }

        private void UnbindManager()
        {
            if (_manager != null)
            {
                _manager.PlayerItemsChangedReceived -= OnPlayerItemsChanged;
                _manager.ProgressionSnapshotReceived -= OnProgressionChanged;
            }
            _manager = null;
        }

        private void BindOwner()
        {
            PlayerEntityClient next = null;
            PlayerEntityClient.TryGetOwner(out next);
            if (ReferenceEquals(_owner, next))
                return;
            UnbindOwner();
            _owner = next;
            if (_owner != null)
                _owner.DisplayNameChanged += OnOwnerDisplayNameChanged;
        }

        private void UnbindOwner()
        {
            if (_owner != null)
                _owner.DisplayNameChanged -= OnOwnerDisplayNameChanged;
            _owner = null;
        }

        private void OnOwnerDisplayNameChanged(PlayerEntityClient owner)
        {
            if (IsOpen)
                RefreshHeader();
        }

        private void OnProgressionChanged(ProgressionSnapshotMessage snapshot)
        {
            _progression = snapshot;
            if (IsOpen)
                RefreshHeader();
        }

        private void OnPlayerItemsChanged(PlayerItemsResponseMessage snapshot)
        {
            if (snapshot.inventoryCapacity <= 0)
                return;
            if (_snapshot.inventoryCapacity > 0 &&
                (snapshot.inventoryRevision < _snapshot.inventoryRevision || snapshot.equipmentRevision < _snapshot.equipmentRevision))
                return;

            _snapshot = snapshot;
            if (!IsOpen)
                return;

            _selectedInventorySlot = -1;
            ClearDragSource();
            RenderSnapshot();
        }

        private void RefreshHeader()
        {
            if (characterNameText != null)
                characterNameText.text = _owner != null && !string.IsNullOrWhiteSpace(_owner.DisplayName)
                    ? _owner.DisplayName
                    : "Character";
            if (levelText != null)
                levelText.text = _progression.success ? $"Level {_progression.level}" : "Level --";

            if (summaryText != null)
            {
                int occupied = _snapshot.inventory != null ? _snapshot.inventory.Length : 0;
                float weight = 0f;
                PlayerItemWire[] items = _snapshot.inventory ?? Array.Empty<PlayerItemWire>();
                for (int i = 0; i < items.Length; ++i)
                    weight += Math.Max(0, items[i].quantity) * Mathf.Max(0f, items[i].unitWeight);

                summaryText.text = _snapshot.inventoryCapacity > 0
                    ? $"Inventory {occupied}/{_snapshot.inventoryCapacity}    Weight {weight:0.##}"
                    : "Inventory not hydrated";
            }
        }

        private void RenderSnapshot()
        {
            RefreshHeader();
            if (_snapshot.inventoryCapacity <= 0)
            {
                HideInventoryViews(0);
                HideEquipmentViews(0);
                UpdateInventoryScrollState(0);
                return;
            }

            int capacity = Math.Max(0, _snapshot.inventoryCapacity);
            EnsureInventoryViews(capacity);

            var bySlot = new Dictionary<int, PlayerItemWire>();
            PlayerItemWire[] items = _snapshot.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i].inventorySlot >= 0)
                    bySlot[items[i].inventorySlot] = items[i];

            for (int slotIndex = 0; slotIndex < capacity; ++slotIndex)
            {
                bool has = bySlot.TryGetValue(slotIndex, out PlayerItemWire item);
                StandaloneInventorySlotView view = _inventoryViews[slotIndex];
                view.Bind(
                    slotIndex,
                    has,
                    item,
                    slotIndex == _selectedInventorySlot,
                    OnInventoryClicked,
                    tooltip,
                    BeginInventoryDrag,
                    ClearDragSource,
                    DropOnInventorySlot,
                    OnInventoryDoubleClicked);
                view.gameObject.SetActive(true);
            }
            HideInventoryViews(capacity);
            UpdateInventoryScrollState(capacity);

            EquipmentSlotWire[] equipment = _snapshot.equipment ?? Array.Empty<EquipmentSlotWire>();
            EnsureEquipmentViews(equipment.Length);
            for (int i = 0; i < equipment.Length; ++i)
            {
                EquipmentSlotWire slot = equipment[i];
                StandaloneEquipmentSlotView view = _equipmentViews[i];
                view.Bind(
                    slot.slotId,
                    slot.displayName,
                    slot.hasItem,
                    slot.item,
                    OnEquipmentClicked,
                    tooltip,
                    BeginEquipmentDrag,
                    ClearDragSource,
                    DropOnEquipmentSlot,
                    OnEquipmentDoubleClicked);
                view.gameObject.SetActive(true);
            }
            HideEquipmentViews(equipment.Length);
        }

        private void UpdateInventoryScrollState(int capacity)
        {
            if (inventoryScroll == null)
                return;
            bool needsScroll = capacity > 30;
            inventoryScroll.vertical = needsScroll;
            inventoryScroll.enabled = needsScroll;
            if (!needsScroll)
                inventoryScroll.verticalNormalizedPosition = 1f;
        }

        private void EnsureInventoryViews(int count)
        {
            if (inventorySlotTemplate == null || inventoryContent == null)
                return;
            while (_inventoryViews.Count < count)
            {
                StandaloneInventorySlotView view = Instantiate(inventorySlotTemplate, inventoryContent);
                view.gameObject.name = $"InventorySlot_{_inventoryViews.Count:00}";
                view.gameObject.SetActive(false);
                _inventoryViews.Add(view);
            }
        }

        private void EnsureEquipmentViews(int count)
        {
            if (equipmentSlotTemplate == null || equipmentContent == null)
                return;
            while (_equipmentViews.Count < count)
            {
                StandaloneEquipmentSlotView view = Instantiate(equipmentSlotTemplate, equipmentContent);
                view.gameObject.name = $"EquipmentSlot_{_equipmentViews.Count:00}";
                view.gameObject.SetActive(false);
                _equipmentViews.Add(view);
            }
        }

        private void HideInventoryViews(int start)
        {
            for (int i = start; i < _inventoryViews.Count; ++i)
                _inventoryViews[i].gameObject.SetActive(false);
        }

        private void HideEquipmentViews(int start)
        {
            for (int i = start; i < _equipmentViews.Count; ++i)
                _equipmentViews[i].gameObject.SetActive(false);
        }

        private void OnInventoryClicked(int slotIndex)
        {
            if (_busy)
                return;
            if (!TryGetInventoryItem(slotIndex, out PlayerItemWire item))
            {
                _selectedInventorySlot = -1;
                SetFeedback(string.Empty);
                RenderSnapshot();
                return;
            }

            _selectedInventorySlot = slotIndex;
            SetFeedback($"Selected {item.displayName}.");
            RenderSnapshot();
        }

        private void OnInventoryDoubleClicked(int slotIndex)
        {
            if (_busy || !TryGetInventoryItem(slotIndex, out PlayerItemWire item))
                return;

            _selectedInventorySlot = slotIndex;
            if (item.canUse)
            {
                UseSelectedAsync(slotIndex, item.displayName).Forget();
                return;
            }

            if (item.allowedEquipmentSlots != null && item.allowedEquipmentSlots.Length == 1)
            {
                EquipSelectedAsync(slotIndex, item.allowedEquipmentSlots[0], item.displayName).Forget();
                return;
            }

            SetFeedback($"Selected {item.displayName}. Choose an equipment slot.");
            RenderSnapshot();
        }

        private void OnEquipmentClicked(string equipmentSlotId)
        {
            if (_busy || string.IsNullOrWhiteSpace(equipmentSlotId))
                return;

            // A selected inventory item may still be placed with one click on the desired
            // compatible equipment slot. Merely clicking an equipped item no longer removes it.
            if (_selectedInventorySlot >= 0 && TryGetInventoryItem(_selectedInventorySlot, out PlayerItemWire selected))
            {
                if (!AllowsEquipmentSlot(selected, equipmentSlotId))
                {
                    SetFeedback($"{selected.displayName} cannot equip there.");
                    return;
                }
                EquipSelectedAsync(_selectedInventorySlot, equipmentSlotId, selected.displayName).Forget();
            }
        }

        private void OnEquipmentDoubleClicked(string equipmentSlotId)
        {
            if (_busy || string.IsNullOrWhiteSpace(equipmentSlotId) || _selectedInventorySlot >= 0)
                return;

            if (TryGetEquipmentSlot(equipmentSlotId, out EquipmentSlotWire slot) && slot.hasItem)
                UnequipAsync(equipmentSlotId, slot.item.displayName, -1).Forget();
        }

        private void BeginInventoryDrag(int slotIndex)
        {
            if (_busy || !IsOpen || !TryGetInventoryItem(slotIndex, out PlayerItemWire item))
                return;

            _dragInventorySlot = slotIndex;
            _dragEquipmentSlotId = string.Empty;
            StandaloneItemDragContext.BeginInventory(slotIndex, item);
            SetFeedback($"Dragging {item.displayName}.");
            dragGhost?.Show(ClientItemIconResolver.Resolve(item));

            for (int i = 0; i < _equipmentViews.Count; ++i)
            {
                StandaloneEquipmentSlotView view = _equipmentViews[i];
                if (view != null && view.gameObject.activeSelf)
                    view.SetDropHighlight(true, AllowsEquipmentSlot(item, view.SlotId));
            }
        }

        private void BeginEquipmentDrag(string equipmentSlotId)
        {
            if (_busy || !IsOpen || string.IsNullOrWhiteSpace(equipmentSlotId) ||
                !TryGetEquipmentSlot(equipmentSlotId, out EquipmentSlotWire slot) || !slot.hasItem)
                return;

            _dragInventorySlot = -1;
            _dragEquipmentSlotId = equipmentSlotId;
            StandaloneItemDragContext.BeginEquipment(equipmentSlotId, slot.item);
            SetFeedback($"Dragging {slot.item.displayName}.");
            dragGhost?.Show(ClientItemIconResolver.Resolve(slot.item));
        }

        private void DropOnInventorySlot(int targetInventorySlot)
        {
            if (_busy || !IsOpen)
                return;

            if (_dragInventorySlot >= 0)
            {
                int source = _dragInventorySlot;
                ClearDragSource();
                if (source != targetInventorySlot)
                    MoveAsync(source, targetInventorySlot).Forget();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_dragEquipmentSlotId))
            {
                string sourceEquipment = _dragEquipmentSlotId;
                string displayName = TryGetEquipmentSlot(sourceEquipment, out EquipmentSlotWire slot) && slot.hasItem
                    ? slot.item.displayName
                    : sourceEquipment;
                ClearDragSource();
                UnequipAsync(sourceEquipment, displayName, targetInventorySlot).Forget();
            }
            else if (StandaloneItemDragContext.Kind == StandaloneItemDragContext.SourceKind.Storage)
            {
                int storageSlot = StandaloneItemDragContext.SlotIndex;
                int quantity = Math.Max(1, StandaloneItemDragContext.Item.quantity);
                ClearDragSource();
                WithdrawStorageAsync(storageSlot, quantity).Forget();
            }
        }

        private void DropOnEquipmentSlot(string targetEquipmentSlotId)
        {
            if (_busy || !IsOpen || string.IsNullOrWhiteSpace(targetEquipmentSlotId))
                return;

            if (_dragInventorySlot >= 0)
            {
                int source = _dragInventorySlot;
                if (!TryGetInventoryItem(source, out PlayerItemWire item))
                {
                    ClearDragSource();
                    return;
                }

                if (!AllowsEquipmentSlot(item, targetEquipmentSlotId))
                {
                    ClearDragSource();
                    SetFeedback($"{item.displayName} cannot equip there.");
                    return;
                }

                string displayName = item.displayName;
                ClearDragSource();
                EquipSelectedAsync(source, targetEquipmentSlotId, displayName).Forget();
                return;
            }

            // Equipment-to-equipment moves intentionally remain explicit through inventory.
            // Existing authoritative requests have unambiguous Inventory->Equipment and
            // Equipment->Inventory semantics; do not invent a third wire operation here.
            if (!string.IsNullOrWhiteSpace(_dragEquipmentSlotId))
            {
                string source = _dragEquipmentSlotId;
                ClearDragSource();
                if (!string.Equals(source, targetEquipmentSlotId, StringComparison.Ordinal))
                    SetFeedback("Move equipped items through Inventory to change equipment slots.");
            }
        }

        private void ClearDragSource()
        {
            _dragInventorySlot = -1;
            _dragEquipmentSlotId = string.Empty;
            StandaloneItemDragContext.Clear();
            dragGhost?.Hide();
            for (int i = 0; i < _equipmentViews.Count; ++i)
                _equipmentViews[i]?.SetDropHighlight(false, false);
        }

        private async UniTaskVoid WithdrawStorageAsync(int storageSlot, int quantity)
        {
            if (_manager == null || storageSlot < 0) return;
            _busy = true;
            SetFeedback("Withdrawing item...");
            SocialEconomyMutationResponseMessage result = await _manager.RequestWithdrawFromStorageAsync(storageSlot, Math.Max(1, quantity));
            await UniTask.SwitchToMainThread();
            _busy = false;
            SetFeedback(result.success ? string.Empty : result.error);
        }

        private async UniTaskVoid UseSelectedAsync(int slotIndex, string displayName)
        {
            if (_manager == null) return;
            _busy = true;
            SetFeedback($"Using {displayName}...");
            PlayerItemMutationResponseMessage result = await _manager.RequestUseItemAsync(slotIndex);
            await UniTask.SwitchToMainThread();
            _busy = false;
            _selectedInventorySlot = -1;
            SetFeedback(result.success ? string.Empty : result.error);
        }

        private async UniTaskVoid MoveAsync(int from, int to)
        {
            if (_manager == null) return;
            _busy = true;
            SetFeedback("Moving item...");
            PlayerItemMutationResponseMessage result = await _manager.RequestMoveInventoryAsync(from, to);
            await UniTask.SwitchToMainThread();
            _busy = false;
            _selectedInventorySlot = -1;
            SetFeedback(result.success ? string.Empty : result.error);
        }

        private async UniTaskVoid EquipSelectedAsync(int slotIndex, string equipmentSlotId, string displayName)
        {
            if (_manager == null) return;
            _busy = true;
            SetFeedback($"Equipping {displayName}...");
            PlayerItemMutationResponseMessage result = await _manager.RequestEquipItemAsync(slotIndex, equipmentSlotId);
            await UniTask.SwitchToMainThread();
            _busy = false;
            _selectedInventorySlot = -1;
            SetFeedback(result.success ? string.Empty : result.error);
        }

        private async UniTaskVoid UnequipAsync(string equipmentSlotId, string displayName, int preferredInventorySlot)
        {
            if (_manager == null) return;
            _busy = true;
            SetFeedback($"Unequipping {displayName}...");
            PlayerItemMutationResponseMessage result = await _manager.RequestUnequipItemAsync(equipmentSlotId, preferredInventorySlot);
            await UniTask.SwitchToMainThread();
            _busy = false;
            SetFeedback(result.success ? string.Empty : result.error);
        }

        private void SetFeedback(string value)
        {
            if (feedbackText == null)
                return;
            feedbackText.text = value ?? string.Empty;
            feedbackText.gameObject.SetActive(!string.IsNullOrWhiteSpace(feedbackText.text));
        }

        private bool TryGetInventoryItem(int slotIndex, out PlayerItemWire item)
        {
            PlayerItemWire[] items = _snapshot.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < items.Length; ++i)
            {
                if (items[i].inventorySlot == slotIndex)
                {
                    item = items[i];
                    return true;
                }
            }
            item = default;
            return false;
        }

        private bool TryGetEquipmentSlot(string slotId, out EquipmentSlotWire slot)
        {
            EquipmentSlotWire[] equipment = _snapshot.equipment ?? Array.Empty<EquipmentSlotWire>();
            for (int i = 0; i < equipment.Length; ++i)
            {
                if (string.Equals(equipment[i].slotId, slotId, StringComparison.Ordinal))
                {
                    slot = equipment[i];
                    return true;
                }
            }
            slot = default;
            return false;
        }

        private static bool AllowsEquipmentSlot(PlayerItemWire item, string equipmentSlotId)
        {
            string[] allowed = item.allowedEquipmentSlots ?? Array.Empty<string>();
            for (int i = 0; i < allowed.Length; ++i)
                if (string.Equals(allowed[i], equipmentSlotId, StringComparison.Ordinal))
                    return true;
            return false;
        }

#if UNITY_EDITOR
        // Original V1 authoring compatibility.
        public void ConfigureForEditor(
            GameObject authoredWindowRoot,
            Button authoredCloseButton,
            Text authoredCharacterName,
            Text authoredLevel,
            Text authoredSummary,
            Text authoredFeedback,
            Transform authoredInventoryContent,
            StandaloneInventorySlotView authoredInventoryTemplate,
            Button authoredPrevious,
            Button authoredNext,
            Text authoredPage,
            Transform authoredEquipmentContent,
            StandaloneEquipmentSlotView authoredEquipmentTemplate,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            ConfigureForEditor(
                authoredWindowRoot,
                authoredCloseButton,
                authoredCharacterName,
                authoredLevel,
                authoredSummary,
                authoredFeedback,
                null,
                authoredInventoryContent,
                authoredInventoryTemplate,
                authoredEquipmentContent,
                authoredEquipmentTemplate,
                authoredTooltip,
                null);
            previousPageButton = authoredPrevious;
            nextPageButton = authoredNext;
            pageText = authoredPage;
        }

        public void ConfigureForEditor(
            GameObject authoredWindowRoot,
            Button authoredCloseButton,
            Text authoredCharacterName,
            Text authoredLevel,
            Text authoredSummary,
            Text authoredFeedback,
            ScrollRect authoredInventoryScroll,
            Transform authoredInventoryContent,
            StandaloneInventorySlotView authoredInventoryTemplate,
            Transform authoredEquipmentContent,
            StandaloneEquipmentSlotView authoredEquipmentTemplate,
            StandaloneHudTooltipPanel authoredTooltip,
            StandaloneDragGhost authoredDragGhost)
        {
            windowRoot = authoredWindowRoot;
            closeButton = authoredCloseButton;
            characterNameText = authoredCharacterName;
            levelText = authoredLevel;
            summaryText = authoredSummary;
            feedbackText = authoredFeedback;
            inventoryScroll = authoredInventoryScroll;
            inventoryContent = authoredInventoryContent;
            inventorySlotTemplate = authoredInventoryTemplate;
            equipmentContent = authoredEquipmentContent;
            equipmentSlotTemplate = authoredEquipmentTemplate;
            tooltip = authoredTooltip;
            dragGhost = authoredDragGhost;
            previousPageButton = null;
            nextPageButton = null;
            pageText = null;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
