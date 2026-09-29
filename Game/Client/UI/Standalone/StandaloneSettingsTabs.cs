using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Authored settings-tab controller for the standalone client UI.
    /// This is presentation-only and owns no networked state.
    /// </summary>
    public sealed class StandaloneSettingsTabs : MonoBehaviour
    {
        [SerializeField] private Button controlsButton;
        [SerializeField] private Button videoButton;
        [SerializeField] private Button audioButton;
        [SerializeField] private Button gameplayUiButton;

        [SerializeField] private GameObject controlsPage;
        [SerializeField] private GameObject videoPage;
        [SerializeField] private GameObject audioPage;
        [SerializeField] private GameObject gameplayUiPage;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            controlsButton?.onClick.AddListener(ShowControls);
            videoButton?.onClick.AddListener(ShowVideo);
            audioButton?.onClick.AddListener(ShowAudio);
            gameplayUiButton?.onClick.AddListener(ShowGameplayUi);
            ShowControls();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            controlsButton?.onClick.RemoveListener(ShowControls);
            videoButton?.onClick.RemoveListener(ShowVideo);
            audioButton?.onClick.RemoveListener(ShowAudio);
            gameplayUiButton?.onClick.RemoveListener(ShowGameplayUi);
#endif
        }

        public void ShowControls() => ShowPage(controlsPage);
        public void ShowVideo() => ShowPage(videoPage);
        public void ShowAudio() => ShowPage(audioPage);
        public void ShowGameplayUi() => ShowPage(gameplayUiPage);

        private void ShowPage(GameObject selected)
        {
#if !UNITY_SERVER
            if (controlsPage != null) controlsPage.SetActive(selected == controlsPage);
            if (videoPage != null) videoPage.SetActive(selected == videoPage);
            if (audioPage != null) audioPage.SetActive(selected == audioPage);
            if (gameplayUiPage != null) gameplayUiPage.SetActive(selected == gameplayUiPage);
#endif
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Button authoredControlsButton,
            Button authoredVideoButton,
            Button authoredAudioButton,
            Button authoredGameplayUiButton,
            GameObject authoredControlsPage,
            GameObject authoredVideoPage,
            GameObject authoredAudioPage,
            GameObject authoredGameplayUiPage)
        {
            controlsButton = authoredControlsButton;
            videoButton = authoredVideoButton;
            audioButton = authoredAudioButton;
            gameplayUiButton = authoredGameplayUiButton;
            controlsPage = authoredControlsPage;
            videoPage = authoredVideoPage;
            audioPage = authoredAudioPage;
            gameplayUiPage = authoredGameplayUiPage;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
