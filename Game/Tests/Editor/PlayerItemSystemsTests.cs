using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Application.Persistence;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
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
using Game.Shared.StatusEffects;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class PlayerItemSystemsTests
    {
        [Test]
        public void GameplayContentValidation_AllowsBalanceTuning_ButRejectsStructuralHotReload()
        {
            GameplayContentSnapshot current = CreateContent(1, swordAttack: 4f, leatherArmor: 25f);
            GameplayContentSnapshot tuned = CreateContent(2, swordAttack: 6f, leatherArmor: 30f);

            Assert.That(GameplayContentValidation.TryValidate(current, out string initialError), Is.True, initialError);
            Assert.That(GameplayContentValidation.TryValidateCompatibleUpdate(current, tuned, out string tuneError), Is.True, tuneError);

            GameplayContentSnapshot structural = CreateContent(3, swordAttack: 6f, leatherArmor: 30f);
            FindItem(structural, "item.weapon.training_sword").allowedEquipmentSlots = new[] { "Chest" };

            Assert.That(GameplayContentValidation.TryValidateCompatibleUpdate(current, structural, out string structuralError), Is.False);
            StringAssert.Contains("equipment-slot compatibility", structuralError);
        }

        [Test]
        public void GameplayContentValidation_RequiresTypedItemKindAndSubtype()
        {
            GameplayContentSnapshot content = CreateContent(1, 4f, 25f);
            ItemDefinition sword = FindItem(content, "item.weapon.training_sword");

            sword.kind = ItemKind.None;
            Assert.That(GameplayContentValidation.TryValidate(content, out string missingKindError), Is.False);
            StringAssert.Contains("primary kind", missingKindError);

            content = CreateContent(1, 4f, 25f);
            sword = FindItem(content, "item.weapon.training_sword");
            sword.subtype = EquipmentItemSubtype.None;
            Assert.That(GameplayContentValidation.TryValidate(content, out string missingSubtypeError), Is.False);
            StringAssert.Contains("subtype", missingSubtypeError);
        }

        [Test]
        public void GameplayContentValidation_HotReloadRejectsItemKindChanges()
        {
            GameplayContentSnapshot current = CreateContent(1, 4f, 25f);
            GameplayContentSnapshot candidate = CreateContent(2, 4f, 25f);
            ItemDefinition ration = FindItem(candidate, "item.food.ration");
            ration.kind = ItemKind.Junk;

            Assert.That(GameplayContentValidation.TryValidateCompatibleUpdate(
                current, candidate, out string error), Is.False);
            StringAssert.Contains("kind/subtype", error);
        }

        [Test]
        public void InventoryState_DefensivelyCopiesSlots()
        {
            var original = new[]
            {
                new ItemInstanceState(new ItemInstanceId(10), "item.food.ration", 2, 0, 0),
                null,
            };
            var state = new InventoryState(2, 7, original);

            original[0] = null;
            ItemInstanceState[] copy = state.CopySlots();
            copy[0] = null;

            Assert.That(state.Capacity, Is.EqualTo(2));
            Assert.That(state.Revision, Is.EqualTo(7));
            Assert.That(state.Get(0), Is.Not.Null);
            Assert.That(state.Get(0).Quantity, Is.EqualTo(2));
        }

        [Test]
        public void EquipmentStats_AreCalculatedFromAuthoritativeDefinitions()
        {
            var content = new GameplayContentCatalog(CreateContent(1, swordAttack: 4f, leatherArmor: 25f));
            var equipment = new EquipmentState(3, new[]
            {
                new EquippedItemState("MainHand", Item(1, "item.weapon.training_sword", 1, 100)),
                new EquippedItemState("Chest", Item(2, "item.armor.leather_chest", 1, 100)),
            });

            StatsState stats = EquipmentStatCalculator.Calculate(content, equipment);

            Assert.That(stats.Get("Health.Max"), Is.EqualTo(100f));
            Assert.That(stats.Get("Armor"), Is.EqualTo(25f));
            Assert.That(stats.Get("AttackPower"), Is.EqualTo(4f));
        }

        [Test]
        public async Task MoveInventory_MergesCompatibleStacks_AndCommitsRevision()
        {
            var content = new GameplayContentCatalog(CreateContent(1, 4f, 25f));
            var repository = new AcceptingRepository();
            PlayerRuntime runtime = Runtime(
                new InventoryState(4, 0, new[]
                {
                    Item(10, "item.food.ration", 3, 0),
                    Item(11, "item.food.ration", 5, 0),
                    null,
                    null,
                }),
                new EquipmentState(0));
            var service = new PlayerItemService(content, repository);

            PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            Assert.That(state.Inventory.Revision, Is.EqualTo(1));
            Assert.That(state.Inventory.Get(0), Is.Null);
            Assert.That(state.Inventory.Get(1).ItemInstanceId, Is.EqualTo(new ItemInstanceId(11)));
            Assert.That(state.Inventory.Get(1).Quantity, Is.EqualTo(8));
            Assert.That(state.Inventory.Get(1).Revision, Is.EqualTo(1));
            Assert.That(repository.LastRequest.ExpectedInventoryRevision, Is.EqualTo(0));
        }

        [Test]
        public async Task EquipItem_MovesAuthorityToEquipment_AndRecalculatesStats()
        {
            var content = new GameplayContentCatalog(CreateContent(1, 4f, 25f));
            var repository = new AcceptingRepository();
            PlayerRuntime runtime = Runtime(
                new InventoryState(3, 0, new[]
                {
                    Item(20, "item.armor.leather_chest", 1, 100),
                    null,
                    null,
                }),
                new EquipmentState(0));
            var service = new PlayerItemService(content, repository);

            PlayerItemOperationResult result = await service.EquipAsync(runtime, 0, "Chest", CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            Assert.That(state.Inventory.Get(0), Is.Null);
            Assert.That(state.Inventory.Revision, Is.EqualTo(1));
            Assert.That(state.Equipment.Revision, Is.EqualTo(1));
            Assert.That(state.Equipment.Get("Chest"), Is.Not.Null);
            Assert.That(state.Equipment.Get("Chest").ItemInstanceId, Is.EqualTo(new ItemInstanceId(20)));
            Assert.That(state.Stats.Get("Armor"), Is.EqualTo(25f));
        }

        [Test]
        public async Task EquipItem_ReplacesExistingEquipment_WithoutLosingDisplacedItem()
        {
            var content = new GameplayContentCatalog(CreateContent(1, 4f, 25f));
            var repository = new AcceptingRepository();
            PlayerRuntime runtime = Runtime(
                new InventoryState(3, 4, new[]
                {
                    Item(31, "item.armor.steel_chest", 1, 120),
                    null,
                    null,
                }),
                new EquipmentState(8, new[]
                {
                    new EquippedItemState("Chest", Item(30, "item.armor.leather_chest", 1, 100)),
                }));
            var service = new PlayerItemService(content, repository);

            PlayerItemOperationResult result = await service.EquipAsync(runtime, 0, "Chest", CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            Assert.That(state.Inventory.Get(0).ItemInstanceId, Is.EqualTo(new ItemInstanceId(30)));
            Assert.That(state.Equipment.Get("Chest").ItemInstanceId, Is.EqualTo(new ItemInstanceId(31)));
            Assert.That(state.Stats.Get("Armor"), Is.EqualTo(50f));
        }

        [Test]
        public async Task UseItem_PersistsConsumeBeforeApplyingResourceAndStatusEffects_AndPreservesInstanceId()
        {
            GameplayContentSnapshot definitions = CreateUsableContent(1);
            var content = new GameplayContentCatalog(definitions);
            var repository = new AcceptingRepository();
            var lifecycle = new AcceptingLifecycleRepository();
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            PlayerRuntime runtime = Runtime(
                new InventoryState(3, 7, new[]
                {
                    Item(60, "item.food.ration", 3, 0),
                    null,
                    null,
                }),
                new EquipmentState(4));
            runtime.InitializeCharacterResources(new CharacterResourcesState(2, new[]
            {
                new CharacterResourceState(CharacterResourceId.Health, 100, 0, 100),
                new CharacterResourceState(CharacterResourceId.Stamina, 50, 0, 100),
            }));
            var service = new PlayerItemService(content, repository, lifecycle, resources, statuses);

            PlayerItemOperationResult result = await service.UseAsync(runtime, 0, 10d, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot items = runtime.CapturePlayerItemSystems();
            Assert.That(items.Inventory.Revision, Is.EqualTo(8));
            Assert.That(items.Equipment.Revision, Is.EqualTo(4));
            Assert.That(items.Inventory.Get(0), Is.Not.Null);
            Assert.That(items.Inventory.Get(0).ItemInstanceId, Is.EqualTo(new ItemInstanceId(60)));
            Assert.That(items.Inventory.Get(0).Quantity, Is.EqualTo(2));
            Assert.That(items.Inventory.Get(0).Revision, Is.EqualTo(1));
            Assert.That(lifecycle.LastRequest.ItemInstanceId, Is.EqualTo(new ItemInstanceId(60)));
            Assert.That(lifecycle.LastRequest.ConsumeQuantity, Is.EqualTo(1));
            Assert.That(lifecycle.LastRequest.ExpectedInventoryRevision, Is.EqualTo(7));
            Assert.That(runtime.CaptureCharacterResources().Get(CharacterResourceId.Stamina).Current, Is.EqualTo(75));
            Assert.That(runtime.CaptureStatusEffects().TryGet("status.well_fed", out var active), Is.True);
            Assert.That(active.Stacks, Is.EqualTo(1));
        }

        [Test]
        public async Task UseItem_WhenStackIsExhausted_RetiresTheInstanceInsteadOfCreatingAReplacementId()
        {
            GameplayContentSnapshot definitions = CreateUsableContent(1);
            var content = new GameplayContentCatalog(definitions);
            var repository = new AcceptingRepository();
            var lifecycle = new AcceptingLifecycleRepository();
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            PlayerRuntime runtime = Runtime(
                new InventoryState(2, 0, new[]
                {
                    Item(62, "item.food.ration", 1, 0),
                    null,
                }),
                new EquipmentState(0));
            runtime.InitializeCharacterResources(new CharacterResourcesState(0, new[]
            {
                new CharacterResourceState(CharacterResourceId.Health, 100, 0, 100),
                new CharacterResourceState(CharacterResourceId.Stamina, 50, 0, 100),
            }));
            var service = new PlayerItemService(content, repository, lifecycle, resources, statuses);

            PlayerItemOperationResult result = await service.UseAsync(runtime, 0, 5d, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot items = runtime.CapturePlayerItemSystems();
            Assert.That(items.Inventory.Get(0), Is.Null);
            Assert.That(items.Inventory.Revision, Is.EqualTo(1));
            Assert.That(lifecycle.LastRequest.ItemInstanceId, Is.EqualTo(new ItemInstanceId(62)));
            Assert.That(lifecycle.LastRequest.Inventory.Get(0), Is.Null);
        }

        [Test]
        public async Task UseItem_WhenNoEffectIsApplicable_DoesNotConsumeOrCallPersistence()
        {
            GameplayContentSnapshot definitions = CreateUsableContent(1);
            var content = new GameplayContentCatalog(definitions);
            var repository = new AcceptingRepository();
            var lifecycle = new AcceptingLifecycleRepository();
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            PlayerRuntime runtime = Runtime(
                new InventoryState(2, 3, new[]
                {
                    Item(61, "item.food.ration", 2, 0),
                    null,
                }),
                new EquipmentState(0));
            runtime.InitializeCharacterResources(new CharacterResourcesState(0, new[]
            {
                new CharacterResourceState(CharacterResourceId.Health, 100, 0, 100),
                new CharacterResourceState(CharacterResourceId.Stamina, 100, 0, 100),
            }));
            statuses.Apply(runtime, "status.well_fed", runtime.CharacterId.Value, 1, 1d, StatusEffectChangeReason.Item);
            var service = new PlayerItemService(content, repository, lifecycle, resources, statuses);

            PlayerItemOperationResult result = await service.UseAsync(runtime, 0, 2d, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Status, Is.EqualTo(PlayerItemOperationStatus.EffectUnavailable));
            Assert.That(lifecycle.CallCount, Is.EqualTo(0));
            PlayerItemSystemsRuntimeSnapshot items = runtime.CapturePlayerItemSystems();
            Assert.That(items.Inventory.Revision, Is.EqualTo(3));
            Assert.That(items.Inventory.Get(0).Quantity, Is.EqualTo(2));
        }

        [Test]
        public async Task UseItem_WhileDead_DoesNotConsumeOrPersist()
        {
            GameplayContentSnapshot definitions = CreateUsableContent(1);
            var content = new GameplayContentCatalog(definitions);
            var repository = new AcceptingRepository();
            var lifecycle = new AcceptingLifecycleRepository();
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            PlayerRuntime runtime = Runtime(
                new InventoryState(2, 0, new[] { Item(63, "item.food.ration", 2, 0), null }),
                new EquipmentState(0));
            runtime.InitializeCharacterResources(new CharacterResourcesState(0, new[]
            {
                new CharacterResourceState(CharacterResourceId.Health, 0, 0, 100),
                new CharacterResourceState(CharacterResourceId.Stamina, 50, 0, 100),
            }));
            var service = new PlayerItemService(content, repository, lifecycle, resources, statuses);

            PlayerItemOperationResult result = await service.UseAsync(runtime, 0, 3d, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Status, Is.EqualTo(PlayerItemOperationStatus.EffectUnavailable));
            Assert.That(lifecycle.CallCount, Is.EqualTo(0));
            Assert.That(runtime.CapturePlayerItemSystems().Inventory.Get(0).Quantity, Is.EqualTo(2));
        }

        [Test]
        public async Task RejectedPersistence_DoesNotMutateLiveAuthoritativeState()
        {
            var content = new GameplayContentCatalog(CreateContent(1, 4f, 25f));
            var repository = new RejectingRepository(stale: true);
            PlayerRuntime runtime = Runtime(
                new InventoryState(2, 0, new[]
                {
                    Item(40, "item.weapon.training_sword", 1, 100),
                    null,
                }),
                new EquipmentState(0));
            var service = new PlayerItemService(content, repository);

            PlayerItemOperationResult result = await service.EquipAsync(runtime, 0, "MainHand", CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Status, Is.EqualTo(PlayerItemOperationStatus.StaleState));
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            Assert.That(state.Inventory.Revision, Is.EqualTo(0));
            Assert.That(state.Equipment.Revision, Is.EqualTo(0));
            Assert.That(state.Inventory.Get(0), Is.Not.Null);
            Assert.That(state.Equipment.Get("MainHand"), Is.Null);
            Assert.That(state.Stats.Get("AttackPower"), Is.EqualTo(0f));
        }

        [Test]
        public async Task EquipItem_DoesNotBecomeLiveUntilDurableCommitAcknowledges()
        {
            var content = new GameplayContentCatalog(CreateContent(1, 4f, 25f));
            var repository = new ControlledRepository();
            PlayerRuntime runtime = Runtime(
                new InventoryState(2, 0, new[]
                {
                    Item(50, "item.weapon.training_sword", 1, 100),
                    null,
                }),
                new EquipmentState(0));
            var service = new PlayerItemService(content, repository);

            Task<PlayerItemOperationResult> pending = service.EquipAsync(runtime, 0, "MainHand", CancellationToken.None);
            await repository.CommitStarted.Task;

            PlayerItemSystemsRuntimeSnapshot beforeAck = runtime.CapturePlayerItemSystems();
            Assert.That(beforeAck.Inventory.Get(0), Is.Not.Null);
            Assert.That(beforeAck.Equipment.Get("MainHand"), Is.Null);
            Assert.That(beforeAck.Stats.Get("AttackPower"), Is.EqualTo(0f));

            repository.Accept();
            PlayerItemOperationResult result = await pending;

            Assert.That(result.Success, Is.True, result.Error);
            PlayerItemSystemsRuntimeSnapshot afterAck = runtime.CapturePlayerItemSystems();
            Assert.That(afterAck.Inventory.Get(0), Is.Null);
            Assert.That(afterAck.Equipment.Get("MainHand"), Is.Not.Null);
            Assert.That(afterAck.Stats.Get("AttackPower"), Is.EqualTo(4f));
        }

        private static PlayerRuntime Runtime(InventoryState inventory, EquipmentState equipment)
        {
            var runtime = new PlayerRuntime(
                new AccountId(1),
                new CharacterId(2),
                PlayerSessionId.New(),
                new CharacterState("Alice"),
                new CharacterLocationState("TestMap", string.Empty, new WorldPosition(0, 0, 0), 0),
                0);
            runtime.InitializePlayerItemSystems(inventory, equipment, StatsState.DefaultCharacter());
            return runtime;
        }

        private static ItemInstanceState Item(long id, string definitionId, int quantity, int durability) =>
            new ItemInstanceState(new ItemInstanceId(id), definitionId, quantity, durability, 0);

        private static GameplayContentSnapshot CreateContent(long revision, float swordAttack, float leatherArmor)
        {
            return new GameplayContentSnapshot
            {
                revision = revision,
                baseInventoryCapacity = 30,
                equipmentSlots = new[]
                {
                    new EquipmentSlotDefinition { slotId = "Chest", displayName = "Chest", order = 10 },
                    new EquipmentSlotDefinition { slotId = "MainHand", displayName = "Main Hand", order = 20 },
                },
                items = new[]
                {
                    new ItemDefinition
                    {
                        definitionId = "item.weapon.training_sword", displayName = "Training Sword",
                        kind = ItemKind.Equipment, subtype = EquipmentItemSubtype.Weapon, maxStack = 1,
                        weight = 3.25f, maxDurability = 100, allowedEquipmentSlots = new[] { "MainHand" },
                        damageMin = 4, damageMax = 8, basicAttackRange = 1.5f, basicAttackInterval = 2f,
                        statModifiers = new[] { new StatModifierDefinition { statId = "AttackPower", additive = swordAttack, multiplier = 1f } },
                    },
                    new ItemDefinition
                    {
                        definitionId = "item.armor.leather_chest", displayName = "Leather Chest Armor",
                        kind = ItemKind.Equipment, subtype = EquipmentItemSubtype.Armor, maxStack = 1,
                        weight = 5f, maxDurability = 100, allowedEquipmentSlots = new[] { "Chest" },
                        statModifiers = new[] { new StatModifierDefinition { statId = "Armor", additive = leatherArmor, multiplier = 1f } },
                    },
                    new ItemDefinition
                    {
                        definitionId = "item.armor.steel_chest", displayName = "Steel Chest Armor",
                        kind = ItemKind.Equipment, subtype = EquipmentItemSubtype.Armor, maxStack = 1,
                        weight = 9f, maxDurability = 120, allowedEquipmentSlots = new[] { "Chest" },
                        statModifiers = new[] { new StatModifierDefinition { statId = "Armor", additive = 50f, multiplier = 1f } },
                    },
                    new ItemDefinition
                    {
                        definitionId = "item.food.ration", displayName = "Field Ration",
                        kind = ItemKind.Material, subtype = EquipmentItemSubtype.None, maxStack = 20,
                        weight = 0.2f, maxDurability = 0, allowedEquipmentSlots = Array.Empty<string>(),
                    },
                },
                starterItems = Array.Empty<StarterItemDefinition>(),
            };
        }

        private static GameplayContentSnapshot CreateUsableContent(long revision)
        {
            GameplayContentSnapshot content = CreateContent(revision, 4f, 25f);
            content.resources = new[]
            {
                new CharacterResourceDefinition
                {
                    id = CharacterResourceId.Health, displayName = "Health", enabled = true, minimum = 0,
                    baseMaximum = 100, startAtMaximum = true, allowSpending = false,
                },
                new CharacterResourceDefinition
                {
                    id = CharacterResourceId.Stamina, displayName = "Stamina", enabled = true, minimum = 0,
                    baseMaximum = 100, startAtMaximum = true, allowSpending = true,
                },
            };
            content.statusEffects = new[]
            {
                new StatusEffectDefinition
                {
                    definitionId = "status.well_fed", displayName = "Well Fed",
                    classification = StatusEffectClassification.Buff, durationSeconds = 30f,
                    stackingPolicy = StatusEffectStackingPolicy.IgnoreWhileActive, maximumStacks = 1,
                },
            };
            ItemDefinition ration = FindItem(content, "item.food.ration");
            ration.kind = ItemKind.Consumable;
            ration.consumeQuantity = 1;
            ration.useEffects = new[]
            {
                new ItemUseEffectDefinition
                {
                    kind = ItemUseEffectKind.RestoreResource, resourceId = CharacterResourceId.Stamina, amount = 25, stacks = 1,
                },
                new ItemUseEffectDefinition
                {
                    kind = ItemUseEffectKind.ApplyStatusEffect, statusEffectId = "status.well_fed", stacks = 1,
                },
            };
            return content;
        }

        private static ItemDefinition FindItem(GameplayContentSnapshot content, string definitionId)
        {
            for (int i = 0; i < content.items.Length; ++i)
                if (content.items[i].definitionId == definitionId)
                    return content.items[i];
            return null;
        }

        private sealed class AcceptingRepository : IPlayerSystemsRepository
        {
            public PlayerSystemsCommitRequest LastRequest { get; private set; }

            public Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken) =>
                Task.FromResult<PlayerSystemsPersistenceRecord>(null);

            public Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(new PlayerSystemsCommitResult(
                    true, false, request.Inventory.Revision, request.Equipment.Revision, string.Empty));
            }
        }

        private sealed class AcceptingLifecycleRepository : IPlayerItemLifecycleRepository
        {
            public PlayerItemConsumePersistenceRequest LastRequest { get; private set; }
            public int CallCount { get; private set; }

            public Task<PlayerItemConsumePersistenceResult> TryConsumeAsync(
                PlayerItemConsumePersistenceRequest request,
                CancellationToken cancellationToken)
            {
                LastRequest = request;
                CallCount++;
                return Task.FromResult(new PlayerItemConsumePersistenceResult(
                    true, false, request.Inventory.Revision, request.Equipment.Revision, string.Empty));
            }

            public Task<PlayerAmmoReloadPersistenceResult> TryConsumeAmmoForReloadAsync(PlayerAmmoReloadPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerAmmoReloadPersistenceResult(
                    false, false, true, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision,
                    string.Empty, 0, null, "not used by this test fake"));

            public Task<PlayerItemDropPersistenceResult> TryDropAsync(PlayerItemDropPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerItemDropPersistenceResult(false, false, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision, 0, null, "not used by this test fake"));

            public Task<PlayerItemPickupPersistenceResult> TryPickupAsync(PlayerItemPickupPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerItemPickupPersistenceResult(false, false, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision, 0, string.Empty, string.Empty, "not used by this test fake"));

            public Task<PlayerItemGrantPersistenceResult> TryGrantAsync(PlayerItemGrantPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerItemGrantPersistenceResult(
                    false,
                    false,
                    request.ExpectedInventoryRevision,
                    request.ExpectedEquipmentRevision,
                    null,
                    "not used by this test fake"));

            public Task<PlayerItemBundleGrantPersistenceResult> TryGrantBundleAsync(PlayerItemBundleGrantPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerItemBundleGrantPersistenceResult(
                    false, false, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision, null, "not used by this test fake"));

            public Task<PlayerCraftPersistenceResult> TryCraftAsync(PlayerCraftPersistenceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerCraftPersistenceResult(
                    false, false, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision, null, "not used by this test fake"));
        }

        private sealed class RejectingRepository : IPlayerSystemsRepository
        {
            private readonly bool _stale;
            public RejectingRepository(bool stale) { _stale = stale; }

            public Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken) =>
                Task.FromResult<PlayerSystemsPersistenceRecord>(null);

            public Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PlayerSystemsCommitResult(false, _stale, request.ExpectedInventoryRevision, request.ExpectedEquipmentRevision, "rejected"));
        }

        private sealed class ControlledRepository : IPlayerSystemsRepository
        {
            private readonly TaskCompletionSource<PlayerSystemsCommitResult> _completion =
                new TaskCompletionSource<PlayerSystemsCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> CommitStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private PlayerSystemsCommitRequest _request;

            public Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken) =>
                Task.FromResult<PlayerSystemsPersistenceRecord>(null);

            public Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken)
            {
                _request = request;
                CommitStarted.TrySetResult(true);
                return _completion.Task;
            }

            public void Accept()
            {
                _completion.TrySetResult(new PlayerSystemsCommitResult(
                    true, false, _request.Inventory.Revision, _request.Equipment.Revision, string.Empty));
            }
        }
    }
}
