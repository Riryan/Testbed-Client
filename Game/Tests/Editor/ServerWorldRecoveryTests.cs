using Game.Server.Application.Spawning;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Shared.Actors;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class ServerWorldRecoveryTests
    {
        [Test]
        public void InvalidEntry_UsesNearestPlayerRecoverySpawn()
        {
            var map = FlatMapWithSpawn();
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var invalid = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(100f, -100f, 100f), 0f);

            Assert.That(service.TryValidateEntryLocation(invalid, out CharacterLocationState recovered, out string detail), Is.True);
            Assert.That(recovered.Position.X, Is.EqualTo(1f).Within(0.05f));
            StringAssert.Contains("recovered", detail);
        }


        [Test]
        public void FirstSpawn_ResolvesAndGroundsCanonicalAnchor()
        {
            ServerMapSnapshot map = FlatMapWithSpawn(ServerSpawnKind.PlayerFirstSpawn, 0.4f, AuthoritativeActorKind.Player);
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var configured = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(3f, 5f, 3f), 15f);

            Assert.That(service.TryResolveFirstSpawn(configured, out CharacterLocationState resolved, out string detail), Is.True, detail);
            Assert.That(resolved.Position.X, Is.EqualTo(1f).Within(0.01f));
            Assert.That(resolved.Position.Y, Is.EqualTo(0f).Within(0.01f));
            Assert.That(resolved.Position.Z, Is.EqualTo(0f).Within(0.01f));
            Assert.That(resolved.YawDegrees, Is.EqualTo(90f).Within(0.01f));
        }

        [Test]
        public void FirstSpawn_InvalidHeight_ReportsExactAnchorRejection()
        {
            ServerMapSnapshot map = FlatMapWithSpawn(ServerSpawnKind.PlayerFirstSpawn, 1.4f, AuthoritativeActorKind.Player);
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var configured = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(0f, 0f, 0f), 0f);

            Assert.That(service.TryResolveFirstSpawn(configured, out _, out string detail), Is.False);
            StringAssert.Contains("all 1 enabled PlayerFirstSpawn anchor(s) are invalid", detail);
            StringAssert.Contains("Recovery", detail);
            StringAssert.Contains("authored=(1,1.4,0)", detail);
            StringAssert.Contains("maxGroundSnap=0.65m", detail);
        }

        [Test]
        public void FirstSpawn_WrongActorKind_FailsClosed()
        {
            ServerMapSnapshot map = FlatMapWithSpawn(ServerSpawnKind.PlayerFirstSpawn, 0f, AuthoritativeActorKind.Npc);
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var configured = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(0f, 0f, 0f), 0f);

            Assert.That(service.TryResolveFirstSpawn(configured, out _, out string detail), Is.False);
            StringAssert.Contains("actorKind is Npc; Player is required", detail);
        }

        [Test]
        public void FirstSpawn_MissingMap_FailsClosed()
        {
            var service = new ServerSpawnService(new ServerMapCatalog(System.Array.Empty<ServerMapSnapshot>()));
            var configured = new CharacterLocationState("missing", string.Empty, new WorldPosition(0f, 0f, 0f), 0f);

            Assert.That(service.TryResolveFirstSpawn(configured, out _, out string detail), Is.False);
            StringAssert.Contains("authoritative first-spawn map data is unavailable", detail);
        }

        [Test]
        public void EntryMissingMap_FailsClosedWhenAuthoritativeMapsAreLoaded()
        {
            ServerMapSnapshot map = FlatMapWithSpawn();
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var requested = new CharacterLocationState("another_map", string.Empty, new WorldPosition(0f, 0f, 0f), 0f);

            Assert.That(service.TryValidateEntryLocation(requested, out _, out string detail), Is.False);
            StringAssert.Contains("authoritative entry map data is unavailable", detail);
        }

        [Test]
        public void OutsideBounds_DetectsFallAndHorizontalEscape()
        {
            var map = FlatMapWithSpawn();
            var service = new ServerSpawnService(new ServerMapCatalog(new[] { map }));
            var below = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(0f, -20f, 0f), 0f);
            var beyond = new CharacterLocationState(map.mapId, string.Empty, new WorldPosition(50f, 0f, 50f), 0f);

            Assert.That(service.IsOutsideRecoveryBounds(below, out _), Is.True);
            Assert.That(service.IsOutsideRecoveryBounds(beyond, out _), Is.True);
        }

        private static ServerMapSnapshot FlatMapWithSpawn() =>
            FlatMapWithSpawn(ServerSpawnKind.PlayerRespawn, 0f, AuthoritativeActorKind.Player);

        private static ServerMapSnapshot FlatMapWithSpawn(
            ServerSpawnKind kind,
            float spawnY,
            AuthoritativeActorKind actorKind) => new ServerMapSnapshot
        {
            mapId = "recovery_test",
            collisionTriangles = new[]
            {
                Triangle(-5f, 0f, -5f, 5f, 0f, -5f, 5f, 0f, 5f),
                Triangle(-5f, 0f, -5f, 5f, 0f, 5f, -5f, 0f, 5f),
            },
            spawnAnchors = new[]
            {
                new ServerSpawnAnchor
                {
                    stableId = 1, label = "Recovery", kind = kind,
                    actorKind = actorKind, pose = new ServerPose(1f, spawnY, 0f, 90f),
                    enabled = true, capsuleRadius = 0.35f, capsuleHeight = 1.8f, maximumGroundSnap = 0.65f,
                }
            }
        };

        private static ServerCollisionTriangle Triangle(float ax,float ay,float az,float bx,float by,float bz,float cx,float cy,float cz) => new ServerCollisionTriangle
        {
            surfaceId=1, ax=ax, ay=ay, az=az, bx=bx, by=by, bz=bz, cx=cx, cy=cy, cz=cz,
            normalX=0f, normalY=1f, normalZ=0f, flags=ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground,
        };
    }
}
