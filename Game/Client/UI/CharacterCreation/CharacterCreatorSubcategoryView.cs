using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorSubcategoryView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Text label;
        [SerializeField] private Outline selectedOutline;

        public Button Button => button;

        public void Bind(string text, bool selected)
        {
            if (label != null)
                label.text = text ?? string.Empty;
            SetSelected(selected);
        }

        public void SetSelected(bool selected)
        {
            if (selectedOutline != null)
                selectedOutline.enabled = selected;
        }
    }
}
