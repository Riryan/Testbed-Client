using Game.Shared.World;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Unity-side API bridge for the canonical GroundedMove source. Unity keeps
    /// its existing standing-capsule query and baked world representation; only
    /// the standalone GameServer changes its internal candidate lookup index.
    /// The collision predicate and acceptance rules remain unchanged.
    /// </summary>
    public static class StandingCapsuleQueryExtensions
    {
        public static bool IsStandingCapsuleClearIndexed(
            this ServerCollisionWorld world,
            WorldPosition feet,
            ServerCapsule capsule,
            float maximumSlopeDegrees)
        {
            return world.IsStandingCapsuleClear(feet, capsule, maximumSlopeDegrees);
        }
    }
}
