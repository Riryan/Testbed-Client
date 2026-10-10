using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class ServerMovementFoundationTests
    {
        [Test]
        public void GroundedMotor_FollowsBakedMovementSlopeWithoutJumping()
        {
            ServerCollisionTriangle[] movement =
            {
                Triangle(-3f, 0f, -2f, 0f, 0f, -2f, 0f, 0f, 2f, true),
                Triangle(-3f, 0f, -2f, 0f, 0f, 2f, -3f, 0f, 2f, true),
                Triangle(0f, 0f, -2f, 3f, 1f, -2f, 3f, 1f, 2f, true),
                Triangle(0f, 0f, -2f, 3f, 1f, 2f, 0f, 0f, 2f, true),
            };
            var world = World("movement_test", movement, movement);

            var motor = new ServerCharacterMotor();
            var state = new CharacterMotorState(new WorldPosition(-1f, 0f, 0f));

            for (int i = 0; i < 14; ++i)
                motor.Tick(state, new CharacterMovementIntent(1f, 0f), 0.05f, world);

            Assert.That(state.Position.X, Is.GreaterThan(1.5f));
            Assert.That(state.Position.Y, Is.GreaterThan(0.35f));
            Assert.That(state.Grounded, Is.True);
        }

        [Test]
        public void GroundedMotor_StopsAtBakedMovementSurfaceEdge()
        {
            ServerCollisionTriangle[] movement =
            {
                Triangle(-3f, 0f, -2f, 0.5f, 0f, -2f, 0.5f, 0f, 2f, true),
                Triangle(-3f, 0f, -2f, 0.5f, 0f, 2f, -3f, 0f, 2f, true),
            };
            ServerCollisionTriangle[] collision =
            {
                movement[0],
                movement[1],
                Triangle(0.5f, 0f, -2f, 3f, 0f, -2f, 3f, 0f, 2f, false),
                Triangle(0.5f, 0f, -2f, 3f, 0f, 2f, 0.5f, 0f, 2f, false),
            };
            var world = World("edge_test", movement, collision);

            var motor = new ServerCharacterMotor();
            var state = new CharacterMotorState(new WorldPosition(-0.5f, 0f, 0f));

            for (int i = 0; i < 20; ++i)
                motor.Tick(state, new CharacterMovementIntent(1f, 0f), 0.05f, world);

            Assert.That(state.Position.X, Is.LessThan(0.9f));
            Assert.That(state.Grounded, Is.True);
            Assert.That(state.Position.Y, Is.EqualTo(0f).Within(0.02f));
        }

        [Test]
        public void GroundedMotor_TraversesBakedStairRampWhileRawRisersRemainCollision()
        {
            ServerCollisionTriangle[] movement =
            {
                Triangle(0f, 0f, -1f, 2f, 0.4f, -1f, 2f, 0.4f, 1f, true),
                Triangle(0f, 0f, -1f, 2f, 0.4f, 1f, 0f, 0f, 1f, true),
            };
            ServerCollisionTriangle[] collision =
            {
                movement[0],
                movement[1],
                Triangle(0.5f, 0f, -1f, 0.5f, 0.1f, -1f, 0.5f, 0.1f, 1f, false),
                Triangle(0.5f, 0f, -1f, 0.5f, 0.1f, 1f, 0.5f, 0f, 1f, false),
                Triangle(1.0f, 0.1f, -1f, 1.0f, 0.2f, -1f, 1.0f, 0.2f, 1f, false),
                Triangle(1.0f, 0.1f, -1f, 1.0f, 0.2f, 1f, 1.0f, 0.1f, 1f, false),
                Triangle(1.5f, 0.2f, -1f, 1.5f, 0.3f, -1f, 1.5f, 0.3f, 1f, false),
                Triangle(1.5f, 0.2f, -1f, 1.5f, 0.3f, 1f, 1.5f, 0.2f, 1f, false),
            };
            var world = World("stair_riser_test", movement, collision);

            var motor = new ServerCharacterMotor();
            var state = new CharacterMotorState(new WorldPosition(0.1f, 0.02f, 0f));

            for (int i = 0; i < 18; ++i)
                motor.Tick(state, new CharacterMovementIntent(1f, 0f), 0.05f, world);

            Assert.That(state.Position.X, Is.GreaterThan(1.6f));
            Assert.That(state.Position.Y, Is.GreaterThan(0.28f));
            Assert.That(state.Grounded, Is.True);
        }

        [Test]
        public void GroundedMotor_DoesNotCrossGapRemovedByMovementBake()
        {
            ServerCollisionTriangle[] movement =
            {
                Triangle(-2f, 0f, -1f, 0f, 0f, -1f, 0f, 0f, 1f, true),
                Triangle(-2f, 0f, -1f, 0f, 0f, 1f, -2f, 0f, 1f, true),
                Triangle(1f, 0f, -1f, 2f, 0f, -1f, 2f, 0f, 1f, true),
                Triangle(1f, 0f, -1f, 2f, 0f, 1f, 1f, 0f, 1f, true),
            };
            ServerCollisionTriangle[] collision =
            {
                Triangle(-2f, 0f, -1f, 2f, 0f, -1f, 2f, 0f, 1f, true),
                Triangle(-2f, 0f, -1f, 2f, 0f, 1f, -2f, 0f, 1f, true),
                Triangle(0.5f, 0f, -1f, 0.5f, 1.5f, -1f, 0.5f, 1.5f, 1f, false),
                Triangle(0.5f, 0f, -1f, 0.5f, 1.5f, 1f, 0.5f, 0f, 1f, false),
            };
            var world = World("wall_gap_test", movement, collision);

            var motor = new ServerCharacterMotor();
            var state = new CharacterMotorState(new WorldPosition(-0.5f, 0f, 0f));

            for (int i = 0; i < 20; ++i)
                motor.Tick(state, new CharacterMovementIntent(1f, 0f), 0.05f, world);

            Assert.That(state.Position.X, Is.LessThan(0.2f));
            Assert.That(state.Grounded, Is.True);
        }

        [Test]
        public void FallingMotor_StillUsesRawCollision()
        {
            ServerCollisionTriangle[] movement =
            {
                Triangle(10f, 0f, 10f, 12f, 0f, 10f, 12f, 0f, 12f, true),
                Triangle(10f, 0f, 10f, 12f, 0f, 12f, 10f, 0f, 12f, true),
            };
            ServerCollisionTriangle[] collision =
            {
                Triangle(-3f, 0f, -3f, 3f, 0f, -3f, 3f, 0f, 3f, false),
                Triangle(-3f, 0f, -3f, 3f, 0f, 3f, -3f, 0f, 3f, false),
            };
            var world = World("solid_floor_test", movement, collision);

            var motor = new ServerCharacterMotor();
            var state = new CharacterMotorState(new WorldPosition(0f, 2f, 0f))
            {
                Grounded = false,
                Mode = Game.Shared.Actors.ActorMovementMode.Falling,
                VerticalVelocity = -2f,
            };

            for (int i = 0; i < 40; ++i)
                motor.Tick(state, new CharacterMovementIntent(0f, 0f), 0.05f, world);

            Assert.That(state.Position.Y, Is.EqualTo(0f).Within(0.03f));
            Assert.That(state.Grounded, Is.True);
        }

        private static ServerCollisionWorld World(
            string mapId,
            ServerCollisionTriangle[] movement,
            ServerCollisionTriangle[] collision)
        {
            return new ServerCollisionWorld(new ServerMapSnapshot
            {
                mapId = mapId,
                movementTriangles = movement,
                collisionTriangles = collision,
            });
        }

        private static ServerCollisionTriangle Triangle(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz,
            bool walkable)
        {
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float length = System.MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 0.00001f)
            {
                nx /= length;
                ny /= length;
                nz /= length;
            }
            if (ny < 0f)
            {
                nx = -nx;
                ny = -ny;
                nz = -nz;
            }

            return new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = ax, ay = ay, az = az,
                bx = bx, by = by, bz = bz,
                cx = cx, cy = cy, cz = cz,
                normalX = nx, normalY = ny, normalZ = nz,
                flags = walkable
                    ? ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground
                    : ServerSurfaceFlags.Ground,
            };
        }
    }
}
