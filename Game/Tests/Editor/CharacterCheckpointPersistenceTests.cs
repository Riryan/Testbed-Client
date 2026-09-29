using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CharacterCheckpointPersistenceTests
    {
        [Test]
        public async Task LocationCheckpoint_DoesNotClearInventoryDirtyState()
        {
            var repository = new AcceptingBatchRepository();
            var saves = new CharacterSaveService(repository);
            var tracker = new DirtyPlayerTracker();
            PlayerRuntime runtime = NewRuntime();
            tracker.Track(runtime);

            runtime.MarkDirty(PlayerDirtyFlags.Inventory);
            runtime.UpdateLocation(Location(10f));

            int saved = await saves.SaveDirtyBatchAsync(tracker, 8, CancellationToken.None);

            Assert.That(saved, Is.EqualTo(1));
            Assert.That(runtime.DirtyFlags, Is.EqualTo(PlayerDirtyFlags.Inventory));
            Assert.That(tracker.DirtyCount, Is.EqualTo(1));
        }

        [Test]
        public void LocationBatch_SkipsInventoryOnlyDirtyRuntime()
        {
            var tracker = new DirtyPlayerTracker();
            PlayerRuntime runtime = NewRuntime();
            tracker.Track(runtime);
            runtime.MarkDirty(PlayerDirtyFlags.Inventory);

            PlayerRuntime[] locationBatch = tracker.GetBatch(8, CharacterSaveService.SupportedDirtyFlags);

            Assert.That(locationBatch, Is.Empty);
            Assert.That(tracker.DirtyCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MutationDuringCheckpoint_PreventsDirtyAcknowledgement()
        {
            var repository = new MutatingBatchRepository();
            var saves = new CharacterSaveService(repository);
            var tracker = new DirtyPlayerTracker();
            PlayerRuntime runtime = NewRuntime();
            tracker.Track(runtime);
            repository.RuntimeToMutate = runtime;

            runtime.UpdateLocation(Location(10f));
            long capturedRevision = runtime.Revision;

            int saved = await saves.SaveDirtyBatchAsync(tracker, 8, CancellationToken.None);

            Assert.That(saved, Is.EqualTo(1), "The captured revision was durably accepted.");
            Assert.That(runtime.Revision, Is.GreaterThan(capturedRevision));
            Assert.That(runtime.DirtyFlags & PlayerDirtyFlags.Location, Is.Not.EqualTo(PlayerDirtyFlags.None));
            Assert.That(tracker.DirtyCount, Is.EqualTo(1));
        }

        private static PlayerRuntime NewRuntime() =>
            new PlayerRuntime(
                new AccountId(1),
                new CharacterId(2),
                PlayerSessionId.New(),
                new CharacterState("Alice"),
                Location(0f),
                0);

        private static CharacterLocationState Location(float x) =>
            new CharacterLocationState(
                "TestMap",
                string.Empty,
                new WorldPosition(x, 0f, 0f),
                0f);

        private class AcceptingBatchRepository : ICharacterRepository, ICharacterBatchRepository
        {
            public virtual Task<IReadOnlyList<CharacterSavePersistenceResult>> SaveBatchAsync(
                IReadOnlyList<CharacterPersistenceRecord> records,
                CancellationToken cancellationToken)
            {
                var result = new CharacterSavePersistenceResult[records.Count];
                for (int i = 0; i < records.Count; ++i)
                {
                    CharacterPersistenceRecord record = records[i];
                    result[i] = new CharacterSavePersistenceResult(
                        record.CharacterId,
                        record.Revision,
                        true,
                        record.Revision);
                }
                return Task.FromResult((IReadOnlyList<CharacterSavePersistenceResult>)result);
            }

            public Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(
                AccountId accountId,
                CancellationToken cancellationToken) =>
                Task.FromResult((IReadOnlyList<CharacterSummary>)Array.Empty<CharacterSummary>());

            public Task<CharacterPersistenceRecord> LoadForAccountAsync(
                AccountId accountId,
                CharacterId characterId,
                CancellationToken cancellationToken) =>
                Task.FromResult<CharacterPersistenceRecord>(null);

            public Task<CharacterCreatePersistenceResult> TryCreateAsync(
                AccountId accountId,
                string name,
                CharacterLocationState initialLocation,
                CharacterAppearanceRecipe initialAppearance,
                CharacterPresentationPreferences initialPresentation,
                int maxCharactersForAccount,
                CancellationToken cancellationToken) =>
                Task.FromResult(CharacterCreatePersistenceResult.Failed());

            public Task SaveAsync(CharacterPersistenceRecord record, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }

        private sealed class MutatingBatchRepository : AcceptingBatchRepository
        {
            public PlayerRuntime RuntimeToMutate { get; set; }

            public override Task<IReadOnlyList<CharacterSavePersistenceResult>> SaveBatchAsync(
                IReadOnlyList<CharacterPersistenceRecord> records,
                CancellationToken cancellationToken)
            {
                // Simulates authoritative gameplay changing after the immutable snapshot
                // was captured but before the persistence acknowledgement returns.
                RuntimeToMutate.UpdateLocation(Location(20f));
                return base.SaveBatchAsync(records, cancellationToken);
            }
        }
    }
}
