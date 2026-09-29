using System;
using System.Collections.Generic;
using Game.Client.Content;
using Game.Shared.Content;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Authored Skills window with two data-driven views:
    /// - Progression tracks from the existing client content catalog + LatestProgression cache.
    /// - Learned abilities from the existing Gameplay Settings catalog + progression learned IDs.
    ///
    /// Opening the window never fetches state merely because it is being viewed.
    /// </summary>
    public sealed class StandaloneSkillsWindow : MonoBehaviour
    {
        [Header("Window")]
        [SerializeField] private GameObject windowRoot;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button progressionTabButton;
        [SerializeField] private Button abilitiesTabButton;
        [SerializeField] private Text levelSummaryText;
        [SerializeField] private Text statusText;

        [Header("Progression")]
        [SerializeField] private GameObject progressionPage;
        [SerializeField] private RectTransform progressionContent;
        [SerializeField] private StandaloneProgressionRow progressionRowTemplate;

        [Header("Abilities")]
        [SerializeField] private GameObject abilitiesPage;
        [SerializeField] private RectTransform abilitiesContent;
        [SerializeField] private StandaloneAbilityRow abilityRowTemplate;
        [SerializeField] private StandaloneHudTooltipPanel tooltip;

        private readonly List<GameObject> _progressionRows = new List<GameObject>(64);
        private readonly List<GameObject> _abilityRows = new List<GameObject>(64);
        private PlayerEntityGameManager _manager;
        private bool _showAbilities;

        public bool IsOpen => windowRoot != null && windowRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            closeButton?.onClick.AddListener(Close);
            progressionTabButton?.onClick.AddListener(ShowProgression);
            abilitiesTabButton?.onClick.AddListener(ShowAbilities);
            windowRoot?.SetActive(false);
            ApplyTabVisibility();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            closeButton?.onClick.RemoveListener(Close);
            progressionTabButton?.onClick.RemoveListener(ShowProgression);
            abilitiesTabButton?.onClick.RemoveListener(ShowAbilities);
            UnbindManager();
#endif
        }

        public void Open()
        {
#if !UNITY_SERVER
            BindManager(FindFirstObjectByType<PlayerEntityGameManager>());
            windowRoot?.SetActive(true);
            Render();
#endif
        }

        public void Close()
        {
#if !UNITY_SERVER
            tooltip?.Hide();
            StandaloneAbilityDragContext.End();
            windowRoot?.SetActive(false);
#endif
        }

        public void Toggle()
        {
#if !UNITY_SERVER
            if (IsOpen) Close(); else Open();
#endif
        }

        public void ShowProgression()
        {
            _showAbilities = false;
            ApplyTabVisibility();
            if (IsOpen) Render();
        }

        public void ShowAbilities()
        {
            _showAbilities = true;
            ApplyTabVisibility();
            if (IsOpen) Render();
        }

        private void BindManager(PlayerEntityGameManager manager)
        {
            if (ReferenceEquals(_manager, manager))
                return;
            UnbindManager();
            _manager = manager;
            if (_manager == null)
                return;
            _manager.ProgressionSnapshotReceived += OnProgression;
            _manager.GameplaySettingsSnapshotReceived += OnGameplaySettings;
        }

        private void UnbindManager()
        {
            if (_manager != null)
            {
                _manager.ProgressionSnapshotReceived -= OnProgression;
                _manager.GameplaySettingsSnapshotReceived -= OnGameplaySettings;
            }
            _manager = null;
        }

        private void OnProgression(ProgressionSnapshotMessage snapshot)
        {
            if (IsOpen)
                Render();
        }

        private void OnGameplaySettings(GameplaySettingsSnapshotMessage snapshot)
        {
            if (IsOpen)
                Render();
        }

        private void ApplyTabVisibility()
        {
            if (progressionPage != null) progressionPage.SetActive(!_showAbilities);
            if (abilitiesPage != null) abilitiesPage.SetActive(_showAbilities);
        }

        private void Render()
        {
            ProgressionSnapshotMessage progression = _manager != null ? _manager.LatestProgression : default;
            if (levelSummaryText != null)
            {
                levelSummaryText.text = progression.success
                    ? $"LEVEL {Math.Max(1, progression.level)}     XP {progression.experience:N0}"
                    : "LEVEL —     XP —";
            }

            if (_showAbilities)
                RenderAbilities(progression);
            else
                RenderProgression(progression);
        }

        private void RenderProgression(ProgressionSnapshotMessage snapshot)
        {
            ClearRows(_progressionRows);
            if (!snapshot.success)
            {
                SetStatus("Progression has not been hydrated yet.");
                return;
            }

            ProgressTrackDefinition[] definitions = ClientRecoveryContentCache.GetProgressTracks();
            ProgressTrackWire[] values = snapshot.tracks ?? Array.Empty<ProgressTrackWire>();
            var byId = new Dictionary<ushort, int>(values.Length);
            for (int i = 0; i < values.Length; ++i)
                byId[values[i].dataId] = values[i].value;

            int count = 0;
            for (int i = 0; i < definitions.Length; ++i)
            {
                ProgressTrackDefinition definition = definitions[i];
                if (definition == null || progressionRowTemplate == null || progressionContent == null)
                    continue;

                StandaloneProgressionRow row = Instantiate(progressionRowTemplate, progressionContent);
                row.gameObject.name = $"Track_{definition.definitionId}";
                row.gameObject.SetActive(true);
                int value = byId.TryGetValue(definition.dataId, out int tracked) ? tracked : 0;
                row.Bind(
                    Humanize(definition.kind.ToString()),
                    string.IsNullOrWhiteSpace(definition.displayName) ? definition.definitionId : definition.displayName,
                    value,
                    Math.Max(1, definition.maximumValue));
                _progressionRows.Add(row.gameObject);
                count++;
            }

            SetStatus(count == 0 ? "No client-known progression definitions are installed." : $"{count} progression tracks");
        }

        private void RenderAbilities(ProgressionSnapshotMessage snapshot)
        {
            ClearRows(_abilityRows);
            if (!snapshot.success)
            {
                SetStatus("Abilities have not been hydrated yet.");
                return;
            }
            if (PlayerGameplaySettingsRuntime.Revision <= 0)
            {
                SetStatus("Ability catalog has not been hydrated yet.");
                return;
            }

            ushort[] learned = snapshot.knownAbilityWireIds ?? Array.Empty<ushort>();
            GameplayAbilityClientReference[] catalog = PlayerGameplaySettingsRuntime.GetAbilityClientReferences();
            var rows = new List<GameplayAbilityClientReference>(learned.Length);
            for (int i = 0; i < catalog.Length; ++i)
            {
                GameplayAbilityClientReference ability = catalog[i];
                if (ability.WireId == 0 || string.IsNullOrWhiteSpace(ability.DefinitionId))
                    continue;
                if (Array.BinarySearch(learned, ability.WireId) < 0)
                    continue;
                rows.Add(ability);
            }

            rows.Sort(CompareAbilities);
            for (int i = 0; i < rows.Count; ++i)
            {
                if (abilityRowTemplate == null || abilitiesContent == null)
                    break;
                GameplayAbilityClientReference ability = rows[i];
                StandaloneAbilityRow row = Instantiate(abilityRowTemplate, abilitiesContent);
                row.gameObject.name = $"Ability_{ability.DefinitionId}";
                row.gameObject.SetActive(true);
                row.Bind(ability, tooltip);
                _abilityRows.Add(row.gameObject);
            }

            SetStatus(rows.Count == 0
                ? "No abilities learned on this character."
                : $"{rows.Count} learned abilities — drag an ability to a hotbar slot to assign it.");
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
                statusText.text = value ?? string.Empty;
        }

        private static int CompareAbilities(GameplayAbilityClientReference a, GameplayAbilityClientReference b)
        {
            int category = a.presentation.category.CompareTo(b.presentation.category);
            if (category != 0) return category;
            int order = a.presentation.sortOrder.CompareTo(b.presentation.sortOrder);
            if (order != 0) return order;
            string an = a.DisplayName;
            string bn = b.DisplayName;
            return string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = new List<char>(value.Length + 8);
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                    chars.Add(' ');
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }

        private static void ClearRows(List<GameObject> rows)
        {
            for (int i = 0; i < rows.Count; ++i)
                if (rows[i] != null) Destroy(rows[i]);
            rows.Clear();
        }

#if UNITY_EDITOR
        [Obsolete("Use the V2.11 tabbed ConfigureForEditor overload.")]
        public void ConfigureForEditor(GameObject authoredRoot, Button authoredClose, Text authoredSummary)
        {
            windowRoot = authoredRoot;
            closeButton = authoredClose;
            levelSummaryText = authoredSummary;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public void ConfigureForEditor(
            GameObject authoredRoot,
            Button authoredClose,
            Button authoredProgressionTab,
            Button authoredAbilitiesTab,
            Text authoredLevelSummary,
            Text authoredStatus,
            GameObject authoredProgressionPage,
            RectTransform authoredProgressionContent,
            StandaloneProgressionRow authoredProgressionTemplate,
            GameObject authoredAbilitiesPage,
            RectTransform authoredAbilitiesContent,
            StandaloneAbilityRow authoredAbilityTemplate,
            StandaloneHudTooltipPanel authoredTooltip)
        {
            windowRoot = authoredRoot;
            closeButton = authoredClose;
            progressionTabButton = authoredProgressionTab;
            abilitiesTabButton = authoredAbilitiesTab;
            levelSummaryText = authoredLevelSummary;
            statusText = authoredStatus;
            progressionPage = authoredProgressionPage;
            progressionContent = authoredProgressionContent;
            progressionRowTemplate = authoredProgressionTemplate;
            abilitiesPage = authoredAbilitiesPage;
            abilitiesContent = authoredAbilitiesContent;
            abilityRowTemplate = authoredAbilityTemplate;
            tooltip = authoredTooltip;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
