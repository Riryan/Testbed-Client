using System;
using Game.Server.Application.Content;
using Game.Server.Application.Persistence;
using Game.Server.Application.Resources;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.Stats;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CharacterResourceTests
    {
        [Test]
        public void RemoveHealth_EmitsOneRevisionedEvent_AndMarksResourceDirty()
        {
            CharacterResourceService service = Service();
            PlayerRuntime runtime = Runtime(service);
            CharacterResourceChange? observed = null;
            int eventCount = 0;
            runtime.ResourceChanged += change =>
            {
                observed = change;
                eventCount++;
            };

            CharacterResourceOperationResult result = service.Remove(
                runtime,
                CharacterResourceId.Health,
                25,
                CharacterResourceChangeReason.DamageTaken);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(eventCount, Is.EqualTo(1));
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Value.ResourceId, Is.EqualTo(CharacterResourceId.Health));
            Assert.That(observed.Value.Previous, Is.EqualTo(100));
            Assert.That(observed.Value.Current, Is.EqualTo(75));
            Assert.That(observed.Value.AppliedDelta, Is.EqualTo(-25));
            Assert.That(observed.Value.Reason, Is.EqualTo(CharacterResourceChangeReason.DamageTaken));
            Assert.That(observed.Value.Revision, Is.EqualTo(1));
            Assert.That(runtime.DirtyFlags & PlayerDirtyFlags.Resources, Is.Not.EqualTo(PlayerDirtyFlags.None));
        }

        [Test]
        public void Spend_RejectsOverspend_WithoutMutationOrEvent()
        {
            CharacterResourceService service = Service();
            PlayerRuntime runtime = Runtime(service);
            int eventCount = 0;
            runtime.ResourceChanged += _ => eventCount++;

            Assert.That(service.Set(runtime, CharacterResourceId.Mana, 20, CharacterResourceChangeReason.Administrative).Success, Is.True);
            long beforeRevision = runtime.CaptureCharacterResources().Revision;
            eventCount = 0;

            CharacterResourceOperationResult result = service.Spend(runtime, CharacterResourceId.Mana, 30);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Status, Is.EqualTo(CharacterResourceOperationStatus.Insufficient));
            Assert.That(runtime.CaptureCharacterResources().Get(CharacterResourceId.Mana).Current, Is.EqualTo(20));
            Assert.That(runtime.CaptureCharacterResources().Revision, Is.EqualTo(beforeRevision));
            Assert.That(eventCount, Is.EqualTo(0));
        }

        [Test]
        public void Add_ClampsAtMaximum_AndDoesNotEmitNoOpEvent()
        {
            CharacterResourceService service = Service();
            PlayerRuntime runtime = Runtime(service);
            Assert.That(service.Set(runtime, CharacterResourceId.Stamina, 95, CharacterResourceChangeReason.Administrative).Success, Is.True);
            int eventCount = 0;
            runtime.ResourceChanged += _ => eventCount++;

            CharacterResourceOperationResult first = service.Add(runtime, CharacterResourceId.Stamina, 20, CharacterResourceChangeReason.PassiveRecovery);
            CharacterResourceOperationResult second = service.Add(runtime, CharacterResourceId.Stamina, 1, CharacterResourceChangeReason.PassiveRecovery);

            Assert.That(first.Success, Is.True, first.Error);
            Assert.That(second.Success, Is.True, second.Error);
            Assert.That(runtime.CaptureCharacterResources().Get(CharacterResourceId.Stamina).Current, Is.EqualTo(100));
            Assert.That(eventCount, Is.EqualTo(1), "A no-op at the maximum must not emit another change event.");
        }


        [Test]
        public void PassiveRecovery_StopsWhenHealthIsEmpty()
        {
            CharacterResourceService service = Service();
            PlayerRuntime runtime = Runtime(service);
            Assert.That(service.Set(runtime, CharacterResourceId.Mana, 50, CharacterResourceChangeReason.Administrative).Success, Is.True);
            Assert.That(service.Set(runtime, CharacterResourceId.Health, 0, CharacterResourceChangeReason.DamageTaken).Success, Is.True);

            CharacterResourceDefinition mana = Array.Find(
                service.GetAutomaticDefinitions(),
                d => d.id == CharacterResourceId.Mana);

            Assert.That(mana, Is.Not.Null);
            Assert.That(service.ShouldAutomaticallyUpdate(runtime, mana, inCombat: false), Is.False,
                "Passive resource recovery must not continue while Health is empty.");
        }

        [Test]
        public void NonPersistentResourceChange_EmitsEventWithoutDirtyCheckpoint()
        {
            GameplayContentCatalog content = new GameplayContentCatalog(CreateContent());
            CharacterResourceService service = new CharacterResourceService(content);
            PlayerRuntime runtime = Runtime(service);
            int eventCount = 0;
            runtime.ResourceChanged += _ => eventCount++;

            CharacterResourceOperationResult result = service.Remove(
                runtime,
                CharacterResourceId.Stamina,
                10,
                CharacterResourceChangeReason.AbilityCost);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(eventCount, Is.EqualTo(1));
            Assert.That(runtime.DirtyFlags & PlayerDirtyFlags.Resources, Is.EqualTo(PlayerDirtyFlags.None),
                "Transient/session resource changes must not create Backend checkpoint traffic.");
        }

        [Test]
        public void RespawnReset_UsesDefinitionAndEmitsResourceEvents()
        {
            CharacterResourceService service = Service();
            PlayerRuntime runtime = Runtime(service);
            Assert.That(service.Set(runtime, CharacterResourceId.Health, 0, CharacterResourceChangeReason.DamageTaken).Success, Is.True);
            Assert.That(service.Set(runtime, CharacterResourceId.Mana, 25, CharacterResourceChangeReason.AbilityCost).Success, Is.True);
            int eventCount = 0;
            runtime.ResourceChanged += change =>
            {
                if (change.Reason == CharacterResourceChangeReason.Respawn)
                    eventCount++;
            };

            int changed = service.ApplyRespawnResets(runtime);

            Assert.That(changed, Is.EqualTo(2));
            Assert.That(eventCount, Is.EqualTo(2));
            Assert.That(runtime.CaptureCharacterResources().Get(CharacterResourceId.Health).Current, Is.EqualTo(100));
            Assert.That(runtime.CaptureCharacterResources().Get(CharacterResourceId.Mana).Current, Is.EqualTo(100));
        }

        [Test]
        public void CharacterPersistence_StoresOnlyCharacterPersistentResources()
        {
            GameplayContentCatalog content = new GameplayContentCatalog(CreateContent());
            CharacterResourceService service = new CharacterResourceService(content);
            PlayerRuntime runtime = Runtime(service);

            CharacterPersistenceRecord record = CharacterPersistenceRecord.Capture(runtime);

            Assert.That(record.Resources.Length, Is.EqualTo(2));
            Assert.That(Array.Exists(record.Resources, r => r.ResourceId == CharacterResourceId.Health), Is.True);
            Assert.That(Array.Exists(record.Resources, r => r.ResourceId == CharacterResourceId.Mana), Is.True);
            Assert.That(Array.Exists(record.Resources, r => r.ResourceId == CharacterResourceId.Stamina), Is.False,
                "Session-only resources must not leak into durable character persistence.");
        }

        [Test]
        public void PersistedCurrentValue_RestoresAgainstCurrentDefinitionMaximum()
        {
            GameplayContentCatalog content = new GameplayContentCatalog(CreateContent());
            var service = new CharacterResourceService(content);
            CharacterResourcesState state = service.CreateInitialState(
                StatsState.DefaultCharacter(),
                7,
                new[] { new PersistedCharacterResource(CharacterResourceId.Health, 37, 999) });

            Assert.That(state.Revision, Is.EqualTo(7));
            Assert.That(state.Get(CharacterResourceId.Health).Current, Is.EqualTo(37));
            Assert.That(state.Get(CharacterResourceId.Health).Maximum, Is.EqualTo(100),
                "Definition/stat authority, not stale persisted maximum, owns the current maximum.");
        }

        private static CharacterResourceService Service() =>
            new CharacterResourceService(new GameplayContentCatalog(CreateContent()));

        private static PlayerRuntime Runtime(CharacterResourceService service)
        {
            var runtime = new PlayerRuntime(
                new AccountId(1),
                new CharacterId(2),
                PlayerSessionId.New(),
                new CharacterState("Alice"),
                new CharacterLocationState("TestMap", string.Empty, new WorldPosition(0, 0, 0), 0),
                0);
            runtime.InitializePlayerItemSystems(
                new InventoryState(1, 0, new ItemInstanceState[1]),
                new EquipmentState(0),
                StatsState.DefaultCharacter());
            runtime.InitializeCharacterResources(service.CreateInitialState(StatsState.DefaultCharacter(), 0, null));
            return runtime;
        }

        private static GameplayContentSnapshot CreateContent() => new GameplayContentSnapshot
        {
            revision = 1,
            baseInventoryCapacity = 1,
            equipmentSlots = Array.Empty<EquipmentSlotDefinition>(),
            items = Array.Empty<ItemDefinition>(),
            starterItems = Array.Empty<StarterItemDefinition>(),
            resources = new[]
            {
                new CharacterResourceDefinition
                {
                    id = CharacterResourceId.Health,
                    displayName = "Health",
                    enabled = true,
                    minimum = 0,
                    baseMaximum = 100,
                    maximumStatId = "Health.Max",
                    startAtMaximum = true,
                    allowSpending = false,
                    updateMode = CharacterResourceUpdateMode.Regenerate,
                    updateCondition = CharacterResourceUpdateCondition.OutOfCombatOnly,
                    ratePerSecond = 1f,
                    persistence = CharacterResourcePersistenceMode.Character,
                    replication = CharacterResourceReplicationMode.OwnerOnly,
                    onRespawn = CharacterResourceResetMode.SetToMaximum,
                },
                new CharacterResourceDefinition
                {
                    id = CharacterResourceId.Mana,
                    displayName = "Mana",
                    enabled = true,
                    minimum = 0,
                    baseMaximum = 100,
                    maximumStatId = "Mana.Max",
                    startAtMaximum = true,
                    allowSpending = true,
                    updateMode = CharacterResourceUpdateMode.Regenerate,
                    updateCondition = CharacterResourceUpdateCondition.OutOfCombatOnly,
                    ratePerSecond = 1f,
                    persistence = CharacterResourcePersistenceMode.Character,
                    replication = CharacterResourceReplicationMode.OwnerOnly,
                    onRespawn = CharacterResourceResetMode.SetToMaximum,
                },
                new CharacterResourceDefinition
                {
                    id = CharacterResourceId.Stamina,
                    displayName = "Stamina",
                    enabled = true,
                    minimum = 0,
                    baseMaximum = 100,
                    maximumStatId = "Stamina.Max",
                    startAtMaximum = true,
                    allowSpending = true,
                    updateMode = CharacterResourceUpdateMode.Regenerate,
                    updateCondition = CharacterResourceUpdateCondition.OutOfCombatOnly,
                    ratePerSecond = 5f,
                    persistence = CharacterResourcePersistenceMode.None,
                    replication = CharacterResourceReplicationMode.OwnerOnly,
                },
            },
        };
    }
}
