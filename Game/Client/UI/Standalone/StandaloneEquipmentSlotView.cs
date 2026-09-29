using System;
using Game.Client.UI.PlayerItems;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Compact authored equipment slot presentation with drag/drop support.</summary>
    public sealed class StandaloneEquipmentSlotView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Image icon;
        [SerializeField] private Text slotNameText;
        [SerializeField] private Text itemNameText;
        [SerializeField] private Image dropHighlight;

        private string _slotId;
        private string _displayName;
        private bool _hasItem;
        private PlayerItemWire _item;
        private Action<string> _clicked;
        private Action<string> _doubleClicked;
        private Action<string> _dragStarted;
        private Action _dragEnded;
        private Action<string> _droppedOn;
        private StandaloneHudTooltipPanel _tooltip;

        public string SlotId => _slotId;
        public bool HasItem => _hasItem;
        public PlayerItemWire Item => _item;

        public void Bind(
            string slotId,
            string displayName,
            bool hasItem,
            PlayerItemWire item,
            Action<string> clicked,
            StandaloneHudTooltipPanel tooltip,
            Action<string> dragStarted = null,
            Action dragEnded = null,
            Action<string> droppedOn = null,
            Action<string> doubleClicked = null)
        {
            _slotId = slotId ?? string.Empty;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? _slotId : displayName;
            _hasItem = hasItem;
            _item = item;
            _clicked = clicked;
            _doubleClicked = doubleClicked;
            _tooltip = tooltip;
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;
            SetDropHighlight(false, false);
            RefreshPresentation();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;

            // A normal click remains available for placing a selected inventory item into
            // this slot. An equipped item is removed only by an actual Unity double-click.
            if (_hasItem && eventData.clickCount >= 2 && _doubleClicked != null)
            {
                _doubleClicked(_slotId);
                return;
            }

            _clicked?.Invoke(_slotId);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_hasItem)
                _tooltip?.ShowItem(_item, _displayName);
            else
                _tooltip?.ShowText(_displayName, "Empty equipment slot");
        }

        public void OnPointerExit(PointerEventData eventData) => _tooltip?.Hide();

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_hasItem || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            _dragStarted?.Invoke(_slotId);
        }

        // Required by Unity EventSystem so begin/end drag events are dispatched.
        public void OnDrag(PointerEventData eventData) { }

        public void OnEndDrag(PointerEventData eventData)
        {
            _dragEnded?.Invoke();
        }

        public void OnDrop(PointerEventData eventData) => _droppedOn?.Invoke(_slotId);

        public void SetDropHighlight(bool active, bool valid)
        {
            if (dropHighlight == null)
                return;
            dropHighlight.enabled = active;
            dropHighlight.color = valid
                ? new Color32(74, 176, 107, 100)
                : new Color32(176, 74, 74, 75);
        }

        private void RefreshPresentation()
        {
            if (slotNameText != null)
                slotNameText.text = _displayName;
            if (itemNameText != null)
                itemNameText.text = _hasItem ? _item.displayName : "Empty";
            if (icon != null)
            {
                icon.sprite = _hasItem ? ClientItemIconResolver.Resolve(_item) : null;
                icon.enabled = _hasItem && icon.sprite != null;
                icon.preserveAspect = true;
            }
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Button authoredButton, Image itemIcon, Text slotName, Text itemName)
        {
            ConfigureForEditor(authoredButton, itemIcon, slotName, itemName, null);
        }

        public void ConfigureForEditor(Button authoredButton, Image itemIcon, Text slotName, Text itemName, Image authoredDropHighlight)
        {
            button = authoredButton;
            icon = itemIcon;
            slotNameText = slotName;
            itemNameText = itemName;
            dropHighlight = authoredDropHighlight;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
