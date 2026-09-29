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
    public sealed class ExternalPlayerWorldLifecycleTests
    {
        private sealed class NeverCalledWorldAdapter : IPlayerWorldAdapter
        {
            public int EnterCalls { get; private set; }
            public int ReadCalls { get; private set; }
            public int LeaveCalls { get; private set; }

            public bool TryEnter(PlayerRuntime runtime, ConnectionKey connection, out PlayerWorldHandle handle)
            {
                EnterCalls++;
                handle = default(PlayerWorldHandle);
                return false;
            }

            public bool TryReadLocation(PlayerWorldHandle handle, out CharacterLocationState location)
            {
                ReadCalls++;
                location = default(CharacterLocationState);
                return false;
            }

            public bool TryLeave(PlayerWorldHandle handle)
            {
                LeaveCalls++;
                return false;
            }
        }

        private static CharacterPersistenceRecord Record(long account, long character, float x = 10f) =>
            new CharacterPersistenceRecord(
                new AccountId(account),
                new CharacterId(character),
                $"Character {character}",
                new CharacterLocationState("test-map", "", new WorldPosition(x, 2f, 3f), 90f),
                0);

        private static PlayerSessionService CreateSessionService(
            ICharacterRepository repository,
            PlayerSessionRegistry registry = null)
        {
            registry = registry ?? new PlayerSessionRegistry();
            return new PlayerSessionService(
                registry,
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
        public async Task CanonicalHostEnter_AdoptsExistingWorldHandle_WithoutCallingAdapterSpawn()
        {
            var sessions = CreateSessionService(
                new InMemoryCharacterRepository(new[] { Record(1, 100) }));
            var adapter = new NeverCalledWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                adapter);
            PlayerSession session = await LoadSessionAsync(sessions, new ConnectionKey(50), 1, 100);

            PlayerWorldLifecycleResult result = lifecycle.AdoptExternalEnter(
                session.Handle,
                new PlayerWorldHandle(7001));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Entered, result.Status);
            Assert.AreEqual(PlayerSessionState.InWorld, session.State);
            Assert.AreEqual(1, lifecycle.ActiveBindingCount);
            Assert.AreEqual(0, adapter.EnterCalls);
            Assert.AreEqual(0, adapter.LeaveCalls);
        }

        [Test]
        public async Task CanonicalHostLeave_CapturesProvidedLocation_WithoutCallingAdapterDestroy()
        {
            var sessions = CreateSessionService(
                new InMemoryCharacterRepository(new[] { Record(2, 200) }));
            var adapter = new NeverCalledWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                adapter);
            PlayerSession session = await LoadSessionAsync(sessions, new ConnectionKey(51), 2, 200);
            Assert.IsTrue(lifecycle.AdoptExternalEnter(session.Handle, new PlayerWorldHandle(7002)).Success);
            Assert.IsTrue(sessions.BeginDisconnect(session.Handle));

            var finalLocation = new CharacterLocationState(
                "test-map",
                "",
                new WorldPosition(55f, 4f, -9f),
                135f);

            PlayerWorldLifecycleResult result = lifecycle.CompleteExternalLeave(
                session.Handle,
                finalLocation);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Left, result.Status);
            Assert.AreEqual(finalLocation, session.Runtime.Location);
            Assert.AreEqual(0, lifecycle.ActiveBindingCount);
            Assert.AreEqual(0, adapter.ReadCalls);
            Assert.AreEqual(0, adapter.LeaveCalls);
        }

        [Test]
        public async Task CanonicalHostEnter_StaleGenerationCannotAdoptReplacementSession()
        {
            var registry = new PlayerSessionRegistry();
            var repository = new InMemoryCharacterRepository(new[]
            {
                Record(3, 300),
                Record(4, 400),
            });
            var sessions = CreateSessionService(repository, registry);
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                new NeverCalledWorldAdapter());
            var connection = new ConnectionKey(52);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 3, 300);
            PlayerSessionHandle staleHandle = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(staleHandle));
            Assert.IsTrue(sessions.Close(staleHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 4, 400);

            PlayerWorldLifecycleResult stale = lifecycle.AdoptExternalEnter(
                staleHandle,
                new PlayerWorldHandle(7003));

            Assert.IsFalse(stale.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.EnterStaleSessionRejected, stale.Status);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, replacement.State);
            Assert.AreEqual(0, lifecycle.ActiveBindingCount);
        }

        [Test]
        public async Task CanonicalHostLeave_StaleGenerationCannotRemoveReplacementBinding()
        {
            var registry = new PlayerSessionRegistry();
            var repository = new InMemoryCharacterRepository(new[]
            {
                Record(5, 500),
                Record(6, 600),
            });
            var sessions = CreateSessionService(repository, registry);
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                new NeverCalledWorldAdapter());
            var connection = new ConnectionKey(53);

            PlayerSession oldSession = await LoadSessionAsync(sessions, connection, 5, 500);
            PlayerSessionHandle staleHandle = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(staleHandle));
            Assert.IsTrue(sessions.Close(staleHandle));

            PlayerSession replacement = await LoadSessionAsync(sessions, connection, 6, 600);
            Assert.IsTrue(lifecycle.AdoptExternalEnter(replacement.Handle, new PlayerWorldHandle(7004)).Success);
            Assert.IsTrue(sessions.BeginDisconnect(replacement.Handle));

            PlayerWorldLifecycleResult stale = lifecycle.CompleteExternalLeave(staleHandle);

            Assert.IsFalse(stale.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.LeaveStaleSessionRejected, stale.Status);
            Assert.AreEqual(1, lifecycle.ActiveBindingCount);
        }
    }
}
