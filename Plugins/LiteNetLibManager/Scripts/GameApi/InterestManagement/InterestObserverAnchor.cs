using UnityEngine;

namespace LiteNetLibManager
{
    /// <summary>
    /// Explicit AOI anchor for a connection. Put this on the authoritative player
    /// identity so pets/owned helper objects do not expand the player's visibility.
    /// If none exists, the lowest object id owned by the connection is used.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InterestObserverAnchor : MonoBehaviour
    {
    }
}
