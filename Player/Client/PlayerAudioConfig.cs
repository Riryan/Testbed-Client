using System;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local-machine-only audio preferences. Nothing here is replicated or sent to the GameServer.
    /// Master volume is applied through AudioListener.volume. Channel levels are exposed for the
    /// existing/current presentation systems to consume without creating another network path.
    /// </summary>
    public static class PlayerAudioConfig
    {
        private const string Prefix = "MMO.PlayerAudio.V1.";
        private const string MasterKey = Prefix + "Master";
        private const string MusicKey = Prefix + "Music";
        private const string SfxKey = Prefix + "Sfx";
        private const string UiKey = Prefix + "Ui";
        private const string VoiceKey = Prefix + "Voice";
        private const string HasOverridesKey = Prefix + "HasOverrides";

        public const float DefaultMaster = 1f;
        public const float DefaultMusic = 1f;
        public const float DefaultSfx = 1f;
        public const float DefaultUi = 1f;
        public const float DefaultVoice = 1f;

        public static event Action Changed;

        public static float MasterVolume { get; private set; } = DefaultMaster;
        public static float MusicVolume { get; private set; } = DefaultMusic;
        public static float SfxVolume { get; private set; } = DefaultSfx;
        public static float UiVolume { get; private set; } = DefaultUi;
        public static float VoiceVolume { get; private set; } = DefaultVoice;

        public static bool HasSavedOverrides => PlayerPrefs.GetInt(HasOverridesKey, 0) != 0;

        public static void ApplySavedSettings()
        {
#if UNITY_SERVER
            return;
#else
            if (!HasSavedOverrides)
            {
                ApplyRuntime(DefaultMaster, DefaultMusic, DefaultSfx, DefaultUi, DefaultVoice);
                return;
            }

            ApplyRuntime(
                PlayerPrefs.GetFloat(MasterKey, DefaultMaster),
                PlayerPrefs.GetFloat(MusicKey, DefaultMusic),
                PlayerPrefs.GetFloat(SfxKey, DefaultSfx),
                PlayerPrefs.GetFloat(UiKey, DefaultUi),
                PlayerPrefs.GetFloat(VoiceKey, DefaultVoice));
#endif
        }

        public static void Preview(float master, float music, float sfx, float ui, float voice)
        {
#if !UNITY_SERVER
            ApplyRuntime(master, music, sfx, ui, voice);
#endif
        }

        public static void SaveAndApply(float master, float music, float sfx, float ui, float voice)
        {
#if UNITY_SERVER
            return;
#else
            master = Clamp(master);
            music = Clamp(music);
            sfx = Clamp(sfx);
            ui = Clamp(ui);
            voice = Clamp(voice);

            PlayerPrefs.SetFloat(MasterKey, master);
            PlayerPrefs.SetFloat(MusicKey, music);
            PlayerPrefs.SetFloat(SfxKey, sfx);
            PlayerPrefs.SetFloat(UiKey, ui);
            PlayerPrefs.SetFloat(VoiceKey, voice);
            PlayerPrefs.SetInt(HasOverridesKey, 1);
            PlayerPrefs.Save();

            ApplyRuntime(master, music, sfx, ui, voice);
#endif
        }

        public static void ResetToDefaults()
        {
#if UNITY_SERVER
            return;
#else
            PlayerPrefs.DeleteKey(MasterKey);
            PlayerPrefs.DeleteKey(MusicKey);
            PlayerPrefs.DeleteKey(SfxKey);
            PlayerPrefs.DeleteKey(UiKey);
            PlayerPrefs.DeleteKey(VoiceKey);
            PlayerPrefs.DeleteKey(HasOverridesKey);
            PlayerPrefs.Save();
            ApplyRuntime(DefaultMaster, DefaultMusic, DefaultSfx, DefaultUi, DefaultVoice);
#endif
        }

        private static void ApplyRuntime(float master, float music, float sfx, float ui, float voice)
        {
            MasterVolume = Clamp(master);
            MusicVolume = Clamp(music);
            SfxVolume = Clamp(sfx);
            UiVolume = Clamp(ui);
            VoiceVolume = Clamp(voice);

            AudioListener.volume = MasterVolume;
            Changed?.Invoke();
        }

        private static float Clamp(float value) => Mathf.Clamp01(value);
    }
}
