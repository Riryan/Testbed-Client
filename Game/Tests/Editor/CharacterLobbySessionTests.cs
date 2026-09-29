using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Characters;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CharacterLobbySessionTests
    {
        private static CharacterPersistenceRecord Record(long account, long character, string name) =>
            new CharacterPersistenceRecord(
                new AccountId(account),
                new CharacterId(character),
                name,
                new CharacterLocationState("test-map", "", new WorldPosition(1f, 2f, 3f), 90f),
                0);

        private static PlayerSessionService CreateService(params CharacterPersistenceRecord[] records) =>
            new PlayerSessionService(
                new PlayerSessionRegistry(),
                new CharacterService(
                    new InMemoryCharacterRepository(records),
                    new CharacterValidator(),
                    new CharacterRuntimeFactory()),
                new InMemoryCharacterLeaseService(),
                new DirtyPlayerTracker());

        private static PlayerSession OpenLobby(PlayerSessionService service, ConnectionKey connection, long account)
        {
            PlayerSession session = service.Open(connection);
            Assert.NotNull(session);
            Assert.IsTrue(service.BeginAuthentication(connection));
            Assert.IsTrue(service.CompleteAuthentication(connection, new AccountId(account)));
            Assert.AreEqual(PlayerSessionState.CharacterLobby, session.State);
            return session;
        }

        [Test]
        public async Task ExactHandle_CharacterList_ReturnsOnlyAuthenticatedAccountsCharacters()
        {
            PlayerSessionService service = CreateService(
                Record(1, 101, "One"),
                Record(1, 102, "Two"),
                Record(2, 201, "Other"));
            PlayerSession session = OpenLobby(service, new ConnectionKey(401), 1);

            CharacterListResult result = await service.GetCharacterListAsync(
                session.Handle,
                CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.Characters.Length);
            Assert.AreEqual(101, result.Characters[0].CharacterId.Value);
            Assert.AreEqual(102, result.Characters[1].CharacterId.Value);
        }

        [Test]
        public async Task ExactHandle_CharacterSelect_LoadsButDoesNotSpawnOrEnterWorld()
        {
            PlayerSessionService service = CreateService(Record(3, 301, "Three"));
            PlayerSession session = OpenLobby(service, new ConnectionKey(402), 3);

            CharacterSelectResult result = await service.SelectCharacterAsync(
                session.Handle,
                new CharacterId(301),
                CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            Assert.NotNull(session.Runtime);
        }

        [Test]
        public async Task StaleExactHandle_CannotListOrSelectOnReplacementGeneration()
        {
            PlayerSessionService service = CreateService(
                Record(4, 401, "Old"),
                Record(5, 501, "Replacement"));
            var connection = new ConnectionKey(403);

            PlayerSession oldSession = OpenLobby(service, connection, 4);
            PlayerSessionHandle stale = oldSession.Handle;
            Assert.IsTrue(service.BeginDisconnect(stale));
            Assert.IsTrue(service.Close(stale));

            PlayerSession replacement = OpenLobby(service, connection, 5);
            Assert.AreNotEqual(stale.SessionId, replacement.SessionId);

            CharacterListResult list = await service.GetCharacterListAsync(stale, CancellationToken.None);
            CharacterSelectResult select = await service.SelectCharacterAsync(
                stale,
                new CharacterId(501),
                CancellationToken.None);

            Assert.IsFalse(list.Success);
            Assert.IsFalse(select.Success);
            Assert.AreEqual(CharacterSelectFailure.SessionNotFound, select.Failure);
            Assert.AreEqual(PlayerSessionState.CharacterLobby, replacement.State);
            Assert.IsFalse(replacement.HasSelectedCharacter);
        }
    }
}
