using UnityEngine.EventSystems;

namespace Game.Client.Presentation.Characters
{
    public sealed class CharacterSelectPreviewDragSurface : UnityEngine.MonoBehaviour, IDragHandler, IScrollHandler
    {
        public CharacterSelectPreviewController controller;

        public void OnDrag(PointerEventData eventData)
        {
            if (controller != null)
                controller.Rotate(eventData.delta.x);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (controller != null)
                controller.Zoom(eventData.scrollDelta.y);
        }
    }
}
