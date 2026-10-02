using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringSlotRow : MonoBehaviour
    {
        [SerializeField] private ushort primarySlotId;
        [SerializeField] private Text labelText;
        [SerializeField] private Text valueText;
        [SerializeField] private Button previousButton;
        [SerializeField] private Button nextButton;

        public ushort PrimarySlotId => primarySlotId;

        public void Bind(Action previous, Action next)
        {
            if (previousButton != null)
            {
                previousButton.onClick.RemoveAllListeners();
                if (previous != null) previousButton.onClick.AddListener(() => previous());
            }
            if (nextButton != null)
            {
                nextButton.onClick.RemoveAllListeners();
                if (next != null) nextButton.onClick.AddListener(() => next());
            }
        }

        public void SetValue(string value)
        {
            if (valueText != null)
                valueText.text = value ?? string.Empty;
        }

        public void SetAvailable(bool available)
        {
            if (previousButton != null) previousButton.interactable = available;
            if (nextButton != null) nextButton.interactable = available;
            if (!available && valueText != null) valueText.text = "Unavailable";
        }

        public void SetLabel(string value)
        {
            if (labelText != null)
                labelText.text = value ?? string.Empty;
        }
    }
}
