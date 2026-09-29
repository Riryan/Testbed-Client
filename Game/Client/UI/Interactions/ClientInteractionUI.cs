using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Root;
using Game.Client.UI.Gameplay;
using Game.Client.World;
using Game.Shared.Interactions;
using Game.Shared.Abilities;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;
using Game.Shared.Population;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Reusable client interaction presentation shell for the standalone-server client.
    ///
    /// Responsibilities:
    /// - target/context selection presentation
    /// - RDR2-style contextual list/category navigation
    /// - disabled/action reason presentation
    /// - consent/request and active-session surfaces
    /// - routing currently implemented Player Inspect and WorldItem Loot requests
    ///
    /// It never grants gameplay state. Current and future action execution remains
    /// server-authoritative. Planned actions can be shown disabled so the client UI can
    /// be built before the corresponding standalone authoritative feature exists.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientInteractionUI : MonoBehaviour
    {
        private sealed class MenuEntry
        {
            public bool IsCategory;
            public bool IsCombatAction;
            public string CategoryPath;
            public ClientInteractionActionView Action;
            public string Label;
        }

        [Serializable]
        private sealed class SlotView
        {
            public GameObject Root;
            public Button Button;
            public Image Image;
            public Text Label;
            public Text KeyLabel;
            public Text DescriptionLabel;
            public ClientInteractionActionButtonHover Hover;
            [NonSerialized] public MenuEntry Entry;
        }

        private const int SlotCount = 7;
        private const int PagedContentSlots = 5;
        private const float ContextMenuWidth = 390f;
        private const float ContextMenuHeight = 640f;

        [Header("Input")]
        [SerializeField] private float pointerRaycastDistance = 250f;

        [Header("Migration / UI Preview")]
        [Tooltip("Shows future catalog actions as disabled entries so the complete interaction UI can be authored before those standalone gameplay owners exist.")]
        [SerializeField] private bool showPlannedActions = false;
        [Tooltip("Adult catalog entries stay hidden until an authoritative eligibility/session system is wired.")]
        [SerializeField] private bool showAdultPlannedActions;

        [Header("Presentation")]
        [SerializeField] private bool positionRightClickMenuAtPointer = false;
        [SerializeField] private Vector2 keyboardMenuOffset = Vector2.zero;

        private readonly List<ClientInteractionActionView> _actions = new List<ClientInteractionActionView>(64);
        private readonly List<MenuEntry> _entries = new List<MenuEntry>(32);
        [Header("Authored Interaction UI - Rows")]
        [SerializeField] private List<SlotView> _slots = new List<SlotView>(SlotCount);
        private readonly HashSet<InteractionActionId> _addedActions = new HashSet<InteractionActionId>();
        private readonly HashSet<string> _addedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ClientInteractionActionView> _authoritativeContextActions = new List<ClientInteractionActionView>(16);
        // Scratch storage for the world-target compatibility resolver. Player and Population
        // acquisition use ClientGameplayUIRoot's canonical center-reticle actor resolver.
        private readonly List<Renderer> _presentationRendererScratch = new List<Renderer>(16);
        private ClientInteractionTarget _authoritativeContextTarget;
        private uint _contextInteractionSequence;

        private ClientUIRoot _root;
        private ClientGameplayUIRoot _gameplayUi;
        private PlayerEntityGameManager _manager;
        private Font _font;

        [Header("Authored Interaction UI - Context Menu")]
        [SerializeField] private RectTransform _uiRoot;
        [SerializeField] private GameObject _menuLayer;
        [SerializeField] private RectTransform _menuPanel;
        [SerializeField] private CanvasGroup _menuCanvasGroup;
        [SerializeField] private Button _dismissButton;
        [SerializeField] private Text _targetNameText;
        [SerializeField] private Text _targetKindText;
        [SerializeField] private Text _pageText;
        [SerializeField] private Text _detailTitleText;
        [SerializeField] private Text _detailBodyText;
        [SerializeField] private Text _detailStateText;
        [SerializeField] private Button _centerButton;
        [SerializeField] private Text _centerButtonText;

        [Header("Authored Interaction UI - Consent")]
        [SerializeField] private GameObject _consentLayer;
        [SerializeField] private Text _consentTitleText;
        [SerializeField] private Text _consentMessageText;
        [SerializeField] private Text _consentCountdownText;
        [SerializeField] private Button _consentAcceptButton;
        [SerializeField] private Button _consentDeclineButton;
        [SerializeField] private Button _consentCloseButton;
        private ClientInteractionConsentRequestView _consentRequest;

        [Header("Authored Interaction UI - Active Session")]
        [SerializeField] private GameObject _sessionStrip;
        [SerializeField] private Text _sessionText;
        [SerializeField] private Button _sessionEndButton;
        private uint _activeSessionId;
        private InteractionActionId _activeSessionAction;

        private ClientInteractionTarget _currentTarget;
        private string _currentCategoryPath = string.Empty;
        private int _currentPage;
        private bool _inputCaptureHeld;
        private bool _pointerCaptureHeld;
        private uint _primaryInteractionResolveVersion;
        private bool _executing;
        private bool _built;

        public bool IsMenuVisible => _menuLayer != null && _menuLayer.activeSelf;
        public bool HasAuthoredPresentation => _built;
        [Obsolete("Use IsMenuVisible. The radial interaction UI was retired.")]
        public bool IsRadialVisible => IsMenuVisible;
        public bool IsConsentVisible => _consentLayer != null && _consentLayer.activeSelf;
        public bool IsBlockingGameplayInput => IsConsentVisible;
        public bool UsesActionFocusStyle => true;
        public bool IsActionFocusActive => IsMenuVisible;
        public bool ShowPlannedActions { get => showPlannedActions; set { showPlannedActions = value; if (IsMenuVisible) RebuildForTarget(); } }

        public event Action<uint, bool> ConsentDecisionRequested;
        public event Action<uint> EndSessionRequested;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#else
            _root = GetComponent<ClientUIRoot>();
            if (_root == null)
                _root = GetComponentInParent<ClientUIRoot>();
            ResolveStandaloneGameplayRoot();

            // Runtime interaction focus only exposes actions that are actually available.
            // The prefab may still carry the old authoring-preview flag; do not let that
            // leak planned/unwired entries into live TPS interaction hotkeys.
            showPlannedActions = false;

            BindAuthoredPresentation();
            ValidateAuthoredPresentation();
            HideMenu();
            HideConsent();
            HideSession();
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindRoot(_root != null ? _root : FindFirstObjectByType<ClientUIRoot>());
            ResolveStandaloneGameplayRoot();
            _manager = ResolveManager();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            BindRoot(null);
            ReleaseInputCapture();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (_root == null)
                BindRoot(FindFirstObjectByType<ClientUIRoot>());

            UpdateConsentCountdown();

            if (IsConsentVisible)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    RespondToConsent(false);
                return;
            }

            if (IsMenuVisible)
            {
                if (!ValidateCurrentTargetPresentation())
                {
                    HideMenu();
                    PresentFeedback("INTERACTION: Target is no longer available.");
                    return;
                }
                if (!TryValidateClientTargetRange(_currentTarget, out _))
                {
                    HideMenu();
                    PresentFeedback("INTERACTION: Target moved out of range.");
                    return;
                }
                UpdateTargetHeader();
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                {
                    HideMenu();
                    return;
                }
                return;
            }

            if (!IsGameplaySessionActive)
                return;

            // Do not steal interaction input from chat/text-entry or another modal UI.
            if (LocalClientInputGate.IsGameplayInputBlocked)
                return;
#endif
        }

        public void BindRoot(ClientUIRoot root)
        {
#if !UNITY_SERVER
            // Preserve the legacy authored-root path, but also allow the standalone authored
            // gameplay prefab to own this same interaction controller directly.
            _root = root;
            ResolveStandaloneGameplayRoot();
            _manager = ResolveManager();
#endif
        }

        private void ResolveStandaloneGameplayRoot()
        {
            if (_gameplayUi != null)
                return;
            _gameplayUi = GetComponent<ClientGameplayUIRoot>();
            if (_gameplayUi == null)
                _gameplayUi = GetComponentInParent<ClientGameplayUIRoot>();
            if (_gameplayUi == null)
                _gameplayUi = FindFirstObjectByType<ClientGameplayUIRoot>(FindObjectsInactive.Include);
        }

        private bool IsGameplaySessionActive
        {
            get
            {
                if (_root != null)
                    return _root.GameplaySessionActive;
                ResolveStandaloneGameplayRoot();
                return _gameplayUi != null && _gameplayUi.GameplayVisible;
            }
        }

        private PlayerEntityGameManager ResolveManager()
        {
            if (_root != null && _root.Manager != null)
                return _root.Manager;
            ResolveStandaloneGameplayRoot();
            if (_gameplayUi != null && _gameplayUi.Manager != null)
                return _gameplayUi.Manager;
            return _manager != null ? _manager : FindFirstObjectByType<PlayerEntityGameManager>(FindObjectsInactive.Include);
        }

        private void PresentFeedback(string message)
        {
            if (_root != null)
                _root.PresentInteractionFeedback(message);
            else
            {
                ResolveStandaloneGameplayRoot();
                _gameplayUi?.PresentLocalSystemMessage(message);
            }
        }

        private void SetInteractionOverlayActive(bool active)
        {
            if (_root != null)
                _root.SetInteractionMenuOverlayActive(active);
            ResolveStandaloneGameplayRoot();
            _gameplayUi?.SetInteractionOverlayActive(active);
        }

        private void PrefillChatCommand(string command)
        {
            if (_root != null)
                _root.PrefillChatCommand(command);
            else
            {
                ResolveStandaloneGameplayRoot();
                _gameplayUi?.PrefillChatCommand(command);
            }
        }

#if UNITY_EDITOR
        public void BuildPresentationForEditor()
        {
            EnsurePresentationForEditor();
            BindAuthoredPresentation();
        }
#endif

        /// <summary>
        /// Canonical TPS interaction entry point.
        /// One valid action executes immediately. Multiple valid actions open the authored
        /// clickable interaction panel and release the cursor. No per-action gameplay hotkeys
        /// are required; E/Interact confirms the first/default action while the panel is open.
        /// </summary>
        public bool HandlePrimaryInteraction()
        {
#if UNITY_SERVER
            return false;
#else
            PresentFeedback(
                $"INTERACTION TRACE: entry session={IsGameplaySessionActive} consent={IsConsentVisible} " +
                $"gameplayBlocked={LocalClientInputGate.IsGameplayInputBlocked} menu={IsMenuVisible}.");

            if (IsMenuVisible)
            {
                bool activated = TryActivatePrimaryMenuAction();
                PresentFeedback($"INTERACTION TRACE: existing menu primary activation={(activated ? "YES" : "NO")}.");
                return activated;
            }

            if (!IsGameplaySessionActive)
            {
                PresentFeedback("INTERACTION TRACE: STOP - gameplay session inactive.");
                return false;
            }
            if (IsConsentVisible)
            {
                PresentFeedback("INTERACTION TRACE: STOP - consent UI active.");
                return false;
            }
            if (LocalClientInputGate.IsGameplayInputBlocked)
            {
                PresentFeedback("INTERACTION TRACE: STOP - gameplay input gate blocked.");
                return false;
            }
            if (!TryResolvePrimaryInteractionTarget(out ClientInteractionTarget target))
            {
                PresentFeedback("INTERACTION TRACE: STOP - no canonical reticle target resolved.");
                return false;
            }

            PresentFeedback(
                $"INTERACTION TRACE: target kind={target.Kind} primary={target.PrimaryId} " +
                $"object={target.NetworkObjectId}.");

            if (!TryValidateClientTargetRange(target, out string traceRangeReason))
            {
                PresentFeedback($"INTERACTION TRACE: STOP - range validation: {traceRangeReason}");
                return false;
            }

            PresentFeedback("INTERACTION TRACE: target accepted; beginning primary interaction.");
            BeginPrimaryInteraction(target);
            return true;
#endif
        }

        [Obsolete("Use HandlePrimaryInteraction. Interaction focus is now a clickable contextual panel.")]
        public bool ToggleKeyboardFocus() => HandlePrimaryInteraction();

        public bool TryActivatePrimaryMenuAction()
        {
#if UNITY_SERVER
            return false;
#else
            if (!IsMenuVisible || _executing)
                return false;

            BuildCurrentEntries();
            for (int i = 0; i < _entries.Count; ++i)
            {
                MenuEntry entry = _entries[i];
                if (entry == null || entry.IsCategory)
                    continue;

                if (entry.IsCombatAction)
                {
                    ExecutePopulationCombatAction();
                    return true;
                }

                if (entry.Action.ActionId != InteractionActionId.None && entry.Action.Enabled)
                {
                    OnActionClicked(entry.Action);
                    return true;
                }
            }

            return false;
#endif
        }

        private void BeginPrimaryInteraction(ClientInteractionTarget target)
        {
            PrepareCurrentTarget(target);

            // Death-loot availability is already replicated as public Population state.
            // Do not round-trip an InteractionMenu request for a fact the client already knows.
            // Present/send the normal Loot action immediately; the GameServer independently
            // validates the existing ContextInteraction request before opening any loot.
            if (TryGetPublicPopulationDeathLootAction(target, out ClientInteractionActionView deathLootAction))
            {
                ExecuteActionForTarget(deathLootAction, target);
                return;
            }

            CollectActions(target, _actions);

            PresentResolvedPrimaryInteraction(target, authorityAlreadyResolved: true);
        }

        private static bool TryGetPublicPopulationDeathLootAction(
            ClientInteractionTarget target,
            out ClientInteractionActionView action)
        {
            action = default;
            if (target.Kind != InteractionTargetKind.PopulationEntity ||
                !(target.SourceObject is PlayerEntityClient population) ||
                !population.IsPopulationPresentation ||
                !population.IsDeadPresentation ||
                population.NetworkBridge == null ||
                !population.NetworkBridge.IsSpawned ||
                (population.NetworkBridge.Appearance.populationInteractionFlags &
                 PopulationPublicInteractionFlags.DeathLootAvailable) == 0)
            {
                return false;
            }

            action = new ClientInteractionActionView(
                InteractionActionId.Loot,
                InteractionCategoryCatalog.Label(InteractionCategoryId.Interact),
                "Loot",
                ClientInteractionPresentationCatalog.Description(InteractionActionId.Loot),
                enabled: true,
                disabledReason: string.Empty,
                InteractionConsentMode.None,
                InteractionContentLevel.General,
                sortOrder: 10,
                isPlanned: false,
                categoryId: InteractionCategoryId.Interact);
            return true;
        }

        private void PrepareCurrentTarget(ClientInteractionTarget target)
        {
            _manager = ResolveManager();
            _currentTarget = target;
            _currentCategoryPath = string.Empty;
            _currentPage = 0;
            _authoritativeContextTarget = default;
            _authoritativeContextActions.Clear();
        }

        private int CountResolvedPrimaryActions(out ClientInteractionActionView firstAction)
        {
            firstAction = default;
            int count = 0;

            for (int i = 0; i < _actions.Count; ++i)
            {
                ClientInteractionActionView action = _actions[i];
                if (action.IsPlanned || !action.Enabled || !TryPreflightAction(_currentTarget, action, out _))
                    continue;

                if (count == 0)
                    firstAction = action;
                count++;
            }

            return count;
        }

        private void PresentResolvedPrimaryInteraction(ClientInteractionTarget target, bool authorityAlreadyResolved)
        {
            if (!target.IsValid || !SameTarget(_currentTarget, target))
                return;
            if (!TryValidateClientTargetRange(target, out string rangeReason))
            {
                PresentFeedback($"INTERACTION: {rangeReason}");
                return;
            }

            CollectActions(_currentTarget, _actions);
            int count = CountResolvedPrimaryActions(out ClientInteractionActionView firstAction);
            if (count <= 0)
            {
                PresentFeedback("INTERACTION: No contextual action is currently available for this target.");
                return;
            }

            if (count == 1)
            {
                if (firstAction.ActionId != InteractionActionId.None)
                    ExecuteActionForTarget(firstAction, _currentTarget);
                return;
            }

            Camera camera = Camera.main;
            Vector2 screenPosition = camera != null
                ? new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f)
                : Vector2.zero;
            ShowTarget(_currentTarget, screenPosition, refreshContext: !authorityAlreadyResolved);
        }


        public bool TryOpenCurrentContext() => HandlePrimaryInteraction();

        public bool TryOpenAtPointer(Vector2 screenPosition)
        {
#if UNITY_SERVER
            return false;
#else
            // Retained only for source compatibility with older callers. Canonical gameplay
            // interaction target acquisition comes from the center reticle when Left Alt toggles
            // Action Focus; pointer/right-click is not an independent interaction input path.
            return TryOpenCurrentContext();
#endif
        }

        public void OpenWorldItem(long itemInstanceId, Vector2 screenPosition)
        {
#if !UNITY_SERVER
            if (!TryCaptureWorldItem(itemInstanceId, out ClientInteractionTarget target))
            {
                PresentFeedback("INTERACTION: World item is no longer available.");
                return;
            }
            BeginPrimaryInteraction(target);
#endif
        }

        public void ShowConsentRequest(ClientInteractionConsentRequestView request)
        {
#if !UNITY_SERVER
            if (!_built)
                return;
            HideMenu();
            _consentRequest = request;
            if (_consentTitleText != null)
                _consentTitleText.text = string.IsNullOrWhiteSpace(request.Title)
                    ? ClientInteractionPresentationCatalog.Label(request.ActionId)
                    : request.Title;
            if (_consentMessageText != null)
                _consentMessageText.text = request.Message;
            if (_consentAcceptButton != null)
                _consentAcceptButton.interactable = request.CanAccept;
            if (_consentLayer != null)
                _consentLayer.SetActive(true);
            UpdateInputCapture();
            UpdateConsentCountdown();
#endif
        }

        public void HideConsent()
        {
#if !UNITY_SERVER
            _consentRequest = default;
            if (_consentLayer != null)
                _consentLayer.SetActive(false);
            UpdateInputCapture();
#endif
        }

        public void ShowActiveSession(uint sessionId, InteractionActionId actionId, string detail)
        {
#if !UNITY_SERVER
            if (!_built)
                return;
            _activeSessionId = sessionId;
            _activeSessionAction = actionId;
            if (_sessionText != null)
            {
                string label = ClientInteractionPresentationCatalog.Label(actionId);
                _sessionText.text = string.IsNullOrWhiteSpace(detail)
                    ? $"ACTIVE INTERACTION: {label}"
                    : $"ACTIVE INTERACTION: {label} — {detail}";
            }
            if (_sessionStrip != null)
                _sessionStrip.SetActive(true);
#endif
        }

        public void HideSession()
        {
#if !UNITY_SERVER
            _activeSessionId = 0;
            _activeSessionAction = InteractionActionId.None;
            if (_sessionStrip != null)
                _sessionStrip.SetActive(false);
#endif
        }

        public void HideAllInteractionSurfaces()
        {
            HideMenu();
            HideConsent();
            HideSession();
        }

        public void OnSlotHover(int slotIndex, bool entered)
        {
#if !UNITY_SERVER
            if (!entered)
                return;
            if (slotIndex < 0 || slotIndex >= _slots.Count)
                return;
            MenuEntry entry = _slots[slotIndex].Entry;
            if (entry == null)
                return;
            if (entry.IsCategory)
            {
                SetDetail(entry.Label, "Open this interaction category.", "CATEGORY");
                return;
            }
            if (entry.IsCombatAction)
            {
                SetDetail("Attack", "Use the existing basic-attack combat path against this target.", "AVAILABLE");
                return;
            }

            ClientInteractionActionView action = entry.Action;
            string state = action.Enabled
                ? (action.ConsentMode == InteractionConsentMode.None ? "AVAILABLE" : $"AVAILABLE • {ConsentLabel(action.ConsentMode)}")
                : (action.IsPlanned ? "PLANNED / NOT WIRED" : "UNAVAILABLE");
            string body = action.Description;
            if (!action.Enabled && !string.IsNullOrWhiteSpace(action.DisabledReason))
                body = string.IsNullOrWhiteSpace(body) ? action.DisabledReason : body + "\n\n" + action.DisabledReason;
            SetDetail(action.Label, body, state);
#endif
        }

        private void OnSelectedPlayerTargetChanged(PlayerEntityNetwork target)
        {
#if !UNITY_SERVER
            if (!IsMenuVisible || _currentTarget.Kind != InteractionTargetKind.PlayerEntity)
                return;
            if (target == null)
            {
                HideMenu();
                return;
            }
            if (TryCapturePlayer(target, out ClientInteractionTarget captured))
            {
                _currentTarget = captured;
                RebuildForTarget();
            }
#endif
        }

        private void ShowTarget(ClientInteractionTarget target, Vector2 screenPosition, bool refreshContext = true)
        {
#if !UNITY_SERVER
            if (!target.IsValid)
                return;
            if (!_built)
            {
                PresentFeedback(
                    "INTERACTION: Authored interaction UI is not bound. " +
                    "Run MMO Tools > Interaction > Repair Canonical Interaction UI.");
                Debug.LogError(
                    "[InteractionUI] Server actions were resolved, but the authored interaction presentation is not bound. " +
                    "Run MMO Tools > Interaction > Repair Canonical Interaction UI.",
                    this);
                return;
            }
            _manager = ResolveManager();
            _currentTarget = target;
            _currentCategoryPath = string.Empty;
            _currentPage = 0;
            RebuildForTarget();
            PositionContextMenu(screenPosition);
            if (_menuLayer != null)
                _menuLayer.SetActive(true);
            SetInteractionOverlayActive(true);
            if (_menuCanvasGroup != null)
            {
                _menuCanvasGroup.alpha = 1f;
                _menuCanvasGroup.blocksRaycasts = true;
                _menuCanvasGroup.interactable = true;
            }
            UpdateInputCapture();
            if (refreshContext)
                RefreshContextualActions(target);
#endif
        }

        public void HideMenu()
        {
#if !UNITY_SERVER
            unchecked { _primaryInteractionResolveVersion++; }
            _currentTarget = default;
            _currentCategoryPath = string.Empty;
            _currentPage = 0;
            _authoritativeContextTarget = default;
            _authoritativeContextActions.Clear();
            _executing = false;
            if (_menuLayer != null)
                _menuLayer.SetActive(false);
            SetInteractionOverlayActive(false);
            UpdateInputCapture();
#endif
        }

        [Obsolete("Use HideMenu. The radial interaction UI was retired.")]
        public void HideRadial() => HideMenu();

        private void RebuildForTarget()
        {
            CollectActions(_currentTarget, _actions);
            UpdateTargetHeader();
            BuildCurrentEntries();
            RenderPage();
        }

        private void CollectActions(ClientInteractionTarget target, List<ClientInteractionActionView> destination)
        {
            destination.Clear();
            _addedActions.Clear();

            // Public interaction capability is client-known presentation data. Opening a menu
            // must not ask the GameServer to rediscover static/public actions. The client filters
            // from baked/catalog data and already-replicated public state; execution still uses
            // ContextInteraction and the GameServer independently validates every selection.
            if (HasLocalPublicInteractionDefinitions(target) &&
                target.SourceObject is ClientInteractionTargetMarker marker)
            {
                InteractionPublicDefinition[] publicDefinitions = marker.PublicInteractions;
                for (int i = 0; i < publicDefinitions.Length; ++i)
                {
                    InteractionPublicDefinition def = publicDefinitions[i];
                    if (target.Kind == InteractionTargetKind.PopulationEntity && def.actionId == InteractionActionId.Inspect)
                        continue;
                    if (!def.IsValid || !_addedActions.Add(def.actionId))
                        continue;
                    if (!EvaluateLocalPublicAction(target, def, out _))
                        continue;

                    destination.Add(new ClientInteractionActionView(
                        def.actionId,
                        InteractionCategoryCatalog.Label(def.categoryId),
                        string.IsNullOrWhiteSpace(def.label) ? ClientInteractionPresentationCatalog.Label(def.actionId) : def.label,
                        ClientInteractionPresentationCatalog.Description(def.actionId),
                        enabled: true,
                        disabledReason: string.Empty,
                        def.consentMode,
                        def.contentLevel,
                        def.sortOrder,
                        isPlanned: false,
                        categoryId: def.categoryId,
                        maximumUseDistance: def.maximumUseDistance));
                }
                destination.Sort(CompareActions);
                return;
            }

            AddClientKnownPublicActions(target, destination);
            if (destination.Count > 0)
            {
                destination.Sort(CompareActions);
                return;
            }

            if (!showPlannedActions)
            {
                destination.Sort(CompareActions);
                return;
            }

            string plannedReason = "Standalone authoritative gameplay owner is not wired yet. This entry is shown for UI/layout development only.";
            switch (target.Kind)
            {
                case InteractionTargetKind.PlayerEntity:
                    AddPlanned(destination, plannedReason,
                        InteractionActionId.TradeRequest, InteractionActionId.PartyInvite, InteractionActionId.GuildInvite,
                        InteractionActionId.DuelRequest, InteractionActionId.Whisper, InteractionActionId.ReportPlayer,
                        InteractionActionId.AddFriend, InteractionActionId.RemoveFriend, InteractionActionId.GiftFriend,
                        InteractionActionId.CoupleInvite, InteractionActionId.Divorce,
                        InteractionActionId.SenseBlood, InteractionActionId.InspectBlood, InteractionActionId.RequestBlood,
                        InteractionActionId.OfferBlood, InteractionActionId.OfferProtection, InteractionActionId.Feed,
                        InteractionActionId.Intimidate, InteractionActionId.Charm, InteractionActionId.Dominate,
                        InteractionActionId.OfferTurning, InteractionActionId.VampireEmbrace,
                        InteractionActionId.Scan, InteractionActionId.Question, InteractionActionId.MarkSuspect,
                        InteractionActionId.BeginTracking, InteractionActionId.SearchTarget, InteractionActionId.Restrain,
                        InteractionActionId.RequestCooperation, InteractionActionId.ShareIntel,
                        InteractionActionId.OpenEmotes, InteractionActionId.PlayTargetedEmote,
                        InteractionActionId.PartnerDance, InteractionActionId.Hug, InteractionActionId.Kiss,
                        InteractionActionId.RequestAdultSession);
                    break;

                case InteractionTargetKind.PopulationEntity:
                    AddPlanned(destination, plannedReason,
                        InteractionActionId.Talk, InteractionActionId.Inspect,
                        InteractionActionId.SenseBlood, InteractionActionId.InspectBlood, InteractionActionId.RequestBlood,
                        InteractionActionId.OfferProtection, InteractionActionId.Feed, InteractionActionId.Intimidate,
                        InteractionActionId.Charm, InteractionActionId.Dominate, InteractionActionId.Recruit,
                        InteractionActionId.Enthrall, InteractionActionId.BloodBond, InteractionActionId.CreateGhoul,
                        InteractionActionId.OfferTurning, InteractionActionId.TurnIntoVampire,
                        InteractionActionId.Scan, InteractionActionId.CollectEvidence, InteractionActionId.CollectSample,
                        InteractionActionId.PhotographEvidence, InteractionActionId.Question,
                        InteractionActionId.InterviewWitness, InteractionActionId.MarkSuspect,
                        InteractionActionId.BeginTracking, InteractionActionId.SearchTarget, InteractionActionId.Restrain,
                        InteractionActionId.ApplyWard, InteractionActionId.TestSupernaturalTrace,
                        InteractionActionId.RecruitInformant, InteractionActionId.RequestCooperation,
                        InteractionActionId.OpenEmotes, InteractionActionId.PlayTargetedEmote);
                    break;

                case InteractionTargetKind.NetworkEntity:
                    AddPlanned(destination, plannedReason,
                        InteractionActionId.Inspect, InteractionActionId.Talk, InteractionActionId.Use,
                        InteractionActionId.Loot, InteractionActionId.Examine,
                        InteractionActionId.Scan, InteractionActionId.CollectEvidence,
                        InteractionActionId.SearchTarget, InteractionActionId.Restrain,
                        InteractionActionId.OpenEmotes, InteractionActionId.PlayTargetedEmote);
                    break;

                case InteractionTargetKind.SceneObject:
                    AddPlanned(destination, plannedReason,
                        InteractionActionId.Use, InteractionActionId.Open, InteractionActionId.OpenContainer,
                        InteractionActionId.Unlock, InteractionActionId.Examine, InteractionActionId.Harvest, InteractionActionId.Craft,
                        InteractionActionId.Teleport, InteractionActionId.OpenStorage, InteractionActionId.OpenQuests);
                    break;

                case InteractionTargetKind.NetworkWorldObject:
                    AddPlanned(destination, plannedReason,
                        InteractionActionId.Examine, InteractionActionId.OpenContainer, InteractionActionId.Unlock);
                    break;
            }

            destination.Sort(CompareActions);
        }

        private bool EvaluateLocalPublicAction(
            ClientInteractionTarget target,
            InteractionPublicDefinition definition,
            out string reason)
        {
            reason = string.Empty;
            if (!definition.IsValid)
            {
                reason = "Interaction definition is invalid.";
                return false;
            }

            if (target.PrimaryId > 0 &&
                ClientWorldInteractableStateRuntime.TryGetState(target.PrimaryId, out WorldInteractableStateMessage state))
            {
                if (!state.enabled)
                {
                    reason = "Target is currently unavailable.";
                    return false;
                }

                if (definition.actionId == InteractionActionId.Search && state.depleted)
                {
                    reason = "Target is depleted.";
                    return false;
                }
            }

            if (definition.maximumUseDistance > 0f)
            {
                Vector3 local = FindLocalPlayerPosition(out bool found);
                if (found)
                {
                    Vector3 targetPosition = target.WorldPosition;
                    if (target.SourceObject is Component component && component != null)
                        targetPosition = component.transform.position;
                    float maximumUseDistance = InteractionRangePolicy.ClampWorldUseRange(definition.maximumUseDistance);
                    if ((targetPosition - local).sqrMagnitude > maximumUseDistance * maximumUseDistance)
                    {
                        reason = "Target is out of interaction range.";
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryPreflightCurrentAction(ClientInteractionActionView action, out string reason) =>
            TryPreflightAction(_currentTarget, action, out reason);

        private bool TryPreflightAction(
            ClientInteractionTarget target,
            ClientInteractionActionView action,
            out string reason)
        {
            reason = string.Empty;
            if (action.CategoryId == InteractionCategoryId.None || action.ActionId == InteractionActionId.None)
            {
                reason = "Interaction category/action is invalid.";
                return false;
            }

            if (!TryValidateClientTargetRange(target, out reason))
                return false;

            if (HasLocalPublicInteractionDefinitions(target) &&
                target.SourceObject is ClientInteractionTargetMarker marker)
            {
                InteractionPublicDefinition[] defs = marker.PublicInteractions;
                for (int i = 0; i < defs.Length; ++i)
                {
                    if (defs[i].categoryId != action.CategoryId || defs[i].actionId != action.ActionId) continue;
                    return EvaluateLocalPublicAction(target, defs[i], out reason);
                }
                reason = "Target does not advertise this interaction.";
                return false;
            }

            if (!InteractionCategoryCatalog.IsCompatible(target.Kind, action.CategoryId, action.ActionId))
            {
                reason = "Interaction category/action is not valid for this target type.";
                return false;
            }

            return true;
        }

        private void AddPlanned(List<ClientInteractionActionView> destination, string reason, params InteractionActionId[] ids)
        {
            for (int i = 0; i < ids.Length; ++i)
            {
                if (!showAdultPlannedActions && ClientInteractionPresentationCatalog.Content(ids[i]) == InteractionContentLevel.Adult)
                    continue;
                AddAction(destination, ids[i], enabled: false, isPlanned: true, disabledReason: reason);
            }
        }

        private void AddAction(
            List<ClientInteractionActionView> destination,
            InteractionActionId actionId,
            bool enabled,
            bool isPlanned,
            string disabledReason)
        {
            if (actionId == InteractionActionId.None || !_addedActions.Add(actionId))
                return;
            destination.Add(new ClientInteractionActionView(
                actionId,
                ClientInteractionPresentationCatalog.Category(actionId),
                ClientInteractionPresentationCatalog.Label(actionId),
                ClientInteractionPresentationCatalog.Description(actionId),
                enabled,
                disabledReason,
                ClientInteractionPresentationCatalog.Consent(actionId),
                ClientInteractionPresentationCatalog.Content(actionId),
                ClientInteractionPresentationCatalog.SortOrder(actionId),
                isPlanned,
                ClientInteractionPresentationCatalog.CategoryId(actionId)));
        }

        private static int CompareActions(ClientInteractionActionView left, ClientInteractionActionView right)
        {
            int enabled = right.Enabled.CompareTo(left.Enabled);
            if (enabled != 0) return enabled;
            int category = string.Compare(left.CategoryPath, right.CategoryPath, StringComparison.OrdinalIgnoreCase);
            if (category != 0) return category;
            int sort = left.SortOrder.CompareTo(right.SortOrder);
            return sort != 0 ? sort : left.ActionId.CompareTo(right.ActionId);
        }

        private void BuildCurrentEntries()
        {
            _entries.Clear();
            _addedCategories.Clear();

            // The standalone interaction panel is intentionally flat: the player sees the
            // server/client-approved actions directly (Talk, Trade, Inspect, etc.) instead
            // of opening nested action categories. Overflow uses the existing page controls.
            for (int i = 0; i < _actions.Count; ++i)
            {
                ClientInteractionActionView action = _actions[i];
                if (action.IsPlanned || !action.Enabled)
                    continue;

                _entries.Add(new MenuEntry
                {
                    IsCategory = false,
                    Action = action,
                    Label = action.Label,
                });
            }

            _entries.Sort(CompareMenuEntries);
            int pageCount = PageCount();
            _currentPage = Mathf.Clamp(_currentPage, 0, Math.Max(0, pageCount - 1));
        }

        private void AddCategoryEntry(string path, string label)
        {
            if (!_addedCategories.Add(path)) return;
            _entries.Add(new MenuEntry { IsCategory = true, CategoryPath = path, Label = label });
        }

        private static int CompareMenuEntries(MenuEntry left, MenuEntry right)
        {
            if (left.IsCategory != right.IsCategory)
                return left.IsCategory ? 1 : -1;
            if (left.IsCategory)
            {
                int l = CategoryOrder(left.CategoryPath);
                int r = CategoryOrder(right.CategoryPath);
                int order = l.CompareTo(r);
                return order != 0 ? order : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
            }
            if (left.IsCombatAction != right.IsCombatAction)
                return left.IsCombatAction ? 1 : -1;
            if (left.IsCombatAction)
                return string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);

            int enabled = right.Action.Enabled.CompareTo(left.Action.Enabled);
            if (enabled != 0) return enabled;
            int sort = left.Action.SortOrder.CompareTo(right.Action.SortOrder);
            return sort != 0 ? sort : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        }

        private static int CategoryOrder(string path)
        {
            string root = path ?? string.Empty;
            int slash = root.IndexOf('/');
            if (slash >= 0) root = root.Substring(0, slash);
            switch (root)
            {
                case "Use": return 0;
                case "Interact": return 10;
                default: return 100;
            }
        }

        private int PageCount()
        {
            if (_entries.Count <= SlotCount) return 1;
            return Math.Max(1, (int)Math.Ceiling(_entries.Count / (double)PagedContentSlots));
        }

        private void BindKeyboardCombatSlot(int index, string keyLabel)
        {
            if (index < 0 || index >= _slots.Count)
                return;
            SlotView slot = _slots[index];
            slot.Entry = new MenuEntry { IsCombatAction = true, Label = "Attack" };
            if (slot.Root != null) slot.Root.SetActive(true);
            if (slot.Label != null) slot.Label.text = "ATTACK";
            if (slot.KeyLabel != null) slot.KeyLabel.text = keyLabel;
            if (slot.DescriptionLabel != null) slot.DescriptionLabel.text = "Attack the selected NPC with the current basic attack.";
            if (slot.Image != null) slot.Image.color = new Color(0.34f, 0.16f, 0.16f, 0.96f);
            if (slot.Button != null)
            {
                slot.Button.onClick.RemoveAllListeners();
                slot.Button.interactable = false;
            }
        }

        private void ExecutePopulationCombatAction()
        {
            if (_root == null || _currentTarget.Kind != InteractionTargetKind.PopulationEntity || !_currentTarget.IsValid)
                return;

            _root.SelectPopulationTarget(
                _currentTarget.PrimaryId,
                _currentTarget.Generation,
                _currentTarget.DisplayName,
                _currentTarget.WorldPosition);
            _root.RequestCombatAction(BasicAttackInputKind.Light);
            HideMenu();
        }

        private void ResetSlot(int index)
        {
            if (index < 0 || index >= _slots.Count)
                return;
            SlotView slot = _slots[index];
            slot.Entry = null;
            if (slot.Root != null) slot.Root.SetActive(false);
            if (slot.KeyLabel != null) slot.KeyLabel.text = string.Empty;
            if (slot.DescriptionLabel != null) slot.DescriptionLabel.text = string.Empty;
        }

        private void BindKeyboardActionSlot(int index, ClientInteractionActionView action, string keyLabel)
        {
            if (index < 0 || index >= _slots.Count)
                return;
            SlotView slot = _slots[index];
            slot.Entry = new MenuEntry { Action = action, Label = action.Label };
            if (slot.Root != null) slot.Root.SetActive(true);
            if (slot.Label != null) slot.Label.text = action.Label.ToUpperInvariant();
            if (slot.KeyLabel != null) slot.KeyLabel.text = keyLabel;
            if (slot.DescriptionLabel != null)
                slot.DescriptionLabel.text = action.Enabled
                    ? action.Description
                    : (string.IsNullOrWhiteSpace(action.DisabledReason) ? "Unavailable" : action.DisabledReason);
            if (slot.Image != null)
                slot.Image.color = action.Enabled
                    ? new Color(0.13f, 0.28f, 0.24f, 0.96f)
                    : new Color(0.14f, 0.14f, 0.16f, 0.92f);
            if (slot.Button != null)
            {
                slot.Button.onClick.RemoveAllListeners();
                slot.Button.interactable = false;
            }
        }

        private void BindKeyboardMoreSlot(int index)
        {
            if (index < 0 || index >= _slots.Count)
                return;
            SlotView slot = _slots[index];
            slot.Entry = new MenuEntry { IsCategory = true, Label = "More" };
            if (slot.Root != null) slot.Root.SetActive(true);
            if (slot.Label != null) slot.Label.text = "MORE";
            if (slot.KeyLabel != null) slot.KeyLabel.text = PlayerControlConfig.BindingLabel(PlayerControlAction.InteractionAction4);
            if (slot.DescriptionLabel != null) slot.DescriptionLabel.text = "Show the next interaction actions.";
            if (slot.Image != null) slot.Image.color = new Color(0.12f, 0.17f, 0.24f, 0.96f);
            if (slot.Button != null)
            {
                slot.Button.onClick.RemoveAllListeners();
                slot.Button.interactable = false;
            }
        }

        private static string KeyboardSlotLabel(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: return PlayerControlConfig.BindingLabel(PlayerControlAction.Interact);
                case 1: return PlayerControlConfig.BindingLabel(PlayerControlAction.InteractionAction2);
                case 2: return PlayerControlConfig.BindingLabel(PlayerControlAction.InteractionAction3);
                case 3: return PlayerControlConfig.BindingLabel(PlayerControlAction.InteractionAction4);
                default: return string.Empty;
            }
        }

        private void RenderPage()
        {
            for (int i = 0; i < _slots.Count; ++i)
                ResetSlot(i);

            int pageCount = PageCount();
            bool paged = pageCount > 1;
            int contentSlots = paged ? PagedContentSlots : SlotCount;
            int start = _currentPage * contentSlots;
            int count = Math.Min(contentSlots, Math.Max(0, _entries.Count - start));
            for (int i = 0; i < count; ++i)
                BindSlot(i, _entries[start + i]);

            if (paged)
            {
                BindNavigationSlot(5, "< PREVIOUS", -1, _currentPage > 0);
                BindNavigationSlot(6, "NEXT >", +1, _currentPage + 1 < pageCount);
            }

            if (_pageText != null)
                _pageText.text = pageCount > 1 ? $"PAGE {_currentPage + 1}/{pageCount}" : string.Empty;
            if (_centerButtonText != null)
                _centerButtonText.text = string.IsNullOrEmpty(_currentCategoryPath) ? "CLOSE" : "BACK";

            SetDetail(
                "INTERACT",
                "Click an action, or press the Interact binding again to use the first/default action.",
                $"{PlayerControlConfig.BindingLabel(PlayerControlAction.Interact)}: PRIMARY • {PlayerControlConfig.BindingLabel(PlayerControlAction.OpenMenu)}: CLOSE");
        }

        private void BindSlot(int index, MenuEntry entry)
        {
            SlotView slot = _slots[index];
            slot.Entry = entry;
            slot.Root.SetActive(true);
            if (slot.KeyLabel != null)
                slot.KeyLabel.text = index == 0 ? PlayerControlConfig.BindingLabel(PlayerControlAction.Interact) : string.Empty;
            if (slot.DescriptionLabel != null) slot.DescriptionLabel.text = string.Empty;
            slot.Button.interactable = true;
            slot.Button.onClick.RemoveAllListeners();
            if (entry.IsCategory)
            {
                slot.Label.text = entry.Label.ToUpperInvariant() + "  >";
                slot.Image.color = new Color(0.12f, 0.17f, 0.24f, 0.96f);
                string path = entry.CategoryPath;
                slot.Button.onClick.AddListener(() => EnterCategory(path));
            }
            else if (entry.IsCombatAction)
            {
                slot.Label.text = "ATTACK";
                slot.Image.color = new Color(0.34f, 0.16f, 0.16f, 0.96f);
                slot.Button.onClick.AddListener(ExecutePopulationCombatAction);
            }
            else
            {
                ClientInteractionActionView action = entry.Action;
                slot.Label.text = action.Enabled ? action.Label.ToUpperInvariant() : action.Label;
                slot.Image.color = action.Enabled
                    ? new Color(0.13f, 0.28f, 0.24f, 0.96f)
                    : new Color(0.14f, 0.14f, 0.16f, 0.92f);
                slot.Button.onClick.AddListener(() => OnActionClicked(action));
            }
        }

        private void BindNavigationSlot(int index, string label, int pageDelta, bool enabled)
        {
            SlotView slot = _slots[index];
            slot.Entry = new MenuEntry { IsCategory = true, Label = label };
            slot.Root.SetActive(true);
            if (slot.KeyLabel != null) slot.KeyLabel.text = string.Empty;
            if (slot.DescriptionLabel != null) slot.DescriptionLabel.text = string.Empty;
            slot.Label.text = label;
            slot.Image.color = enabled
                ? new Color(0.12f, 0.17f, 0.24f, 0.96f)
                : new Color(0.10f, 0.10f, 0.11f, 0.86f);
            slot.Button.interactable = enabled;
            slot.Button.onClick.RemoveAllListeners();
            if (enabled)
                slot.Button.onClick.AddListener(() => ChangePage(pageDelta));
        }

        private void EnterCategory(string path)
        {
            _currentCategoryPath = path ?? string.Empty;
            _currentPage = 0;
            BuildCurrentEntries();
            RenderPage();
        }

        private void ChangePage(int delta)
        {
            int pageCount = PageCount();
            _currentPage = Mathf.Clamp(_currentPage + delta, 0, Math.Max(0, pageCount - 1));
            RenderPage();
        }

        private void CenterClicked()
        {
            if (string.IsNullOrEmpty(_currentCategoryPath))
            {
                HideMenu();
                return;
            }

            int slash = _currentCategoryPath.LastIndexOf('/');
            _currentCategoryPath = slash >= 0 ? _currentCategoryPath.Substring(0, slash) : string.Empty;
            _currentPage = 0;
            BuildCurrentEntries();
            RenderPage();
        }

        private void OnActionClicked(ClientInteractionActionView action)
        {
            if (_executing)
                return;
            if (!TryPreflightCurrentAction(action, out string preflightReason))
            {
                SetDetail(action.Label, preflightReason, "UNAVAILABLE");
                PresentFeedback($"INTERACTION: {action.Label} is not currently valid.");
                return;
            }
            if (!action.Enabled)
            {
                string reason = string.IsNullOrWhiteSpace(action.DisabledReason) ? "Action is not currently available." : action.DisabledReason;
                SetDetail(action.Label, action.Description + "\n\n" + reason, action.IsPlanned ? "PLANNED / NOT WIRED" : "UNAVAILABLE");
                PresentFeedback($"INTERACTION: {action.Label} is not available yet.");
                return;
            }

            ExecuteActionForTarget(action, _currentTarget);
        }

        private void ExecuteActionForTarget(ClientInteractionActionView action, ClientInteractionTarget target)
        {
            if (_executing || action.ActionId == InteractionActionId.None || !target.IsValid)
                return;

            // A choice is complete as soon as the player clicks it (or confirms it with E).
            // Close the menu immediately so cursor/TPS ownership returns without waiting on RTT.
            if (IsMenuVisible)
                HideMenu();

            ExecuteActionAsync(action, target).Forget();
        }

        private async UniTaskVoid ExecuteActionAsync(ClientInteractionActionView action, ClientInteractionTarget target)
        {
            _executing = true;
            SetDetail(action.Label, "Waiting for authoritative GameServer response…", "REQUESTING");
            try
            {
                _manager = ResolveManager();
                if (_manager == null)
                {
                    PresentFeedback("INTERACTION: Game manager is unavailable.");
                    return;
                }

                if (!target.IsValid)
                {
                    PresentFeedback("INTERACTION: Target is no longer available.");
                    return;
                }

                // These are presentation/chat conveniences only. They still come from the
                // same authoritative Player interaction menu and configured Interact action;
                // they do not mutate gameplay authority locally.
                if (target.Kind == InteractionTargetKind.PlayerEntity &&
                    action.ActionId == InteractionActionId.Whisper)
                {
                    PrefillChatCommand($"/w \"{SanitizeCommandTarget(target.DisplayName)}\" ");
                    return;
                }
                if (target.Kind == InteractionTargetKind.PlayerEntity &&
                    action.ActionId == InteractionActionId.ReportPlayer)
                {
                    PrefillChatCommand($"/report \"{SanitizeCommandTarget(target.DisplayName)}\" ");
                    return;
                }

                if (action.ActionId == InteractionActionId.Search)
                    _manager.HintWorldLootSourcePosition(target.WorldPosition);

                InteractionTargetKind targetKind = target.Kind;
                long targetStableId = target.PrimaryId;
                InteractionTargetReferenceWire targetWire = ToWire(target);
                uint sequence = unchecked(++_contextInteractionSequence);
                if (sequence == 0)
                    sequence = unchecked(++_contextInteractionSequence);

                ContextInteractionResponseMessage response = await _manager.RequestContextInteractionAsync(
                    targetWire,
                    action.CategoryId,
                    action.ActionId,
                    sequence);
                await UniTask.SwitchToMainThread();

                string state = response.success ? "SUCCESS" : response.ResultCode.ToString().ToUpperInvariant();
                SetDetail(action.Label, response.detail, state);
                PresentFeedback(response.success
                    ? $"INTERACTION: {action.Label} accepted — {response.detail}"
                    : $"INTERACTION: {action.Label} rejected — {response.ResultCode}: {response.detail}");

                if (!response.success)
                    return;

                if (targetKind == InteractionTargetKind.SceneObject &&
                    action.ActionId == InteractionActionId.Craft && _root != null)
                    _root.OpenCrafting(targetStableId);
            }
            finally
            {
                _executing = false;
            }
        }

        private void RefreshContextualActions(ClientInteractionTarget target)
        {
            if (!target.IsValid)
                return;
            if (!TryValidateClientTargetRange(target, out _))
            {
                if (IsMenuVisible && SameTarget(_currentTarget, target))
                    HideMenu();
                return;
            }

            _authoritativeContextActions.Clear();
            _authoritativeContextTarget = default;
            RebuildForTarget();
        }

        private static bool SameTarget(ClientInteractionTarget left, ClientInteractionTarget right) =>
            left.Kind == right.Kind &&
            left.PrimaryId == right.PrimaryId &&
            left.NetworkObjectId == right.NetworkObjectId &&
            left.Generation == right.Generation;

        private static InteractionTargetReferenceWire ToWire(ClientInteractionTarget target) =>
            new InteractionTargetReferenceWire
            {
                kind = (byte)target.Kind,
                objectId = target.NetworkObjectId,
                generation = target.Generation,
                primaryId = target.PrimaryId,
            };

        private void RespondToConsent(bool accepted)
        {
            uint session = _consentRequest.SessionId;
            HideConsent();
            ConsentDecisionRequested?.Invoke(session, accepted);
            PresentFeedback(accepted
                ? "INTERACTION: Request accepted; awaiting authoritative session result."
                : "INTERACTION: Request declined.");
        }

        private void EndSessionClicked()
        {
            uint session = _activeSessionId;
            EndSessionRequested?.Invoke(session);
            PresentFeedback("INTERACTION: End-session request sent.");
        }

        private void UpdateConsentCountdown()
        {
            if (!IsConsentVisible || _consentCountdownText == null)
                return;
            double expires = _consentRequest.ExpiresAtRealtime;
            if (expires <= 0)
            {
                _consentCountdownText.text = string.Empty;
                return;
            }
            double remaining = expires - (double)Time.realtimeSinceStartup;
            if (remaining <= 0)
            {
                _consentCountdownText.text = "EXPIRED";
                if (_consentAcceptButton != null) _consentAcceptButton.interactable = false;
                return;
            }
            _consentCountdownText.text = $"EXPIRES IN {Math.Ceiling(remaining):0}s";
        }


        private static bool TryCapturePlayer(PlayerEntityNetwork player, out ClientInteractionTarget target)
        {
            target = default;
            if (player == null || !player.IsSpawned || player.IsOwnerClient || player.ObjectId == 0 || player.Generation == 0)
                return false;
            string label = string.IsNullOrWhiteSpace(player.DisplayName) ? $"Player {player.ObjectId}" : player.DisplayName;
            Vector3 worldPosition = player.transform.position;
            if (PlayerEntityClient.TryGetByNetwork(player, out PlayerEntityClient client) && client.PresentationTransform != null)
                worldPosition = client.PresentationTransform.position;
            target = new ClientInteractionTarget(
                InteractionTargetKind.PlayerEntity,
                0,
                player.ObjectId,
                player.Generation,
                label,
                worldPosition,
                player);
            return true;
        }

        private static bool TryCapturePopulation(PlayerEntityClient population, out ClientInteractionTarget target)
        {
            target = default;
            if (population == null || !population.IsPopulationPresentation ||
                population.PopulationActorId <= 0 || population.PopulationGeneration == 0)
                return false;

            Transform presentation = population.PresentationTransform;
            Vector3 worldPosition = presentation != null ? presentation.position : population.transform.position;
            string label = string.IsNullOrWhiteSpace(population.DisplayName) ? "Population" : population.DisplayName;
            target = new ClientInteractionTarget(
                InteractionTargetKind.PopulationEntity,
                population.PopulationActorId,
                0,
                population.PopulationGeneration,
                label,
                worldPosition,
                population);
            return true;
        }


        private bool TryCaptureWorldItem(long itemInstanceId, out ClientInteractionTarget target)
        {
            target = default;
            if (_manager == null)
                _manager = ResolveManager();
            WorldItemWire[] items = _manager != null && _manager.LatestWorldItems.success
                ? (_manager.LatestWorldItems.items ?? Array.Empty<WorldItemWire>())
                : Array.Empty<WorldItemWire>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i].itemInstanceId == itemInstanceId)
                    return TryCaptureWorldItem(items[i], out target);
            return false;
        }

        private static bool TryCaptureWorldItem(WorldItemWire item, out ClientInteractionTarget target)
        {
            target = default;
            if (item.itemInstanceId <= 0)
                return false;
            string label = string.IsNullOrWhiteSpace(item.displayName) ? item.definitionId : item.displayName;
            if (item.quantity > 1) label += $" x{item.quantity}";
            target = new ClientInteractionTarget(
                InteractionTargetKind.NetworkWorldObject,
                item.itemInstanceId,
                0,
                0,
                label,
                new Vector3(item.positionX, item.positionY, item.positionZ),
                null);
            return true;
        }

        private bool TryResolvePrimaryInteractionTarget(out ClientInteractionTarget target)
        {
            target = default;

            _gameplayUi ??= ClientGameplayUIRoot.Instance;
            if (_gameplayUi != null &&
                _gameplayUi.TryResolveReticleActor(out PlayerEntityClient actor, out _) &&
                actor != null)
            {
                if (actor.IsPopulationPresentation)
                    return TryCapturePopulation(actor, out target);

                if (actor.NetworkBridge != null)
                    return TryCapturePlayer(actor.NetworkBridge, out target);
            }

            Camera camera = Camera.main;
            if (camera == null)
                return false;

            return TryResolvePointerTarget(
                new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f),
                out target);
        }

        private bool TryResolvePointerTarget(Vector2 screenPosition, out ClientInteractionTarget target)
        {
            target = default;
            Camera camera = Camera.main;
            if (camera == null)
                return false;

            float maxDistance = Mathf.Min(
                Mathf.Max(1f, pointerRaycastDistance),
                InteractionRangePolicy.ClientPointerRaycastDistance);
            Ray ray = camera.ScreenPointToRay(screenPosition);
            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                maxDistance,
                ~0,
                QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            ClientInteractionTarget physicsTarget = default;
            float physicsTargetDistance = float.PositiveInfinity;

            Transform ownerNetworkRoot = null;
            Transform ownerPresentationRoot = null;
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
            {
                ownerNetworkRoot = owner.transform;
                ownerPresentationRoot = owner.PresentationTransform;
            }

            for (int i = 0; i < hits.Length; ++i)
            {
                Collider hitCollider = hits[i].collider;
                if (hitCollider == null)
                    continue;

                Transform hitTransform = hitCollider.transform;
                if (IsUnderRoot(hitTransform, ownerNetworkRoot) ||
                    IsUnderRoot(hitTransform, ownerPresentationRoot))
                {
                    continue;
                }

                ClientInteractionTargetMarker marker = hitCollider.GetComponentInParent<ClientInteractionTargetMarker>();
                if (marker != null)
                {
                    ClientInteractionTarget captured = marker.CaptureTarget();
                    if (captured.IsValid)
                    {
                        if (captured.Kind == InteractionTargetKind.NetworkWorldObject &&
                            captured.PrimaryId > 0 &&
                            TryCaptureWorldItem(captured.PrimaryId, out ClientInteractionTarget worldItem))
                            captured = worldItem;

                        physicsTarget = captured;
                        physicsTargetDistance = hits[i].distance;
                        break;
                    }
                }

                // Some presentation prefabs do carry colliders. Keep the direct path first,
                // but do not require those colliders for Player/Population interaction.
                if (PlayerEntityClient.TryGetByPresentationTransform(
                        hitCollider.transform,
                        out PlayerEntityClient presented) &&
                    presented != null)
                {
                    if (presented.IsPopulationPresentation)
                    {
                        if (TryCapturePopulation(presented, out physicsTarget))
                        {
                            physicsTargetDistance = hits[i].distance;
                            break;
                        }
                    }
                    else if (presented.NetworkBridge != null &&
                             TryCapturePlayer(presented.NetworkBridge, out physicsTarget))
                    {
                        physicsTargetDistance = hits[i].distance;
                        break;
                    }
                }

                PlayerEntityNetwork player = hitCollider.GetComponentInParent<PlayerEntityNetwork>();
                if (player != null && TryCapturePlayer(player, out physicsTarget))
                {
                    physicsTargetDistance = hits[i].distance;
                    break;
                }
            }

            // Canonical PlayerEntity presentations are visual proxies and are not required to
            // carry physics colliders. Resolve the visible rendered avatar from the existing
            // active presentation registry, then compare it with the nearest physical
            // interactable so one center-reticle resolver still chooses the target.
            if (TryResolvePresentationActorTarget(
                    ray,
                    maxDistance,
                    hits,
                    out ClientInteractionTarget presentationTarget,
                    out float presentationDistance) &&
                (!physicsTarget.IsValid || presentationDistance <= physicsTargetDistance + 0.01f))
            {
                target = presentationTarget;
                return true;
            }

            if (physicsTarget.IsValid)
            {
                target = physicsTarget;
                return true;
            }

            return false;
        }

        private bool TryResolvePresentationActorTarget(
            Ray ray,
            float maxDistance,
            RaycastHit[] physicsHits,
            out ClientInteractionTarget target,
            out float targetDistance)
        {
            target = default;
            targetDistance = float.PositiveInfinity;

            IReadOnlyList<PlayerEntityClient> clients = PlayerEntityClient.ActiveClients;
            Vector3 localPosition = FindLocalPlayerPosition(out bool localFound);

            Transform ownerNetworkRoot = null;
            Transform ownerPresentationRoot = null;
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
            {
                ownerNetworkRoot = owner.transform;
                ownerPresentationRoot = owner.PresentationTransform;
            }
            for (int i = 0; i < clients.Count; ++i)
            {
                PlayerEntityClient client = clients[i];
                if (client == null || client.PresentationTransform == null)
                    continue;

                ClientInteractionTarget captured;
                if (client.IsPopulationPresentation)
                {
                    if (!TryCapturePopulation(client, out captured))
                        continue;
                }
                else
                {
                    if (!client.IsTargetableRemotePlayer ||
                        client.NetworkBridge == null ||
                        !TryCapturePlayer(client.NetworkBridge, out captured))
                        continue;

                    // Public client state is sufficient to suppress an obviously impossible
                    // Player interaction request. GameServer independently revalidates range.
                    if (localFound)
                    {
                        float dx = captured.WorldPosition.x - localPosition.x;
                        float dz = captured.WorldPosition.z - localPosition.z;
                        float range = InteractionRangePolicy.PlayerInteractionRange;
                        if (dx * dx + dz * dz > range * range)
                            continue;
                    }
                }

                if (!TryGetPresentationBounds(client.PresentationTransform, out Bounds bounds) ||
                    !bounds.IntersectRay(ray, out float distance) ||
                    distance < 0f || distance > maxDistance ||
                    distance >= targetDistance ||
                    IsPresentationOccluded(
                        client.PresentationTransform,
                        client.NetworkBridge != null ? client.NetworkBridge.transform : null,
                        ownerNetworkRoot,
                        ownerPresentationRoot,
                        distance,
                        physicsHits))
                {
                    continue;
                }

                target = captured;
                targetDistance = distance;
            }

            return target.IsValid;
        }

        private bool TryGetPresentationBounds(Transform presentationRoot, out Bounds bounds)
        {
            bounds = default;
            if (presentationRoot == null)
                return false;

            _presentationRendererScratch.Clear();
            presentationRoot.GetComponentsInChildren<Renderer>(true, _presentationRendererScratch);

            bool hasBounds = false;
            for (int i = 0; i < _presentationRendererScratch.Count; ++i)
            {
                Renderer renderer = _presentationRendererScratch[i];
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

            _presentationRendererScratch.Clear();
            return hasBounds;
        }

        private static bool IsPresentationOccluded(
            Transform presentationRoot,
            Transform targetNetworkRoot,
            Transform ownerNetworkRoot,
            Transform ownerPresentationRoot,
            float targetDistance,
            RaycastHit[] physicsHits)
        {
            if (presentationRoot == null || physicsHits == null)
                return false;

            for (int i = 0; i < physicsHits.Length; ++i)
            {
                RaycastHit hit = physicsHits[i];
                if (hit.distance + 0.01f >= targetDistance)
                    break;

                Collider collider = hit.collider;
                if (collider == null || collider.isTrigger)
                    continue;

                Transform hitTransform = collider.transform;
                if (IsUnderRoot(hitTransform, presentationRoot) ||
                    IsUnderRoot(hitTransform, targetNetworkRoot) ||
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

        private Vector3 FindLocalPlayerPosition(out bool found)
        {
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner))
            {
                Transform presentation = owner.PresentationTransform;
                found = presentation != null;
                return presentation != null ? presentation.position : Vector3.zero;
            }

            found = false;
            return Vector3.zero;
        }

        private bool TryValidateClientTargetRange(ClientInteractionTarget target, out string reason)
        {
            reason = string.Empty;
            if (!target.IsValid)
            {
                reason = "Target is unavailable.";
                return false;
            }

            Vector3 local = FindLocalPlayerPosition(out bool foundLocal);
            if (!foundLocal)
                return true;

            Vector3 targetPosition = target.WorldPosition;
            if (target.Kind != InteractionTargetKind.PlayerEntity &&
                target.Kind != InteractionTargetKind.PopulationEntity &&
                target.SourceObject is Component component && component != null)
            {
                targetPosition = component.transform.position;
            }

            float range = ClientInteractionRange(target);
            float rangeSq = range * range;
            bool inRange;
            if (target.Kind == InteractionTargetKind.PlayerEntity ||
                target.Kind == InteractionTargetKind.PopulationEntity)
            {
                // Mirrors the GameServer actor/player interaction admission contract.
                float dx = targetPosition.x - local.x;
                float dz = targetPosition.z - local.z;
                inRange = dx * dx + dz * dz <= rangeSq;
            }
            else
            {
                inRange = (targetPosition - local).sqrMagnitude <= rangeSq;
            }

            if (!inRange)
                reason = "Target is out of interaction range.";
            return inRange;
        }

        private void AddClientKnownPublicActions(
            ClientInteractionTarget target,
            List<ClientInteractionActionView> destination)
        {
            // This list mirrors public capabilities owned by the current standalone runtime.
            // It is presentation/preflight only: enum/catalog membership never grants authority.
            switch (target.Kind)
            {
                case InteractionTargetKind.PlayerEntity:
                    AddPublicCatalogAction(destination, InteractionActionId.Inspect);
                    AddPublicCatalogAction(destination, InteractionActionId.Feed);
                    break;

                case InteractionTargetKind.PopulationEntity:
                    // The existing UI deliberately suppresses Population Inspect. Preserve that
                    // behavior. Corpse Loot is derived separately from replicated death/loot flags.
                    if (TryGetPublicPopulationDeathLootAction(target, out ClientInteractionActionView loot))
                        destination.Add(loot);
                    break;

                case InteractionTargetKind.NetworkWorldObject:
                    AddPublicCatalogAction(destination, InteractionActionId.Loot);
                    break;

                case InteractionTargetKind.CombatTestTarget:
                    AddPublicCatalogAction(destination, InteractionActionId.Feed);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyResetHealth);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyClearStatuses);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyNormalDefense);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyConductivePlate);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyPoisonResistant);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyPoisonImmune);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyFireWeak);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyInvulnerable);
                    AddPublicCatalogAction(destination, InteractionActionId.CombatDummyShowStats);
                    break;
            }
        }

        private void AddPublicCatalogAction(
            List<ClientInteractionActionView> destination,
            InteractionActionId actionId)
        {
            if (actionId == InteractionActionId.None || !_addedActions.Add(actionId))
                return;

            InteractionCategoryId categoryId = InteractionCategoryCatalog.DefaultForAction(actionId);
            destination.Add(new ClientInteractionActionView(
                actionId,
                InteractionCategoryCatalog.Label(categoryId),
                ClientInteractionPresentationCatalog.Label(actionId),
                ClientInteractionPresentationCatalog.Description(actionId),
                enabled: true,
                disabledReason: string.Empty,
                ClientInteractionPresentationCatalog.Consent(actionId),
                ClientInteractionPresentationCatalog.Content(actionId),
                (short)Mathf.Clamp(ClientInteractionPresentationCatalog.SortOrder(actionId), short.MinValue, short.MaxValue),
                isPlanned: false,
                categoryId: categoryId));
        }

        private static bool HasLocalPublicInteractionDefinitions(ClientInteractionTarget target) =>
            (target.SourceObject is ClientInteractionTargetMarker marker && marker.HasPublicInteractions) ||
            target.Kind == InteractionTargetKind.PlayerEntity ||
            target.Kind == InteractionTargetKind.PopulationEntity ||
            target.Kind == InteractionTargetKind.NetworkWorldObject ||
            target.Kind == InteractionTargetKind.CombatTestTarget;

        private static float ClientInteractionRange(ClientInteractionTarget target)
        {
            if (target.Kind == InteractionTargetKind.PlayerEntity ||
                target.Kind == InteractionTargetKind.PopulationEntity)
            {
                return InteractionRangePolicy.PlayerInteractionRange;
            }

            if (HasLocalPublicInteractionDefinitions(target) &&
                target.SourceObject is ClientInteractionTargetMarker marker)
            {
                InteractionPublicDefinition[] definitions = marker.PublicInteractions;
                float maximum = 0f;
                for (int i = 0; i < definitions.Length; ++i)
                {
                    InteractionPublicDefinition definition = definitions[i];
                    if (!definition.IsValid)
                        continue;
                    maximum = Mathf.Max(maximum, InteractionRangePolicy.ClampWorldUseRange(definition.maximumUseDistance));
                }
                if (maximum > 0f)
                    return maximum;
            }

            return InteractionRangePolicy.WorldObjectUseRange;
        }

        private bool ValidateCurrentTargetPresentation()
        {
            if (!_currentTarget.IsValid)
                return false;

            if (_currentTarget.Kind == InteractionTargetKind.PlayerEntity)
            {
                PlayerEntityNetwork player = _currentTarget.SourceObject as PlayerEntityNetwork;
                if (player == null || !player.IsSpawned || player.IsOwnerClient ||
                    player.ObjectId != _currentTarget.NetworkObjectId ||
                    player.Generation != _currentTarget.Generation)
                    return false;
                if (TryCapturePlayer(player, out ClientInteractionTarget refreshedPlayer))
                    _currentTarget = refreshedPlayer;
                return true;
            }

            if (_currentTarget.Kind == InteractionTargetKind.NetworkWorldObject && _currentTarget.PrimaryId > 0)
            {
                if (!TryCaptureWorldItem(_currentTarget.PrimaryId, out ClientInteractionTarget refreshedItem))
                    return false;
                _currentTarget = refreshedItem;
                return true;
            }

            if (_currentTarget.Kind == InteractionTargetKind.PopulationEntity)
            {
                if (!PlayerEntityClient.TryGetPopulationPresentation(
                        _currentTarget.PrimaryId,
                        _currentTarget.Generation,
                        out PlayerEntityClient population) ||
                    !TryCapturePopulation(population, out ClientInteractionTarget refreshedPopulation))
                    return false;
                _currentTarget = refreshedPopulation;
                return true;
            }

            if (_currentTarget.SourceObject is Component component)
                return component != null && component.gameObject.activeInHierarchy;

            return true;
        }

        private void UpdateTargetHeader()
        {
            if (_targetNameText != null)
                _targetNameText.text = string.IsNullOrWhiteSpace(_currentTarget.DisplayName)
                    ? TargetKindLabel(_currentTarget.Kind)
                    : _currentTarget.DisplayName;

            if (_targetKindText == null)
                return;

            Vector3 targetPosition = _currentTarget.WorldPosition;
            if (_currentTarget.SourceObject is Component component && component != null)
                targetPosition = component.transform.position;
            Vector3 localPosition = FindLocalPlayerPosition(out bool foundLocal);
            string kind = TargetKindLabel(_currentTarget.Kind).ToUpperInvariant();
            _targetKindText.text = foundLocal
                ? $"{kind}   •   {Vector3.Distance(localPosition, targetPosition):0.0}m"
                : kind;
        }

        private void PositionContextMenu(Vector2 screenPosition)
        {
            if (_menuPanel == null || _uiRoot == null)
                return;

            // RDR2-style interaction presentation is screen-edge anchored instead of
            // orbiting around the target/cursor. Pointer positioning is retained only
            // as an optional authoring/debug preference.
            if (!positionRightClickMenuAtPointer)
            {
                _menuPanel.anchorMin = new Vector2(1f, 0.5f);
                _menuPanel.anchorMax = new Vector2(1f, 0.5f);
                _menuPanel.pivot = new Vector2(1f, 0.5f);
                _menuPanel.sizeDelta = new Vector2(ContextMenuWidth, ContextMenuHeight);
                _menuPanel.anchoredPosition = new Vector2(-32f, 0f);
                return;
            }

            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_uiRoot, screenPosition, null, out local);
            Rect rect = _uiRoot.rect;
            float halfWidth = ContextMenuWidth * 0.5f;
            float halfHeight = ContextMenuHeight * 0.5f;
            local.x = Mathf.Clamp(local.x, rect.xMin + halfWidth, rect.xMax - halfWidth);
            local.y = Mathf.Clamp(local.y, rect.yMin + halfHeight, rect.yMax - halfHeight);
            _menuPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _menuPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _menuPanel.pivot = new Vector2(0.5f, 0.5f);
            _menuPanel.sizeDelta = new Vector2(ContextMenuWidth, ContextMenuHeight);
            _menuPanel.anchoredPosition = local;
        }

        private Vector2 ScreenCenterWithOffset(Vector2 offset) =>
            new Vector2(Screen.width * 0.5f + offset.x, Screen.height * 0.5f + offset.y);

        private void SetDetail(string title, string body, string state)
        {
            if (_detailTitleText != null) _detailTitleText.text = title ?? string.Empty;
            if (_detailBodyText != null) _detailBodyText.text = body ?? string.Empty;
            if (_detailStateText != null) _detailStateText.text = state ?? string.Empty;
        }

        private static string SanitizeCommandTarget(string value) =>
            (value ?? string.Empty).Replace("\"", string.Empty).Trim();

        private static string TargetKindLabel(InteractionTargetKind kind)
        {
            switch (kind)
            {
                case InteractionTargetKind.PlayerEntity: return "Player";
                case InteractionTargetKind.PopulationEntity: return "Population";
                case InteractionTargetKind.SceneObject: return "World Prop";
                case InteractionTargetKind.NetworkWorldObject: return "World Item";
                case InteractionTargetKind.NetworkEntity: return "World Actor";
                case InteractionTargetKind.CombatTestTarget: return "Combat Target";
                default: return "Target";
            }
        }

        private static string ConsentLabel(InteractionConsentMode mode)
        {
            switch (mode)
            {
                case InteractionConsentMode.Confirmation: return "CONFIRMATION";
                case InteractionConsentMode.TargetAcceptance: return "TARGET ACCEPTANCE";
                case InteractionConsentMode.MutualOptIn: return "MUTUAL OPT-IN";
                default: return "NO CONSENT STEP";
            }
        }

        private void UpdateInputCapture()
        {
            bool shouldHoldExclusive = IsBlockingGameplayInput;
            if (shouldHoldExclusive && !_inputCaptureHeld)
            {
                LocalClientInputGate.Acquire();
                _inputCaptureHeld = true;
            }
            else if (!shouldHoldExclusive && _inputCaptureHeld)
            {
                LocalClientInputGate.Release();
                _inputCaptureHeld = false;
            }

            bool shouldHoldPointer = IsMenuVisible || IsConsentVisible;
            if (shouldHoldPointer && !_pointerCaptureHeld)
            {
                LocalClientInputGate.AcquirePointerUi();
                _pointerCaptureHeld = true;
            }
            else if (!shouldHoldPointer && _pointerCaptureHeld)
            {
                LocalClientInputGate.ReleasePointerUi();
                _pointerCaptureHeld = false;
            }
        }

        private void ReleaseInputCapture()
        {
            if (_inputCaptureHeld)
            {
                LocalClientInputGate.Release();
                _inputCaptureHeld = false;
            }

            if (_pointerCaptureHeld)
            {
                LocalClientInputGate.ReleasePointerUi();
                _pointerCaptureHeld = false;
            }
        }

        private void BindAuthoredPresentation()
        {
            _built = _uiRoot != null && _menuLayer != null && _menuPanel != null &&
                     _consentLayer != null && _sessionStrip != null && _slots != null && _slots.Count > 0;

            if (_dismissButton != null)
            {
                _dismissButton.onClick.RemoveListener(HideMenu);
                _dismissButton.onClick.AddListener(HideMenu);
            }
            if (_centerButton != null)
            {
                _centerButton.onClick.RemoveListener(CenterClicked);
                _centerButton.onClick.AddListener(CenterClicked);
            }
            if (_consentAcceptButton != null)
            {
                _consentAcceptButton.onClick.RemoveAllListeners();
                _consentAcceptButton.onClick.AddListener(() => RespondToConsent(true));
            }
            if (_consentDeclineButton != null)
            {
                _consentDeclineButton.onClick.RemoveAllListeners();
                _consentDeclineButton.onClick.AddListener(() => RespondToConsent(false));
            }
            if (_consentCloseButton != null)
            {
                _consentCloseButton.onClick.RemoveAllListeners();
                _consentCloseButton.onClick.AddListener(() => RespondToConsent(false));
            }
            if (_sessionEndButton != null)
            {
                _sessionEndButton.onClick.RemoveListener(EndSessionClicked);
                _sessionEndButton.onClick.AddListener(EndSessionClicked);
            }

            if (_slots != null)
            {
                for (int i = 0; i < _slots.Count; ++i)
                {
                    SlotView slot = _slots[i];
                    if (slot == null)
                        continue;
                    slot.Entry = null;
                    if (slot.Hover != null)
                        slot.Hover.Bind(OnSlotHover, i);
                }
            }
        }

        private void ValidateAuthoredPresentation()
        {
            if (_built)
                return;

            Debug.LogError(
                "[InteractionUI] Authored interaction presentation is incomplete on ClientUIRoot. " +
                "Repair the authored interaction references on ClientUIRoot.prefab directly. " +
                "The old one-time migration menu has been retired; runtime does not create or path-bind interaction visuals.",
                this);
        }

#if UNITY_EDITOR
        private void EnsurePresentationForEditor()
        {
#if UNITY_SERVER
            return;
#else
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // Once serialized references exist, names/hierarchy are no longer authoritative.
            // This lets artists freely rename/re-parent the authored UI without migration
            // recreating default objects on top of their work.
            if (_uiRoot != null && _menuLayer != null && _consentLayer != null &&
                _sessionStrip != null && _slots != null && _slots.Count > 0)
            {
                _built = true;
                return;
            }

            if (_uiRoot == null)
            {
                Transform existing = transform.Find("InteractionUIRoot");
                if (existing != null)
                    _uiRoot = existing as RectTransform;
            }

            if (_uiRoot == null)
            {
                GameObject rootObject = NewUi("InteractionUIRoot", transform);
                _uiRoot = rootObject.GetComponent<RectTransform>();
                Stretch(_uiRoot);
                _uiRoot.SetAsLastSibling();
            }

            Transform legacyRadial = _uiRoot.Find("RadialLayer");
            if (legacyRadial != null)
                legacyRadial.gameObject.SetActive(false);

            if (_menuLayer == null)
            {
                Transform existingMenu = _uiRoot.Find("ContextMenuLayer");
                if (existingMenu != null)
                    WireExistingPresentation();
                else
                    BuildContextMenu();
            }

            if (_consentLayer == null)
            {
                Transform existingConsent = _uiRoot.Find("ConsentLayer");
                if (existingConsent != null)
                    WireExistingPresentation();
                else
                    BuildConsent();
            }

            if (_sessionStrip == null)
            {
                Transform existingSession = _uiRoot.Find("SessionStrip");
                if (existingSession != null)
                    WireExistingPresentation();
                else
                    BuildSessionStrip();
            }

            if (_slots == null || _slots.Count == 0 || _targetNameText == null || _targetKindText == null)
                WireExistingPresentation();

            _built = _uiRoot != null && _menuLayer != null && _consentLayer != null &&
                     _sessionStrip != null && _slots != null && _slots.Count > 0;
#endif
        }

        private void WireExistingPresentation()
        {
            _menuLayer = Find("ContextMenuLayer")?.gameObject;
            _menuPanel = Find("ContextMenuLayer/ContextPanel") as RectTransform;
            _menuCanvasGroup = _menuPanel != null ? _menuPanel.GetComponent<CanvasGroup>() : null;
            _dismissButton = FindButton("ContextMenuLayer/DismissBlocker");
            _targetNameText = FindText("ContextMenuLayer/ContextPanel/Header/TargetName");
            _targetKindText = FindText("ContextMenuLayer/ContextPanel/Header/TargetKind");
            _pageText = FindText("ContextMenuLayer/ContextPanel/PageText");
            _centerButton = FindButton("ContextMenuLayer/ContextPanel/BackButton");
            _centerButtonText = _centerButton != null ? _centerButton.GetComponentInChildren<Text>(true) : null;
            if (_centerButton != null)
                _centerButton.onClick.RemoveAllListeners();
            _detailTitleText = FindText("ContextMenuLayer/ContextPanel/DetailPanel/Title");
            _detailBodyText = FindText("ContextMenuLayer/ContextPanel/DetailPanel/Body");
            _detailStateText = FindText("ContextMenuLayer/ContextPanel/DetailPanel/State");

            _slots.Clear();
            for (int i = 0; i < SlotCount; ++i)
            {
                Transform slotTransform = Find($"ContextMenuLayer/ContextPanel/ActionRows/Row{i}");
                if (slotTransform == null) continue;
                Button button = slotTransform.GetComponent<Button>();
                Image image = slotTransform.GetComponent<Image>();
                Text label = slotTransform.GetComponentInChildren<Text>(true);
                ClientInteractionActionButtonHover hover = slotTransform.GetComponent<ClientInteractionActionButtonHover>();
                if (hover == null) hover = slotTransform.gameObject.AddComponent<ClientInteractionActionButtonHover>();
                var slot = new SlotView { Root = slotTransform.gameObject, Button = button, Image = image, Label = label, Hover = hover };
                _slots.Add(slot);
                hover.Bind(OnSlotHover, _slots.Count - 1);
            }

            _consentLayer = Find("ConsentLayer")?.gameObject;
            _consentTitleText = FindText("ConsentLayer/Panel/Title");
            _consentMessageText = FindText("ConsentLayer/Panel/Message");
            _consentCountdownText = FindText("ConsentLayer/Panel/Countdown");
            _consentAcceptButton = FindButton("ConsentLayer/Panel/AcceptButton");
            _consentDeclineButton = FindButton("ConsentLayer/Panel/DeclineButton");
            _consentCloseButton = FindButton("ConsentLayer/Panel/CloseButton");
            WireConsentButtons();

            _sessionStrip = Find("SessionStrip")?.gameObject;
            _sessionText = FindText("SessionStrip/SessionText");
            _sessionEndButton = FindButton("SessionStrip/EndButton");
            if (_sessionEndButton != null)
                _sessionEndButton.onClick.RemoveAllListeners();
        }

        private void BuildContextMenu()
        {
            _menuLayer = NewUi("ContextMenuLayer", _uiRoot);
            Stretch(_menuLayer.GetComponent<RectTransform>());

            // Transparent click-away surface. The RDR2-style list does not dim the
            // world; the player should retain visual context while choosing an action.
            GameObject blocker = NewImage("DismissBlocker", _menuLayer.transform, new Color(0f, 0f, 0f, 0f));
            Stretch(blocker.GetComponent<RectTransform>());
            _dismissButton = blocker.AddComponent<Button>();
            _dismissButton.transition = Selectable.Transition.None;

            GameObject panel = NewImage("ContextPanel", _menuLayer.transform, new Color(0.025f, 0.025f, 0.025f, 0.90f));
            _menuPanel = panel.GetComponent<RectTransform>();
            _menuPanel.anchorMin = new Vector2(1f, 0.5f);
            _menuPanel.anchorMax = new Vector2(1f, 0.5f);
            _menuPanel.pivot = new Vector2(1f, 0.5f);
            _menuPanel.sizeDelta = new Vector2(ContextMenuWidth, ContextMenuHeight);
            _menuPanel.anchoredPosition = new Vector2(-32f, 0f);
            _menuCanvasGroup = panel.AddComponent<CanvasGroup>();

            GameObject header = NewImage("Header", panel.transform, new Color(0f, 0f, 0f, 0.34f));
            SetRect(header.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(354f, 72f), new Vector2(0f, -42f));
            _targetNameText = NewText("TargetName", header.transform, _font, "TARGET", 20, TextAnchor.MiddleLeft);
            RectTransform targetNameRect = _targetNameText.rectTransform;
            targetNameRect.anchorMin = new Vector2(0f, 0.42f); targetNameRect.anchorMax = new Vector2(1f, 1f);
            targetNameRect.offsetMin = new Vector2(14f, 0f); targetNameRect.offsetMax = new Vector2(-14f, -2f);
            _targetNameText.fontStyle = FontStyle.Bold;
            _targetKindText = NewText("TargetKind", header.transform, _font, "TARGET", 11, TextAnchor.MiddleLeft);
            RectTransform kindRect = _targetKindText.rectTransform;
            kindRect.anchorMin = new Vector2(0f, 0f); kindRect.anchorMax = new Vector2(1f, 0.42f);
            kindRect.offsetMin = new Vector2(14f, 2f); kindRect.offsetMax = new Vector2(-14f, 0f);

            GameObject rowsRoot = NewUi("ActionRows", panel.transform);
            RectTransform rowsRect = rowsRoot.GetComponent<RectTransform>();
            rowsRect.anchorMin = new Vector2(0.5f, 1f);
            rowsRect.anchorMax = new Vector2(0.5f, 1f);
            rowsRect.pivot = new Vector2(0.5f, 1f);
            rowsRect.sizeDelta = new Vector2(354f, 318f);
            rowsRect.anchoredPosition = new Vector2(0f, -88f);

            _slots.Clear();
            for (int i = 0; i < SlotCount; ++i)
            {
                GameObject rowObject = NewImage($"Row{i}", rowsRoot.transform, new Color(0.055f, 0.055f, 0.055f, 0.96f));
                RectTransform rowRect = rowObject.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(354f, 40f);
                rowRect.anchoredPosition = new Vector2(0f, -i * 44f);

                Button button = rowObject.AddComponent<Button>();
                button.targetGraphic = rowObject.GetComponent<Image>();
                Text label = NewText("Label", rowObject.transform, _font, string.Empty, 13, TextAnchor.MiddleLeft);
                Stretch(label.rectTransform, 14f, 4f, -12f, -4f);
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 10;
                label.resizeTextMaxSize = 13;
                var hover = rowObject.AddComponent<ClientInteractionActionButtonHover>();
                var slot = new SlotView { Root = rowObject, Button = button, Image = rowObject.GetComponent<Image>(), Label = label, Hover = hover };
                _slots.Add(slot);
                hover.Bind(OnSlotHover, i);
                rowObject.SetActive(false);
            }

            _pageText = NewText("PageText", panel.transform, _font, string.Empty, 10, TextAnchor.MiddleRight);
            SetRect(_pageText.rectTransform, new Vector2(0.5f, 1f), new Vector2(354f, 20f), new Vector2(0f, -406f));

            GameObject detail = NewImage("DetailPanel", panel.transform, new Color(0f, 0f, 0f, 0.28f));
            SetRect(detail.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(354f, 148f), new Vector2(0f, 104f));
            _detailTitleText = NewText("Title", detail.transform, _font, "INTERACTIONS", 15, TextAnchor.UpperLeft);
            SetRect(_detailTitleText.rectTransform, new Vector2(0.5f, 1f), new Vector2(326f, 24f), new Vector2(0f, -16f));
            _detailTitleText.fontStyle = FontStyle.Bold;
            _detailStateText = NewText("State", detail.transform, _font, string.Empty, 10, TextAnchor.UpperLeft);
            SetRect(_detailStateText.rectTransform, new Vector2(0.5f, 1f), new Vector2(326f, 20f), new Vector2(0f, -42f));
            _detailBodyText = NewText("Body", detail.transform, _font, string.Empty, 11, TextAnchor.UpperLeft);
            RectTransform bodyRect = _detailBodyText.rectTransform;
            bodyRect.anchorMin = new Vector2(0f, 0f); bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.offsetMin = new Vector2(14f, 10f); bodyRect.offsetMax = new Vector2(-14f, -64f);
            _detailBodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _detailBodyText.verticalOverflow = VerticalWrapMode.Truncate;

            _centerButton = NewButton("BackButton", panel.transform, _font, "CLOSE", new Vector2(0f, 0f), new Vector2(72f, 22f), new Vector2(120f, 34f));
            _centerButtonText = _centerButton.GetComponentInChildren<Text>(true);

            Text hint = NewText("InputHint", panel.transform, _font, "Q / ESC  CLOSE", 10, TextAnchor.MiddleRight);
            SetRect(hint.rectTransform, new Vector2(1f, 0f), new Vector2(180f, 28f), new Vector2(-104f, 22f));

            _menuLayer.SetActive(false);
        }

        private void BuildConsent()
        {
            _consentLayer = NewUi("ConsentLayer", _uiRoot);
            Stretch(_consentLayer.GetComponent<RectTransform>());

            GameObject dim = NewImage("Dim", _consentLayer.transform, new Color(0f, 0f, 0f, 0.55f));
            Stretch(dim.GetComponent<RectTransform>());

            GameObject panel = NewImage("Panel", _consentLayer.transform, new Color(0.04f, 0.05f, 0.075f, 0.99f));
            SetRect(panel.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(560f, 290f), Vector2.zero);

            _consentTitleText = NewText("Title", panel.transform, _font, "INTERACTION REQUEST", 22, TextAnchor.MiddleCenter);
            SetRect(_consentTitleText.rectTransform, new Vector2(0.5f, 1f), new Vector2(520f, 48f), new Vector2(0f, -32f));
            _consentTitleText.fontStyle = FontStyle.Bold;
            _consentMessageText = NewText("Message", panel.transform, _font, string.Empty, 15, TextAnchor.MiddleCenter);
            SetRect(_consentMessageText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(490f, 110f), new Vector2(0f, 18f));
            _consentMessageText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _consentCountdownText = NewText("Countdown", panel.transform, _font, string.Empty, 12, TextAnchor.MiddleCenter);
            SetRect(_consentCountdownText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(300f, 24f), new Vector2(0f, -55f));

            _consentAcceptButton = NewButton("AcceptButton", panel.transform, _font, "ACCEPT", new Vector2(0.5f, 0f), new Vector2(-120f, 38f), new Vector2(190f, 48f));
            _consentDeclineButton = NewButton("DeclineButton", panel.transform, _font, "DECLINE", new Vector2(0.5f, 0f), new Vector2(120f, 38f), new Vector2(190f, 48f));
            _consentCloseButton = NewButton("CloseButton", panel.transform, _font, "CLOSE", new Vector2(1f, 1f), new Vector2(-42f, -26f), new Vector2(72f, 34f));
            WireConsentButtons();
            _consentLayer.SetActive(false);
        }

        private void WireConsentButtons()
        {
            if (_consentAcceptButton != null)
            {
                _consentAcceptButton.onClick.RemoveAllListeners();
                _consentAcceptButton.onClick.AddListener(() => RespondToConsent(true));
            }
            if (_consentDeclineButton != null)
            {
                _consentDeclineButton.onClick.RemoveAllListeners();
                _consentDeclineButton.onClick.AddListener(() => RespondToConsent(false));
            }
            if (_consentCloseButton != null)
            {
                _consentCloseButton.onClick.RemoveAllListeners();
                _consentCloseButton.onClick.AddListener(() => RespondToConsent(false));
            }
        }

        private void BuildSessionStrip()
        {
            _sessionStrip = NewImage("SessionStrip", _uiRoot, new Color(0.045f, 0.055f, 0.075f, 0.96f));
            SetRect(_sessionStrip.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(650f, 52f), new Vector2(0f, -78f));
            _sessionText = NewText("SessionText", _sessionStrip.transform, _font, "ACTIVE INTERACTION", 13, TextAnchor.MiddleLeft);
            RectTransform textRect = _sessionText.rectTransform;
            textRect.anchorMin = new Vector2(0f, 0f); textRect.anchorMax = new Vector2(1f, 1f); textRect.offsetMin = new Vector2(16f, 4f); textRect.offsetMax = new Vector2(-130f, -4f);
            _sessionEndButton = NewButton("EndButton", _sessionStrip.transform, _font, "END", new Vector2(1f, 0.5f), new Vector2(-66f, 0f), new Vector2(110f, 36f));
            _sessionStrip.SetActive(false);
        }

        private Transform Find(string path) => _uiRoot != null ? _uiRoot.Find(path) : null;
        private Text FindText(string path) => Find(path)?.GetComponent<Text>();
        private Button FindButton(string path) => Find(path)?.GetComponent<Button>();

        private static GameObject NewUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
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

        private static Text NewText(string name, Transform parent, Font font, string value, int size, TextAnchor anchor)
        {
            GameObject go = NewUi(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.text = value ?? string.Empty;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static Button NewButton(string name, Transform parent, Font font, string label, Vector2 anchor, Vector2 position, Vector2 size)
        {
            GameObject go = NewImage(name, parent, new Color(0.10f, 0.13f, 0.18f, 0.98f));
            SetRect(go.GetComponent<RectTransform>(), anchor, size, position);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.75f);
            button.colors = colors;
            Text text = NewText("Label", go.transform, font, label, 12, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 5f, 3f, -5f, -3f);
            return button;
        }

        private static void SetRect(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }
#endif
    }
}
