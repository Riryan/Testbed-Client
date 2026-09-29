using Game.Server.Application.Connections;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Identity;
using Game.Shared.Sessions;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class PlayerWorldAdmissionTests
    {
        private static PlayerSession CreateAwaitingWorldEntrySession()
        {
            var session = new PlayerSession(
                new ConnectionKey(4200),
                PlayerSessionId.New());
            var accountId = new AccountId(44);
            var characterId = new CharacterId(55);

            Assert.IsTrue(session.TryBeginAuthentication());
            Assert.IsTrue(session.TryCompleteAuthentication(accountId));
            Assert.IsTrue(session.TryBeginCharacterLoad(characterId));

            var runtime = new PlayerRuntime(
                accountId,
                characterId,
                session.SessionId,
                new CharacterState("AdmissionTest"),
                new CharacterLocationState(
                    "test-map",
                    string.Empty,
                    new WorldPosition(1f, 2f, 3f),
                    90f),
                0);

            Assert.IsTrue(session.TryAttachLoadedRuntime(runtime));
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            return session;
        }

        [Test]
        public void WorldAdmission_RejectsConnectedAndCharacterLobbySessions()
        {
            var session = new PlayerSession(
                new ConnectionKey(4201),
                PlayerSessionId.New());

            Assert.IsFalse(PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(session));

            Assert.IsTrue(session.TryBeginAuthentication());
            Assert.IsTrue(session.TryCompleteAuthentication(new AccountId(88)));
            Assert.AreEqual(PlayerSessionState.CharacterLobby, session.State);
            Assert.IsFalse(PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(session));
        }

        [Test]
        public void WorldAdmission_AllowsOnlyLoadedAwaitingWorldEntryRuntime()
        {
            PlayerSession session = CreateAwaitingWorldEntrySession();

            Assert.IsTrue(PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(session));
        }

        [Test]
        public void WorldAdmission_StopsAfterWorldEntryOrDisconnectBegins()
        {
            PlayerSession inWorld = CreateAwaitingWorldEntrySession();
            Assert.IsTrue(inWorld.TryEnterWorld());
            Assert.IsFalse(PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(inWorld));

            PlayerSession disconnecting = CreateAwaitingWorldEntrySession();
            Assert.IsTrue(disconnecting.TryBeginDisconnect());
            Assert.IsFalse(PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(disconnecting));
        }
    }
}
