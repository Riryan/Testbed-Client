using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorColorChannelView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private RectTransform swatchContent;

        public Text Label => label;
        public RectTransform SwatchContent => swatchContent;
    }
}
