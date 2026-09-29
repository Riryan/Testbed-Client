using System;
using UnityEngine;

namespace Game.Client.UI.SocialEconomy
{
    /// <summary>
    /// Retired migration shim. Friends are owned by Game.Client.UI.Social.ClientSocialEconomyUI;
    /// Trade/Storage are owned by Game.Client.UI.Social.ClientTradeStorageUI. The prefab repair
    /// removes this component from older ClientUIRoot prefabs. It intentionally performs no
    /// runtime work so stale serialized copies cannot duplicate requests, subscriptions or UI.
    /// </summary>
    [Obsolete("Use Game.Client.UI.Social.ClientSocialEconomyUI and ClientTradeStorageUI.")]
    [DisallowMultipleComponent]
    public sealed class ClientSocialEconomyUI : MonoBehaviour
    {
        private void Awake() => enabled = false;
    }
}
