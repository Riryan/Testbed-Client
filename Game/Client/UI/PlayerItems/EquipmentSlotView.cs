using System;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.PlayerItems
{
    /// <summary>Editable presentation for one server-defined equipment slot.</summary>
    public sealed class EquipmentSlotView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Text label;
        [SerializeField] private Image icon;

        private string _slotId;
        private string _displayName;
        private bool _hasItem;
        private PlayerItemWire _item;
        private Action<string> _clicked;
        private Action<string> _dragStarted;
        private Action _dragEnded;
        private Action<string> _droppedOn;
        private CanvasGroup _canvasGroup;

        public string SlotId => _slotId;
        public bool HasItem => _hasItem;

        private void Awake()
        {
            AutoWire();
            button?.onClick.AddListener(OnClicked);
        }

        private void OnDestroy() => button?.onClick.RemoveListener(OnClicked);

        public void Bind(
            string slotId,
            string displayName,
            bool hasItem,
            PlayerItemWire item,
            Action<string> clicked,
            Action<string> dragStarted = null,
            Action dragEnded = null,
            Action<string> droppedOn = null)
        {
            AutoWire();
            if (button != null)
            {
                button.onClick.RemoveListener(OnClicked);
                button.onClick.AddListener(OnClicked);
            }

            _slotId = slotId ?? string.Empty;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? _slotId : displayName;
            _hasItem = hasItem;
            _item = item;
            _clicked = clicked;
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;
            RefreshPresentation();
        }

        private void OnClicked() => _clicked?.Invoke(_slotId);

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

        private void RefreshPresentation()
        {
            if (label != null)
            {
                string value = _hasItem ? _item.displayName : "[ Empty ]";
                string durability = _hasItem && _item.maxDurability > 0
                    ? $"\nDurability {_item.durability}/{_item.maxDurability}"
                    : string.Empty;
                label.text = $"{_displayName}\n{value}{durability}";
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
