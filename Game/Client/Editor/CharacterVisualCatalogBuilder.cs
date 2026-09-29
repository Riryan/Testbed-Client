#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Builds the client-only semantic visual catalog from the imported modular source.
    /// The source prefab remains an authoring catalog; creator policy decides which of its
    /// meshes are appearance choices versus equipment-driven content.
    /// </summary>
    public static class CharacterVisualCatalogBuilder
    {
        private const int BuilderSchemaVersion = 12;
        private const ushort VisualProfileId = 1;
        internal const string SourcePrefabPath =
            "Assets/Game/Client/ThirdParty/Character-Custom/Prefabs/ModularCharacter.prefab";
        internal const string EditableBasePrefabPath =
            "Assets/Game/Client/ThirdParty/Character-Custom/Prefabs/Character.prefab";
        internal const string OutputDirectory = "Assets/Game/Client/Resources/MMO/Characters";
        internal const string OutputAssetPath = OutputDirectory + "/CharacterVisualProfile.asset";

        private static readonly Regex SlotRegex =
            new Regex(@"_(\d{2})([A-Z]{3,5})_", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [MenuItem("MMO Tools/Characters/Authoring/Rebuild Character Visual Catalog")]
        public static void RebuildFromMenu() => BuildCatalog(force: true);

        private static void BuildCatalog(bool force)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
            GameObject editableBase = AssetDatabase.LoadAssetAtPath<GameObject>(EditableBasePrefabPath);
            if (source == null || editableBase == null)
                return;

            string sourceHash = AssetDatabase.GetAssetDependencyHash(SourcePrefabPath).ToString();
            string baseHash = AssetDatabase.GetAssetDependencyHash(EditableBasePrefabPath).ToString();
            string fingerprint = "builder:" + BuilderSchemaVersion + ":" + sourceHash + ":" + baseHash;
            CharacterVisualProfile existing = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(OutputAssetPath);
            if (!force && existing != null && existing.SourceFingerprint == fingerprint &&
                existing.SourcePrefab == source && existing.EditableBasePrefab == editableBase)
                return;

            CharacterVisualSlotDefinition[] slots = BuildSlots(source, editableBase, existing);
            CharacterVisualMorphDefinition[] morphs = BuildMorphs(source, editableBase);
            CharacterVisualColorDefinition[] colors = BuildColorChannels();

            Directory.CreateDirectory(OutputDirectory);
            AssetDatabase.Refresh();

            CharacterVisualProfile profile = existing;
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<CharacterVisualProfile>();
                AssetDatabase.CreateAsset(profile, OutputAssetPath);
            }

            uint catalogVersion = Stable32(fingerprint);
            if (catalogVersion == 0)
                catalogVersion = 1;
            profile.Configure(VisualProfileId, catalogVersion, fingerprint, source, editableBase, slots, morphs, colors);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();

            int optionCount = slots.Sum(s => s?.options?.Length ?? 0);
            int creatorOptions = slots.Sum(s => s?.options?.Count(o => o != null && o.creatorSelectable && !o.equipmentDriven) ?? 0);
            int populationOptions = slots.Sum(s => s?.options?.Count(o => o != null && o.AllowsPopulationUse) ?? 0);
            int savedMorphs = morphs.Count(m => m != null && m.persistInAppearance);
            Debug.Log(
                $"[CharacterVisualCatalog] Built profile {VisualProfileId}: {slots.Length} slots, " +
                $"{optionCount} source options ({creatorOptions} creator options, {populationOptions} Population options), " +
                $"{savedMorphs} saved morph controls, {colors.Length} color channels. Catalog={catalogVersion}.");
            if (!Application.isBatchMode)
                CharacterVisualThumbnailRenderer.GenerateMissingForCatalog();
        }

        private static CharacterVisualSlotDefinition[] BuildSlots(GameObject source, GameObject editableBase, CharacterVisualProfile existing)
        {
            var existingOptions = new Dictionary<ulong, CharacterVisualOptionDefinition>();
            if (existing != null)
            {
                IReadOnlyList<CharacterVisualSlotDefinition> oldSlots = existing.Slots;
                for (int s = 0; s < oldSlots.Count; ++s)
                {
                    CharacterVisualSlotDefinition oldSlot = oldSlots[s];
                    if (oldSlot == null)
                        continue;
                    CharacterVisualOptionDefinition[] oldOptions = oldSlot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                    for (int o = 0; o < oldOptions.Length; ++o)
                    {
                        CharacterVisualOptionDefinition old = oldOptions[o];
                        if (old != null)
                            existingOptions[OptionKey(oldSlot.slotId, old.optionId)] = old;
                    }
                }
            }

            Dictionary<ushort, string> baseDefaultNames = BuildBaseDefaultRendererNames(editableBase);
            HashSet<string> editableTransformPaths = BuildTransformPathSet(editableBase.transform);

            SkinnedMeshRenderer[] renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var grouped = new Dictionary<ushort, List<SkinnedMeshRenderer>>();
            var codes = new Dictionary<ushort, string>();
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                Match match = SlotRegex.Match(renderer.gameObject.name);
                if (!match.Success || !ushort.TryParse(match.Groups[1].Value, out ushort slotId) || slotId == 0)
                    continue;
                if (!grouped.TryGetValue(slotId, out List<SkinnedMeshRenderer> list))
                {
                    list = new List<SkinnedMeshRenderer>();
                    grouped.Add(slotId, list);
                    codes[slotId] = match.Groups[1].Value + match.Groups[2].Value;
                }
                list.Add(renderer);
            }

            var result = new List<CharacterVisualSlotDefinition>(grouped.Count);
            foreach (KeyValuePair<ushort, List<SkinnedMeshRenderer>> pair in grouped.OrderBy(p => p.Key))
            {
                ushort slotId = pair.Key;
                List<SkinnedMeshRenderer> values = pair.Value;
                values.Sort((a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));
                var options = new List<CharacterVisualOptionDefinition>(values.Count);
                var seenOptionIds = new HashSet<ushort>();

                ushort defaultOption = 0;
                for (int i = 0; i < values.Count; ++i)
                {
                    SkinnedMeshRenderer renderer = values[i];
                    string rawName = renderer.gameObject.name;
                    ushort optionId = Stable16(rawName);
                    if (!seenOptionIds.Add(optionId))
                        throw new InvalidOperationException($"Character visual option id collision in slot {slotId}: {rawName} -> {optionId}");

                    string normalizedPair = SlotRegex.Replace(rawName, "_PAIR_", 1);
                    ushort pairId = Stable16(normalizedPair);
                    bool equipmentDriven = IsEquipmentDrivenSlot(slotId) || IsEquipmentLikeBodyOption(slotId, rawName);
                    bool creatorSelectable = IsCreatorAppearanceOption(slotId, rawName) && !equipmentDriven;

                    string rootBonePath = renderer.rootBone != null ? RelativePath(source.transform, renderer.rootBone) : string.Empty;
                    string[] bonePaths = BuildBonePaths(source.transform, renderer.bones);
                    ValidateEditableSkeletonCompatibility(rawName, rootBonePath, bonePaths, editableTransformPaths);

                    var option = new CharacterVisualOptionDefinition
                    {
                        optionId = optionId,
                        pairId = pairId,
                        displayName = FriendlyOptionName(rawName),
                        rendererPath = RelativePath(source.transform, renderer.transform),
                        mesh = renderer.sharedMesh,
                        materials = renderer.sharedMaterials != null ? renderer.sharedMaterials.ToArray() : Array.Empty<Material>(),
                        rootBonePath = rootBonePath,
                        bonePaths = bonePaths,
                        localPosition = renderer.transform.localPosition,
                        localRotation = renderer.transform.localRotation,
                        localScale = renderer.transform.localScale,
                        localBounds = renderer.localBounds,
                        creatorSelectable = creatorSelectable,
                        equipmentDriven = equipmentDriven,
                        usageReviewed = false,
                        playerEligible = true,
                        populationEligible = IsPopulationEligible(slotId, rawName),
                    };

                    if (existingOptions.TryGetValue(OptionKey(slotId, optionId), out CharacterVisualOptionDefinition old))
                    {
                        option.accessTier = old.accessTier;
                        option.factionRestricted = old.factionRestricted;
                        option.requiredFaction = old.requiredFaction;
                        option.thumbnail = old.thumbnail;
                        option.thumbnailFingerprint = old.thumbnailFingerprint;
                        option.paletteCells = old.paletteCells != null
                            ? old.paletteCells.ToArray()
                            : Array.Empty<CharacterVisualPaletteCellDefinition>();
                        option.paletteReviewConfirmed = old.paletteReviewConfirmed;
                        option.paletteReviewFingerprint = old.paletteReviewFingerprint;
                        option.paletteScanSignature = old.paletteScanSignature;
                        option.usageReviewed = old.usageReviewed;
                        if (old.usageReviewed)
                        {
                            option.playerEligible = old.playerEligible;
                            option.populationEligible = old.populationEligible;
                        }
                        else
                        {
                            // Before explicit review, keep Player compatibility open and preserve
                            // the existing Population default/seed classification.
                            option.playerEligible = true;
                            option.populationEligible = old.populationEligible;
                        }
                        // Creator/equipment classification is derived from the current builder schema.
                        // Preserve authored entitlement/faction/thumbnail metadata, but do not carry
                        // the V1 default value of creatorSelectable forward as an accidental lockout.
                    }

                    options.Add(option);
                    if (baseDefaultNames.TryGetValue(slotId, out string baseName) &&
                        string.Equals(baseName, rawName, StringComparison.Ordinal))
                        defaultOption = optionId;
                }

                if (defaultOption == 0 && !SlotAllowsNone(slotId) && !IsEquipmentDrivenSlot(slotId))
                {
                    CharacterVisualOptionDefinition coherent = options.FirstOrDefault(o =>
                        o != null && o.rendererPath != null &&
                        o.rendererPath.IndexOf("SK_HUMN_BASE_01_", StringComparison.OrdinalIgnoreCase) >= 0);
                    CharacterVisualOptionDefinition firstCreator = options.FirstOrDefault(o => o != null && o.creatorSelectable);
                    defaultOption = coherent != null ? coherent.optionId :
                        (firstCreator != null ? firstCreator.optionId : (options.Count > 0 ? options[0].optionId : (ushort)0));
                }

                ushort mirror = MirrorSlot(slotId);
                bool mirrorPrimary = mirror == 0 || slotId < mirror;
                result.Add(new CharacterVisualSlotDefinition
                {
                    slotId = slotId,
                    slotCode = codes[slotId],
                    displayName = SlotDisplayName(slotId, mirrorPrimary),
                    category = SlotCategory(slotId),
                    region = SlotRegion(slotId),
                    allowNone = IsEquipmentDrivenSlot(slotId) || SlotAllowsNone(slotId),
                    equipmentDriven = IsEquipmentDrivenSlot(slotId),
                    mirrorSlotId = mirror,
                    mirrorPrimary = mirrorPrimary,
                    defaultOptionId = IsEquipmentDrivenSlot(slotId) ? (ushort)0 : defaultOption,
                    options = options.ToArray(),
                });
            }
            return result.ToArray();
        }

        private sealed class MorphGroupCandidate
        {
            public readonly List<string> rawNames = new List<string>();
            public bool seenOnFaceSlot;
        }

        private static CharacterVisualMorphDefinition[] BuildMorphs(GameObject source, GameObject editableBase)
        {
            SkinnedMeshRenderer[] renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var grouped = new Dictionary<string, MorphGroupCandidate>(StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < renderers.Length; ++r)
            {
                SkinnedMeshRenderer renderer = renderers[r];
                Mesh mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null)
                    continue;

                ushort slotId = 0;
                Match slotMatch = SlotRegex.Match(renderer.gameObject.name);
                if (slotMatch.Success)
                    ushort.TryParse(slotMatch.Groups[1].Value, out slotId);
                bool faceSlot = IsFaceMorphSlot(slotId);

                for (int b = 0; b < mesh.blendShapeCount; ++b)
                {
                    string rawName = mesh.GetBlendShapeName(b);
                    if (string.IsNullOrWhiteSpace(rawName))
                        continue;

                    rawName = rawName.Trim();
                    string canonical = CanonicalBlendShapeToken(rawName);
                    if (string.IsNullOrWhiteSpace(canonical))
                        continue;

                    string key = BuildMorphGroupKey(canonical);
                    if (!grouped.TryGetValue(key, out MorphGroupCandidate candidate))
                    {
                        candidate = new MorphGroupCandidate();
                        grouped[key] = candidate;
                    }

                    if (!candidate.rawNames.Contains(rawName))
                        candidate.rawNames.Add(rawName);
                    if (faceSlot)
                        candidate.seenOnFaceSlot = true;
                }
            }

            string[] bodyOrder = { "defaultBuff", "defaultHeavy", "defaultSkinny", "masculineFeminine" };
            var bodyKeys = new HashSet<string>(bodyOrder.Select(BuildMorphGroupKey), StringComparer.OrdinalIgnoreCase);
            var result = new List<CharacterVisualMorphDefinition>();
            var usedChannels = new HashSet<ushort>();

            foreach (string body in bodyOrder)
            {
                string key = BuildMorphGroupKey(body);
                if (!grouped.TryGetValue(key, out MorphGroupCandidate candidate))
                    continue;

                result.Add(BuildMorphDefinition(
                    source,
                    editableBase,
                    body,
                    candidate.rawNames,
                    CharacterVisualMorphClassification.Body,
                    CharacterCreatorCategory.Body,
                    CharacterVisualRegionMask.All,
                    usedChannels));
            }

            foreach (KeyValuePair<string, MorphGroupCandidate> pair in grouped.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (bodyKeys.Contains(pair.Key))
                    continue;

                MorphGroupCandidate candidate = pair.Value;
                // Match the proven uMMORPG behavior: persistent face controls are built
                // from facial/head source meshes only. Copies of the same semantic
                // blendshape on body/equipment meshes are targets of the same logical
                // control, not additional creator sliders.
                if (candidate == null || !candidate.seenOnFaceSlot)
                    continue;

                CharacterVisualMorphClassification classification =
                    ClassifyMorph(pair.Key, candidate.rawNames);
                result.Add(BuildMorphDefinition(
                    source,
                    editableBase,
                    pair.Key,
                    candidate.rawNames,
                    classification,
                    CharacterCreatorCategory.Face,
                    CharacterVisualRegionMask.HeadHair,
                    usedChannels));
            }

            // Bone-driven proportions are stored in the exact same compact morph channel
            // contract as blendshapes. The server/network recipe remains presentation-agnostic;
            // only this client profile knows whether a channel drives a blendshape or bones.
            result.AddRange(BuildBoneMorphDefinitions(editableBase, source, usedChannels));

            return result
                .OrderBy(m => m.category)
                .ThenBy(m => MorphOrder(m))
                .ThenBy(m => m.displayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static CharacterVisualMorphDefinition BuildMorphDefinition(
            GameObject source,
            GameObject editableBase,
            string key,
            List<string> targets,
            CharacterVisualMorphClassification classification,
            CharacterCreatorCategory category,
            CharacterVisualRegionMask region,
            HashSet<ushort> usedChannels)
        {
            ushort channelId = Stable16("morph:" + key.ToLowerInvariant());
            int salt = 1;
            while (!usedChannels.Add(channelId))
                channelId = Stable16("morph:" + key.ToLowerInvariant() + "#" + salt++);

            float min = 0f;
            float max = 100f;
            float defaultWeight = 0f;
            SkinnedMeshRenderer[] renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < renderers.Length; ++r)
            {
                SkinnedMeshRenderer renderer = renderers[r];
                Mesh mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null)
                    continue;
                for (int t = 0; t < targets.Count; ++t)
                {
                    int index = mesh.GetBlendShapeIndex(targets[t]);
                    if (index < 0)
                        continue;
                    int frames = mesh.GetBlendShapeFrameCount(index);
                    for (int f = 0; f < frames; ++f)
                    {
                        float frameWeight = mesh.GetBlendShapeFrameWeight(index, f);
                        min = Mathf.Min(min, frameWeight);
                        max = Mathf.Max(max, frameWeight);
                    }
                }
            }

            // Defaults come from the clean editable base, not the giant source/catalog
            // prefab. The source intentionally contains many authored alternatives and may
            // have non-zero preview weights that are not the default new-character shape.
            bool defaultFound = false;
            if (editableBase != null)
            {
                SkinnedMeshRenderer[] baseRenderers = editableBase.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int r = 0; r < baseRenderers.Length && !defaultFound; ++r)
                {
                    SkinnedMeshRenderer renderer = baseRenderers[r];
                    Mesh mesh = renderer != null ? renderer.sharedMesh : null;
                    if (mesh == null)
                        continue;
                    for (int t = 0; t < targets.Count; ++t)
                    {
                        int index = mesh.GetBlendShapeIndex(targets[t]);
                        if (index < 0)
                            continue;
                        defaultWeight = renderer.GetBlendShapeWeight(index);
                        defaultFound = true;
                        break;
                    }
                }
            }

            bool saved = classification == CharacterVisualMorphClassification.Body ||
                         classification == CharacterVisualMorphClassification.Structural;
            return new CharacterVisualMorphDefinition
            {
                channelId = channelId,
                semanticName = CanonicalBlendShapeToken(key),
                semanticNames = targets.Distinct(StringComparer.Ordinal).ToArray(),
                displayName = BodyMorphDisplayName(key),
                category = category,
                region = region,
                classification = classification,
                driver = CharacterVisualMorphDriver.BlendShape,
                boneTargets = Array.Empty<CharacterVisualBoneTargetDefinition>(),
                visibleInCreator = saved,
                persistInAppearance = saved,
                minWeight = min,
                maxWeight = Mathf.Approximately(min, max) ? min + 100f : max,
                defaultValue = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(min, Mathf.Approximately(min, max) ? min + 100f : max, defaultWeight) * 255f), 0, 255),
            };
        }

        private static CharacterVisualMorphDefinition[] BuildBoneMorphDefinitions(
            GameObject editableBase,
            GameObject source,
            HashSet<ushort> usedChannels)
        {
            if (editableBase == null && source == null)
                return Array.Empty<CharacterVisualMorphDefinition>();

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddBonePaths(editableBase, byName);
            // Prefab/model assets can expose valid SkinnedMeshRenderer.bones references even
            // when those imported model transforms are not returned as ordinary prefab children
            // by GetComponentsInChildren<Transform>. Harvest the renderer references explicitly.
            // This is the same skeleton data already used when the catalog records mesh bonePaths,
            // and prevents the creator from silently publishing only blendshape sliders.
            AddRendererBonePaths(editableBase, byName);

            // The modular catalog source uses the same canonical Sidekick skeleton. Use both its
            // transform tree and renderer bone references as a missing-path fallback.
            AddBonePaths(source, byName);
            AddRendererBonePaths(source, byName);

            var result = new List<CharacterVisualMorphDefinition>();
            AddBoneMorph(result, usedChannels, byName,
                "height", "Height", new[] { "root" },
                new Vector3(0.95f, 0.95f, 0.95f), new Vector3(1.05f, 1.05f, 1.05f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "headSize", "Head Size", new[] { "head" },
                Vector3.one * 0.90f, Vector3.one * 1.12f, Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "neckLength", "Neck Length", new[] { "neck_01" },
                Vector3.one, Vector3.one, new Vector3(1f, 0.90f, 1f), new Vector3(1f, 1.10f, 1f));
            AddBoneMorph(result, usedChannels, byName,
                "neckSize", "Neck Size", new[] { "neck_01" },
                new Vector3(1f, 0.90f, 0.90f), new Vector3(1f, 1.12f, 1.12f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "shoulderWidth", "Shoulder Width", new[] { "clavicle_l", "clavicle_r" },
                Vector3.one, Vector3.one, new Vector3(0.88f, 1f, 1f), new Vector3(1.12f, 1f, 1f));
            AddBoneMorph(result, usedChannels, byName,
                "torsoLength", "Torso Length", new[] { "spine_01", "spine_02", "spine_03" },
                Vector3.one, Vector3.one, new Vector3(1f, 0.94f, 1f), new Vector3(1f, 1.06f, 1f));
            AddBoneMorph(result, usedChannels, byName,
                "waistSize", "Waist Size", new[] { "spine_01", "spine_02" },
                new Vector3(1f, 0.88f, 0.88f), new Vector3(1f, 1.18f, 1.18f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "hipWidth", "Hip Width", new[] { "thigh_l", "thigh_r" },
                new Vector3(1f, 0.92f, 0.92f), new Vector3(1f, 1.12f, 1.12f),
                new Vector3(0.92f, 1f, 1f), new Vector3(1.08f, 1f, 1f));
            AddBoneMorph(result, usedChannels, byName,
                "upperArmSize", "Upper Arms", new[] { "upperarm_l", "upperarm_r" },
                new Vector3(1f, 0.90f, 0.90f), new Vector3(1f, 1.12f, 1.12f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "forearmSize", "Forearms", new[] { "lowerarm_l", "lowerarm_r" },
                new Vector3(1f, 0.90f, 0.90f), new Vector3(1f, 1.12f, 1.12f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "thighSize", "Thighs", new[] { "thigh_l", "thigh_r" },
                new Vector3(1f, 0.90f, 0.90f), new Vector3(1f, 1.12f, 1.12f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "calfSize", "Calves", new[] { "calf_l", "calf_r" },
                new Vector3(1f, 0.90f, 0.90f), new Vector3(1f, 1.12f, 1.12f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "armLength", "Arm Length", new[] { "upperarm_l", "upperarm_r" },
                new Vector3(0.94f, 1f, 1f), new Vector3(1.06f, 1f, 1f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "forearmLength", "Forearm Length", new[] { "lowerarm_l", "lowerarm_r" },
                new Vector3(0.94f, 1f, 1f), new Vector3(1.06f, 1f, 1f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "handScale", "Hand Scale", new[] { "hand_l", "hand_r" },
                Vector3.one * 0.92f, Vector3.one * 1.08f, Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "legLength", "Leg Length", new[] { "thigh_l", "thigh_r" },
                new Vector3(0.94f, 1f, 1f), new Vector3(1.06f, 1f, 1f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "lowerLegLength", "Lower Leg Length", new[] { "calf_l", "calf_r" },
                new Vector3(0.94f, 1f, 1f), new Vector3(1.06f, 1f, 1f), Vector3.one, Vector3.one);
            AddBoneMorph(result, usedChannels, byName,
                "footScale", "Foot Scale", new[] { "foot_l", "foot_r" },
                Vector3.one * 0.92f, Vector3.one * 1.08f, Vector3.one, Vector3.one);
            return result.ToArray();
        }

        private static void AddBonePaths(GameObject rootObject, Dictionary<string, string> byName)
        {
            if (rootObject == null || byName == null)
                return;
            Transform[] transforms = rootObject.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; ++i)
            {
                Transform value = transforms[i];
                if (value == null || byName.ContainsKey(value.name))
                    continue;
                byName[value.name] = RelativePath(rootObject.transform, value);
            }
        }

        private static void AddRendererBonePaths(GameObject rootObject, Dictionary<string, string> byName)
        {
            if (rootObject == null || byName == null)
                return;

            SkinnedMeshRenderer[] renderers = rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < renderers.Length; ++r)
            {
                SkinnedMeshRenderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                AddReferencedBonePath(rootObject.transform, renderer.rootBone, byName);
                Transform[] bones = renderer.bones ?? Array.Empty<Transform>();
                for (int b = 0; b < bones.Length; ++b)
                    AddReferencedBonePath(rootObject.transform, bones[b], byName);
            }
        }

        private static void AddReferencedBonePath(
            Transform root,
            Transform bone,
            Dictionary<string, string> byName)
        {
            if (root == null || bone == null || byName == null || byName.ContainsKey(bone.name))
                return;

            string path = RelativePath(root, bone);
            if (!string.IsNullOrWhiteSpace(path))
                byName[bone.name] = path;
        }

        private static void AddBoneMorph(
            List<CharacterVisualMorphDefinition> output,
            HashSet<ushort> usedChannels,
            Dictionary<string, string> transformPaths,
            string semanticName,
            string displayName,
            string[] requiredBoneNames,
            Vector3 minScale,
            Vector3 maxScale,
            Vector3 minPosition,
            Vector3 maxPosition)
        {
            if (output == null || transformPaths == null || requiredBoneNames == null || requiredBoneNames.Length == 0)
                return;

            // Keep newly generated bone-driver ranges aligned with the runtime safety cap.
            // The runtime presenter still clamps the final composed result because multiple
            // logical controls can affect the same bone.
            minScale = ClampBoneMorphMultiplier(minScale);
            maxScale = ClampBoneMorphMultiplier(maxScale);
            minPosition = ClampBoneMorphMultiplier(minPosition);
            maxPosition = ClampBoneMorphMultiplier(maxPosition);

            var targets = new CharacterVisualBoneTargetDefinition[requiredBoneNames.Length];
            for (int i = 0; i < requiredBoneNames.Length; ++i)
            {
                if (!transformPaths.TryGetValue(requiredBoneNames[i], out string path) || string.IsNullOrWhiteSpace(path))
                    return;
                targets[i] = new CharacterVisualBoneTargetDefinition
                {
                    transformPath = path,
                    minLocalScaleMultiplier = minScale,
                    maxLocalScaleMultiplier = maxScale,
                    minLocalPositionMultiplier = minPosition,
                    maxLocalPositionMultiplier = maxPosition,
                };
            }

            ushort channelId = Stable16("bone:" + semanticName.ToLowerInvariant());
            int salt = 1;
            while (!usedChannels.Add(channelId))
                channelId = Stable16("bone:" + semanticName.ToLowerInvariant() + "#" + salt++);

            // Sidekick/Synty profile V1 intentionally suppresses bone controls that are
            // technically valid transforms but produce little useful change or visibly poor
            // deformation on the canonical Sidekick mesh. Keep the driver definitions in the
            // profile so the capability can be revisited without deleting the implementation,
            // but do not expose or persist them for this visual profile. A future character
            // family/profile may opt into a different control policy.
            bool supportedForSidekickCreator = IsSupportedSidekickBoneControl(semanticName);

            output.Add(new CharacterVisualMorphDefinition
            {
                channelId = channelId,
                semanticName = semanticName,
                semanticNames = Array.Empty<string>(),
                displayName = displayName,
                category = CharacterCreatorCategory.Body,
                region = CharacterVisualRegionMask.All,
                classification = CharacterVisualMorphClassification.Structural,
                driver = CharacterVisualMorphDriver.Bone,
                boneTargets = targets,
                visibleInCreator = supportedForSidekickCreator,
                persistInAppearance = supportedForSidekickCreator,
                minWeight = 0f,
                maxWeight = 1f,
                defaultValue = 128,
            });
        }

        private static Vector3 ClampBoneMorphMultiplier(Vector3 value)
        {
            const float maxDelta = 0.03f;
            const float min = 1f - maxDelta;
            const float max = 1f + maxDelta;
            return new Vector3(
                Mathf.Clamp(value.x, min, max),
                Mathf.Clamp(value.y, min, max),
                Mathf.Clamp(value.z, min, max));
        }

        private static bool IsSupportedSidekickBoneControl(string semanticName)
        {
            // Runtime-tested on the current Sidekick/Synty character. These controls either
            // had no useful visible effect or produced unacceptable deformation at their
            // authored range, so they are intentionally hidden/disabled for profile 1.
            switch (semanticName)
            {
                case "neckSize":
                case "torsoLength":
                case "waistSize":
                case "lowerLegLength":
                    return false;
                default:
                    return true;
            }
        }

        private static int MorphOrder(CharacterVisualMorphDefinition morph)
        {
            if (morph == null)
                return 1000;
            switch (morph.semanticName)
            {
                case "defaultBuff": return 0;
                case "defaultHeavy": return 10;
                case "defaultSkinny": return 20;
                case "masculineFeminine": return 30;
                case "height": return 40;
                case "headSize": return 50;
                case "neckLength": return 60;
                case "neckSize": return 70;
                case "shoulderWidth": return 80;
                case "torsoLength": return 90;
                case "waistSize": return 100;
                case "hipWidth": return 110;
                case "upperArmSize": return 120;
                case "forearmSize": return 130;
                case "thighSize": return 140;
                case "calfSize": return 150;
                case "armLength": return 160;
                case "forearmLength": return 170;
                case "handScale": return 180;
                case "legLength": return 190;
                case "lowerLegLength": return 200;
                case "footScale": return 210;
                default: return 300;
            }
        }

        private static CharacterVisualColorDefinition[] BuildColorChannels()
        {
            return new[]
            {
                new CharacterVisualColorDefinition
                {
                    channelId = 1, semanticName = "skin", displayName = "Skin Color",
                    category = CharacterCreatorCategory.Body, region = CharacterVisualRegionMask.All,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.SkinPalette),
                },
                new CharacterVisualColorDefinition
                {
                    channelId = 3, semanticName = "eyes", displayName = "Eye Color",
                    category = CharacterCreatorCategory.Face, region = CharacterVisualRegionMask.HeadHair,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    affectedSlotIds = new ushort[] { 5, 6 },
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.EyePalette),
                },
                new CharacterVisualColorDefinition
                {
                    channelId = 2, semanticName = "hair", displayName = "Hair Color",
                    category = CharacterCreatorCategory.Hair, region = CharacterVisualRegionMask.HeadHair,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    affectedSlotIds = new ushort[] { 2, 3, 4, 9 },
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.HairPalette),
                },
                new CharacterVisualColorDefinition
                {
                    channelId = 4, semanticName = "hairAccessory", displayName = "Acc's Color",
                    category = CharacterCreatorCategory.Hair, region = CharacterVisualRegionMask.HeadHair,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    affectedSlotIds = new ushort[] { 2, 9 },
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.HairPalette),
                },
                new CharacterVisualColorDefinition
                {
                    channelId = SidekickCharacterPaletteUtility.ClothingPrimaryColorChannelId,
                    semanticName = "clothingPrimary", displayName = "Clothing Primary",
                    category = CharacterCreatorCategory.Body, region = CharacterVisualRegionMask.All,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.ClothingPalette),
                },
                new CharacterVisualColorDefinition
                {
                    channelId = SidekickCharacterPaletteUtility.ClothingSecondaryColorChannelId,
                    semanticName = "clothingSecondary", displayName = "Clothing Secondary",
                    category = CharacterCreatorCategory.Body, region = CharacterVisualRegionMask.All,
                    encoding = Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    suggestedRgba = PackColors(SidekickCharacterPaletteUtility.ClothingPalette),
                },
            };
        }

        private static uint[] PackColors(Color[] colors)
        {
            if (colors == null)
                return Array.Empty<uint>();
            var values = new uint[colors.Length];
            for (int i = 0; i < colors.Length; ++i)
                values[i] = ModularCharacterAppearancePresenter.PackRgba(colors[i]);
            return values;
        }

        private static bool IsCreatorAppearanceOption(ushort slotId, string rawName)
        {
            if (IsEquipmentDrivenSlot(slotId))
                return false;

            // The basic creator owns identity appearance, not clothing/equipment. Keep the
            // canonical head/teeth/tongue source pieces fixed and shape the face through the
            // semantic morph catalog plus brows/eyes/ears/nose/facial-hair selections.
            if (slotId == 1 || slotId == 36 || slotId == 37)
                return false;

            // Base body slots are intentionally not individually selectable in the basic
            // creator. Outfit/equipment owns replacements later; Body is driven by shape
            // keys + skin while the coherent Human Base pieces remain underneath equipment.
            if (slotId >= 10 && slotId <= 21)
                return false;
            return true;
        }

        private static bool IsEquipmentLikeBodyOption(ushort slotId, string rawName)
        {
            if (slotId < 10 || slotId > 21)
                return false;
            return rawName == null || rawName.IndexOf("_BASE_", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsEquipmentDrivenSlot(ushort slotId) => slotId == 22 || slotId == 23 || slotId == 24 || slotId == 29 || slotId == 30;

        // Ambient Population deliberately reuses the Player visual catalog. Keep this policy
        // authoring-time only so runtime selection never has to classify FBX/source names.
        // Approved content:
        //   - all Human Base
        //   - all Modern Civilians
        //   - Elven Warriors hair / brows / facial hair only
        //   - Fantasy Knights hair / brows / facial hair only
        private static bool IsPopulationEligible(ushort slotId, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return false;

            if (rawName.IndexOf("SK_HUMN_BASE_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawName.IndexOf("SK_MDRN_CIVL_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            bool restrictedFantasySource =
                rawName.IndexOf("SK_ELVN_WARR_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawName.IndexOf("SK_FANT_KNGT_", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!restrictedFantasySource)
                return false;

            return slotId == 2 || slotId == 3 || slotId == 4 || slotId == 9;
        }

        private static bool IsCoherentHumanBase(string rawName) => !string.IsNullOrWhiteSpace(rawName) && rawName.IndexOf("SK_HUMN_BASE_01_", StringComparison.OrdinalIgnoreCase) >= 0;
        private static ulong OptionKey(ushort slotId, ushort optionId) => ((ulong)slotId << 32) | optionId;

        private static CharacterCreatorCategory SlotCategory(ushort slotId)
        {
            switch (slotId)
            {
                case 2: return CharacterCreatorCategory.Hair;
                case 1: case 3: case 4: case 5: case 6: case 7: case 8: case 9: case 35: case 36: case 37:
                    return CharacterCreatorCategory.Face;
                case 22: case 23: case 24: case 29: case 30:
                    return CharacterCreatorCategory.Details;
                default: return CharacterCreatorCategory.Body;
            }
        }

        private static CharacterVisualRegionMask SlotRegion(ushort slotId)
        {
            if (slotId <= 9 || slotId == 22 || slotId == 23 || slotId >= 35)
                return CharacterVisualRegionMask.HeadHair;
            if (slotId >= 17 && slotId <= 21)
                return CharacterVisualRegionMask.LowerBody;
            return CharacterVisualRegionMask.UpperBody;
        }

        private static bool SlotAllowsNone(ushort slotId)
        {
            return slotId == 2 || slotId == 3 || slotId == 4 || slotId == 9 ||
                   slotId == 22 || slotId == 23 || slotId == 24 || slotId == 29 || slotId == 30;
        }

        private static ushort MirrorSlot(ushort slotId)
        {
            switch (slotId)
            {
                case 3: return 4; case 4: return 3; case 5: return 6; case 6: return 5;
                case 7: return 8; case 8: return 7; case 11: return 12; case 12: return 11;
                case 13: return 14; case 14: return 13; case 15: return 16; case 16: return 15;
                case 18: return 19; case 19: return 18; case 20: return 21; case 21: return 20;
                case 29: return 30; case 30: return 29; default: return 0;
            }
        }

        private static string SlotDisplayName(ushort slotId, bool primary)
        {
            switch (slotId)
            {
                case 1: return "Head"; case 2: return "Hair";
                case 3: return primary ? "Eyebrows" : "Right Eyebrow"; case 4: return "Right Eyebrow";
                case 5: return primary ? "Eyes" : "Right Eye"; case 6: return "Right Eye";
                case 7: return primary ? "Ears" : "Right Ear"; case 8: return "Right Ear";
                case 9: return "Facial Hair"; case 10: return "Torso";
                case 11: return primary ? "Upper Arms" : "Right Upper Arm"; case 12: return "Right Upper Arm";
                case 13: return primary ? "Lower Arms" : "Right Lower Arm"; case 14: return "Right Lower Arm";
                case 15: return primary ? "Hands" : "Right Hand"; case 16: return "Right Hand";
                case 17: return "Hips / Lower Base"; case 18: return primary ? "Legs" : "Right Leg";
                case 19: return "Right Leg"; case 20: return primary ? "Feet" : "Right Foot";
                case 21: return "Right Foot"; case 22: return "Head Accessory"; case 23: return "Face Accessory";
                case 24: return "Back Accessory"; case 29: return primary ? "Shoulders" : "Right Shoulder";
                case 30: return "Right Shoulder"; case 35: return "Nose"; case 36: return "Teeth";
                case 37: return "Tongue"; default: return "Slot " + slotId;
            }
        }

        private static CharacterVisualMorphClassification ClassifyMorph(string key, List<string> targets)
        {
            string canonical = CanonicalBlendShapeToken(key ?? string.Empty);
            string probe = canonical.ToLowerInvariant();
            if (targets != null)
                for (int i = 0; i < targets.Count; ++i)
                    probe += " " + CanonicalBlendShapeToken(targets[i] ?? string.Empty).ToLowerInvariant();

            if (probe.Contains("defaultbuff") || probe.Contains("defaultheavy") ||
                probe.Contains("defaultskinny") || probe.Contains("masculinefeminine"))
                return CharacterVisualMorphClassification.Body;

            // Permanent identity shapes are explicit. This prevents animation channels such
            // as browRaise, cheekPuff, jawOpen, mouthSmile and noseSneer from becoming saved
            // facial identity merely because their names mention facial anatomy.
            if (canonical.StartsWith("id", StringComparison.OrdinalIgnoreCase))
                return CharacterVisualMorphClassification.Structural;

            if (LooksInternal(probe))
                return CharacterVisualMorphClassification.Internal;

            string[] expressionTerms =
            {
                "blink", "smile", "frown", "happy", "sad", "angry", "anger", "fear", "disgust", "surprise",
                "squint", "sneer", "viseme", "phoneme", "jawopen", "jawleft", "jawright", "jawforward", "jawbackward",
                "mouthopen", "mouthclose", "kiss", "puff", "tongue", "eyesclosed", "eyeclosed", "eyewide", "eyelook",
                "mouthfunnel", "mouthpress", "mouthpucker", "mouthroll", "mouthshrug", "mouthstretch", "mouthdimple",
                "mouthlowerdown", "mouthupperup", "mouthleft", "mouthright", "brow", "cheek", "iris"
            };
            for (int i = 0; i < expressionTerms.Length; ++i)
                if (probe.Contains(expressionTerms[i]))
                    return CharacterVisualMorphClassification.Expression;

            return CharacterVisualMorphClassification.Unknown;
        }

        private static bool LooksInternal(string probe) => probe.Contains("corrective") || probe.Contains("helper") || probe.Contains("driver") || probe.EndsWith("shape", StringComparison.OrdinalIgnoreCase);

        private static string CanonicalBlendShapeToken(string blendShapeName)
        {
            if (string.IsNullOrWhiteSpace(blendShapeName))
                return string.Empty;

            string token = blendShapeName.Trim();
            int separator = token.LastIndexOf('.');
            if (separator >= 0 && separator + 1 < token.Length)
                token = token.Substring(separator + 1);

            int pipe = token.LastIndexOf('|');
            if (pipe >= 0 && pipe + 1 < token.Length)
                token = token.Substring(pipe + 1);

            return token.Trim();
        }

        private static bool IsFaceMorphSlot(ushort slotId)
        {
            return (slotId >= 1 && slotId <= 9) || slotId == 35;
        }

        private static string BuildMorphGroupKey(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return "morph";

            string value = CanonicalBlendShapeToken(token);
            if (!IsOpposedDirectionalLeftRight(value))
                value = StripLeftRight(value);
            value = CollapseKnownUpperLowerFamily(value);
            value = value.Trim(' ', '_', '-', '.');
            return string.IsNullOrWhiteSpace(value) ? CanonicalBlendShapeToken(token) : value;
        }

        private static string StripLeftRight(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string[] suffixes =
            {
                "_left", "_right", " left", " right", ".left", ".right",
                "-left", "-right", "Left", "Right",
                "_l", "_r", ".l", ".r", "-l", "-r"
            };
            for (int i = 0; i < suffixes.Length; ++i)
            {
                if (value.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - suffixes[i].Length);
                    break;
                }
            }

            string[] prefixes =
            {
                "left_", "right_", "left-", "right-", "left.", "right.", "l_", "r_"
            };
            for (int i = 0; i < prefixes.Length; ++i)
            {
                if (value.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(prefixes[i].Length);
                    return value;
                }
            }

            if (value.StartsWith("Left", StringComparison.OrdinalIgnoreCase) &&
                value.Length > 4 && char.IsUpper(value[4]))
                value = value.Substring(4);
            else if (value.StartsWith("Right", StringComparison.OrdinalIgnoreCase) &&
                     value.Length > 5 && char.IsUpper(value[5]))
                value = value.Substring(5);

            return value;
        }

        private static bool IsOpposedDirectionalLeftRight(string token)
        {
            string compact = Compact(token);
            return compact == "jawleft" || compact == "jawright" ||
                   compact == "mouthleft" || compact == "mouthright" ||
                   compact == "tongueleft" || compact == "tongueright" ||
                   compact == "eyelookleft" || compact == "eyelookright" ||
                   compact == "eyeslookleft" || compact == "eyeslookright";
        }

        private static string CollapseKnownUpperLowerFamily(string value)
        {
            string compact = Compact(value);
            if (compact == "eyesquintupper" || compact == "eyesquintlower")
                return "eyeSquint";
            if (compact == "eyeblinkupper" || compact == "eyeblinklower")
                return "eyeBlink";
            if (compact == "eyewideupper" || compact == "eyewidelower")
                return "eyeWide";
            if (compact == "mouthrollupper" || compact == "mouthrolllower")
                return "mouthRoll";
            if (compact == "mouthrolloutupper" || compact == "mouthrolloutlower")
                return "mouthRollOut";
            if (compact == "mouthshrugupper" || compact == "mouthshruglower")
                return "mouthShrug";
            return value;
        }

        private static string Compact(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;
            var sb = new StringBuilder(token.Length);
            for (int i = 0; i < token.Length; ++i)
                if (char.IsLetterOrDigit(token[i]))
                    sb.Append(char.ToLowerInvariant(token[i]));
            return sb.ToString();
        }

        private static string BodyMorphDisplayName(string token)
        {
            if (string.Equals(token, "defaultBuff", StringComparison.OrdinalIgnoreCase)) return "Muscularity";
            if (string.Equals(token, "defaultHeavy", StringComparison.OrdinalIgnoreCase)) return "Body Weight";
            if (string.Equals(token, "defaultSkinny", StringComparison.OrdinalIgnoreCase)) return "Slimness";
            if (string.Equals(token, "masculineFeminine", StringComparison.OrdinalIgnoreCase)) return "Body Shape";
            return FriendlyWords(token);
        }

        private static Dictionary<ushort, string> BuildBaseDefaultRendererNames(GameObject editableBase)
        {
            var result = new Dictionary<ushort, string>();
            if (editableBase == null)
                return result;

            SkinnedMeshRenderer[] renderers = editableBase.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMesh == null)
                    continue;
                Match match = SlotRegex.Match(renderer.gameObject.name);
                if (!match.Success || !ushort.TryParse(match.Groups[1].Value, out ushort slotId) || slotId == 0)
                    continue;
                if (result.ContainsKey(slotId))
                    throw new InvalidOperationException(
                        $"Editable Character base has more than one SkinnedMeshRenderer for slot {slotId}. " +
                        "The clean base must contain exactly one renderer per required slot.");
                result[slotId] = renderer.gameObject.name;
            }
            return result;
        }

        private static HashSet<string> BuildTransformPathSet(Transform root)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (root == null)
                return result;
            Transform[] values = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < values.Length; ++i)
                if (values[i] != null)
                    result.Add(RelativePath(root, values[i]));
            return result;
        }

        private static void ValidateEditableSkeletonCompatibility(
            string optionName,
            string rootBonePath,
            string[] bonePaths,
            HashSet<string> editableTransformPaths)
        {
            if (editableTransformPaths == null || editableTransformPaths.Count == 0)
                return;
            if (!string.IsNullOrEmpty(rootBonePath) && !editableTransformPaths.Contains(rootBonePath))
                throw new InvalidOperationException(
                    $"Character visual option '{optionName}' requires missing root bone '{rootBonePath}' in Character.prefab.");

            string[] paths = bonePaths ?? Array.Empty<string>();
            for (int i = 0; i < paths.Length; ++i)
            {
                string path = paths[i] ?? string.Empty;
                if (!editableTransformPaths.Contains(path))
                    throw new InvalidOperationException(
                        $"Character visual option '{optionName}' requires missing bone '{path}' in Character.prefab.");
            }
        }

        private static string[] BuildBonePaths(Transform root, Transform[] bones)
        {
            if (bones == null || bones.Length == 0)
                return Array.Empty<string>();
            var result = new string[bones.Length];
            for (int i = 0; i < bones.Length; ++i)
                result[i] = bones[i] != null ? RelativePath(root, bones[i]) : string.Empty;
            return result;
        }

        private static string FriendlyOptionName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Option";
            Match match = SlotRegex.Match(raw);
            string head = match.Success ? raw.Substring(0, match.Index) : raw;
            if (head.StartsWith("SK_", StringComparison.Ordinal)) head = head.Substring(3);
            return FriendlyWords(head.Replace("HUMN", "Human").Replace("ELVN", "Elven").Replace("MDRN", "Modern").Replace("CIVL", "Civilian").Replace("APOC", "Apocalypse").Replace("OUTL", "Outlaw").Replace("FANT", "Fantasy").Replace("KNGT", "Knight").Replace("BASE", "Base"));
        }

        private static string FriendlyWords(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Option";
            string value = raw.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();
            var sb = new StringBuilder(value.Length + 8);
            char previous = '\0';
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(previous)) sb.Append(' ');
                if (c == ' ' && sb.Length > 0 && sb[sb.Length - 1] == ' ') continue;
                sb.Append(c);
                previous = c;
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "Option" : char.ToUpperInvariant(result[0]) + result.Substring(1);
        }

        private static string RelativePath(Transform root, Transform child)
        {
            if (root == child) return string.Empty;
            var names = new Stack<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static ushort Stable16(string value)
        {
            uint hash = Stable32(value);
            ushort folded = (ushort)((hash & 0xFFFFu) ^ (hash >> 16));
            return folded == 0 ? (ushort)1 : folded;
        }

        private static uint Stable32(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; ++i)
                {
                    char c = text[i];
                    hash ^= (byte)(c & 0xFF); hash *= 16777619u;
                    hash ^= (byte)(c >> 8); hash *= 16777619u;
                }
                return hash;
            }
        }
    }
}
#endif
