using System;
using UnityEngine;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Retired V2.1 compatibility shim.
    /// V3 reuses the existing authored StandaloneProgressionRow from the real Skills UI.
    /// </summary>
    [Obsolete("V3 reuses StandaloneProgressionRow.")]
    [DisallowMultipleComponent]
    public sealed class AchievementMilestoneRow : MonoBehaviour
    {
        private void Awake() => enabled = false;
    }
}
