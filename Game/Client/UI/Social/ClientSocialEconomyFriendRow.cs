using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Social
{
    [DisallowMultipleComponent]
    public sealed class ClientSocialEconomyFriendRow : MonoBehaviour
    {
        [SerializeField] private Text nameText;
        [SerializeField] private Text presenceText;
        [SerializeField] private Button removeButton;

#if UNITY_EDITOR
        public void ConfigureForEditor(Text nameLabel, Text presenceLabel, Button remove)
        {
            nameText = nameLabel;
            presenceText = presenceLabel;
            removeButton = remove;
        }
#endif

        public void Bind(long characterId, string displayName, bool online, Action<long> remove)
        {
            if (nameText != null) nameText.text = string.IsNullOrWhiteSpace(displayName) ? $"Character {characterId}" : displayName;
            if (presenceText != null) presenceText.text = online ? "ONLINE" : "OFFLINE";
            if (removeButton == null) return;
            removeButton.onClick.RemoveAllListeners();
            removeButton.onClick.AddListener(() => remove?.Invoke(characterId));
        }
    }
}
