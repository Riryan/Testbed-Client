using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class ServerMapIdTests
    {
        [TestCase("MMOPlayerEntityTest", "mmoplayerentitytest")]
        [TestCase(" MMO Player Entity Test ", "mmo_player_entity_test")]
        [TestCase("APT STYLE 001", "apt_style_001")]
        public void MapId_NormalizesToCanonicalForm(string source, string expected)
        {
            Assert.That(ServerMapId.Normalize(source), Is.EqualTo(expected));
        }

        [Test]
        public void PersistedLegacyMapCasing_ResolvesBakedCollisionWorld()
        {
            var map = new ServerMapSnapshot
            {
                formatVersion = ServerMapFormat.Version,
                mapId = "mmoplayerentitytest",
                instanceId = string.Empty,
            };

            var catalog = new ServerMapCatalog(new[] { map });
            var location = new CharacterLocationState(
                "MMOPlayerEntityTest",
                string.Empty,
                new WorldPosition(0f, 0f, 0f),
                0f);

            Assert.That(location.MapId, Is.EqualTo("mmoplayerentitytest"));
            Assert.That(catalog.TryGetCollisionWorld(location.MapId, location.InstanceId, out _), Is.True);
        }
    }
}
