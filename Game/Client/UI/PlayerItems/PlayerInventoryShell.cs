using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Shared.Protocol;
using Game.Client.UI.Root;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0414 // Serialized presentation fields are unused in UNITY_SERVER builds.

namespace Game.Client.UI.PlayerItems
{
    /// <summary>
    /// Editable world inventory/equipment shell. It sends only gameplay intent through
    /// Player.Networking; every mutation displayed here is the GameServer-approved result.
    /// </summary>
    public sealed class PlayerInventoryShell : MonoBehaviour
    {
        [Header("Window")]
        [SerializeField] private GameObject windowRoot;
        [SerializeField] private Button closeButton;

        [Header("Inventory")]
        [SerializeField] private Transform inventoryContent;
        [SerializeField] private InventorySlotView inventorySlotTemplate;
        [SerializeField] private Button previousPageButton;
        [SerializeField] private Button nextPageButton;
        [SerializeField] private Text pageText;
        [SerializeField, Min(5)] private int slotsPerPage = 30;

        [Header("Equipment")]
        [SerializeField] private Transform equipmentContent;
        [SerializeField] private EquipmentSlotView equipmentSlotTemplate;

        private readonly List<InventorySlotView> _inventoryViews = new List<InventorySlotView>();
        private readonly List<EquipmentSlotView> _equipmentViews = new List<EquipmentSlotView>();
        private PlayerEntityGameManager _manager;
        private PlayerItemsResponseMessage _snapshot;
        private int _selectedInventorySlot = -1;
        private bool _busy;
        private int _inventoryPage;
        private int _dragInventorySlot = -1;
        private string _dragEquipmentSlotId = string.Empty;

        private const string InventoryPagePreferenceKey = "MMO.Inventory.LastPage";

        public bool IsOpen => windowRoot != null && windowRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            ValidateAuthoredPresentation();
            closeButton?.onClick.AddListener(Close);
            previousPageButton?.onClick.AddListener(OnPreviousPageClicked);
            nextPageButton?.onClick.AddListener(OnNextPageClicked);
            if (inventorySlotTemplate != null) inventorySlotTemplate.gameObject.SetActive(false);
            if (equipmentSlotTemplate != null) equipmentSlotTemplate.gameObject.SetActive(false);
            _inventoryPage = Math.Max(0, PlayerPrefs.GetInt(InventoryPagePreferenceKey, 0));
            windowRoot?.SetActive(false);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            closeButton?.onClick.RemoveListener(Close);
            previousPageButton?.onClick.RemoveListener(OnPreviousPageClicked);
            nextPageButton?.onClick.RemoveListener(OnNextPageClicked);
            UnbindManager();
#endif
        }

        public void Toggle()
        {
#if !UNITY_SERVER
            if (IsOpen) Close();
            else Open();
#endif
        }

        public void Open()
        {
#if !UNITY_SERVER
            if (windowRoot == null) return;
            windowRoot.SetActive(true);
            _selectedInventorySlot = -1;

            if (!TryGetManager(out PlayerEntityGameManager manager))
            {
                SetStatus("Game server connection is unavailable.");
                RefreshButtons();
                return;
            }

            PlayerItemsResponseMessage cached = manager.LatestPlayerItems;
            if (cached.inventoryCapacity > 0)
            {
                _snapshot = cached;
                SetStatus("Inventory ready from authoritative client cache.");
                RenderSnapshot();
                RefreshButtons();
                return;
            }

            if (manager.PlayerItemsSnapshotRequestInFlight)
            {
                SetStatus("Authoritative inventory baseline is synchronizing...");
                RefreshButtons();
                return;
            }

            SetStatus("Loading authoritative inventory...");
            RefreshAsync(forceServer: false).Forget();
#endif
        }

        public void Close()
        {
#if !UNITY_SERVER
            _selectedInventorySlot = -1;
            ClearDragSource();
            windowRoot?.SetActive(false);
#endif
        }

        private void OnPreviousPageClicked()
        {
            if (_inventoryPage <= 0) return;
            _inventoryPage--; _selectedInventorySlot = -1; SaveInventoryPagePreference(); RenderSnapshot();
        }
        private void OnNextPageClicked()
        {
            int pages = GetInventoryPageCount();
            if (_inventoryPage + 1 >= pages) return;
            _inventoryPage++; _selectedInventorySlot = -1; SaveInventoryPagePreference(); RenderSnapshot();
        }

        private async UniTaskVoid RefreshAsync(bool forceServer)
        {
            if (_busy) return;
            if (!TryGetManager(out PlayerEntityGameManager manager))
            {
                SetStatus("Game server connection is unavailable.");
                return;
            }

            _busy = true;
            RefreshButtons();
            PlayerItemsResponseMessage response = await manager.RequestPlayerItemsAsync(forceServer);
            await UniTask.SwitchToMainThread();
            _busy = false;
            ApplyResponse(response, response.success ? "Inventory synchronized." : response.error);
        }

        private void OnInventoryClicked(int slotIndex)
        {
            if (_busy || !IsOpen) return;
            bool clickedHasItem = TryGetInventoryItem(slotIndex, out PlayerItemWire clickedItem);

            if (_selectedInventorySlot < 0)
            {
                if (!clickedHasItem)
                {
                    SetStatus("Select an occupied slot first.");
                    return;
                }
                _selectedInventorySlot = slotIndex;
                SetStatus(clickedItem.canUse
                    ? $"Selected {clickedItem.displayName}. Click it again to use it, or choose another inventory/equipment slot."
                    : $"Selected {clickedItem.displayName}. Choose another inventory slot or an equipment slot.");
                RenderSnapshot();
                return;
            }

            if (_selectedInventorySlot == slotIndex)
            {
                // Usable items take precedence over the quick-equip shortcut. This is still
                // only client intent; the GameServer validates content, state, and persistence.
                if (clickedHasItem && clickedItem.canUse)
                {
                    SetStatus($"Using {clickedItem.displayName}...");
                    UseAsync(slotIndex).Forget();
                    return;
                }

                // A second click on an equippable item is a quick-equip shortcut when the
                // server snapshot exposes exactly one legal equipment slot. The GameServer
                // still performs the authoritative validation.
                if (clickedHasItem &&
                    clickedItem.allowedEquipmentSlots != null &&
                    clickedItem.allowedEquipmentSlots.Length == 1 &&
                    !string.IsNullOrWhiteSpace(clickedItem.allowedEquipmentSlots[0]))
                {
                    string targetSlot = clickedItem.allowedEquipmentSlots[0];
                    SetStatus($"Equipping {clickedItem.displayName} -> {GetEquipmentDisplayName(targetSlot)}...");
                    EquipAsync(slotIndex, targetSlot).Forget();
                    return;
                }

                _selectedInventorySlot = -1;
                SetStatus("Selection cleared.");
                RenderSnapshot();
                return;
            }

            MoveAsync(_selectedInventorySlot, slotIndex).Forget();
        }

        private void OnEquipmentClicked(string equipmentSlotId)
        {
            if (_busy || !IsOpen || string.IsNullOrWhiteSpace(equipmentSlotId)) return;

            if (_selectedInventorySlot >= 0)
            {
                if (!TryGetInventoryItem(_selectedInventorySlot, out PlayerItemWire selected))
                {
                    _selectedInventorySlot = -1;
                    SetStatus("Selected inventory item is no longer available.");
                    RenderSnapshot();
                    return;
                }

                if (!AllowsEquipmentSlot(selected, equipmentSlotId))
                {
                    SetStatus($"{selected.displayName} cannot equip to {GetEquipmentDisplayName(equipmentSlotId)}.");
                    return;
                }

                SetStatus($"Equipping {selected.displayName} -> {GetEquipmentDisplayName(equipmentSlotId)}...");
                EquipAsync(_selectedInventorySlot, equipmentSlotId).Forget();
                return;
            }

            if (TryGetEquipmentSlot(equipmentSlotId, out EquipmentSlotWire slot) && slot.hasItem)
            {
                SetStatus($"Unequipping {slot.item.displayName}...");
                UnequipAsync(equipmentSlotId).Forget();
                return;
            }

            SetStatus("Select an inventory item to equip, or click occupied equipment to unequip it.");
        }

        private void BeginInventoryDrag(int slotIndex)
        {
            if (_busy || !IsOpen || !TryGetInventoryItem(slotIndex, out PlayerItemWire item))
                return;
            _dragInventorySlot = slotIndex;
            _dragEquipmentSlotId = string.Empty;
            SetStatus($"Dragging {item.displayName}.");
        }

        private void BeginEquipmentDrag(string equipmentSlotId)
        {
            if (_busy || !IsOpen || string.IsNullOrWhiteSpace(equipmentSlotId) ||
                !TryGetEquipmentSlot(equipmentSlotId, out EquipmentSlotWire slot) || !slot.hasItem)
                return;
            _dragInventorySlot = -1;
            _dragEquipmentSlotId = equipmentSlotId;
            SetStatus($"Dragging {slot.item.displayName} from {GetEquipmentDisplayName(equipmentSlotId)}.");
        }

        private void DropOnInventorySlot(int targetInventorySlot)
        {
            if (_busy || !IsOpen) return;

            if (_dragInventorySlot >= 0)
            {
                int source = _dragInventorySlot;
                ClearDragSource();
                if (source == targetInventorySlot) return;
                SetStatus($"Moving inventory slot {source + 1} -> {targetInventorySlot + 1}...");
                MoveAsync(source, targetInventorySlot).Forget();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_dragEquipmentSlotId))
            {
                string sourceEquipment = _dragEquipmentSlotId;
                ClearDragSource();
                SetStatus($"Unequipping {GetEquipmentDisplayName(sourceEquipment)} -> slot {targetInventorySlot + 1}...");
                UnequipAsync(sourceEquipment, targetInventorySlot).Forget();
            }
        }

        private void DropOnEquipmentSlot(string targetEquipmentSlotId)
        {
            if (_busy || !IsOpen || string.IsNullOrWhiteSpace(targetEquipmentSlotId)) return;

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
                    SetStatus($"{item.displayName} cannot equip to {GetEquipmentDisplayName(targetEquipmentSlotId)}.");
                    return;
                }

                ClearDragSource();
                SetStatus($"Equipping {item.displayName} -> {GetEquipmentDisplayName(targetEquipmentSlotId)}...");
                EquipAsync(source, targetEquipmentSlotId).Forget();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_dragEquipmentSlotId))
            {
                string sourceEquipment = _dragEquipmentSlotId;
                ClearDragSource();
                if (!string.Equals(sourceEquipment, targetEquipmentSlotId, StringComparison.Ordinal))
                    SetStatus("Move equipped items through inventory so the existing authoritative equip/unequip requests stay unambiguous.");
            }
        }

        private void ClearDragSource()
        {
            _dragInventorySlot = -1;
            _dragEquipmentSlotId = string.Empty;
        }

        public void DropSelected(bool fullStack)
        {
#if !UNITY_SERVER
            if (_busy || !IsOpen)
                return;
            if (_selectedInventorySlot < 0 || !TryGetInventoryItem(_selectedInventorySlot, out PlayerItemWire selected))
            {
                SetStatus("Select an inventory item before dropping it.");
                return;
            }
            int quantity = fullStack ? selected.quantity : 1;
            SetStatus($"Dropping {quantity} {selected.displayName}...");
            DropAsync(_selectedInventorySlot, quantity).Forget();
#endif
        }

        private async UniTaskVoid DropAsync(int inventoryIndex, int quantity)
        {
            if (!TryGetManager(out PlayerEntityGameManager manager)) return;
            _busy = true; RefreshButtons();
            PlayerItemMutationResponseMessage response = await manager.RequestDropItemAsync(inventoryIndex, quantity);
            await UniTask.SwitchToMainThread();
            _busy = false; _selectedInventorySlot = -1;
            ApplyMutationResponse(response, response.success ? "Item dropped into the world." : response.error);
        }

        private async UniTaskVoid MoveAsync(int from, int to)
        {
            if (!TryGetManager(out PlayerEntityGameManager manager)) return;
            _busy = true; RefreshButtons();
            PlayerItemMutationResponseMessage response = await manager.RequestMoveInventoryAsync(from, to);
            await UniTask.SwitchToMainThread();
            _busy = false; _selectedInventorySlot = -1;
            ApplyMutationResponse(response, response.success ? "Inventory updated." : response.error);
        }

        private async UniTaskVoid UseAsync(int inventoryIndex)
        {
            if (!TryGetManager(out PlayerEntityGameManager manager)) return;
            _busy = true; RefreshButtons();
            PlayerItemMutationResponseMessage response = await manager.RequestUseItemAsync(inventoryIndex);
            await UniTask.SwitchToMainThread();
            _busy = false; _selectedInventorySlot = -1;
            ApplyMutationResponse(response, response.success ? "Item used." : response.error);
        }

        private async UniTaskVoid EquipAsync(int inventoryIndex, string equipmentSlotId)
        {
            if (!TryGetManager(out PlayerEntityGameManager manager)) return;
            _busy = true; RefreshButtons();
            PlayerItemMutationResponseMessage response = await manager.RequestEquipItemAsync(inventoryIndex, equipmentSlotId);
            await UniTask.SwitchToMainThread();
            _busy = false; _selectedInventorySlot = -1;
            ApplyMutationResponse(response, response.success ? "Equipment updated." : response.error);
        }

        private async UniTaskVoid UnequipAsync(string equipmentSlotId, int preferredInventoryIndex = -1)
        {
            if (!TryGetManager(out PlayerEntityGameManager manager)) return;
            _busy = true; RefreshButtons();
            PlayerItemMutationResponseMessage response = await manager.RequestUnequipItemAsync(equipmentSlotId, preferredInventoryIndex);
            await UniTask.SwitchToMainThread();
            _busy = false;
            ApplyMutationResponse(response, response.success ? "Item returned to inventory." : response.error);
        }

        private void ApplyResponse(PlayerItemsResponseMessage response, string message)
        {
            if (response.inventoryCapacity > 0)
                _snapshot = response;
            SetStatus(string.IsNullOrWhiteSpace(message)
                ? ((PlayerItemOperationStatus)response.status).ToString()
                : message);
            RenderSnapshot();
            RefreshButtons();
        }

        private void ApplyMutationResponse(PlayerItemMutationResponseMessage response, string message)
        {
            string feedback = string.IsNullOrWhiteSpace(message)
                ? ((PlayerItemOperationStatus)response.status).ToString()
                : message;
            SetStatus(feedback);
            ClientUIRoot.Instance?.PresentSystemMessage(feedback);

            // Successful mutations are reflected by the authoritative revisioned delta.
            // Do not request or apply a second full inventory/equipment snapshot here.
            RenderSnapshot();
            RefreshButtons();
        }

        private void RenderSnapshot()
        {
            if (_snapshot.inventoryCapacity < 1)
                return;

            int pageSize = Math.Max(5, slotsPerPage);
            int pageCount = GetInventoryPageCount();
            _inventoryPage = Math.Max(0, Math.Min(_inventoryPage, pageCount - 1));
            int firstSlot = _inventoryPage * pageSize;
            int visibleCount = Math.Min(pageSize, Math.Max(0, _snapshot.inventoryCapacity - firstSlot));
            EnsureInventoryViews(visibleCount);
            var bySlot = new Dictionary<int, PlayerItemWire>();
            PlayerItemWire[] items = _snapshot.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i].inventorySlot >= 0) bySlot[items[i].inventorySlot] = items[i];

            for (int i = 0; i < visibleCount; ++i)
            {
                int slotIndex = firstSlot + i;
                bool has = bySlot.TryGetValue(slotIndex, out PlayerItemWire item);
                InventorySlotView view = _inventoryViews[i];
                LayoutInventoryView(view, i);
                view.Bind(
                    slotIndex, has, item, slotIndex == _selectedInventorySlot, OnInventoryClicked,
                    BeginInventoryDrag, ClearDragSource, DropOnInventorySlot);
                view.gameObject.SetActive(true);
            }
            for (int i = visibleCount; i < _inventoryViews.Count; ++i)
                _inventoryViews[i].gameObject.SetActive(false);
            if (pageText != null) pageText.text = $"Page {_inventoryPage + 1}/{pageCount}   Slots {_snapshot.inventoryCapacity}";
            if (previousPageButton != null) previousPageButton.interactable = !_busy && _inventoryPage > 0;
            if (nextPageButton != null) nextPageButton.interactable = !_busy && _inventoryPage + 1 < pageCount;

            EquipmentSlotWire[] equipment = _snapshot.equipment ?? Array.Empty<EquipmentSlotWire>();
            EnsureEquipmentViews(equipment.Length);
            for (int i = 0; i < equipment.Length; ++i)
            {
                EquipmentSlotWire slot = equipment[i];
                LayoutEquipmentView(_equipmentViews[i], i);
                _equipmentViews[i].Bind(
                    slot.slotId, slot.displayName, slot.hasItem, slot.item, OnEquipmentClicked,
                    BeginEquipmentDrag, ClearDragSource, DropOnEquipmentSlot);
                _equipmentViews[i].gameObject.SetActive(true);
            }
            for (int i = equipment.Length; i < _equipmentViews.Count; ++i)
                _equipmentViews[i].gameObject.SetActive(false);

        }

        private int GetInventoryPageCount()
        {
            int pageSize = Math.Max(5, slotsPerPage);
            return Math.Max(1, (_snapshot.inventoryCapacity + pageSize - 1) / pageSize);
        }

        private static void LayoutInventoryView(InventorySlotView view, int localIndex)
        {
            if (view == null) return;
            RectTransform rect = view.transform as RectTransform;
            if (rect == null) return;
            const int columns = 5;
            const float width = 132f;
            const float height = 70f;
            const float xGap = 8f;
            const float yGap = 8f;
            int column = localIndex % columns;
            int row = localIndex / columns;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(column * (width + xGap), -row * (height + yGap));
        }

        private static void LayoutEquipmentView(EquipmentSlotView view, int index)
        {
            if (view == null) return;
            RectTransform rect = view.transform as RectTransform;
            if (rect == null) return;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(320, 62);
            rect.anchoredPosition = new Vector2(0, -index * 68f);
        }

        private void EnsureInventoryViews(int count)
        {
            if (inventorySlotTemplate == null || inventoryContent == null) return;
            while (_inventoryViews.Count < count)
            {
                InventorySlotView view = Instantiate(inventorySlotTemplate, inventoryContent);
                view.gameObject.name = $"InventorySlot_{_inventoryViews.Count:00}";
                view.gameObject.SetActive(true);
                _inventoryViews.Add(view);
            }
        }

        private void EnsureEquipmentViews(int count)
        {
            if (equipmentSlotTemplate == null || equipmentContent == null) return;
            while (_equipmentViews.Count < count)
            {
                EquipmentSlotView view = Instantiate(equipmentSlotTemplate, equipmentContent);
                view.gameObject.name = $"EquipmentSlot_{_equipmentViews.Count:00}";
                view.gameObject.SetActive(true);
                _equipmentViews.Add(view);
            }
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
            EquipmentSlotWire[] slots = _snapshot.equipment ?? Array.Empty<EquipmentSlotWire>();
            for (int i = 0; i < slots.Length; ++i)
            {
                if (string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal))
                {
                    slot = slots[i];
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

        private string GetEquipmentDisplayName(string equipmentSlotId)
        {
            return TryGetEquipmentSlot(equipmentSlotId, out EquipmentSlotWire slot) &&
                   !string.IsNullOrWhiteSpace(slot.displayName)
                ? slot.displayName
                : equipmentSlotId;
        }

        private bool TryGetManager(out PlayerEntityGameManager manager)
        {
            if (_manager == null)
                BindManager(FindFirstObjectByType<PlayerEntityGameManager>());
            manager = _manager;
            return manager != null && manager.IsClientConnected;
        }

        private void BindManager(PlayerEntityGameManager manager)
        {
            if (ReferenceEquals(_manager, manager)) return;
            UnbindManager();
            _manager = manager;
            if (_manager != null)
                _manager.PlayerItemsChangedReceived += OnAuthoritativePlayerItemsChanged;
        }

        private void UnbindManager()
        {
            if (_manager != null)
                _manager.PlayerItemsChangedReceived -= OnAuthoritativePlayerItemsChanged;
            _manager = null;
        }

        private void OnAuthoritativePlayerItemsChanged(PlayerItemsResponseMessage response)
        {
            if (response.inventoryCapacity <= 0) return;
            if (_snapshot.inventoryCapacity > 0 &&
                (response.inventoryRevision < _snapshot.inventoryRevision ||
                 response.equipmentRevision < _snapshot.equipmentRevision))
                return;

            _snapshot = response;
            if (!IsOpen) return;
            _selectedInventorySlot = -1;
            SetStatus("Inventory updated by the server.");
            RenderSnapshot();
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            int pages = GetInventoryPageCount();
            if (previousPageButton != null) previousPageButton.interactable = !_busy && _inventoryPage > 0;
            if (nextPageButton != null) nextPageButton.interactable = !_busy && _inventoryPage + 1 < pages;
        }


        private void SaveInventoryPagePreference()
        {
            PlayerPrefs.SetInt(InventoryPagePreferenceKey, Math.Max(0, _inventoryPage));
        }

        private void SetStatus(string value)
        {
            // The old inventory status strip was removed. Durable mutation results are already
            // routed to local System chat by ApplyMutationResponse; transient selection/drag
            // instructions stay local to pointer/slot presentation and create no extra HUD noise.
        }

        private void ValidateAuthoredPresentation()
        {
#if !UNITY_SERVER
            if (windowRoot != null && inventoryContent != null && equipmentContent != null &&
                inventorySlotTemplate != null && equipmentSlotTemplate != null)
                return;

            Debug.LogError(
                "[InventoryUI] PlayerInventoryShell is missing authored prefab references. " +
                "Repair the authored references on ClientUIRoot.prefab directly. The old one-time UI builder menu has been retired.",
                this);
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only migration helper. Runtime never binds by hierarchy path. Once these
        /// references are serialized into ClientUIRoot.prefab, the inventory hierarchy may
        /// be renamed/reparented without breaking the controller.
        /// </summary>
        public void CaptureAuthoredReferencesForEditor()
        {
            if (windowRoot == null)
            {
                Transform candidate = FindDescendantEditor(transform, "InventoryWindow") ??
                                      FindDescendantEditor(transform, "WindowRoot");
                windowRoot = candidate != null ? candidate.gameObject : null;
            }

            Transform window = windowRoot != null ? windowRoot.transform : null;
            if (window != null)
            {
                if (closeButton == null) closeButton = window.Find("Header/CloseButton")?.GetComponent<Button>();
                if (inventoryContent == null) inventoryContent = window.Find("InventoryPanel/SlotContent");
                if (equipmentContent == null) equipmentContent = window.Find("EquipmentPanel/SlotContent");
                if (inventorySlotTemplate == null) inventorySlotTemplate = window.Find("InventoryPanel/SlotContent/InventorySlotTemplate")?.GetComponent<InventorySlotView>();
                if (equipmentSlotTemplate == null) equipmentSlotTemplate = window.Find("EquipmentPanel/SlotContent/EquipmentSlotTemplate")?.GetComponent<EquipmentSlotView>();
                if (previousPageButton == null) previousPageButton = window.Find("InventoryPanel/PreviousPageButton")?.GetComponent<Button>();
                if (nextPageButton == null) nextPageButton = window.Find("InventoryPanel/NextPageButton")?.GetComponent<Button>();
                if (pageText == null) pageText = window.Find("InventoryPanel/PageText")?.GetComponent<Text>();
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }

        private static Transform FindDescendantEditor(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found = FindDescendantEditor(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        public GameObject AuthoredWindowRootForEditor => windowRoot;

#endif
    }
}
