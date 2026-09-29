using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Application.Loot;
using Game.Shared.Content;
using Game.Shared.Interactions;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// Contract tests for the event-driven Harvest foundation and canonical loot resolver.
    /// Harvest owns method/tool/profession admission; Loot only resolves an already-selected table.
    /// </summary>
    public sealed class HarvestContentContractTests
    {
        [Test]
        public void HarvestProfile_ResolvesCompactProfessionAndLootIds()
        {
            GameplayContentSnapshot snapshot = CreateValidSnapshot();
            Assert.That(GameplayContentValidation.TryValidate(snapshot, out string error), Is.True, error);

            var catalog = new GameplayContentCatalog(snapshot);
            Assert.That(catalog.TryGetHarvestProfile(1, out HarvestProfileDefinition profile), Is.True);
            Assert.That(profile.definitionId, Is.EqualTo("recovery_salvage"));
            Assert.That(profile.professionTrackDefinitionId, Is.EqualTo("profession.salvaging"));
            Assert.That(profile.professionTrackDataId, Is.Not.Zero);
            Assert.That(profile.methods, Has.Length.EqualTo(1));
            Assert.That(profile.methods[0].capability, Is.EqualTo(HarvestCapability.Hands));
            Assert.That(profile.methods[0].toolAccess, Is.EqualTo(HarvestToolAccess.None));
            Assert.That(profile.methods[0].lootTableDataId, Is.Not.Zero);
            Assert.That(catalog.TryGetLootTable(profile.methods[0].lootTableDataId, out _), Is.True);
        }

        [Test]
        public void HarvestContent_RejectsLootEntriesThatReferenceUnknownItems()
        {
            GameplayContentSnapshot snapshot = CreateValidSnapshot();
            snapshot.lootTables[0].entries[0].itemDefinitionId = "missing_item";

            Assert.That(GameplayContentValidation.TryValidate(snapshot, out string error), Is.False);
            StringAssert.Contains("unknown item", error);
        }

        [Test]
        public void LootResolver_WeightedUsesExplicitIntegerWeightsAndQuantity()
        {
            var table = new LootTableDefinition
            {
                definitionId = "test",
                rollMode = LootRollMode.Weighted,
                rolls = 1,
                entries = new[]
                {
                    new LootTableEntryDefinition
                    {
                        itemDataId = 1,
                        itemDefinitionId = "scrap",
                        minQuantity = 1,
                        maxQuantity = 1,
                        chance = 0f,
                        weight = 25,
                    },
                    new LootTableEntryDefinition
                    {
                        itemDataId = 2,
                        itemDefinitionId = "rare_scrap",
                        minQuantity = 2,
                        maxQuantity = 4,
                        chance = 0f,
                        weight = 75,
                    },
                },
            };

            Assert.That(
                LootTableResolver.TryRoll(
                    table,
                    "salvaging",
                    10,
                    0.50d,
                    0.50d,
                    null,
                    out LootRollResult result,
                    out string error),
                Is.True,
                error);

            Assert.That(result.ItemDefinitionId, Is.EqualTo("rare_scrap"));
            Assert.That(result.Quantity, Is.InRange(2, 4));
        }

        [Test]
        public void LootResolver_AppliesCallerPredicateBeforeWeighting()
        {
            var table = new LootTableDefinition
            {
                definitionId = "test",
                rollMode = LootRollMode.Weighted,
                entries = new[]
                {
                    new LootTableEntryDefinition { itemDataId = 1, itemDefinitionId = "scrap", minQuantity = 1, maxQuantity = 1, weight = 1 },
                    new LootTableEntryDefinition { itemDataId = 2, itemDefinitionId = "rare_scrap", minQuantity = 1, maxQuantity = 1, weight = 99 },
                },
            };

            Assert.That(
                LootTableResolver.TryRoll(
                    table,
                    "salvaging",
                    10,
                    0.90d,
                    0d,
                    entry => entry.itemDefinitionId == "scrap",
                    out LootRollResult result,
                    out string error),
                Is.True,
                error);

            Assert.That(result.ItemDefinitionId, Is.EqualTo("scrap"));
        }

        [Test]
        public void LootResolver_IndependentModeCanAwardMultipleEntries()
        {
            var table = new LootTableDefinition
            {
                definitionId = "independent",
                rollMode = LootRollMode.Independent,
                entries = new[]
                {
                    new LootTableEntryDefinition { itemDataId = 1, itemDefinitionId = "a", minQuantity = 1, maxQuantity = 1, chance = 1f },
                    new LootTableEntryDefinition { itemDataId = 2, itemDefinitionId = "b", minQuantity = 2, maxQuantity = 2, chance = 1f },
                },
            };

            Assert.That(
                LootTableResolver.TryRollAll(table, () => 0d, null, out LootRollResult[] results, out string error),
                Is.True,
                error);
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results[0].ItemDefinitionId, Is.EqualTo("a"));
            Assert.That(results[1].ItemDefinitionId, Is.EqualTo("b"));
        }

        [Test]
        public void LootResolver_WeightedModeHonorsRollCount()
        {
            var samples = new Queue<double>(new[] { 0d, 0d, 0.99d, 0d });
            var table = new LootTableDefinition
            {
                definitionId = "weighted_n",
                rollMode = LootRollMode.Weighted,
                rolls = 2,
                entries = new[]
                {
                    new LootTableEntryDefinition { itemDataId = 1, itemDefinitionId = "common", minQuantity = 1, maxQuantity = 1, weight = 50 },
                    new LootTableEntryDefinition { itemDataId = 2, itemDefinitionId = "rare", minQuantity = 1, maxQuantity = 1, weight = 50 },
                },
            };

            Assert.That(
                LootTableResolver.TryRollAll(table, () => samples.Dequeue(), null, out LootRollResult[] results, out string error),
                Is.True,
                error);
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results[0].ItemDefinitionId, Is.EqualTo("common"));
            Assert.That(results[1].ItemDefinitionId, Is.EqualTo("rare"));
        }

        private static GameplayContentSnapshot CreateValidSnapshot() => new GameplayContentSnapshot
        {
            revision = 1,
            baseInventoryCapacity = 16,
            equipmentSlots = Array.Empty<EquipmentSlotDefinition>(),
            items = new[]
            {
                new ItemDefinition
                {
                    definitionId = "scrap",
                    displayName = "Scrap",
                    kind = ItemKind.Material,
                    subtype = EquipmentItemSubtype.None,
                    maxStack = 20,
                    weight = 0.1f,
                    tags = Array.Empty<string>(),
                    allowedEquipmentSlots = Array.Empty<string>(),
                    statModifiers = Array.Empty<StatModifierDefinition>(),
                    useEffects = Array.Empty<ItemUseEffectDefinition>(),
                },
            },
            starterItems = Array.Empty<StarterItemDefinition>(),
            resources = Array.Empty<CharacterResourceDefinition>(),
            statusEffects = Array.Empty<StatusEffectDefinition>(),
            abilities = Array.Empty<AbilityDefinition>(),
            damageTypes = Array.Empty<DamageTypeDefinition>(),
            progressTracks = new[]
            {
                new ProgressTrackDefinition
                {
                    definitionId = "profession.salvaging",
                    displayName = "Salvaging",
                    kind = ProgressTrackKind.Profession,
                    maximumValue = 100,
                },
            },
            // Legacy profession metadata remains readable while Harvest runtime authority uses
            // the canonical compact ProgressTrack data ID above.
            professions = new[]
            {
                new ProfessionDefinition
                {
                    definitionId = "salvaging",
                    displayName = "Salvaging",
                    maximumLevel = 100,
                },
            },
            lootTables = new[]
            {
                new LootTableDefinition
                {
                    definitionId = "recovery_salvage_loot",
                    displayName = "Recovery Salvage Loot",
                    rollMode = LootRollMode.Weighted,
                    rolls = 1,
                    entries = new[]
                    {
                        new LootTableEntryDefinition
                        {
                            itemDefinitionId = "scrap",
                            minQuantity = 1,
                            maxQuantity = 3,
                            chance = 0f,
                            weight = 100,
                        },
                    },
                },
            },
            harvestProfiles = new[]
            {
                new HarvestProfileDefinition
                {
                    profileId = 1,
                    definitionId = "recovery_salvage",
                    actionId = InteractionActionId.Harvest,
                    professionId = "salvaging",
                    durationSeconds = 3f,
                    maximumUseDistance = 4f,
                    maximumFacingAngle = 180f,
                    minimumProfessionLevel = 0,
                    minimumCharges = 1,
                    maximumCharges = 3,
                    respawnPolicy = HarvestRespawnPolicy.Timed,
                    respawnSeconds = 20f,
                    successProfessionReward = 5,
                    failureProfessionReward = 1,
                    methods = new[]
                    {
                        new HarvestMethodDefinition
                        {
                            capability = HarvestCapability.Hands,
                            toolAccess = HarvestToolAccess.None,
                            rewardTier = 0,
                            baseSuccessBasisPoints = 7500,
                            skillBasisPointsPerLevel = 10,
                            lootTableId = "recovery_salvage_loot",
                        },
                    },
                },
            },
        };
    }
}
