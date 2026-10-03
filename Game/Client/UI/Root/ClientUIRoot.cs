using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using Game.Client.Chat;
using Game.Client.Content;
using Game.Client.Targeting;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Recovery;
using Game.Client.UI.Social;
using Game.Client.UI.Interactions;
using Game.Client.UI.Gameplay;
using Game.Client.UI.CharacterSelect;
using Game.Client.WorldItems;
using Game.Shared.Abilities;
using Game.Shared.Chat;
using Game.Shared.Content;
using Game.Shared.Combat;
using Game.Shared.Interactions;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#pragma warning disable 0414

namespace Game.Client.UI.Root
{
    /// <summary>
    /// One drag-and-drop client gameplay UI shell. The prefab owns the already-working
    /// inventory/equipment window and adds event-driven resources, statuses, basic
    /// action/interaction surfaces, text chat, and connection-loss/retry presentation.
    /// It never mutates authoritative gameplay state directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientUIRoot : MonoBehaviour
    {
        private enum ChatViewFilter : byte
        {
            All = 0,
            Local = 1,
            Social = 2,
            System = 3,
        }

        private const string ChatViewFilterPreferenceKey = "MMO.Chat.ViewFilter";

        private struct ResourceBarView
        {
            public CharacterResourceId Id;
            public Slider Slider;
            public Text Label;
        }

        private static ClientUIRoot _instance;
        public static ClientUIRoot Instance => _instance;

        private string _lastLocalSystemMessage = string.Empty;
        private double _lastLocalSystemMessageAt;

        private PlayerEntityGameManager _manager;
        private PlayerInventoryShell _inventoryShell;
        private PlayerLootShell _lootShell;
        private WorldItemPresentationController _worldItemPresentation;
        private ClientInteractionUI _interactionUi;
        private ClientGameplayUIRoot _gameplayUiV2;
        private ClientRecoveryUI _recoveryUi;
        private ClientSocialEconomyUI _socialEconomyUi;
        private ClientTradeStorageUI _tradeStorageUi;
        private IClientChatTransport _chatTransport;
        private readonly ClientChatState _chatState = new ClientChatState();
        private readonly Dictionary<CharacterResourceId, ResourceBarView> _resourceBars =
            new Dictionary<CharacterResourceId, ResourceBarView>();
        private readonly RaycastHit[] _actionCombatRaycastHits = new RaycastHit[16];

        [Header("Authored Master UI - Roots")]
        [SerializeField] private CharacterSelectShell _frontendShell;
        [SerializeField] private GameObject _hudRoot;
        [SerializeField] private GameObject _chatRoot;
        [SerializeField] private GameObject _disconnectOverlay;
        [SerializeField] private GameObject _serviceNoticeRoot;

        [Header("Authored Master UI - Window Framework")]
        [SerializeField] private ClientWindowManager _windowManager;
        [SerializeField] private ClientPopupController _popupController;

        [Header("Authored Master UI - Resources")]
        [SerializeField] private Slider _healthSlider;
        [SerializeField] private Text _healthLabel;
        [SerializeField] private Slider _manaSlider;
        [SerializeField] private Text _manaLabel;
        [SerializeField] private Slider _staminaSlider;
        [SerializeField] private Text _staminaLabel;

        [Header("Authored Master UI - HUD")]
        [SerializeField] private Text _statusEffectsText;
        [SerializeField] private Text _targetText;
        [SerializeField] private Text _interactionText;
        [SerializeField] private Text _systemText;
        [SerializeField] private Text _basicAttackLabel;
        [SerializeField] private Text _playerNameText;
        [SerializeField] private Text _playerLevelText;
        [SerializeField] private Button _characterButton;
        [SerializeField] private Button _inventoryButton;
        [SerializeField] private Button _skillsButton;
        [SerializeField] private Button _mapButton;
        [SerializeField] private Button _menuButton;
        [SerializeField] private Button _menuResumeButton;
        [SerializeField] private Button _menuSettingsButton;
        [SerializeField] private Button _menuLogoutButton;
        [SerializeField] private Button _menuQuitButton;
        [SerializeField] private Text _skillsSummaryText;

        [Header("Authored Master UI - Chat")]
        [SerializeField] private Text _chatHistoryText;
        [SerializeField] private InputField _chatInput;
        [SerializeField] private InputField _whisperTargetInput;
        [SerializeField] private Button _chatChannelButton;
        [SerializeField] private Text _chatChannelLabel;
        [SerializeField] private ScrollRect _chatScroll;
        [SerializeField] private Button _chatSendButton;
        [SerializeField] private Button _chatAllTabButton;
        [SerializeField] private Button _chatLocalTabButton;
        [SerializeField] private Button _chatSocialTabButton;
        [SerializeField] private Button _chatSystemTabButton;

        [Header("Authored Master UI - Overlays")]
        [SerializeField] private Text _disconnectReasonText;
        [SerializeField] private Text _serviceNoticeText;
        [SerializeField] private Button _retryButton;
        [SerializeField] private Button _returnLoginButton;
        [SerializeField] private Button _quitButton;
        private ChatChannel _selectedChatChannel = ChatChannel.Local;
        private ChatViewFilter _chatViewFilter = ChatViewFilter.Local;
        private bool _targetOverlaySuppressed;
        private bool _subscribed;
        private bool _gameplaySessionActive;
        private bool _ownerResourceReconciliationInFlight;
        private long _localCharacterId;
        private PlayerEntityNetwork _selectedTarget;
        private CombatTargetReferenceWire _selectedCombatTarget;
        private string _selectedCombatTargetLabel = string.Empty;
        private Vector3 _selectedNonPlayerCombatTargetPosition;
        private bool _hasSelectedNonPlayerCombatTargetPosition;
        private uint _interactionSequence;
        private GameObject _playerCastBarRoot;
        private RectTransform _playerCastProgress;
        private Text _playerCastLabel;
        private bool _playerCastActive;
        private float _playerCastStartedAt;
        private float _playerCastDuration;
        private string _playerCastAbilityLabel = string.Empty;

        public PlayerEntityGameManager Manager => _manager;
        public bool GameplaySessionActive => _gameplaySessionActive;
        public PlayerEntityNetwork SelectedPlayerTarget => _selectedTarget;
        public CombatTargetReferenceWire SelectedCombatTarget => _selectedCombatTarget;
        public ClientInteractionUI InteractionUi => _interactionUi;
        public ClientRecoveryUI Recovery => _recoveryUi;
        public PlayerLootShell Loot => _lootShell;
        public ClientWindowManager Windows => _windowManager;
        public ClientPopupController Popups => _popupController;
        public event Action<PlayerEntityNetwork> SelectedPlayerTargetChanged;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
            return;
#else
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            _windowManager = _windowManager != null ? _windowManager : GetComponent<ClientWindowManager>();
            _popupController = _popupController != null
                ? _popupController
                : GetComponentInChildren<ClientPopupController>(true);
            _gameplayUiV2 = GetComponent<ClientGameplayUIRoot>();

            EnsureEventSystem();
            int savedChatFilter = PlayerPrefs.GetInt(ChatViewFilterPreferenceKey, (int)ChatViewFilter.Local);
            _chatViewFilter = Enum.IsDefined(typeof(ChatViewFilter), savedChatFilter)
                ? (ChatViewFilter)savedChatFilter
                : ChatViewFilter.All;
            WireAuthoredPresentation();
            ValidateAuthoredPresentation();
            SetTargetFrameVisible(false);
            SetStatusEffectsVisible(false);
            _inventoryShell = GetComponent<PlayerInventoryShell>();
            _lootShell = GetComponent<PlayerLootShell>();
            if (_lootShell != null)
                _lootShell.BindRoot(this);
            else
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing PlayerLootShell. Restore the authored PlayerLootShell binding on ClientUIRoot.prefab.", this);
            _worldItemPresentation = GetComponent<WorldItemPresentationController>();
            if (_worldItemPresentation == null)
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing WorldItemPresentationController. Restore the canonical ClientUIRoot.prefab.", this);
            _interactionUi = GetComponent<ClientInteractionUI>();
            if (_interactionUi == null)
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing ClientInteractionUI. Restore the canonical ClientUIRoot.prefab.", this);
            else
                _interactionUi.BindRoot(this);

            // Recovery presentation is a thin controller over the existing semantic window
            // registry. It creates only its minimal test views and performs no authority work.
            _recoveryUi = GetComponent<ClientRecoveryUI>();
            if (_recoveryUi == null)
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing ClientRecoveryUI. Restore the canonical ClientUIRoot.prefab; runtime will not rebuild this UI.", this);
            else if (!_recoveryUi.HasAuthoredBindings)
                Debug.LogError("[ClientUI] ClientRecoveryUI is missing authored recovery view references. Restore the authored recovery references on ClientUIRoot.prefab.", this);
            else
                _recoveryUi.BindRoot(this);

            _socialEconomyUi = GetComponent<ClientSocialEconomyUI>();
            if (_socialEconomyUi == null)
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing ClientSocialEconomyUI. Restore the canonical ClientUIRoot.prefab; runtime will not rebuild Social UI.", this);
            else if (!_socialEconomyUi.HasAuthoredBindings)
                Debug.LogError("[ClientUI] ClientSocialEconomyUI is missing authored references. Restore the authored Social references on ClientUIRoot.prefab.", this);
            else
                _socialEconomyUi.BindRoot(this);

            _tradeStorageUi = GetComponent<ClientTradeStorageUI>();
            if (_tradeStorageUi == null)
                Debug.LogError("[ClientUI] ClientUIRoot prefab is missing ClientTradeStorageUI. Restore the authored Trade/Storage bindings on ClientUIRoot.prefab.", this);
            else if (!_tradeStorageUi.HasAuthoredBindings)
                Debug.LogError("[ClientUI] ClientTradeStorageUI is missing authored Trade/Storage references. Restore the canonical ClientUIRoot.prefab.", this);
            SetGameplayVisible(false);
            SetDisconnectVisible(false);
            SetServiceNoticeVisible(false);
            _chatState.Changed += RenderChatHistory;
            PlayerEntityTargetSelection.SelectedChanged += OnCanonicalPlayerTargetChanged;
            OnCanonicalPlayerTargetChanged(PlayerEntityTargetSelection.SelectedClient);
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindManager();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (_playerCastActive)
                UpdatePlayerCastBarPresentation();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            _worldItemPresentation?.Unbind();
            _lootShell?.Bind(null);
            _lootShell?.BindRoot(null);
            _interactionUi?.BindRoot(null);
            _recoveryUi?.BindManager(null);
            _recoveryUi?.BindRoot(null);
            _socialEconomyUi?.BindManager(null);
            _socialEconomyUi?.BindRoot(null);
            _tradeStorageUi?.UnbindManager();
            UnsubscribeManager();
            _chatState.Changed -= RenderChatHistory;
            PlayerEntityTargetSelection.SelectedChanged -= OnCanonicalPlayerTargetChanged;
            if (_instance == this)
                _instance = null;
#endif
        }

        private void BindManager()
        {
            if (_manager == null)
                _manager = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == null)
            {
                SetSystemMessage("PlayerEntityGameManager was not found in the scene.");
                return;
            }

            SubscribeManager();
            _worldItemPresentation?.Bind(_manager);
            _lootShell?.Bind(_manager);
            _interactionUi?.BindRoot(this);
            _recoveryUi?.BindManager(_manager);
            _socialEconomyUi?.BindManager(_manager);
            _tradeStorageUi?.BindManager(_manager);

            // Reuse the established owner-cache path. Do not infer world admission from
            // spawned PlayerEntity objects; ClientWorldEntered and resource snapshots own
            // the gameplay UI lifecycle.
            SetDisconnectVisible(false);
            SetServiceNoticeVisible(false);
            if (_manager.LatestPlayerResources.success)
            {
                SetGameplayVisible(true);
                ApplyResourceSnapshot(_manager.LatestPlayerResources);
            }
            if (_manager.LatestPlayerStatusEffects.success)
                ApplyStatusSnapshot(_manager.LatestPlayerStatusEffects);
            if (_manager.LatestProgression.success)
                RenderSkills(_manager.LatestProgression);
        }

        private void SubscribeManager()
        {
            if (_subscribed || _manager == null)
                return;

            _manager.ClientConnectionEstablished += OnClientConnectionEstablished;
            _manager.ClientConnectionClosed += OnClientConnectionClosed;
            _manager.ClientWorldEntered += OnClientWorldEntered;
            _manager.ClientServiceStatusChanged += OnClientServiceStatusChanged;
            _manager.PlayerResourcesSnapshotReceived += OnResourceSnapshot;
            _manager.PlayerResourceDeltaReceived += OnResourceDelta;
            _manager.PlayerStatusEffectsSnapshotReceived += OnStatusSnapshot;
            _manager.PlayerStatusEffectDeltaReceived += OnStatusDelta;
            _manager.ProgressionSnapshotReceived += OnProgressionSnapshot;
            _manager.PlayerCombatDamageReceived += OnCombatDamageReceived;
            _manager.PlayerAbilityCastStateReceived += OnAbilityCastStateReceived;
            _manager.PlayerCombatOwnerStateReceived += OnCombatOwnerStateReceived;
            _manager.ContextInteractionResultReceived += OnInteractionResultReceived;
            _manager.WorldItemsChangedReceived += OnWorldItemsChangedReceived;
            _manager.WorldItemInteractionResultReceived += OnWorldItemInteractionResultReceived;
            _chatTransport?.Dispose();
            _chatTransport = new PlayerEntityChatTransport(_manager);
            _chatTransport.MessageReceived += OnChatMessage;
            _subscribed = true;
        }

        private void UnsubscribeManager()
        {
            if (!_subscribed || _manager == null)
                return;

            _manager.ClientConnectionEstablished -= OnClientConnectionEstablished;
            _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            _manager.ClientWorldEntered -= OnClientWorldEntered;
            _manager.ClientServiceStatusChanged -= OnClientServiceStatusChanged;
            _manager.PlayerResourcesSnapshotReceived -= OnResourceSnapshot;
            _manager.PlayerResourceDeltaReceived -= OnResourceDelta;
            _manager.PlayerStatusEffectsSnapshotReceived -= OnStatusSnapshot;
            _manager.PlayerStatusEffectDeltaReceived -= OnStatusDelta;
            _manager.ProgressionSnapshotReceived -= OnProgressionSnapshot;
            _manager.PlayerCombatDamageReceived -= OnCombatDamageReceived;
            _manager.PlayerAbilityCastStateReceived -= OnAbilityCastStateReceived;
            _manager.PlayerCombatOwnerStateReceived -= OnCombatOwnerStateReceived;
            _manager.ContextInteractionResultReceived -= OnInteractionResultReceived;
            _manager.WorldItemsChangedReceived -= OnWorldItemsChangedReceived;
            _manager.WorldItemInteractionResultReceived -= OnWorldItemInteractionResultReceived;
            if (_chatTransport != null)
            {
                _chatTransport.MessageReceived -= OnChatMessage;
                _chatTransport.Dispose();
                _chatTransport = null;
            }
            _subscribed = false;
        }

        private void OnClientConnectionEstablished()
        {
            _gameplaySessionActive = false;
            _ownerResourceReconciliationInFlight = false;
            _localCharacterId = 0;
            ClearSelectedTarget();
            SetDisconnectVisible(false);
            SetServiceNoticeVisible(false);
            ResetResourcePresentationUnknown();
            SetGameplayVisible(false);
            SetSystemMessage("Connected. Complete login/character entry to resume gameplay.");
        }

        private void OnClientWorldEntered(long characterId)
        {
            _gameplaySessionActive = true;
            _ownerResourceReconciliationInFlight = false;
            _localCharacterId = characterId;
            ClearSelectedTarget();

            // World entry always starts from a clean authored UI state. No gameplay window
            // is allowed to carry over from frontend selection, reconnect, or stale local UI.
            _windowManager?.CloseAllGameplayWindows();
            EventSystem.current?.SetSelectedGameObject(null);
            ResetResourcePresentationUnknown();
            SetGameplayVisible(true);
            SetDisconnectVisible(false);
            SetServiceNoticeVisible(false);
            if (_manager != null && _manager.LatestPlayerResources.success)
                ApplyResourceSnapshot(_manager.LatestPlayerResources);

            _chatState.Clear();
            _chatState.AddSystem("Chat ready. Local is proximity/AOI chat. Use /w \"Name\" message, /p message, or /g message.");
            _chatState.AddSystem("Social: /party help, /guild help. Visibility: /hide. Report: /report \"Player Name\" reason.");
            HidePlayerCastBar();
            _interactionUi?.HideAllInteractionSurfaces();
            RefreshPlayerIdentityPresentation();
            RenderSkills(_manager != null ? _manager.LatestProgression : default);
            SetSystemMessage($"Character {characterId} entered the world.");
        }

        private void OnClientConnectionClosed(ClientDisconnectNotice notice)
        {
            bool wasInWorld = _gameplaySessionActive;
            _gameplaySessionActive = false;
            _ownerResourceReconciliationInFlight = false;
            _localCharacterId = 0;
            ClearSelectedTarget();
            ClearPlayerIdentityPresentation();
            HidePlayerCastBar();
            ResetResourcePresentationUnknown();
            _interactionUi?.HideAllInteractionSurfaces();

            // A dropped GameServer connection invalidates the authenticated gameplay
            // session. Do not trap the player behind a second blocking reconnect modal:
            // the existing CharacterSelectShell already owns connection/auth admission and
            // knows how to re-establish the normal frontend flow. Reuse it directly.
            SetGameplayVisible(false);
            SetServiceNoticeVisible(false);
            SetDisconnectVisible(false);

            if (!wasInWorld)
                return;

            string reason = notice.Unexpected && !string.IsNullOrWhiteSpace(notice.Message)
                ? notice.Message
                : "Disconnected from game server.";

            CharacterSelectShell frontend = _frontendShell != null
                ? _frontendShell
                : FindFirstObjectByType<CharacterSelectShell>(FindObjectsInactive.Include);
            if (frontend != null)
                frontend.ReturnToFrontend(reason);
        }

        private void OnClientServiceStatusChanged(ClientServiceStatusNotice notice)
        {
            if (notice.Service != PlayerServiceKind.Backend)
                return;

            // Backend availability remains transport/service state, not a blocking gameplay
            // surface. The frontend owns admission/status presentation. Do not put a persistent
            // banner over gameplay.
            SetServiceNoticeVisible(false);
        }

        private void ApplyLatestServiceStatus() => SetServiceNoticeVisible(false);

        private void OnResourceSnapshot(PlayerResourcesResponseMessage snapshot) =>
            ApplyResourceSnapshot(snapshot);

        private void OnResourceDelta(PlayerResourceDeltaMessage delta)
        {
            if (!_resourceBars.TryGetValue(delta.ResourceId, out ResourceBarView view))
                return;
            ApplyResource(view, delta.current, delta.minimum, delta.maximum);
        }

        private void ApplyResourceSnapshot(PlayerResourcesResponseMessage snapshot)
        {
            if (!snapshot.success || snapshot.resources == null)
                return;

            _gameplaySessionActive = true;
            SetGameplayVisible(true);
            for (int i = 0; i < snapshot.resources.Length; ++i)
            {
                CharacterResourceWire resource = snapshot.resources[i];
                CharacterResourceId id = (CharacterResourceId)resource.id;
                if (_resourceBars.TryGetValue(id, out ResourceBarView view))
                    ApplyResource(view, resource.current, resource.minimum, resource.maximum);
            }
        }

        private static void ApplyResource(ResourceBarView view, int current, int minimum, int maximum)
        {
            if (view.Slider != null)
            {
                view.Slider.minValue = minimum;
                view.Slider.maxValue = Math.Max(minimum + 1, maximum);
                view.Slider.SetValueWithoutNotify(Mathf.Clamp(current, minimum, maximum));
            }
            if (view.Label != null)
                view.Label.text = $"{current:N0} / {maximum:N0}";
        }

        private void OnStatusSnapshot(PlayerStatusEffectsResponseMessage snapshot) => ApplyStatusSnapshot(snapshot);

        private void OnStatusDelta(PlayerStatusEffectDeltaMessage delta)
        {
            if (_manager != null && _manager.LatestPlayerStatusEffects.success)
                ApplyStatusSnapshot(_manager.LatestPlayerStatusEffects);
        }

        private void ApplyStatusSnapshot(PlayerStatusEffectsResponseMessage snapshot)
        {
            if (_statusEffectsText == null)
                return;
            if (!snapshot.success || snapshot.effects == null || snapshot.effects.Length == 0)
            {
                _statusEffectsText.text = string.Empty;
                SetStatusEffectsVisible(false);
                return;
            }

            SetStatusEffectsVisible(true);
            var builder = new StringBuilder(256);
            int count = Math.Min(snapshot.effects.Length, 10);
            for (int i = 0; i < count; ++i)
            {
                StatusEffectWire effect = snapshot.effects[i];
                string prefix = (StatusEffectClassification)effect.classification == StatusEffectClassification.Debuff
                    ? "[-]"
                    : "[+]";
                builder.Append(prefix).Append(' ').Append(
                    string.IsNullOrWhiteSpace(effect.displayName) ? effect.definitionId : effect.displayName);
                if (effect.stacks > 1)
                    builder.Append(" x").Append(effect.stacks);
                builder.AppendLine();
            }
            if (snapshot.effects.Length > count)
                builder.Append("+").Append(snapshot.effects.Length - count).Append(" more");
            _statusEffectsText.text = builder.ToString();
        }

        public void CyclePlayerTarget()
        {
            if (!_gameplaySessionActive)
                return;

            if (!PlayerEntityTargetSelection.CycleRemotePlayer())
            {
                ClearSelectedTarget();
                SetSystemMessage("No remote player target is available.");
            }
        }

        public void RequestActionPrimaryFire()
        {
            if (_manager == null || !_gameplaySessionActive)
                return;

            // Compact combat input is targetless. Local acquisition is presentation only
            // and lets the clean client suppress an obviously impossible hard-cap contact.
            bool acquiredTarget = TryAcquireActionCombatTarget();
            if (acquiredTarget && !CanSendSelectedBasicAttack(out string rangeReason))
            {
                SetSystemMessage(rangeReason);
                return;
            }

            SubmitCombatAction(BasicAttackInputKind.Primary);
        }

        private bool TryAcquireActionCombatTarget()
        {
            Camera camera = Camera.main;
            if (camera == null)
                return false;

            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            int count = Physics.RaycastNonAlloc(ray, _actionCombatRaycastHits, CombatRangePolicy.ClientTargetRaycastDistance, ~0, QueryTriggerInteraction.Collide);
            if (count <= 0)
                return false;

            Transform ownerRoot = null;
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner))
                ownerRoot = owner.transform;

            int nearestIndex = -1;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < count; ++i)
            {
                Collider collider = _actionCombatRaycastHits[i].collider;
                if (collider == null)
                    continue;
                Transform hitTransform = collider.transform;
                if (ownerRoot != null && (hitTransform == ownerRoot || hitTransform.IsChildOf(ownerRoot)))
                    continue;
                if (_actionCombatRaycastHits[i].distance < nearestDistance)
                {
                    nearestDistance = _actionCombatRaycastHits[i].distance;
                    nearestIndex = i;
                }
            }

            if (nearestIndex < 0)
                return false;

            Collider nearest = _actionCombatRaycastHits[nearestIndex].collider;
            PlayerEntityNetwork player = nearest.GetComponentInParent<PlayerEntityNetwork>();
            PlayerEntityClient presented = null;
            if (player != null)
                PlayerEntityClient.TryGetByNetwork(player, out presented);
            else if (PlayerEntityClient.TryGetByPresentationTransform(nearest.transform, out presented))
                player = presented.NetworkBridge;

            if (player != null && player.ObjectId != 0 && player.Generation != 0 && !player.IsOwnerClient)
            {
                if (presented != null && presented.IsPopulationPresentation)
                {
                    Vector3 worldPosition = presented.PresentationTransform != null
                        ? presented.PresentationTransform.position
                        : player.transform.position;
                    SelectPopulationTarget(
                        presented.PopulationActorId,
                        presented.PopulationGeneration,
                        presented.DisplayName,
                        worldPosition);
                }
                else
                {
                    SelectPlayerTarget(player);
                }
                return true;
            }

            ClientInteractionTargetMarker targetMarker = nearest.GetComponentInParent<ClientInteractionTargetMarker>();
            if (targetMarker != null && targetMarker.PrimaryId > 0)
            {
                if (targetMarker.TargetKind == InteractionTargetKind.PopulationEntity && targetMarker.Generation != 0)
                {
                    SelectPopulationTarget(
                        targetMarker.PrimaryId,
                        targetMarker.Generation,
                        targetMarker.DisplayName,
                        targetMarker.transform.position);
                    return true;
                }

                if (targetMarker.TargetKind == InteractionTargetKind.CombatTestTarget)
                {
                    SelectCombatTestTarget(targetMarker.PrimaryId, targetMarker.DisplayName, targetMarker.transform.position);
                    return true;
                }
            }

            // Nearest collider was geometry or a non-combat target: do not acquire through it.
            return false;
        }

        public void RequestSelectedBasicAttack(BasicAttackInputKind inputKind = BasicAttackInputKind.Light)
        {
            RequestCombatAction(inputKind);
        }

        /// <summary>
        /// Canonical client combat input entry point. Uses the existing compact targetless
        /// combat intent; the server independently resolves contact and authoritative range.
        /// </summary>
        public void RequestCombatAction(BasicAttackInputKind inputKind)
        {
            if (_manager == null || !_gameplaySessionActive)
                return;

            if (_selectedCombatTarget.IsValid && !CanSendSelectedBasicAttack(out string rangeReason))
            {
                SetSystemMessage(rangeReason);
                return;
            }

            SubmitCombatAction(inputKind);
        }

        private void SubmitCombatAction(BasicAttackInputKind inputKind)
        {
            if (_manager == null || !_gameplaySessionActive)
                return;

            float yaw = 0f;
            float pitch = 0f;
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
            {
                yaw = owner.PresentationYaw;
                PlayerEntityLocomotionCameraController camera =
                    owner.GetComponent<PlayerEntityLocomotionCameraController>();
                if (camera != null)
                {
                    yaw = camera.CurrentFacingYaw;
                    pitch = camera.CurrentAimPitchDegrees;

                    PlayerCombatOwnerStateMessage ownerState = _manager.LatestCombatOwnerState;
                    if (ownerState.revision > 0 &&
                        ownerState.Mode == BasicAttackMode.Firearm)
                    {
                        camera.TryGetPrecisionAim(out yaw, out pitch);
                    }
                }
            }

            if (!_manager.TrySubmitCombatAction(inputKind, yaw, pitch, out BasicAttackResultCode localResult) &&
                localResult != BasicAttackResultCode.RejectedRecovery)
            {
                SetSystemMessage(BasicAttackFeedbackText(localResult));
            }
        }

        public void RequestSelectedInspect()
        {
            RequestSelectedInspectAsync().Forget();
        }

        public void RequestRespawn()
        {
            RequestRespawnAsync().Forget();
        }

        public void RequestReloadOrRespawn()
        {
            RequestReloadOrRespawnAsync().Forget();
        }

        /// <summary>
        /// Presentation hook for data-driven action-bar bindings. Production content
        /// currently has no ability definitions, so this is intentionally not bound to
        /// a fabricated hotbar ability in this parity slice.
        /// </summary>
        public void RequestAbility(string abilityDefinitionId, int rank = 1)
        {
            RequestAbilityAsync(abilityDefinitionId, rank).Forget();
        }

        private async UniTaskVoid RequestSelectedInspectAsync()
        {
            if (_manager == null || !TryGetSelectedTarget(out uint objectId, out ushort generation))
            {
                PresentSystemMessage("Select a remote player with T.");
                return;
            }

            await RequestPlayerInteractionAsync(
                objectId,
                generation,
                InteractionCategoryId.Interact,
                InteractionActionId.Inspect);
        }

        public async UniTask<ContextInteractionResponseMessage> RequestPlayerInteractionAsync(
            uint targetObjectId,
            ushort targetGeneration,
            InteractionCategoryId categoryId,
            InteractionActionId actionId)
        {
            if (_manager == null)
                BindManager();
            uint sequence = NextInteractionSequence();
            if (_manager == null)
                return ContextInteractionResponseMessage.Failed(
                    sequence,
                    InteractionTargetReferenceWire.Player(targetObjectId, targetGeneration),
                    categoryId,
                    actionId,
                    InteractionResultCode.InvalidState,
                    "client game manager is unavailable");
            return await _manager.RequestContextInteractionAsync(
                InteractionTargetReferenceWire.Player(targetObjectId, targetGeneration),
                categoryId,
                actionId,
                sequence);
        }

        private uint NextInteractionSequence()
        {
            uint sequence = unchecked(++_interactionSequence);
            if (sequence == 0)
                sequence = unchecked(++_interactionSequence);
            return sequence;
        }

        private async UniTaskVoid RequestReloadOrRespawnAsync()
        {
            if (_manager == null)
                BindManager();
            if (_manager == null || !_gameplaySessionActive)
                return;

            if (IsLocallyKnownDead())
            {
                RequestRespawnAsync().Forget();
                return;
            }

            SetSystemMessage("Reloading...");
            PlayerReloadResponseMessage response = await _manager.RequestReloadAsync();
            PlayerCombatOwnerStateMessage owner = _manager.LatestCombatOwnerState;
            SetSystemMessage(response.success
                ? $"Reloaded: {response.loadedRounds}/{owner.magazineCapacity}."
                : ReloadFeedbackText(response.resultCode));
        }

        private bool IsLocallyKnownDead()
        {
            if (_manager == null || !_manager.LatestPlayerResources.success)
                return false;
            CharacterResourceWire[] resources = _manager.LatestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
                if ((CharacterResourceId)resources[i].id == CharacterResourceId.Health)
                    return resources[i].current <= resources[i].minimum;
            return false;
        }

        private async UniTaskVoid RequestRespawnAsync()
        {
            if (_manager == null)
                BindManager();
            if (_manager == null || !_gameplaySessionActive)
                return;

            PlayerRespawnResponseMessage response = await _manager.RequestRespawnAsync();
            SetSystemMessage(response.success
                ? "Respawn accepted."
                : $"Respawn rejected: {response.detail}");
        }

        private async UniTaskVoid RequestAbilityAsync(string abilityDefinitionId, int rank)
        {
            if (_manager == null || string.IsNullOrWhiteSpace(abilityDefinitionId))
                return;

            PlayerAbilityRequestAckMessage response = await _manager.RequestBeginAbilityAsync(
                abilityDefinitionId,
                Math.Max(1, rank),
                _selectedCombatTarget);

            if (!response.success)
                SetSystemMessage($"Ability rejected: {response.Failure} ({abilityDefinitionId}).");
        }

        private bool TryGetSelectedTarget(out uint objectId, out ushort generation)
        {
            objectId = 0;
            generation = 0;

            PlayerEntityNetwork selected = PlayerEntityTargetSelection.SelectedNetwork;
            if (selected == null || !selected.IsSpawned || selected.IsOwnerClient)
            {
                ClearSelectedTarget();
                return false;
            }

            generation = selected.Generation;
            objectId = selected.ObjectId;
            if (objectId == 0 || generation == 0)
            {
                ClearSelectedTarget();
                return false;
            }
            return true;
        }

        private void OnCanonicalPlayerTargetChanged(PlayerEntityClient selectedClient)
        {
            PlayerEntityNetwork target = selectedClient != null ? selectedClient.NetworkBridge : null;
            bool changed = !ReferenceEquals(_selectedTarget, target);
            _selectedTarget = target;
            if (target != null && target.ObjectId != 0 && target.Generation != 0)
            {
                _selectedCombatTarget = CombatTargetReferenceWire.Player(target.ObjectId, target.Generation);
                _selectedCombatTargetLabel = string.IsNullOrWhiteSpace(target.DisplayName) ? $"Player {target.ObjectId}" : target.DisplayName;
                _hasSelectedNonPlayerCombatTargetPosition = false;
            }
            else if (_selectedCombatTarget.IsPlayer)
            {
                _selectedCombatTarget = default;
                _selectedCombatTargetLabel = string.Empty;
                _hasSelectedNonPlayerCombatTargetPosition = false;
            }

            if (_targetText != null)
            {
                if (target == null)
                {
                    _targetText.text = string.Empty;
                    SetTargetFrameVisible(false);
                }
                else
                {
                    string displayName = target.DisplayName;
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = $"Player {target.ObjectId}";
                    _targetText.text = displayName;
                    RefreshTargetOverlayVisibility();
                }
            }

            if (changed)
                SelectedPlayerTargetChanged?.Invoke(target);
        }

        public void SelectPopulationTarget(long actorId, ushort generation, string displayName, Vector3 worldPosition)
        {
            if (actorId <= 0 || generation == 0)
                return;
            PlayerEntityTargetSelection.Clear();
            _selectedCombatTarget = CombatTargetReferenceWire.Population(actorId, generation);
            _selectedCombatTargetLabel = string.IsNullOrWhiteSpace(displayName) ? "Population" : displayName;
            _selectedNonPlayerCombatTargetPosition = worldPosition;
            _hasSelectedNonPlayerCombatTargetPosition = true;
            if (_targetText != null)
            {
                _targetText.text = _selectedCombatTargetLabel;
                RefreshTargetOverlayVisibility();
            }
        }

        public void SelectCombatTestTarget(long stableId, string displayName, Vector3 worldPosition)
        {
            if (stableId <= 0)
                return;
            PlayerEntityTargetSelection.Clear();
            _selectedCombatTarget = CombatTargetReferenceWire.CombatTest(stableId);
            _selectedCombatTargetLabel = string.IsNullOrWhiteSpace(displayName) ? "Combat Test Dummy" : displayName;
            _selectedNonPlayerCombatTargetPosition = worldPosition;
            _hasSelectedNonPlayerCombatTargetPosition = true;
            if (_targetText != null)
            {
                _targetText.text = _selectedCombatTargetLabel;
                RefreshTargetOverlayVisibility();
            }
        }

        private bool CanSendSelectedBasicAttack(out string reason)
        {
            reason = string.Empty;
            if (_manager == null || !_selectedCombatTarget.IsValid)
            {
                reason = "No combat target is selected.";
                return false;
            }

            PlayerCombatOwnerStateMessage state = _manager.LatestCombatOwnerState;
            if (!state.success)
            {
                reason = "Combat state is not ready.";
                return false;
            }

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner.PresentationTransform == null)
            {
                reason = "Local player position is unavailable.";
                return false;
            }

            if (!TryGetSelectedCombatTargetPosition(out Vector3 targetPosition))
            {
                reason = "Combat target position is unavailable.";
                return false;
            }

            Vector3 delta = targetPosition - owner.PresentationTransform.position;
            BasicAttackMode mode = state.Mode;
            float range = CombatRangePolicy.ClientRequestRangeForMode(mode);
            if (range <= 0f)
            {
                reason = "Combat mode is not ready.";
                return false;
            }
            bool withinRange;
            if (mode == BasicAttackMode.Unarmed || mode == BasicAttackMode.MeleeWeapon)
            {
                withinRange = Mathf.Abs(delta.y) <= CombatRangePolicy.MeleeVerticalTolerance &&
                              delta.x * delta.x + delta.z * delta.z <= range * range;
            }
            else
            {
                withinRange = delta.sqrMagnitude <= range * range;
            }

            if (withinRange)
                return true;

            reason = $"Target is out of attack range ({range:0.##} m).";
            return false;
        }

        private bool TryGetSelectedCombatTargetPosition(out Vector3 position)
        {
            position = default;
            if (_selectedCombatTarget.IsPlayer)
            {
                PlayerEntityNetwork selected = SelectedPlayerTarget;
                if (selected == null || selected.ObjectId != _selectedCombatTarget.objectId || selected.Generation != _selectedCombatTarget.generation)
                    return false;
                position = selected.transform.position;
                if (PlayerEntityClient.TryGetByNetwork(selected, out PlayerEntityClient client) && client.PresentationTransform != null)
                    position = client.PresentationTransform.position;
                return true;
            }

            if (_selectedCombatTarget.IsPopulation)
            {
                if (PlayerEntityClient.TryGetPopulationPresentation(
                        _selectedCombatTarget.primaryId,
                        _selectedCombatTarget.generation,
                        out PlayerEntityClient populationClient) &&
                    populationClient.PresentationTransform != null)
                {
                    position = populationClient.PresentationTransform.position;
                    return true;
                }

                if (_hasSelectedNonPlayerCombatTargetPosition)
                {
                    position = _selectedNonPlayerCombatTargetPosition;
                    return true;
                }
                return false;
            }

            if (_selectedCombatTarget.IsCombatTestTarget && _hasSelectedNonPlayerCombatTargetPosition)
            {
                position = _selectedNonPlayerCombatTargetPosition;
                return true;
            }
            return false;
        }

        private void SetSelectedTarget(PlayerEntityNetwork target)
        {
            if (target == null)
            {
                PlayerEntityTargetSelection.Clear();
                return;
            }

            if (!PlayerEntityTargetSelection.TrySelect(target))
                PlayerEntityTargetSelection.Clear();
        }

        public void SelectPlayerTarget(PlayerEntityNetwork target) => SetSelectedTarget(target);
        public void ClearPlayerTarget() => SetSelectedTarget(null);

        public void OpenCrafting(long stationStableId)
        {
            _recoveryUi?.OpenCrafting(stationStableId);
        }

        public void OpenInteractionMenu()
        {
            if (_interactionUi == null)
                return;
            if (!_interactionUi.TryOpenCurrentContext())
                PresentInteractionFeedback("INTERACTION: No valid contextual target is under the reticle.");
        }

        public bool UsesActionFocusInteractionStyle =>
            _interactionUi != null && _interactionUi.UsesActionFocusStyle;

        public bool IsInteractionFocusActive =>
            _interactionUi != null && _interactionUi.IsActionFocusActive;

        public bool ToggleInteractionFocus()
        {
            if (_interactionUi == null)
                return false;
            bool handled = _interactionUi.ToggleKeyboardFocus();
            if (!handled)
                PresentInteractionFeedback("INTERACTION: No valid contextual target is under the reticle.");
            return handled;
        }

        public void CloseInteractionFocus() => _interactionUi?.HideMenu();

        /// <summary>
        /// Canonical interaction-menu input entry point. Kept on ClientUIRoot so the
        /// UI input router does not depend on a specific interaction presentation.
        /// </summary>
        public void ToggleInteractionMenu()
        {
            ToggleInteractionFocus();
        }

        public void OpenWorldItemInteractionMenu(long itemInstanceId)
        {
            if (_interactionUi == null)
                return;
            _interactionUi.OpenWorldItem(
                itemInstanceId,
                new Vector2(Screen.width * 0.5f + 250f, Screen.height * 0.5f - 30f));
        }

        private void ClearSelectedTarget() => SetSelectedTarget(null);

        private void OnCombatOwnerStateReceived(PlayerCombatOwnerStateMessage message)
        {
            if (!message.success || _basicAttackLabel == null)
                return;

            if (message.Mode == BasicAttackMode.Firearm)
            {
                if (message.magazineCapacity <= 0)
                    _basicAttackLabel.text = "FIREARM CONFIG INVALID";
                else if (message.loadedRounds <= 0)
                    _basicAttackLabel.text = $"FIRE  0/{message.magazineCapacity}   [R] RELOAD";
                else
                    _basicAttackLabel.text = $"FIRE  {message.loadedRounds}/{message.magazineCapacity}";
                return;
            }

            _basicAttackLabel.text = message.Mode == BasicAttackMode.Unarmed
                ? $"UNARMED  Combo {message.comboStep}"
                : "ATTACK";
        }

        private static string BasicAttackFeedbackText(BasicAttackResultCode code)
        {
            switch (code)
            {
                case BasicAttackResultCode.RejectedNoAmmo:
                    return "Magazine empty — press R to reload.";
                case BasicAttackResultCode.RejectedOutOfRange:
                    return "Target is out of range.";
                case BasicAttackResultCode.RejectedInsufficientResource:
                    return "Not enough combat resource.";
                case BasicAttackResultCode.RejectedAlreadyCasting:
                    return "Cannot fire while casting.";
                case BasicAttackResultCode.RejectedDead:
                    return "Cannot attack while dead.";
                case BasicAttackResultCode.RejectedInvalidTarget:
                    return "No valid combat target.";
                case BasicAttackResultCode.RejectedDifferentWorld:
                    return "Target is no longer in this world instance.";
                case BasicAttackResultCode.RejectedRateLimited:
                    return "Fire input was rate limited.";
                case BasicAttackResultCode.RejectedInvalidInput:
                    return "Invalid fire input.";
                case BasicAttackResultCode.RejectedNoDamage:
                    return "Shot produced no authoritative damage.";
                case BasicAttackResultCode.RejectedRecovery:
                    return "Weapon is recovering.";
                default:
                    return $"Fire rejected ({code}).";
            }
        }

        private static string ReloadFeedbackText(byte resultCode)
        {
            // Mirrors CombatReloadResultCode without adding a client UI dependency on
            // Game.Server.Application.Combat. The server remains authoritative.
            switch (resultCode)
            {
                case 1:
                    return "Cannot reload right now.";
                case 2:
                    return "Equipped weapon is not a firearm.";
                case 3:
                    return "Magazine is already full.";
                case 4:
                    return "No compatible ammunition is available in inventory.";
                case 5:
                    return "Cannot mix ammunition types in a partially loaded magazine.";
                case 6:
                    return "Reload failed to commit inventory state. Try again.";
                default:
                    return $"Reload rejected ({resultCode}).";
            }
        }

        private void OnCombatDamageReceived(PlayerCombatDamageEventMessage message)
        {
            if (_manager == null)
                return;

            CombatDamageWire damage = message.damage;
            if (_manager.IsLocalPlayerReference(damage.target))
            {
                SetSystemMessage(damage.Flags.HasFlag(CombatDamagePresentationFlags.Killed)
                    ? $"Took {damage.amount} damage and was defeated. Press R to respawn."
                    : $"Took {damage.amount} damage.");
            }
            else if (_manager.IsLocalPlayerReference(damage.source))
            {
                SetSystemMessage(damage.Flags.HasFlag(CombatDamagePresentationFlags.Killed)
                    ? $"Dealt {damage.amount} damage; target defeated."
                    : $"Dealt {damage.amount} damage.");
            }
        }

        private void OnAbilityCastStateReceived(PlayerAbilityCastStateMessage message)
        {
            if (_manager == null || !_manager.IsLocalPlayerReference(message.source))
                return;

            string ability = "ability";
            float authoredCastTime = 0f;
            if (PlayerGameplaySettingsRuntime.TryGetAbility(message.abilityWireId, out GameplayAbilityReferenceWire reference))
            {
                if (!string.IsNullOrWhiteSpace(reference.definitionId))
                    ability = reference.definitionId;
                authoredCastTime = Mathf.Max(0f, reference.castTimeSeconds);
            }

            if (message.success && message.Phase == AbilityPresentationPhase.CastStarted && message.remainingMilliseconds > 0)
            {
                float remaining = message.remainingMilliseconds / 1000f;
                float duration = Mathf.Max(remaining, authoredCastTime);
                BeginPlayerCastBar(ability, duration, remaining);
            }
            else if (message.Phase == AbilityPresentationPhase.CastCompleted ||
                     message.Phase == AbilityPresentationPhase.CastCancelled ||
                     !message.success)
            {
                HidePlayerCastBar();
            }

            SetSystemMessage(message.success
                ? $"{ability}: {message.Phase}."
                : $"{ability}: {message.Failure}.");
        }

        private void ResolvePlayerCastBarPresentation()
        {
            _playerCastBarRoot = null;
            _playerCastProgress = null;
            _playerCastLabel = null;
            if (_windowManager == null || !_windowManager.TryGet(ClientUIPanelId.HudPlayerCastBar, out GameObject panel) || panel == null)
                return;

            _playerCastBarRoot = panel;
            _playerCastProgress = panel.transform.Find("Progress") as RectTransform;
            _playerCastLabel = panel.transform.Find("Label")?.GetComponent<Text>();
        }

        private void BeginPlayerCastBar(string ability, float duration, float remaining)
        {
            if (_playerCastBarRoot == null || _playerCastProgress == null || _playerCastLabel == null)
                ResolvePlayerCastBarPresentation();
            if (_playerCastBarRoot == null || _playerCastProgress == null || _playerCastLabel == null)
                return;

            _playerCastAbilityLabel = string.IsNullOrWhiteSpace(ability) ? "Ability" : ability;
            _playerCastDuration = Mathf.Max(0.001f, duration);
            float elapsedAtReceipt = Mathf.Clamp(_playerCastDuration - Mathf.Max(0f, remaining), 0f, _playerCastDuration);
            _playerCastStartedAt = Time.unscaledTime - elapsedAtReceipt;
            _playerCastActive = true;
            _playerCastBarRoot.SetActive(true);
            UpdatePlayerCastBarPresentation();
        }

        private void UpdatePlayerCastBarPresentation()
        {
            if (!_playerCastActive || _playerCastProgress == null || _playerCastLabel == null)
                return;

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _playerCastStartedAt);
            float progress = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, _playerCastDuration));
            Vector2 max = _playerCastProgress.anchorMax;
            max.x = progress;
            _playerCastProgress.anchorMax = max;
            float remaining = Mathf.Max(0f, _playerCastDuration - elapsed);
            _playerCastLabel.text = $"{_playerCastAbilityLabel}  {remaining:0.0}s";
            // Do not hide at the predicted end. The authoritative completion/cancellation
            // event owns lifetime; the bar simply remains full if that event is delayed.
        }

        private void HidePlayerCastBar()
        {
            _playerCastActive = false;
            _playerCastDuration = 0f;
            _playerCastAbilityLabel = string.Empty;
            if (_playerCastBarRoot == null)
                ResolvePlayerCastBarPresentation();
            if (_playerCastBarRoot != null)
                _playerCastBarRoot.SetActive(false);
        }

        public void RequestNearestWorldItemPickup()
        {
            RequestNearestWorldItemPickupAsync().Forget();
        }

        private async UniTaskVoid RequestNearestWorldItemPickupAsync()
        {
            if (!_gameplaySessionActive || _manager == null)
                return;
            WorldItemsSnapshotMessage snapshot = _manager.LatestWorldItems;
            WorldItemWire[] items = snapshot.items ?? Array.Empty<WorldItemWire>();
            if (!snapshot.success || items.Length == 0)
            {
                SetSystemMessage("No world item is available to loot.");
                return;
            }

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient ownerClient) || ownerClient.PresentationTransform == null)
            {
                SetSystemMessage("Local player presentation is not ready for loot selection.");
                return;
            }

            Vector3 position = ownerClient.PresentationTransform.position;
            int nearest = -1;
            float nearestSq = float.MaxValue;
            for (int i = 0; i < items.Length; ++i)
            {
                WorldItemWire item = items[i];
                float dx = item.positionX - position.x, dy = item.positionY - position.y, dz = item.positionZ - position.z;
                float sq = dx * dx + dy * dy + dz * dz;
                if (sq < nearestSq) { nearestSq = sq; nearest = i; }
            }
            if (nearest < 0) return;

            WorldItemInteractionResponseMessage response = await _manager.RequestWorldItemLootAsync(items[nearest].itemInstanceId);
            await UniTask.SwitchToMainThread();
            SetSystemMessage(response.success ? response.detail : $"Loot rejected: {response.detail}");
        }

        private void OnWorldItemsChangedReceived(WorldItemsSnapshotMessage snapshot)
        {
            // World-item availability is already visible in-world and cached locally.
            // Do not maintain a duplicate HUD status strip for it.
        }

        private void OnWorldItemInteractionResultReceived(WorldItemInteractionResponseMessage message)
        {
            PresentSystemMessage(message.success
                ? $"Loot: {message.detail}"
                : $"Loot rejected: {(InteractionResultCode)message.resultCode}: {message.detail}");
        }

        private void OnInteractionResultReceived(ContextInteractionResponseMessage message)
        {
            PresentSystemMessage(message.success
                ? $"{message.ActionId}: succeeded"
                : $"{message.ActionId}: {message.ResultCode}: {message.detail}");
        }

        private void OnChatMessage(ClientChatMessage message) => _chatState.Add(message);

        private void OnChatChannelClicked()
        {
            switch (_selectedChatChannel)
            {
                case ChatChannel.Local: _selectedChatChannel = ChatChannel.Whisper; break;
                case ChatChannel.Whisper: _selectedChatChannel = ChatChannel.Party; break;
                case ChatChannel.Party: _selectedChatChannel = ChatChannel.Guild; break;
                default: _selectedChatChannel = ChatChannel.Local; break;
            }
            RefreshChatChannelPresentation();
        }

        private void OnChatEndEdit(string value)
        {
            if (!UnityEngine.Input.GetKeyDown(KeyCode.Return) &&
                !UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
                return;
            SendChatInput();
        }

        private void SendChatInput()
        {
            if (_chatInput == null)
                return;

            string raw = (_chatInput.text ?? string.Empty).Trim();
            if (raw.Length == 0)
                return;

            if (string.Equals(raw, "/clear", StringComparison.OrdinalIgnoreCase))
            {
                _chatState.Clear();
                _chatInput.text = string.Empty;
                _chatInput.ActivateInputField();
                return;
            }
            if (string.Equals(raw, "/help", StringComparison.OrdinalIgnoreCase))
            {
                _chatState.AddSystem("Chat: Local by default. /w \"First Last\" message, /p message, /g message, /s message, /clear.");
                _chatInput.text = string.Empty;
                _chatInput.ActivateInputField();
                return;
            }

            ChatChannel channel = _selectedChatChannel;
            string target = _whisperTargetInput != null ? (_whisperTargetInput.text ?? string.Empty).Trim() : string.Empty;
            string message = raw;
            ParseSlashCommand(raw, ref channel, ref target, ref message);

            if (_manager == null)
                BindManager();
            string error = string.Empty;
            if (_chatTransport == null || !_chatTransport.TrySend(channel, target, message, out error))
            {
                _chatState.AddSystem(string.IsNullOrWhiteSpace(error) ? "Unable to send chat message." : error);
                return;
            }

            _chatInput.text = string.Empty;
            _chatInput.ActivateInputField();
        }

        private static void ParseSlashCommand(
            string raw,
            ref ChatChannel channel,
            ref string target,
            ref string message)
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
            if (raw.StartsWith("/s ", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("/l ", StringComparison.OrdinalIgnoreCase))
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
                target = rest;
                message = string.Empty;
            }
        }

        private void RenderChatHistory()
        {
            if (_chatHistoryText == null)
                return;

            _chatHistoryText.supportRichText = true;
            var builder = new StringBuilder(2048);
            foreach (ClientChatState.Entry entry in _chatState.Entries)
            {
                if (!ChatEntryVisible(entry.Channel))
                    continue;

                string sender = EscapeRichText(entry.Sender);
                string target = EscapeRichText(entry.Target);
                string message = EscapeRichText(entry.Message);
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

            _chatHistoryText.text = builder.ToString();
            Canvas.ForceUpdateCanvases();
            if (_chatScroll != null)
                _chatScroll.verticalNormalizedPosition = 0f;
        }

        private bool ChatEntryVisible(ChatChannel channel)
        {
            switch (_chatViewFilter)
            {
                case ChatViewFilter.Local:
                    return channel == ChatChannel.Local;
                case ChatViewFilter.Social:
                    return channel == ChatChannel.Whisper || channel == ChatChannel.Party || channel == ChatChannel.Guild;
                case ChatViewFilter.System:
                    return channel == ChatChannel.System;
                default:
                    return true;
            }
        }

        private static string EscapeRichText(string value) =>
            (value ?? string.Empty).Replace("<", "&lt;").Replace(">", "&gt;");

        private void SetChatViewFilter(ChatViewFilter filter)
        {
            _chatViewFilter = filter;
            PlayerPrefs.SetInt(ChatViewFilterPreferenceKey, (int)filter);
            PlayerPrefs.Save();
            RefreshChatTabPresentation();
            RenderChatHistory();
        }

        private void RefreshChatTabPresentation()
        {
            SetChatTabLabel(_chatAllTabButton, "ALL", _chatViewFilter == ChatViewFilter.All);
            SetChatTabLabel(_chatLocalTabButton, "LOCAL", _chatViewFilter == ChatViewFilter.Local);
            SetChatTabLabel(_chatSocialTabButton, "SOCIAL", _chatViewFilter == ChatViewFilter.Social);
            SetChatTabLabel(_chatSystemTabButton, "SYSTEM", _chatViewFilter == ChatViewFilter.System);
        }

        private static void SetChatTabLabel(Button button, string label, bool selected)
        {
            if (button == null)
                return;
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
                text.text = selected ? $"[{label}]" : label;
        }

        private void RefreshChatChannelPresentation()
        {
            if (_chatChannelLabel != null)
                _chatChannelLabel.text = _selectedChatChannel.ToString().ToUpperInvariant();
            if (_whisperTargetInput != null)
                _whisperTargetInput.gameObject.SetActive(_selectedChatChannel == ChatChannel.Whisper);
        }

        private void OnRetryConnectionClicked()
        {
            if (_manager == null)
                BindManager();
            if (_manager == null)
                return;

            if (_manager.IsNetworkActive && !_manager.IsClientConnected && _manager.IsClient && !_manager.IsServer)
                _manager.StopClient();

            SetSystemMessage($"Reconnecting to {_manager.networkAddress}:{_manager.networkPort}...");
            if (!_manager.StartClient(_manager.networkAddress, _manager.networkPort))
            {
                if (_disconnectReasonText != null)
                    _disconnectReasonText.text = "Unable to restart the client connection. You can retry again.";
                return;
            }

            if (_disconnectReasonText != null)
                _disconnectReasonText.text = "Reconnecting...";
        }

        private void OnReturnToLoginClicked()
        {
            if (_manager != null && _manager.IsClient)
                _manager.StopClient();

            SetDisconnectVisible(false);
            SetServiceNoticeVisible(false);
            SetGameplayVisible(false);

            CharacterSelectShell frontend = _frontendShell != null ? _frontendShell : FindFirstObjectByType<CharacterSelectShell>();
            if (frontend != null)
                frontend.ReturnToFrontend("Disconnected. Ready to reconnect.");
            else
                SetSystemMessage("Character Select frontend was not found in the scene.");
        }

        private static void OnQuitClicked() => Application.Quit();

        private void OnInventoryClicked()
        {
            if (!_gameplaySessionActive)
                return;

            if (_inventoryShell == null)
                _inventoryShell = GetComponent<PlayerInventoryShell>();
            _inventoryShell?.Toggle();
        }

        private void OnCharacterClicked() { }
        private void OnSkillsClicked()
        {
            RenderSkills(_manager != null ? _manager.LatestProgression : default);
            _windowManager?.Toggle(ClientUIPanelId.SkillsWindow);
        }
        private void OnMapClicked() => _windowManager?.Toggle(ClientUIPanelId.WorldMapWindow);
        private void OnMenuClicked() => _windowManager?.Toggle(ClientUIPanelId.MainMenuWindow);
        private void OnMenuResumeClicked() => _windowManager?.Close(ClientUIPanelId.MainMenuWindow);

        private void OnMenuSettingsClicked()
        {
            _windowManager?.Close(ClientUIPanelId.MainMenuWindow);
            _windowManager?.Open(ClientUIPanelId.SettingsWindow);
        }

        private void OnMenuLogoutClicked()
        {
            _windowManager?.Close(ClientUIPanelId.MainMenuWindow);
            OnReturnToLoginClicked();
        }

        private void OnMenuQuitClicked()
        {
            // Reuse the normal client disconnect path before process exit so the server
            // receives the same authoritative disconnect/save opportunity as other exits.
            if (_manager != null && _manager.IsClient)
                _manager.StopClient();
            Application.Quit();
        }

        private void SetTargetFrameVisible(bool visible)
        {
            if (_targetText != null && _targetText.transform.parent != null)
                _targetText.transform.parent.gameObject.SetActive(visible && !_targetOverlaySuppressed);
        }

        private void SetStatusEffectsVisible(bool visible)
        {
            if (_statusEffectsText != null && _statusEffectsText.transform.parent != null)
                _statusEffectsText.transform.parent.gameObject.SetActive(visible);
        }

        private void OnProgressionSnapshot(ProgressionSnapshotMessage snapshot)
        {
            if (_playerLevelText != null)
                _playerLevelText.text = string.Empty;
            RenderSkills(snapshot);
        }

        private void RenderSkills(ProgressionSnapshotMessage snapshot)
        {
            if (_skillsSummaryText == null)
                return;

            if (!snapshot.success)
            {
                _skillsSummaryText.text = "Skills are not available yet.";
                return;
            }

            ProgressTrackDefinition[] definitions = ClientRecoveryContentCache.GetProgressTracks();
            ProgressTrackWire[] values = snapshot.tracks ?? Array.Empty<ProgressTrackWire>();
            var byId = new Dictionary<ushort, int>(values.Length);
            for (int i = 0; i < values.Length; ++i)
                byId[values[i].dataId] = values[i].value;

            var builder = new StringBuilder(512);
            builder.Append("Level ").Append(Math.Max(1, snapshot.level))
                .Append("    XP ").Append(snapshot.experience.ToString("N0"));

            ProgressTrackKind? currentKind = null;
            for (int i = 0; i < definitions.Length; ++i)
            {
                ProgressTrackDefinition definition = definitions[i];
                if (definition == null)
                    continue;

                if (!currentKind.HasValue || currentKind.Value != definition.kind)
                {
                    currentKind = definition.kind;
                    builder.Append("\n\n")
                        .Append(TrackKindLabel(definition.kind).ToUpperInvariant())
                        .Append('\n');
                }

                int value = byId.TryGetValue(definition.dataId, out int trackedValue) ? trackedValue : 0;
                int maximum = Math.Max(1, definition.maximumValue);
                builder.Append("• ")
                    .Append(string.IsNullOrWhiteSpace(definition.displayName) ? definition.definitionId : definition.displayName)
                    .Append("    ").Append(value).Append(" / ").Append(maximum).Append('\n');
            }

            if (definitions.Length == 0)
                builder.Append("\n\nNo client-known skill definitions are installed.");

            _skillsSummaryText.text = builder.ToString().TrimEnd();
        }

        private static string TrackKindLabel(ProgressTrackKind kind)
        {
            switch (kind)
            {
                case ProgressTrackKind.Mastery: return "Masteries";
                case ProgressTrackKind.Profession: return "Professions";
                case ProgressTrackKind.Hunter: return "Hunter Skills";
                case ProgressTrackKind.Vampire: return "Vampire Skills";
                default: return "General Skills";
            }
        }

        private void RefreshPlayerIdentityPresentation()
        {
            if (_playerNameText != null)
            {
                string displayName = string.Empty;
                if (_manager != null && _manager.HasCharacterListCache)
                {
                    CharacterSessionCharacterSummary[] characters = _manager.LatestCharacterList.characters ?? Array.Empty<CharacterSessionCharacterSummary>();
                    for (int i = 0; i < characters.Length; ++i)
                    {
                        if (characters[i].characterId != _localCharacterId)
                            continue;
                        displayName = characters[i].name ?? string.Empty;
                        break;
                    }
                }
                _playerNameText.text = displayName;
            }

            if (_playerLevelText != null)
                _playerLevelText.text = string.Empty;
        }

        private void ClearPlayerIdentityPresentation()
        {
            if (_playerNameText != null) _playerNameText.text = string.Empty;
            if (_playerLevelText != null) _playerLevelText.text = string.Empty;
        }

        private void SetGameplayVisible(bool visible)
        {
            // Gameplay UI V2 owns the post-login presentation. Keep the old authored HUD/chat
            // dark when V2 is installed, but leave the frontend and existing gameplay services
            // on this root intact for compatibility during the migration.
            if (_gameplayUiV2 == null)
                _gameplayUiV2 = GetComponent<ClientGameplayUIRoot>();

            if (_gameplayUiV2 != null)
            {
                if (_hudRoot != null) _hudRoot.SetActive(false);
                if (_chatRoot != null) _chatRoot.SetActive(false);
                _gameplayUiV2.SetGameplayVisible(visible);
            }
            else
            {
                if (_hudRoot != null) _hudRoot.SetActive(visible);
                if (_chatRoot != null) _chatRoot.SetActive(visible);
            }

            // Gameplay windows must never remain open when the master UI returns
            // to Connect/Login/Character Select. The frontend and gameplay HUD
            // intentionally share one prefab, but they use separate input contexts.
            if (!visible)
            {
                if (_inventoryShell == null)
                    _inventoryShell = GetComponent<PlayerInventoryShell>();
                _inventoryShell?.Close();
                _interactionUi?.HideAllInteractionSurfaces();
                _windowManager?.CloseAllGameplayWindows();
                _popupController?.Hide();
            }
        }

        private void SetDisconnectVisible(bool visible)
        {
            if (_disconnectOverlay != null)
            {
                _disconnectOverlay.SetActive(visible);
                if (visible)
                    _disconnectOverlay.transform.SetAsLastSibling();
            }
        }

        private void SetServiceNoticeVisible(bool visible)
        {
            // Retired gameplay overlay. Keep the serialized reference only for backwards
            // prefab compatibility, but it is never allowed to become visible.
            if (_serviceNoticeRoot != null && _serviceNoticeRoot.activeSelf)
                _serviceNoticeRoot.SetActive(false);
        }

        private void SetSystemMessage(string value) => AddLocalSystemChat(value);

        public void PresentInteractionFeedback(string value) => AddLocalSystemChat(value);

        /// <summary>
        /// Presents durable local feedback through the existing System chat stream. This is
        /// presentation of information the client already knows; it creates no network traffic.
        /// </summary>
        public void PresentSystemMessage(string value) => AddLocalSystemChat(value);

        private void AddLocalSystemChat(string value)
        {
            string message = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (string.Equals(message, _lastLocalSystemMessage, StringComparison.Ordinal) &&
                now - _lastLocalSystemMessageAt < 0.75d)
                return;

            _lastLocalSystemMessage = message;
            _lastLocalSystemMessageAt = now;
            _chatState.AddSystem(message);
            if (_gameplayUiV2 == null)
                _gameplayUiV2 = GetComponent<ClientGameplayUIRoot>();
            _gameplayUiV2?.PresentLocalSystemMessage(message);
        }

        public void SetInteractionMenuOverlayActive(bool active)
        {
            _targetOverlaySuppressed = active;
            RefreshTargetOverlayVisibility();
            if (_gameplayUiV2 == null)
                _gameplayUiV2 = GetComponent<ClientGameplayUIRoot>();
            _gameplayUiV2?.SetInteractionOverlayActive(active);
        }

        private void RefreshTargetOverlayVisibility()
        {
            bool hasTarget = _selectedCombatTarget.IsValid || _selectedTarget != null;
            SetTargetFrameVisible(!_targetOverlaySuppressed && hasTarget);
        }

        /// <summary>
        /// Reuses the authored chat input for contextual social actions such as Whisper and
        /// Report Player. The client only prepares text the player can inspect/edit; normal
        /// chat parsing and server authority remain unchanged.
        /// </summary>
        public void PrefillChatCommand(string command)
        {
            if (_gameplayUiV2 == null)
                _gameplayUiV2 = GetComponent<ClientGameplayUIRoot>();
            if (_gameplayUiV2 != null)
            {
                _gameplayUiV2.PrefillChatCommand(command);
                return;
            }

            if (_chatInput == null)
                return;
            _chatInput.text = command ?? string.Empty;
            _chatInput.gameObject.SetActive(true);
            _chatInput.Select();
            _chatInput.ActivateInputField();
            _chatInput.MoveTextEnd(false);
        }

#if UNITY_EDITOR
        /// <summary>
        /// One-time authoring/migration hook. It creates only missing default presentation
        /// objects, then captures direct serialized references. Runtime never reconstructs
        /// the visual hierarchy and never depends on hierarchy paths.
        /// </summary>
        public void BuildPresentationForEditor()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            if (_hudRoot == null)
            {
                Transform existingHud = FindTopLevelPanelGroupEditor(ClientUIPanelId.HudPlayerVitals);
                _hudRoot = existingHud != null ? existingHud.gameObject : BuildHud(font);
            }

            if (_chatRoot == null)
            {
                Transform existingChat = FindTopLevelPanelGroupEditor(ClientUIPanelId.ChatWindow);
                _chatRoot = existingChat != null ? existingChat.gameObject : BuildChat(font);
            }

            if (_serviceNoticeRoot == null)
            {
                Transform existingServiceNotice = transform.Find("ServiceNotice");
                _serviceNoticeRoot = existingServiceNotice != null
                    ? existingServiceNotice.gameObject
                    : BuildServiceNotice(font);
            }

            if (_disconnectOverlay == null)
            {
                Transform existingDisconnect = transform.Find("DisconnectOverlay");
                _disconnectOverlay = existingDisconnect != null
                    ? existingDisconnect.gameObject
                    : BuildDisconnect(font);
            }

            _interactionUi = GetComponent<ClientInteractionUI>();
            if (_interactionUi == null)
                _interactionUi = gameObject.AddComponent<ClientInteractionUI>();
            _interactionUi.BuildPresentationForEditor();

            _frontendShell = GetComponentInChildren<CharacterSelectShell>(true);
            _windowManager = GetComponent<ClientWindowManager>();
            _popupController = GetComponentInChildren<ClientPopupController>(true);
            CaptureAuthoredReferencesFromHierarchy();
            WireAuthoredPresentation();
        }

        /// <summary>
        /// Editor-only path lookup used exclusively to migrate the old generated layout
        /// into serialized prefab references. Once saved, artists may freely rename or
        /// re-parent visual objects without breaking runtime binding.
        /// </summary>
        public void CaptureAuthoredReferencesFromHierarchy()
        {
            if (_frontendShell == null) _frontendShell = GetComponentInChildren<CharacterSelectShell>(true);
            if (_hudRoot == null) _hudRoot = FindTopLevelPanelGroupEditor(ClientUIPanelId.HudPlayerVitals)?.gameObject;
            if (_chatRoot == null) _chatRoot = FindTopLevelPanelGroupEditor(ClientUIPanelId.ChatWindow)?.gameObject;
            if (_serviceNoticeRoot == null) _serviceNoticeRoot = FindPanelEditor(ClientUIPanelId.SystemServiceNotice)?.gameObject;
            if (_disconnectOverlay == null) _disconnectOverlay = FindPanelEditor(ClientUIPanelId.SystemDisconnectOverlay)?.gameObject;

            CaptureResourceBarIfMissing("HUD/Resources/HealthBar", ref _healthSlider, ref _healthLabel);
            CaptureResourceBarIfMissing("HUD/Resources/ManaBar", ref _manaSlider, ref _manaLabel);
            CaptureResourceBarIfMissing("HUD/Resources/StaminaBar", ref _staminaSlider, ref _staminaLabel);

            if (_targetText == null) _targetText = FindTextEditor("HUD/TargetFrame/TargetText");
            if (_statusEffectsText == null) _statusEffectsText = FindTextEditor("HUD/StatusPanel/StatusEffectsText");
            if (_interactionText == null) _interactionText = FindTextEditor("HUD/InteractionPrompt/InteractionText");
            if (_basicAttackLabel == null) _basicAttackLabel = FindTextEditor("HUD/ActionBar/ActionSlot1/Label");
            if (_systemText == null) _systemText = FindTextEditor("HUD/SystemMessage/SystemText");
            if (_inventoryButton == null) _inventoryButton = FindButtonEditor("HUD/InventoryButton");

            if (_chatHistoryText == null) _chatHistoryText = FindTextEditor("Chat/Viewport/Content/History");
            if (_chatScroll == null) _chatScroll = _chatRoot != null ? _chatRoot.GetComponent<ScrollRect>() : null;
            if (_chatChannelButton == null) _chatChannelButton = FindButtonEditor("Chat/ChannelButton");
            if (_chatChannelLabel == null && _chatChannelButton != null) _chatChannelLabel = _chatChannelButton.GetComponentInChildren<Text>(true);
            if (_whisperTargetInput == null) _whisperTargetInput = FindInputEditor("Chat/WhisperTarget");
            if (_chatInput == null) _chatInput = FindInputEditor("Chat/ChatInput");
            if (_chatSendButton == null) _chatSendButton = FindButtonEditor("Chat/SendButton");

            if (_serviceNoticeText == null && _serviceNoticeRoot != null)
                _serviceNoticeText = _serviceNoticeRoot.transform.Find("NoticeText")?.GetComponent<Text>();
            if (_disconnectOverlay != null)
            {
                Transform disconnectPanel = _disconnectOverlay.transform.Find("Panel");
                if (_disconnectReasonText == null) _disconnectReasonText = disconnectPanel?.Find("Reason")?.GetComponent<Text>();
                if (_retryButton == null) _retryButton = disconnectPanel?.Find("RetryButton")?.GetComponent<Button>();
                if (_returnLoginButton == null) _returnLoginButton = disconnectPanel?.Find("ReturnLoginButton")?.GetComponent<Button>();
                if (_quitButton == null) _quitButton = disconnectPanel?.Find("QuitButton")?.GetComponent<Button>();
            }
        }

        private void CaptureResourceBarIfMissing(string path, ref Slider slider, ref Text label)
        {
            if (slider != null && label != null)
                return;
            Transform bar = transform.Find(path);
            if (bar == null)
                return;
            if (slider == null) slider = bar.GetComponent<Slider>();
            if (label == null) label = bar.Find("Label")?.GetComponent<Text>();
        }

        private Text FindTextEditor(string path) => transform.Find(path)?.GetComponent<Text>();
        private Button FindButtonEditor(string path) => transform.Find(path)?.GetComponent<Button>();
        private InputField FindInputEditor(string path) => transform.Find(path)?.GetComponent<InputField>();

        private Transform FindPanelEditor(ClientUIPanelId panelId)
        {
            ClientUIPanelMarker[] markers = GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelMarker marker in markers)
            {
                if (marker != null && marker.PanelId == panelId)
                    return marker.transform;
            }
            return null;
        }

        private Transform FindTopLevelPanelGroupEditor(ClientUIPanelId panelId)
        {
            Transform panel = FindPanelEditor(panelId);
            if (panel == null)
                return null;

            Transform current = panel;
            while (current.parent != null && current.parent != transform)
                current = current.parent;
            return current.parent == transform ? current : null;
        }

        private static Transform FindDescendantEditor(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found = FindDescendantEditor(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
#endif

        private void WireAuthoredPresentation()
        {
            _resourceBars.Clear();
            AddAuthoredResourceBar(CharacterResourceId.Health, _healthSlider, _healthLabel);
            AddAuthoredResourceBar(CharacterResourceId.Mana, _manaSlider, _manaLabel);
            AddAuthoredResourceBar(CharacterResourceId.Stamina, _staminaSlider, _staminaLabel);
            ResolvePlayerCastBarPresentation();
            HidePlayerCastBar();

            if (_basicAttackLabel != null)
                _basicAttackLabel.text = "1\nATTACK";
            if (_characterButton != null)
                _characterButton.gameObject.SetActive(false);
            if (_inventoryButton != null)
            {
                _inventoryButton.onClick.RemoveListener(OnInventoryClicked);
                _inventoryButton.onClick.AddListener(OnInventoryClicked);
            }
            if (_skillsButton != null)
            {
                _skillsButton.onClick.RemoveListener(OnSkillsClicked);
                _skillsButton.onClick.AddListener(OnSkillsClicked);
            }
            if (_mapButton != null)
            {
                _mapButton.onClick.RemoveListener(OnMapClicked);
                _mapButton.onClick.AddListener(OnMapClicked);
            }
            if (_menuButton != null)
            {
                _menuButton.onClick.RemoveListener(OnMenuClicked);
                _menuButton.onClick.AddListener(OnMenuClicked);
            }
            if (_menuResumeButton != null)
            {
                _menuResumeButton.onClick.RemoveListener(OnMenuResumeClicked);
                _menuResumeButton.onClick.AddListener(OnMenuResumeClicked);
            }
            if (_menuSettingsButton != null)
            {
                _menuSettingsButton.onClick.RemoveListener(OnMenuSettingsClicked);
                _menuSettingsButton.onClick.AddListener(OnMenuSettingsClicked);
            }
            if (_menuLogoutButton != null)
            {
                _menuLogoutButton.onClick.RemoveListener(OnMenuLogoutClicked);
                _menuLogoutButton.onClick.AddListener(OnMenuLogoutClicked);
            }
            if (_menuQuitButton != null)
            {
                _menuQuitButton.onClick.RemoveListener(OnMenuQuitClicked);
                _menuQuitButton.onClick.AddListener(OnMenuQuitClicked);
            }

            if (_chatChannelButton != null)
            {
                _chatChannelButton.onClick.RemoveListener(OnChatChannelClicked);
                _chatChannelButton.onClick.AddListener(OnChatChannelClicked);
            }
            if (_chatInput != null)
            {
                _chatInput.onEndEdit.RemoveListener(OnChatEndEdit);
                _chatInput.onEndEdit.AddListener(OnChatEndEdit);
            }
            if (_chatSendButton != null)
            {
                _chatSendButton.onClick.RemoveListener(SendChatInput);
                _chatSendButton.onClick.AddListener(SendChatInput);
            }
            WireChatTab(_chatAllTabButton, ChatViewFilter.All);
            WireChatTab(_chatLocalTabButton, ChatViewFilter.Local);
            WireChatTab(_chatSocialTabButton, ChatViewFilter.Social);
            WireChatTab(_chatSystemTabButton, ChatViewFilter.System);

            if (_retryButton != null)
            {
                _retryButton.onClick.RemoveListener(OnRetryConnectionClicked);
                _retryButton.onClick.AddListener(OnRetryConnectionClicked);
            }
            if (_returnLoginButton != null)
            {
                _returnLoginButton.onClick.RemoveListener(OnReturnToLoginClicked);
                _returnLoginButton.onClick.AddListener(OnReturnToLoginClicked);
            }
            if (_quitButton != null)
            {
                _quitButton.onClick.RemoveListener(OnQuitClicked);
                _quitButton.onClick.AddListener(OnQuitClicked);
            }

            RefreshChatChannelPresentation();
            RefreshChatTabPresentation();
            ResetResourcePresentationUnknown();
        }

        private void ResetResourcePresentationUnknown()
        {
            foreach (KeyValuePair<CharacterResourceId, ResourceBarView> pair in _resourceBars)
            {
                ResourceBarView view = pair.Value;
                if (view.Slider != null)
                {
                    view.Slider.minValue = 0f;
                    view.Slider.maxValue = 1f;
                    view.Slider.SetValueWithoutNotify(0f);
                }

                if (view.Label != null)
                    view.Label.text = $"{GetResourceDisplayName(pair.Key)}  —";
            }
        }

        private void WireChatTab(Button button, ChatViewFilter filter)
        {
            if (button == null)
                return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => SetChatViewFilter(filter));
        }

        private void AddAuthoredResourceBar(CharacterResourceId id, Slider slider, Text label)
        {
            if (slider != null && label != null)
                _resourceBars[id] = new ResourceBarView { Id = id, Slider = slider, Label = label };
        }

        private void ValidateAuthoredPresentation()
        {
            var missing = new List<string>(16);
            if (_hudRoot == null) missing.Add("HUD root");
            if (_chatRoot == null) missing.Add("Chat root");
            if (_healthSlider == null || _healthLabel == null) missing.Add("Health resource bar");
            if (_manaSlider == null || _manaLabel == null) missing.Add("Mana resource bar");
            if (_staminaSlider == null || _staminaLabel == null) missing.Add("Stamina resource bar");
            if (_targetText == null) missing.Add("Target text");
            if (_statusEffectsText == null) missing.Add("Status effects text");
            if (_skillsSummaryText == null) missing.Add("Skills summary text");
            if (_chatInput == null || _chatHistoryText == null) missing.Add("Chat controls");
            if (_disconnectOverlay == null) missing.Add("Disconnect overlay");
            if (_serviceNoticeRoot == null) missing.Add("Service notice");

            if (missing.Count > 0)
            {
                Debug.LogError(
                    "[ClientUI] The master ClientUIRoot prefab is missing authored references: " +
                    string.Join(", ", missing) +
                    ". Repair the authored references on ClientUIRoot.prefab directly. The old one-time migration menu has been retired.",
                    this);
            }
        }

#if UNITY_EDITOR
        private GameObject BuildHud(Font font)
        {
            GameObject root = NewUi("HUD", transform);
            Stretch(root.GetComponent<RectTransform>());

            GameObject resources = NewImage("Resources", root.transform, new Color(0.04f, 0.05f, 0.07f, 0.86f));
            Rect(resources.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(410, 150), new Vector2(225, -90));
            AddResourceBar(resources.transform, font, CharacterResourceId.Health, 0);
            AddResourceBar(resources.transform, font, CharacterResourceId.Mana, 1);
            AddResourceBar(resources.transform, font, CharacterResourceId.Stamina, 2);

            GameObject target = NewImage("TargetFrame", root.transform, new Color(0.04f, 0.05f, 0.07f, 0.82f));
            Rect(target.GetComponent<RectTransform>(), new Vector2(0.5f, 1), new Vector2(420, 74), new Vector2(0, -55));
            _targetText = NewText("TargetText", target.transform, font, "TARGET: None", 17, TextAnchor.MiddleCenter);
            Stretch(_targetText.rectTransform);

            GameObject statuses = NewImage("StatusPanel", root.transform, new Color(0.04f, 0.05f, 0.07f, 0.82f));
            Rect(statuses.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(360, 220), new Vector2(-200, -125));
            _statusEffectsText = NewText("StatusEffectsText", statuses.transform, font, "STATUS EFFECTS\nNone", 15, TextAnchor.UpperLeft);
            Stretch(_statusEffectsText.rectTransform);
            _statusEffectsText.rectTransform.offsetMin = new Vector2(12, 10);
            _statusEffectsText.rectTransform.offsetMax = new Vector2(-12, -10);

            GameObject actionBar = NewImage("ActionBar", root.transform, new Color(0.04f, 0.05f, 0.07f, 0.88f));
            Rect(actionBar.GetComponent<RectTransform>(), new Vector2(0.5f, 0), new Vector2(740, 88), new Vector2(0, 58));
            for (int i = 0; i < 8; ++i)
            {
                GameObject slot = NewImage($"ActionSlot{i + 1}", actionBar.transform, new Color(0.12f, 0.14f, 0.19f, 1f));
                Rect(slot.GetComponent<RectTransform>(), new Vector2(0, 0.5f), new Vector2(76, 64), new Vector2(52 + i * 88, 0));
                Text label = NewText("Label", slot.transform, font, $"{i + 1}\n—", 14, TextAnchor.MiddleCenter);
                Stretch(label.rectTransform);
            }

            GameObject interaction = NewImage("InteractionPrompt", root.transform, new Color(0.04f, 0.05f, 0.07f, 0.78f));
            Rect(interaction.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(420, 44), new Vector2(0, -180));
            _interactionText = NewText("InteractionText", interaction.transform, font, "INTERACTION: Ready", 15, TextAnchor.MiddleCenter);
            Stretch(_interactionText.rectTransform);

            GameObject system = NewImage("SystemMessage", root.transform, new Color(0.02f, 0.02f, 0.03f, 0.58f));
            Rect(system.GetComponent<RectTransform>(), new Vector2(0.5f, 0), new Vector2(760, 34), new Vector2(0, 116));
            _systemText = NewText("SystemText", system.transform, font, string.Empty, 14, TextAnchor.MiddleCenter);
            Stretch(_systemText.rectTransform);

            _inventoryButton = NewButton("InventoryButton", root.transform, font, "INVENTORY [I]", new Vector2(1, 0), new Vector2(-125, 48), new Vector2(220, 44));
            _inventoryButton.onClick.AddListener(OnInventoryClicked);

            return root;
        }

        private void AddResourceBar(Transform parent, Font font, CharacterResourceId id, int row)
        {
            GameObject barRoot = NewUi(id + "Bar", parent);
            Rect(barRoot.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(380, 38), new Vector2(205, -28 - row * 43));

            GameObject background = NewImage("Background", barRoot.transform, new Color(0.12f, 0.13f, 0.17f, 1f));
            Stretch(background.GetComponent<RectTransform>());

            GameObject fillArea = NewUi("FillArea", barRoot.transform);
            Stretch(fillArea.GetComponent<RectTransform>());
            fillArea.GetComponent<RectTransform>().offsetMin = new Vector2(3, 3);
            fillArea.GetComponent<RectTransform>().offsetMax = new Vector2(-3, -3);

            GameObject fill = NewImage("Fill", fillArea.transform, ResourceColor(id));
            Stretch(fill.GetComponent<RectTransform>());

            Slider slider = barRoot.AddComponent<Slider>();
            slider.transition = Selectable.Transition.None;
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.targetGraphic = background.GetComponent<Image>();
            slider.minValue = 0;
            slider.maxValue = 100;
            slider.value = 100;
            slider.interactable = false;

            Text label = NewText("Label", barRoot.transform, font, $"{GetResourceDisplayName(id)}  —", 15, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.raycastTarget = false;

            _resourceBars[id] = new ResourceBarView { Id = id, Slider = slider, Label = label };
        }

        private GameObject BuildChat(Font font)
        {
            GameObject root = NewImage("Chat", transform, new Color(0.025f, 0.03f, 0.045f, 0.90f));
            Rect(root.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(620, 320), new Vector2(330, 185));

            GameObject viewport = NewUi("Viewport", root.transform);
            Rect(viewport.GetComponent<RectTransform>(), new Vector2(0.5f, 1), new Vector2(590, 230), new Vector2(0, -125));
            viewport.AddComponent<RectMask2D>();

            GameObject content = NewUi("Content", viewport.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 230);
            VerticalLayoutGroup contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.childAlignment = TextAnchor.UpperLeft;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            ContentSizeFitter contentFitter = content.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _chatHistoryText = NewText("History", content.transform, font, string.Empty, 14, TextAnchor.UpperLeft);
            _chatHistoryText.supportRichText = false;
            _chatHistoryText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _chatHistoryText.verticalOverflow = VerticalWrapMode.Overflow;
            _chatHistoryText.rectTransform.anchorMin = new Vector2(0, 1);
            _chatHistoryText.rectTransform.anchorMax = new Vector2(1, 1);
            _chatHistoryText.rectTransform.pivot = new Vector2(0.5f, 1);
            _chatHistoryText.rectTransform.anchoredPosition = Vector2.zero;
            _chatHistoryText.rectTransform.sizeDelta = new Vector2(-8, 230);
            ContentSizeFitter fitter = _chatHistoryText.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _chatScroll = root.AddComponent<ScrollRect>();
            _chatScroll.viewport = viewport.GetComponent<RectTransform>();
            _chatScroll.content = contentRect;
            _chatScroll.horizontal = false;
            _chatScroll.vertical = true;
            _chatScroll.movementType = ScrollRect.MovementType.Clamped;
            _chatScroll.scrollSensitivity = 24f;

            _chatChannelButton = NewButton("ChannelButton", root.transform, font, "LOCAL", new Vector2(0, 0), new Vector2(64, 32), new Vector2(110, 38));
            _chatChannelLabel = _chatChannelButton.GetComponentInChildren<Text>();
            _chatChannelButton.onClick.AddListener(OnChatChannelClicked);

            _whisperTargetInput = NewInputField("WhisperTarget", root.transform, font, "Whisper target", new Vector2(0, 0), new Vector2(220, 32), new Vector2(185, 38));

            _chatInput = NewInputField("ChatInput", root.transform, font, "Type message or /help", new Vector2(0, 0), new Vector2(414, 32), new Vector2(355, 38));
            _chatInput.characterLimit = 220;
            _chatInput.onEndEdit.AddListener(OnChatEndEdit);

            Button send = NewButton("SendButton", root.transform, font, "SEND", new Vector2(1, 0), new Vector2(-57, 38), new Vector2(96, 38));
            send.onClick.AddListener(SendChatInput);

            RefreshChatChannelPresentation();
            return root;
        }

        private GameObject BuildServiceNotice(Font font)
        {
            GameObject root = NewImage("ServiceNotice", transform, new Color(0.18f, 0.12f, 0.02f, 0.96f));
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -12f);
            rect.sizeDelta = new Vector2(720f, 48f);

            Text text = NewText("NoticeText", root.transform, font,
                "Backend services unavailable. Persistence/content services are reconnecting.",
                16, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(10f, 8f);
            text.rectTransform.offsetMax = new Vector2(-10f, -8f);
            _serviceNoticeText = text;
            return root;
        }

        private GameObject BuildDisconnect(Font font)
        {
            GameObject root = NewImage("DisconnectOverlay", transform, new Color(0, 0, 0, 0.72f));
            Stretch(root.GetComponent<RectTransform>());

            GameObject panel = NewImage("Panel", root.transform, new Color(0.07f, 0.08f, 0.11f, 0.99f));
            Rect(panel.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(620, 320), Vector2.zero);

            Text title = NewText("Title", panel.transform, font, "CONNECTION LOST", 28, TextAnchor.MiddleCenter);
            Rect(title.rectTransform, new Vector2(0.5f, 1), new Vector2(560, 54), new Vector2(0, -48));

            _disconnectReasonText = NewText("Reason", panel.transform, font, "Connection to the game server was lost.", 17, TextAnchor.MiddleCenter);
            Rect(_disconnectReasonText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(540, 90), new Vector2(0, 25));

            _retryButton = NewButton("RetryButton", panel.transform, font, "RETRY CONNECTION", new Vector2(0.5f, 0), new Vector2(-180, 58), new Vector2(200, 50));
            _returnLoginButton = NewButton("ReturnLoginButton", panel.transform, font, "RETURN TO LOGIN", new Vector2(0.5f, 0), new Vector2(45, 58), new Vector2(200, 50));
            _quitButton = NewButton("QuitButton", panel.transform, font, "QUIT", new Vector2(0.5f, 0), new Vector2(225, 58), new Vector2(120, 50));

            _retryButton.onClick.AddListener(OnRetryConnectionClicked);
            _returnLoginButton.onClick.AddListener(OnReturnToLoginClicked);
            _quitButton.onClick.AddListener(OnQuitClicked);
            return root;
        }

#endif

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
                return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private static string GetResourceDisplayName(CharacterResourceId id)
        {
            switch (id)
            {
                case CharacterResourceId.Health: return "HP";
                case CharacterResourceId.Mana: return "MP";
                case CharacterResourceId.Stamina: return "SP";
                default: return id.ToString();
            }
        }

#if UNITY_EDITOR
        private static Color ResourceColor(CharacterResourceId id)
        {
            switch (id)
            {
                case CharacterResourceId.Health: return new Color(0.66f, 0.14f, 0.14f, 1f);
                case CharacterResourceId.Mana: return new Color(0.15f, 0.33f, 0.72f, 1f);
                case CharacterResourceId.Stamina: return new Color(0.18f, 0.58f, 0.25f, 1f);
                default: return new Color(0.45f, 0.45f, 0.48f, 1f);
            }
        }

        private static GameObject NewUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject NewImage(string name, Transform parent, Color color)
        {
            GameObject go = NewUi(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private static Text NewText(string name, Transform parent, Font font, string value, int size, TextAnchor alignment)
        {
            GameObject go = NewUi(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.text = value ?? string.Empty;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.supportRichText = false;
            return text;
        }

        private static Button NewButton(
            string name,
            Transform parent,
            Font font,
            string label,
            Vector2 anchor,
            Vector2 position,
            Vector2 size)
        {
            GameObject go = NewImage(name, parent, new Color(0.16f, 0.22f, 0.34f, 1f));
            Rect(go.GetComponent<RectTransform>(), anchor, size, position);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            Text text = NewText("Label", go.transform, font, label, 14, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            return button;
        }

        private static InputField NewInputField(
            string name,
            Transform parent,
            Font font,
            string placeholderValue,
            Vector2 anchor,
            Vector2 size,
            Vector2 position)
        {
            GameObject go = NewImage(name, parent, new Color(0.08f, 0.09f, 0.12f, 1f));
            Rect(go.GetComponent<RectTransform>(), anchor, size, position);

            Text text = NewText("Text", go.transform, font, string.Empty, 14, TextAnchor.MiddleLeft);
            Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(8, 3);
            text.rectTransform.offsetMax = new Vector2(-8, -3);

            Text placeholder = NewText("Placeholder", go.transform, font, placeholderValue, 14, TextAnchor.MiddleLeft);
            placeholder.color = new Color(0.58f, 0.61f, 0.68f, 1f);
            Stretch(placeholder.rectTransform);
            placeholder.rectTransform.offsetMin = new Vector2(8, 3);
            placeholder.rectTransform.offsetMax = new Vector2(-8, -3);

            InputField input = go.AddComponent<InputField>();
            go.AddComponent<ClientUiTextInputCapture>();
            input.textComponent = text;
            input.placeholder = placeholder;
            input.targetGraphic = go.GetComponent<Image>();
            input.lineType = InputField.LineType.SingleLine;
            return input;
        }

        private static void Rect(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
#endif
    }
}
