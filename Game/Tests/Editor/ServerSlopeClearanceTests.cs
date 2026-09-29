using Game.Server.Application.World;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class ServerSlopeClearanceTests
    {
        [Test]
        public void StandingCapsule_DoesNotTreatLegalSlopeAsWall()
        {
            // 3m run, 1m rise ~= 18.4 degrees.
            ServerCollisionTriangle ramp = Triangle(
                0f, 0f, -2f,
                3f, 1f, -2f,
                3f, 1f, 2f,
                ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground);

            ServerCollisionTriangle ramp2 = Triangle(
                0f, 0f, -2f,
                3f, 1f, 2f,
                0f, 0f, 2f,
                ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground);

            var world = new ServerCollisionWorld(new ServerMapSnapshot
            {
                mapId = "slope_test",
                collisionTriangles = new[] { ramp, ramp2 },
            });

            var capsule = new ServerCapsule(0.35f, 1.8f);
            var feet = new WorldPosition(1.5f, 0.5f, 0f);

            Assert.That(
                world.IsStandingCapsuleClear(feet, capsule, 50f),
                Is.True);
        }

        [Test]
        public void StandingCapsule_StillRejectsVerticalWall()
        {
            ServerCollisionTriangle floor = Triangle(
                -2f, 0f, -2f,
                 2f, 0f, -2f,
                 2f, 0f,  2f,
                ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground);

            ServerCollisionTriangle floor2 = Triangle(
                -2f, 0f, -2f,
                 2f, 0f,  2f,
                -2f, 0f,  2f,
                ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground);

            ServerCollisionTriangle wall = Triangle(
                0.20f, 0f, -1f,
                0.20f, 2f, -1f,
                0.20f, 2f,  1f,
                ServerSurfaceFlags.None);

            ServerCollisionTriangle wall2 = Triangle(
                0.20f, 0f, -1f,
                0.20f, 2f,  1f,
                0.20f, 0f,  1f,
                ServerSurfaceFlags.None);

            var world = new ServerCollisionWorld(new ServerMapSnapshot
            {
                mapId = "wall_test",
                collisionTriangles = new[] { floor, floor2, wall, wall2 },
            });

            var capsule = new ServerCapsule(0.35f, 1.8f);
            var feet = new WorldPosition(0f, 0f, 0f);

            Assert.That(
                world.IsStandingCapsuleClear(feet, capsule, 50f),
                Is.False);
        }

        private static ServerCollisionTriangle Triangle(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz,
            ServerSurfaceFlags flags)
        {
            float ux = bx - ax;
            float uy = by - ay;
            float uz = bz - az;
            float vx = cx - ax;
            float vy = cy - ay;
            float vz = cz - az;

            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float len = System.MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.00001f)
            {
                nx /= len;
                ny /= len;
                nz /= len;
            }

            return new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = ax, ay = ay, az = az,
                bx = bx, by = by, bz = bz,
                cx = cx, cy = cy, cz = cz,
                normalX = nx,
                normalY = ny,
                normalZ = nz,
                flags = flags,
            };
        }
    }
}
