using System;
using System.Collections.Generic;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Networking;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only resolution from compact server presentation ids to concrete modular
    /// character options. Authored wearables now resolve directly by their existing
    /// CharacterWearableSetDefinition.presentationId. Legacy hardcoded mappings remain only
    /// as a compatibility fallback for content that has not yet been migrated.
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

        private readonly struct ResolvedPart
        {
            public readonly ushort optionId;
            public readonly int priority;
            public readonly int equipmentOrder;

            public ResolvedPart(ushort optionId, int priority, int equipmentOrder)
            {
                this.optionId = optionId;
                this.priority = priority;
                this.equipmentOrder = equipmentOrder;
            }
        }

        // Legacy compatibility only. Newly-authored items are resolved from the existing
        // CharacterVisualProfile.WearableSets collection by presentation id.
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

            var selectedBySlot = new Dictionary<ushort, ResolvedPart>();
            for (int i = 0; i < equipmentVisuals.Length; ++i)
            {
                PlayerEquipmentVisualSelection equipped = equipmentVisuals[i];
                if (equipped.itemPresentationId == 0)
                    continue;

                if (TryFindAuthoredWearable(profile, equipped.itemPresentationId, out CharacterWearableSetDefinition wearable))
                {
                    CharacterWearablePartSelection[] parts = wearable.parts ?? Array.Empty<CharacterWearablePartSelection>();
                    for (int p = 0; p < parts.Length; ++p)
                    {
                        CharacterWearablePartSelection part = parts[p];
                        if (part.slotId == 0 || part.optionId == 0 ||
                            !profile.TryGetOption(part.slotId, part.optionId, out CharacterVisualOptionDefinition option) ||
                            option == null || option.mesh == null || !option.AllowsPlayerUse)
                            continue;

                        int priority = ResolveBodySlotPriority(wearable.region, part.slotId);
                        AssignPart(selectedBySlot, part.slotId, part.optionId, priority, i);
                    }
                    continue;
                }

                // Preserve the pre-authoring fallback for current content that has not yet
                // received a wearable record in CharacterVisualProfile.
                ModularSet definition = FindModularSet(
                    equipped.equipmentSlotPresentationId,
                    equipped.itemPresentationId);
                if (definition == null)
                    continue;

                for (int slotIndex = 0; slotIndex < definition.SlotIds.Length; ++slotIndex)
                {
                    ushort slotId = definition.SlotIds[slotIndex];
                    if (TryResolveSetOption(profile, slotId, definition.SetKey, out ushort optionId))
                    {
                        int priority = ResolveBodySlotPriority(CharacterWearableRegion.Upper, slotId);
                        AssignPart(selectedBySlot, slotId, optionId, priority, i);
                    }
                }
            }

            if (selectedBySlot.Count == 0)
                return Array.Empty<CharacterMeshSelection>();

            var keys = new List<ushort>(selectedBySlot.Keys);
            keys.Sort();
            var result = new CharacterMeshSelection[keys.Count];
            for (int i = 0; i < keys.Count; ++i)
            {
                ushort slotId = keys[i];
                result[i] = new CharacterMeshSelection(slotId, selectedBySlot[slotId].optionId);
            }
            return result;
        }

        /// <summary>
        /// Per-body-slot ownership makes overlapping equipment compose predictably:
        /// pants own lower legs over boots, while boots own feet over pants. Gloves own hands
        /// over upper-body clothing. A lower-priority item's supporting mesh still appears when
        /// no higher-priority item occupies that body slot.
        /// </summary>
        private static int ResolveBodySlotPriority(CharacterWearableRegion sourceRegion, ushort bodySlotId)
        {
            // Torso + arms.
            if (bodySlotId >= 10 && bodySlotId <= 14)
            {
                switch (sourceRegion)
                {
                    case CharacterWearableRegion.Upper: return 400;
                    case CharacterWearableRegion.Accessory: return 300;
                    case CharacterWearableRegion.Lower: return 200;
                    case CharacterWearableRegion.Feet: return 100;
                }
            }

            // Hands: dedicated glove/accessory content wins over sleeves.
            if (bodySlotId == 15 || bodySlotId == 16)
            {
                switch (sourceRegion)
                {
                    case CharacterWearableRegion.Accessory: return 500;
                    case CharacterWearableRegion.Upper: return 400;
                    case CharacterWearableRegion.Lower: return 200;
                    case CharacterWearableRegion.Feet: return 100;
                }
            }

            // Hips + legs: pants/lower-body content wins, then footwear support geometry.
            if (bodySlotId >= 17 && bodySlotId <= 19)
            {
                switch (sourceRegion)
                {
                    case CharacterWearableRegion.Lower: return 500;
                    case CharacterWearableRegion.Feet: return 400;
                    case CharacterWearableRegion.Upper: return 200;
                    case CharacterWearableRegion.Accessory: return 100;
                }
            }

            // Feet: footwear wins, with lower-body meshes as fallback only.
            if (bodySlotId == 20 || bodySlotId == 21)
            {
                switch (sourceRegion)
                {
                    case CharacterWearableRegion.Feet: return 500;
                    case CharacterWearableRegion.Lower: return 400;
                    case CharacterWearableRegion.Accessory: return 200;
                    case CharacterWearableRegion.Upper: return 100;
                }
            }

            // Attachment slots are primarily accessory-owned.
            switch (sourceRegion)
            {
                case CharacterWearableRegion.Accessory: return 500;
                case CharacterWearableRegion.Upper: return 300;
                case CharacterWearableRegion.Lower: return 200;
                case CharacterWearableRegion.Feet: return 100;
                default: return 0;
            }
        }

        private static void AssignPart(
            Dictionary<ushort, ResolvedPart> selectedBySlot,
            ushort slotId,
            ushort optionId,
            int priority,
            int equipmentOrder)
        {
            if (!selectedBySlot.TryGetValue(slotId, out ResolvedPart existing) ||
                priority > existing.priority ||
                (priority == existing.priority && equipmentOrder >= existing.equipmentOrder))
            {
                selectedBySlot[slotId] = new ResolvedPart(optionId, priority, equipmentOrder);
            }
        }

        private static bool TryFindAuthoredWearable(
            CharacterVisualProfile profile,
            ushort itemPresentationId,
            out CharacterWearableSetDefinition wearable)
        {
            wearable = null;
            if (profile == null || itemPresentationId == 0)
                return false;

            IReadOnlyList<CharacterWearableSetDefinition> values = profile.WearableSets;
            for (int i = 0; i < values.Count; ++i)
            {
                CharacterWearableSetDefinition candidate = values[i];
                if (candidate != null && candidate.playerEligible &&
                    candidate.presentationId == itemPresentationId)
                {
                    wearable = candidate;
                    return true;
                }
            }
            return false;
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
