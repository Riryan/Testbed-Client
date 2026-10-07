using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Game.Client.UI.Standalone;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Thin client-only hotkey bridge for the authored Achievements window.
    ///
    /// The binding itself belongs to the existing PlayerControlConfig/Controls settings
    /// system. This component only dispatches that existing local action to the authored
    /// achievement window. It sends no network message and owns no gameplay state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneAchievementHotkeyBridge : MonoBehaviour
    {
        [SerializeField] private StandaloneClientUIRoot uiRoot;
        [SerializeField] private StandaloneAchievementMilestoneWindow achievementsWindow;

        public bool HasAuthoredBindings =>
            uiRoot != null &&
            achievementsWindow != null;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            if (uiRoot == null)
                uiRoot = GetComponent<StandaloneClientUIRoot>();

            if (achievementsWindow == null)
                achievementsWindow =
                    GetComponentInChildren<StandaloneAchievementMilestoneWindow>(true);
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (uiRoot == null ||
                achievementsWindow == null ||
                !uiRoot.GameplaySessionActive)
                return;

            if (IsTextEntryFocused())
                return;

            // Rebinding/other exclusive local UI capture should suppress gameplay hotkeys.
            if (LocalClientInputGate.IsGameplayInputBlocked)
                return;

            if (PlayerControlConfig.GetKeyDown(PlayerControlAction.OpenAchievements))
                achievementsWindow.Toggle();
#endif
        }

        private static bool IsTextEntryFocused()
        {
            GameObject selected =
                EventSystem.current != null
                    ? EventSystem.current.currentSelectedGameObject
                    : null;

            return selected != null &&
                   selected.GetComponentInParent<InputField>() != null;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            StandaloneClientUIRoot authoredUiRoot,
            StandaloneAchievementMilestoneWindow authoredAchievementsWindow)
        {
            uiRoot = authoredUiRoot;
            achievementsWindow = authoredAchievementsWindow;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
