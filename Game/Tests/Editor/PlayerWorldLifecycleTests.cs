using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Characters;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class PlayerWorldLifecycleTests
    {
        private sealed class FakeWorldAdapter : IPlayerWorldAdapter
        {
            private ulong _nextHandle = 1;
            private readonly HashSet<PlayerWorldHandle> _active = new HashSet<PlayerWorldHandle>();

            public bool FailEnter { get; set; }
            public bool FailLeave { get; set; }
            public int EnterCalls { get; private set; }
            public int LeaveCalls { get; private set; }
            public int ReadLocationCalls { get; private set; }
            public CharacterLocationState LocationToRead { get; set; }

            public bool TryEnter(PlayerRuntime runtime, ConnectionKey connection, out PlayerWorldHandle handle)
            {
                EnterCalls++;
                if (FailEnter)
                {
                    handle = default(PlayerWorldHandle);
                    return false;
                }

                handle = new PlayerWorldHandle(_nextHandle++);
                _active.Add(handle);
                return true;
            }

            public bool TryReadLocation(PlayerWorldHandle handle, out CharacterLocationState location)
            {
                ReadLocationCalls++;
                if (!_active.Contains(handle))
                {
                    location = default(CharacterLocationState);
                    return false;
                }

                location = LocationToRead;
                return true;
            }

            public bool TryLeave(PlayerWorldHandle handle)
            {
                LeaveCalls++;
                if (FailLeave)
                    return false;
                return _active.Remove(handle);
            }
        }

        private sealed class DelayedCharacterRepository : ICharacterRepository
        {
            private readonly CharacterPersistenceRecord _record;
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public DelayedCharacterRepository(CharacterPersistenceRecord record)
            {
                _record = record;
            }

            public void ReleaseLoad() => _release.TrySetResult(true);

            public Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(AccountId accountId, CancellationToken cancellationToken)
            {
                IReadOnlyList<CharacterSummary> list = new[]
                {
                    new CharacterSummary(_record.CharacterId, _record.Name, _record.Location.MapId),
                };
                return Task.FromResult(list);
            }

            public async Task<CharacterPersistenceRecord> LoadForAccountAsync(
                AccountId accountId,
                CharacterId characterId,
                CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(() => _release.TrySetCanceled()))
                    await _release.Task.ConfigureAwait(false);

                if (_record.AccountId != accountId || _record.CharacterId != characterId)
                    return null;
                return _record.Copy();
            }

            public Task<CharacterCreatePersistenceResult> TryCreateAsync(
                AccountId accountId,
                string name,
                CharacterLocationState initialLocation,
                CharacterAppearanceRecipe initialAppearance,
                CharacterPresentationPreferences initialPresentation,
                int maxCharactersForAccount,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(CharacterCreatePersistenceResult.Failed());
            }

            public Task SaveAsync(CharacterPersistenceRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private static CharacterPersistenceRecord Record(long account = 1, long character = 100, float x = 10f) =>
            new CharacterPersistenceRecord(
                new AccountId(account),
                new CharacterId(character),
                "Test Character",
                new CharacterLocationState("test-map", "", new WorldPosition(x, 2f, 3f), 90f),
                0);

        private static PlayerSessionService CreateSessionService(
            ICharacterRepository repository,
            PlayerSessionRegistry sessions = null,
            ICharacterLeaseService leases = null,
            DirtyPlayerTracker dirty = null)
        {
            sessions = sessions ?? new PlayerSessionRegistry();
            leases = leases ?? new InMemoryCharacterLeaseService();
            dirty = dirty ?? new DirtyPlayerTracker();
            return new PlayerSessionService(
                sessions,
                new CharacterService(repository, new CharacterValidator(), new CharacterRuntimeFactory()),
                leases,
                dirty);
        }

        private static async Task<PlayerSession> LoadSessionAsync(
            PlayerSessionService sessions,
            ConnectionKey connection,
            long account = 1,
            long character = 100)
        {
            PlayerSession session = sessions.Open(connection);
            Assert.NotNull(session);
            Assert.IsTrue(sessions.BeginAuthentication(connection));
            Assert.IsTrue(sessions.CompleteAuthentication(connection, new AccountId(account)));
            CharacterSelectResult result = await sessions.SelectCharacterAsync(
                connection,
                new CharacterId(character),
                CancellationToken.None);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            return session;
        }

        [Test]
        public async Task WorldEntry_IsQueuedUntilAuthoritativeTick()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var adapter = new FakeWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                adapter);
            var connection = new ConnectionKey(20);
            PlayerSession session = await LoadSessionAsync(sessions, connection);

            Assert.IsTrue(lifecycle.TryQueueEnter(connection));
            Assert.AreEqual(0, adapter.EnterCalls);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);

            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult processed));
            Assert.IsTrue(processed.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Entered, processed.Status);
            Assert.AreEqual(1, adapter.EnterCalls);
            Assert.AreEqual(1, lifecycle.ActiveBindingCount);
            Assert.AreEqual(PlayerSessionState.InWorld, session.State);
        }

        [Test]
        public async Task WorldEntry_AdapterFailure_DoesNotCommitSession()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var adapter = new FakeWorldAdapter { FailEnter = true };
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(21);
            PlayerSession session = await LoadSessionAsync(sessions, connection);

            Assert.IsTrue(lifecycle.TryQueueEnter(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult processed));

            Assert.IsFalse(processed.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.EnterAdapterFailed, processed.Status);
            Assert.AreEqual(PlayerSessionState.AwaitingWorldEntry, session.State);
            Assert.AreEqual(0, lifecycle.ActiveBindingCount);
        }

        [Test]
        public async Task DisconnectBeforeWorldTick_CancelsPendingEntryWithoutSpawning()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var adapter = new FakeWorldAdapter();
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(22);
            PlayerSession session = await LoadSessionAsync(sessions, connection);

            Assert.IsTrue(lifecycle.TryQueueEnter(connection));
            Assert.IsTrue(sessions.BeginDisconnect(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult enterResult));

            Assert.AreEqual(PlayerWorldLifecycleStatus.EnterRejected, enterResult.Status);
            Assert.AreEqual(0, adapter.EnterCalls);
            Assert.AreEqual(PlayerSessionState.Disconnecting, session.State);

            Assert.IsTrue(lifecycle.TryQueueLeave(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult leaveResult));
            Assert.IsTrue(leaveResult.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.LeftWithoutBinding, leaveResult.Status);
        }

        [Test]
        public async Task WorldLeave_CapturesFinalLocationBeforeRemovingBinding()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var finalLocation = new CharacterLocationState(
                "test-map",
                "",
                new WorldPosition(77f, 8f, 9f),
                270f);
            var adapter = new FakeWorldAdapter { LocationToRead = finalLocation };
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(23);
            PlayerSession session = await LoadSessionAsync(sessions, connection);

            lifecycle.TryQueueEnter(connection);
            lifecycle.TryProcessNext(out _);
            Assert.AreEqual(PlayerSessionState.InWorld, session.State);

            Assert.IsTrue(sessions.BeginDisconnect(connection));
            Assert.IsTrue(lifecycle.TryQueueLeave(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult leaveResult));

            Assert.IsTrue(leaveResult.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Left, leaveResult.Status);
            Assert.AreEqual(finalLocation, session.Runtime.Location);
            Assert.IsTrue(session.Runtime.IsDirty);
            Assert.AreEqual(0, lifecycle.ActiveBindingCount);
            Assert.AreEqual(1, adapter.ReadLocationCalls);
            Assert.AreEqual(1, adapter.LeaveCalls);
            // Session intentionally stays open until required final persistence completes.
            Assert.AreEqual(PlayerSessionState.Disconnecting, session.State);
        }

        [Test]
        public async Task WorldLeave_AdapterFailureRetainsBindingForRetry()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var adapter = new FakeWorldAdapter
            {
                LocationToRead = Record().Location,
            };
            var lifecycle = new PlayerWorldLifecycleService(sessions, new PlayerWorldBindingRegistry(), adapter);
            var connection = new ConnectionKey(24);
            await LoadSessionAsync(sessions, connection);

            lifecycle.TryQueueEnter(connection);
            lifecycle.TryProcessNext(out _);
            sessions.BeginDisconnect(connection);

            adapter.FailLeave = true;
            Assert.IsTrue(lifecycle.TryQueueLeave(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult first));
            Assert.IsFalse(first.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.LeaveAdapterFailed, first.Status);
            Assert.AreEqual(1, lifecycle.ActiveBindingCount);

            adapter.FailLeave = false;
            Assert.IsTrue(lifecycle.TryQueueLeave(connection));
            Assert.IsTrue(lifecycle.TryProcessNext(out PlayerWorldLifecycleResult second));
            Assert.IsTrue(second.Success);
            Assert.AreEqual(PlayerWorldLifecycleStatus.Left, second.Status);
            Assert.AreEqual(0, lifecycle.ActiveBindingCount);
        }

        [Test]
        public async Task WorldQueue_CoalescesDuplicateEntryRequests()
        {
            var sessions = CreateSessionService(new InMemoryCharacterRepository(new[] { Record() }));
            var lifecycle = new PlayerWorldLifecycleService(
                sessions,
                new PlayerWorldBindingRegistry(),
                new FakeWorldAdapter(),
                maxPendingCommands: 2);
            var connection = new ConnectionKey(25);
            await LoadSessionAsync(sessions, connection);

            Assert.IsTrue(lifecycle.TryQueueEnter(connection));
            Assert.IsFalse(lifecycle.TryQueueEnter(connection));
            Assert.AreEqual(1, lifecycle.PendingCommandCount);
        }

        [Test]
        public async Task DisconnectDuringAsyncLoad_CannotAttachRuntimeAfterSessionChanged()
        {
            CharacterPersistenceRecord record = Record();
            var repository = new DelayedCharacterRepository(record);
            var sessions = CreateSessionService(repository);
            var connection = new ConnectionKey(26);
            PlayerSession session = sessions.Open(connection);
            sessions.BeginAuthentication(connection);
            sessions.CompleteAuthentication(connection, record.AccountId);

            Task<CharacterSelectResult> load = sessions.SelectCharacterAsync(
                connection,
                record.CharacterId,
                CancellationToken.None);

            // Ensure SelectCharacterAsync has entered LoadingCharacter before disconnect.
            Assert.AreEqual(PlayerSessionState.LoadingCharacter, session.State);
            Assert.IsTrue(sessions.BeginDisconnect(connection));
            repository.ReleaseLoad();

            CharacterSelectResult result = await load;
            Assert.IsFalse(result.Success);
            Assert.AreEqual(CharacterSelectFailure.SessionChangedDuringLoad, result.Failure);
            Assert.AreEqual(PlayerSessionState.Disconnecting, session.State);
            Assert.IsNull(session.Runtime);
        }
    }
}
