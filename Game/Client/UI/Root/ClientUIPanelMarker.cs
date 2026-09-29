using Player.Networking;
using UnityEngine;

namespace Game.Client.UI.Root
{
    /// <summary>
    /// Stable semantic binding for an authored UI surface. Window code resolves this
    /// marker by ClientUIPanelId rather than by Transform path or GameObject name.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientUIPanelMarker : MonoBehaviour
    {
        [SerializeField] private ClientUIPanelId panelId;
        [SerializeField] private bool gameplayOnly = true;
        [SerializeField] private bool modal;
        [SerializeField] private bool closeOnEscape = true;
        [SerializeField] private bool managedWindow = true;

        private bool _pointerUiCaptureHeld;
        private bool _exclusiveGameplayCaptureHeld;

        public ClientUIPanelId PanelId => panelId;
        public bool GameplayOnly => gameplayOnly;
        public bool Modal => modal;
        public bool CloseOnEscape => closeOnEscape;
        public bool ManagedWindow => managedWindow;

        private void OnEnable()
        {
#if !UNITY_SERVER
            if (!Application.isPlaying || !managedWindow)
                return;

            // Pointer ownership is not gameplay ownership. Ordinary Inventory/Friends/Map/etc.
            // windows only unlock the pointer. Explicit modal windows additionally acquire the
            // existing exclusive gameplay gate so they can safely own input while visible.
            if (!_pointerUiCaptureHeld && RequiresPointerCursor(panelId))
            {
                LocalClientInputGate.AcquirePointerUi();
                _pointerUiCaptureHeld = true;
            }

            if (!_exclusiveGameplayCaptureHeld && modal)
            {
                LocalClientInputGate.Acquire();
                _exclusiveGameplayCaptureHeld = true;
            }
#endif
        }

        private void OnDisable()
        {
#if !UNITY_SERVER
            ReleaseInputCaptures();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            ReleaseInputCaptures();
#endif
        }

        private void ReleaseInputCaptures()
        {
            if (_pointerUiCaptureHeld)
            {
                LocalClientInputGate.ReleasePointerUi();
                _pointerUiCaptureHeld = false;
            }

            if (_exclusiveGameplayCaptureHeld)
            {
                LocalClientInputGate.Release();
                _exclusiveGameplayCaptureHeld = false;
            }
        }

        private static bool RequiresPointerCursor(ClientUIPanelId id)
        {
            int value = (int)id;

            // Semantic panel ranges are intentionally used here so the existing authored
            // window registry remains the source of truth. The contextual Interaction Menu
            // itself (500) stays keyboard/controller driven with the gameplay cursor captured.
            // Interaction consent/selection surfaces are ordinary clickable UI and may request
            // the pointer independently. HUD, notifications, and overlays remain presentation-only.
            return (value >= 300 && value < 500) ||
                   id == ClientUIPanelId.InteractionConsent ||
                   id == ClientUIPanelId.InteractionParticipantSelection ||
                   id == ClientUIPanelId.InteractionPrivateSessionControls ||
                   (value >= 600 && value < 700);
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(ClientUIPanelId id, bool isGameplayOnly, bool isModal, bool closesOnEscape, bool isManagedWindow = true)
        {
            panelId = id;
            gameplayOnly = isGameplayOnly;
            modal = isModal;
            closeOnEscape = closesOnEscape;
            managedWindow = isManagedWindow;
        }
#endif
    }
}
