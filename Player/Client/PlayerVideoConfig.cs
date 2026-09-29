using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local-machine-only video preferences.
    ///
    /// No values in this class are replicated or sent to the GameServer. The startup
    /// project/Unity settings are captured before saved overrides are applied so that
    /// Reset To Defaults can return to the project's own baseline instead of inventing
    /// a second hard-coded graphics baseline.
    /// </summary>
    public static class PlayerVideoConfig
    {
        private const string Prefix = "MMO.PlayerVideo.V1.";
        private const string WidthKey = Prefix + "Width";
        private const string HeightKey = Prefix + "Height";
        private const string FullScreenModeKey = Prefix + "FullScreenMode";
        private const string VSyncKey = Prefix + "VSync";
        private const string FrameLimitKey = Prefix + "FrameLimit";
        private const string QualityKey = Prefix + "Quality";
        private const string HasOverridesKey = Prefix + "HasOverrides";

        private static bool _startupCaptured;
        private static int _startupWidth;
        private static int _startupHeight;
        private static FullScreenMode _startupFullScreenMode;
        private static int _startupVSyncCount;
        private static int _startupFrameLimit;
        private static int _startupQualityLevel;

        public static bool HasSavedOverrides => PlayerPrefs.GetInt(HasOverridesKey, 0) != 0;

        /// <summary>
        /// Called by the always-active standalone UI root bootstrap. On first launch with
        /// no saved override, this deliberately leaves the project's configured defaults alone.
        /// </summary>
        public static void ApplySavedSettings()
        {
#if UNITY_SERVER
            return;
#else
            CaptureStartupDefaults();
            if (!HasSavedOverrides)
                return;

            int width = Mathf.Max(640, PlayerPrefs.GetInt(WidthKey, _startupWidth));
            int height = Mathf.Max(360, PlayerPrefs.GetInt(HeightKey, _startupHeight));
            FullScreenMode mode = ReadMode(PlayerPrefs.GetInt(FullScreenModeKey, (int)_startupFullScreenMode));
            bool vSync = PlayerPrefs.GetInt(VSyncKey, _startupVSyncCount > 0 ? 1 : 0) != 0;
            int frameLimit = SanitizeFrameLimit(PlayerPrefs.GetInt(FrameLimitKey, _startupFrameLimit));
            int quality = ClampQuality(PlayerPrefs.GetInt(QualityKey, _startupQualityLevel));

            ApplyInternal(width, height, mode, vSync, frameLimit, quality);
#endif
        }

        public static void SaveAndApply(
            int width,
            int height,
            FullScreenMode mode,
            bool vSync,
            int frameLimit,
            int qualityLevel)
        {
#if UNITY_SERVER
            return;
#else
            CaptureStartupDefaults();

            width = Mathf.Max(640, width);
            height = Mathf.Max(360, height);
            mode = ReadMode((int)mode);
            frameLimit = SanitizeFrameLimit(frameLimit);
            qualityLevel = ClampQuality(qualityLevel);

            PlayerPrefs.SetInt(WidthKey, width);
            PlayerPrefs.SetInt(HeightKey, height);
            PlayerPrefs.SetInt(FullScreenModeKey, (int)mode);
            PlayerPrefs.SetInt(VSyncKey, vSync ? 1 : 0);
            PlayerPrefs.SetInt(FrameLimitKey, frameLimit);
            PlayerPrefs.SetInt(QualityKey, qualityLevel);
            PlayerPrefs.SetInt(HasOverridesKey, 1);
            PlayerPrefs.Save();

            ApplyInternal(width, height, mode, vSync, frameLimit, qualityLevel);
#endif
        }

        public static void ResetToProjectDefaults()
        {
#if UNITY_SERVER
            return;
#else
            CaptureStartupDefaults();

            PlayerPrefs.DeleteKey(WidthKey);
            PlayerPrefs.DeleteKey(HeightKey);
            PlayerPrefs.DeleteKey(FullScreenModeKey);
            PlayerPrefs.DeleteKey(VSyncKey);
            PlayerPrefs.DeleteKey(FrameLimitKey);
            PlayerPrefs.DeleteKey(QualityKey);
            PlayerPrefs.DeleteKey(HasOverridesKey);
            PlayerPrefs.Save();

            ApplyInternal(
                _startupWidth,
                _startupHeight,
                _startupFullScreenMode,
                _startupVSyncCount > 0,
                _startupFrameLimit,
                _startupQualityLevel);
#endif
        }

        public static void CaptureStartupDefaults()
        {
#if UNITY_SERVER
            return;
#else
            if (_startupCaptured)
                return;

            _startupCaptured = true;
            _startupWidth = Mathf.Max(640, Screen.width);
            _startupHeight = Mathf.Max(360, Screen.height);
            _startupFullScreenMode = Screen.fullScreenMode;
            _startupVSyncCount = QualitySettings.vSyncCount;
            _startupFrameLimit = Application.targetFrameRate;
            _startupQualityLevel = QualitySettings.GetQualityLevel();
#endif
        }

        private static void ApplyInternal(
            int width,
            int height,
            FullScreenMode mode,
            bool vSync,
            int frameLimit,
            int qualityLevel)
        {
            QualitySettings.SetQualityLevel(ClampQuality(qualityLevel), true);
            QualitySettings.vSyncCount = vSync ? 1 : 0;
            Application.targetFrameRate = SanitizeFrameLimit(frameLimit);

            if (Screen.width != width || Screen.height != height || Screen.fullScreenMode != mode)
                Screen.SetResolution(width, height, mode);
        }

        private static int ClampQuality(int qualityLevel)
        {
            int count = QualitySettings.names != null ? QualitySettings.names.Length : 0;
            return count <= 0 ? 0 : Mathf.Clamp(qualityLevel, 0, count - 1);
        }

        private static int SanitizeFrameLimit(int frameLimit)
        {
            if (frameLimit <= 0)
                return -1;
            return Mathf.Clamp(frameLimit, 15, 1000);
        }

        private static FullScreenMode ReadMode(int value)
        {
            switch ((FullScreenMode)value)
            {
                case FullScreenMode.ExclusiveFullScreen:
                case FullScreenMode.FullScreenWindow:
                case FullScreenMode.MaximizedWindow:
                case FullScreenMode.Windowed:
                    return (FullScreenMode)value;
                default:
                    return FullScreenMode.FullScreenWindow;
            }
        }
    }
}
