using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorMorphRowView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Text valueText;
        [SerializeField] private Slider slider;

        public Text Label => label;
        public Text ValueText => valueText;
        public Slider Slider => slider;
    }
}
