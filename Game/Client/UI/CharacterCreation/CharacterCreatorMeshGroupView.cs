using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorMeshGroupView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private RectTransform optionContent;

        public Text Label => label;
        public RectTransform OptionContent => optionContent;
    }
}
