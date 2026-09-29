using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorMeshOptionView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image icon;
        [SerializeField] private Text fallbackLabel;
        [SerializeField] private Outline selectedOutline;

        public Button Button => button;

        public void Bind(Sprite sprite, string fallbackText, bool selected)
        {
            if (icon != null)
            {
                icon.sprite = sprite;
                icon.enabled = sprite != null;
            }

            if (fallbackLabel != null)
            {
                fallbackLabel.text = sprite == null ? fallbackText ?? string.Empty : string.Empty;
                fallbackLabel.gameObject.SetActive(sprite == null);
            }

            SetSelected(selected);
        }

        public void SetSelected(bool selected)
        {
            if (selectedOutline != null)
                selectedOutline.enabled = selected;
        }
    }
}
