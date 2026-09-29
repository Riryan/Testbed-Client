using System;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterSelect
{
    /// <summary>
    /// One editable character-list slot. It is presentation only: the slot stores the
    /// wire summary it was given and emits a local selection callback.
    /// </summary>
    public sealed class CharacterSelectSlotView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Text nameText;
        [SerializeField] private Text mapText;
        [SerializeField] private GameObject selectedMarker;

        private long _characterId;
        private Action<long> _onSelected;

        public long CharacterId => _characterId;
        public bool HasCharacter => _characterId > 0;

        private void Awake()
        {
            if (button != null)
                button.onClick.AddListener(OnClicked);
        }

        private void OnDestroy()
        {
            if (button != null)
                button.onClick.RemoveListener(OnClicked);
        }

        public void Bind(CharacterSessionCharacterSummary summary, Action<long> onSelected)
        {
            _characterId = summary.characterId;
            _onSelected = onSelected;

            if (nameText != null)
                nameText.text = string.IsNullOrWhiteSpace(summary.name) ? $"Character {summary.characterId}" : summary.name;
            if (mapText != null)
                mapText.text = string.IsNullOrWhiteSpace(summary.mapId) ? "Unknown map" : summary.mapId;
            if (button != null)
                button.interactable = _characterId > 0;

            gameObject.SetActive(true);
            SetSelected(false);
        }

        public void Clear()
        {
            _characterId = 0;
            _onSelected = null;
            if (selectedMarker != null)
                selectedMarker.SetActive(false);
            gameObject.SetActive(false);
        }

        public void SetSelected(bool selected)
        {
            if (selectedMarker != null)
                selectedMarker.SetActive(selected && HasCharacter);
        }

        private void OnClicked()
        {
            if (HasCharacter)
                _onSelected?.Invoke(_characterId);
        }
    }
}
