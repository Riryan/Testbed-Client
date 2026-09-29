using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorColorSwatchView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image swatchImage;
        [SerializeField] private Text label;
        [SerializeField] private Outline selectedOutline;

        public Button Button => button;

        public void Bind(Color color, string text, bool selected, bool showColor)
        {
            if (swatchImage != null)
            {
                swatchImage.color = color;
                swatchImage.enabled = showColor;
            }
            if (label != null)
            {
                label.text = text ?? string.Empty;
                label.gameObject.SetActive(!string.IsNullOrEmpty(text));
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
