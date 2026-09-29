using Game.Client.UI.Gameplay;
using Game.Client.UI.PlayerItems;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Lifecycle/window owner for the authored StandaloneClientUI prefab.
    /// Frontend/authentication remains owned by the existing CharacterSelectShell.
    /// No UI hierarchy is constructed at runtime.
    /// </summary>
    public sealed class StandaloneClientUIRoot : MonoBehaviour
    {
        public static StandaloneClientUIRoot Instance { get; private set; }

        [Header("Authored Roots")]
        [SerializeField] private GameObject gameplayRoot;

        [Header("Gameplay Windows")]
        [SerializeField] private StandaloneCharacterInventoryWindow characterInventoryWindow;
        [SerializeField] private StandaloneSkillsWindow skillsWindow;
        [SerializeField] private GameObject mapWindowRoot;
        [SerializeField] private GameObject socialWindowRoot;
        [SerializeField] private GameObject emotesWindowRoot;
        [SerializeField] private StandaloneControlsWindow controlsWindow;
        [SerializeField] private StandaloneTradeUI tradeWindow;
        [SerializeField] private PlayerLootShell lootWindow;
        [SerializeField] private StandaloneSocialEconomyUI socialEconomyUi;
        [SerializeField] private StandaloneHudTooltipPanel tooltip;

        private PlayerEntityGameManager _manager;
        private bool _pointerCaptureHeld;
        private bool _gameplaySessionActive;

        public bool GameplaySessionActive => _gameplaySessionActive;
        public bool CharacterInventoryOpen => characterInventoryWindow != null && characterInventoryWindow.IsOpen;
        public bool SkillsOpen => skillsWindow != null && skillsWindow.IsOpen;
        public bool MapOpen => mapWindowRoot != null && mapWindowRoot.activeSelf;
        public bool SocialOpen => socialWindowRoot != null && socialWindowRoot.activeSelf;
        public bool EmotesOpen => emotesWindowRoot != null && emotesWindowRoot.activeSelf;
        public bool ControlsOpen => controlsWindow != null && controlsWindow.IsOpen;
        public bool TradeOpen => tradeWindow != null && tradeWindow.IsOpen;
        public bool LootOpen => lootWindow != null && lootWindow.IsOpen;
        public bool StorageOpen => socialEconomyUi != null && socialEconomyUi.StorageOpen;
        public bool SocialInvitePopupOpen => socialEconomyUi != null && socialEconomyUi.InvitePopupOpen;
        // Trade is modal and deliberately excluded from AnyTransientWindowOpen so movement-dismiss
        // logic cannot hide the presentation while the authoritative trade session remains active.
        public bool AnyTransientWindowOpen => CharacterInventoryOpen || SkillsOpen || MapOpen || SocialOpen || EmotesOpen || ControlsOpen || StorageOpen || SocialInvitePopupOpen || LootOpen;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            if (Instance != null && Instance != this)
            {
                Debug.LogError("[StandaloneUI] More than one StandaloneClientUIRoot is active.", this);
                enabled = false;
                return;
            }
            Instance = this;
            if (lootWindow != null)
                lootWindow.BindStandaloneRoot(this, lootWindow.gameObject);
            SetGameplayVisible(false);
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindManager();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            UnbindManager();
            ReleasePointerCapture();
            if (Instance == this)
                Instance = null;
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || gameplayRoot == null || !gameplayRoot.activeInHierarchy)
                return;

            // Gameplay V2.04 is the canonical post-login input router when present.
            if (ClientGameplayUIRoot.Instance != null && ClientGameplayUIRoot.Instance.HandlesGameplayInput)
                return;

            if (IsTextEntryFocused())
                return;

            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenInventory))
            {
                ToggleCharacterInventory();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenSkills))
            {
                ToggleSkills();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenMap))
            {
                ToggleMap();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenSocial))
            {
                ToggleSocial();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenEmotes))
            {
                ToggleEmotes();
                return;
            }
            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenMenu))
            {
                if (CloseTopmostTransientWindow())
                    EventSystem.current?.SetSelectedGameObject(null);
            }
#endif
        }

        private void LateUpdate()
        {
#if !UNITY_SERVER
            bool shouldHold = _gameplaySessionActive && (AnyTransientWindowOpen || TradeOpen);
            if (shouldHold && !_pointerCaptureHeld)
                AcquirePointerCapture();
            else if (!shouldHold && _pointerCaptureHeld)
                ReleasePointerCapture();
#endif
        }

        private void BindManager()
        {
            if (_manager == null)
                _manager = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == null)
            {
                Debug.LogError("[StandaloneUI] PlayerEntityGameManager was not found. Frontend login can render, but gameplay visibility cannot bind.", this);
                return;
            }

            _manager.ClientWorldEntered -= OnClientWorldEntered;
            _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            _manager.ClientTransportStopped -= OnClientTransportStopped;
            _manager.TradeStateReceived -= OnTradeStateReceived;
            _manager.WorldLootSnapshotReceived -= OnWorldLootSnapshotReceived;
            _manager.ClientWorldEntered += OnClientWorldEntered;
            _manager.ClientConnectionClosed += OnClientConnectionClosed;
            _manager.ClientTransportStopped += OnClientTransportStopped;

            // State-driven authored windows cannot rely on Start/Awake inside their own
            // inactive window hierarchy to bootstrap visibility. The always-active canonical
            // UI root owns that first presentation handoff from already-received owner state.
            _manager.TradeStateReceived -= OnTradeStateReceived;
            _manager.TradeStateReceived += OnTradeStateReceived;
            _manager.WorldLootSnapshotReceived -= OnWorldLootSnapshotReceived;
            _manager.WorldLootSnapshotReceived += OnWorldLootSnapshotReceived;
            lootWindow?.Bind(_manager);

            if (_manager.LatestPlayerResources.success)
                SetGameplayVisible(true);

            ApplyCachedStateDrivenWindows();
        }

        private void UnbindManager()
        {
            if (_manager == null)
                return;

            _manager.ClientWorldEntered -= OnClientWorldEntered;
            _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            _manager.ClientTransportStopped -= OnClientTransportStopped;
            _manager.TradeStateReceived -= OnTradeStateReceived;
            lootWindow?.Bind(null);
        }

        private void OnClientWorldEntered(long _)
        {
            SetGameplayVisible(true);
            ApplyCachedStateDrivenWindows();
        }

        private void OnClientConnectionClosed(ClientDisconnectNotice _) => SetGameplayVisible(false);
        private void OnClientTransportStopped() => SetGameplayVisible(false);

        private void OnTradeStateReceived(TradeStateMessage state)
        {
            if (!_gameplaySessionActive || tradeWindow == null)
                return;

            tradeWindow.ApplyAuthoritativeState(state);
        }

        private void OnWorldLootSnapshotReceived(WorldLootResponseMessage snapshot)
        {
            if (!_gameplaySessionActive || lootWindow == null || !snapshot.success)
                return;

            // Same canonical ownership rule as Trade: the always-active UI root receives
            // authoritative state and activates the inactive authored presenter.
            lootWindow.Open(snapshot);
        }

        private void ApplyCachedStateDrivenWindows()
        {
            if (!_gameplaySessionActive || _manager == null)
                return;

            if (tradeWindow != null)
                tradeWindow.ApplyAuthoritativeState(_manager.LatestTrade);

        }

        private void SetGameplayVisible(bool visible)
        {
            _gameplaySessionActive = visible;
            if (gameplayRoot != null && gameplayRoot.activeSelf != visible)
                gameplayRoot.SetActive(visible);

            if (visible)
                return;

            CloseTransientGameplayWindows();
            socialEconomyUi?.ResetPresentation();
            tooltip?.Hide();
            ReleasePointerCapture();
        }

        public void ToggleCharacterInventory()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || characterInventoryWindow == null)
                return;
            if (characterInventoryWindow.IsOpen) CloseCharacterInventory(); else OpenCharacterInventory();
#endif
        }

        public void OpenCharacterInventory()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || characterInventoryWindow == null || TradeOpen)
                return;
            CloseTransientExcept(characterInventoryWindow.gameObject);
            characterInventoryWindow.Open();
            AcquirePointerCapture();
#endif
        }

        public void CloseCharacterInventory()
        {
#if !UNITY_SERVER
            characterInventoryWindow?.Close();
            tooltip?.Hide();
#endif
        }

        public void ToggleSkills()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || skillsWindow == null)
                return;
            if (skillsWindow.IsOpen) skillsWindow.Close(); else OpenSkills();
#endif
        }

        public void OpenSkills()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || skillsWindow == null || TradeOpen)
                return;
            CloseTransientExcept(skillsWindow.gameObject);
            skillsWindow.Open();
            AcquirePointerCapture();
#endif
        }

        public void CloseSkills() => skillsWindow?.Close();

        public void ToggleMap() => ToggleSimple(mapWindowRoot);
        public void OpenMap() => OpenSimple(mapWindowRoot);
        public void CloseMap() => CloseSimple(mapWindowRoot);
        public void ToggleSocial()
        {
#if !UNITY_SERVER
            if (SocialOpen) CloseSocial(); else OpenSocial();
#endif
        }

        public void OpenSocial()
        {
#if !UNITY_SERVER
            OpenSimple(socialWindowRoot);
            if (SocialOpen)
                socialEconomyUi?.SetSocialVisible(true);
#endif
        }

        public void CloseSocial()
        {
#if !UNITY_SERVER
            CloseSimple(socialWindowRoot);
            socialEconomyUi?.SetSocialVisible(false);
#endif
        }
        public void ToggleEmotes() => ToggleSimple(emotesWindowRoot);
        public void OpenEmotes() => OpenSimple(emotesWindowRoot);
        public void CloseEmotes() => CloseSimple(emotesWindowRoot);

        public void OpenControls()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || controlsWindow == null || TradeOpen)
                return;
            CloseTransientExcept(controlsWindow.gameObject);
            controlsWindow.Open();
            AcquirePointerCapture();
#endif
        }

        public void CloseControls() => controlsWindow?.Close();

        /// <summary>
        /// Opens the already-authored Storage window after the canonical interaction response has
        /// granted access. The Storage presenter owns hydration and authoritative transfer actions.
        /// </summary>
        public bool PrepareForStorageWindow(GameObject storageRoot)
        {
#if UNITY_SERVER
            return false;
#else
            if (!_gameplaySessionActive || storageRoot == null || TradeOpen)
                return false;
            CloseTransientExcept(storageRoot);
            storageRoot.SetActive(true);
            AcquirePointerCapture();
            return true;
#endif
        }

        /// <summary>Called by the authored Trade presenter when an authoritative trade becomes visible.</summary>
        public void PrepareForTradeModal()
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive)
                return;
            CloseTransientGameplayWindows();
            AcquirePointerCapture();
#endif
        }

        public void PrepareForLootWindow(GameObject lootRoot)
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || lootRoot == null)
                return;
            CloseTransientExcept(lootRoot);
            AcquirePointerCapture();
#endif
        }

        public bool CloseTopmostTransientWindow()
        {
#if UNITY_SERVER
            return false;
#else
            if (LootOpen) { lootWindow.Close(); return true; }
            if (ControlsOpen) { controlsWindow.Close(); return true; }
            if (StorageOpen) { socialEconomyUi.CloseStorageLocal(); return true; }
            if (EmotesOpen) { CloseEmotes(); return true; }
            if (SocialOpen) { CloseSocial(); return true; }
            if (MapOpen) { CloseMap(); return true; }
            if (SkillsOpen) { skillsWindow.Close(); return true; }
            if (CharacterInventoryOpen) { CloseCharacterInventory(); return true; }
            return false;
#endif
        }

        public void CloseTransientGameplayWindows()
        {
#if !UNITY_SERVER
            characterInventoryWindow?.Close();
            skillsWindow?.Close();
            CloseSimple(mapWindowRoot);
            CloseSimple(socialWindowRoot);
            socialEconomyUi?.SetSocialVisible(false);
            socialEconomyUi?.CloseStorageLocal();
            CloseSimple(emotesWindowRoot);
            controlsWindow?.Close();
            lootWindow?.Close();
            tooltip?.Hide();
#endif
        }

        private void ToggleSimple(GameObject root)
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || root == null)
                return;
            if (root.activeSelf) CloseSimple(root); else OpenSimple(root);
#endif
        }

        private void OpenSimple(GameObject root)
        {
#if !UNITY_SERVER
            if (!_gameplaySessionActive || root == null || TradeOpen)
                return;
            CloseTransientExcept(root);
            root.SetActive(true);
            AcquirePointerCapture();
#endif
        }

        private static void CloseSimple(GameObject root)
        {
#if !UNITY_SERVER
            if (root != null)
                root.SetActive(false);
#endif
        }

        private void CloseTransientExcept(GameObject keep)
        {
            if (characterInventoryWindow != null && characterInventoryWindow.gameObject != keep)
                characterInventoryWindow.Close();
            if (skillsWindow != null && skillsWindow.gameObject != keep)
                skillsWindow.Close();
            if (mapWindowRoot != null && mapWindowRoot != keep)
                mapWindowRoot.SetActive(false);
            if (socialWindowRoot != null && socialWindowRoot != keep)
            {
                socialWindowRoot.SetActive(false);
                socialEconomyUi?.SetSocialVisible(false);
            }
            if (socialEconomyUi != null && socialEconomyUi.StorageWindowRoot != null && socialEconomyUi.StorageWindowRoot != keep)
                socialEconomyUi.CloseStorageLocal();
            if (emotesWindowRoot != null && emotesWindowRoot != keep)
                emotesWindowRoot.SetActive(false);
            if (controlsWindow != null && controlsWindow.gameObject != keep)
                controlsWindow.Close();
            if (lootWindow != null && lootWindow.gameObject != keep && lootWindow.IsOpen)
                lootWindow.Close();
            tooltip?.Hide();
        }

        private void AcquirePointerCapture()
        {
            if (_pointerCaptureHeld)
                return;
            _pointerCaptureHeld = true;
            LocalClientInputGate.AcquirePointerUi();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void ReleasePointerCapture()
        {
            if (!_pointerCaptureHeld)
                return;
            _pointerCaptureHeld = false;
            LocalClientInputGate.ReleasePointerUi();
        }

        private static bool IsTextEntryFocused()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponentInParent<InputField>() != null;
        }

#if UNITY_EDITOR
        // Backward-compatible overloads retained so previous authoring utilities still compile.
        public void ConfigureForEditor(
            StandaloneCharacterInventoryWindow window,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            ConfigureForEditor(null, window, authoredTooltip);
        }

        public void ConfigureForEditor(
            GameObject authoredGameplayRoot,
            StandaloneCharacterInventoryWindow window,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            ConfigureForEditor(
                authoredGameplayRoot,
                window,
                null,
                null,
                null,
                null,
                null,
                authoredTooltip);
        }

        public void ConfigureForEditor(
            GameObject authoredGameplayRoot,
            StandaloneCharacterInventoryWindow window,
            StandaloneSkillsWindow authoredSkills,
            GameObject authoredMap,
            GameObject authoredSocial,
            GameObject authoredEmotes,
            StandaloneControlsWindow authoredControls,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            ConfigureForEditor(
                authoredGameplayRoot, window, authoredSkills, authoredMap, authoredSocial, authoredEmotes,
                authoredControls, null, authoredTooltip);
        }

        public void ConfigureForEditor(
            GameObject authoredGameplayRoot,
            StandaloneCharacterInventoryWindow window,
            StandaloneSkillsWindow authoredSkills,
            GameObject authoredMap,
            GameObject authoredSocial,
            GameObject authoredEmotes,
            StandaloneControlsWindow authoredControls,
            StandaloneTradeUI authoredTrade,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            ConfigureForEditor(
                authoredGameplayRoot, window, authoredSkills, authoredMap, authoredSocial, authoredEmotes,
                authoredControls, authoredTrade, null, authoredTooltip);
        }

        public void ConfigureForEditor(
            GameObject authoredGameplayRoot,
            StandaloneCharacterInventoryWindow window,
            StandaloneSkillsWindow authoredSkills,
            GameObject authoredMap,
            GameObject authoredSocial,
            GameObject authoredEmotes,
            StandaloneControlsWindow authoredControls,
            StandaloneTradeUI authoredTrade,
            StandaloneSocialEconomyUI authoredSocialEconomyUi,
            StandaloneHudTooltipPanel authoredTooltip,
            PlayerLootShell authoredLoot = null)
        {
            gameplayRoot = authoredGameplayRoot;
            characterInventoryWindow = window;
            skillsWindow = authoredSkills;
            mapWindowRoot = authoredMap;
            socialWindowRoot = authoredSocial;
            emotesWindowRoot = authoredEmotes;
            controlsWindow = authoredControls;
            tradeWindow = authoredTrade;
            socialEconomyUi = authoredSocialEconomyUi;
            tooltip = authoredTooltip;
            lootWindow = authoredLoot;
            if (lootWindow != null)
                lootWindow.BindStandaloneRoot(this, lootWindow.gameObject);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
