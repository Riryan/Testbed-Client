using System;
using System.Collections.Generic;
using Game.Shared.Characters;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only deterministic ambient Population appearance construction.
    ///
    /// The server already sends the canonical Population actor id through the existing
    /// PlayerEntity appearance envelope. That id is the only seed required here; concrete
    /// mesh, morph and palette choices remain client-local catalog data and therefore add
    /// no Population appearance payload to the wire.
    /// </summary>
    internal static class PopulationAppearanceRecipeBuilder
    {
        private const ulong MeshDomain = 0x4D455348UL;       // MESH
        private const ulong OptionalDomain = 0x4F50544EUL;   // OPTN
        private const ulong MorphDomain = 0x4D4F5250UL;      // MORP
        private const ulong ColorDomain = 0x434F4C52UL;      // COLR
        private const ulong BodyArchetypeDomain = 0x424F4459UL; // BODY
        private const ulong NamePoolDomain = 0x4E504F4CUL;      // NPOL
        private const ulong FirstNameDomain = 0x4E464952UL;     // NFIR
        private const ulong SurnameDomain = 0x4E535552UL;       // NSUR

        // Static client-known content. Ambient names are deterministic from actorId and
        // therefore require no name string/id/category on the wire. Keep these catalogs
        // ordered: changing existing order intentionally changes generated ambient names.
        private static readonly string[] MaleFirstNames =
        {
            "Aaron", "Adam", "Adrian", "Alex", "Andre", "Anthony", "Benjamin", "Blake",
            "Brandon", "Caleb", "Cameron", "Charles", "Daniel", "Darius", "David", "Derek",
            "Dominic", "Elias", "Ethan", "Gabriel", "Gavin", "Henry", "Isaac", "Jack",
            "James", "Jason", "Julian", "Leo", "Liam", "Logan", "Marcus", "Mason",
            "Nathan", "Nicholas", "Noah", "Oliver", "Owen", "Ryan", "Samuel", "Thomas"
        };

        private static readonly string[] FemaleFirstNames =
        {
            "Abigail", "Alexis", "Amelia", "Anna", "Avery", "Brooke", "Camila", "Chloe",
            "Claire", "Danielle", "Elena", "Ella", "Emily", "Emma", "Eva", "Gabrielle",
            "Grace", "Hannah", "Isabella", "Jasmine", "Julia", "Katherine", "Leah", "Lily",
            "Madeline", "Maya", "Natalie", "Nora", "Olivia", "Rachel", "Rebecca", "Samantha",
            "Sarah", "Sophia", "Stella", "Taylor", "Vanessa", "Victoria", "Violet", "Zoe"
        };

        private static readonly string[] Surnames =
        {
            "Adams", "Baker", "Bennett", "Brooks", "Carter", "Clark", "Coleman", "Collins",
            "Cooper", "Davis", "Edwards", "Evans", "Foster", "Garcia", "Gray", "Green",
            "Hall", "Harris", "Hayes", "Hill", "Howard", "Jackson", "James", "Johnson",
            "Kelly", "King", "Lee", "Lewis", "Martin", "Miller", "Mitchell", "Moore",
            "Morgan", "Morris", "Nelson", "Parker", "Reed", "Rivera", "Roberts", "Ross",
            "Scott", "Smith", "Stewart", "Taylor", "Thomas", "Turner", "Walker", "Ward",
            "White", "Williams", "Wilson", "Wood", "Wright", "Young"
        };

        public static bool TryBuild(
            CharacterVisualProfile profile,
            long actorId,
            out CharacterAppearanceRecipe recipe)
        {
            recipe = null;
            if (profile == null || actorId <= 0)
                return false;

            CharacterAppearanceRecipe result = profile.CreateDefaultRecipe();
            result.visualProfileId = profile.VisualProfileId;
            result.meshes = BuildMeshes(profile, actorId, result.meshes);
            result.morphs = BuildMorphs(profile, actorId);
            result.colors = BuildColors(profile, actorId);
            recipe = result;
            return true;
        }

        public static bool TryResolveDisplayName(
            CharacterVisualProfile profile,
            long actorId,
            CharacterAppearanceRecipe recipe,
            out string displayName)
        {
            displayName = string.Empty;
            if (profile == null || actorId <= 0 || recipe == null)
                return false;

            NamePool pool = ResolveNamePool(profile, recipe);
            string[] firstNames;
            if (pool == NamePool.Male)
            {
                firstNames = MaleFirstNames;
            }
            else if (pool == NamePool.Female)
            {
                firstNames = FemaleFirstNames;
            }
            else
            {
                // Mixed actors may draw from either existing first-name catalog. The choice
                // is independently deterministic and does not alter their appearance.
                firstNames = Range(actorId, NamePoolDomain, 0, 2) == 0
                    ? MaleFirstNames
                    : FemaleFirstNames;
            }

            if (firstNames.Length == 0 || Surnames.Length == 0)
                return false;

            string first = firstNames[Range(actorId, FirstNameDomain, 0, firstNames.Length)];
            string last = Surnames[Range(actorId, SurnameDomain, 0, Surnames.Length)];
            displayName = first + " " + last;
            return true;
        }

        private enum NamePool : byte
        {
            Mixed = 0,
            Male = 1,
            Female = 2
        }

        private static NamePool ResolveNamePool(
            CharacterVisualProfile profile,
            CharacterAppearanceRecipe recipe)
        {
            bool hasFacialHair = HasSelectedMesh(recipe, 9);
            if (!TryGetMasculineFeminineValue(profile, recipe, out byte masculineFeminine))
                return NamePool.Mixed;

            // The Sidekick masculineFeminine channel is authored with 0 as the masculine
            // baseline and increasing weight toward the feminine state. Keep the midpoint
            // neutral/mixed rather than forcing it into either constrained pool.
            bool masculine = masculineFeminine < 128;
            bool feminine = masculineFeminine > 128;

            if (hasFacialHair && masculine)
                return NamePool.Male;
            if (!hasFacialHair && feminine)
                return NamePool.Female;
            return NamePool.Mixed;
        }

        private static bool HasSelectedMesh(CharacterAppearanceRecipe recipe, ushort slotId)
        {
            CharacterMeshSelection[] meshes = recipe.meshes ?? Array.Empty<CharacterMeshSelection>();
            for (int i = 0; i < meshes.Length; ++i)
            {
                if (meshes[i].slotId == slotId)
                    return meshes[i].optionId != 0;
            }
            return false;
        }

        private static bool TryGetMasculineFeminineValue(
            CharacterVisualProfile profile,
            CharacterAppearanceRecipe recipe,
            out byte value)
        {
            value = 128;
            if (profile == null || recipe == null)
                return false;

            ushort channelId = 0;
            IReadOnlyList<CharacterVisualMorphDefinition> definitions = profile.Morphs;
            for (int i = 0; i < definitions.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = definitions[i];
                if (definition != null &&
                    string.Equals(definition.semanticName, "masculineFeminine", StringComparison.OrdinalIgnoreCase))
                {
                    channelId = definition.channelId;
                    break;
                }
            }

            if (channelId == 0)
                return false;

            CharacterMorphSelection[] morphs = recipe.morphs ?? Array.Empty<CharacterMorphSelection>();
            for (int i = 0; i < morphs.Length; ++i)
            {
                if (morphs[i].channelId == channelId)
                {
                    value = morphs[i].value;
                    return true;
                }
            }
            return false;
        }

        private static CharacterMeshSelection[] BuildMeshes(
            CharacterVisualProfile profile,
            long actorId,
            CharacterMeshSelection[] defaults)
        {
            var selected = new Dictionary<ushort, ushort>();
            CharacterMeshSelection[] baseValues = defaults ?? Array.Empty<CharacterMeshSelection>();
            for (int i = 0; i < baseValues.Length; ++i)
            {
                if (baseValues[i].slotId != 0)
                    selected[baseValues[i].slotId] = baseValues[i].optionId;
            }

            IReadOnlyList<CharacterVisualSlotDefinition> slots = profile.Slots;
            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || slot.slotId == 0 || slot.equipmentDriven || !slot.mirrorPrimary)
                    continue;

                // Keep the canonical fixed Head/teeth/tongue defaults. Population should look
                // like a Player, and those foundational pieces are not normal Player choices.
                if (slot.slotId == 1 || slot.slotId == 36 || slot.slotId == 37)
                    continue;

                List<CharacterVisualOptionDefinition> candidates = CollectEligibleOptions(slot);
                if (candidates.Count == 0)
                    continue;

                if (ShouldUseNone(actorId, slot))
                {
                    selected[slot.slotId] = 0;
                    if (slot.mirrorSlotId != 0)
                        selected[slot.mirrorSlotId] = 0;
                    continue;
                }

                int optionIndex = Range(actorId, MeshDomain, slot.slotId, candidates.Count);
                CharacterVisualOptionDefinition option = candidates[optionIndex];
                selected[slot.slotId] = option.optionId;

                if (slot.mirrorSlotId == 0)
                    continue;

                if (profile.TryGetMirrorOption(slot, option.optionId, out ushort mirrorOptionId) &&
                    mirrorOptionId != 0 &&
                    profile.TryGetOption(slot.mirrorSlotId, mirrorOptionId, out CharacterVisualOptionDefinition mirror) &&
                    mirror != null && mirror.AllowsPopulationUse)
                {
                    selected[slot.mirrorSlotId] = mirrorOptionId;
                }
            }

            var result = new List<CharacterMeshSelection>(selected.Count);
            foreach (KeyValuePair<ushort, ushort> pair in selected)
                result.Add(new CharacterMeshSelection(pair.Key, pair.Value));
            result.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            return result.ToArray();
        }

        private static List<CharacterVisualOptionDefinition> CollectEligibleOptions(CharacterVisualSlotDefinition slot)
        {
            CharacterVisualOptionDefinition[] values = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            var result = new List<CharacterVisualOptionDefinition>(values.Length);
            for (int i = 0; i < values.Length; ++i)
            {
                CharacterVisualOptionDefinition option = values[i];
                if (option != null && option.AllowsPopulationUse && option.optionId != 0 && option.mesh != null)
                    result.Add(option);
            }
            return result;
        }

        private static bool ShouldUseNone(long actorId, CharacterVisualSlotDefinition slot)
        {
            if (slot == null || !slot.allowNone)
                return false;

            // Keep brows present. Hair can be bald occasionally; facial hair is intentionally
            // much less common. Other optional/equipment-owned slots are not randomized here.
            switch (slot.slotId)
            {
                case 2:
                    return Range(actorId, OptionalDomain, slot.slotId, 10) == 0;
                case 9:
                    return Range(actorId, OptionalDomain, slot.slotId, 2) == 0;
                default:
                    return false;
            }
        }

        private static CharacterMorphSelection[] BuildMorphs(CharacterVisualProfile profile, long actorId)
        {
            IReadOnlyList<CharacterVisualMorphDefinition> definitions = profile.Morphs;
            var result = new List<CharacterMorphSelection>(definitions.Count);

            int bodyArchetype = Range(actorId, BodyArchetypeDomain, 0, 4);
            for (int i = 0; i < definitions.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = definitions[i];
                if (definition == null || !definition.persistInAppearance || definition.channelId == 0)
                    continue;

                byte value = ResolveMorphValue(actorId, definition, bodyArchetype);
                result.Add(new CharacterMorphSelection(definition.channelId, value));
            }

            result.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            return result.ToArray();
        }

        private static byte ResolveMorphValue(
            long actorId,
            CharacterVisualMorphDefinition definition,
            int bodyArchetype)
        {
            string semantic = definition.semanticName ?? string.Empty;
            if (definition.classification == CharacterVisualMorphClassification.Body)
            {
                if (semantic.Equals("defaultBuff", StringComparison.OrdinalIgnoreCase))
                    return bodyArchetype == 1 ? RangeByte(actorId, MorphDomain, definition.channelId, 64, 255) : (byte)0;
                if (semantic.Equals("defaultHeavy", StringComparison.OrdinalIgnoreCase))
                    return bodyArchetype == 2 ? RangeByte(actorId, MorphDomain, definition.channelId, 48, 224) : (byte)0;
                if (semantic.Equals("defaultSkinny", StringComparison.OrdinalIgnoreCase))
                    return bodyArchetype == 3 ? RangeByte(actorId, MorphDomain, definition.channelId, 48, 224) : (byte)0;
                if (semantic.Equals("masculineFeminine", StringComparison.OrdinalIgnoreCase))
                    return RangeByte(actorId, MorphDomain, definition.channelId, 0, 255);

                return RangeByte(actorId, MorphDomain, definition.channelId, 48, 208);
            }

            if (definition.classification == CharacterVisualMorphClassification.Structural)
            {
                // Bone-proportion controls are authored around 128. Blendshape identity
                // controls often rest at 0. Keep both varied but away from pathological
                // all-sliders-at-the-extreme combinations.
                return definition.defaultValue >= 96 && definition.defaultValue <= 160
                    ? RangeByte(actorId, MorphDomain, definition.channelId, 72, 184)
                    : RangeByte(actorId, MorphDomain, definition.channelId, 0, 176);
            }

            return definition.defaultValue;
        }

        private static CharacterColorSelection[] BuildColors(CharacterVisualProfile profile, long actorId)
        {
            IReadOnlyList<CharacterVisualColorDefinition> definitions = profile.Colors;
            var result = new List<CharacterColorSelection>(Math.Min(
                CharacterAppearanceRecipe.MaxColorSelections,
                definitions.Count + 8));

            // Skin/hair/eyes continue to use the normal visual-profile definitions. The old
            // character-wide clothing pair (5/6) is intentionally omitted for Population:
            // regional clothing channels are emitted below so Upper, Lower, Feet and Accessory
            // can vary independently while staying deterministic for this actor id.
            for (int i = 0; i < definitions.Count; ++i)
            {
                CharacterVisualColorDefinition definition = definitions[i];
                if (definition == null || definition.channelId == 0 ||
                    definition.encoding != CharacterColorEncoding.PaletteIndex ||
                    IsLegacyClothingColorChannel(definition))
                    continue;

                uint[] swatches = definition.suggestedRgba ?? Array.Empty<uint>();
                if (swatches.Length == 0)
                    continue;

                int paletteIndex = 1 + Range(actorId, ColorDomain, definition.channelId, swatches.Length);
                result.Add(new CharacterColorSelection(
                    definition.channelId,
                    CharacterColorEncoding.PaletteIndex,
                    (uint)paletteIndex));
            }

            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.UpperPrimaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.UpperSecondaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.LowerPrimaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.LowerSecondaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.FeetPrimaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.FeetSecondaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.AccessoryPrimaryColorChannelId);
            AppendRegionalClothingColor(result, profile, actorId, SidekickCharacterPaletteUtility.AccessorySecondaryColorChannelId);

            result.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            return result.ToArray();
        }

        private static bool IsLegacyClothingColorChannel(CharacterVisualColorDefinition definition)
        {
            if (definition == null)
                return false;

            if (definition.channelId == SidekickCharacterPaletteUtility.ClothingPrimaryColorChannelId ||
                definition.channelId == SidekickCharacterPaletteUtility.ClothingSecondaryColorChannelId)
                return true;

            string semantic = definition.semanticName ?? string.Empty;
            return semantic.Equals("clothingPrimary", StringComparison.OrdinalIgnoreCase) ||
                   semantic.Equals("clothingSecondary", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendRegionalClothingColor(
            List<CharacterColorSelection> result,
            CharacterVisualProfile profile,
            long actorId,
            ushort channelId)
        {
            if (result == null || result.Count >= CharacterAppearanceRecipe.MaxColorSelections)
                return;

            int paletteId = ResolvePopulationClothingPaletteId(
                profile,
                actorId,
                channelId,
                SidekickCharacterPaletteUtility.ClothingPalette.Length);
            if (paletteId <= 0)
                return;

            result.Add(new CharacterColorSelection(
                channelId,
                CharacterColorEncoding.PaletteIndex,
                (uint)paletteId));
        }

        private static int ResolvePopulationClothingPaletteId(
            CharacterVisualProfile profile,
            long actorId,
            ushort channelId,
            int paletteSize)
        {
            if (profile == null || paletteSize <= 0)
                return 0;

            IReadOnlyList<ushort> approved;
            if (!profile.PopulationClothingColorPoolReviewed)
                approved = SidekickCharacterPaletteUtility.PopulationDefaultClothingPaletteIds;
            else
                approved = profile.PopulationClothingPaletteIds;
            int validCount = 0;
            for (int i = 0; i < approved.Count; ++i)
            {
                ushort id = approved[i];
                if (id >= 1 && id <= paletteSize)
                    validCount++;
            }

            if (validCount == 0)
                return 0;

            int target = Range(actorId, ColorDomain, channelId, validCount);
            for (int i = 0; i < approved.Count; ++i)
            {
                ushort id = approved[i];
                if (id < 1 || id > paletteSize)
                    continue;
                if (target-- == 0)
                    return id;
            }

            return 0;
        }

        private static byte RangeByte(long actorId, ulong domain, ushort key, int minInclusive, int maxInclusive)
        {
            int span = Math.Max(1, maxInclusive - minInclusive + 1);
            return (byte)(minInclusive + Range(actorId, domain, key, span));
        }

        private static int Range(long actorId, ulong domain, ushort key, int exclusiveMax)
        {
            if (exclusiveMax <= 1)
                return 0;

            ulong seed = unchecked((ulong)actorId);
            seed ^= domain * 0x9E3779B97F4A7C15UL;
            seed ^= ((ulong)key + 0xD1B54A32D192ED03UL);
            ulong mixed = Mix64(seed);
            return (int)(mixed % (ulong)exclusiveMax);
        }

        private static ulong Mix64(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
