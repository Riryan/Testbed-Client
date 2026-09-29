using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored reusable row for one data-driven progression track.</summary>
    public sealed class StandaloneProgressionRow : MonoBehaviour
    {
        [SerializeField] private Text kindText;
        [SerializeField] private Text nameText;
        [SerializeField] private Text valueText;

        public void Bind(string kind, string displayName, int value, int maximum)
        {
            if (kindText != null) kindText.text = (kind ?? string.Empty).ToUpperInvariant();
            if (nameText != null) nameText.text = displayName ?? string.Empty;
            if (valueText != null) valueText.text = $"{value:N0} / {maximum:N0}";
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Text authoredKind, Text authoredName, Text authoredValue)
        {
            kindText = authoredKind;
            nameText = authoredName;
            valueText = authoredValue;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
