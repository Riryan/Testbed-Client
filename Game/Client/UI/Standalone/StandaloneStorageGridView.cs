using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Gameplay;
using Game.Client.UI.PlayerItems;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Icon-grid presentation layered onto the existing authorized Storage window.
    ///
    /// Authority and wire behavior are unchanged:
    /// - Inventory -> Storage uses RequestDepositToStorageAsync.
    /// - Storage -> Inventory uses RequestWithdrawFromStorageAsync.
    /// - normal drop transfers the current full stack.
    /// - Shift+Drop opens a local quantity picker, then sends the same existing request
    ///   with the selected quantity.
    ///
    /// The target visual slot is intentionally not sent because the existing protocol is
    /// source-slot + quantity only; the server remains responsible for destination packing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneStorageGridView : MonoBehaviour
    {
        [SerializeField] private GameObject storageWindowRoot;

        [Header("Inventory Grid")]
        [SerializeField] private Transform inventoryContent;
        [SerializeField] private StandaloneStorageSlotView inventorySlotTemplate;

        [Header("Storage Grid")]
        [SerializeField] private Transform storageContent;
        [SerializeField] private StandaloneStorageSlotView storageSlotTemplate;

        [Header("Shared Presentation")]
        [SerializeField] private StandaloneHudTooltipPanel tooltip;
        [SerializeField] private StandaloneDragGhost dragGhost;
        [SerializeField] private StandaloneStorageQuantityPicker quantityPicker;

        [Header("Optional Existing Labels")]
        [SerializeField] private Text statusText;

        private readonly List<StandaloneStorageSlotView> _inventoryViews =
            new List<StandaloneStorageSlotView>(32);
        private readonly List<StandaloneStorageSlotView> _storageViews =
            new List<StandaloneStorageSlotView>(128);

        private PlayerEntityGameManager _manager;
        private bool _busy;

        private bool IsOpen =>
            storageWindowRoot != null &&
            storageWindowRoot.activeInHierarchy;

        private void OnEnable()
        {
#if !UNITY_SERVER
            if (inventorySlotTemplate != null)
                inventorySlotTemplate.gameObject.SetActive(false);
            if (storageSlotTemplate != null)
                storageSlotTemplate.gameObject.SetActive(false);

            BindManager();
            Render();
#endif
        }

        private void OnDisable()
        {
#if !UNITY_SERVER
            EndDrag();
            quantityPicker?.Close();
            tooltip?.Hide();
            UnbindManager();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            UnbindManager();
#endif
        }

        private void BindManager()
        {
            PlayerEntityGameManager next =
                FindFirstObjectByType<PlayerEntityGameManager>();

            if (ReferenceEquals(next, _manager))
                return;

            UnbindManager();
            _manager = next;
            if (_manager == null)
                return;

            _manager.StorageStateReceived += OnStorageStateReceived;
            _manager.PlayerItemsChangedReceived += OnPlayerItemsChanged;
        }

        private void UnbindManager()
        {
            if (_manager != null)
            {
                _manager.StorageStateReceived -= OnStorageStateReceived;
                _manager.PlayerItemsChangedReceived -= OnPlayerItemsChanged;
            }
            _manager = null;
        }

        private void OnStorageStateReceived(StorageStateMessage _)
        {
            if (IsOpen)
                Render();
        }

        private void OnPlayerItemsChanged(PlayerItemsResponseMessage _)
        {
            if (IsOpen)
                Render();
        }

        private void Render()
        {
            if (_manager == null)
                return;

            RenderInventory();
            RenderStorage();
        }

        private void RenderInventory()
        {
            PlayerItemsResponseMessage snapshot = _manager.LatestPlayerItems;
            int capacity = Math.Max(0, snapshot.inventoryCapacity);
            EnsureViews(
                _inventoryViews,
                capacity,
                inventoryContent,
                inventorySlotTemplate,
                "StorageInventorySlot");

            var bySlot = new Dictionary<int, PlayerItemWire>();
            PlayerItemWire[] items = snapshot.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < items.Length; ++i)
            {
                PlayerItemWire item = items[i];
                if (item.inventorySlot >= 0 && item.quantity > 0)
                    bySlot[item.inventorySlot] = item;
            }

            for (int slot = 0; slot < capacity; ++slot)
            {
                bool has = bySlot.TryGetValue(slot, out PlayerItemWire item);
                StandaloneStorageSlotView view = _inventoryViews[slot];
                view.Bind(
                    slot,
                    has,
                    item,
                    "Inventory",
                    tooltip,
                    OnInventoryDoubleClicked,
                    BeginInventoryDrag,
                    EndDrag,
                    DropOnInventoryGrid);
                view.gameObject.SetActive(true);
            }

            HideViews(_inventoryViews, capacity);
        }

        private void RenderStorage()
        {
            StorageStateMessage snapshot = _manager.LatestStorage;
            int capacity = Math.Max(0, snapshot.capacity);
            EnsureViews(
                _storageViews,
                capacity,
                storageContent,
                storageSlotTemplate,
                "StorageSlot");

            var bySlot = new Dictionary<int, PlayerItemWire>();
            PlayerItemWire[] items = snapshot.items ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < items.Length; ++i)
            {
                PlayerItemWire item = items[i];
                if (item.inventorySlot >= 0 && item.quantity > 0)
                    bySlot[item.inventorySlot] = item;
            }

            for (int slot = 0; slot < capacity; ++slot)
            {
                bool has = bySlot.TryGetValue(slot, out PlayerItemWire item);
                StandaloneStorageSlotView view = _storageViews[slot];
                view.Bind(
                    slot,
                    has,
                    item,
                    "Storage",
                    tooltip,
                    OnStorageDoubleClicked,
                    BeginStorageDrag,
                    EndDrag,
                    DropOnStorageGrid);
                view.gameObject.SetActive(true);
            }

            HideViews(_storageViews, capacity);
        }

        private static void EnsureViews(
            List<StandaloneStorageSlotView> views,
            int count,
            Transform content,
            StandaloneStorageSlotView template,
            string prefix)
        {
            if (content == null || template == null)
                return;

            while (views.Count < count)
            {
                StandaloneStorageSlotView view =
                    Instantiate(template, content, false);
                view.name = $"{prefix}_{views.Count:000}";
                view.gameObject.SetActive(false);
                views.Add(view);
            }
        }

        private static void HideViews(
            List<StandaloneStorageSlotView> views,
            int start)
        {
            for (int i = start; i < views.Count; ++i)
                if (views[i] != null)
                    views[i].gameObject.SetActive(false);
        }

        private void BeginInventoryDrag(int slot)
        {
            if (_busy ||
                !TryGetInventoryItem(slot, out PlayerItemWire item))
            {
                return;
            }

            StandaloneItemDragContext.BeginInventory(slot, item);
            dragGhost?.Show(ClientItemIconResolver.Resolve(item));
            SetOpposingHighlights(_storageViews, true);
        }

        private void BeginStorageDrag(int slot)
        {
            if (_busy ||
                !TryGetStorageItem(slot, out PlayerItemWire item))
            {
                return;
            }

            StandaloneItemDragContext.BeginStorage(slot, item);
            dragGhost?.Show(ClientItemIconResolver.Resolve(item));
            SetOpposingHighlights(_inventoryViews, true);
        }

        private void EndDrag()
        {
            StandaloneItemDragContext.Clear();
            dragGhost?.Hide();
            SetOpposingHighlights(_inventoryViews, false);
            SetOpposingHighlights(_storageViews, false);
        }

        private static void SetOpposingHighlights(
            List<StandaloneStorageSlotView> views,
            bool visible)
        {
            for (int i = 0; i < views.Count; ++i)
            {
                StandaloneStorageSlotView view = views[i];
                if (view != null && view.gameObject.activeSelf)
                    view.SetDropHighlight(visible);
            }
        }

        private void DropOnInventoryGrid(int _)
        {
            if (_busy ||
                StandaloneItemDragContext.Kind !=
                    StandaloneItemDragContext.SourceKind.Storage)
            {
                return;
            }

            int sourceSlot = StandaloneItemDragContext.SlotIndex;
            PlayerItemWire item = StandaloneItemDragContext.Item;
            bool chooseQuantity = ShiftHeld() && item.quantity > 1;
            EndDrag();
            TransferOrChooseQuantity(
                deposit: false,
                sourceSlot,
                item,
                chooseQuantity);
        }

        private void DropOnStorageGrid(int _)
        {
            if (_busy ||
                StandaloneItemDragContext.Kind !=
                    StandaloneItemDragContext.SourceKind.Inventory)
            {
                return;
            }

            int sourceSlot = StandaloneItemDragContext.SlotIndex;
            PlayerItemWire item = StandaloneItemDragContext.Item;
            bool chooseQuantity = ShiftHeld() && item.quantity > 1;
            EndDrag();
            TransferOrChooseQuantity(
                deposit: true,
                sourceSlot,
                item,
                chooseQuantity);
        }

        private void OnInventoryDoubleClicked(int slot)
        {
            if (_busy || !TryGetInventoryItem(slot, out PlayerItemWire item))
                return;

            TransferAsync(
                deposit: true,
                slot,
                Math.Max(1, item.quantity)).Forget();
        }

        private void OnStorageDoubleClicked(int slot)
        {
            if (_busy || !TryGetStorageItem(slot, out PlayerItemWire item))
                return;

            TransferAsync(
                deposit: false,
                slot,
                Math.Max(1, item.quantity)).Forget();
        }

        private void TransferOrChooseQuantity(
            bool deposit,
            int sourceSlot,
            PlayerItemWire item,
            bool chooseQuantity)
        {
            int maximum = Math.Max(1, item.quantity);
            if (chooseQuantity && quantityPicker != null)
            {
                string verb = deposit ? "Store" : "Take";
                string name = ItemName(item);
                quantityPicker.Open(
                    $"{verb} {name} — quantity",
                    maximum,
                    quantity =>
                        TransferAsync(
                            deposit,
                            sourceSlot,
                            quantity).Forget());
                return;
            }

            TransferAsync(
                deposit,
                sourceSlot,
                maximum).Forget();
        }

        private async UniTaskVoid TransferAsync(
            bool deposit,
            int sourceSlot,
            int requestedQuantity)
        {
            if (_manager == null || _busy)
                return;

            PlayerItemWire current;
            bool exists = deposit
                ? TryGetInventoryItem(sourceSlot, out current)
                : TryGetStorageItem(sourceSlot, out current);

            if (!exists || current.quantity <= 0)
            {
                Render();
                return;
            }

            // Re-clamp immediately before sending so a picker that stayed open while
            // authoritative state changed cannot request more than the latest local stack.
            int quantity = Math.Clamp(
                requestedQuantity,
                1,
                Math.Max(1, current.quantity));

            _busy = true;
            if (statusText != null)
                statusText.text = deposit
                    ? $"Depositing x{quantity}..."
                    : $"Withdrawing x{quantity}...";

            try
            {
                SocialEconomyMutationResponseMessage result = deposit
                    ? await _manager.RequestDepositToStorageAsync(
                        sourceSlot,
                        quantity)
                    : await _manager.RequestWithdrawFromStorageAsync(
                        sourceSlot,
                        quantity);

                await UniTask.SwitchToMainThread();

                if (!result.success)
                {
                    string error = string.IsNullOrWhiteSpace(result.error)
                        ? "Storage transfer was rejected."
                        : result.error;
                    if (statusText != null)
                        statusText.text = error;
                    ClientGameplayUIRoot.Instance?.PresentLocalSystemMessage(
                        "STORAGE: " + error);
                }
                else if (statusText != null)
                {
                    statusText.text =
                        "Storage transfer submitted. Waiting for authoritative state...";
                }
            }
            finally
            {
                _busy = false;
                if (IsOpen)
                    Render();
            }
        }

        private bool TryGetInventoryItem(
            int slot,
            out PlayerItemWire item)
        {
            PlayerItemWire[] values =
                _manager != null
                    ? (_manager.LatestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>())
                    : Array.Empty<PlayerItemWire>();

            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].inventorySlot == slot &&
                    values[i].quantity > 0)
                {
                    item = values[i];
                    return true;
                }
            }

            item = default;
            return false;
        }

        private bool TryGetStorageItem(
            int slot,
            out PlayerItemWire item)
        {
            PlayerItemWire[] values =
                _manager != null
                    ? (_manager.LatestStorage.items ?? Array.Empty<PlayerItemWire>())
                    : Array.Empty<PlayerItemWire>();

            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].inventorySlot == slot &&
                    values[i].quantity > 0)
                {
                    item = values[i];
                    return true;
                }
            }

            item = default;
            return false;
        }

        private static bool ShiftHeld() =>
            UnityEngine.Input.GetKey(KeyCode.LeftShift) ||
            UnityEngine.Input.GetKey(KeyCode.RightShift);

        private static string ItemName(PlayerItemWire item)
        {
            if (!string.IsNullOrWhiteSpace(item.displayName))
                return item.displayName;
            if (!string.IsNullOrWhiteSpace(item.definitionId))
                return item.definitionId;
            return item.itemDataId != 0
                ? $"Item {item.itemDataId}"
                : "Item";
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject windowRoot,
            Transform authoredInventoryContent,
            StandaloneStorageSlotView authoredInventoryTemplate,
            Transform authoredStorageContent,
            StandaloneStorageSlotView authoredStorageTemplate,
            StandaloneHudTooltipPanel authoredTooltip,
            StandaloneDragGhost authoredDragGhost,
            StandaloneStorageQuantityPicker authoredQuantityPicker,
            Text authoredStatusText)
        {
            storageWindowRoot = windowRoot;
            inventoryContent = authoredInventoryContent;
            inventorySlotTemplate = authoredInventoryTemplate;
            storageContent = authoredStorageContent;
            storageSlotTemplate = authoredStorageTemplate;
            tooltip = authoredTooltip;
            dragGhost = authoredDragGhost;
            quantityPicker = authoredQuantityPicker;
            statusText = authoredStatusText;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
