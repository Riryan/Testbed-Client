using System;
using Game.Client.UI.PlayerItems;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Inventory-style icon slot used by the authored Storage window.
    /// It is presentation-only: drag/drop callbacks route into the existing storage requests.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneStorageSlotView : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IDropHandler
    {
        [SerializeField] private Image icon;
        [SerializeField] private Text quantityText;
        [SerializeField] private Image dropHighlight;

        private int _slotIndex;
        private bool _hasItem;
        private PlayerItemWire _item;
        private string _tooltipContext = string.Empty;
        private StandaloneHudTooltipPanel _tooltip;
        private Action<int> _doubleClicked;
        private Action<int> _dragStarted;
        private Action _dragEnded;
        private Action<int> _droppedOn;

        public int SlotIndex => _slotIndex;
        public bool HasItem => _hasItem;
        public PlayerItemWire Item => _item;

        public void Bind(
            int slotIndex,
            bool hasItem,
            PlayerItemWire item,
            string tooltipContext,
            StandaloneHudTooltipPanel tooltip,
            Action<int> doubleClicked,
            Action<int> dragStarted,
            Action dragEnded,
            Action<int> droppedOn)
        {
            _slotIndex = slotIndex;
            _hasItem = hasItem;
            _item = item;
            _tooltipContext = tooltipContext ?? string.Empty;
            _tooltip = tooltip;
            _doubleClicked = doubleClicked;
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;

            if (dropHighlight != null)
                dropHighlight.enabled = false;

            RefreshPresentation();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_hasItem)
                _tooltip?.ShowItem(_item, _tooltipContext);
            else
                _tooltip?.ShowText(
                    string.IsNullOrWhiteSpace(_tooltipContext) ? "Storage" : _tooltipContext,
                    "Empty slot");
        }

        public void OnPointerExit(PointerEventData eventData) => _tooltip?.Hide();

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_hasItem ||
                eventData == null ||
                eventData.button != PointerEventData.InputButton.Left ||
                eventData.clickCount < 2)
            {
                return;
            }

            _doubleClicked?.Invoke(_slotIndex);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_hasItem ||
                eventData == null ||
                eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _dragStarted?.Invoke(_slotIndex);
        }

        // Unity requires IDragHandler for begin/end drag dispatch.
        public void OnDrag(PointerEventData eventData) { }

        public void OnEndDrag(PointerEventData eventData) => _dragEnded?.Invoke();

        public void OnDrop(PointerEventData eventData) => _droppedOn?.Invoke(_slotIndex);

        public void SetDropHighlight(bool visible)
        {
            if (dropHighlight != null)
                dropHighlight.enabled = visible;
        }

        private void RefreshPresentation()
        {
            if (icon != null)
            {
                icon.sprite = _hasItem ? ClientItemIconResolver.Resolve(_item) : null;
                icon.enabled = _hasItem && icon.sprite != null;
                icon.preserveAspect = true;
            }

            if (quantityText != null)
                quantityText.text = _hasItem && _item.quantity > 1
                    ? _item.quantity.ToString()
                    : string.Empty;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Image itemIcon, Text quantity, Image highlight)
        {
            icon = itemIcon;
            quantityText = quantity;
            dropHighlight = highlight;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
