using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only Sidekick palette routing. Hair/accessory/eye colors edit only the
    /// verified tiny _ColorMap cells instead of tinting the whole renderer/material.
    /// </summary>
    public static class SidekickCharacterPaletteUtility
    {
        private static readonly int ColorMapId = Shader.PropertyToID("_ColorMap");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int SkinColorId = Shader.PropertyToID("_SkinColor");
        private static readonly int SkinColorAmountId = Shader.PropertyToID("_SkinColorAmount");
        private static readonly int SyntySkinColorId = Shader.PropertyToID("_Color_Skin");
        private static readonly int SyntySkinColorAltId = Shader.PropertyToID("_Color_Skin_Color");
        private static readonly int SyntySkinColorBodyId = Shader.PropertyToID("_Color_Body");

        // Verified Sidekick Starter palette cells carried over from the production uMMORPG path.
        private const int HairMainX = 12, HairMainY = 8;
        private const int HairAccessoryX = 12, HairAccessoryY = 2;
        private const int BrowLeftX = 12, BrowLeftY = 10;
        private const int BrowRightX = 14, BrowRightY = 10;
        private const int FacialHairMainX = 14, FacialHairMainY = 8;
        private const int FacialHairAccessory1X = 20, FacialHairAccessory1Y = 4;
        private const int FacialHairAccessory2X = 16, FacialHairAccessory2Y = 18;
        private const int FacialHairAccessory3X = 21, FacialHairAccessory3Y = 5;
        private const int EyeLeftInnerX = 8, EyeLeftInnerY = 4;
        private const int EyeLeftOuterX = 8, EyeLeftOuterY = 8;
        private const int EyeRightInnerX = 10, EyeRightInnerY = 4;
        private const int EyeRightOuterX = 10, EyeRightOuterY = 8;

        public static readonly Color[] SkinPalette =
        {
            // Human-only creator palette. Values are deliberately warm/neutral skin tones;
            // fantasy hues belong in a different visual profile/content gate, not the base
            // human creator.
            new Color(0.98f, 0.84f, 0.75f, 1f),
            new Color(0.96f, 0.77f, 0.66f, 1f),
            new Color(0.92f, 0.69f, 0.56f, 1f),
            new Color(0.86f, 0.61f, 0.46f, 1f),
            new Color(0.78f, 0.52f, 0.37f, 1f),
            new Color(0.69f, 0.44f, 0.30f, 1f),
            new Color(0.59f, 0.36f, 0.24f, 1f),
            new Color(0.49f, 0.29f, 0.19f, 1f),
            new Color(0.40f, 0.23f, 0.15f, 1f),
            new Color(0.32f, 0.18f, 0.12f, 1f),
            new Color(0.25f, 0.135f, 0.095f, 1f),
            new Color(0.19f, 0.095f, 0.070f, 1f),
        };

        public static readonly Color[] HairPalette =
        {
            new Color(0.045f, 0.035f, 0.030f, 1f),
            new Color(0.120f, 0.070f, 0.040f, 1f),
            new Color(0.330f, 0.190f, 0.080f, 1f),
            new Color(0.620f, 0.380f, 0.120f, 1f),
            new Color(0.840f, 0.620f, 0.280f, 1f),
            new Color(0.900f, 0.850f, 0.700f, 1f),
            new Color(0.650f, 0.650f, 0.650f, 1f),
            new Color(0.920f, 0.920f, 0.900f, 1f),
            new Color(0.350f, 0.050f, 0.040f, 1f),
            new Color(0.200f, 0.050f, 0.260f, 1f),
            new Color(0.040f, 0.120f, 0.260f, 1f),
        };

        public static readonly Color[] EyePalette =
        {
            new Color(0.120f, 0.300f, 0.700f, 1f),
            new Color(0.100f, 0.460f, 0.280f, 1f),
            new Color(0.380f, 0.210f, 0.080f, 1f),
            new Color(0.700f, 0.650f, 0.420f, 1f),
            new Color(0.650f, 0.650f, 0.650f, 1f),
            new Color(0.020f, 0.020f, 0.020f, 1f),
            new Color(0.650f, 0.160f, 0.160f, 1f),
            new Color(0.550f, 0.180f, 0.720f, 1f),
        };

        // Existing global clothing channels remain the compatibility fallback for Players that
        // were authored before regional dye channels existed. Regional channels use the same
        // CharacterAppearanceRecipe color-selection contract; no new message or wire type is
        // introduced. Population derives every regional value locally from actor id.
        public const ushort ClothingPrimaryColorChannelId = 5;
        public const ushort ClothingSecondaryColorChannelId = 6;

        public const ushort UpperPrimaryColorChannelId = 7;
        public const ushort UpperSecondaryColorChannelId = 8;
        public const ushort LowerPrimaryColorChannelId = 9;
        public const ushort LowerSecondaryColorChannelId = 10;
        public const ushort FeetPrimaryColorChannelId = 11;
        public const ushort FeetSecondaryColorChannelId = 12;
        public const ushort AccessoryPrimaryColorChannelId = 13;
        public const ushort AccessorySecondaryColorChannelId = 14;

        // Production uMMORPG clothing palette carried forward into the shared client visual path.
        // Palette selection is intentionally separate from the authored _ColorMap cells: the
        // option metadata decides WHERE dye may apply; this palette decides WHAT color is used.
        public static readonly Color[] ClothingPalette =
        {
            // IDs 1-10 are already shipped/used. Never reorder or change their meaning.
            Color.white,                                      // 1  White
            new Color(0.050f, 0.050f, 0.055f, 1f),          // 2  Near Black
            new Color(0.200f, 0.040f, 0.035f, 1f),          // 3  Dark Red
            new Color(0.070f, 0.100f, 0.220f, 1f),          // 4  Navy
            new Color(0.060f, 0.170f, 0.100f, 1f),          // 5  Dark Green
            new Color(0.420f, 0.300f, 0.140f, 1f),          // 6  Brown
            new Color(0.450f, 0.450f, 0.450f, 1f),          // 7  Gray
            new Color(0.700f, 0.700f, 0.680f, 1f),          // 8  Light Neutral
            new Color(0.880f, 0.300f, 0.500f, 1f),          // 9  Pink
            new Color(0.880f, 0.330f, 0.070f, 1f),          // 10 Orange

            // Curated additions. Append-only stable ids give Players a broad dye library while
            // Population selection can remain a narrower approved subset in CharacterVisualProfile.
            new Color(0.120f, 0.125f, 0.135f, 1f),          // 11 Charcoal
            new Color(0.300f, 0.305f, 0.315f, 1f),          // 12 Dark Gray
            new Color(0.560f, 0.565f, 0.575f, 1f),          // 13 Medium Gray
            new Color(0.760f, 0.765f, 0.775f, 1f),          // 14 Silver Gray
            new Color(0.910f, 0.890f, 0.830f, 1f),          // 15 Off White
            new Color(0.900f, 0.820f, 0.650f, 1f),          // 16 Cream
            new Color(0.760f, 0.660f, 0.500f, 1f),          // 17 Beige
            new Color(0.620f, 0.470f, 0.280f, 1f),          // 18 Tan
            new Color(0.220f, 0.120f, 0.070f, 1f),          // 19 Dark Brown
            new Color(0.500f, 0.260f, 0.100f, 1f),          // 20 Leather Brown
            new Color(0.320f, 0.055f, 0.090f, 1f),          // 21 Burgundy
            new Color(0.690f, 0.080f, 0.060f, 1f),          // 22 Red
            new Color(0.560f, 0.180f, 0.080f, 1f),          // 23 Rust
            new Color(0.900f, 0.360f, 0.290f, 1f),          // 24 Coral
            new Color(0.650f, 0.300f, 0.390f, 1f),          // 25 Dusty Pink
            new Color(0.930f, 0.590f, 0.680f, 1f),          // 26 Pale Pink
            new Color(0.670f, 0.230f, 0.055f, 1f),          // 27 Burnt Orange
            new Color(0.650f, 0.390f, 0.070f, 1f),          // 28 Ochre
            new Color(0.650f, 0.520f, 0.080f, 1f),          // 29 Mustard
            new Color(0.760f, 0.590f, 0.120f, 1f),          // 30 Gold
            new Color(0.080f, 0.250f, 0.110f, 1f),          // 31 Forest Green
            new Color(0.270f, 0.310f, 0.100f, 1f),          // 32 Olive
            new Color(0.090f, 0.460f, 0.180f, 1f),          // 33 Green
            new Color(0.390f, 0.520f, 0.340f, 1f),          // 34 Sage
            new Color(0.560f, 0.760f, 0.610f, 1f),          // 35 Mint
            new Color(0.040f, 0.380f, 0.390f, 1f),          // 36 Teal
            new Color(0.120f, 0.300f, 0.470f, 1f),          // 37 Denim
            new Color(0.080f, 0.260f, 0.700f, 1f),          // 38 Blue
            new Color(0.300f, 0.560f, 0.820f, 1f),          // 39 Light Blue
            new Color(0.310f, 0.100f, 0.300f, 1f),          // 40 Plum
            new Color(0.440f, 0.160f, 0.600f, 1f),          // 41 Purple
            new Color(0.670f, 0.520f, 0.790f, 1f),          // 42 Lavender
            new Color(0.130f, 0.610f, 0.650f, 1f),          // 43 Turquoise
            new Color(0.860f, 0.720f, 0.180f, 1f),          // 44 Warm Yellow
        };

        private static readonly string[] ClothingPaletteNames =
        {
            "White", "Near Black", "Dark Red", "Navy", "Dark Green", "Brown", "Gray", "Light Neutral", "Pink", "Orange",
            "Charcoal", "Dark Gray", "Medium Gray", "Silver Gray", "Off White", "Cream", "Beige", "Tan", "Dark Brown", "Leather Brown",
            "Burgundy", "Red", "Rust", "Coral", "Dusty Pink", "Pale Pink", "Burnt Orange", "Ochre", "Mustard", "Gold",
            "Forest Green", "Olive", "Green", "Sage", "Mint", "Teal", "Denim", "Blue", "Light Blue", "Plum", "Purple", "Lavender",
            "Turquoise", "Warm Yellow",
        };

        private static readonly string[] ClothingPaletteFamilies =
        {
            "Neutral", "Neutral", "Red / Pink", "Blue", "Green", "Brown / Tan", "Neutral", "Neutral", "Red / Pink", "Orange / Yellow",
            "Neutral", "Neutral", "Neutral", "Neutral", "Neutral", "Neutral", "Brown / Tan", "Brown / Tan", "Brown / Tan", "Brown / Tan",
            "Red / Pink", "Red / Pink", "Red / Pink", "Red / Pink", "Red / Pink", "Red / Pink", "Orange / Yellow", "Orange / Yellow", "Orange / Yellow", "Orange / Yellow",
            "Green", "Green", "Green", "Green", "Green", "Blue", "Blue", "Blue", "Blue", "Purple", "Purple", "Purple", "Blue", "Orange / Yellow",
        };

        // Curated ambient-Population defaults. There is intentionally no pure-black entry;
        // near-black/charcoal and the rest of the ordinary clothing colors remain available.
        // The authoring window can explicitly save any narrower subset.
        private static readonly ushort[] DefaultPopulationClothingPaletteIds =
        {
            // Every entry in this curated library is an ordinary clothing color. There is no
            // pure-black entry; id 2 is the deliberately retained near-black. Authors can still
            // narrow Population to any subset in the existing visual authoring window.
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
            31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
            41, 42, 43, 44,
        };

        public static string GetClothingPaletteName(int oneBasedPaletteId)
        {
            int index = oneBasedPaletteId - 1;
            return index >= 0 && index < ClothingPaletteNames.Length
                ? ClothingPaletteNames[index]
                : "Palette " + oneBasedPaletteId;
        }

        public static string GetClothingPaletteFamily(int oneBasedPaletteId)
        {
            int index = oneBasedPaletteId - 1;
            return index >= 0 && index < ClothingPaletteFamilies.Length
                ? ClothingPaletteFamilies[index]
                : "Other";
        }

        public static bool IsDefaultPopulationClothingPaletteId(int oneBasedPaletteId)
        {
            for (int i = 0; i < DefaultPopulationClothingPaletteIds.Length; ++i)
                if (DefaultPopulationClothingPaletteIds[i] == oneBasedPaletteId)
                    return true;
            return false;
        }

        public static IReadOnlyList<ushort> PopulationDefaultClothingPaletteIds =>
            DefaultPopulationClothingPaletteIds;

        public static ushort[] GetDefaultPopulationClothingPaletteIds()
        {
            var copy = new ushort[DefaultPopulationClothingPaletteIds.Length];
            Array.Copy(DefaultPopulationClothingPaletteIds, copy, copy.Length);
            return copy;
        }

        private readonly struct PaletteKey : IEquatable<PaletteKey>
        {
            public readonly int sourceId;
            public readonly ushort slotId;
            public readonly byte hair;
            public readonly byte accessory;
            public readonly byte eyes;

            public PaletteKey(Texture2D source, ushort slotId, byte hair, byte accessory, byte eyes)
            {
                sourceId = source != null ? source.GetInstanceID() : 0;
                this.slotId = slotId;
                this.hair = hair;
                this.accessory = accessory;
                this.eyes = eyes;
            }

            public bool Equals(PaletteKey other) =>
                sourceId == other.sourceId && slotId == other.slotId && hair == other.hair &&
                accessory == other.accessory && eyes == other.eyes;
            public override bool Equals(object obj) => obj is PaletteKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = sourceId;
                    hash = (hash * 397) ^ slotId;
                    hash = (hash * 397) ^ hair;
                    hash = (hash * 397) ^ accessory;
                    hash = (hash * 397) ^ eyes;
                    return hash;
                }
            }
        }

        private static readonly Dictionary<PaletteKey, Texture2D> RuntimeMaps =
            new Dictionary<PaletteKey, Texture2D>();

        private readonly struct ClothingPaletteKey : IEquatable<ClothingPaletteKey>
        {
            public readonly int sourceId;
            public readonly int cellsHash;
            public readonly byte primary;
            public readonly byte secondary;

            public ClothingPaletteKey(Texture2D source, int cellsHash, byte primary, byte secondary)
            {
                sourceId = source != null ? source.GetInstanceID() : 0;
                this.cellsHash = cellsHash;
                this.primary = primary;
                this.secondary = secondary;
            }

            public bool Equals(ClothingPaletteKey other) =>
                sourceId == other.sourceId && cellsHash == other.cellsHash &&
                primary == other.primary && secondary == other.secondary;

            public override bool Equals(object obj) => obj is ClothingPaletteKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = sourceId;
                    hash = (hash * 397) ^ cellsHash;
                    hash = (hash * 397) ^ primary;
                    hash = (hash * 397) ^ secondary;
                    return hash;
                }
            }
        }

        private static readonly Dictionary<ClothingPaletteKey, Texture2D> ClothingRuntimeMaps =
            new Dictionary<ClothingPaletteKey, Texture2D>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeMaps()
        {
            foreach (Texture2D texture in RuntimeMaps.Values)
            {
                if (texture != null && (texture.hideFlags & HideFlags.HideAndDontSave) != 0)
                    UnityEngine.Object.Destroy(texture);
            }
            RuntimeMaps.Clear();

            foreach (Texture2D texture in ClothingRuntimeMaps.Values)
            {
                if (texture != null && (texture.hideFlags & HideFlags.HideAndDontSave) != 0)
                    UnityEngine.Object.Destroy(texture);
            }
            ClothingRuntimeMaps.Clear();
        }

        public static void ApplySkin(MaterialPropertyBlock block, byte paletteId)
        {
            if (block == null)
                return;
            if (paletteId == 0)
            {
                block.SetFloat(SkinColorAmountId, 0f);
                block.SetColor(ColorId, Color.white);
                block.SetColor(SyntySkinColorId, Color.white);
                block.SetColor(SyntySkinColorAltId, Color.white);
                block.SetColor(SyntySkinColorBodyId, Color.white);
                return;
            }

            Color skin = GetPaletteColor(SkinPalette, paletteId);
            block.SetColor(SkinColorId, skin);
            block.SetFloat(SkinColorAmountId, 1f);
            block.SetColor(ColorId, Color.white);
            block.SetColor(SyntySkinColorId, skin);
            block.SetColor(SyntySkinColorAltId, skin);
            block.SetColor(SyntySkinColorBodyId, skin);
        }

        public static bool ApplyPaletteMap(
            MaterialPropertyBlock block,
            Material material,
            ushort slotId,
            byte hairColorId,
            byte hairAccessoryColorId,
            byte eyeColorId)
        {
            if (block == null || material == null || !material.HasProperty(ColorMapId))
                return false;

            Texture2D source = material.GetTexture(ColorMapId) as Texture2D;
            if (source == null)
                return false;

            bool relevant = IsHairOrBrowSlot(slotId) || IsEyeSlot(slotId);
            if (!relevant)
                return false;

            byte hair = IsHairOrBrowSlot(slotId) ? hairColorId : (byte)0;
            byte accessory = SupportsHairAccessory(slotId) ? hairAccessoryColorId : (byte)0;
            byte eyes = IsEyeSlot(slotId) ? eyeColorId : (byte)0;
            if (hair == 0 && accessory == 0 && eyes == 0)
            {
                block.SetTexture(ColorMapId, source);
                return true;
            }

            PaletteKey key = new PaletteKey(source, slotId, hair, accessory, eyes);
            if (!RuntimeMaps.TryGetValue(key, out Texture2D runtime) || runtime == null)
            {
                runtime = CreateRuntimeCopy(source);
                if (runtime == null)
                    return false;

                bool changed = false;
                if (hair != 0)
                {
                    Color target = GetPaletteColor(HairPalette, hair);
                    if (slotId == 2)
                        changed |= SetCell(runtime, HairMainX, HairMainY, target);
                    else if (slotId == 3)
                        changed |= SetCell(runtime, BrowLeftX, BrowLeftY, target);
                    else if (slotId == 4)
                        changed |= SetCell(runtime, BrowRightX, BrowRightY, target);
                    else if (slotId == 9)
                        changed |= SetCell(runtime, FacialHairMainX, FacialHairMainY, target);
                }

                if (accessory != 0)
                {
                    Color target = GetPaletteColor(HairPalette, accessory);
                    if (slotId == 2)
                        changed |= SetCell(runtime, HairAccessoryX, HairAccessoryY, target);
                    else if (slotId == 9)
                    {
                        changed |= SetCell(runtime, FacialHairAccessory1X, FacialHairAccessory1Y, target);
                        changed |= SetCell(runtime, FacialHairAccessory2X, FacialHairAccessory2Y, target);
                        changed |= SetCell(runtime, FacialHairAccessory3X, FacialHairAccessory3Y, target);
                    }
                }

                if (eyes != 0)
                {
                    Color target = GetPaletteColor(EyePalette, eyes);
                    if (slotId == 5)
                        changed |= RecolorIrisPair(runtime, EyeLeftInnerX, EyeLeftInnerY, EyeLeftOuterX, EyeLeftOuterY, target);
                    else if (slotId == 6)
                        changed |= RecolorIrisPair(runtime, EyeRightInnerX, EyeRightInnerY, EyeRightOuterX, EyeRightOuterY, target);
                }

                if (!changed)
                {
                    UnityEngine.Object.Destroy(runtime);
                    block.SetTexture(ColorMapId, source);
                    return true;
                }

                runtime.name = source.name + "_RuntimePalette_" + slotId + "_H" + hair + "_A" + accessory + "_E" + eyes;
                runtime.hideFlags = HideFlags.HideAndDontSave;
                runtime.Apply(false, true);
                RuntimeMaps[key] = runtime;
            }

            block.SetTexture(ColorMapId, runtime);
            return true;
        }

        /// <summary>
        /// Applies only explicitly-authored clothing palette cells from the existing visual
        /// option metadata. Primary and Secondary use the two shared appearance color channels;
        /// Accent remains authored/original until a separate accent policy is intentionally added.
        /// </summary>
        public static bool ApplyClothingPaletteMap(
            MaterialPropertyBlock block,
            Material material,
            CharacterVisualPaletteCellDefinition[] cells,
            byte primaryColorId,
            byte secondaryColorId)
        {
            if (block == null || material == null || !material.HasProperty(ColorMapId) ||
                cells == null || cells.Length == 0)
                return false;

            Texture2D source = material.GetTexture(ColorMapId) as Texture2D;
            if (source == null)
                return false;

            int cellsHash = 17;
            int matchingCells = 0;
            unchecked
            {
                for (int i = 0; i < cells.Length; ++i)
                {
                    CharacterVisualPaletteCellDefinition cell = cells[i];
                    if (!cell.IsValid || cell.colorMapWidth != source.width ||
                        cell.colorMapHeight != source.height)
                        continue;

                    byte paletteId;
                    switch (cell.channel)
                    {
                        case CharacterVisualPaletteChannel.Primary:
                            paletteId = primaryColorId;
                            break;
                        case CharacterVisualPaletteChannel.Secondary:
                            paletteId = secondaryColorId;
                            break;
                        default:
                            // Accent is deliberately preserved as authored.
                            continue;
                    }

                    if (paletteId == 0)
                        continue;

                    cellsHash = cellsHash * 31 + cell.cellX;
                    cellsHash = cellsHash * 31 + cell.cellY;
                    cellsHash = cellsHash * 31 + cell.colorMapWidth;
                    cellsHash = cellsHash * 31 + cell.colorMapHeight;
                    cellsHash = cellsHash * 31 + (int)cell.channel;
                    matchingCells++;
                }
            }

            if (matchingCells == 0)
                return false;

            var key = new ClothingPaletteKey(source, cellsHash, primaryColorId, secondaryColorId);
            if (!ClothingRuntimeMaps.TryGetValue(key, out Texture2D runtime) || runtime == null)
            {
                runtime = CreateRuntimeCopy(source);
                if (runtime == null)
                    return false;

                bool changed = false;
                for (int i = 0; i < cells.Length; ++i)
                {
                    CharacterVisualPaletteCellDefinition cell = cells[i];
                    if (!cell.IsValid || cell.colorMapWidth != runtime.width ||
                        cell.colorMapHeight != runtime.height)
                        continue;

                    byte paletteId;
                    switch (cell.channel)
                    {
                        case CharacterVisualPaletteChannel.Primary:
                            paletteId = primaryColorId;
                            break;
                        case CharacterVisualPaletteChannel.Secondary:
                            paletteId = secondaryColorId;
                            break;
                        default:
                            continue;
                    }

                    if (paletteId == 0)
                        continue;

                    changed |= SetCell(runtime, cell.cellX, cell.cellY, GetPaletteColor(ClothingPalette, paletteId));
                }

                if (!changed)
                {
                    UnityEngine.Object.Destroy(runtime);
                    return false;
                }

                runtime.name = source.name + "_RuntimeClothingPalette_" + cellsHash +
                               "_P" + primaryColorId + "_S" + secondaryColorId;
                runtime.hideFlags = HideFlags.HideAndDontSave;
                runtime.Apply(false, true);
                ClothingRuntimeMaps[key] = runtime;
            }

            block.SetTexture(ColorMapId, runtime);
            return true;
        }


        /// <summary>
        /// Resolves the regional clothing color channels for one canonical modular slot.
        /// Torso/arms/hands share Upper, hips/legs share Lower, feet are independent, and
        /// authored attachments share Accessory. Unknown slots keep the legacy/global pair.
        /// </summary>
        public static void ResolveClothingColorChannels(
            ushort slotId,
            out ushort primaryChannelId,
            out ushort secondaryChannelId)
        {
            if (slotId >= 10 && slotId <= 16)
            {
                primaryChannelId = UpperPrimaryColorChannelId;
                secondaryChannelId = UpperSecondaryColorChannelId;
                return;
            }

            if (slotId >= 17 && slotId <= 19)
            {
                primaryChannelId = LowerPrimaryColorChannelId;
                secondaryChannelId = LowerSecondaryColorChannelId;
                return;
            }

            if (slotId == 20 || slotId == 21)
            {
                primaryChannelId = FeetPrimaryColorChannelId;
                secondaryChannelId = FeetSecondaryColorChannelId;
                return;
            }

            if (slotId == 22 || slotId == 23 || slotId == 24 || slotId == 29 || slotId == 30)
            {
                primaryChannelId = AccessoryPrimaryColorChannelId;
                secondaryChannelId = AccessorySecondaryColorChannelId;
                return;
            }

            primaryChannelId = ClothingPrimaryColorChannelId;
            secondaryChannelId = ClothingSecondaryColorChannelId;
        }

        public static string ClothingDyeRegionLabel(ushort slotId)
        {
            ResolveClothingColorChannels(slotId, out ushort primary, out _);
            if (primary == UpperPrimaryColorChannelId) return "Upper";
            if (primary == LowerPrimaryColorChannelId) return "Lower";
            if (primary == FeetPrimaryColorChannelId) return "Feet";
            if (primary == AccessoryPrimaryColorChannelId) return "Accessory";
            return "Legacy / Global";
        }

        private static bool IsHairOrBrowSlot(ushort slotId) => slotId == 2 || slotId == 3 || slotId == 4 || slotId == 9;
        private static bool SupportsHairAccessory(ushort slotId) => slotId == 2 || slotId == 9;
        private static bool IsEyeSlot(ushort slotId) => slotId == 5 || slotId == 6;

        private static Color GetPaletteColor(Color[] palette, byte id)
        {
            if (palette == null || palette.Length == 0 || id == 0)
                return Color.white;
            int index = Mathf.Clamp(id - 1, 0, palette.Length - 1);
            return palette[index];
        }

        private static bool SetCell(Texture2D texture, int x, int y, Color target)
        {
            if (!IsValidCell(texture, x, y))
                return false;
            Color authored = texture.GetPixel(x, y);
            target.a = authored.a;
            texture.SetPixel(x, y, target);
            return true;
        }

        private static bool RecolorIrisPair(Texture2D texture, int ix, int iy, int ox, int oy, Color target)
        {
            if (!IsValidCell(texture, ix, iy) || !IsValidCell(texture, ox, oy))
                return false;
            Color authoredInner = texture.GetPixel(ix, iy);
            Color authoredOuter = texture.GetPixel(ox, oy);
            float inner = Brightness(authoredInner);
            float outer = Brightness(authoredOuter);
            float brightest = Mathf.Max(inner, outer, 0.0001f);
            Color innerResult = Scale(target, inner / brightest);
            Color outerResult = Scale(target, outer / brightest);
            innerResult.a = authoredInner.a;
            outerResult.a = authoredOuter.a;
            texture.SetPixel(ix, iy, innerResult);
            texture.SetPixel(ox, oy, outerResult);
            return true;
        }

        private static float Brightness(Color c) => Mathf.Max(0f, c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f);
        private static Color Scale(Color c, float s) => new Color(Mathf.Clamp01(c.r * s), Mathf.Clamp01(c.g * s), Mathf.Clamp01(c.b * s), c.a);
        private static bool IsValidCell(Texture2D texture, int x, int y) => texture != null && x >= 0 && y >= 0 && x < texture.width && y < texture.height;

        private static Texture2D CreateRuntimeCopy(Texture2D source)
        {
            RenderTexture temporary = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false)
                {
                    filterMode = source.filterMode,
                    wrapModeU = source.wrapModeU,
                    wrapModeV = source.wrapModeV,
                    anisoLevel = source.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }
}
