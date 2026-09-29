using System;
using System.Collections.Generic;
using System.Text;
using Game.Client.Chat;
using Game.Client.UI.Interactions;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Root;
using Game.Client.UI.Standalone;
using Game.Shared.Chat;
using Game.Shared.Abilities;
using Game.Shared.Interactions;
using Game.Shared.Resources;
using Player.Client;
using Player.Client.Presentation;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Gameplay
{
    /// <summary>
    /// Clean post-login gameplay presentation shell.
    ///
    /// Scope is intentionally narrow:
    /// - owner HP / MP / Stamina from the existing revisioned resource cache/events
    /// - pushed chat through the existing PlayerEntity chat transport
    /// - combat-mode-only reticle target presentation
    /// - existing contextual interaction controller/presentation
    /// - ESC Resume / Options / Quit menu
    /// - existing Inventory shell remains untouched and is only toggled from here
    ///
    /// This component owns no gameplay authority and introduces no network messages.
    /// It stays enabled through frontend/world transitions while only its authored visualRoot
    /// is shown after world admission, so subscriptions cannot be lost during the handoff.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientGameplayUIRoot : MonoBehaviour
    {
        public static ClientGameplayUIRoot Instance { get; private set; }

        [Header("Authored Gameplay V2 Root")]
        [SerializeField] private GameObject visualRoot;

        [Header("Owner Resources")]
        [SerializeField] private Slider healthSlider;
        [SerializeField] private Text healthText;
        [SerializeField] private Slider manaSlider;
        [SerializeField] private Text manaText;
        [SerializeField] private Slider staminaSlider;
        [SerializeField] private Text staminaText;

        [Header("Chat")]
        [SerializeField] private ScrollRect chatScroll;
        [SerializeField] private Text chatHistoryText;
        [SerializeField] private InputField chatInput;
        [SerializeField] private Button chatSendButton;

        [Header("Combat Target Presentation")]
        [SerializeField] private GameObject combatTargetRoot;
        [SerializeField] private Text combatTargetText;
        [SerializeField] private Slider combatTargetHealthSlider;

        [Header("ESC Menu")]
        [SerializeField] private GameObject pauseMenuRoot;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button optionsButton;
        [SerializeField] private Button quitButton;

        [Header("Reticle Target Presentation")]
        [SerializeField, Min(1f)] private float targetRaycastDistance = 250f;
        [SerializeField, Range(0.02f, 0.25f)] private float targetRefreshIntervalSeconds = 0.05f;

        [Header("Exploration / Hotbar Presentation")]
        [SerializeField] private GameObject explorationReticleRoot;
        [SerializeField] private Text[] hotbarBindingTexts = Array.Empty<Text>();
        [SerializeField] private StandaloneHotbarSlotView[] hotbarSlots = Array.Empty<StandaloneHotbarSlotView>();

        private readonly ClientChatState _chatState = new ClientChatState();
        private readonly RaycastHit[] _targetHits = new RaycastHit[32];
        private readonly List<Renderer> _rendererScratch = new List<Renderer>(16);
        private readonly Dictionary<ulong, int> _populationEstimatedHealth = new Dictionary<ulong, int>();

        private ClientUIRoot _legacyRoot;
        private PlayerEntityGameManager _manager;
        private PlayerInventoryShell _inventory;
        private ClientInteractionUI _interaction;
        private IClientChatTransport _chatTransport;
        private bool _managerSubscribed;
        private bool _pauseCaptureHeld;
        private bool _cursorReleaseCaptureHeld;
        private bool _interactionOverlayActive;
        private bool _visible;
        private double _nextManagerBindAttemptAt;
        private double _nextTargetRefreshAt;
        private PlayerEntityLocomotionCameraController _ownerLocomotionCamera;

        public PlayerEntityGameManager Manager => _manager;
        public bool HandlesGameplayInput => enabled && gameObject.activeInHierarchy && visualRoot != null;
        public bool GameplayVisible => _visible && visualRoot != null && visualRoot.activeSelf;
        public bool PauseMenuOpen => pauseMenuRoot != null && pauseMenuRoot.activeSelf;
        public bool HasAuthoredBindings =>
            visualRoot != null &&
            healthSlider != null && healthText != null &&
            manaSlider != null && manaText != null &&
            staminaSlider != null && staminaText != null &&
            chatHistoryText != null && chatInput != null && chatSendButton != null &&
            combatTargetRoot != null && combatTargetText != null && combatTargetHealthSlider != null &&
            pauseMenuRoot != null && resumeButton != null && optionsButton != null && quitButton != null;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#else
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            _legacyRoot = GetComponent<ClientUIRoot>();
            if (_legacyRoot == null)
                _legacyRoot = GetComponentInParent<ClientUIRoot>();
            _inventory = GetComponent<PlayerInventoryShell>();
            if (_inventory == null)
                _inventory = GetComponentInParent<PlayerInventoryShell>();
            _interaction = GetComponent<ClientInteractionUI>();
            if (_interaction == null && _legacyRoot != null)
                _interaction = _legacyRoot.InteractionUi;
            if (_interaction != null)
                _interaction.BindRoot(_legacyRoot);

            _chatState.Changed += RenderChatHistory;
            PlayerControlConfig.BindingChanged += OnControlBindingChanged;
            PlayerHotbarConfig.Changed += OnHotbarAssignmentChanged;
            PlayerGameplayUiConfig.Changed += OnGameplayUiSettingsChanged;
            PlayerCombatPresentationRouter.ParticipantDamageReceived += OnParticipantCombatDamage;
            WireButtons();
            ResetResourcePresentation();
            RefreshHotbarBindingLabels();
            RefreshHotbarAssignments();
            SetCombatTargetVisible(false);
            SetPauseMenuVisible(false, releaseCapture: false);
            SetGameplayVisible(false);
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            TryBindManager(force: true);
            if (_legacyRoot != null && _legacyRoot.GameplaySessionActive)
                SetGameplayVisible(true);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            if (Instance == this)
                Instance = null;
            UnwireButtons();
            _chatState.Changed -= RenderChatHistory;
            PlayerControlConfig.BindingChanged -= OnControlBindingChanged;
            PlayerHotbarConfig.Changed -= OnHotbarAssignmentChanged;
            PlayerGameplayUiConfig.Changed -= OnGameplayUiSettingsChanged;
            PlayerCombatPresentationRouter.ParticipantDamageReceived -= OnParticipantCombatDamage;
            SetInteractionActionCameraActive(false);
            UnbindManager();
            ReleasePauseCapture();
            ReleaseCursorReleaseCapture();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (_manager == null && Time.realtimeSinceStartupAsDouble >= _nextManagerBindAttemptAt)
                TryBindManager(force: false);

            if (!GameplayVisible)
                return;

            // Temporary canonical input trace requested for runtime diagnosis.
            // Uses the existing local System chat path; no network message is introduced.
            if (UnityEngine.Input.GetKeyDown(KeyCode.E))
                PresentLocalSystemMessage(
                    $"INPUT TRACE E: raw=YES mappedInteract={PlayerControlConfig.GetKeyDown(PlayerControlAction.Interact)} " +
                    $"textFocused={IsTextInputFocused()} gameplayBlocked={LocalClientInputGate.IsGameplayInputBlocked} " +
                    $"pointerUi={LocalClientInputGate.IsPointerUiActive} interaction={( _interaction != null ? "BOUND" : "NULL")}");

            if (UnityEngine.Input.GetKeyDown(KeyCode.R))
                PresentLocalSystemMessage(
                    $"INPUT TRACE R: raw=YES mappedReload={PlayerControlConfig.GetKeyDown(PlayerControlAction.ReloadOrRespawn)} " +
                    $"textFocused={IsTextInputFocused()} gameplayBlocked={LocalClientInputGate.IsGameplayInputBlocked} " +
                    $"pointerUi={LocalClientInputGate.IsPointerUiActive} legacyRoot={( _legacyRoot != null ? "BOUND" : "NULL")}");

            HandleCursorReleaseInput();
            HandleGameplayUiInput();
            SetInteractionActionCameraActive(_interaction != null && _interaction.IsActionFocusActive);
            RefreshCombatTargetPresentation();
            RefreshExplorationReticlePresentation();
#endif
        }

        public void SetGameplayVisible(bool visible)
        {
#if !UNITY_SERVER
            _visible = visible;
            if (visualRoot != null)
                visualRoot.SetActive(visible);

            if (!visible)
            {
                ClosePauseMenu();
                _interaction?.HideAllInteractionSurfaces();
                SetInteractionActionCameraActive(false);
                _inventory?.Close();
                StandaloneClientUIRoot.Instance?.CloseTransientGameplayWindows();
                EventSystem.current?.SetSelectedGameObject(null);
                SetCombatTargetVisible(false);
                if (explorationReticleRoot != null)
                    explorationReticleRoot.SetActive(false);
                ReleaseCursorReleaseCapture();
                return;
            }

            TryBindManager(force: true);
            if (_manager != null && _manager.LatestPlayerResources.success)
                ApplyResourceSnapshot(_manager.LatestPlayerResources);
            else
                ResetResourcePresentation();
#endif
        }

        public void PresentLocalSystemMessage(string message)
        {
#if !UNITY_SERVER
            if (string.IsNullOrWhiteSpace(message))
                return;
            _chatState.AddSystem(message.Trim());
#endif
        }

        public void PrefillChatCommand(string command)
        {
#if !UNITY_SERVER
            if (chatInput == null || !GameplayVisible)
                return;
            chatInput.text = command ?? string.Empty;
            FocusChatInput(moveToEnd: true);
#endif
        }

        public void SetInteractionOverlayActive(bool active)
        {
            _interactionOverlayActive = active;
            if (active)
                SetCombatTargetVisible(false);
        }

        private void TryBindManager(bool force)
        {
            if (!force && _manager != null)
                return;

            PlayerEntityGameManager found = FindFirstObjectByType<PlayerEntityGameManager>(FindObjectsInactive.Include);
            if (found == null)
            {
                _nextManagerBindAttemptAt = Time.realtimeSinceStartupAsDouble + 0.5d;
                return;
            }
            if (ReferenceEquals(found, _manager) && _managerSubscribed)
                return;

            UnbindManager();
            _manager = found;
            _manager.ClientConnectionEstablished += OnClientConnectionEstablished;
            _manager.ClientConnectionClosed += OnClientConnectionClosed;
            _manager.ClientWorldEntered += OnClientWorldEntered;
            _manager.PlayerResourcesSnapshotReceived += OnResourceSnapshot;
            _manager.PlayerResourceDeltaReceived += OnResourceDelta;
            _manager.GameplaySettingsSnapshotReceived += OnGameplaySettingsSnapshot;
            _manager.ProgressionSnapshotReceived += OnProgressionSnapshot;
            _managerSubscribed = true;

            _chatTransport = new PlayerEntityChatTransport(_manager);
            _chatTransport.MessageReceived += OnChatMessage;

            if (_manager.LatestPlayerResources.success)
                ApplyResourceSnapshot(_manager.LatestPlayerResources);
        }

        private void UnbindManager()
        {
            if (_managerSubscribed && _manager != null)
            {
                _manager.ClientConnectionEstablished -= OnClientConnectionEstablished;
                _manager.ClientConnectionClosed -= OnClientConnectionClosed;
                _manager.ClientWorldEntered -= OnClientWorldEntered;
                _manager.PlayerResourcesSnapshotReceived -= OnResourceSnapshot;
                _manager.PlayerResourceDeltaReceived -= OnResourceDelta;
                _manager.GameplaySettingsSnapshotReceived -= OnGameplaySettingsSnapshot;
                _manager.ProgressionSnapshotReceived -= OnProgressionSnapshot;
            }
            _managerSubscribed = false;

            if (_chatTransport != null)
            {
                _chatTransport.MessageReceived -= OnChatMessage;
                _chatTransport.Dispose();
                _chatTransport = null;
            }
            _manager = null;
        }

        private void OnClientConnectionEstablished()
        {
            SetGameplayVisible(false);
            ResetResourcePresentation();
        }

        private void OnClientWorldEntered(long characterId)
        {
            // World admission is the clean boundary between frontend and gameplay input.
            // Clear stale local-only UI capture bookkeeping from the frontend/previous world,
            // then every gameplay window acquires a fresh scoped capture when opened.
            LocalClientInputGate.Reset();
            _chatState.Clear();
            _chatState.AddSystem("Chat ready.");
            SetGameplayVisible(true);
            if (_manager != null && _manager.LatestPlayerResources.success)
                ApplyResourceSnapshot(_manager.LatestPlayerResources);
        }

        private void OnClientConnectionClosed(ClientDisconnectNotice notice)
        {
            SetGameplayVisible(false);
            LocalClientInputGate.Reset();
        }

        private void OnResourceSnapshot(PlayerResourcesResponseMessage snapshot) => ApplyResourceSnapshot(snapshot);

        private void OnResourceDelta(PlayerResourceDeltaMessage delta)
        {
            CharacterResourceId id = delta.ResourceId;
            switch (id)
            {
                case CharacterResourceId.Health:
                    ApplyResource(healthSlider, healthText, "HP", delta.current, delta.minimum, delta.maximum);
                    break;
                case CharacterResourceId.Mana:
                    ApplyResource(manaSlider, manaText, "MP", delta.current, delta.minimum, delta.maximum);
                    break;
                case CharacterResourceId.Stamina:
                    ApplyResource(staminaSlider, staminaText, "STAM", delta.current, delta.minimum, delta.maximum);
                    break;
            }
        }

        private void ApplyResourceSnapshot(PlayerResourcesResponseMessage snapshot)
        {
            if (!snapshot.success || snapshot.resources == null)
                return;

            for (int i = 0; i < snapshot.resources.Length; ++i)
            {
                CharacterResourceWire resource = snapshot.resources[i];
                switch ((CharacterResourceId)resource.id)
                {
                    case CharacterResourceId.Health:
                        ApplyResource(healthSlider, healthText, "HP", resource.current, resource.minimum, resource.maximum);
                        break;
                    case CharacterResourceId.Mana:
                        ApplyResource(manaSlider, manaText, "MP", resource.current, resource.minimum, resource.maximum);
                        break;
                    case CharacterResourceId.Stamina:
                        ApplyResource(staminaSlider, staminaText, "STAM", resource.current, resource.minimum, resource.maximum);
                        break;
                }
            }
        }

        private void ResetResourcePresentation()
        {
            ResetResource(healthSlider, healthText, "HP");
            ResetResource(manaSlider, manaText, "MP");
            ResetResource(staminaSlider, staminaText, "STAM");
        }

        private static void ResetResource(Slider slider, Text label, string prefix)
        {
            if (slider != null)
            {
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.SetValueWithoutNotify(0f);
            }
            if (label != null)
                label.text = prefix;
        }

        private static void ApplyResource(Slider slider, Text label, string prefix, int current, int minimum, int maximum)
        {
            int safeMaximum = Math.Max(minimum + 1, maximum);
            if (slider != null)
            {
                slider.minValue = minimum;
                slider.maxValue = safeMaximum;
                slider.SetValueWithoutNotify(Mathf.Clamp(current, minimum, safeMaximum));
            }
            if (label != null)
                label.text = $"{prefix}  {current:N0} / {maximum:N0}";
        }

        private void HandleGameplayUiInput()
        {
            if (IsTextInputFocused())
            {
                if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenMenu))
                    ReleaseChatFocus();
                return;
            }

            // Pointer-driven gameplay windows are transient. Starting normal movement closes
            // them, then the already-held movement key is free for the canonical locomotion path.
            if (LocalClientInputGate.IsPointerUiActive && HasMovementDismissInput())
            {
                bool closed = false;
                if (_interaction != null && _interaction.IsMenuVisible)
                {
                    _interaction.HideMenu();
                    closed = true;
                }
                if (StandaloneClientUIRoot.Instance != null && StandaloneClientUIRoot.Instance.AnyTransientWindowOpen)
                {
                    StandaloneClientUIRoot.Instance.CloseTransientGameplayWindows();
                    closed = true;
                }
                if (_inventory != null && _inventory.IsOpen)
                {
                    _inventory.Close();
                    closed = true;
                }
                if (_legacyRoot != null && _legacyRoot.Windows != null)
                {
                    while (_legacyRoot.Windows.CloseTopmostEscapable())
                        closed = true;
                }
                if (closed)
                {
                    EventSystem.current?.SetSelectedGameObject(null);
                    return;
                }
            }

            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenMenu))
            {
                if (_interaction != null)
                {
                    if (_interaction.IsConsentVisible)
                        return;
                    if (_interaction.IsActionFocusActive)
                    {
                        _interaction.HideMenu();
                        return;
                    }
                    if (_interaction.IsMenuVisible)
                    {
                        _interaction.HideMenu();
                        return;
                    }
                }

                if (StandaloneClientUIRoot.Instance != null && StandaloneClientUIRoot.Instance.CloseTopmostTransientWindow())
                    return;

                if (_inventory != null && _inventory.IsOpen)
                {
                    _inventory.Close();
                    return;
                }

                if (_legacyRoot != null && _legacyRoot.Windows != null && _legacyRoot.Windows.CloseTopmostEscapable())
                    return;

                if (PauseMenuOpen)
                    ClosePauseMenu();
                else
                    OpenPauseMenu();
                return;
            }

            if (PauseMenuOpen)
                return;

            if (_interaction != null && _interaction.IsConsentVisible)
                return;

            if (_interaction != null && _interaction.IsMenuVisible)
            {
                if (PlayerControlConfig.GetKeyDown(PlayerControlAction.Interact))
                    _interaction.TryActivatePrimaryMenuAction();
                return;
            }

            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenInventory))
            {
                if (StandaloneClientUIRoot.Instance != null)
                    StandaloneClientUIRoot.Instance.ToggleCharacterInventory();
                else
                    _inventory?.Toggle();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenSkills))
            {
                StandaloneClientUIRoot.Instance?.ToggleSkills();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenMap))
            {
                StandaloneClientUIRoot.Instance?.ToggleMap();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenSocial))
            {
                StandaloneClientUIRoot.Instance?.ToggleSocial();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenEmotes))
            {
                StandaloneClientUIRoot.Instance?.ToggleEmotes();
                return;
            }

            if (_inventory != null && _inventory.IsOpen && PlayerControlConfig.GetKeyDown(PlayerControlAction.DropSelected))
            {
                _inventory.DropSelected(UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift));
                return;
            }

            // Pointer ownership is presentation/cursor state only. LocalClientInputGate
            // explicitly defines it as NOT a gameplay lock, so do not suppress E/R/F/combat
            // merely because a pointer-driven UI surface owns the cursor. Exclusive/modal
            // input is handled separately by the gameplay-capture/text-entry paths above.
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.FocusChat))
            {
                FocusChatInput(moveToEnd: false);
                return;
            }

            // One canonical contextual interaction binding. If the target exposes one valid
            // action, Interact executes it directly. Multiple actions open the authored
            // clickable panel; pressing Interact again confirms its first/default action.
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.Interact))
            {
                PresentLocalSystemMessage("INPUT TRACE E: canonical dispatch reached.");
                if (_interaction == null)
                {
                    PresentLocalSystemMessage("INPUT TRACE E: FAIL - ClientInteractionUI is not bound.");
                    return;
                }

                bool accepted = _interaction.HandlePrimaryInteraction();
                PresentLocalSystemMessage(accepted
                    ? "INPUT TRACE E: ClientInteractionUI accepted input."
                    : "INPUT TRACE E: ClientInteractionUI rejected input; see INTERACTION TRACE.");
                return;
            }

            for (int slot = 1; slot <= 10; ++slot)
            {
                if (!PlayerControlConfig.GetKeyDown(PlayerControlConfig.HotbarAction(slot)))
                    continue;
                ActivateHotbarSlot(slot);
                return;
            }

            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.ReloadOrRespawn))
            {
                PresentLocalSystemMessage("INPUT TRACE R: canonical reload dispatch reached.");
                RequestReloadOrRespawnCanonical();
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.PickupNearest))
                _legacyRoot?.RequestNearestWorldItemPickup();
        }

        /// <summary>
        /// Canonical gameplay-owned reload/respawn dispatch.
        /// Reload authority belongs to PlayerEntityGameManager, not an optional/legacy UI root.
        /// </summary>
        public void RequestReloadOrRespawnCanonical()
        {
#if !UNITY_SERVER
            RequestReloadOrRespawnCanonicalAsync().Forget();
#endif
        }

#if !UNITY_SERVER
        private async Cysharp.Threading.Tasks.UniTaskVoid RequestReloadOrRespawnCanonicalAsync()
        {
            TryBindManager(force: true);
            if (_manager == null)
            {
                PresentLocalSystemMessage("RELOAD: Game manager is unavailable.");
                return;
            }

            if (IsLocallyKnownDeadCanonical())
            {
                PlayerRespawnResponseMessage respawn = await _manager.RequestRespawnAsync();
                PresentLocalSystemMessage(respawn.success
                    ? "Respawn requested."
                    : $"Respawn rejected ({respawn.resultCode}).");
                return;
            }

            PresentLocalSystemMessage("Reloading...");
            PlayerReloadResponseMessage response = await _manager.RequestReloadAsync();
            PlayerCombatOwnerStateMessage owner = _manager.LatestCombatOwnerState;
            PresentLocalSystemMessage(response.success
                ? $"Reloaded: {response.loadedRounds}/{owner.magazineCapacity}."
                : ReloadFeedbackTextCanonical(response.resultCode));
        }

        private bool IsLocallyKnownDeadCanonical()
        {
            if (_manager == null || !_manager.LatestPlayerResources.success)
                return false;

            CharacterResourceWire[] resources =
                _manager.LatestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
            {
                if ((CharacterResourceId)resources[i].id == CharacterResourceId.Health)
                    return resources[i].current <= resources[i].minimum;
            }
            return false;
        }

        private static string ReloadFeedbackTextCanonical(byte resultCode)
        {
            switch (resultCode)
            {
                case 1: return "Cannot reload right now.";
                case 2: return "Equipped weapon is not a firearm.";
                case 3: return "Magazine is already full.";
                case 4: return "No compatible ammunition is available in inventory.";
                case 5: return "Cannot mix ammunition types in a partially loaded magazine.";
                case 6: return "Reload failed to commit inventory state. Try again.";
                default: return $"Reload rejected ({resultCode}).";
            }
        }
#endif

        private static bool HasMovementDismissInput()
        {
            return PlayerControlConfig.GetKeyDown(PlayerControlAction.MoveForward) ||
                   PlayerControlConfig.GetKeyDown(PlayerControlAction.MoveBackward) ||
                   PlayerControlConfig.GetKeyDown(PlayerControlAction.MoveLeft) ||
                   PlayerControlConfig.GetKeyDown(PlayerControlAction.MoveRight) ||
                   PlayerControlConfig.GetKeyDown(PlayerControlAction.Jump);
        }

        private bool IsTextInputFocused()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponentInParent<InputField>() != null;
        }

        private void FocusChatInput(bool moveToEnd)
        {
            if (chatInput == null)
                return;
            chatInput.gameObject.SetActive(true);
            chatInput.Select();
            chatInput.ActivateInputField();
            if (moveToEnd)
                chatInput.MoveTextEnd(false);
        }

        private void ReleaseChatFocus()
        {
            if (chatInput != null)
                chatInput.DeactivateInputField();
            EventSystem.current?.SetSelectedGameObject(null);
        }

        private void HandleCursorReleaseInput()
        {
            bool shouldHold = GameplayVisible && PlayerControlConfig.GetKey(PlayerControlAction.ReleaseCursor) && !IsTextInputFocused();
            if (shouldHold && !_cursorReleaseCaptureHeld)
            {
                _cursorReleaseCaptureHeld = true;
                LocalClientInputGate.AcquirePointerUi();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (!shouldHold && _cursorReleaseCaptureHeld)
            {
                ReleaseCursorReleaseCapture();
            }
        }

        private void ReleaseCursorReleaseCapture()
        {
            if (!_cursorReleaseCaptureHeld)
                return;
            _cursorReleaseCaptureHeld = false;
            LocalClientInputGate.ReleasePointerUi();
        }

        private void RefreshExplorationReticlePresentation()
        {
            if (explorationReticleRoot == null)
                return;

            if (!GameplayVisible || LocalClientInputGate.IsPointerUiActive)
            {
                if (explorationReticleRoot.activeSelf)
                    explorationReticleRoot.SetActive(false);
                return;
            }

            if (_ownerLocomotionCamera == null && PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
                _ownerLocomotionCamera = owner.GetComponent<PlayerEntityLocomotionCameraController>();

            bool show = PlayerGameplayUiConfig.ShowInteractionDot &&
                        (_ownerLocomotionCamera == null || !_ownerLocomotionCamera.CombatCameraActive);
            if (explorationReticleRoot.activeSelf != show)
                explorationReticleRoot.SetActive(show);
        }

        private void ActivateHotbarSlot(int oneBasedSlot)
        {
            PlayerHotbarAssignment assignment = PlayerHotbarConfig.Get(oneBasedSlot);
            switch (assignment.Kind)
            {
                case PlayerHotbarEntryKind.BasicLight:
                    _legacyRoot?.RequestCombatAction(BasicAttackInputKind.Light);
                    return;
                case PlayerHotbarEntryKind.BasicHeavy:
                    _legacyRoot?.RequestCombatAction(BasicAttackInputKind.Heavy);
                    return;
                case PlayerHotbarEntryKind.Ability:
                    if (!PlayerGameplaySettingsRuntime.TryGetAbility(assignment.AbilityDefinitionId, out GameplayAbilityReferenceWire ability))
                    {
                        PresentLocalSystemMessage($"Hotbar slot {oneBasedSlot} references an unavailable ability.");
                        return;
                    }
                    if (!IsLocallyKnownAbility(ability.wireId))
                    {
                        string abilityName = ability.definitionId;
                        if (PlayerGameplaySettingsRuntime.TryGetAbilityPresentation(ability.wireId, out GameplayAbilityPresentationWire presentation) &&
                            !string.IsNullOrWhiteSpace(presentation.displayName))
                            abilityName = presentation.displayName;
                        PresentLocalSystemMessage($"{abilityName} is not learned on this character.");
                        return;
                    }
                    _legacyRoot?.RequestAbility(ability.definitionId);
                    return;
                default:
                    PresentLocalSystemMessage($"Hotbar slot {oneBasedSlot} is not assigned yet.");
                    return;
            }
        }

        private bool IsLocallyKnownAbility(ushort wireId)
        {
            if (_manager == null || wireId == 0 || !_manager.LatestProgression.success)
                return false;
            ushort[] known = _manager.LatestProgression.knownAbilityWireIds ?? Array.Empty<ushort>();
            return Array.BinarySearch(known, wireId) >= 0;
        }

        private void OnControlBindingChanged(PlayerControlAction action, KeyCode key) => RefreshHotbarBindingLabels();

        private void OnHotbarAssignmentChanged(int slot, PlayerHotbarAssignment assignment) => RefreshHotbarAssignments();

        private void OnGameplaySettingsSnapshot(GameplaySettingsSnapshotMessage snapshot) => RefreshHotbarAssignments();

        private void OnProgressionSnapshot(ProgressionSnapshotMessage snapshot) => RefreshHotbarAssignments();

        private void OnGameplayUiSettingsChanged()
        {
            RenderChatHistory();
            RefreshExplorationReticlePresentation();
        }

        private void RefreshHotbarBindingLabels()
        {
            if (hotbarBindingTexts == null)
                return;
            for (int i = 0; i < hotbarBindingTexts.Length; ++i)
            {
                if (hotbarBindingTexts[i] != null)
                    hotbarBindingTexts[i].text = PlayerControlConfig.BindingLabel(PlayerControlConfig.HotbarAction(i + 1));
            }
        }

        private void RefreshHotbarAssignments()
        {
            if (hotbarSlots == null)
                return;
            for (int i = 0; i < hotbarSlots.Length; ++i)
                hotbarSlots[i]?.Refresh();
        }

        private void WireButtons()
        {
            if (chatSendButton != null)
            {
                chatSendButton.onClick.RemoveListener(SendChatInput);
                chatSendButton.onClick.AddListener(SendChatInput);
            }
            if (chatInput != null)
            {
                chatInput.onEndEdit.RemoveListener(OnChatEndEdit);
                chatInput.onEndEdit.AddListener(OnChatEndEdit);
            }
            if (resumeButton != null)
            {
                resumeButton.onClick.RemoveListener(ClosePauseMenu);
                resumeButton.onClick.AddListener(ClosePauseMenu);
            }
            if (optionsButton != null)
            {
                optionsButton.onClick.RemoveListener(OpenOptions);
                optionsButton.onClick.AddListener(OpenOptions);
            }
            if (quitButton != null)
            {
                quitButton.onClick.RemoveListener(QuitGame);
                quitButton.onClick.AddListener(QuitGame);
            }
        }

        private void UnwireButtons()
        {
            chatSendButton?.onClick.RemoveListener(SendChatInput);
            chatInput?.onEndEdit.RemoveListener(OnChatEndEdit);
            resumeButton?.onClick.RemoveListener(ClosePauseMenu);
            optionsButton?.onClick.RemoveListener(OpenOptions);
            quitButton?.onClick.RemoveListener(QuitGame);
        }

        private void OnChatEndEdit(string value)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
                SendChatInput();
        }

        private void SendChatInput()
        {
            if (chatInput == null)
                return;

            string raw = (chatInput.text ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                ReleaseChatFocus();
                return;
            }

            if (string.Equals(raw, "/clear", StringComparison.OrdinalIgnoreCase))
            {
                _chatState.Clear();
                chatInput.text = string.Empty;
                ReleaseChatFocus();
                return;
            }

            if (string.Equals(raw, "/help", StringComparison.OrdinalIgnoreCase))
            {
                _chatState.AddSystem("Chat: Local by default. /w \"First Last\" message, /p message, /g message, /s message, /clear.");
                chatInput.text = string.Empty;
                ReleaseChatFocus();
                return;
            }

            ChatChannel channel = ChatChannel.Local;
            string target = string.Empty;
            string message = raw;
            ParseSlashCommand(raw, ref channel, ref target, ref message);

            TryBindManager(force: true);
            string error = string.Empty;
            if (_chatTransport == null || !_chatTransport.TrySend(channel, target, message, out error))
            {
                _chatState.AddSystem(string.IsNullOrWhiteSpace(error) ? "Unable to send chat message." : error);
                ReleaseChatFocus();
                return;
            }

            chatInput.text = string.Empty;
            ReleaseChatFocus();
        }

        private static void ParseSlashCommand(string raw, ref ChatChannel channel, ref string target, ref string message)
        {
            if (raw.StartsWith("/p ", StringComparison.OrdinalIgnoreCase))
            {
                channel = ChatChannel.Party;
                message = raw.Substring(3).Trim();
                return;
            }
            if (raw.StartsWith("/g ", StringComparison.OrdinalIgnoreCase))
            {
                channel = ChatChannel.Guild;
                message = raw.Substring(3).Trim();
                return;
            }
            if (raw.StartsWith("/s ", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("/l ", StringComparison.OrdinalIgnoreCase))
            {
                channel = ChatChannel.Local;
                message = raw.Substring(3).Trim();
                return;
            }
            if (!raw.StartsWith("/w ", StringComparison.OrdinalIgnoreCase))
                return;

            channel = ChatChannel.Whisper;
            string rest = raw.Substring(3).TrimStart();
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = rest.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    target = rest.Substring(1, closingQuote - 1).Trim();
                    message = rest.Substring(closingQuote + 1).Trim();
                    return;
                }
            }

            int split = rest.IndexOf(' ');
            if (split > 0)
            {
                target = rest.Substring(0, split).Trim();
                message = rest.Substring(split + 1).Trim();
            }
            else
            {
                target = rest.Trim();
                message = string.Empty;
            }
        }

        private void OnChatMessage(ClientChatMessage message) => _chatState.Add(message);

        private void RenderChatHistory()
        {
            if (chatHistoryText == null)
                return;

            chatHistoryText.supportRichText = true;
            var builder = new StringBuilder(2048);
            foreach (ClientChatState.Entry entry in _chatState.Entries)
            {
                string sender = EscapeRichText(entry.Sender);
                string target = EscapeRichText(entry.Target);
                string message = EscapeRichText(entry.Message);
                if (PlayerGameplayUiConfig.ShowChatTimestamps && entry.ServerUtcTicks > DateTime.MinValue.Ticks)
                {
                    try
                    {
                        DateTime localTime = new DateTime(entry.ServerUtcTicks, DateTimeKind.Utc).ToLocalTime();
                        builder.Append("<color=#8995A8>[").Append(localTime.ToString("HH:mm")).Append("]</color> ");
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Malformed presentation timestamp must never break chat rendering.
                    }
                }
                switch (entry.Channel)
                {
                    case ChatChannel.System:
                        builder.Append("<color=#E8C66A>[System]</color> ").Append(message);
                        break;
                    case ChatChannel.Whisper:
                        builder.Append("<color=#71D7FF>[Whisper]</color> ").Append(sender);
                        if (!string.IsNullOrWhiteSpace(target))
                            builder.Append(" -> ").Append(target);
                        builder.Append(": ").Append(message);
                        break;
                    case ChatChannel.Party:
                        builder.Append("<color=#79AFFF>[Party]</color> ").Append(sender).Append(": ").Append(message);
                        break;
                    case ChatChannel.Guild:
                        builder.Append("<color=#C58CFF>[Guild]</color> ").Append(sender).Append(": ").Append(message);
                        break;
                    default:
                        builder.Append("<color=#9BE28F>[Local]</color> ").Append(sender).Append(": ").Append(message);
                        break;
                }
                builder.AppendLine();
            }
            chatHistoryText.text = builder.ToString();
            Canvas.ForceUpdateCanvases();
            if (chatScroll != null)
                chatScroll.verticalNormalizedPosition = 0f;
        }

        private static string EscapeRichText(string value) =>
            (value ?? string.Empty).Replace("<", "&lt;").Replace(">", "&gt;");


        /// <summary>Authored HUD/Menu button entry point. Escape still uses the same path.</summary>
        public void TogglePauseMenu()
        {
#if !UNITY_SERVER
            if (!GameplayVisible)
                return;
            if (PauseMenuOpen)
                ClosePauseMenu();
            else
                OpenPauseMenu();
#endif
        }

        private void OpenPauseMenu()
        {
            if (!GameplayVisible || PauseMenuOpen)
                return;
            SetPauseMenuVisible(true, releaseCapture: false);
            if (!_pauseCaptureHeld)
            {
                LocalClientInputGate.AcquirePointerUi();
                _pauseCaptureHeld = true;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EventSystem.current?.SetSelectedGameObject(resumeButton != null ? resumeButton.gameObject : null);
        }

        private void ClosePauseMenu()
        {
            SetPauseMenuVisible(false, releaseCapture: true);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        private void SetPauseMenuVisible(bool visible, bool releaseCapture)
        {
            if (pauseMenuRoot != null)
                pauseMenuRoot.SetActive(visible);
            if (!visible && releaseCapture)
                ReleasePauseCapture();
        }

        private void ReleasePauseCapture()
        {
            if (!_pauseCaptureHeld)
                return;
            _pauseCaptureHeld = false;
            LocalClientInputGate.ReleasePointerUi();
        }

        private void OpenOptions()
        {
            ClosePauseMenu();
            if (StandaloneClientUIRoot.Instance != null)
            {
                StandaloneClientUIRoot.Instance.OpenControls();
                return;
            }
            if (_legacyRoot == null || _legacyRoot.Windows == null || !_legacyRoot.Windows.Open(ClientUIPanelId.SettingsWindow))
                PresentLocalSystemMessage("Options window is not available yet.");
        }

        private void QuitGame()
        {
            if (_manager != null && _manager.IsClient)
                _manager.StopClient();
            Application.Quit();
        }

        private void SetInteractionActionCameraActive(bool active)
        {
            if (_ownerLocomotionCamera == null)
            {
                if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
                    _ownerLocomotionCamera = owner.GetComponent<PlayerEntityLocomotionCameraController>();
            }

            _ownerLocomotionCamera?.SetInteractionActionCameraActive(active);
        }

        private void RefreshCombatTargetPresentation()
        {
            if (_interactionOverlayActive || PauseMenuOpen || (_interaction != null && _interaction.IsMenuVisible))
            {
                SetCombatTargetVisible(false);
                return;
            }

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner == null)
            {
                SetCombatTargetVisible(false);
                return;
            }

            PlayerEntityLocomotionCameraController cameraController = owner.GetComponent<PlayerEntityLocomotionCameraController>();
            if (cameraController == null || !cameraController.CombatCameraActive)
            {
                SetCombatTargetVisible(false);
                _nextTargetRefreshAt = 0d;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextTargetRefreshAt)
                return;
            _nextTargetRefreshAt = now + Mathf.Max(0.02f, targetRefreshIntervalSeconds);

            if (!TryResolveReticleCombatTarget(owner, out PlayerEntityClient target, out string label, out float distance))
            {
                SetCombatTargetVisible(false);
                return;
            }

            if (combatTargetText != null)
                combatTargetText.text = $"{label}   {distance:0.0}m";

            if (combatTargetHealthSlider != null)
            {
                bool showPopulationHealth = target != null && target.IsPopulationPresentation;
                combatTargetHealthSlider.gameObject.SetActive(showPopulationHealth);
                if (showPopulationHealth)
                    combatTargetHealthSlider.value = GetPopulationEstimatedHealth(target) / 100f;
            }

            SetCombatTargetVisible(true);
        }

        private void SetCombatTargetVisible(bool visible)
        {
            if (combatTargetRoot != null && combatTargetRoot.activeSelf != visible)
                combatTargetRoot.SetActive(visible);
        }

        /// <summary>
        /// Canonical center-reticle actor resolver shared by combat presentation and contextual
        /// interaction. Player and Population acquisition must not maintain separate ray/bounds
        /// implementations: both consume this one presentation registry + physics/occlusion path.
        /// </summary>
        public bool TryResolveReticleActor(out PlayerEntityClient resolved, out float distance)
        {
            resolved = null;
            distance = float.PositiveInfinity;

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner == null)
                return false;

            Camera camera = Camera.main;
            if (camera == null)
                return false;

            float maxDistance = Mathf.Min(Mathf.Max(1f, targetRaycastDistance), CombatRangePolicy.ClientTargetRaycastDistance);
            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            int hitCount = Physics.RaycastNonAlloc(ray, _targetHits, maxDistance, ~0, QueryTriggerInteraction.Collide);

            Transform ownerNetworkRoot = owner.transform;
            Transform ownerPresentationRoot = owner.PresentationTransform;
            float nearestBlockingDistance = float.PositiveInfinity;
            PlayerEntityClient physicalTarget = null;
            float physicalTargetDistance = float.PositiveInfinity;

            for (int i = 0; i < hitCount; ++i)
            {
                Collider collider = _targetHits[i].collider;
                if (collider == null)
                    continue;

                Transform hitTransform = collider.transform;
                if (IsUnderRoot(hitTransform, ownerNetworkRoot) || IsUnderRoot(hitTransform, ownerPresentationRoot))
                    continue;

                PlayerEntityClient candidate = null;
                if (PlayerEntityClient.TryGetByPresentationTransform(hitTransform, out PlayerEntityClient presented) &&
                    presented != null &&
                    (presented.IsPopulationPresentation || !presented.IsDeadPresentation) &&
                    (presented.IsPopulationPresentation || presented.IsTargetableRemotePlayer))
                {
                    candidate = presented;
                }
                else
                {
                    PlayerEntityNetwork network = collider.GetComponentInParent<PlayerEntityNetwork>();
                    if (network != null &&
                        !network.IsOwnerClient &&
                        PlayerEntityClient.TryGetByNetwork(network, out presented) &&
                        presented != null &&
                        !presented.IsDeadPresentation &&
                        (presented.IsPopulationPresentation || presented.IsTargetableRemotePlayer))
                    {
                        candidate = presented;
                    }
                }

                if (candidate != null)
                {
                    if (_targetHits[i].distance < physicalTargetDistance)
                    {
                        physicalTarget = candidate;
                        physicalTargetDistance = _targetHits[i].distance;
                    }
                    continue;
                }

                if (!collider.isTrigger && _targetHits[i].distance < nearestBlockingDistance)
                    nearestBlockingDistance = _targetHits[i].distance;
            }

            if (physicalTarget != null && physicalTargetDistance <= nearestBlockingDistance + 0.01f)
            {
                resolved = physicalTarget;
                distance = physicalTargetDistance;
            }

            IReadOnlyList<PlayerEntityClient> clients = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < clients.Count; ++i)
            {
                PlayerEntityClient client = clients[i];
                if (client == null || client == owner || client.PresentationTransform == null)
                    continue;
                if (client.IsDeadPresentation && !client.IsPopulationPresentation)
                    continue;
                if (!client.IsPopulationPresentation && !client.IsTargetableRemotePlayer)
                    continue;

                if (!TryGetPresentationBounds(client.PresentationTransform, out Bounds bounds) ||
                    !bounds.IntersectRay(ray, out float candidateDistance) ||
                    candidateDistance < 0f || candidateDistance > maxDistance ||
                    candidateDistance >= distance ||
                    IsPresentationOccluded(
                        client.PresentationTransform,
                        ownerNetworkRoot,
                        ownerPresentationRoot,
                        candidateDistance,
                        hitCount))
                {
                    continue;
                }

                resolved = client;
                distance = candidateDistance;
            }

            return resolved != null;
        }

        private bool TryResolveReticleCombatTarget(PlayerEntityClient owner, out PlayerEntityClient target, out string label, out float distance)
        {
            target = null;
            label = string.Empty;
            distance = float.PositiveInfinity;

            if (!TryResolveReticleActor(out PlayerEntityClient client, out distance) || client == null)
                return false;

            target = client;
            label = client.DisplayName;
            if (string.IsNullOrWhiteSpace(label))
                label = client.IsPopulationPresentation ? "Target" : "Player";
            return true;
        }

        private void OnParticipantCombatDamage(CombatDamageWire damage)
        {
            if (damage.amount <= 0 || !damage.target.IsValid)
                return;

            PlayerEntityClient target = ResolveClientTarget(damage.target);
            if (target == null || !target.IsPopulationPresentation)
                return;

            ulong key = MakePopulationEstimateKey(damage.target.objectId, damage.target.generation);
            int current = _populationEstimatedHealth.TryGetValue(key, out int known) ? known : 100;
            bool killed = damage.ResultCode == Game.Shared.Combat.CombatDamageResultCode.Killed ||
                          (damage.Flags & Game.Shared.Combat.CombatDamagePresentationFlags.Killed) != 0;
            _populationEstimatedHealth[key] = killed ? 0 : Mathf.Clamp(current - damage.amount, 1, 100);
        }

        private int GetPopulationEstimatedHealth(PlayerEntityClient target)
        {
            if (target == null || !target.IsPopulationPresentation)
                return 100;
            if (target.IsDeadPresentation)
                return 0;

            PlayerEntityNetwork network = target.NetworkBridge;
            if (network == null)
                return 100;

            ulong key = MakePopulationEstimateKey(network.ObjectId, network.Generation);
            return _populationEstimatedHealth.TryGetValue(key, out int health)
                ? Mathf.Clamp(health, 0, 100)
                : 100;
        }

        private static PlayerEntityClient ResolveClientTarget(PlayerTargetReferenceWire reference)
        {
            IReadOnlyList<PlayerEntityClient> clients = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < clients.Count; ++i)
            {
                PlayerEntityClient client = clients[i];
                PlayerEntityNetwork network = client != null ? client.NetworkBridge : null;
                if (network != null && network.ObjectId == reference.objectId && network.Generation == reference.generation)
                    return client;
            }
            return null;
        }

        private static ulong MakePopulationEstimateKey(uint objectId, ushort generation) =>
            ((ulong)objectId << 16) | generation;

        private static bool TryGetCombatLabelFromCollider(Collider collider, out string label)
        {
            label = string.Empty;
            if (collider == null)
                return false;

            if (PlayerEntityClient.TryGetByPresentationTransform(collider.transform, out PlayerEntityClient presented) &&
                presented != null && !presented.IsDeadPresentation &&
                (presented.IsPopulationPresentation || presented.IsTargetableRemotePlayer))
            {
                label = string.IsNullOrWhiteSpace(presented.DisplayName)
                    ? (presented.IsPopulationPresentation ? "Target" : "Player")
                    : presented.DisplayName;
                return true;
            }

            PlayerEntityNetwork network = collider.GetComponentInParent<PlayerEntityNetwork>();
            if (network != null && !network.IsOwnerClient &&
                PlayerEntityClient.TryGetByNetwork(network, out presented) &&
                presented != null && !presented.IsDeadPresentation &&
                (presented.IsPopulationPresentation || presented.IsTargetableRemotePlayer))
            {
                label = string.IsNullOrWhiteSpace(presented.DisplayName)
                    ? (presented.IsPopulationPresentation ? "Target" : "Player")
                    : presented.DisplayName;
                return true;
            }

            ClientInteractionTargetMarker marker = collider.GetComponentInParent<ClientInteractionTargetMarker>();
            if (marker == null)
                return false;

            switch (marker.TargetKind)
            {
                case InteractionTargetKind.PlayerEntity:
                case InteractionTargetKind.PopulationEntity:
                case InteractionTargetKind.NetworkEntity:
                case InteractionTargetKind.CombatTestTarget:
                    label = string.IsNullOrWhiteSpace(marker.DisplayName) ? marker.gameObject.name : marker.DisplayName;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetPresentationBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
                return false;

            _rendererScratch.Clear();
            root.GetComponentsInChildren<Renderer>(true, _rendererScratch);
            bool hasBounds = false;
            for (int i = 0; i < _rendererScratch.Count; ++i)
            {
                Renderer renderer = _rendererScratch[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                Bounds rendererBounds = renderer.bounds;
                if (rendererBounds.size.sqrMagnitude <= 0.000001f)
                    continue;
                if (!hasBounds)
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(rendererBounds);
                }
            }
            _rendererScratch.Clear();
            return hasBounds;
        }

        private bool IsPresentationOccluded(
            Transform presentationRoot,
            Transform ownerNetworkRoot,
            Transform ownerPresentationRoot,
            float targetDistance,
            int hitCount)
        {
            for (int i = 0; i < hitCount; ++i)
            {
                RaycastHit hit = _targetHits[i];
                if (hit.distance + 0.01f >= targetDistance)
                    continue;
                Collider collider = hit.collider;
                if (collider == null || collider.isTrigger)
                    continue;
                Transform hitTransform = collider.transform;
                if (IsUnderRoot(hitTransform, presentationRoot) ||
                    IsUnderRoot(hitTransform, ownerNetworkRoot) ||
                    IsUnderRoot(hitTransform, ownerPresentationRoot))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static bool IsUnderRoot(Transform candidate, Transform root) =>
            candidate != null && root != null && (candidate == root || candidate.IsChildOf(root));

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject authoredVisualRoot,
            Slider authoredHealthSlider,
            Text authoredHealthText,
            Slider authoredManaSlider,
            Text authoredManaText,
            Slider authoredStaminaSlider,
            Text authoredStaminaText,
            ScrollRect authoredChatScroll,
            Text authoredChatHistoryText,
            InputField authoredChatInput,
            Button authoredChatSendButton,
            GameObject authoredCombatTargetRoot,
            Text authoredCombatTargetText,
            Slider authoredCombatTargetHealthSlider,
            GameObject authoredPauseMenuRoot,
            Button authoredResumeButton,
            Button authoredOptionsButton,
            Button authoredQuitButton)
        {
            ConfigureForEditor(
                authoredVisualRoot,
                authoredHealthSlider,
                authoredHealthText,
                authoredManaSlider,
                authoredManaText,
                authoredStaminaSlider,
                authoredStaminaText,
                authoredChatScroll,
                authoredChatHistoryText,
                authoredChatInput,
                authoredChatSendButton,
                authoredCombatTargetRoot,
                authoredCombatTargetText,
                authoredCombatTargetHealthSlider,
                authoredPauseMenuRoot,
                authoredResumeButton,
                authoredOptionsButton,
                authoredQuitButton,
                null,
                null);
        }

        public void ConfigureForEditor(
            GameObject authoredVisualRoot,
            Slider authoredHealthSlider,
            Text authoredHealthText,
            Slider authoredManaSlider,
            Text authoredManaText,
            Slider authoredStaminaSlider,
            Text authoredStaminaText,
            ScrollRect authoredChatScroll,
            Text authoredChatHistoryText,
            InputField authoredChatInput,
            Button authoredChatSendButton,
            GameObject authoredCombatTargetRoot,
            Text authoredCombatTargetText,
            Slider authoredCombatTargetHealthSlider,
            GameObject authoredPauseMenuRoot,
            Button authoredResumeButton,
            Button authoredOptionsButton,
            Button authoredQuitButton,
            GameObject authoredExplorationReticle,
            Text[] authoredHotbarBindingTexts)
        {
            visualRoot = authoredVisualRoot;
            healthSlider = authoredHealthSlider;
            healthText = authoredHealthText;
            manaSlider = authoredManaSlider;
            manaText = authoredManaText;
            staminaSlider = authoredStaminaSlider;
            staminaText = authoredStaminaText;
            chatScroll = authoredChatScroll;
            chatHistoryText = authoredChatHistoryText;
            chatInput = authoredChatInput;
            chatSendButton = authoredChatSendButton;
            combatTargetRoot = authoredCombatTargetRoot;
            combatTargetText = authoredCombatTargetText;
            combatTargetHealthSlider = authoredCombatTargetHealthSlider;
            pauseMenuRoot = authoredPauseMenuRoot;
            resumeButton = authoredResumeButton;
            optionsButton = authoredOptionsButton;
            quitButton = authoredQuitButton;
            explorationReticleRoot = authoredExplorationReticle;
            hotbarBindingTexts = authoredHotbarBindingTexts ?? Array.Empty<Text>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
