using System;
using System.Collections.Generic;
using Game.Shared.Actors;
using Game.Shared.Characters;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    public enum CharacterCreatorCategory : byte
    {
        Body = 0,
        Face = 1,
        Hair = 2,
        Details = 3,
        Movement = 4,
    }

    [Flags]
    public enum CharacterVisualRegionMask : byte
    {
        None = 0,
        HeadHair = 1 << 0,
        UpperBody = 1 << 1,
        LowerBody = 1 << 2,
        All = HeadHair | UpperBody | LowerBody,
    }

    public enum CharacterCosmeticAccessTier : byte
    {
        Free = 0,
        Premium = 1,
    }

    public enum CharacterVisualMorphClassification : byte
    {
        Body = 0,
        Structural = 1,
        Expression = 2,
        Internal = 3,
        Unknown = 4,
    }

    public enum CharacterVisualPaletteChannel : byte
    {
        Primary = 0,
        Secondary = 1,
        Accent = 2,
    }

    public enum CharacterWearableRegion : byte
    {
        Upper = 0,
        Lower = 1,
        Feet = 2,
        Accessory = 3,
    }

    [Serializable]
    public struct CharacterWearablePartSelection
    {
        public ushort slotId;
        public ushort optionId;

        public CharacterWearablePartSelection(ushort slotId, ushort optionId)
        {
            this.slotId = slotId;
            this.optionId = optionId;
        }
    }

    [Serializable]
    public sealed class CharacterWearableSetDefinition
    {
        [Tooltip("Stable authoring/content id. This is not a mesh name and is intended to survive source-file renames.")]
        public string definitionId;
        [Tooltip("Player-facing item name. Multiple visual variants may intentionally share the same display name.")]
        public string displayName = "Wearable";
        [Tooltip("Reserved compact presentation id for later server ItemDefinition.presentationId binding. Zero means not assigned yet.")]
        public ushort presentationId;
        public CharacterWearableRegion region = CharacterWearableRegion.Upper;
        public bool playerEligible = true;
        public bool populationEligible = true;
        [Tooltip("One-based stable clothing-palette id used by the authoring preview/default presentation.")]
        public ushort defaultPrimaryColorId = 1;
        [Tooltip("One-based stable clothing-palette id used by the authoring preview/default presentation.")]
        public ushort defaultSecondaryColorId = 8;
        [Tooltip("True after this wearable has an explicitly reviewed color allow-list. Until reviewed, it inherits the profile-wide Player/Population clothing color pools.")]
        public bool colorUsageReviewed;
        [Tooltip("One-based stable clothing-palette ids allowed for this wearable when used by Players. Profile-wide eligibility is still applied first.")]
        public ushort[] playerAllowedColorIds = Array.Empty<ushort>();
        [Tooltip("One-based stable clothing-palette ids allowed for this wearable when used by Population. Profile-wide eligibility is still applied first.")]
        public ushort[] populationAllowedColorIds = Array.Empty<ushort>();
        [Tooltip("Only explicit mesh overrides are stored. Missing foundational body slots resolve to the profile Default Base; missing optional attachment slots remain empty.")]
        public CharacterWearablePartSelection[] parts = Array.Empty<CharacterWearablePartSelection>();
        [Tooltip("Optional inventory presentation asset. Automated icon capture can populate this later without changing server state.")]
        public Sprite icon;
    }

    [Serializable]
    public struct CharacterVisualPaletteCellDefinition
    {
        [Min(1)] public int colorMapWidth;
        [Min(1)] public int colorMapHeight;
        [Min(0)] public int cellX;
        [Min(0)] public int cellY;
        public CharacterVisualPaletteChannel channel;

        public bool IsValid =>
            colorMapWidth > 0 &&
            colorMapHeight > 0 &&
            cellX >= 0 && cellX < colorMapWidth &&
            cellY >= 0 && cellY < colorMapHeight;
    }

    [Serializable]
    public sealed class CharacterVisualOptionDefinition
    {
        public ushort optionId;
        public ushort pairId;
        public string displayName;
        public string rendererPath;

        [Header("Resolved Client Mesh Data")]
        public Mesh mesh;
        public Material[] materials = Array.Empty<Material>();
        public string rootBonePath;
        public string[] bonePaths = Array.Empty<string>();
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
        public Bounds localBounds;

        [Header("Character Creator Policy")]
        public bool creatorSelectable = true;
        public bool equipmentDriven;

        [Header("Client Usage Approval")]
        [Tooltip("True after this option has been explicitly reviewed in the Population Clothing Palette Inspector. Unreviewed options preserve the existing catalog defaults.")]
        public bool usageReviewed;
        [Tooltip("Whether this visual option may be used by Player presentation/content once explicitly reviewed.")]
        public bool playerEligible = true;
        [Tooltip("Client-only eligibility for deterministic ambient Population appearance selection.")]
        public bool populationEligible;

        public bool AllowsPlayerUse => !usageReviewed || playerEligible;
        public bool AllowsPopulationUse => populationEligible;

        [Header("Palette Authoring")]
        [Tooltip("Authored Sidekick _ColorMap cells for clothing/body coloration. Unlisted cells preserve their original palette value.")]
        public CharacterVisualPaletteCellDefinition[] paletteCells = Array.Empty<CharacterVisualPaletteCellDefinition>();
        [Tooltip("True after the dye-zone mapping for this exact mesh revision has been explicitly confirmed in the existing visual authoring window. A confirmed option may legitimately have zero dye cells (Not Dyeable).")]
        public bool paletteReviewConfirmed;
        [Tooltip("Editor-authored dependency fingerprint for the mesh revision that was confirmed. Runtime never consumes this value; the authoring window uses it to flag stale reviews after FBX/content changes.")]
        public string paletteReviewFingerprint;
        [Tooltip("Editor-authored signature of the scanned UV0 palette-cell layout. Used only to identify exact matching meshes for opt-in mapping reuse during content authoring.")]
        public string paletteScanSignature;

        public CharacterCosmeticAccessTier accessTier = CharacterCosmeticAccessTier.Free;
        public bool factionRestricted;
        public ActorFaction requiredFaction = ActorFaction.Human;

        [Header("Client Presentation")]
        public Sprite thumbnail;
        [HideInInspector] public string thumbnailFingerprint;

        public bool IsSelectableFor(ActorFaction faction, bool hasPremiumAccess)
        {
            if (!AllowsPlayerUse || !creatorSelectable || equipmentDriven)
                return false;
            if (accessTier == CharacterCosmeticAccessTier.Premium && !hasPremiumAccess)
                return false;
            return !factionRestricted || faction == requiredFaction;
        }
    }

    [Serializable]
    public sealed class CharacterVisualSlotDefinition
    {
        public ushort slotId;
        public string slotCode;
        public string displayName;
        public CharacterCreatorCategory category;
        public CharacterVisualRegionMask region;
        public bool allowNone;
        public bool equipmentDriven;
        public ushort mirrorSlotId;
        public bool mirrorPrimary = true;
        public ushort defaultOptionId;
        public CharacterVisualOptionDefinition[] options = Array.Empty<CharacterVisualOptionDefinition>();

        public bool IsPublishedChooser
        {
            get
            {
                if (!mirrorPrimary || equipmentDriven)
                    return false;
                int count = allowNone ? 1 : 0;
                CharacterVisualOptionDefinition[] values = options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int i = 0; i < values.Length; ++i)
                    if (values[i] != null && values[i].AllowsPlayerUse && values[i].creatorSelectable && !values[i].equipmentDriven)
                        count++;
                return count >= 2;
            }
        }
    }

    public enum CharacterVisualMorphDriver : byte
    {
        BlendShape = 0,
        Bone = 1,
    }

    [Serializable]
    public sealed class CharacterVisualBoneTargetDefinition
    {
        public string transformPath;
        public Vector3 minLocalScaleMultiplier = Vector3.one;
        public Vector3 maxLocalScaleMultiplier = Vector3.one;
        public Vector3 minLocalPositionMultiplier = Vector3.one;
        public Vector3 maxLocalPositionMultiplier = Vector3.one;
    }

    [Serializable]
    public sealed class CharacterVisualMorphDefinition
    {
        public ushort channelId;
        public string semanticName;
        public string[] semanticNames = Array.Empty<string>();
        public string displayName;
        public CharacterCreatorCategory category;
        public CharacterVisualRegionMask region;
        public CharacterVisualMorphClassification classification;
        public CharacterVisualMorphDriver driver = CharacterVisualMorphDriver.BlendShape;
        public CharacterVisualBoneTargetDefinition[] boneTargets = Array.Empty<CharacterVisualBoneTargetDefinition>();
        public bool visibleInCreator = true;
        public bool persistInAppearance = true;
        public float minWeight;
        public float maxWeight = 100f;
        public byte defaultValue;
    }

    [Serializable]
    public sealed class CharacterVisualColorDefinition
    {
        public ushort channelId;
        public string semanticName;
        public string displayName;
        public CharacterCreatorCategory category;
        public CharacterVisualRegionMask region;
        public CharacterColorEncoding encoding = CharacterColorEncoding.PaletteIndex;
        public string colorProperty;
        public string amountProperty;
        public bool applyToAllRenderers;
        public ushort[] affectedSlotIds = Array.Empty<ushort>();
        public uint[] suggestedRgba = Array.Empty<uint>();
    }

    /// <summary>
    /// Client-only catalog for one concrete visual system. Shared/server code only sees
    /// the compact semantic ids stored in CharacterAppearanceRecipe.
    /// </summary>
    public sealed class CharacterVisualProfile : ScriptableObject
    {
        [SerializeField] private ushort visualProfileId = 1;
        [SerializeField] private uint catalogVersion = 1;
        [SerializeField] private string sourceFingerprint;
        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private GameObject editableBasePrefab;
        [SerializeField] private CharacterVisualSlotDefinition[] slots = Array.Empty<CharacterVisualSlotDefinition>();
        [SerializeField] private CharacterVisualMorphDefinition[] morphs = Array.Empty<CharacterVisualMorphDefinition>();
        [SerializeField] private CharacterVisualColorDefinition[] colors = Array.Empty<CharacterVisualColorDefinition>();
        [SerializeField] private bool playerClothingColorPoolReviewed;
        [SerializeField] private ushort[] playerClothingPaletteIds = Array.Empty<ushort>();
        [SerializeField] private bool populationClothingColorPoolReviewed;
        [SerializeField] private ushort[] populationClothingPaletteIds = Array.Empty<ushort>();
        [SerializeField] private CharacterWearableSetDefinition[] wearableSets = Array.Empty<CharacterWearableSetDefinition>();

        public ushort VisualProfileId => visualProfileId;
        public uint CatalogVersion => catalogVersion;
        public string SourceFingerprint => sourceFingerprint ?? string.Empty;
        public GameObject SourcePrefab => sourcePrefab;
        public GameObject EditableBasePrefab => editableBasePrefab != null ? editableBasePrefab : sourcePrefab;
        public IReadOnlyList<CharacterVisualSlotDefinition> Slots => slots ?? Array.Empty<CharacterVisualSlotDefinition>();
        public IReadOnlyList<CharacterVisualMorphDefinition> Morphs => morphs ?? Array.Empty<CharacterVisualMorphDefinition>();
        public IReadOnlyList<CharacterVisualColorDefinition> Colors => colors ?? Array.Empty<CharacterVisualColorDefinition>();
        public bool PlayerClothingColorPoolReviewed => playerClothingColorPoolReviewed;
        public IReadOnlyList<ushort> PlayerClothingPaletteIds => playerClothingPaletteIds ?? Array.Empty<ushort>();
        public bool PopulationClothingColorPoolReviewed => populationClothingColorPoolReviewed;
        public IReadOnlyList<ushort> PopulationClothingPaletteIds => populationClothingPaletteIds ?? Array.Empty<ushort>();
        public IReadOnlyList<CharacterWearableSetDefinition> WearableSets => wearableSets ?? Array.Empty<CharacterWearableSetDefinition>();

        public void SetPlayerClothingColorPool(bool reviewed, ushort[] paletteIds)
        {
            playerClothingColorPoolReviewed = reviewed;
            playerClothingPaletteIds = paletteIds ?? Array.Empty<ushort>();
        }

        public void SetPopulationClothingColorPool(bool reviewed, ushort[] paletteIds)
        {
            populationClothingColorPoolReviewed = reviewed;
            populationClothingPaletteIds = paletteIds ?? Array.Empty<ushort>();
        }

        public void SetWearableSets(CharacterWearableSetDefinition[] values)
        {
            wearableSets = values ?? Array.Empty<CharacterWearableSetDefinition>();
        }

        public void Configure(
            ushort profileId,
            uint version,
            string fingerprint,
            GameObject source,
            GameObject editableBase,
            CharacterVisualSlotDefinition[] slotDefinitions,
            CharacterVisualMorphDefinition[] morphDefinitions,
            CharacterVisualColorDefinition[] colorDefinitions)
        {
            visualProfileId = profileId;
            catalogVersion = version == 0 ? 1u : version;
            sourceFingerprint = fingerprint ?? string.Empty;
            sourcePrefab = source;
            editableBasePrefab = editableBase;
            slots = slotDefinitions ?? Array.Empty<CharacterVisualSlotDefinition>();
            morphs = morphDefinitions ?? Array.Empty<CharacterVisualMorphDefinition>();
            colors = colorDefinitions ?? Array.Empty<CharacterVisualColorDefinition>();
        }

        public bool TryGetSlot(ushort slotId, out CharacterVisualSlotDefinition slot)
        {
            CharacterVisualSlotDefinition[] values = slots ?? Array.Empty<CharacterVisualSlotDefinition>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i] != null && values[i].slotId == slotId)
                {
                    slot = values[i];
                    return true;
                }
            }
            slot = null;
            return false;
        }

        public bool TryGetOption(ushort slotId, ushort optionId, out CharacterVisualOptionDefinition option)
        {
            option = null;
            if (!TryGetSlot(slotId, out CharacterVisualSlotDefinition slot))
                return false;
            CharacterVisualOptionDefinition[] values = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i] != null && values[i].optionId == optionId)
                {
                    option = values[i];
                    return true;
                }
            }
            return false;
        }

        public bool TryGetMirrorOption(CharacterVisualSlotDefinition primary, ushort primaryOptionId, out ushort mirrorOptionId)
        {
            mirrorOptionId = 0;
            if (primary == null || primary.mirrorSlotId == 0 || primaryOptionId == 0)
                return false;
            if (!TryGetOption(primary.slotId, primaryOptionId, out CharacterVisualOptionDefinition selected) ||
                selected.pairId == 0 ||
                !TryGetSlot(primary.mirrorSlotId, out CharacterVisualSlotDefinition mirror))
                return false;

            CharacterVisualOptionDefinition[] options = mirror.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            for (int i = 0; i < options.Length; ++i)
            {
                if (options[i] != null && options[i].pairId == selected.pairId)
                {
                    mirrorOptionId = options[i].optionId;
                    return true;
                }
            }
            return false;
        }

        public CharacterAppearanceRecipe CreateDefaultRecipe()
        {
            var meshValues = new List<CharacterMeshSelection>((slots ?? Array.Empty<CharacterVisualSlotDefinition>()).Length);
            CharacterVisualSlotDefinition[] slotValues = slots ?? Array.Empty<CharacterVisualSlotDefinition>();
            for (int i = 0; i < slotValues.Length; ++i)
            {
                CharacterVisualSlotDefinition slot = slotValues[i];
                if (slot == null)
                    continue;
                ushort value = slot.defaultOptionId;
                if (value == 0 && !slot.allowNone && slot.options != null && slot.options.Length > 0)
                    value = slot.options[0].optionId;
                meshValues.Add(new CharacterMeshSelection(slot.slotId, value));
            }

            var morphValues = new List<CharacterMorphSelection>();
            CharacterVisualMorphDefinition[] morphDefinitions = morphs ?? Array.Empty<CharacterVisualMorphDefinition>();
            for (int i = 0; i < morphDefinitions.Length; ++i)
            {
                CharacterVisualMorphDefinition morph = morphDefinitions[i];
                if (morph != null && morph.persistInAppearance)
                    morphValues.Add(new CharacterMorphSelection(morph.channelId, morph.defaultValue));
            }

            return new CharacterAppearanceRecipe
            {
                schemaVersion = CharacterAppearanceRecipe.CurrentSchemaVersion,
                visualProfileId = visualProfileId,
                revision = 0,
                meshes = meshValues.ToArray(),
                morphs = morphValues.ToArray(),
                colors = Array.Empty<CharacterColorSelection>(),
            };
        }
    }

    public static class CharacterVisualProfileRegistry
    {
        private const string DefaultResourcePath = "MMO/Characters/CharacterVisualProfile";
        private const string AdditionalProfilesResourcePath = "MMO/Characters/Profiles";
        private static CharacterVisualProfile _default;
        private static Dictionary<ushort, CharacterVisualProfile> _profiles;

        public static CharacterVisualProfile Default
        {
            get
            {
                EnsureLoaded();
                return _default;
            }
        }

        public static bool TryResolve(ushort visualProfileId, out CharacterVisualProfile profile)
        {
            EnsureLoaded();
            if (visualProfileId == 0)
            {
                profile = _default;
                return profile != null;
            }

            // Assign the out parameter even when the registry is unavailable.
            // The previous short-circuit expression could skip TryGetValue, leaving
            // profile unassigned and causing CS0177.
            profile = null;
            return _profiles != null
                && _profiles.TryGetValue(visualProfileId, out profile)
                && profile != null;
        }

        private static void EnsureLoaded()
        {
            if (_profiles != null)
                return;

            _profiles = new Dictionary<ushort, CharacterVisualProfile>();
            _default = Resources.Load<CharacterVisualProfile>(DefaultResourcePath);
            AddProfile(_default, true);

            CharacterVisualProfile[] additional = Resources.LoadAll<CharacterVisualProfile>(AdditionalProfilesResourcePath);
            for (int i = 0; i < additional.Length; ++i)
                AddProfile(additional[i], false);
        }

        private static void AddProfile(CharacterVisualProfile profile, bool isDefault)
        {
            if (profile == null || profile.VisualProfileId == 0)
                return;
            if (_profiles.TryGetValue(profile.VisualProfileId, out CharacterVisualProfile existing) && existing != null && existing != profile)
            {
                // Default wins profile-id collisions so old characters remain deterministic.
                if (isDefault)
                    _profiles[profile.VisualProfileId] = profile;
                else
                    Debug.LogError($"[CharacterVisualProfile] Duplicate visualProfileId {profile.VisualProfileId}: '{existing.name}' and '{profile.name}'.");
                return;
            }
            _profiles[profile.VisualProfileId] = profile;
        }

        public static CharacterAppearanceRecipe CreateDefaultRecipeOrFallback()
        {
            CharacterVisualProfile current = Default;
            return current != null ? current.CreateDefaultRecipe() : CharacterAppearanceRecipe.CreateDefault();
        }

        public static CharacterAppearanceRecipe NormalizeForPresentation(CharacterAppearanceRecipe source)
        {
            if (source == null)
                return CreateDefaultRecipeOrFallback();

            if (source.visualProfileId == 0 &&
                (source.meshes == null || source.meshes.Length == 0) &&
                Default != null)
                return Default.CreateDefaultRecipe();

            return source.Clone();
        }

        /// <summary>
        /// Moves a semantic appearance recipe to another client visual profile without inventing
        /// a second appearance format. Shared channel IDs are retained. Unsupported semantic
        /// channels are also retained (within the shared contract limit) so switching model
        /// families does not silently destroy appearance intent that a later profile can use.
        /// Exact mesh choices are profile-specific and therefore start from the target defaults.
        /// </summary>
        public static CharacterAppearanceRecipe RemapRecipeToProfile(
            CharacterAppearanceRecipe source,
            CharacterVisualProfile target)
        {
            if (target == null)
                return source?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();

            CharacterAppearanceRecipe result = target.CreateDefaultRecipe();
            if (source == null)
                return result;

            var morphs = new Dictionary<ushort, byte>();
            CharacterMorphSelection[] targetMorphs = result.morphs ?? Array.Empty<CharacterMorphSelection>();
            for (int i = 0; i < targetMorphs.Length; ++i)
                if (targetMorphs[i].channelId != 0) morphs[targetMorphs[i].channelId] = targetMorphs[i].value;
            CharacterMorphSelection[] sourceMorphs = source.morphs ?? Array.Empty<CharacterMorphSelection>();
            for (int i = 0; i < sourceMorphs.Length; ++i)
            {
                if (sourceMorphs[i].channelId == 0) continue;
                morphs[sourceMorphs[i].channelId] = sourceMorphs[i].value;
                if (morphs.Count >= CharacterAppearanceRecipe.MaxMorphSelections) break;
            }
            var packedMorphs = new List<CharacterMorphSelection>(morphs.Count);
            foreach (KeyValuePair<ushort, byte> pair in morphs)
                packedMorphs.Add(new CharacterMorphSelection(pair.Key, pair.Value));
            packedMorphs.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            result.morphs = packedMorphs.ToArray();

            var colors = new Dictionary<ushort, CharacterColorSelection>();
            CharacterColorSelection[] targetColors = result.colors ?? Array.Empty<CharacterColorSelection>();
            for (int i = 0; i < targetColors.Length; ++i)
                if (targetColors[i].channelId != 0) colors[targetColors[i].channelId] = targetColors[i];
            CharacterColorSelection[] sourceColors = source.colors ?? Array.Empty<CharacterColorSelection>();
            for (int i = 0; i < sourceColors.Length; ++i)
            {
                if (sourceColors[i].channelId == 0) continue;
                colors[sourceColors[i].channelId] = sourceColors[i];
                if (colors.Count >= CharacterAppearanceRecipe.MaxColorSelections) break;
            }
            var packedColors = new List<CharacterColorSelection>(colors.Values);
            packedColors.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            result.colors = packedColors.ToArray();
            result.revision = source.revision;
            return result;
        }

        public static uint CatalogVersionFor(ushort visualProfileId)
        {
            return TryResolve(visualProfileId, out CharacterVisualProfile profile) ? profile.CatalogVersion : 0u;
        }

        public static void ResetForTestsOrReload()
        {
            _default = null;
            _profiles = null;
        }
    }

}
