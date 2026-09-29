using System;
using System.Collections.Generic;
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
    public sealed class CharacterSessionRuntimeTests
    {
        private static CharacterPersistenceRecord Record(long account, long character, float x = 10f) =>
            new CharacterPersistenceRecord(
                new AccountId(account),
                new CharacterId(character),
                "Test Character",
                new CharacterLocationState("test-map", "", new WorldPosition(x, 2f, 3f), 90f),
                0);

        private static PlayerSessionService CreateService(
            ICharacterRepository repository,
            PlayerSessionRegistry sessions = null,
            ICharacterLeaseService leases = null,
            DirtyPlayerTracker dirty = null)
        {
            sessions = sessions ?? new PlayerSessionRegistry();
            leases = leases ?? new InMemoryCharacterLeaseService();
            dirty = dirty ?? new DirtyPlayerTracker();
            var characters = new CharacterService(repository, new CharacterValidator(), new CharacterRuntimeFactory());
            return new PlayerSessionService(sessions, characters, leases, dirty);
        }

        [Test]
        public void Session_StateMachine_RejectsOutOfOrderWorldEntry()
        {
            var session = new PlayerSession(new ConnectionKey(1), PlayerSessionId.New());

            Assert.AreEqual(PlayerSessionState.Connected, session.State);
            Assert.IsFalse(session.TryEnterWorld());
            Assert.IsTrue(session.TryBeginAuthentication());
            Assert.IsTrue(session.TryCompleteAuthentication(new AccountId(1)));
            Assert.IsFalse(session.TryEnterWorld());
            Assert.AreEqual(PlayerSessionState.CharacterLobby, session.State);
        }

        [Test]
        public async Task CharacterSelection_RejectsCharacterOwnedByAnotherAccount()
        {
            var repository = new InMemoryCharacterRepository(new[] { Record(2, 100) });
            PlayerSessionService service = CreateService(repository);
            var connection = new ConnectionKey(7);

            Assert.NotNull(service.Open(connection));
            Assert.IsTrue(service.BeginAuthentication(connection));
            Assert.IsTrue(service.CompleteAuthentication(connection, new AccountId(1)));

            CharacterSelectResult result = await service.SelectCharacterAsync(connection, new CharacterId(100), CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(CharacterSelectFailure.CharacterNotFoundOrNotOwned, result.Failure);
        }

        [Test]
        public async Task CharacterSelection_LoadsRuntimeButDoesNotEnterWorldUntilCommitted()
        {
            var repository = new InMemoryCharacterRepository(new[] { Record(1, 100) });
            var registry = new PlayerSessionRegistry();
            PlayerSessionService service = CreateService(repository, registry);
            var connection = new ConnectionKey(9);

            PlayerSession session = service.Open(connection);
            Assert.IsTrue(service.BeginAuthentication(connection));
            Assert.IsTrue(service.CompleteAuthentication(connection, new AccountId(1)));

            CharacterSelectResult result = await service.SelectCharacterAsync(connection, new CharacterId(100), CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            Assert.NotNull(session.Runtime);
            Assert.IsTrue(service.CommitWorldEntry(connection));
            Assert.AreEqual(PlayerSessionState.InWorld, session.State);
        }

        [Test]
        public async Task DuplicateCharacterLease_IsRejectedAcrossSessions()
        {
            var repository = new InMemoryCharacterRepository(new[] { Record(1, 100) });
            var leases = new InMemoryCharacterLeaseService();
            PlayerSessionService service = CreateService(repository, leases: leases);
            var c1 = new ConnectionKey(1);
            var c2 = new ConnectionKey(2);

            PlayerSession s1 = service.Open(c1);
            PlayerSession s2 = service.Open(c2);
            Assert.IsTrue(service.BeginAuthentication(c1));
            Assert.IsTrue(service.CompleteAuthentication(c1, new AccountId(1)));

            // Different account cannot own the character, but lease rejection happens first.
            Assert.IsTrue(service.BeginAuthentication(c2));
            Assert.IsTrue(service.CompleteAuthentication(c2, new AccountId(2)));

            CharacterSelectResult first = await service.SelectCharacterAsync(c1, new CharacterId(100), CancellationToken.None);
            CharacterSelectResult second = await service.SelectCharacterAsync(c2, new CharacterId(100), CancellationToken.None);

            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
            Assert.AreEqual(CharacterSelectFailure.CharacterAlreadyActive, second.Failure);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, s1.State);
            Assert.AreEqual(PlayerSessionState.CharacterLobby, s2.State);
        }

        [Test]
        public async Task SaveThenReconnect_RestoresSamePersistentLocation()
        {
            var repository = new InMemoryCharacterRepository(new[] { Record(1, 100) });
            var dirty = new DirtyPlayerTracker();
            PlayerSessionService service = CreateService(repository, dirty: dirty);
            var saveService = new CharacterSaveService(repository);
            var firstConnection = new ConnectionKey(10);

            PlayerSession first = service.Open(firstConnection);
            service.BeginAuthentication(firstConnection);
            service.CompleteAuthentication(firstConnection, new AccountId(1));
            CharacterSelectResult selected = await service.SelectCharacterAsync(firstConnection, new CharacterId(100), CancellationToken.None);
            Assert.IsTrue(selected.Success);

            var moved = new CharacterLocationState("test-map", "", new WorldPosition(44f, 5f, 6f), 180f);
            Assert.IsTrue(first.Runtime.UpdateLocation(moved));
            Assert.AreEqual(1, dirty.DirtyCount);
            Assert.IsTrue(await saveService.SaveAsync(first.Runtime, CancellationToken.None));
            dirty.RefreshAfterSave(first.Runtime);
            Assert.AreEqual(0, dirty.DirtyCount);

            Assert.IsTrue(service.BeginDisconnect(firstConnection));
            Assert.IsTrue(service.Close(firstConnection));

            var secondConnection = new ConnectionKey(11);
            PlayerSession second = service.Open(secondConnection);
            service.BeginAuthentication(secondConnection);
            service.CompleteAuthentication(secondConnection, new AccountId(1));
            CharacterSelectResult reconnect = await service.SelectCharacterAsync(secondConnection, new CharacterId(100), CancellationToken.None);

            Assert.IsTrue(reconnect.Success);
            Assert.AreEqual(moved, second.Runtime.Location);
            Assert.AreNotEqual(first.SessionId, second.SessionId);
        }

        [Test]
        public void DirtyAcknowledge_DoesNotClearNewerMutation()
        {
            CharacterPersistenceRecord record = Record(1, 100);
            PlayerRuntime runtime = new CharacterRuntimeFactory().Create(record, PlayerSessionId.New());

            runtime.UpdateLocation(new CharacterLocationState("test-map", "", new WorldPosition(20f, 2f, 3f), 90f));
            long capturedRevision = runtime.Revision;
            runtime.UpdateLocation(new CharacterLocationState("test-map", "", new WorldPosition(21f, 2f, 3f), 90f));

            Assert.IsFalse(runtime.AcknowledgePersisted(capturedRevision));
            Assert.IsTrue(runtime.IsDirty);
        }
    }
}
