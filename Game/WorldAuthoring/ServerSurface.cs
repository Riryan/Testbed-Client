using Game.Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring
{
    [AddComponentMenu("MMO/Server Surface")]
    public sealed class ServerSurface : MonoBehaviour
    {
        [Tooltip("Metadata attached to exported server collision. The Recast server NavMesh derives normal walkability from collider geometry and agent settings. Water-only surfaces are omitted from humanoid navigation.")]
        public ServerSurfaceFlags flags = ServerSurfaceFlags.None;
    }
}
