using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Client.UI.Standalone
{
    /// <summary>Routes authored HUD hover help into the one fixed HUD tooltip panel.</summary>
    public sealed class StandaloneHudTooltipTarget : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private StandaloneHudTooltipPanel tooltip;
        [SerializeField] private string title;
        [SerializeField, TextArea] private string body;

        public void OnPointerEnter(PointerEventData eventData) => tooltip?.ShowText(title, body);
        public void OnPointerExit(PointerEventData eventData) => tooltip?.Hide();

#if UNITY_EDITOR
        public void ConfigureForEditor(StandaloneHudTooltipPanel authoredTooltip, string authoredTitle, string authoredBody)
        {
            tooltip = authoredTooltip;
            title = authoredTitle ?? string.Empty;
            body = authoredBody ?? string.Empty;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
