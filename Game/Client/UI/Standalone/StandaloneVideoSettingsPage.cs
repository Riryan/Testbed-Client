using System;
using System.Collections.Generic;
using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Runtime binder for the authored Video settings page.
    /// The hierarchy is authored in the prefab; this component only binds local settings data.
    /// </summary>
    public sealed class StandaloneVideoSettingsPage : MonoBehaviour
    {
        private readonly struct ResolutionOption
        {
            public readonly int Width;
            public readonly int Height;

            public ResolutionOption(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public override string ToString() => $"{Width} x {Height}";
        }

        private static readonly FullScreenMode[] DisplayModes =
        {
            FullScreenMode.FullScreenWindow,
            FullScreenMode.ExclusiveFullScreen,
            FullScreenMode.Windowed,
        };

        private static readonly int[] StandardFrameLimits =
        {
            30, 60, 90, 120, 144, 165, 240, -1,
        };

        [Header("Resolution")]
        [SerializeField] private Button resolutionPreviousButton;
        [SerializeField] private Button resolutionNextButton;
        [SerializeField] private Text resolutionValueText;

        [Header("Display Mode")]
        [SerializeField] private Button displayModePreviousButton;
        [SerializeField] private Button displayModeNextButton;
        [SerializeField] private Text displayModeValueText;

        [Header("Quality")]
        [SerializeField] private Button qualityPreviousButton;
        [SerializeField] private Button qualityNextButton;
        [SerializeField] private Text qualityValueText;

        [Header("VSync")]
        [SerializeField] private Button vSyncButton;
        [SerializeField] private Text vSyncValueText;

        [Header("Frame Limit")]
        [SerializeField] private Button frameLimitPreviousButton;
        [SerializeField] private Button frameLimitNextButton;
        [SerializeField] private Text frameLimitValueText;

        [Header("Actions")]
        [SerializeField] private Button applyButton;
        [SerializeField] private Button resetDefaultsButton;
        [SerializeField] private Text statusText;

        private readonly List<ResolutionOption> _resolutions = new List<ResolutionOption>(32);
        private readonly List<int> _frameLimits = new List<int>(StandardFrameLimits.Length + 1);
        private int _resolutionIndex;
        private int _displayModeIndex;
        private int _qualityIndex;
        private int _frameLimitIndex;
        private bool _vSync;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            resolutionPreviousButton?.onClick.AddListener(PreviousResolution);
            resolutionNextButton?.onClick.AddListener(NextResolution);
            displayModePreviousButton?.onClick.AddListener(PreviousDisplayMode);
            displayModeNextButton?.onClick.AddListener(NextDisplayMode);
            qualityPreviousButton?.onClick.AddListener(PreviousQuality);
            qualityNextButton?.onClick.AddListener(NextQuality);
            vSyncButton?.onClick.AddListener(ToggleVSync);
            frameLimitPreviousButton?.onClick.AddListener(PreviousFrameLimit);
            frameLimitNextButton?.onClick.AddListener(NextFrameLimit);
            applyButton?.onClick.AddListener(Apply);
            resetDefaultsButton?.onClick.AddListener(ResetDefaults);
#endif
        }

        private void OnEnable()
        {
#if !UNITY_SERVER
            RefreshFromCurrentSettings();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            resolutionPreviousButton?.onClick.RemoveListener(PreviousResolution);
            resolutionNextButton?.onClick.RemoveListener(NextResolution);
            displayModePreviousButton?.onClick.RemoveListener(PreviousDisplayMode);
            displayModeNextButton?.onClick.RemoveListener(NextDisplayMode);
            qualityPreviousButton?.onClick.RemoveListener(PreviousQuality);
            qualityNextButton?.onClick.RemoveListener(NextQuality);
            vSyncButton?.onClick.RemoveListener(ToggleVSync);
            frameLimitPreviousButton?.onClick.RemoveListener(PreviousFrameLimit);
            frameLimitNextButton?.onClick.RemoveListener(NextFrameLimit);
            applyButton?.onClick.RemoveListener(Apply);
            resetDefaultsButton?.onClick.RemoveListener(ResetDefaults);
#endif
        }

        private void RefreshFromCurrentSettings()
        {
            BuildResolutionOptions();
            BuildFrameLimitOptions();

            _resolutionIndex = FindResolutionIndex(Screen.width, Screen.height);
            _displayModeIndex = FindDisplayModeIndex(Screen.fullScreenMode);
            _qualityIndex = ClampQuality(QualitySettings.GetQualityLevel());
            _frameLimitIndex = FindFrameLimitIndex(Application.targetFrameRate);
            _vSync = QualitySettings.vSyncCount > 0;

            SetStatus("Changes apply locally to this client only. Nothing here is sent to the GameServer.");
            RefreshLabels();
        }

        private void BuildResolutionOptions()
        {
            _resolutions.Clear();
            Resolution[] available = Screen.resolutions;
            for (int i = 0; i < available.Length; ++i)
            {
                int width = available[i].width;
                int height = available[i].height;
                if (width < 640 || height < 360 || ContainsResolution(width, height))
                    continue;
                _resolutions.Add(new ResolutionOption(width, height));
            }

            if (!ContainsResolution(Screen.width, Screen.height))
                _resolutions.Add(new ResolutionOption(Screen.width, Screen.height));

            _resolutions.Sort((a, b) =>
            {
                long pixelsA = (long)a.Width * a.Height;
                long pixelsB = (long)b.Width * b.Height;
                int pixels = pixelsA.CompareTo(pixelsB);
                return pixels != 0 ? pixels : a.Width.CompareTo(b.Width);
            });

            if (_resolutions.Count == 0)
                _resolutions.Add(new ResolutionOption(Mathf.Max(640, Screen.width), Mathf.Max(360, Screen.height)));
        }

        private void BuildFrameLimitOptions()
        {
            _frameLimits.Clear();
            int current = Application.targetFrameRate <= 0 ? -1 : Application.targetFrameRate;
            for (int i = 0; i < StandardFrameLimits.Length; ++i)
                _frameLimits.Add(StandardFrameLimits[i]);

            if (!_frameLimits.Contains(current))
            {
                int insert = _frameLimits.Count - 1; // keep Unlimited last
                _frameLimits.Insert(Mathf.Max(0, insert), current);
                _frameLimits.Sort((a, b) =>
                {
                    if (a < 0) return 1;
                    if (b < 0) return -1;
                    return a.CompareTo(b);
                });
            }
        }

        private bool ContainsResolution(int width, int height)
        {
            for (int i = 0; i < _resolutions.Count; ++i)
                if (_resolutions[i].Width == width && _resolutions[i].Height == height)
                    return true;
            return false;
        }

        private int FindResolutionIndex(int width, int height)
        {
            for (int i = 0; i < _resolutions.Count; ++i)
                if (_resolutions[i].Width == width && _resolutions[i].Height == height)
                    return i;
            return Mathf.Clamp(_resolutions.Count - 1, 0, Mathf.Max(0, _resolutions.Count - 1));
        }

        private static int FindDisplayModeIndex(FullScreenMode mode)
        {
            for (int i = 0; i < DisplayModes.Length; ++i)
                if (DisplayModes[i] == mode)
                    return i;
            return 0;
        }

        private int FindFrameLimitIndex(int frameLimit)
        {
            int normalized = frameLimit <= 0 ? -1 : frameLimit;
            int index = _frameLimits.IndexOf(normalized);
            return index >= 0 ? index : 0;
        }

        private static int ClampQuality(int quality)
        {
            int count = QualitySettings.names != null ? QualitySettings.names.Length : 0;
            return count <= 0 ? 0 : Mathf.Clamp(quality, 0, count - 1);
        }

        private void PreviousResolution() => Cycle(ref _resolutionIndex, -1, _resolutions.Count);
        private void NextResolution() => Cycle(ref _resolutionIndex, 1, _resolutions.Count);
        private void PreviousDisplayMode() => Cycle(ref _displayModeIndex, -1, DisplayModes.Length);
        private void NextDisplayMode() => Cycle(ref _displayModeIndex, 1, DisplayModes.Length);
        private void PreviousQuality() => Cycle(ref _qualityIndex, -1, QualitySettings.names != null ? QualitySettings.names.Length : 0);
        private void NextQuality() => Cycle(ref _qualityIndex, 1, QualitySettings.names != null ? QualitySettings.names.Length : 0);
        private void PreviousFrameLimit() => Cycle(ref _frameLimitIndex, -1, _frameLimits.Count);
        private void NextFrameLimit() => Cycle(ref _frameLimitIndex, 1, _frameLimits.Count);

        private void ToggleVSync()
        {
            _vSync = !_vSync;
            RefreshLabels();
            MarkPending();
        }

        private void Cycle(ref int value, int delta, int count)
        {
            if (count <= 0)
                return;
            value = (value + delta + count) % count;
            RefreshLabels();
            MarkPending();
        }

        private void Apply()
        {
            if (_resolutions.Count == 0 || _frameLimits.Count == 0)
                return;

            ResolutionOption resolution = _resolutions[Mathf.Clamp(_resolutionIndex, 0, _resolutions.Count - 1)];
            FullScreenMode mode = DisplayModes[Mathf.Clamp(_displayModeIndex, 0, DisplayModes.Length - 1)];
            int quality = ClampQuality(_qualityIndex);
            int frameLimit = _frameLimits[Mathf.Clamp(_frameLimitIndex, 0, _frameLimits.Count - 1)];

            PlayerVideoConfig.SaveAndApply(
                resolution.Width,
                resolution.Height,
                mode,
                _vSync,
                frameLimit,
                quality);

            SetStatus("Video settings applied and saved locally.");
            RefreshLabels();
        }

        private void ResetDefaults()
        {
            PlayerVideoConfig.ResetToProjectDefaults();
            RefreshFromCurrentSettings();
            SetStatus("Project/default video settings restored. Local video overrides were cleared.");
        }

        private void MarkPending()
        {
            SetStatus("Pending changes. Select APPLY to save them locally.");
        }

        private void RefreshLabels()
        {
            if (resolutionValueText != null && _resolutions.Count > 0)
                resolutionValueText.text = _resolutions[Mathf.Clamp(_resolutionIndex, 0, _resolutions.Count - 1)].ToString();

            if (displayModeValueText != null)
                displayModeValueText.text = DisplayModeLabel(DisplayModes[Mathf.Clamp(_displayModeIndex, 0, DisplayModes.Length - 1)]);

            string[] qualityNames = QualitySettings.names;
            if (qualityValueText != null)
                qualityValueText.text = qualityNames != null && qualityNames.Length > 0
                    ? qualityNames[ClampQuality(_qualityIndex)]
                    : "Default";

            if (vSyncValueText != null)
                vSyncValueText.text = _vSync ? "ON" : "OFF";

            if (frameLimitValueText != null && _frameLimits.Count > 0)
            {
                int limit = _frameLimits[Mathf.Clamp(_frameLimitIndex, 0, _frameLimits.Count - 1)];
                frameLimitValueText.text = limit <= 0 ? "UNLIMITED" : $"{limit} FPS";
            }

            // Unity ignores targetFrameRate while vSyncCount is active on desktop.
            bool frameLimitInteractive = !_vSync;
            if (frameLimitPreviousButton != null) frameLimitPreviousButton.interactable = frameLimitInteractive;
            if (frameLimitNextButton != null) frameLimitNextButton.interactable = frameLimitInteractive;
        }

        private static string DisplayModeLabel(FullScreenMode mode)
        {
            switch (mode)
            {
                case FullScreenMode.ExclusiveFullScreen: return "EXCLUSIVE FULLSCREEN";
                case FullScreenMode.FullScreenWindow: return "BORDERLESS FULLSCREEN";
                case FullScreenMode.Windowed: return "WINDOWED";
                case FullScreenMode.MaximizedWindow: return "MAXIMIZED WINDOW";
                default: return mode.ToString().ToUpperInvariant();
            }
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
                statusText.text = message ?? string.Empty;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Button authoredResolutionPrevious,
            Button authoredResolutionNext,
            Text authoredResolutionValue,
            Button authoredDisplayModePrevious,
            Button authoredDisplayModeNext,
            Text authoredDisplayModeValue,
            Button authoredQualityPrevious,
            Button authoredQualityNext,
            Text authoredQualityValue,
            Button authoredVSyncButton,
            Text authoredVSyncValue,
            Button authoredFrameLimitPrevious,
            Button authoredFrameLimitNext,
            Text authoredFrameLimitValue,
            Button authoredApply,
            Button authoredResetDefaults,
            Text authoredStatus)
        {
            resolutionPreviousButton = authoredResolutionPrevious;
            resolutionNextButton = authoredResolutionNext;
            resolutionValueText = authoredResolutionValue;
            displayModePreviousButton = authoredDisplayModePrevious;
            displayModeNextButton = authoredDisplayModeNext;
            displayModeValueText = authoredDisplayModeValue;
            qualityPreviousButton = authoredQualityPrevious;
            qualityNextButton = authoredQualityNext;
            qualityValueText = authoredQualityValue;
            vSyncButton = authoredVSyncButton;
            vSyncValueText = authoredVSyncValue;
            frameLimitPreviousButton = authoredFrameLimitPrevious;
            frameLimitNextButton = authoredFrameLimitNext;
            frameLimitValueText = authoredFrameLimitValue;
            applyButton = authoredApply;
            resetDefaultsButton = authoredResetDefaults;
            statusText = authoredStatus;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
