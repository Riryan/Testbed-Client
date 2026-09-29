using System;
using System.Collections.Generic;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Actors;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CombatStatusEffectTests
    {
        [Test]
        public void StandardDamage_MutatesHealthThroughCanonicalResourceEvent()
        {
            Services services = CreateServices();
            PlayerRuntime target = Runtime(services.Resources, armor: 25f);
            int resourceEvents = 0;
            target.ResourceChanged += change =>
            {
                if (change.ResourceId == CharacterResourceId.Health)
                    resourceEvents++;
            };

            CombatDamageResult result = services.Combat.ApplyStandardDamage(null, target, 30, 10d);

            Assert.That(result.resultCode, Is.EqualTo(CombatDamageResultCode.Applied));
            Assert.That(result.dealtAmount, Is.EqualTo(5));
            Assert.That(result.healthBefore, Is.EqualTo(100));
            Assert.That(result.healthAfter, Is.EqualTo(95));
            Assert.That(resourceEvents, Is.EqualTo(1), "Combat must use the canonical ResourceChanged path, not a second health mutation path.");
        }

        [Test]
        public void StackingStatus_UpdatesMultiplierAndEmitsRevisionedEvents()
        {
            Services services = CreateServices();
            PlayerRuntime target = Runtime(services.Resources);
            int events = 0;
            target.StatusEffectChanged += _ => events++;

            StatusEffectOperationResult first = services.Statuses.Apply(
                target, "status.test.vulnerable", 0, 1, 10d, StatusEffectChangeReason.Ability);
            StatusEffectOperationResult second = services.Statuses.Apply(
                target, "status.test.vulnerable", 0, 1, 11d, StatusEffectChangeReason.Ability);

            Assert.That(first.Success, Is.True, first.Error);
            Assert.That(second.Success, Is.True, second.Error);
            Assert.That(target.CaptureStatusEffects().Revision, Is.EqualTo(2));
            Assert.That(target.CaptureStatusEffects().TryGet("status.test.vulnerable", out var active), Is.True);
            Assert.That(active.Stacks, Is.EqualTo(2));
            Assert.That(target.GetStat("DamageTakenMultiplier", 1f), Is.EqualTo(2.25f).Within(0.001f));
            Assert.That(events, Is.EqualTo(2));
        }

        [Test]
        public void StatusDamageMultiplier_IsConsumedByCombatAuthority()
        {
            Services services = CreateServices();
            PlayerRuntime target = Runtime(services.Resources);
            Assert.That(services.Statuses.Apply(
                target, "status.test.vulnerable", 0, 1, 10d, StatusEffectChangeReason.Ability).Success, Is.True);

            CombatDamageResult result = services.Combat.ApplyStandardDamage(null, target, 10, 11d);

            Assert.That(result.dealtAmount, Is.EqualTo(15));
            Assert.That(result.healthAfter, Is.EqualTo(85));
        }

        [Test]
        public void LethalDamage_ClearsRemoveOnDeathStatus_AndEmitsKill()
        {
            Services services = CreateServices();
            PlayerRuntime target = Runtime(services.Resources);
            Assert.That(services.Statuses.Apply(
                target, "status.test.vulnerable", 0, 1, 10d, StatusEffectChangeReason.Ability).Success, Is.True);
            int kills = 0;
            services.Combat.CharacterKilled += (_, __) => kills++;

            CombatDamageResult result = services.Combat.ApplyStandardDamage(null, target, 1000, 11d);

            Assert.That(result.killed, Is.True);
            Assert.That(result.resultCode, Is.EqualTo(CombatDamageResultCode.Killed));
            Assert.That(target.CaptureStatusEffects().Count, Is.EqualTo(0));
            Assert.That(kills, Is.EqualTo(1));
        }

        [Test]
        public void CombatState_IsDeadlineBased_NotPollingState()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, characterId: 2);
            PlayerRuntime target = Runtime(services.Resources, characterId: 3);

            CombatDamageResult result = services.Combat.ApplyStandardDamage(source, target, 5, 20d);

            Assert.That(result.resultCode, Is.EqualTo(CombatDamageResultCode.Applied));
            Assert.That(services.Combat.IsInCombat(source, 24.99d), Is.True);
            Assert.That(services.Combat.IsInCombat(target, 24.99d), Is.True);
            Assert.That(services.Combat.IsInCombat(source, 25.01d), Is.False);
            Assert.That(services.Combat.GetCombatExpiry(source), Is.EqualTo(25d).Within(0.001d));
        }

        private sealed class Services
        {
            public CharacterResourceService Resources;
            public StatusEffectService Statuses;
            public CombatService Combat;
        }

        private static Services CreateServices()
        {
            var content = new GameplayContentCatalog(Content());
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            return new Services
            {
                Resources = resources,
                Statuses = statuses,
                Combat = new CombatService(content, resources, statuses),
            };
        }

        private static PlayerRuntime Runtime(
            CharacterResourceService resources,
            float armor = 0f,
            long characterId = 2)
        {
            StatsState stats = new StatsState(new[]
            {
                new KeyValuePair<string, float>("Health.Max", 100f),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", armor),
                new KeyValuePair<string, float>("AttackPower", 0f),
                new KeyValuePair<string, float>("CriticalChance", 0f),
                new KeyValuePair<string, float>("BlockChance", 0f),
            });
            var runtime = new PlayerRuntime(
                new AccountId(1),
                new CharacterId(characterId),
                PlayerSessionId.New(),
                new CharacterState("Alice"),
                new CharacterLocationState("TestMap", string.Empty, new WorldPosition(0, 0, 0), 0),
                0);
            runtime.InitializePlayerItemSystems(
                new InventoryState(1, 0, new ItemInstanceState[1]),
                new EquipmentState(0),
                stats);
            runtime.InitializeCharacterResources(resources.CreateInitialState(stats, 0, null));
            return runtime;
        }

        private static GameplayContentSnapshot Content() => new GameplayContentSnapshot
        {
            revision = 3,
            baseInventoryCapacity = 30,
            equipmentSlots = Array.Empty<EquipmentSlotDefinition>(),
            items = Array.Empty<ItemDefinition>(),
            starterItems = Array.Empty<StarterItemDefinition>(),
            resources = new[]
            {
                Resource(CharacterResourceId.Health, false),
                Resource(CharacterResourceId.Mana, true),
                Resource(CharacterResourceId.Stamina, true),
            },
            combat = new CombatRulesDefinition
            {
                combatStateSeconds = 5f,
                minimumDamageAfterDefense = 1,
                criticalDamageMultiplier = 2f,
                blockDamageMultiplier = 0f,
                maximumCriticalChance = 0.75f,
                maximumBlockChance = 0.75f,
            },
            statusEffects = new[]
            {
                new StatusEffectDefinition
                {
                    definitionId = "status.test.vulnerable",
                    legacyStableId = 1001,
                    displayName = "Vulnerable",
                    classification = StatusEffectClassification.Debuff,
                    allowedActors = GameplayActorAccessMask.Player,
                    durationSeconds = 10f,
                    stackingPolicy = StatusEffectStackingPolicy.AddStacksAndRefresh,
                    maximumStacks = 3,
                    removeOnDeath = true,
                    presentationId = 1,
                    effects = new[]
                    {
                        new GameplayEffectDefinition
                        {
                            kind = GameplayEffectKind.StatModifier,
                            timing = GameplayEffectTiming.WhileActive,
                            statId = "DamageTakenMultiplier",
                            statOperation = StatModifierOperation.Multiply,
                            minValue = 1.5f,
                            maxValue = 1.5f,
                        },
                    },
                },
            },
        };

        private static CharacterResourceDefinition Resource(CharacterResourceId id, bool allowSpending) =>
            new CharacterResourceDefinition
            {
                id = id,
                displayName = id.ToString(),
                tags = id == CharacterResourceId.Health
                    ? CharacterResourceTags.Core | CharacterResourceTags.Combat
                    : CharacterResourceTags.Combat,
                enabled = true,
                minimum = 0,
                baseMaximum = 100,
                maximumStatId = id + ".Max",
                startAtMaximum = true,
                startingValue = 100,
                allowSpending = allowSpending,
                updateMode = CharacterResourceUpdateMode.None,
                updateCondition = CharacterResourceUpdateCondition.Always,
                ratePerSecond = 0f,
                persistence = CharacterResourcePersistenceMode.Character,
                replication = CharacterResourceReplicationMode.OwnerOnly,
                onDeath = CharacterResourceResetMode.KeepCurrent,
                onRespawn = CharacterResourceResetMode.SetToMaximum,
            };
    }
}
