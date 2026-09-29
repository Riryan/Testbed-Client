using System;
using System.Collections.Generic;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Networking;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only resolution from compact server presentation ids to concrete modular
    /// character options. The server never knows mesh names, FBXs, materials or bones.
    ///
    /// V1 contains the currently-available Testbed clothing mapping. Weapon presentation ids
    /// are already transported by the same contract, but this source archive contains no
    /// weapon model prefabs to bind yet; those can be added here/through a later authored
    /// catalog without changing the network contract.
    /// </summary>
    internal static class PlayerEquipmentVisualCatalog
    {
        private sealed class ModularSet
        {
            public readonly ushort ItemPresentationId;
            public readonly ushort EquipmentSlotPresentationId;
            public readonly string SetKey;
            public readonly ushort[] SlotIds;

            public ModularSet(
                ushort itemPresentationId,
                ushort equipmentSlotPresentationId,
                string setKey,
                params ushort[] slotIds)
            {
                ItemPresentationId = itemPresentationId;
                EquipmentSlotPresentationId = equipmentSlotPresentationId;
                SetKey = setKey;
                SlotIds = slotIds ?? Array.Empty<ushort>();
            }
        }

        // Current standalone content:
        //   Chest slot presentation id = 2
        //   Leather Chest Armor presentation id = 201
        // Use one coherent Sidekick donor family across torso/arms/hands so the equipped
        // top reads as one outfit instead of mixing unrelated modular pieces.
        private static readonly ModularSet[] ModularSets =
        {
            new ModularSet(201, 2, "SK_APOC_OUTL_01", 10, 11, 12, 13, 14, 15, 16),
        };

        public static CharacterMeshSelection[] ResolveMeshOverrides(
            CharacterVisualProfile profile,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            if (profile == null || equipmentVisuals == null || equipmentVisuals.Length == 0)
                return Array.Empty<CharacterMeshSelection>();

            var selectedBySlot = new Dictionary<ushort, ushort>();
            for (int i = 0; i < equipmentVisuals.Length; ++i)
            {
                PlayerEquipmentVisualSelection equipped = equipmentVisuals[i];
                if (equipped.itemPresentationId == 0)
                    continue;

                ModularSet definition = FindModularSet(
                    equipped.equipmentSlotPresentationId,
                    equipped.itemPresentationId);
                if (definition == null)
                    continue;

                for (int slotIndex = 0; slotIndex < definition.SlotIds.Length; ++slotIndex)
                {
                    ushort slotId = definition.SlotIds[slotIndex];
                    if (TryResolveSetOption(profile, slotId, definition.SetKey, out ushort optionId))
                        selectedBySlot[slotId] = optionId;
                }
            }

            if (selectedBySlot.Count == 0)
                return Array.Empty<CharacterMeshSelection>();

            var result = new CharacterMeshSelection[selectedBySlot.Count];
            int output = 0;
            foreach (KeyValuePair<ushort, ushort> pair in selectedBySlot)
                result[output++] = new CharacterMeshSelection(pair.Key, pair.Value);
            return result;
        }

        public static HumanoidCombatStance ResolveCombatStance(
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            if (equipmentVisuals == null || equipmentVisuals.Length == 0)
                return HumanoidCombatStance.Unarmed;

            HumanoidCombatStance resolved = HumanoidCombatStance.Unarmed;
            int meleeWeaponCount = 0;
            int pistolCount = 0;
            bool hasShield = false;

            for (int i = 0; i < equipmentVisuals.Length; ++i)
            {
                ushort presentationId = equipmentVisuals[i].itemPresentationId;
                if (!PlayerGameplaySettingsRuntime.TryGetItemByPresentationId(
                        presentationId, out GameplayItemReferenceWire item))
                    continue;

                HumanoidCombatStance candidate =
                    HumanoidCombatStanceResolver.ResolveFromDefinitionId(item.definitionId);
                if (candidate == HumanoidCombatStance.None)
                    continue;

                if (candidate == HumanoidCombatStance.Shield)
                {
                    hasShield = true;
                    continue;
                }

                if (candidate == HumanoidCombatStance.Pistol)
                    pistolCount++;
                if (IsMeleeWeaponStance(candidate))
                    meleeWeaponCount++;

                if (resolved == HumanoidCombatStance.Unarmed)
                    resolved = candidate;
            }

            if (pistolCount >= 2)
                return HumanoidCombatStance.DualPistol;
            if (meleeWeaponCount >= 2 && IsOneHandMeleeStance(resolved))
                return HumanoidCombatStance.DualWieldMelee;
            if (hasShield)
                return HumanoidCombatStance.Shield;
            return resolved;
        }

        private static bool IsMeleeWeaponStance(HumanoidCombatStance stance)
        {
            switch (stance)
            {
                case HumanoidCombatStance.Bat:
                case HumanoidCombatStance.SmallBlade:
                case HumanoidCombatStance.OneHandBlade:
                case HumanoidCombatStance.TwoHandBlade:
                case HumanoidCombatStance.SmallBlunt:
                case HumanoidCombatStance.OneHandBlunt:
                case HumanoidCombatStance.TwoHandBlunt:
                case HumanoidCombatStance.FistWeapon:
                case HumanoidCombatStance.OneHandAxe:
                case HumanoidCombatStance.TwoHandAxe:
                case HumanoidCombatStance.Polearm:
                case HumanoidCombatStance.Staff:
                case HumanoidCombatStance.ImprovisedOneHand:
                case HumanoidCombatStance.ImprovisedTwoHand:
                case HumanoidCombatStance.Stake:
                case HumanoidCombatStance.NaturalWeapon:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsOneHandMeleeStance(HumanoidCombatStance stance)
        {
            switch (stance)
            {
                case HumanoidCombatStance.Bat:
                case HumanoidCombatStance.SmallBlade:
                case HumanoidCombatStance.OneHandBlade:
                case HumanoidCombatStance.SmallBlunt:
                case HumanoidCombatStance.OneHandBlunt:
                case HumanoidCombatStance.FistWeapon:
                case HumanoidCombatStance.OneHandAxe:
                case HumanoidCombatStance.ImprovisedOneHand:
                case HumanoidCombatStance.Stake:
                case HumanoidCombatStance.NaturalWeapon:
                    return true;
                default:
                    return false;
            }
        }

        private static ModularSet FindModularSet(ushort equipmentSlotPresentationId, ushort itemPresentationId)
        {
            for (int i = 0; i < ModularSets.Length; ++i)
            {
                ModularSet value = ModularSets[i];
                if (value.ItemPresentationId == itemPresentationId &&
                    (value.EquipmentSlotPresentationId == 0 ||
                     value.EquipmentSlotPresentationId == equipmentSlotPresentationId))
                    return value;
            }
            return null;
        }

        private static bool TryResolveSetOption(
            CharacterVisualProfile profile,
            ushort slotId,
            string setKey,
            out ushort optionId)
        {
            optionId = 0;
            if (!profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                return false;

            CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            for (int i = 0; i < options.Length; ++i)
            {
                CharacterVisualOptionDefinition option = options[i];
                if (option == null || option.optionId == 0 || option.mesh == null ||
                    string.IsNullOrEmpty(option.rendererPath))
                    continue;

                string token = setKey + "_" + slot.slotCode + "_";
                if (option.rendererPath.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    optionId = option.optionId;
                    return true;
                }
            }
            return false;
        }
    }
}
