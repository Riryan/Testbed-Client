using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Authored reusable row for one data-driven progression track.
    /// Self-resolves its authored Text children if older prefab data lost serialized references.
    /// </summary>
    public sealed class StandaloneProgressionRow : MonoBehaviour
    {
        [SerializeField] private Text kindText;
        [SerializeField] private Text nameText;
        [SerializeField] private Text valueText;

        public void Bind(string kind, string displayName, int value, int maximum)
        {
            EnsureReferences();

            if (kindText != null)
                kindText.text = (kind ?? string.Empty).ToUpperInvariant();
            if (nameText != null)
                nameText.text = displayName ?? string.Empty;
            if (valueText != null)
                valueText.text = $"{value:N0} / {maximum:N0}";
        }

        private void EnsureReferences()
        {
            if (kindText != null && nameText != null && valueText != null)
                return;

            Text[] texts = GetComponentsInChildren<Text>(true);

            for (int i = 0; i < texts.Length; ++i)
            {
                Text text = texts[i];
                if (text == null)
                    continue;

                string n = text.gameObject.name;
                if (kindText == null && string.Equals(n, "Kind", System.StringComparison.OrdinalIgnoreCase))
                    kindText = text;
                else if (nameText == null && string.Equals(n, "Name", System.StringComparison.OrdinalIgnoreCase))
                    nameText = text;
                else if (valueText == null && string.Equals(n, "Value", System.StringComparison.OrdinalIgnoreCase))
                    valueText = text;
            }

            // Compatibility with any previously-authored three-column row.
            if (texts.Length >= 3)
            {
                if (kindText == null) kindText = texts[0];
                if (nameText == null) nameText = texts[1];
                if (valueText == null) valueText = texts[2];
            }
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
