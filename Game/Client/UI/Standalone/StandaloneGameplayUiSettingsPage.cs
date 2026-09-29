using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored local-only Gameplay/UI settings page.</summary>
    public sealed class StandaloneGameplayUiSettingsPage : MonoBehaviour
    {
        [SerializeField] private Slider mouseSensitivitySlider;
        [SerializeField] private Text mouseSensitivityValue;
        [SerializeField] private Button invertYButton;
        [SerializeField] private Text invertYValue;
        [SerializeField] private Slider uiScaleSlider;
        [SerializeField] private Text uiScaleValue;
        [SerializeField] private Button chatTimestampsButton;
        [SerializeField] private Text chatTimestampsValue;
        [SerializeField] private Button interactionDotButton;
        [SerializeField] private Text interactionDotValue;
        [SerializeField] private Button applyButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private Text statusText;

        private bool _invertY;
        private bool _chatTimestamps;
        private bool _interactionDot;
        private bool _dirty;
        private bool _suppress;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            if (mouseSensitivitySlider != null) mouseSensitivitySlider.onValueChanged.AddListener(OnSliderChanged);
            if (uiScaleSlider != null) uiScaleSlider.onValueChanged.AddListener(OnSliderChanged);
            invertYButton?.onClick.AddListener(ToggleInvertY);
            chatTimestampsButton?.onClick.AddListener(ToggleChatTimestamps);
            interactionDotButton?.onClick.AddListener(ToggleInteractionDot);
            applyButton?.onClick.AddListener(Apply);
            resetButton?.onClick.AddListener(ResetDefaults);
            RefreshFromRuntime();
#endif
        }

        private void OnEnable()
        {
#if !UNITY_SERVER
            RefreshFromRuntime();
#endif
        }

        private void OnDisable()
        {
#if !UNITY_SERVER
            if (_dirty)
            {
                PlayerGameplayUiConfig.ApplySavedSettings();
                _dirty = false;
            }
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            if (mouseSensitivitySlider != null) mouseSensitivitySlider.onValueChanged.RemoveListener(OnSliderChanged);
            if (uiScaleSlider != null) uiScaleSlider.onValueChanged.RemoveListener(OnSliderChanged);
            invertYButton?.onClick.RemoveListener(ToggleInvertY);
            chatTimestampsButton?.onClick.RemoveListener(ToggleChatTimestamps);
            interactionDotButton?.onClick.RemoveListener(ToggleInteractionDot);
            applyButton?.onClick.RemoveListener(Apply);
            resetButton?.onClick.RemoveListener(ResetDefaults);
#endif
        }

        private void OnSliderChanged(float _)
        {
            if (_suppress)
                return;
            Preview();
        }

        private void ToggleInvertY()
        {
            _invertY = !_invertY;
            Preview();
        }

        private void ToggleChatTimestamps()
        {
            _chatTimestamps = !_chatTimestamps;
            Preview();
        }

        private void ToggleInteractionDot()
        {
            _interactionDot = !_interactionDot;
            Preview();
        }

        private void Preview()
        {
            if (_suppress)
                return;

            _dirty = true;
            RefreshLabels();
            PlayerGameplayUiConfig.Preview(
                MouseSensitivityValue(),
                _invertY,
                UiScaleValue(),
                _chatTimestamps,
                _interactionDot);
            SetStatus("Previewing local presentation changes. Select APPLY to save them.");
        }

        private void Apply()
        {
            PlayerGameplayUiConfig.SaveAndApply(
                MouseSensitivityValue(),
                _invertY,
                UiScaleValue(),
                _chatTimestamps,
                _interactionDot);
            _dirty = false;
            RefreshLabels();
            SetStatus("Gameplay/UI settings applied and saved on this machine.");
        }

        private void ResetDefaults()
        {
            PlayerGameplayUiConfig.ResetToDefaults();
            _dirty = false;
            RefreshFromRuntime();
            SetStatus("Default local gameplay/UI presentation settings restored.");
        }

        private void RefreshFromRuntime()
        {
            _suppress = true;
            if (mouseSensitivitySlider != null)
                mouseSensitivitySlider.SetValueWithoutNotify(PlayerGameplayUiConfig.MouseSensitivity);
            if (uiScaleSlider != null)
                uiScaleSlider.SetValueWithoutNotify(PlayerGameplayUiConfig.UiScale);
            _invertY = PlayerGameplayUiConfig.InvertY;
            _chatTimestamps = PlayerGameplayUiConfig.ShowChatTimestamps;
            _interactionDot = PlayerGameplayUiConfig.ShowInteractionDot;
            _suppress = false;
            _dirty = false;
            RefreshLabels();
            SetStatus("Local-machine only. These preferences never generate GameServer traffic.");
        }

        private float MouseSensitivityValue() =>
            mouseSensitivitySlider != null ? Mathf.Clamp(mouseSensitivitySlider.value, 0.25f, 2.00f) : 1f;

        private float UiScaleValue() =>
            uiScaleSlider != null ? Mathf.Clamp(uiScaleSlider.value, 0.75f, 1.50f) : 1f;

        private void RefreshLabels()
        {
            if (mouseSensitivityValue != null)
                mouseSensitivityValue.text = Mathf.RoundToInt(MouseSensitivityValue() * 100f) + "%";
            if (invertYValue != null)
                invertYValue.text = _invertY ? "ON" : "OFF";
            if (uiScaleValue != null)
                uiScaleValue.text = Mathf.RoundToInt(UiScaleValue() * 100f) + "%";
            if (chatTimestampsValue != null)
                chatTimestampsValue.text = _chatTimestamps ? "ON" : "OFF";
            if (interactionDotValue != null)
                interactionDotValue.text = _interactionDot ? "ON" : "OFF";
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
                statusText.text = value;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Slider mouseSensitivity, Text mouseSensitivityText,
            Button invertY, Text invertYText,
            Slider uiScale, Text uiScaleText,
            Button chatTimestamps, Text chatTimestampsText,
            Button interactionDot, Text interactionDotText,
            Button apply, Button reset, Text status)
        {
            mouseSensitivitySlider = mouseSensitivity;
            mouseSensitivityValue = mouseSensitivityText;
            invertYButton = invertY;
            invertYValue = invertYText;
            uiScaleSlider = uiScale;
            uiScaleValue = uiScaleText;
            chatTimestampsButton = chatTimestamps;
            chatTimestampsValue = chatTimestampsText;
            interactionDotButton = interactionDot;
            interactionDotValue = interactionDotText;
            applyButton = apply;
            resetButton = reset;
            statusText = status;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
