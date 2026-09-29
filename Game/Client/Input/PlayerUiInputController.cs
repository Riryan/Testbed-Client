using Game.Client.UI.PlayerItems;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Root;
using Player.Networking;
using Player.Client;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.Input
{
    /// <summary>
    /// Client-only UI key router. Gameplay state is never mutated here; it only opens
    /// presentation surfaces that send authoritative requests through networking.
    /// </summary>
    public sealed class PlayerUiInputController : MonoBehaviour
    {
        [SerializeField] private PlayerInventoryShell inventoryShell;
        [SerializeField] private ClientUIRoot gameplayUi;

        [Header("Inventory / character")]
        [SerializeField] private KeyCode inventoryKey = KeyCode.I;
        [SerializeField] private KeyCode characterEquipmentKey = KeyCode.C;

        [Header("Interaction")]
        [Tooltip("Canonical contextual interaction input. E resolves the center-reticle target and enters/executes the existing interaction flow.")]
        [SerializeField] private KeyCode interactionKey = KeyCode.E;

        [Header("Gameplay windows")]
        [SerializeField] private KeyCode progressionKey = KeyCode.K;
        [SerializeField] private KeyCode professionsKey = KeyCode.P;
        [SerializeField] private KeyCode heatBountyKey = KeyCode.H;

        private PlayerEntityLocomotionCameraController _ownerLocomotionCamera;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            if (inventoryShell == null)
                inventoryShell = GetComponent<PlayerInventoryShell>();
            if (inventoryShell == null)
                inventoryShell = FindFirstObjectByType<PlayerInventoryShell>();
            if (gameplayUi == null)
                gameplayUi = FindFirstObjectByType<ClientUIRoot>();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            // Gameplay UI V2 owns post-login UI/input routing. Keep this legacy router as a
            // fallback for older prefabs, but never let both routers process the same frame.
            if (ClientGameplayUIRoot.Instance != null && ClientGameplayUIRoot.Instance.HandlesGameplayInput)
                return;

            // A selected text field owns keyboard input before any gameplay/window shortcut.
            // This deliberately checks the EventSystem as well as the existing input gate so
            // typing into chat, whisper targets, popup inputs, or future authored InputFields
            // cannot also toggle Inventory/Character/Skills/etc. Escape only leaves text entry.
            if (IsTextEntryFocused())
            {
                gameplayUi?.CloseInteractionFocus();
                SetInteractionActionTpsActive(false);

                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    EventSystem.current?.SetSelectedGameObject(null);
                return;
            }

            // MMO-style chat entry: when no text field already owns the keyboard, Enter
            // goes directly to the existing authored chat input. ClientUIRoot already owns
            // chat submission through InputField.onEndEdit, so the next Enter submits via
            // the canonical chat transport without adding another input/message path.
            if (gameplayUi != null &&
                gameplayUi.GameplaySessionActive &&
                (UnityEngine.Input.GetKeyDown(KeyCode.Return) ||
                 UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                gameplayUi.PrefillChatCommand(string.Empty);
                return;
            }

            if (LocalClientInputGate.IsGameplayInputBlocked)
            {
                gameplayUi?.CloseInteractionFocus();
                SetInteractionActionTpsActive(false);

                // Normal mouse-driven windows keep the pointer free. Starting ordinary
                // movement dismisses the open escapable gameplay windows instead of leaving
                // the player apparently hard-locked behind UI. The movement key remains held,
                // so canonical locomotion can consume it as soon as the window capture releases.
                if (LocalClientInputGate.IsPointerUiActive)
                {
                    if (HasMovementDismissInput())
                    {
                        DismissPointerGameplayWindowsForMovement();
                        return;
                    }

                    HandlePointerUiWindowInput();
                    return;
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    EventSystem.current?.SetSelectedGameObject(null);
                return;
            }

            if (gameplayUi == null || !gameplayUi.GameplaySessionActive)
            {
                gameplayUi?.CloseInteractionFocus();
                SetInteractionActionTpsActive(false);
                return;
            }

            // Escape opens the authored in-game menu only when another interaction
            // surface is not already consuming Escape. The menu itself is a normal
            // pointer-driven semantic window, so the existing input gate/cursor path
            // owns mouse release while it is open.
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                if (gameplayUi.IsInteractionFocusActive)
                {
                    gameplayUi.CloseInteractionFocus();
                    SetInteractionActionTpsActive(false);
                }
                else
                {
                    gameplayUi.Windows?.Open(ClientUIPanelId.MainMenuWindow);
                }
                return;
            }

            if (UnityEngine.Input.GetMouseButtonDown(0) &&
                (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()) &&
                PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) &&
                owner.GetComponent<PlayerEntityLocomotionCameraController>() is PlayerEntityLocomotionCameraController combatCamera &&
                combatCamera.CombatCameraActive)
            {
                gameplayUi.RequestActionPrimaryFire();
            }

            // Canonical contextual interaction input:
            //   E -> resolve the center-reticle target and enter/execute the existing
            //        HandlePrimaryInteraction path.
            // There is no separate Left-Alt focus mode and no R/F/Q interaction-slot
            // routing. Those keys retain their normal gameplay meanings.
            if (UnityEngine.Input.GetKeyDown(interactionKey))
            {
                gameplayUi.ToggleInteractionFocus();
                SetInteractionActionTpsActive(gameplayUi.IsInteractionFocusActive);
                return;
            }

            SetInteractionActionTpsActive(gameplayUi.IsInteractionFocusActive);

            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha1))
                gameplayUi.RequestCombatAction(Game.Shared.Abilities.BasicAttackInputKind.Light);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha2))
                gameplayUi.RequestCombatAction(Game.Shared.Abilities.BasicAttackInputKind.Heavy);

            if (UnityEngine.Input.GetKeyDown(KeyCode.R))
                gameplayUi.RequestReloadOrRespawn();
            if (UnityEngine.Input.GetKeyDown(KeyCode.F))
                gameplayUi.RequestNearestWorldItemPickup();

            HandlePointerUiWindowInput();
#endif
        }


        private static bool IsTextEntryFocused()
        {
#if !UNITY_SERVER
            GameObject selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject
                : null;
            return selected != null && selected.GetComponentInParent<InputField>() != null;
#else
            return false;
#endif
        }

        private static bool HasMovementDismissInput()
        {
#if !UNITY_SERVER
            return UnityEngine.Input.GetKeyDown(KeyCode.W) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.A) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.S) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.D) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.RightArrow) ||
                   UnityEngine.Input.GetKeyDown(KeyCode.Space);
#else
            return false;
#endif
        }

        private void DismissPointerGameplayWindowsForMovement()
        {
#if !UNITY_SERVER
            bool closedAny = false;

            if (inventoryShell != null && inventoryShell.IsOpen)
            {
                inventoryShell.Close();
                closedAny = true;
            }

            // Use the existing semantic window manager so every normal close still emits
            // PanelVisibilityChanged and each panel releases its existing pointer capture.
            if (gameplayUi?.Windows != null)
            {
                while (gameplayUi.Windows.CloseTopmostEscapable())
                    closedAny = true;
            }

            if (closedAny)
                EventSystem.current?.SetSelectedGameObject(null);
#endif
        }

        private void HandlePointerUiWindowInput()
        {
#if !UNITY_SERVER
            if (IsTextEntryFocused())
                return;

            if (gameplayUi != null && gameplayUi.GameplaySessionActive)
            {
                if (UnityEngine.Input.GetKeyDown(progressionKey))
                    gameplayUi.Windows?.Toggle(ClientUIPanelId.SkillsWindow);
                if (UnityEngine.Input.GetKeyDown(professionsKey))
                    gameplayUi.Windows?.Toggle(ClientUIPanelId.ProfessionsWindow);
                if (UnityEngine.Input.GetKeyDown(heatBountyKey))
                    gameplayUi.Windows?.Toggle(ClientUIPanelId.HeatBountyWindow);
            }

            if (inventoryShell != null &&
                (UnityEngine.Input.GetKeyDown(inventoryKey) || UnityEngine.Input.GetKeyDown(characterEquipmentKey)))
            {
                inventoryShell.Toggle();
                return;
            }

            if (inventoryShell != null && inventoryShell.IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.G))
            {
                inventoryShell.DropSelected(UnityEngine.Input.GetKey(KeyCode.LeftShift) ||
                                            UnityEngine.Input.GetKey(KeyCode.RightShift));
                return;
            }

            if (!UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                return;

            if (inventoryShell != null && inventoryShell.IsOpen)
                inventoryShell.Close();
            else
                gameplayUi?.Windows?.CloseTopmostEscapable();

            EventSystem.current?.SetSelectedGameObject(null);
#endif
        }

        private void SetInteractionActionTpsActive(bool active)
        {
#if !UNITY_SERVER
            if (_ownerLocomotionCamera == null)
            {
                if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner == null)
                    return;
                _ownerLocomotionCamera = owner.GetComponent<PlayerEntityLocomotionCameraController>();
            }

            _ownerLocomotionCamera?.SetInteractionActionCameraActive(active);
#endif
        }
    }
}
