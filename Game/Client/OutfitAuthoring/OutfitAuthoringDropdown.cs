using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    /// <summary>
    /// Small scene-authored dropdown shell. Only data-driven option rows are cloned at runtime;
    /// the visible field, popup panel and row template remain serialized scene UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringDropdown : MonoBehaviour
    {
        [SerializeField] private Button openButton;
        [SerializeField] private Text selectedText;
        [SerializeField] private GameObject optionsPanel;
        [SerializeField] private RectTransform optionsContainer;
        [SerializeField] private Button optionTemplate;

        private readonly List<Button> _spawnedOptions = new List<Button>();
        private readonly List<string> _labels = new List<string>();
        private int _selectedIndex = -1;

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
                optionsPanel.SetActive(false);
            if (optionTemplate != null)
                optionTemplate.gameObject.SetActive(false);
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
                        rect.anchoredPosition = new Vector2(0f, -6f - i * 34f);
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
                optionsContainer.sizeDelta = new Vector2(
                    optionsContainer.sizeDelta.x,
                    Mathf.Max(40f, 12f + _labels.Count * 34f));

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

        private void ToggleOpen()
        {
            if (optionsPanel == null)
                return;
            bool opening = !optionsPanel.activeSelf;
            if (opening)
                transform.SetAsLastSibling();
            optionsPanel.SetActive(opening);
        }

        private void ClearSpawned()
        {
            for (int i = 0; i < _spawnedOptions.Count; ++i)
                if (_spawnedOptions[i] != null)
                    Destroy(_spawnedOptions[i].gameObject);
            _spawnedOptions.Clear();
        }
    }
}
