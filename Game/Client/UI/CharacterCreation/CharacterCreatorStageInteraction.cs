using Game.Client.Presentation.Characters;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Client.UI.CharacterCreation
{
    /// <summary>
    /// Creator-stage pointer bridge. LMB drag on a supported body/face region adjusts the
    /// same semantic appearance channels used by the creator UI. LMB drag on empty preview
    /// space orbits the preview camera around the model; the model itself is never rotated.
    /// Mouse wheel zoom remains local presentation only. No network traffic is generated.
    /// </summary>
    public sealed class CharacterCreatorStageInteraction : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
    {
        [SerializeField] private CharacterSelectPreviewController preview;
        [SerializeField] private CharacterCreatorShell creator;
        [SerializeField, Range(0.05f, 2f)] private float sculptSensitivity = 0.42f;

        private CharacterCreatorSculptRegion _activeRegion;
        private string _activeHorizontal = string.Empty;
        private string _activeVertical = string.Empty;
        private bool _orbit;

        public void Configure(CharacterSelectPreviewController previewController, CharacterCreatorShell creatorShell)
        {
            preview = previewController;
            creator = creatorShell;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _activeRegion = null;
            _activeHorizontal = string.Empty;
            _activeVertical = string.Empty;
            _orbit = eventData.button == PointerEventData.InputButton.Left;

            if (!_orbit || preview == null || creator == null)
                return;

            if (!preview.TryPickCreatorSculptRegion(
                    eventData.position,
                    eventData.pressEventCamera,
                    out CharacterCreatorSculptRegion region) ||
                region == null)
                return;

            string horizontal = creator.ResolveFirstSupportedSculptSemantic(region.HorizontalChannels);
            string vertical = creator.ResolveFirstSupportedSculptSemantic(region.VerticalChannels);
            if (string.IsNullOrEmpty(horizontal) && string.IsNullOrEmpty(vertical))
                return;

            _activeRegion = region;
            _activeHorizontal = horizontal;
            _activeVertical = vertical;
            _orbit = false;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _activeRegion = null;
            _activeHorizontal = string.Empty;
            _activeVertical = string.Empty;
            _orbit = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (preview == null)
                return;

            if (_activeRegion == null)
            {
                if (_orbit)
                    preview.Orbit(-eventData.delta.x, -eventData.delta.y);
                return;
            }

            if (creator == null)
                return;

            if (!string.IsNullOrEmpty(_activeHorizontal))
            {
                creator.AdjustMorphBySemantic(
                    _activeHorizontal,
                    -eventData.delta.x * sculptSensitivity * _activeRegion.HorizontalMultiplier);
            }

            if (!string.IsNullOrEmpty(_activeVertical))
            {
                // Creator direct manipulation follows the established test-creator drag convention:
                // drag direction moves the perceived feature rather than the underlying value axis.
                creator.AdjustMorphBySemantic(
                    _activeVertical,
                    -eventData.delta.y * sculptSensitivity * _activeRegion.VerticalMultiplier);
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            preview?.Zoom(eventData.scrollDelta.y);
        }
    }
}
