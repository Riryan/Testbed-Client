using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    /// <summary>
    /// Serialized dropdown shell used by the Outfit Builder.
    /// Option rows remain data-driven, but the popup is clipped and wheel-scrollable so
    /// large server catalogs do not spill across the entire authoring UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringDropdown : MonoBehaviour, IScrollHandler
    {
        [SerializeField] private Button openButton;
        [SerializeField] private Text selectedText;
        [SerializeField] private GameObject optionsPanel;
        [SerializeField] private RectTransform optionsContainer;
        [SerializeField] private Button optionTemplate;

        private readonly List<Button> _spawnedOptions = new List<Button>();
        private readonly List<string> _labels = new List<string>();
        private int _selectedIndex = -1;
        private float _scrollOffset;
        private Canvas _popupCanvas;

        private const float RowHeight = 34f;
        private const float TopPadding = 6f;
        private const float MaxPopupHeight = 260f;

        public int SelectedIndex => _selectedIndex;
        public event Action<int> SelectionChanged;

        private void Awake()
        {
            if (openButton != null)
            {
                openButton.onClick.RemoveAllListeners();
                openButton.onClick.AddListener(ToggleOpen);
            }

            if (optionsPanel != null)
            {
                if (optionsPanel.GetComponent<RectMask2D>() == null)
                    optionsPanel.AddComponent<RectMask2D>();

                _popupCanvas = optionsPanel.GetComponent<Canvas>();
                if (_popupCanvas == null)
                    _popupCanvas = optionsPanel.AddComponent<Canvas>();

                _popupCanvas.overrideSorting = true;
                _popupCanvas.sortingOrder = 5000;

                if (optionsPanel.GetComponent<GraphicRaycaster>() == null)
                    optionsPanel.AddComponent<GraphicRaycaster>();

                RectTransform popupRect = optionsPanel.transform as RectTransform;
                if (popupRect != null)
                {
                    Vector2 size = popupRect.sizeDelta;
                    size.y = MaxPopupHeight;
                    popupRect.sizeDelta = size;
                }

                optionsPanel.SetActive(false);
            }

            if (optionTemplate != null)
                optionTemplate.gameObject.SetActive(false);

            ConfigureContainerForScrolling();
        }

        public void SetOptions(IReadOnlyList<string> labels, int selectedIndex)
        {
            ClearSpawned();
            _labels.Clear();

            if (labels != null)
            {
                for (int i = 0; i < labels.Count; ++i)
                    _labels.Add(labels[i] ?? string.Empty);
            }

            ConfigureContainerForScrolling();

            if (optionTemplate != null && optionsContainer != null)
            {
                for (int i = 0; i < _labels.Count; ++i)
                {
                    int captured = i;
                    Button clone = Instantiate(optionTemplate, optionsContainer);
                    clone.name = $"Option {i + 1:00}";
                    clone.gameObject.SetActive(true);

                    RectTransform rect = clone.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        rect.anchorMin = new Vector2(0f, 1f);
                        rect.anchorMax = new Vector2(1f, 1f);
                        rect.pivot = new Vector2(0.5f, 1f);
                        rect.anchoredPosition = new Vector2(0f, -TopPadding - i * RowHeight);
                        rect.sizeDelta = new Vector2(-12f, 30f);
                    }

                    Text label = clone.GetComponentInChildren<Text>(true);
                    if (label != null)
                        label.text = _labels[i];

                    clone.onClick.RemoveAllListeners();
                    clone.onClick.AddListener(() => Select(captured));
                    _spawnedOptions.Add(clone);
                }
            }

            if (optionsContainer != null)
            {
                optionsContainer.sizeDelta = new Vector2(
                    optionsContainer.sizeDelta.x,
                    Mathf.Max(40f, 12f + _labels.Count * RowHeight));
            }

            _scrollOffset = 0f;
            ApplyScrollOffset();
            Select(selectedIndex, close: true, notify: true);
        }

        public void Select(int index, bool close = true)
        {
            Select(index, close, notify: true);
        }

        public void Select(int index, bool close, bool notify)
        {
            int next = index >= 0 && index < _labels.Count ? index : -1;
            bool changed = next != _selectedIndex;
            _selectedIndex = next;

            if (selectedText != null)
                selectedText.text = _selectedIndex >= 0 ? _labels[_selectedIndex] : "No options";

            if (close && optionsPanel != null)
                optionsPanel.SetActive(false);

            if (notify && (changed || _selectedIndex >= 0))
                SelectionChanged?.Invoke(_selectedIndex);
        }

        public void Close()
        {
            if (optionsPanel != null)
                optionsPanel.SetActive(false);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (optionsPanel == null || !optionsPanel.activeSelf || optionsContainer == null)
                return;

            RectTransform panelRect = optionsPanel.transform as RectTransform;
            float viewportHeight = panelRect != null
                ? Mathf.Min(MaxPopupHeight, Mathf.Max(1f, panelRect.rect.height))
                : MaxPopupHeight;
            float maxOffset = Mathf.Max(0f, optionsContainer.rect.height - viewportHeight + 10f);

            _scrollOffset = Mathf.Clamp(
                _scrollOffset - eventData.scrollDelta.y * 34f,
                0f,
                maxOffset);

            ApplyScrollOffset();
        }

        private void ToggleOpen()
        {
            if (optionsPanel == null)
                return;

            bool opening = !optionsPanel.activeSelf;
            if (opening)
            {
                transform.SetAsLastSibling();
                if (_popupCanvas != null)
                {
                    _popupCanvas.overrideSorting = true;
                    _popupCanvas.sortingOrder = 5000;
                }
                _scrollOffset = 0f;
                ApplyScrollOffset();
            }

            optionsPanel.SetActive(opening);
        }

        private void ConfigureContainerForScrolling()
        {
            if (optionsContainer == null)
                return;

            optionsContainer.anchorMin = new Vector2(0f, 1f);
            optionsContainer.anchorMax = new Vector2(1f, 1f);
            optionsContainer.pivot = new Vector2(0.5f, 1f);
            optionsContainer.anchoredPosition = new Vector2(0f, -4f);
        }

        private void ApplyScrollOffset()
        {
            if (optionsContainer == null)
                return;

            Vector2 position = optionsContainer.anchoredPosition;
            position.y = -4f + _scrollOffset;
            optionsContainer.anchoredPosition = position;
        }

        private void ClearSpawned()
        {
            for (int i = 0; i < _spawnedOptions.Count; ++i)
            {
                if (_spawnedOptions[i] != null)
                    Destroy(_spawnedOptions[i].gameObject);
            }

            _spawnedOptions.Clear();
        }
    }
}
