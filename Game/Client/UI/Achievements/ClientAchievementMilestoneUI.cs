using System;
using UnityEngine;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Retired V2.1 compatibility shim. V3 uses the actual StandaloneClientUI prefab.
    /// </summary>
    [Obsolete("Use StandaloneAchievementMilestoneWindow.")]
    [DisallowMultipleComponent]
    public sealed class ClientAchievementMilestoneUI : MonoBehaviour
    {
        private void Awake() => enabled = false;
    }
}
