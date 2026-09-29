using System;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.PlayerItems
{
    [DisallowMultipleComponent]
    public sealed class WorldLootRowView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Button takeButton;
        private int _entryIndex;
        private Action<int> _take;

        public void ConfigureForEditor(Text rowLabel, Button button)
        {
            label = rowLabel;
            takeButton = button;
        }

        public void Bind(WorldLootEntryWire entry, Action<int> take)
        {
            _entryIndex = entry.entryIndex;
            _take = take;
            if (label != null)
                label.text = entry.quantity > 1
                    ? $"{entry.displayName}  x{entry.quantity}"
                    : entry.displayName;
            if (takeButton != null)
            {
                takeButton.onClick.RemoveAllListeners();
                takeButton.interactable = entry.quantity > 0;
                takeButton.onClick.AddListener(OnTakeClicked);
            }
            gameObject.SetActive(entry.quantity > 0);
        }

        private void OnTakeClicked() => _take?.Invoke(_entryIndex);
    }
}
