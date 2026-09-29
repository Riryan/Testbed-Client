using System;
using System.Collections.Generic;
using Game.Server.Application.Abilities;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.Stats;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// Contract tests for client-predicted/server-authoritative basic attacks. These tests
    /// deliberately call the server service directly: no client-supplied damage, ammo type,
    /// resource cost, combo step, or move id exists in the request contract.
    /// </summary>
    public sealed class CombatIntentAuthorityTests
    {
        [Test]
        public void UnarmedLight_ServerSpendsStaminaAndOwnsComboStep()
        {
            Fixture fixture = CreateFixture();
            PlayerRuntime source = Runtime(fixture.Resources, 10, 0f, null);
            PlayerRuntime target = Runtime(fixture.Resources, 11, 1f, null);

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 10d);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Mode, Is.EqualTo(BasicAttackMode.Unarmed));
            Assert.That(result.InputKind, Is.EqualTo(BasicAttackInputKind.Light));
            Assert.That(result.ComboStep, Is.EqualTo(1));
            Assert.That(Resource(source, CharacterResourceId.Stamina).Current, Is.EqualTo(95));
            Assert.That(Resource(target, CharacterResourceId.Health).Current, Is.LessThan(100));

            CombatOwnerStateSnapshot owner = fixture.Loadout.Capture(source, 10d);
            Assert.That(owner.ComboStep, Is.EqualTo(1));
        }

        [Test]
        public void UnarmedHardRange_UsesPlanarReachDespiteTargetPivotHeight()
        {
            Fixture fixture = CreateFixture();
            PlayerRuntime source = RuntimeAt(fixture.Resources, 12, 0f, 0f, null);
            PlayerRuntime target = RuntimeAt(fixture.Resources, 13, 1.95f, 0.82f, null);

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 12d);

            Assert.That(result.Success, Is.True);
        }

        [Test]
        public void UnarmedHardRange_RejectsBeyondTwoMetres()
        {
            Fixture fixture = CreateFixture();
            PlayerRuntime source = Runtime(fixture.Resources, 14, 0f, null);
            PlayerRuntime target = Runtime(fixture.Resources, 15, CombatRangePolicy.UnarmedRange + 0.01f, null);

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 14d);

            Assert.That(result.Code, Is.EqualTo(BasicAttackResultCode.RejectedOutOfRange));
            Assert.That(Resource(target, CharacterResourceId.Health).Current, Is.EqualTo(100));
        }

        [Test]
        public void UnarmedHeavy_ServerRejectsWhenAuthoritativeStaminaIsInsufficient()
        {
            Fixture fixture = CreateFixture();
            PlayerRuntime source = Runtime(fixture.Resources, 20, 0f, null);
            PlayerRuntime target = Runtime(fixture.Resources, 21, 1f, null);
            Assert.That(fixture.Resources.Set(
                source,
                CharacterResourceId.Stamina,
                4,
                CharacterResourceChangeReason.Administrative).Success, Is.True);

            int healthBefore = Resource(target, CharacterResourceId.Health).Current;
            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Heavy, 20d);

            Assert.That(result.Code, Is.EqualTo(BasicAttackResultCode.RejectedInsufficientResource));
            Assert.That(Resource(source, CharacterResourceId.Stamina).Current, Is.EqualTo(4));
            Assert.That(Resource(target, CharacterResourceId.Health).Current, Is.EqualTo(healthBefore));
            Assert.That(fixture.Loadout.Capture(source, 20d).ComboStep, Is.EqualTo(0));
        }

        [Test]
        public void Firearm_ServerConsumesMagazineAndResolvesAmmoDamageType()
        {
            Fixture fixture = CreateFixture();
            ItemInstanceState weaponInstance = new ItemInstanceState(
                new ItemInstanceId(1001), "weapon.test.pistol", 1, 100, 0);
            PlayerRuntime source = Runtime(fixture.Resources, 30, 0f, weaponInstance);
            PlayerRuntime target = Runtime(
                fixture.Resources,
                31,
                2f,
                null,
                new KeyValuePair<string, float>(CombatDerivedStatIds.DamageResponseMultiplier(2), 1.5f));

            CombatOwnerStateSnapshot loaded = fixture.Loadout.ApplyReload(
                source, "ammo.test.electric", 2, 29d);
            Assert.That(loaded.Mode, Is.EqualTo(BasicAttackMode.Firearm));
            Assert.That(loaded.LoadedRounds, Is.EqualTo(2));

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 30d);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Mode, Is.EqualTo(BasicAttackMode.Firearm));
            Assert.That(result.Damage.damageTypeId, Is.EqualTo(2));
            Assert.That(result.Damage.dealtAmount, Is.EqualTo(30));
            Assert.That(result.Damage.presentationFlags.HasFlag(CombatDamagePresentationFlags.Weakness), Is.True);

            CombatOwnerStateSnapshot after = fixture.Loadout.Capture(source, 30d);
            Assert.That(after.LoadedRounds, Is.EqualTo(1));
            Assert.That(after.LoadedAmmoDefinitionId, Is.EqualTo("ammo.test.electric"));
        }

        [Test]
        public void FirearmHardRange_RejectsBeyondAuthoredWeaponRangeBeforeConsumingAmmo()
        {
            Fixture fixture = CreateFixture();
            ItemInstanceState weaponInstance = new ItemInstanceState(
                new ItemInstanceId(1501), "weapon.test.pistol", 1, 100, 0);
            PlayerRuntime source = Runtime(fixture.Resources, 32, 0f, weaponInstance);
            PlayerRuntime target = Runtime(fixture.Resources, 33, 20.01f, null);
            fixture.Loadout.ApplyReload(source, "ammo.test.electric", 2, 31d);

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 32d);

            Assert.That(result.Code, Is.EqualTo(BasicAttackResultCode.RejectedOutOfRange));
            Assert.That(fixture.Loadout.Capture(source, 32d).LoadedRounds, Is.EqualTo(2));
        }

        [Test]
        public void Firearm_EmptyMagazineRejectsWithoutManufacturingDamageOrRounds()
        {
            Fixture fixture = CreateFixture();
            ItemInstanceState weaponInstance = new ItemInstanceState(
                new ItemInstanceId(2001), "weapon.test.pistol", 1, 100, 0);
            PlayerRuntime source = Runtime(fixture.Resources, 40, 0f, weaponInstance);
            PlayerRuntime target = Runtime(fixture.Resources, 41, 2f, null);
            int before = Resource(target, CharacterResourceId.Health).Current;

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Light, 40d);

            Assert.That(result.Code, Is.EqualTo(BasicAttackResultCode.RejectedNoAmmo));
            Assert.That(Resource(target, CharacterResourceId.Health).Current, Is.EqualTo(before));
            Assert.That(fixture.Loadout.Capture(source, 40d).LoadedRounds, Is.EqualTo(0));
        }

        [Test]
        public void WeaponHeavyInput_IsRejectedInsteadOfTrustingClientSelectedMove()
        {
            Fixture fixture = CreateFixture();
            ItemInstanceState weaponInstance = new ItemInstanceState(
                new ItemInstanceId(3001), "weapon.test.pistol", 1, 100, 0);
            PlayerRuntime source = Runtime(fixture.Resources, 50, 0f, weaponInstance);
            PlayerRuntime target = Runtime(fixture.Resources, 51, 2f, null);
            fixture.Loadout.ApplyReload(source, "ammo.test.electric", 2, 49d);

            BasicAttackResult result = fixture.Attacks.TryAttack(
                source, target, BasicAttackInputKind.Heavy, 50d);

            Assert.That(result.Code, Is.EqualTo(BasicAttackResultCode.RejectedInvalidInput));
            Assert.That(fixture.Loadout.Capture(source, 50d).LoadedRounds, Is.EqualTo(2));
        }

        private sealed class Fixture
        {
            public GameplayContentCatalog Content;
            public CharacterResourceService Resources;
            public StatusEffectService Statuses;
            public CombatService Combat;
            public CombatLoadoutService Loadout;
            public BasicAttackService Attacks;
        }

        private static Fixture CreateFixture()
        {
            var content = new GameplayContentCatalog(Content());
            var resources = new CharacterResourceService(content);
            var statuses = new StatusEffectService(content);
            var combat = new CombatService(content, resources, statuses);
            var loadout = new CombatLoadoutService(content);
            return new Fixture
            {
                Content = content,
                Resources = resources,
                Statuses = statuses,
                Combat = combat,
                Loadout = loadout,
                Attacks = new BasicAttackService(content, combat, resources, loadout),
            };
        }

        private static PlayerRuntime Runtime(
            CharacterResourceService resources,
            long characterId,
            float x,
            ItemInstanceState mainHand,
            params KeyValuePair<string, float>[] extraStats) =>
            RuntimeAt(resources, characterId, x, 0f, mainHand, extraStats);

        private static PlayerRuntime RuntimeAt(
            CharacterResourceService resources,
            long characterId,
            float x,
            float y,
            ItemInstanceState mainHand,
            params KeyValuePair<string, float>[] extraStats)
        {
            var statsList = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Health.Max", 100f),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", 0f),
                new KeyValuePair<string, float>("AttackPower", 10f),
                new KeyValuePair<string, float>("CriticalChance", 0f),
                new KeyValuePair<string, float>("BlockChance", 0f),
            };
            if (extraStats != null)
                statsList.AddRange(extraStats);
            StatsState stats = new StatsState(statsList);

            var runtime = new PlayerRuntime(
                new AccountId(characterId),
                new CharacterId(characterId),
                PlayerSessionId.New(),
                new CharacterState("CombatTest" + characterId),
                new CharacterLocationState("TestMap", string.Empty, new WorldPosition(x, y, 0), 0),
                0);

            EquipmentState equipment = mainHand == null
                ? new EquipmentState(0)
                : new EquipmentState(0, new[] { new EquippedItemState("MainHand", mainHand) });
            runtime.InitializePlayerItemSystems(
                new InventoryState(4, 0, new ItemInstanceState[4]),
                equipment,
                stats);
            runtime.InitializeCharacterResources(resources.CreateInitialState(stats, 0, null));
            return runtime;
        }

        private static CharacterResourceState Resource(PlayerRuntime runtime, CharacterResourceId id)
        {
            Assert.That(runtime.TryGetCharacterResource(id, out _, out CharacterResourceState state), Is.True);
            return state;
        }

        private static GameplayContentSnapshot Content() => new GameplayContentSnapshot
        {
            revision = 100,
            baseInventoryCapacity = 30,
            equipmentSlots = new[]
            {
                new EquipmentSlotDefinition
                {
                    slotId = "MainHand",
                    displayName = "Main Hand",
                    presentationSlotId = 1,
                },
            },
            items = new[]
            {
                new ItemDefinition
                {
                    definitionId = "weapon.test.pistol",
                    displayName = "Authority Test Pistol",
                    kind = ItemKind.Equipment,
                    subtype = EquipmentItemSubtype.Weapon,
                    maxStack = 1,
                    maxDurability = 100,
                    allowedEquipmentSlots = new[] { "MainHand" },
                    damageMin = 7f,
                    damageMax = 7f,
                    damageTypeId = 1,
                    canCrit = false,
                    ammoFamily = "ammo.test",
                    firearmMagazineCapacity = 6,
                    basicAttackRange = 20f,
                    basicAttackInterval = BasicAttackCadenceTiming.MinimumInterval,
                },
                new ItemDefinition
                {
                    definitionId = "ammo.test.electric",
                    displayName = "Authority Test Electric Ammo",
                    kind = ItemKind.Ammo,
                    subtype = EquipmentItemSubtype.None,
                    maxStack = 100,
                    damageMin = 20f,
                    damageMax = 20f,
                    damageTypeId = 2,
                    canCrit = false,
                    ammoFamily = "ammo.test",
                },
            },
            starterItems = Array.Empty<StarterItemDefinition>(),
            resources = new[]
            {
                ResourceDefinition(CharacterResourceId.Health, false),
                ResourceDefinition(CharacterResourceId.Mana, true),
                ResourceDefinition(CharacterResourceId.Stamina, true),
            },
            damageTypes = new[]
            {
                new DamageTypeDefinition { wireId = 1, definitionId = "damage.physical", displayName = "Physical", defenseStatId = "Armor" },
                new DamageTypeDefinition { wireId = 2, definitionId = "damage.electric", displayName = "Electric", defenseStatId = "Armor" },
            },
            combat = new CombatRulesDefinition
            {
                combatStateSeconds = 5f,
                minimumDamageAfterDefense = 1,
                criticalDamageMultiplier = 2f,
                blockDamageMultiplier = 0f,
                maximumCriticalChance = 0.75f,
                maximumBlockChance = 0.75f,
                minimumDamageResponseMultiplier = 0f,
                maximumDamageResponseMultiplier = 3f,
                basicAttackRange = 2f,
                basicAttackInterval = BasicAttackCadenceTiming.StandardInterval,
                unarmedBasicAttackDamage = 10,
                unarmedDamageTypeId = 1,
                unarmedLightStaminaCost = 5,
                unarmedHeavyStaminaCost = 10,
                unarmedHeavyDamageMultiplier = 1.5f,
                unarmedMaximumComboStep = 5,
                unarmedComboWindowSeconds = 2.5f,
                unarmedComboDamagePerStep = 0.05f,
            },
            statusEffects = Array.Empty<StatusEffectDefinition>(),
            abilities = Array.Empty<AbilityDefinition>(),
        };

        private static CharacterResourceDefinition ResourceDefinition(CharacterResourceId id, bool allowSpending) =>
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
