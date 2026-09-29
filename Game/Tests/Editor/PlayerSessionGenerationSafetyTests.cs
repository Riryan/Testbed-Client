using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Characters;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class PlayerSessionGenerationSafetyTests
    {
        private sealed class FakeWorldAdapter : IPlayerWorldAdapter
        {
            private ulong _nextHandle = 1;
            public int EnterCalls { get; private set; }
            public int LeaveCalls { get; private set; }

            public bool TryEnter(PlayerRuntime runtime, ConnectionKey connection, out PlayerWorldHandle handle)
            {
                EnterCalls++;
                handle = new PlayerWorldHandle(_nextHandle++);
                return true;
            }

            public bool TryReadLocation(PlayerWorldHandle handle, out CharacterLocationState location)
            {
                location = default(CharacterLocationState);
                return false;
            }

            public bool TryLeave(PlayerWorldHandle handle)
            {
                LeaveCalls++;
                return true;
            }
        }

        private static CharacterPersistenceRecord Record(long account, long character, float x) =>
            new CharacterPersistenceRecord(
                new AccountId(account),
                new CharacterId(character),
                $"Character {character}",
                new CharacterLocationState("test-map", "", new WorldPosition(x, 2f, 3f), 90f),
                0);

        private static PlayerSessionService CreateSessionService(params CharacterPersistenceRecord[] records)
        {
            var repository = new InMemoryCharacterRepository(records);
            return new PlayerSessionService(
                new PlayerSessionRegistry(),
                new CharacterService(repository, new CharacterValidator(), new CharacterRuntimeFactory()),
                new InMemoryCharacterLeaseService(),
                new DirtyPlayerTracker());
        }

        private static async Task<PlayerSession> LoadSessionAsync(
            PlayerSessionService sessions,
            ConnectionKey connection,
            long account,
            long character)
        {
            PlayerSession session = sessions.Open(connection);
            Assert.NotNull(session);
            Assert.IsTrue(sessions.BeginAuthentication(connection));
            Assert.IsTrue(sessions.CompleteAuthentication(connection, new AccountId(account)));
            CharacterSelectResult selected = await sessions.SelectCharacterAsync(
                connection,
                new CharacterId(character),
                CancellationToken.None);
            Assert.IsTrue(selected.Success);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            return session;
        }

        [Test]
        public async Task StaleQueuedEnter_CannotEnterReplacementSession_WhenConnectionKeyIsReused()
        {
            var firstRecord = Record(1, 101, 10f);
            var secondRecord = Record(2, 202, 20f);
            var sessions = CreateSessionService(firstRecord, secondRecord);
            var adapter = new FakeWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(70);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 1, 101);
            PlayerSessionHandle oldHandle = oldSession.Handle;
            Assert.IsTrue(lifecycle.TryQueueEnter(oldHandle));

            Assert.IsTrue(sessions.BeginDisconnect(oldHandle));
            Assert.IsTrue(sessions.Close(oldHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 2, 202);
            Assert.AreNotEqual(oldHandle.SessionId, replacement.SessionId);

            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult result));
            Assert.IsFalse(result.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.EnterStaleSessionRejected, result.Status);
            Assert.AreEqual(oldHandle.SessionId, result.SessionId);
            Assert.AreEqual(0, adapter.EnterCalls);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, replacement.State);
        }

        [Test]
        public async Task ReplacementSession_CanQueueEnterWhileOldGenerationCommandIsStillPending()
        {
            var firstRecord = Record(3, 303, 30f);
            var secondRecord = Record(4, 404, 40f);
            var sessions = CreateSessionService(firstRecord, secondRecord);
            var adapter = new FakeWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(71);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 3, 303);
            PlayerSessionHandle oldHandle = oldSession.Handle;
            Assert.IsTrue(lifecycle.TryQueueEnter(oldHandle));
            Assert.IsTrue(sessions.BeginDisconnect(oldHandle));
            Assert.IsTrue(sessions.Close(oldHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 4, 404);
            PlayerSessionHandle replacementHandle = replacement.Handle;

            // Generation-aware pending sets allow the replacement to queue its own work.
            Assert.IsTrue(lifecycle.TryQueueEnter(replacementHandle));
            Assert.AreEqual(2, lifecycle.PendingCommandCount);

            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult stale));
            Assert.AreEqual(PlayerWorldLifecycleStatus.EnterStaleSessionRejected, stale.Status);
            Assert.AreEqual(0, adapter.EnterCalls);

            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult current));
            Assert.IsTrue(current.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Entered, current.Status);
            Assert.AreEqual(replacementHandle.SessionId, current.SessionId);
            Assert.AreEqual(1, adapter.EnterCalls);
            Assert.AreEqual(PlayerSessionState.InWorld, replacement.State);
        }

        [Test]
        public async Task StaleDisconnectHandle_CannotDisconnectReplacementSession()
        {
            var firstRecord = Record(5, 505, 50f);
            var secondRecord = Record(6, 606, 60f);
            var sessions = CreateSessionService(firstRecord, secondRecord);
            var connection = new ConnectionKey(72);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 5, 505);
            PlayerSessionHandle oldHandle = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(oldHandle));
            Assert.IsTrue(sessions.Close(oldHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 6, 606);
            Assert.IsFalse(sessions.BeginDisconnect(oldHandle));
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, replacement.State);
        }

        [Test]
        public async Task StaleCloseHandle_CannotCloseReplacementSession()
        {
            var firstRecord = Record(7, 707, 70f);
            var secondRecord = Record(8, 808, 80f);
            var sessions = CreateSessionService(firstRecord, secondRecord);
            var connection = new ConnectionKey(73);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 7, 707);
            PlayerSessionHandle oldHandle = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(oldHandle));
            Assert.IsTrue(sessions.Close(oldHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 8, 808);
            Assert.IsFalse(sessions.Close(oldHandle));
            Assert.IsTrue(sessions.TryGetSession(replacement.Handle, out PlayerSession resolved));
            Assert.AreSame(replacement, resolved);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, replacement.State);
        }

        [Test]
        public async Task StaleCommitHandle_CannotCommitReplacementWorldEntry()
        {
            var firstRecord = Record(9, 909, 90f);
            var secondRecord = Record(10, 1001, 100f);
            var sessions = CreateSessionService(firstRecord, secondRecord);
            var connection = new ConnectionKey(74);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 9, 909);
            PlayerSessionHandle oldHandle = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(oldHandle));
            Assert.IsTrue(sessions.Close(oldHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 10, 1001);
            Assert.IsFalse(sessions.CommitWorldEntry(oldHandle));
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, replacement.State);
        }
    }
}
