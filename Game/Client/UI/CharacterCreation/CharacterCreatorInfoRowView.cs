using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    public sealed class CharacterCreatorInfoRowView : MonoBehaviour
    {
        [SerializeField] private Text messageText;
        public Text MessageText => messageText;
    }
}
