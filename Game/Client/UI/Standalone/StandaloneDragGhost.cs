using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored drag visual reused by Inventory and Equipment. No runtime hierarchy construction.</summary>
    public sealed class StandaloneDragGhost : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private Image icon;
        [SerializeField] private Vector2 cursorOffset = new Vector2(18f, -18f);

        public bool Visible => root != null && root.gameObject.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            Hide();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (!Visible)
                return;
            root.position = (Vector2)UnityEngine.Input.mousePosition + cursorOffset;
#endif
        }

        public void Show(Sprite sprite)
        {
#if !UNITY_SERVER
            if (root == null)
                return;
            if (icon != null)
            {
                icon.sprite = sprite;
                icon.enabled = sprite != null;
                icon.preserveAspect = true;
            }
            root.position = (Vector2)UnityEngine.Input.mousePosition + cursorOffset;
            root.gameObject.SetActive(true);
#endif
        }

        public void Hide()
        {
#if !UNITY_SERVER
            if (root != null)
                root.gameObject.SetActive(false);
            if (icon != null)
                icon.sprite = null;
#endif
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(RectTransform authoredRoot, Image authoredIcon)
        {
            root = authoredRoot;
            icon = authoredIcon;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
