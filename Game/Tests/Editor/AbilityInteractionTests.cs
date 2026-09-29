using System;
using System.Collections.Generic;
using Game.Server.Application.Abilities;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Abilities;
using Game.Shared.Actors;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Identity;
using Game.Shared.Interactions;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class AbilityInteractionTests
    {
        [Test]
        public void BasicAttack_CommitsRecoveryAndRejectsSecondAttackInsideWindow()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 4f);
            PlayerRuntime target = Runtime(services.Resources, 2, 1f, 0f);

            BasicAttackResult first = services.Basic.TryAttack(source, target, 10d);
            BasicAttackResult second = services.Basic.TryAttack(source, target, 10.5d);

            Assert.That(first.Success, Is.True);
            Assert.That(first.RecoveryEnd, Is.EqualTo(12d).Within(0.001d));
            Assert.That(second.Code, Is.EqualTo(BasicAttackResultCode.RejectedRecovery));
            Assert.That(source.CaptureActionState().BasicAttackRecoveryEnd, Is.EqualTo(12d).Within(0.001d));
        }

        [Test]
        public void InstantAbility_SpendsMana_Heals_AndCommitsCooldownByEvent()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            services.Resources.Remove(source, CharacterResourceId.Health, 25, CharacterResourceChangeReason.DamageTaken);
            int actionEvents = 0;
            source.ActionStateChanged += _ => actionEvents++;

            AbilityCastResult result = services.Abilities.TryBeginCast(
                source,
                source,
                "ability.test.heal",
                1,
                source.Location.Position,
                20d);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Phase, Is.EqualTo(AbilityPresentationPhase.CastCompleted));
            Assert.That(source.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health), Is.True);
            Assert.That(source.TryGetCharacterResource(CharacterResourceId.Mana, out _, out var mana), Is.True);
            Assert.That(health.Current, Is.EqualTo(85));
            Assert.That(mana.Current, Is.EqualTo(90));
            Assert.That(source.CaptureActionState().GetCooldownEnd("ability.test.heal"), Is.EqualTo(23d).Within(0.001d));
            Assert.That(actionEvents, Is.EqualTo(1));
        }

        [Test]
        public void TimedAbility_StoresDeadline_AndCompletesOnlyWhenExplicitlyScheduled()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            services.Resources.Remove(source, CharacterResourceId.Health, 20, CharacterResourceChangeReason.DamageTaken);

            AbilityCastResult started = services.Abilities.TryBeginCast(
                source,
                source,
                "ability.test.cast_heal",
                1,
                source.Location.Position,
                30d);

            Assert.That(started.Success, Is.True);
            Assert.That(started.Phase, Is.EqualTo(AbilityPresentationPhase.CastStarted));
            Assert.That(source.CaptureActionState().ActiveCast.IsActive, Is.True);
            Assert.That(started.CompletesAt, Is.EqualTo(31.5d).Within(0.001d));

            AbilityCastResult completed = services.Abilities.TryCompleteCast(source, source, started.CastId, 31.5d);
            Assert.That(completed.Success, Is.True);
            Assert.That(completed.Phase, Is.EqualTo(AbilityPresentationPhase.CastCompleted));
            Assert.That(source.CaptureActionState().ActiveCast.IsActive, Is.False);
        }

        [Test]
        public void InteractionCore_FailsClosedWhenNoGameplaySystemOwnsAction()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            PlayerRuntime target = Runtime(services.Resources, 2, 0f, 0f);

            InteractionResult result = services.Interactions.ExecutePlayerAction(
                source,
                target,
                InteractionActionId.TradeRequest,
                42,
                5d);

            Assert.That(result.ResultCode, Is.EqualTo(InteractionResultCode.Unsupported));
            Assert.That(result.Sequence, Is.EqualTo(42));
        }

        [Test]
        public void InteractionInspect_SucceedsThroughRegisteredAuthoritativeHandler()
        {
            Services services = CreateServices();
            Assert.That(services.Interactions.Register(new InspectPlayerInteractionHandler()), Is.True);

            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            PlayerRuntime target = Runtime(services.Resources, 2, 0f, 0f);

            InteractionResult result = services.Interactions.ExecutePlayerAction(
                source,
                target,
                InteractionActionId.Inspect,
                7,
                5d);

            Assert.That(result.ResultCode, Is.EqualTo(InteractionResultCode.Success));
            Assert.That(result.Target.PrimaryId, Is.EqualTo(target.CharacterId.Value));
        }

        [Test]
        public void PlayerInteractionRange_UsesPlanarDistanceLikeProductionPeerInteractions()
        {
            Services services = CreateServices();
            Assert.That(services.Interactions.Register(new InspectPlayerInteractionHandler()), Is.True);

            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            PlayerRuntime target = Runtime(services.Resources, 2, 0f, 0f);
            target.UpdateLocation(new CharacterLocationState(
                "TestMap",
                string.Empty,
                new WorldPosition(1f, 100f, 0f),
                0f));

            InteractionResult result = services.Interactions.ExecutePlayerAction(
                source,
                target,
                InteractionActionId.Inspect,
                8,
                5d);

            Assert.That(result.ResultCode, Is.EqualTo(InteractionResultCode.Success));
        }

        [Test]
        public void InteractionDiscovery_ExposesOnlyRegisteredAuthoritativeActions()
        {
            Services services = CreateServices();
            Assert.That(services.Interactions.Register(new InspectPlayerInteractionHandler()), Is.True);
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            PlayerRuntime target = Runtime(services.Resources, 2, 0f, 0f);

            InteractionActionSet menu = services.Interactions.DiscoverPlayerActions(source, target, 5d);

            Assert.That(menu.Actions.Length, Is.EqualTo(1));
            Assert.That(menu.Actions[0].ActionId, Is.EqualTo(InteractionActionId.Inspect));
            Assert.That(menu.Actions[0].Availability, Is.EqualTo(InteractionAvailability.Available));
            Assert.That(menu.Actions[0].Label, Is.EqualTo("Inspect"));
        }

        [Test]
        public void InteractionDiscovery_ReturnsDisabledReasonWhenAuthoritativeRangeFails()
        {
            Services services = CreateServices();
            Assert.That(services.Interactions.Register(new InspectPlayerInteractionHandler()), Is.True);
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            PlayerRuntime target = Runtime(services.Resources, 2, 0f, 0f);
            target.UpdateLocation(new CharacterLocationState(
                "TestMap",
                string.Empty,
                new WorldPosition(100f, 0f, 0f),
                0f));

            InteractionActionSet menu = services.Interactions.DiscoverPlayerActions(source, target, 5d);

            Assert.That(menu.Actions.Length, Is.EqualTo(1));
            Assert.That(menu.Actions[0].Availability, Is.EqualTo(InteractionAvailability.Disabled));
            Assert.That(menu.Actions[0].DisabledReason, Does.Contain("out of range"));
        }

        [Test]
        public void WorldInteraction_HardRangeRejectsBeyondThreePointTwoFiveMetres()
        {
            Services services = CreateServices();
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);
            var target = new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, 99);

            InteractionResult result = services.Interactions.ExecuteWorldActionAsync(
                source,
                target,
                "TestMap",
                string.Empty,
                new WorldPosition(InteractionRangePolicy.WorldObjectUseRange + 0.01f, 0f, 0f),
                InteractionActionId.Loot,
                10,
                5d,
                default).GetAwaiter().GetResult();

            Assert.That(result.ResultCode, Is.EqualTo(InteractionResultCode.OutOfRange));
        }

        [Test]
        public void PlayerInteraction_RejectsSelfTarget()
        {
            Services services = CreateServices();
            Assert.That(services.Interactions.Register(new InspectPlayerInteractionHandler()), Is.True);
            PlayerRuntime source = Runtime(services.Resources, 1, 0f, 0f);

            InteractionResult result = services.Interactions.ExecutePlayerAction(
                source,
                source,
                InteractionActionId.Inspect,
                9,
                5d);

            Assert.That(result.ResultCode, Is.EqualTo(InteractionResultCode.InvalidTarget));
        }

        private sealed class Services
        {
            public CharacterResourceService Resources;
            public CombatService Combat;
            public BasicAttackService Basic;
            public AbilityService Abilities;
            public InteractionService Interactions;
        }

        private static Services CreateServices()
        {
            var content = new GameplayContentCatalog(Content());
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            var combat = new CombatService(content, resources, statuses);
            return new Services
            {
                Resources = resources,
                Combat = combat,
                Basic = new BasicAttackService(content, combat),
                Abilities = new AbilityService(content, resources, combat, statuses),
                Interactions = new InteractionService(),
            };
        }

        private static PlayerRuntime Runtime(
            CharacterResourceService resources,
            long characterId,
            float armor,
            float attackPower)
        {
            StatsState stats = new StatsState(new[]
            {
                new KeyValuePair<string, float>("Health.Max", 100f),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", armor),
                new KeyValuePair<string, float>("AttackPower", attackPower),
                new KeyValuePair<string, float>("CriticalChance", 0f),
                new KeyValuePair<string, float>("BlockChance", 0f),
            });
            var runtime = new PlayerRuntime(
                new AccountId(characterId),
                new CharacterId(characterId),
                PlayerSessionId.New(),
                new CharacterState("P" + characterId),
                new CharacterLocationState("TestMap", string.Empty, new WorldPosition(characterId - 1, 0, 0), 0),
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
            revision = 4,
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
                maximumCriticalChance = 0f,
                maximumBlockChance = 0f,
                basicAttackRange = 2f,
                basicAttackInterval = 2f,
                unarmedBasicAttackDamage = 1,
            },
            statusEffects = Array.Empty<StatusEffectDefinition>(),
            abilities = new[]
            {
                new AbilityDefinition
                {
                    definitionId = "ability.test.heal",
                    displayName = "Test Heal",
                    allowedActors = GameplayActorAccessMask.Player,
                    category = AbilityCategory.Healing,
                    targetMode = AbilityTargetMode.Self,
                    targetRelation = AbilityTargetRelation.Self,
                    castTimeSeconds = 0f,
                    cooldownPolicy = AbilityCooldownPolicy.AbilityDefined,
                    cooldownSeconds = 3f,
                    range = 0f,
                    resourceId = CharacterResourceId.Mana,
                    resourceCost = 10,
                    effects = new[]
                    {
                        new GameplayEffectDefinition { kind = GameplayEffectKind.Heal, minValue = 10f, maxValue = 10f },
                    },
                },
                new AbilityDefinition
                {
                    definitionId = "ability.test.cast_heal",
                    displayName = "Test Cast Heal",
                    allowedActors = GameplayActorAccessMask.Player,
                    category = AbilityCategory.Healing,
                    targetMode = AbilityTargetMode.Self,
                    targetRelation = AbilityTargetRelation.Self,
                    castTimeSeconds = 1.5f,
                    cooldownPolicy = AbilityCooldownPolicy.None,
                    cooldownSeconds = 0f,
                    range = 0f,
                    resourceId = CharacterResourceId.Mana,
                    resourceCost = 0,
                    effects = new[]
                    {
                        new GameplayEffectDefinition { kind = GameplayEffectKind.Heal, minValue = 10f, maxValue = 10f },
                    },
                },
            },
        };

        private static CharacterResourceDefinition Resource(CharacterResourceId id, bool allowSpending) =>
            new CharacterResourceDefinition
            {
                id = id,
                displayName = id.ToString(),
                tags = CharacterResourceTags.Core | CharacterResourceTags.Combat,
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
