using System;
using System.Collections.Generic;
using Game.Client.Content;
using Game.Client.UI.Standalone;
using Game.Shared.Content;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Runtime binder for the authored AchievementsWindow cloned from the real
    /// Standalone SkillsWindow during the one-time Editor install.
    ///
    /// Runtime creates only repeated data rows from the already-authored row template.
    /// The window/header/scroll/header-columns/status/quick-link are serialized in the prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneAchievementMilestoneWindow : MonoBehaviour
    {
        [Header("Authored Window")]
        [SerializeField] private GameObject windowRoot;
        [SerializeField] private GameObject skillsWindow;
        [SerializeField] private Button openButton;
        [SerializeField] private Button skillsButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text statusText;

        [Header("Authored List")]
        [SerializeField] private RectTransform content;
        [SerializeField] private StandaloneProgressionRow rowTemplate;

        private readonly List<GameObject> _rows =
            new List<GameObject>(64);

        private PlayerEntityGameManager _manager;
        private bool _pointerCaptureHeld;

        public bool HasAuthoredBindings =>
            windowRoot != null &&
            skillsWindow != null &&
            openButton != null &&
            skillsButton != null &&
            closeButton != null &&
            statusText != null &&
            content != null &&
            rowTemplate != null;

        public bool IsOpen =>
            windowRoot != null &&
            windowRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            openButton?.onClick.AddListener(Toggle);
            skillsButton?.onClick.AddListener(Close);
            closeButton?.onClick.AddListener(Close);

            if (rowTemplate != null)
                rowTemplate.gameObject.SetActive(false);

            windowRoot?.SetActive(false);
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindManager(
                FindFirstObjectByType<PlayerEntityGameManager>());
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            openButton?.onClick.RemoveListener(Toggle);
            skillsButton?.onClick.RemoveListener(Close);
            closeButton?.onClick.RemoveListener(Close);
            BindManager(null);
            ReleasePointerCapture();
#endif
        }

        public void Toggle()
        {
#if !UNITY_SERVER
            if (IsOpen)
                Close();
            else
                Open();
#endif
        }

        public void Open()
        {
#if !UNITY_SERVER
            BindManager(
                FindFirstObjectByType<PlayerEntityGameManager>());

            // Reuse the standalone root's existing transient-window cleanup instead of
            // allowing overlapping gameplay windows.
            StandaloneClientUIRoot.Instance?.CloseTransientGameplayWindows();

            // The original Skills window occupies the same authored screen position.
            if (skillsWindow != null)
                skillsWindow.SetActive(false);

            windowRoot?.SetActive(true);
            AcquirePointerCapture();
            Render();
#endif
        }

        public void Close()
        {
#if !UNITY_SERVER
            windowRoot?.SetActive(false);
            ReleasePointerCapture();
#endif
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

        private void BindManager(
            PlayerEntityGameManager manager)
        {
            if (ReferenceEquals(_manager, manager))
                return;

            if (_manager != null)
                _manager.ProgressionSnapshotReceived -= OnProgression;

            _manager = manager;

            if (_manager != null)
                _manager.ProgressionSnapshotReceived += OnProgression;
        }

        private void OnProgression(
            ProgressionSnapshotMessage snapshot)
        {
            if (IsOpen)
                Render(snapshot);
        }

        private void Render()
        {
            ProgressionSnapshotMessage snapshot =
                _manager != null
                    ? _manager.LatestProgression
                    : default;

            Render(snapshot);
        }

        private void Render(
            ProgressionSnapshotMessage snapshot)
        {
            ClearRows();

            if (!snapshot.success)
            {
                SetStatus(
                    "Achievements are waiting for the existing progression cache.");
                return;
            }

            List<AchievementMilestoneProjection.MilestoneRow> milestones =
                AchievementMilestoneProjection.Build(snapshot);

            int completed = 0;

            for (int i = 0; i < milestones.Count; ++i)
            {
                AchievementMilestoneProjection.MilestoneRow milestone =
                    milestones[i];

                if (rowTemplate == null ||
                    content == null)
                    break;

                StandaloneProgressionRow row =
                    Instantiate(
                        rowTemplate,
                        content);

                row.gameObject.name =
                    $"AchievementRow_{i:00}";

                row.gameObject.SetActive(true);

                string displayName =
                    milestone.Complete
                        ? $"{milestone.Name}  [COMPLETE]"
                        : $"{milestone.Name}  — next {milestone.NextThreshold:N0}";

                row.Bind(
                    milestone.Category,
                    displayName,
                    milestone.Current,
                    milestone.Maximum);

                _rows.Add(row.gameObject);

                if (milestone.Complete)
                    completed++;
            }

            Canvas.ForceUpdateCanvases();

            if (content != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            SetStatus(
                $"{completed}/{milestones.Count} milestone tracks complete" +
                FactionSuffix(snapshot.factionDataId) +
                "  •  other-faction achievements hidden");
        }

        private void ClearRows()
        {
            for (int i = 0; i < _rows.Count; ++i)
            {
                if (_rows[i] != null)
                    Destroy(_rows[i]);
            }

            _rows.Clear();
        }

        private void SetStatus(
            string value)
        {
            if (statusText != null)
                statusText.text = value ?? string.Empty;
        }

        private static string FactionSuffix(
            ushort factionDataId)
        {
            if (factionDataId == 0)
                return string.Empty;

            if (ClientRecoveryContentCache.TryGetFaction(
                    factionDataId,
                    out FactionDefinition faction) &&
                faction != null)
            {
                string name =
                    string.IsNullOrWhiteSpace(faction.displayName)
                        ? faction.definitionId
                        : faction.displayName;

                return string.IsNullOrWhiteSpace(name)
                    ? string.Empty
                    : $"  •  {name}";
            }

            return string.Empty;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject authoredWindowRoot,
            GameObject authoredSkillsWindow,
            Button authoredOpenButton,
            Button authoredSkillsButton,
            Button authoredCloseButton,
            Text authoredStatusText,
            RectTransform authoredContent,
            StandaloneProgressionRow authoredRowTemplate)
        {
            windowRoot = authoredWindowRoot;
            skillsWindow = authoredSkillsWindow;
            openButton = authoredOpenButton;
            skillsButton = authoredSkillsButton;
            closeButton = authoredCloseButton;
            statusText = authoredStatusText;
            content = authoredContent;
            rowTemplate = authoredRowTemplate;

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
