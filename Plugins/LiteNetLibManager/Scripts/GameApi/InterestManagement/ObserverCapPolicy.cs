using UnityEngine;

namespace LiteNetLibManager
{
    /// <summary>
    /// Inspector-configurable soft observer-cap override.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ObserverCapPolicy :
        MonoBehaviour,
        IMMOObserverCapPolicy
    {
        [Min(0)] public int observerCapOverride;
        public int ObserverCapOverride => observerCapOverride;
    }
}
