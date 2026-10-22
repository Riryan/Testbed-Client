using System;
using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Shared.World;

namespace Player.Networking
{
    /// <summary>
    /// Thin adapter over the EXISTING Game.Server.Application collision world.
    /// Player.Networking already references that assembly; Player.Client already
    /// references Player.Networking. No assembly definition or network change.
    /// </summary>
    public sealed class SharedMovementQueries
    {
        private readonly ServerCollisionWorld _world;

        public SharedMovementQueries(SharedWorldSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            ServerCollisionTriangle[] support = snapshot.supportTriangles ??
                                               Array.Empty<ServerCollisionTriangle>();
            if (support.Length == 0)
                throw new InvalidOperationException("Shared world has no baked support geometry.");
            _world = new ServerCollisionWorld(new ServerMapSnapshot
            {
                mapId = snapshot.mapId,
                instanceId = snapshot.instanceId,
                movementTriangles = support,
                collisionTriangles = snapshot.collisionTriangles ??
                                     Array.Empty<ServerCollisionTriangle>(),
                dynamicBlockers = snapshot.dynamicBlockers ??
                                  Array.Empty<ServerDynamicBlocker>()
            });
            // Unity prepares the same immutable clearance proof during the
            // existing asynchronous shared-world load, before normal prediction.
            GroundedMove.RegisterWorld(_world, snapshot.collisionTriangles,
                snapshot.dynamicBlockers);
        }

        public bool TryResolveGroundedMove(
            WorldPosition start, float deltaX, float deltaZ,
            float radius, float stepUp, float snapDown,
            out WorldPosition resolved, out bool blocked)
        {
            resolved = GroundedMove.Resolve(_world, start, deltaX, deltaZ,
                new ServerCapsule(radius, 1.8f), stepUp, snapDown, 50f,
                out blocked);
            return true;
        }

        public bool TryFindSupportHeight(float x, float z, float referenceY,
            float probeUp, float probeDown, out float y)
        {
            bool ok = _world.TryFindGround(new WorldPosition(x, referenceY, z),
                0.35f, probeUp, probeDown, 50f, out ServerGroundHit ground);
            y = ok ? ground.Position.Y : referenceY;
            return ok;
        }

        public bool TrySetDynamicBlockerEnabled(long id, bool enabled) =>
            _world.TrySetDynamicBlockerEnabled(id, enabled);
    }
}
