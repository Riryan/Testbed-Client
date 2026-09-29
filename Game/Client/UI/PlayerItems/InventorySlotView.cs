using System;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.PlayerItems
{
    /// <summary>Editable presentation for one fixed inventory slot.</summary>
    public sealed class InventorySlotView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Text label;
        [SerializeField] private Image icon;

        private int _slotIndex;
        private Action<int> _clicked;
        private Action<int> _dragStarted;
        private Action _dragEnded;
        private Action<int> _droppedOn;
        private PlayerItemWire _item;
        private bool _hasItem;
        private bool _selected;
        private CanvasGroup _canvasGroup;

        public int SlotIndex => _slotIndex;
        public bool HasItem => _hasItem;
        public PlayerItemWire Item => _item;

        private void Awake()
        {
            AutoWire();
            button?.onClick.AddListener(OnClicked);
        }

        private void OnDestroy() => button?.onClick.RemoveListener(OnClicked);

        public void Bind(
            int slotIndex,
            bool hasItem,
            PlayerItemWire item,
            bool selected,
            Action<int> clicked,
            Action<int> dragStarted = null,
            Action dragEnded = null,
            Action<int> droppedOn = null)
        {
            AutoWire();
            _slotIndex = slotIndex;
            _hasItem = hasItem;
            _item = item;
            _selected = selected;
            _clicked = clicked;
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;
            RefreshPresentation();
        }

        private void OnClicked() => _clicked?.Invoke(_slotIndex);

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
            if (label != null)
            {
                string prefix = _selected ? "> " : string.Empty;
                if (!_hasItem)
                {
                    label.text = $"{prefix}{_slotIndex + 1:00}  [ Empty ]";
                }
                else
                {
                    string quantity = _item.quantity > 1 ? $" x{_item.quantity}" : string.Empty;
                    string durability = _item.maxDurability > 0
                        ? $"\nDurability {_item.durability}/{_item.maxDurability}"
                        : string.Empty;
                    label.text = $"{prefix}{_slotIndex + 1:00}  {_item.displayName}{quantity}{durability}";
                }
            }

            if (icon != null)
            {
                icon.enabled = _hasItem;
                icon.sprite = _hasItem ? ClientItemIconResolver.Resolve(_item) : null;
                icon.preserveAspect = true;
            }
        }

        private void EnsureCanvasGroup()
        {
            if (_canvasGroup == null)
                _canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

        private void AutoWire()
        {
            if (button == null) button = GetComponent<Button>();
            if (label == null) label = GetComponentInChildren<Text>(true);
            if (icon == null)
            {
                Transform child = transform.Find("ItemIcon");
                if (child != null) icon = child.GetComponent<Image>();
            }
            if (icon == null && Application.isPlaying)
            {
                GameObject go = new GameObject("ItemIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                icon = go.GetComponent<Image>();
                icon.raycastTarget = false;
                RectTransform rect = icon.rectTransform;
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(42f, 42f);
                rect.anchoredPosition = new Vector2(7f, 0f);
                if (label != null)
                {
                    RectTransform textRect = label.rectTransform;
                    textRect.offsetMin = new Vector2(Mathf.Max(textRect.offsetMin.x, 54f), textRect.offsetMin.y);
                }
            }
        }
    }
}
