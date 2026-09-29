#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterCreation;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Builds a production CharacterVisualProfile from a selected humanoid/model by reusing
    /// the canonical semantic channels already understood by CharacterCreatorShell.
    /// It intentionally does not import a second appearance/save system. Known high-fidelity
    /// blendshape aliases (including the MMOTests reference model) are adapted into the same
    /// CharacterAppearanceRecipe contract used by the Synty profile.
    /// </summary>
    public sealed class CharacterVisualCapabilityProfileBuilder : EditorWindow
    {
        private const string ProfilesFolder = "Assets/Game/Client/Resources/MMO/Characters/Profiles";
        private const string MasterPrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        private GameObject _model;
        private int _profileId = 2;
        private string _assetName = "CharacterVisualProfile_HighFidelity";
        private bool _makeCreatorDefault;

        private sealed class Alias
        {
            public string Semantic;
            public string Display;
            public CharacterCreatorCategory Category;
            public CharacterVisualRegionMask Region;
            public CharacterVisualMorphClassification Classification;
            public string[] BlendShapes;
        }

        private static readonly Alias[] KnownAliases =
        {
            new Alias { Semantic = "shoulderWidth", Display = "Shoulder Width", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_ShoulderWidth" } },
            new Alias { Semantic = "hipWidth", Display = "Hip Width", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_HipWidth" } },
            new Alias { Semantic = "waistSize", Display = "Waist Size", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_WaistSize" } },
            new Alias { Semantic = "upperArmSize", Display = "Upper Arms", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_UpperArmScale" } },
            new Alias { Semantic = "forearmSize", Display = "Forearms", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_LowerArmScale" } },
            new Alias { Semantic = "thighSize", Display = "Thighs", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_ThighScale" } },
            new Alias { Semantic = "calfSize", Display = "Calves", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.All, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_CalfScale" } },
            new Alias { Semantic = "neckSize", Display = "Neck Size", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "BodyCustomization_NeckScale" } },
            new Alias { Semantic = "jawWidth", Display = "Jaw Width", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_jaw_width" } },
            new Alias { Semantic = "jawHeight", Display = "Jaw Height", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_jaw_height" } },
            new Alias { Semantic = "browDepth", Display = "Brow Depth", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_brow_depth" } },
            new Alias { Semantic = "browHeight", Display = "Brow Height", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_brow_height" } },
            new Alias { Semantic = "chinWidth", Display = "Chin Width", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_chin_width" } },
            new Alias { Semantic = "chinSize", Display = "Chin Size", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_chin_size" } },
            new Alias { Semantic = "cheekboneSize", Display = "Cheekbone Size", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_cheekbone_size" } },
            new Alias { Semantic = "cheeksSize", Display = "Cheeks Size", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_cheeks_size" } },
            new Alias { Semantic = "eyesDepth", Display = "Eye Depth", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_eyes_depth" } },
            new Alias { Semantic = "eyesHeight", Display = "Eye Height", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_eyes_height" } },
            new Alias { Semantic = "eyesNarrow", Display = "Eye Width", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_eyes_narrow" } },
            new Alias { Semantic = "eyeballFlatten", Display = "Eye Shape", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_eyeball_flatten" } },
            new Alias { Semantic = "mouthDepth", Display = "Mouth Depth", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_mouth_depth" } },
            new Alias { Semantic = "mouthHeight", Display = "Mouth Height", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_mouth_height" } },
            new Alias { Semantic = "mouthSize", Display = "Mouth Size", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_mouth_size" } },
            new Alias { Semantic = "noseHeight", Display = "Nose Height", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_nose_height" } },
            new Alias { Semantic = "noseOut", Display = "Nose Depth", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_nose_out" } },
            new Alias { Semantic = "noseSize", Display = "Nose Size", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_nose_size" } },
            new Alias { Semantic = "noseWidth", Display = "Nose Width", Category = CharacterCreatorCategory.Face, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_nose_width" } },
            new Alias { Semantic = "neckFat", Display = "Neck Fullness", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.HeadHair, Classification = CharacterVisualMorphClassification.Structural, BlendShapes = new[] { "mod_neck_fat" } },
            new Alias { Semantic = "breastSize", Display = "Chest Size", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.UpperBody, Classification = CharacterVisualMorphClassification.Body, BlendShapes = new[] { "BodyCustomization_BreastSize" } },
            new Alias { Semantic = "breastShrink", Display = "Chest Shape", Category = CharacterCreatorCategory.Body, Region = CharacterVisualRegionMask.UpperBody, Classification = CharacterVisualMorphClassification.Body, BlendShapes = new[] { "mod_breast_shrink" } },
        };

        [MenuItem("MMO Tools/Characters/Authoring/Build Capability Profile From Model")]
        public static void OpenWindow()
        {
            GetWindow<CharacterVisualCapabilityProfileBuilder>(false, "Visual Capability Profile", true);
        }

        private void OnEnable()
        {
            if (Selection.activeObject is GameObject selected)
                _model = selected;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unified Character Creator — Model Capability Profile", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Builds a client-only visual profile for the selected model. Shared semantic controls reuse the same " +
                "CharacterAppearanceRecipe channels as Synty where possible. Unsupported controls simply do not appear. " +
                "Known high-fidelity/MMOTests blendshapes are adapted automatically. No network message is added.",
                MessageType.Info);

            _model = (GameObject)EditorGUILayout.ObjectField("Model / Prefab", _model, typeof(GameObject), false);
            _profileId = EditorGUILayout.IntField("Visual Profile ID", _profileId);
            _assetName = EditorGUILayout.TextField("Profile Asset Name", _assetName);
            _makeCreatorDefault = EditorGUILayout.ToggleLeft("Use this profile for NEW Character Creator previews", _makeCreatorDefault);

            using (new EditorGUI.DisabledScope(_model == null || _profileId <= 0 || _profileId > ushort.MaxValue))
            {
                if (GUILayout.Button("Build / Update Capability Profile", GUILayout.Height(34f)))
                    BuildProfile(_model, (ushort)_profileId, _assetName, _makeCreatorDefault);
            }
        }

        private static void BuildProfile(GameObject model, ushort profileId, string assetName, bool makeCreatorDefault)
        {
            if (model == null)
                throw new InvalidOperationException("Select a model or prefab first.");
            if (profileId == 0)
                throw new InvalidOperationException("Visual Profile ID 0 is reserved.");

            EnsureFolder(ProfilesFolder);
            string safeName = string.IsNullOrWhiteSpace(assetName) ? $"CharacterVisualProfile_{profileId}" : SanitizeFileName(assetName);
            string assetPath = $"{ProfilesFolder}/{safeName}.asset";
            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(assetPath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<CharacterVisualProfile>();
                AssetDatabase.CreateAsset(profile, assetPath);
            }

            CharacterVisualProfile canonical = CharacterVisualProfileRegistry.Default;
            Dictionary<string, CharacterVisualMorphDefinition> canonicalBySemantic = BuildCanonicalSemanticMap(canonical);
            HashSet<ushort> usedChannels = new HashSet<ushort>();
            if (canonical != null)
            {
                IReadOnlyList<CharacterVisualMorphDefinition> definitions = canonical.Morphs;
                for (int i = 0; i < definitions.Count; ++i)
                    if (definitions[i] != null && definitions[i].channelId != 0) usedChannels.Add(definitions[i].channelId);
            }

            Dictionary<string, List<string>> available = CollectBlendShapes(model);
            var morphs = new List<CharacterVisualMorphDefinition>();
            var emitted = new HashSet<ushort>();

            // First, reuse any canonical semantic whose existing blendshape names are present
            // on this model. This keeps shared facial/body channels portable without guessing.
            if (canonical != null)
            {
                IReadOnlyList<CharacterVisualMorphDefinition> definitions = canonical.Morphs;
                for (int i = 0; i < definitions.Count; ++i)
                {
                    CharacterVisualMorphDefinition source = definitions[i];
                    if (source == null || source.driver != CharacterVisualMorphDriver.BlendShape || !source.persistInAppearance)
                        continue;
                    string[] matches = FindMatches(available, source.semanticNames);
                    if (matches.Length == 0)
                        continue;
                    CharacterVisualMorphDefinition built = BuildBlendShapeDefinition(model, source.channelId, source.semanticName,
                        source.displayName, source.category, source.region, source.classification, matches, source.defaultValue);
                    if (emitted.Add(built.channelId))
                        morphs.Add(built);
                }
            }

            // Then adapt known alternate names used by higher fidelity character packages.
            for (int i = 0; i < KnownAliases.Length; ++i)
            {
                Alias alias = KnownAliases[i];
                string[] matches = FindMatches(available, alias.BlendShapes);
                if (matches.Length == 0)
                    continue;

                ushort channelId;
                byte defaultValue = 128;
                if (canonicalBySemantic.TryGetValue(alias.Semantic, out CharacterVisualMorphDefinition shared))
                {
                    channelId = shared.channelId;
                    defaultValue = shared.defaultValue;
                }
                else
                {
                    channelId = StableSemanticChannel(alias.Semantic, usedChannels);
                }
                if (!emitted.Add(channelId))
                    continue;

                morphs.Add(BuildBlendShapeDefinition(model, channelId, alias.Semantic, alias.Display,
                    alias.Category, alias.Region, alias.Classification, matches, defaultValue));
            }

            string sourcePath = AssetDatabase.GetAssetPath(model);
            string fingerprint = AssetDatabase.AssetPathToGUID(sourcePath) + ":" + GetModelSignature(model);
            profile.Configure(
                profileId,
                version: 1,
                fingerprint: fingerprint,
                source: model,
                editableBase: model,
                slotDefinitions: Array.Empty<CharacterVisualSlotDefinition>(),
                morphDefinitions: morphs.OrderBy(m => m.category).ThenBy(m => m.displayName, StringComparer.OrdinalIgnoreCase).ToArray(),
                colorDefinitions: Array.Empty<CharacterVisualColorDefinition>());

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();

            if (makeCreatorDefault)
                SetCreatorProfileOverride(profileId);

            Selection.activeObject = profile;
            Debug.Log($"[CharacterCreator] Built visual capability profile {profileId} '{profile.name}' with {morphs.Count} supported semantic controls from '{model.name}'. Bone deformation remains bounded by the runtime ±3% clamp when a profile uses bone drivers.", profile);
        }

        private static CharacterVisualMorphDefinition BuildBlendShapeDefinition(
            GameObject model,
            ushort channelId,
            string semantic,
            string display,
            CharacterCreatorCategory category,
            CharacterVisualRegionMask region,
            CharacterVisualMorphClassification classification,
            string[] blendShapes,
            byte fallbackDefault)
        {
            float min = 0f;
            float max = 100f;
            float firstDefault = 0f;
            bool foundDefault = false;
            SkinnedMeshRenderer[] renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < renderers.Length; ++r)
            {
                SkinnedMeshRenderer renderer = renderers[r];
                Mesh mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null) continue;
                for (int b = 0; b < blendShapes.Length; ++b)
                {
                    int index = FindBlendShapeIndex(mesh, blendShapes[b]);
                    if (index < 0) continue;
                    int frames = mesh.GetBlendShapeFrameCount(index);
                    for (int f = 0; f < frames; ++f)
                    {
                        float frame = mesh.GetBlendShapeFrameWeight(index, f);
                        min = Mathf.Min(min, frame);
                        max = Mathf.Max(max, frame);
                    }
                    if (!foundDefault)
                    {
                        firstDefault = renderer.GetBlendShapeWeight(index);
                        foundDefault = true;
                    }
                }
            }
            if (Mathf.Approximately(min, max)) max = min + 100f;
            byte packedDefault = foundDefault
                ? (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(min, max, firstDefault) * 255f), 0, 255)
                : fallbackDefault;

            return new CharacterVisualMorphDefinition
            {
                channelId = channelId,
                semanticName = semantic,
                semanticNames = blendShapes,
                displayName = display,
                category = category,
                region = region,
                classification = classification,
                driver = CharacterVisualMorphDriver.BlendShape,
                boneTargets = Array.Empty<CharacterVisualBoneTargetDefinition>(),
                visibleInCreator = true,
                persistInAppearance = true,
                minWeight = min,
                maxWeight = max,
                defaultValue = packedDefault,
            };
        }

        private static Dictionary<string, CharacterVisualMorphDefinition> BuildCanonicalSemanticMap(CharacterVisualProfile profile)
        {
            var result = new Dictionary<string, CharacterVisualMorphDefinition>(StringComparer.OrdinalIgnoreCase);
            if (profile == null) return result;
            IReadOnlyList<CharacterVisualMorphDefinition> definitions = profile.Morphs;
            for (int i = 0; i < definitions.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = definitions[i];
                if (definition != null && !string.IsNullOrWhiteSpace(definition.semanticName))
                    result[definition.semanticName] = definition;
            }
            return result;
        }

        private static Dictionary<string, List<string>> CollectBlendShapes(GameObject model)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            SkinnedMeshRenderer[] renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < renderers.Length; ++r)
            {
                Mesh mesh = renderers[r] != null ? renderers[r].sharedMesh : null;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; ++i)
                {
                    string actual = mesh.GetBlendShapeName(i);
                    if (string.IsNullOrWhiteSpace(actual)) continue;
                    string key = actual.Trim();
                    if (!result.TryGetValue(key, out List<string> values))
                    {
                        values = new List<string>();
                        result.Add(key, values);
                    }
                    if (!values.Contains(actual)) values.Add(actual);
                }
            }
            return result;
        }

        private static string[] FindMatches(Dictionary<string, List<string>> available, IEnumerable<string> candidates)
        {
            if (candidates == null) return Array.Empty<string>();
            var result = new List<string>();
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (available.TryGetValue(candidate, out List<string> names))
                {
                    for (int i = 0; i < names.Count; ++i)
                        if (!result.Contains(names[i])) result.Add(names[i]);
                }
            }
            return result.ToArray();
        }

        private static int FindBlendShapeIndex(Mesh mesh, string name)
        {
            if (mesh == null || string.IsNullOrWhiteSpace(name)) return -1;
            int exact = mesh.GetBlendShapeIndex(name);
            if (exact >= 0) return exact;
            for (int i = 0; i < mesh.blendShapeCount; ++i)
                if (string.Equals(mesh.GetBlendShapeName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static ushort StableSemanticChannel(string semantic, HashSet<ushort> used)
        {
            int salt = 0;
            while (true)
            {
                string value = "semantic:" + (semantic ?? string.Empty).ToLowerInvariant() + (salt == 0 ? string.Empty : "#" + salt);
                ushort candidate = Stable16(value);
                if (used.Add(candidate)) return candidate;
                salt++;
            }
        }

        private static ushort Stable16(string value)
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
                ushort folded = (ushort)((hash & 0xFFFFu) ^ (hash >> 16));
                return folded == 0 ? (ushort)1 : folded;
            }
        }

        private static string GetModelSignature(GameObject model)
        {
            SkinnedMeshRenderer[] renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int meshCount = 0;
            int blendCount = 0;
            for (int i = 0; i < renderers.Length; ++i)
            {
                Mesh mesh = renderers[i] != null ? renderers[i].sharedMesh : null;
                if (mesh == null) continue;
                meshCount++;
                blendCount += mesh.blendShapeCount;
            }
            return $"smr={meshCount};blend={blendCount}";
        }

        private static void SetCreatorProfileOverride(ushort profileId)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(MasterPrefabPath);
            try
            {
                CharacterCreatorShell creator = root.GetComponentInChildren<CharacterCreatorShell>(true);
                if (creator == null)
                {
                    throw new InvalidOperationException(
                        "Character Creator is missing from ClientUIRoot.prefab. Restore the canonical prefab; " +
                        "this reusable profile tool will not rebuild or repair frontend UI.");
                }

                SerializedObject creatorSo = new SerializedObject(creator);
                CharacterSelectPreviewController preview = creatorSo.FindProperty("preview")?.objectReferenceValue as CharacterSelectPreviewController;
                if (preview == null)
                    throw new InvalidOperationException("Character Creator preview is not bound.");

                SerializedObject previewSo = new SerializedObject(preview);
                SerializedProperty property = previewSo.FindProperty("creatorVisualProfileIdOverride");
                if (property == null)
                    throw new InvalidOperationException("Character Creator preview profile field was not found.");
                property.intValue = profileId;
                previewSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(preview);
                PrefabUtility.SaveAsPrefabAsset(root, MasterPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string SanitizeFileName(string value)
        {
            string result = value ?? string.Empty;
            foreach (char c in Path.GetInvalidFileNameChars())
                result = result.Replace(c, '_');
            return string.IsNullOrWhiteSpace(result) ? "CharacterVisualProfile" : result.Trim();
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; ++i)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
