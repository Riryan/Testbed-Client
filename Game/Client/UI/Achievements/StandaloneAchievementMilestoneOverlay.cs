using System;
using System.Collections.Generic;
using Player.Networking;
using UnityEngine;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Temporary F8 validation overlay for milestone UI/UX.
    /// It intentionally creates no request path and consumes only the existing cached
    /// progression snapshot/delta stream. Remove once the authored production UI is approved.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class StandaloneAchievementMilestoneOverlay : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.F8;
        private static StandaloneAchievementMilestoneOverlay _instance;

        private PlayerEntityGameManager _manager;
        private Vector2 _scroll;
        private bool _visible;
        private bool _showCompleted = true;
        private bool _showIncomplete = true;
        private bool _completionBaselineReady;
        private HashSet<string> _completed = new HashSet<string>(StringComparer.Ordinal);
        private string _toast = string.Empty;
        private float _toastUntil;
        private Rect _windowRect = new Rect(40f, 60f, 660f, 720f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if !UNITY_SERVER
            if (_instance != null)
                return;

            var root = new GameObject("AchievementMilestoneTestOverlay");
            DontDestroyOnLoad(root);
            _instance = root.AddComponent<StandaloneAchievementMilestoneOverlay>();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (UnityEngine.Input.GetKeyDown(ToggleKey))
                _visible = !_visible;

            if (_manager == null)
                BindManager(FindFirstObjectByType<PlayerEntityGameManager>());
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            BindManager(null);
            if (_instance == this)
                _instance = null;
#endif
        }

        private void BindManager(PlayerEntityGameManager manager)
        {
            if (ReferenceEquals(_manager, manager))
                return;

            if (_manager != null)
                _manager.ProgressionSnapshotReceived -= OnProgressionChanged;

            _manager = manager;
            _completionBaselineReady = false;
            _completed.Clear();

            if (_manager != null)
            {
                _manager.ProgressionSnapshotReceived += OnProgressionChanged;
                if (_manager.LatestProgression.success)
                    EstablishBaseline(_manager.LatestProgression);
            }
        }

        private void OnProgressionChanged(ProgressionSnapshotMessage snapshot)
        {
            if (!snapshot.success)
                return;

            HashSet<string> next = AchievementMilestoneProjection.CompletedKeys(snapshot);
            if (!_completionBaselineReady)
            {
                _completed = next;
                _completionBaselineReady = true;
                return;
            }

            foreach (string key in next)
            {
                if (_completed.Contains(key))
                    continue;

                _toast = AchievementMilestoneProjection.MilestoneLabel(key, snapshot);
                _toastUntil = Time.unscaledTime + 4f;
            }

            _completed = next;
        }

        private void EstablishBaseline(ProgressionSnapshotMessage snapshot)
        {
            _completed = AchievementMilestoneProjection.CompletedKeys(snapshot);
            _completionBaselineReady = true;
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
            {
                Rect toastRect = new Rect((Screen.width - 440f) * 0.5f, 24f, 440f, 54f);
                GUI.Box(toastRect, $"MILESTONE REACHED\n{_toast}");
            }

            if (!_visible)
            {
                GUI.Label(new Rect(12f, Screen.height - 28f, 320f, 24f), "F8 - Milestones test overlay");
                return;
            }

            _windowRect = GUI.Window(GetInstanceID(), _windowRect, DrawWindow, "ACHIEVEMENTS / MILESTONES - TEST OVERLAY");
#endif
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("SOURCE: existing LatestProgression cache + client-known content definitions");
            GUILayout.Label("WIRE: opening/closing this overlay sends 0 requests; existing progression deltas refresh it.");

            if (_manager == null)
            {
                GUILayout.Space(8f);
                GUILayout.Label("PlayerEntityGameManager not available yet.");
                GUI.DragWindow();
                return;
            }

            ProgressionSnapshotMessage snapshot = _manager.LatestProgression;
            if (!snapshot.success)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Authoritative progression has not been hydrated yet.");
                GUILayout.Label("Enter the world normally. This overlay does not request it itself.");
                GUI.DragWindow();
                return;
            }

            GUILayout.Space(6f);
            GUILayout.Label($"Progression revision: {snapshot.revision:N0}    Content revision: {snapshot.contentRevision:N0}");
            GUILayout.BeginHorizontal();
            _showCompleted = GUILayout.Toggle(_showCompleted, "Completed");
            _showIncomplete = GUILayout.Toggle(_showIncomplete, "In Progress");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close (F8)", GUILayout.Width(100f)))
                _visible = false;
            GUILayout.EndHorizontal();

            List<AchievementMilestoneProjection.MilestoneRow> rows = AchievementMilestoneProjection.Build(snapshot);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            string currentCategory = null;
            for (int i = 0; i < rows.Count; ++i)
            {
                AchievementMilestoneProjection.MilestoneRow row = rows[i];
                if ((row.Complete && !_showCompleted) || (!row.Complete && !_showIncomplete))
                    continue;

                if (!string.Equals(currentCategory, row.Category, StringComparison.Ordinal))
                {
                    currentCategory = row.Category;
                    GUILayout.Space(8f);
                    GUILayout.Label($"--- {currentCategory.ToUpperInvariant()} ---");
                }

                DrawRow(row);
            }

            GUILayout.EndScrollView();
            GUILayout.Space(4f);
            GUILayout.Label("This is a validation overlay only. Production UI can reuse the same projection without adding network traffic.");
            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 28f));
        }

        private static void DrawRow(AchievementMilestoneProjection.MilestoneRow row)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(row.Name, GUILayout.Width(260f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(row.Complete ? "COMPLETE" : $"NEXT {row.NextThreshold:N0}", GUILayout.Width(110f));
            GUILayout.EndHorizontal();

            Rect barRect = GUILayoutUtility.GetRect(10f, 18f, GUILayout.ExpandWidth(true));
            GUI.Box(barRect, string.Empty);
            float normalized = AchievementMilestoneProjection.Normalized(row);
            Rect fill = new Rect(barRect.x + 2f, barRect.y + 2f, Math.Max(0f, (barRect.width - 4f) * normalized), Math.Max(0f, barRect.height - 4f));
            if (fill.width > 0f)
                GUI.Box(fill, string.Empty);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{row.Current:N0} / {row.Maximum:N0}");
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Milestones {row.CompletedSteps}/{row.TotalSteps}");
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}
