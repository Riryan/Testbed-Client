using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored local-only audio settings page.</summary>
    public sealed class StandaloneAudioSettingsPage : MonoBehaviour
    {
        [SerializeField] private Slider masterSlider;
        [SerializeField] private Slider musicSlider;
        [SerializeField] private Slider sfxSlider;
        [SerializeField] private Slider uiSlider;
        [SerializeField] private Slider voiceSlider;

        [SerializeField] private Text masterValue;
        [SerializeField] private Text musicValue;
        [SerializeField] private Text sfxValue;
        [SerializeField] private Text uiValue;
        [SerializeField] private Text voiceValue;

        [SerializeField] private Button applyButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private Text statusText;

        private bool _dirty;
        private bool _suppress;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            Bind(masterSlider);
            Bind(musicSlider);
            Bind(sfxSlider);
            Bind(uiSlider);
            Bind(voiceSlider);
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
                // Do not leave an unsaved preview active after the player leaves the page.
                PlayerAudioConfig.ApplySavedSettings();
                _dirty = false;
            }
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            Unbind(masterSlider);
            Unbind(musicSlider);
            Unbind(sfxSlider);
            Unbind(uiSlider);
            Unbind(voiceSlider);
            applyButton?.onClick.RemoveListener(Apply);
            resetButton?.onClick.RemoveListener(ResetDefaults);
#endif
        }

        private void Bind(Slider slider)
        {
            if (slider != null)
                slider.onValueChanged.AddListener(OnSliderChanged);
        }

        private void Unbind(Slider slider)
        {
            if (slider != null)
                slider.onValueChanged.RemoveListener(OnSliderChanged);
        }

        private void OnSliderChanged(float _)
        {
            if (_suppress)
                return;
            _dirty = true;
            RefreshLabels();
            PlayerAudioConfig.Preview(Value(masterSlider), Value(musicSlider), Value(sfxSlider), Value(uiSlider), Value(voiceSlider));
            SetStatus("Previewing changes. Select APPLY to save them locally.");
        }

        private void Apply()
        {
            PlayerAudioConfig.SaveAndApply(Value(masterSlider), Value(musicSlider), Value(sfxSlider), Value(uiSlider), Value(voiceSlider));
            _dirty = false;
            SetStatus("Audio settings applied and saved locally.");
            RefreshLabels();
        }

        private void ResetDefaults()
        {
            PlayerAudioConfig.ResetToDefaults();
            _dirty = false;
            RefreshFromRuntime();
            SetStatus("Default audio levels restored and local audio overrides cleared.");
        }

        private void RefreshFromRuntime()
        {
            _suppress = true;
            Set(masterSlider, PlayerAudioConfig.MasterVolume);
            Set(musicSlider, PlayerAudioConfig.MusicVolume);
            Set(sfxSlider, PlayerAudioConfig.SfxVolume);
            Set(uiSlider, PlayerAudioConfig.UiVolume);
            Set(voiceSlider, PlayerAudioConfig.VoiceVolume);
            _suppress = false;
            _dirty = false;
            RefreshLabels();
            SetStatus("Changes are local to this client. Master applies globally; channel levels are consumed by their presentation systems.");
        }

        private void RefreshLabels()
        {
            SetPercent(masterValue, Value(masterSlider));
            SetPercent(musicValue, Value(musicSlider));
            SetPercent(sfxValue, Value(sfxSlider));
            SetPercent(uiValue, Value(uiSlider));
            SetPercent(voiceValue, Value(voiceSlider));
        }

        private static float Value(Slider slider) => slider != null ? Mathf.Clamp01(slider.value) : 1f;
        private static void Set(Slider slider, float value) { if (slider != null) slider.SetValueWithoutNotify(Mathf.Clamp01(value)); }
        private static void SetPercent(Text text, float value) { if (text != null) text.text = Mathf.RoundToInt(Mathf.Clamp01(value) * 100f) + "%"; }
        private void SetStatus(string value) { if (statusText != null) statusText.text = value; }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Slider master, Text masterText,
            Slider music, Text musicText,
            Slider sfx, Text sfxText,
            Slider ui, Text uiText,
            Slider voice, Text voiceText,
            Button apply, Button reset, Text status)
        {
            masterSlider = master; masterValue = masterText;
            musicSlider = music; musicValue = musicText;
            sfxSlider = sfx; sfxValue = sfxText;
            uiSlider = ui; uiValue = uiText;
            voiceSlider = voice; voiceValue = voiceText;
            applyButton = apply; resetButton = reset; statusText = status;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
