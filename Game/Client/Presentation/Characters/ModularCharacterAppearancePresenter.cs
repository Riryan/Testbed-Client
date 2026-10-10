using System;
using System.Collections.Generic;
using Game.Shared.Characters;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only editable modular-character adapter used by Character Creator/preview.
    /// The large 699-renderer source prefab is never instantiated here. The clean editable
    /// base provides one skeleton plus one renderer per required slot; optional creator
    /// slots are created lazily and swap catalog mesh/material/bone data in place.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ModularCharacterAppearancePresenter : MonoBehaviour, ICharacterAppearancePresenter
    {
        // Hard safety limit for structural bone-driven appearance changes. This is applied
        // against the captured rest pose after all logical bone controls are composed, so
        // overlapping controls can never compound beyond +/-3%. Blendshapes are unaffected.
        private const float MaxBoneMorphDelta = 0.03f;

        private CharacterVisualProfile _profile;
        private readonly Dictionary<ushort, SkinnedMeshRenderer> _rendererBySlot =
            new Dictionary<ushort, SkinnedMeshRenderer>();
        private readonly Dictionary<ushort, List<SkinnedMeshRenderer>> _renderersBySlot =
            new Dictionary<ushort, List<SkinnedMeshRenderer>>();
        private readonly List<SkinnedMeshRenderer> _allSkinnedRenderers = new List<SkinnedMeshRenderer>();
        private readonly Dictionary<string, Transform> _transformByPath =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<string, TransformRestState> _restTransformByPath =
            new Dictionary<string, TransformRestState>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, byte> _morphValues = new Dictionary<ushort, byte>();
        private readonly Dictionary<string, Vector3> _boneScaleMultiplierByPath =
            new Dictionary<string, Vector3>(StringComparer.Ordinal);
        private readonly Dictionary<string, Vector3> _bonePositionMultiplierByPath =
            new Dictionary<string, Vector3>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, CharacterColorSelection> _colorValues =
            new Dictionary<ushort, CharacterColorSelection>();
        private readonly Dictionary<ushort, CharacterVisualOptionDefinition> _activeOptionBySlot =
            new Dictionary<ushort, CharacterVisualOptionDefinition>();
        private MaterialPropertyBlock _propertyBlock;
        // Captured from the canonical editable humanoid before modular FBX options replace
        // its renderer materials. Sidekick donor FBXs frequently serialize generic URP/Lit
        // placeholders; authored palette-cell clothing must render through the already-loaded
        // Character Master material for _ColorMap overrides to have any effect.
        private Material _canonicalPaletteMaterial;
        private static readonly int ColorMapShaderId = Shader.PropertyToID("_ColorMap");
        private CharacterAppearanceRecipe _lastAppearance;
        private bool _hasBoneMorphDefinitions;

        private struct TransformRestState
        {
            public Vector3 localPosition;
            public Vector3 localScale;
        }

        public ushort VisualProfileId => _profile != null ? _profile.VisualProfileId : (ushort)0;

        private void LateUpdate()
        {
            // Humanoid animation can rewrite local bone positions/scales after the appearance
            // recipe is applied. Re-apply only the small set of authored bone proportions in
            // LateUpdate so the customization remains stable while idle/walk/run clips play.
            if (_hasBoneMorphDefinitions && _profile != null && _lastAppearance != null)
                ApplyBoneMorphs(_profile.Morphs);
        }

        public void Configure(CharacterVisualProfile profile)
        {
            if (profile == null)
                return;
            if (_profile == profile && _rendererBySlot.Count > 0)
                return;

            _profile = profile;
            RebuildRuntimeLookup();
        }

        public void ApplyAppearance(CharacterAppearanceRecipe appearance)
        {
            if (_profile == null)
            {
                if (!CharacterVisualProfileRegistry.TryResolve(
                        appearance != null ? appearance.visualProfileId : (ushort)0,
                        out CharacterVisualProfile profile))
                    return;
                Configure(profile);
            }

            CharacterAppearanceRecipe effective = CharacterVisualProfileRegistry.NormalizeForPresentation(appearance);
            _lastAppearance = effective.Clone();
            EnforceRendererOwnership();
            ApplyMeshes(effective);
            ApplyMorphs(effective);
            ApplyColors(effective);
        }


        /// <summary>
        /// Applies client-resolved equipment mesh overrides on top of the persisted body
        /// appearance. Calling this with an empty array restores the body recipe and clears
        /// equipment-only slots. This is presentation-only and is invoked only when the rare
        /// authoritative equipment appearance state changes.
        /// </summary>
        public void ApplyEquipmentMeshOverrides(CharacterMeshSelection[] overrides)
        {
            if (_profile == null)
                return;

            EnforceRendererOwnership();

            // Always restore the persisted base before applying the current equipment layer.
            // This makes unequip deterministic and avoids retaining meshes from a prior item.
            if (_lastAppearance != null)
                ApplyMeshes(_lastAppearance);

            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || !slot.equipmentDriven ||
                    !_rendererBySlot.TryGetValue(slot.slotId, out SkinnedMeshRenderer renderer))
                    continue;
                _activeOptionBySlot.Remove(slot.slotId);
                ClearRenderer(renderer);
            }

            CharacterMeshSelection[] values = overrides ?? Array.Empty<CharacterMeshSelection>();
            for (int i = 0; i < values.Length; ++i)
            {
                CharacterMeshSelection selection = values[i];
                if (selection.optionId == 0 ||
                    !_rendererBySlot.TryGetValue(selection.slotId, out SkinnedMeshRenderer renderer) ||
                    renderer == null ||
                    !_profile.TryGetOption(selection.slotId, selection.optionId, out CharacterVisualOptionDefinition option) ||
                    option == null || !option.AllowsPlayerUse || option.mesh == null)
                    continue;

                ApplyOption(selection.slotId, renderer, option);
            }

            // Equipment meshes use the same humanoid skeleton/morph and palette contract.
            if (_lastAppearance != null)
            {
                ApplyMorphs(_lastAppearance);
                ApplyColors(_lastAppearance);
            }
        }

        /// <summary>
        /// Editor thumbnail helper: leave only the requested semantic slots visible after
        /// applying the recipe. This keeps icons focused on the actual option instead of
        /// photographing the whole character.
        /// </summary>
        public void SetOnlySlotsVisible(ISet<ushort> visibleSlots)
        {
            EnforceRendererOwnership();
            foreach (KeyValuePair<ushort, List<SkinnedMeshRenderer>> pair in _renderersBySlot)
            {
                bool visible = visibleSlots != null && visibleSlots.Contains(pair.Key);
                SkinnedMeshRenderer canonical = _rendererBySlot.TryGetValue(pair.Key, out SkinnedMeshRenderer value)
                    ? value
                    : null;
                List<SkinnedMeshRenderer> renderers = pair.Value;
                for (int i = 0; i < renderers.Count; ++i)
                {
                    SkinnedMeshRenderer renderer = renderers[i];
                    if (renderer == null)
                        continue;
                    renderer.enabled = visible && renderer == canonical && renderer.sharedMesh != null;
                }
            }
        }

        private void RebuildRuntimeLookup()
        {
            _rendererBySlot.Clear();
            _renderersBySlot.Clear();
            _allSkinnedRenderers.Clear();
            _activeOptionBySlot.Clear();
            _transformByPath.Clear();
            _restTransformByPath.Clear();
            BuildTransformLookup(transform, string.Empty);
            CaptureRestTransforms();
            _hasBoneMorphDefinitions = HasBoneMorphDefinitions();

            SkinnedMeshRenderer[] renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            // Resolve once while the editable base still owns its authored Character Master
            // material. Later ApplyOption calls swap in modular FBX meshes/material arrays.
            _canonicalPaletteMaterial = ResolveCanonicalPaletteMaterial(renderers);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                _allSkinnedRenderers.Add(renderer);
                if (!TryExtractSlotId(renderer.gameObject.name, out ushort slotId))
                    continue;

                if (!_renderersBySlot.TryGetValue(slotId, out List<SkinnedMeshRenderer> list))
                {
                    list = new List<SkinnedMeshRenderer>();
                    _renderersBySlot.Add(slotId, list);
                }
                list.Add(renderer);

                // Prefer the first renderer that already represents a valid enabled base
                // slot. Any additional renderer for the same semantic slot is non-canonical
                // and will be disabled/cleared below.
                if (!_rendererBySlot.TryGetValue(slotId, out SkinnedMeshRenderer canonical) ||
                    (canonical != null && (!canonical.enabled || canonical.sharedMesh == null) &&
                     renderer.enabled && renderer.sharedMesh != null))
                    _rendererBySlot[slotId] = renderer;
            }

            if (_profile != null)
            {
                IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
                for (int i = 0; i < slots.Count; ++i)
                {
                    CharacterVisualSlotDefinition slot = slots[i];
                    if (slot == null || _rendererBySlot.ContainsKey(slot.slotId))
                        continue;

                    GameObject go = new GameObject($"__VisualSlot_{slot.slotId:00}_{slot.slotCode}");
                    go.transform.SetParent(transform, false);
                    SkinnedMeshRenderer renderer = go.AddComponent<SkinnedMeshRenderer>();
                    renderer.enabled = false;
                    renderer.updateWhenOffscreen = false;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    _rendererBySlot.Add(slot.slotId, renderer);
                    _renderersBySlot.Add(slot.slotId, new List<SkinnedMeshRenderer> { renderer });
                    _allSkinnedRenderers.Add(renderer);
                }
            }

            EnforceRendererOwnership();
        }

        private void EnforceRendererOwnership()
        {
            foreach (KeyValuePair<ushort, List<SkinnedMeshRenderer>> pair in _renderersBySlot)
            {
                if (!_rendererBySlot.TryGetValue(pair.Key, out SkinnedMeshRenderer canonical) || canonical == null)
                    continue;

                List<SkinnedMeshRenderer> values = pair.Value;
                for (int i = 0; i < values.Count; ++i)
                {
                    SkinnedMeshRenderer renderer = values[i];
                    if (renderer == null || renderer == canonical)
                        continue;

                    // A semantic slot has exactly one renderer owner. Clearing the extras is
                    // intentional: mesh selection and blendshape changes must never reveal an
                    // unchanged default body/face part underneath the current option.
                    ClearRenderer(renderer);
                }
            }
        }

        private void BuildTransformLookup(Transform current, string path)
        {
            if (current == null)
                return;

            _transformByPath[path] = current;
            for (int i = 0; i < current.childCount; ++i)
            {
                Transform child = current.GetChild(i);
                string childPath = string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name;
                BuildTransformLookup(child, childPath);
            }
        }

        private void CaptureRestTransforms()
        {
            foreach (KeyValuePair<string, Transform> pair in _transformByPath)
            {
                Transform value = pair.Value;
                if (value == null)
                    continue;
                _restTransformByPath[pair.Key] = new TransformRestState
                {
                    localPosition = value.localPosition,
                    localScale = value.localScale,
                };
            }
        }

        private bool HasBoneMorphDefinitions()
        {
            if (_profile == null)
                return false;
            IReadOnlyList<CharacterVisualMorphDefinition> morphs = _profile.Morphs;
            for (int i = 0; i < morphs.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = morphs[i];
                if (definition != null && definition.persistInAppearance &&
                    definition.driver == CharacterVisualMorphDriver.Bone &&
                    definition.boneTargets != null && definition.boneTargets.Length > 0)
                    return true;
            }
            return false;
        }

        private void ApplyMeshes(CharacterAppearanceRecipe appearance)
        {
            CharacterMeshSelection[] selections = appearance.meshes ?? Array.Empty<CharacterMeshSelection>();
            var selectedBySlot = new Dictionary<ushort, ushort>(selections.Length);
            for (int i = 0; i < selections.Length; ++i)
                selectedBySlot[selections[i].slotId] = selections[i].optionId;

            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || slot.equipmentDriven || !_rendererBySlot.TryGetValue(slot.slotId, out SkinnedMeshRenderer renderer) || renderer == null)
                    continue;

                ushort selected;
                if (!selectedBySlot.TryGetValue(slot.slotId, out selected))
                    selected = slot.defaultOptionId;

                if (selected == 0)
                {
                    _activeOptionBySlot.Remove(slot.slotId);
                    ClearRenderer(renderer);
                    continue;
                }

                // Test nude donor: existing persisted recipes may explicitly select the
                // original Human Base even after the catalog default changes. When the
                // authored nude option is installed, replace only that base selection.
                // Equipment still overrides the region normally after this base pass.
                if ((slot.slotId == 10 || slot.slotId == 17) &&
                    _profile.TryGetOption(slot.slotId, selected, out CharacterVisualOptionDefinition baseOption) &&
                    baseOption != null &&
                    !string.IsNullOrEmpty(baseOption.rendererPath) &&
                    baseOption.rendererPath.IndexOf("_HUMN_BASE_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ushort nudeId = slot.slotId == 10 ? (ushort)65010 : (ushort)65017;
                    if (_profile.TryGetOption(slot.slotId, nudeId, out CharacterVisualOptionDefinition nudeOption) &&
                        nudeOption != null && nudeOption.mesh != null)
                        selected = nudeId;
                }

                if (!_profile.TryGetOption(slot.slotId, selected, out CharacterVisualOptionDefinition option) ||
                    option == null || option.mesh == null)
                {
                    _activeOptionBySlot.Remove(slot.slotId);
                    ClearRenderer(renderer);
                    continue;
                }

                ApplyOption(slot.slotId, renderer, option);
            }
        }

        private void ApplyOption(
            ushort slotId,
            SkinnedMeshRenderer renderer,
            CharacterVisualOptionDefinition option)
        {
            _activeOptionBySlot[slotId] = option;
            renderer.transform.localPosition = option.localPosition;
            renderer.transform.localRotation = option.localRotation;
            renderer.transform.localScale = option.localScale == Vector3.zero ? Vector3.one : option.localScale;
            renderer.sharedMesh = option.mesh;
            renderer.sharedMaterials = ResolveOptionMaterials(option);

            // Swapped SkinnedMeshRenderers must receive bounds that belong to the selected
            // mesh. Leaving a lazily-created renderer at zero bounds, or retaining bounds
            // from a previous option, causes hair/parts to disappear at specific preview
            // rotation angles due to frustum culling. Prefer the authored source renderer
            // bounds when the catalog has them, with mesh bounds as a safe migration path
            // for existing catalog assets created before localBounds was recorded.
            Bounds optionBounds = option.localBounds;
            if (optionBounds.size.sqrMagnitude <= 0.000001f)
                optionBounds = option.mesh.bounds;
            renderer.localBounds = optionBounds;

            string[] paths = option.bonePaths ?? Array.Empty<string>();
            if (paths.Length > 0)
            {
                var bones = new Transform[paths.Length];
                for (int i = 0; i < paths.Length; ++i)
                    _transformByPath.TryGetValue(paths[i] ?? string.Empty, out bones[i]);
                renderer.bones = bones;
            }

            if (!string.IsNullOrEmpty(option.rootBonePath) &&
                _transformByPath.TryGetValue(option.rootBonePath, out Transform rootBone))
                renderer.rootBone = rootBone;

            renderer.enabled = true;
        }

        private Material[] ResolveOptionMaterials(CharacterVisualOptionDefinition option)
        {
            Material[] source = option != null && option.materials != null
                ? option.materials
                : Array.Empty<Material>();

            // Do not rewrite ordinary authored/special materials. Only options with explicit
            // palette-cell metadata need the Character Master palette contract, and only known
            // generic importer fallback materials (Lit/Default/Standard) are normalized. This
            // mirrors the proven uMMORPG humanoid material normalization behavior.
            if (_canonicalPaletteMaterial == null || option == null || option.paletteCells == null ||
                option.paletteCells.Length == 0 || source.Length == 0)
                return source;

            Material[] resolved = null;
            for (int i = 0; i < source.Length; ++i)
            {
                Material material = source[i];
                if (!IsGenericFallbackMaterial(material))
                    continue;

                if (resolved == null)
                    resolved = (Material[])source.Clone();
                resolved[i] = _canonicalPaletteMaterial;
            }

            return resolved ?? source;
        }

        private static Material ResolveCanonicalPaletteMaterial(SkinnedMeshRenderer[] renderers)
        {
            if (renderers == null)
                return null;

            Material best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                Material[] materials = renderer != null ? renderer.sharedMaterials : null;
                if (materials == null)
                    continue;

                for (int m = 0; m < materials.Length; ++m)
                {
                    Material material = materials[m];
                    if (!HasUsableColorMap(material))
                        continue;

                    int score = 1000;
                    string shaderName = material.shader != null ? material.shader.name ?? string.Empty : string.Empty;
                    if (string.Equals(shaderName, "Custom/URP/Character Master", StringComparison.OrdinalIgnoreCase) ||
                        shaderName.EndsWith("CharacterMaster_URP", StringComparison.OrdinalIgnoreCase))
                        score += 1000;
                    if (material.HasProperty("_SkinColor"))
                        score += 100;
                    if (material.HasProperty("_SkinColorAmount"))
                        score += 100;

                    if (score > bestScore)
                    {
                        best = material;
                        bestScore = score;
                    }
                }
            }

            return best;
        }

        private static bool HasUsableColorMap(Material material) =>
            material != null && material.HasProperty(ColorMapShaderId) &&
            material.GetTexture(ColorMapShaderId) is Texture2D;

        private static bool IsGenericFallbackMaterial(Material material)
        {
            if (material == null)
                return false;

            string materialName = material.name ?? string.Empty;
            const string instanceSuffix = " (Instance)";
            if (materialName.EndsWith(instanceSuffix, StringComparison.OrdinalIgnoreCase))
                materialName = materialName.Substring(0, materialName.Length - instanceSuffix.Length);
            materialName = materialName.Trim();

            return string.Equals(materialName, "Lit", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(materialName, "Default-Material", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(materialName, "Default Material", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(materialName, "Standard", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(materialName, "URP Lit", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(materialName, "HDRP Lit", StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
                return;
            renderer.enabled = false;
            renderer.sharedMesh = null;
            renderer.sharedMaterials = Array.Empty<Material>();
        }

        private void ApplyMorphs(CharacterAppearanceRecipe appearance)
        {
            _morphValues.Clear();
            CharacterMorphSelection[] selections = appearance.morphs ?? Array.Empty<CharacterMorphSelection>();
            for (int i = 0; i < selections.Length; ++i)
                _morphValues[selections[i].channelId] = selections[i].value;

            IReadOnlyList<CharacterVisualMorphDefinition> morphs = _profile.Morphs;
            // Blendshape-capability profiles are not required to use the Synty slot naming
            // convention. Apply semantic blendshape channels across every active renderer in
            // the model; only meshes that actually contain a mapped shape are touched.
            for (int rendererIndex = 0; rendererIndex < _allSkinnedRenderers.Count; ++rendererIndex)
            {
                SkinnedMeshRenderer renderer = _allSkinnedRenderers[rendererIndex];
                if (renderer == null || !renderer.enabled || renderer.sharedMesh == null)
                    continue;

                Mesh mesh = renderer.sharedMesh;
                for (int i = 0; i < morphs.Count; ++i)
                {
                    CharacterVisualMorphDefinition definition = morphs[i];
                    if (definition == null || !definition.persistInAppearance ||
                        definition.driver != CharacterVisualMorphDriver.BlendShape)
                        continue;

                    byte packed = _morphValues.TryGetValue(definition.channelId, out byte value)
                        ? value
                        : definition.defaultValue;
                    float weight = Mathf.Lerp(definition.minWeight, definition.maxWeight, packed / 255f);
                    string[] names = definition.semanticNames != null && definition.semanticNames.Length > 0
                        ? definition.semanticNames
                        : new[] { definition.semanticName };
                    for (int n = 0; n < names.Length; ++n)
                    {
                        if (string.IsNullOrWhiteSpace(names[n]))
                            continue;
                        int blendShapeIndex = mesh.GetBlendShapeIndex(names[n]);
                        if (blendShapeIndex >= 0)
                            renderer.SetBlendShapeWeight(blendShapeIndex, weight);
                    }
                }
            }

            ApplyBoneMorphs(morphs);
        }

        private void ApplyBoneMorphs(IReadOnlyList<CharacterVisualMorphDefinition> morphs)
        {
            if (!_hasBoneMorphDefinitions || morphs == null)
                return;

            // Several logical controls can intentionally affect the same bone (for example
            // Neck Length + Neck Size, or Hip Width + Thigh Size). Compose their multipliers
            // first, then write each transform exactly once. This keeps controls independent
            // instead of whichever slider happens to be last in the profile overwriting another.
            _boneScaleMultiplierByPath.Clear();
            _bonePositionMultiplierByPath.Clear();

            for (int i = 0; i < morphs.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = morphs[i];
                if (definition == null || !definition.persistInAppearance ||
                    definition.driver != CharacterVisualMorphDriver.Bone)
                    continue;

                byte packed = _morphValues.TryGetValue(definition.channelId, out byte value)
                    ? value
                    : definition.defaultValue;
                float amount = packed / 255f;
                CharacterVisualBoneTargetDefinition[] targets =
                    definition.boneTargets ?? Array.Empty<CharacterVisualBoneTargetDefinition>();

                for (int t = 0; t < targets.Length; ++t)
                {
                    CharacterVisualBoneTargetDefinition target = targets[t];
                    if (target == null || string.IsNullOrWhiteSpace(target.transformPath) ||
                        !_transformByPath.ContainsKey(target.transformPath) ||
                        !_restTransformByPath.ContainsKey(target.transformPath))
                        continue;

                    Vector3 localScaleMultiplier = Vector3.Lerp(
                        target.minLocalScaleMultiplier, target.maxLocalScaleMultiplier, amount);
                    Vector3 localPositionMultiplier = Vector3.Lerp(
                        target.minLocalPositionMultiplier, target.maxLocalPositionMultiplier, amount);

                    Vector3 accumulatedScale = _boneScaleMultiplierByPath.TryGetValue(
                        target.transformPath, out Vector3 existingScale)
                        ? existingScale
                        : Vector3.one;
                    Vector3 accumulatedPosition = _bonePositionMultiplierByPath.TryGetValue(
                        target.transformPath, out Vector3 existingPosition)
                        ? existingPosition
                        : Vector3.one;

                    _boneScaleMultiplierByPath[target.transformPath] =
                        Vector3.Scale(accumulatedScale, localScaleMultiplier);
                    _bonePositionMultiplierByPath[target.transformPath] =
                        Vector3.Scale(accumulatedPosition, localPositionMultiplier);
                }
            }

            foreach (KeyValuePair<string, Vector3> pair in _boneScaleMultiplierByPath)
            {
                string path = pair.Key;
                if (!_transformByPath.TryGetValue(path, out Transform target) || target == null ||
                    !_restTransformByPath.TryGetValue(path, out TransformRestState rest))
                    continue;

                Vector3 positionMultiplier = _bonePositionMultiplierByPath.TryGetValue(
                    path, out Vector3 position)
                    ? position
                    : Vector3.one;

                // Clamp the final composed result rather than only each individual control.
                // Several semantic controls can intentionally touch the same bone, and their
                // multipliers are multiplicative. The final clamp guarantees the actual pose
                // stays within +/-3% of the authored rest transform.
                Vector3 scaleMultiplier = ClampBoneMorphMultiplier(pair.Value);
                positionMultiplier = ClampBoneMorphMultiplier(positionMultiplier);
                target.localScale = Vector3.Scale(rest.localScale, scaleMultiplier);
                target.localPosition = Vector3.Scale(rest.localPosition, positionMultiplier);
            }
        }

        private static Vector3 ClampBoneMorphMultiplier(Vector3 value)
        {
            float min = 1f - MaxBoneMorphDelta;
            float max = 1f + MaxBoneMorphDelta;
            return new Vector3(
                Mathf.Clamp(value.x, min, max),
                Mathf.Clamp(value.y, min, max),
                Mathf.Clamp(value.z, min, max));
        }

        private void ApplyColors(CharacterAppearanceRecipe appearance)
        {
            _colorValues.Clear();
            CharacterColorSelection[] selections = appearance.colors ?? Array.Empty<CharacterColorSelection>();
            for (int i = 0; i < selections.Length; ++i)
                _colorValues[selections[i].channelId] = selections[i];

            byte skin = ResolvePaletteId(1);
            byte hair = ResolvePaletteId(2);
            byte eyes = ResolvePaletteId(3);
            byte hairAccessory = ResolvePaletteId(4);
            // Keep the original character-wide dye pair as a compatibility fallback for
            // existing Player recipes. Regional values override these per slot when present.
            byte legacyClothingPrimary = ResolvePaletteId(SidekickCharacterPaletteUtility.ClothingPrimaryColorChannelId);
            byte legacyClothingSecondary = ResolvePaletteId(SidekickCharacterPaletteUtility.ClothingSecondaryColorChannelId);

            if (_propertyBlock == null)
                _propertyBlock = new MaterialPropertyBlock();

            foreach (KeyValuePair<ushort, SkinnedMeshRenderer> pair in _rendererBySlot)
            {
                ushort slotId = pair.Key;
                SkinnedMeshRenderer renderer = pair.Value;
                if (renderer == null || !renderer.enabled || renderer.sharedMesh == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                int materialCount = materials != null ? materials.Length : 0;
                if (materialCount <= 0)
                    materialCount = 1;

                for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
                {
                    _propertyBlock.Clear();
                    renderer.GetPropertyBlock(_propertyBlock, materialIndex);
                    if (!IsHairBrowOrEye(slotId))
                        SidekickCharacterPaletteUtility.ApplySkin(_propertyBlock, skin);

                    Material material = materials != null && materialIndex < materials.Length ? materials[materialIndex] : null;
                    SidekickCharacterPaletteUtility.ApplyPaletteMap(
                        _propertyBlock,
                        material,
                        slotId,
                        hair,
                        hairAccessory,
                        eyes);

                    if (_activeOptionBySlot.TryGetValue(slotId, out CharacterVisualOptionDefinition activeOption) &&
                        activeOption != null)
                    {
                        SidekickCharacterPaletteUtility.ResolveClothingColorChannels(
                            slotId,
                            out ushort primaryChannelId,
                            out ushort secondaryChannelId);

                        byte clothingPrimary = ResolvePaletteId(primaryChannelId);
                        byte clothingSecondary = ResolvePaletteId(secondaryChannelId);

                        // Existing Player appearances may only contain the legacy/global pair.
                        // Preserve that behavior until Player dye UI/persistence intentionally
                        // starts authoring the regional channel ids. Population already emits
                        // the regional values locally.
                        if (clothingPrimary == 0)
                            clothingPrimary = legacyClothingPrimary;
                        if (clothingSecondary == 0)
                            clothingSecondary = legacyClothingSecondary;

                        SidekickCharacterPaletteUtility.ApplyClothingPaletteMap(
                            _propertyBlock,
                            material,
                            activeOption.paletteCells,
                            clothingPrimary,
                            clothingSecondary);
                    }

                    renderer.SetPropertyBlock(_propertyBlock, materialIndex);
                }
            }
        }

        private byte ResolvePaletteId(ushort channelId)
        {
            if (!_colorValues.TryGetValue(channelId, out CharacterColorSelection value) ||
                value.encoding != CharacterColorEncoding.PaletteIndex || value.value == 0)
                return 0;

            return (byte)Mathf.Clamp((int)value.value, 1, byte.MaxValue);
        }

        private static bool IsHairBrowOrEye(ushort slotId) =>
            slotId == 2 || slotId == 3 || slotId == 4 || slotId == 5 || slotId == 6 || slotId == 9;

        private static bool TryExtractSlotId(string name, out ushort slotId)
        {
            slotId = 0;
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i + 3 < name.Length; ++i)
            {
                if (name[i] != '_' || !char.IsDigit(name[i + 1]) || !char.IsDigit(name[i + 2]))
                    continue;
                if (!ushort.TryParse(name.Substring(i + 1, 2), out ushort parsed) || parsed == 0)
                    continue;
                slotId = parsed;
                return true;
            }
            return false;
        }

        public static uint PackRgba(Color color)
        {
            Color32 c = color;
            return (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
        }

        public static Color UnpackRgba(uint packed)
        {
            return new Color32(
                (byte)(packed & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 24) & 0xFF));
        }
    }
}
