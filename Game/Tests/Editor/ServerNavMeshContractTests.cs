using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class ServerNavMeshContractTests
    {
        [Test]
        public void ServerMapFormat_IsVersion5_WithDetourAndSharedWorldManifests()
        {
            Assert.That(ServerMapFormat.Version, Is.EqualTo(5));

            var nav = new ServerNavMeshInfo();
            Assert.That(nav.formatVersion, Is.EqualTo(ServerNavMeshInfo.CurrentFormatVersion));
            Assert.That(nav.builder, Is.EqualTo("DotRecast"));
            Assert.That(nav.agentRadius, Is.EqualTo(0.35f).Within(0.0001f));
            Assert.That(nav.agentHeight, Is.EqualTo(1.80f).Within(0.0001f));
            Assert.That(nav.agentMaxClimb, Is.EqualTo(0.40f).Within(0.0001f));
            Assert.That(nav.agentMaxSlope, Is.EqualTo(50f).Within(0.0001f));

            var shared = new ServerSharedWorldInfo();
            Assert.That(shared.formatVersion, Is.EqualTo(SharedWorldFormat.Version));
            Assert.That(SharedWorldFormat.Version, Is.EqualTo(1));
        }
    }
}
