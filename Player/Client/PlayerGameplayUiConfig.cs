using System;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local-machine-only gameplay/presentation preferences.
    /// These values never replicate and never generate GameServer traffic.
    /// </summary>
    public static class PlayerGameplayUiConfig
    {
        private const string Prefix = "MMO.PlayerGameplayUi.V1.";
        private const string MouseSensitivityKey = Prefix + "MouseSensitivity";
        private const string InvertYKey = Prefix + "InvertY";
        private const string UiScaleKey = Prefix + "UiScale";
        private const string ChatTimestampsKey = Prefix + "ChatTimestamps";
        private const string InteractionDotKey = Prefix + "InteractionDot";

        public const float DefaultMouseSensitivity = 1f;
        public const bool DefaultInvertY = false;
        public const float DefaultUiScale = 1f;
        public const bool DefaultChatTimestamps = false;
        public const bool DefaultInteractionDot = true;

        private static bool _loaded;
        private static float _mouseSensitivity = DefaultMouseSensitivity;
        private static bool _invertY = DefaultInvertY;
        private static float _uiScale = DefaultUiScale;
        private static bool _chatTimestamps = DefaultChatTimestamps;
        private static bool _interactionDot = DefaultInteractionDot;

        public static event Action Changed;

        public static float MouseSensitivity { get { EnsureLoaded(); return _mouseSensitivity; } }
        public static bool InvertY { get { EnsureLoaded(); return _invertY; } }
        public static float UiScale { get { EnsureLoaded(); return _uiScale; } }
        public static bool ShowChatTimestamps { get { EnsureLoaded(); return _chatTimestamps; } }
        public static bool ShowInteractionDot { get { EnsureLoaded(); return _interactionDot; } }

        public static void ApplySavedSettings()
        {
#if UNITY_SERVER
            return;
#else
            LoadFromPrefs();
            Changed?.Invoke();
#endif
        }

        public static void Preview(
            float mouseSensitivity,
            bool invertY,
            float uiScale,
            bool chatTimestamps,
            bool interactionDot)
        {
#if !UNITY_SERVER
            ApplyRuntime(mouseSensitivity, invertY, uiScale, chatTimestamps, interactionDot);
#endif
        }

        public static void SaveAndApply(
            float mouseSensitivity,
            bool invertY,
            float uiScale,
            bool chatTimestamps,
            bool interactionDot)
        {
#if UNITY_SERVER
            return;
#else
            mouseSensitivity = ClampMouseSensitivity(mouseSensitivity);
            uiScale = ClampUiScale(uiScale);

            PlayerPrefs.SetFloat(MouseSensitivityKey, mouseSensitivity);
            PlayerPrefs.SetInt(InvertYKey, invertY ? 1 : 0);
            PlayerPrefs.SetFloat(UiScaleKey, uiScale);
            PlayerPrefs.SetInt(ChatTimestampsKey, chatTimestamps ? 1 : 0);
            PlayerPrefs.SetInt(InteractionDotKey, interactionDot ? 1 : 0);
            PlayerPrefs.Save();

            ApplyRuntime(mouseSensitivity, invertY, uiScale, chatTimestamps, interactionDot);
#endif
        }

        public static void ResetToDefaults()
        {
#if UNITY_SERVER
            return;
#else
            PlayerPrefs.DeleteKey(MouseSensitivityKey);
            PlayerPrefs.DeleteKey(InvertYKey);
            PlayerPrefs.DeleteKey(UiScaleKey);
            PlayerPrefs.DeleteKey(ChatTimestampsKey);
            PlayerPrefs.DeleteKey(InteractionDotKey);
            PlayerPrefs.Save();

            ApplyRuntime(
                DefaultMouseSensitivity,
                DefaultInvertY,
                DefaultUiScale,
                DefaultChatTimestamps,
                DefaultInteractionDot);
#endif
        }

        private static void EnsureLoaded()
        {
#if !UNITY_SERVER
            if (!_loaded)
                LoadFromPrefs();
#endif
        }

        private static void LoadFromPrefs()
        {
            _loaded = true;
            ApplyRuntime(
                PlayerPrefs.GetFloat(MouseSensitivityKey, DefaultMouseSensitivity),
                PlayerPrefs.GetInt(InvertYKey, DefaultInvertY ? 1 : 0) != 0,
                PlayerPrefs.GetFloat(UiScaleKey, DefaultUiScale),
                PlayerPrefs.GetInt(ChatTimestampsKey, DefaultChatTimestamps ? 1 : 0) != 0,
                PlayerPrefs.GetInt(InteractionDotKey, DefaultInteractionDot ? 1 : 0) != 0,
                raiseChanged: false);
        }

        private static void ApplyRuntime(
            float mouseSensitivity,
            bool invertY,
            float uiScale,
            bool chatTimestamps,
            bool interactionDot,
            bool raiseChanged = true)
        {
            _loaded = true;
            _mouseSensitivity = ClampMouseSensitivity(mouseSensitivity);
            _invertY = invertY;
            _uiScale = ClampUiScale(uiScale);
            _chatTimestamps = chatTimestamps;
            _interactionDot = interactionDot;
            if (raiseChanged)
                Changed?.Invoke();
        }

        private static float ClampMouseSensitivity(float value) => Mathf.Clamp(value, 0.25f, 2.00f);
        private static float ClampUiScale(float value) => Mathf.Clamp(value, 0.75f, 1.50f);
    }
}
