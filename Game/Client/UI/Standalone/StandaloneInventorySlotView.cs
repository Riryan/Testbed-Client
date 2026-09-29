using System;
using Game.Client.UI.PlayerItems;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored inventory slot presentation. Layout is owned by the prefab GridLayoutGroup.</summary>
    public sealed class StandaloneInventorySlotView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Image icon;
        [SerializeField] private Text slotText;
        [SerializeField] private Text quantityText;
        [SerializeField] private Image selectedFrame;

        private int _slotIndex;
        private bool _hasItem;
        private bool _selected;
        private PlayerItemWire _item;
        private Action<int> _clicked;
        private Action<int> _dragStarted;
        private Action _dragEnded;
        private Action<int> _droppedOn;
        private StandaloneHudTooltipPanel _tooltip;
        private CanvasGroup _canvasGroup;

        public int SlotIndex => _slotIndex;
        public bool HasItem => _hasItem;
        public PlayerItemWire Item => _item;

        private void Awake() => button?.onClick.AddListener(OnClicked);
        private void OnDestroy() => button?.onClick.RemoveListener(OnClicked);

        public void Bind(
            int slotIndex,
            bool hasItem,
            PlayerItemWire item,
            bool selected,
            Action<int> clicked,
            StandaloneHudTooltipPanel tooltip,
            Action<int> dragStarted = null,
            Action dragEnded = null,
            Action<int> droppedOn = null)
        {
            _slotIndex = slotIndex;
            _hasItem = hasItem;
            _item = item;
            _selected = selected;
            _clicked = clicked;
            _tooltip = tooltip;
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;
            RefreshPresentation();
        }

        private void OnClicked() => _clicked?.Invoke(_slotIndex);

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_hasItem)
                _tooltip?.ShowItem(_item, "Inventory");
            else
                _tooltip?.ShowText("Inventory", "Empty slot");
        }

        public void OnPointerExit(PointerEventData eventData) => _tooltip?.Hide();

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_hasItem || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            _dragStarted?.Invoke(_slotIndex);
        }

        // Required by Unity EventSystem so begin/end drag events are dispatched.
        public void OnDrag(PointerEventData eventData) { }

        public void OnEndDrag(PointerEventData eventData)
        {
            _dragEnded?.Invoke();
        }

        public void OnDrop(PointerEventData eventData) => _droppedOn?.Invoke(_slotIndex);

        private void RefreshPresentation()
        {
            // Inventory slots are intentionally unnumbered. The slot index remains internal
            // and authoritative requests still use the same stable inventory slot integer.
            if (slotText != null)
                slotText.text = string.Empty;

            if (selectedFrame != null)
                selectedFrame.enabled = _selected;

            if (icon != null)
            {
                icon.sprite = _hasItem ? ClientItemIconResolver.Resolve(_item) : null;
                icon.enabled = _hasItem && icon.sprite != null;
                icon.preserveAspect = true;
            }

            if (quantityText != null)
                quantityText.text = _hasItem && _item.quantity > 1 ? _item.quantity.ToString() : string.Empty;
        }

        private void EnsureCanvasGroup()
        {
            if (_canvasGroup == null)
                _canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Button authoredButton, Image itemIcon, Text slot, Text quantity, Image selection)
        {
            button = authoredButton;
            icon = itemIcon;
            slotText = slot;
            quantityText = quantity;
            selectedFrame = selection;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
