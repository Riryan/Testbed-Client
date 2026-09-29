using Player.Client;
using UnityEngine;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Applies local-machine-only settings early from the always-active authored UI root.
    /// It owns no network state and sends no messages.
    /// </summary>
    public sealed class StandaloneLocalSettingsBootstrap : MonoBehaviour
    {
        private void Awake()
        {
#if !UNITY_SERVER
            PlayerVideoConfig.ApplySavedSettings();
            PlayerAudioConfig.ApplySavedSettings();
            PlayerGameplayUiConfig.ApplySavedSettings();
#endif
        }
    }
}
