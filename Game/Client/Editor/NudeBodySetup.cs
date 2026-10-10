#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    // Authoring-only replacement of the EXISTING Human Base body mesh options.
    // No new IDs, Player runtime components, saved-recipe migration, or network changes.
    public static class NudeBodySetup
    {
        private const string ProfilePath =
            "Assets/Game/Client/Resources/MMO/Characters/CharacterVisualProfile.asset";
        private const string FbxFileName = "SIDEKICK NUDE_WITH_SHAPEKEYS.fbx";

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Apply")]
        public static void Apply()
        {
            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
            if (profile == null || profile.EditableBasePrefab == null ||
                !profile.TryGetSlot(10, out CharacterVisualSlotDefinition torsoSlot) ||
                !profile.TryGetSlot(17, out CharacterVisualSlotDefinition hipSlot))
            {
                Debug.LogError("[NudeBody] Missing visual profile, editable Player skeleton, or torso/hip slots. No changes made.");
                return;
            }

            string[] sourcePaths = AssetDatabase.FindAssets("SIDEKICK NUDE_WITH_SHAPEKEYS t:Model")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(FbxFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (sourcePaths.Length != 1)
            {
                Debug.LogError("[NudeBody] Expected one imported " + FbxFileName + "; found " + sourcePaths.Length + ". No changes made.");
                return;
            }

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePaths[0]);
            if (source == null)
            {
                Debug.LogError("[NudeBody] FBX could not be loaded. No changes made.");
                return;
            }

            SkinnedMeshRenderer[] donors = source.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null).ToArray();
            SkinnedMeshRenderer torso = donors.FirstOrDefault(r => Matches(r, "10TORS"));
            SkinnedMeshRenderer hips = donors.FirstOrDefault(r => Matches(r, "17HIPS"));
            if (donors.Length != 2 || torso == null || hips == null || torso == hips)
            {
                Debug.LogError("[NudeBody] Expected exactly two SkinnedMeshRenderers named 10TORS and 17HIPS. Found: " +
                    string.Join(", ", donors.Select(r => r.name)) + ". No changes made.");
                return;
            }

            Dictionary<string, string> canonicalBones = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectBones(profile.EditableBasePrefab.transform, "", canonicalBones);
            // Evaluate both mappings separately: short-circuiting would leave hipsError
            // unassigned if the torso fails first.
            bool torsoValid = TryMapBones(torso, canonicalBones,
                out string torsoRoot, out string[] torsoBones, out string torsoError);
            bool hipsValid = TryMapBones(hips, canonicalBones,
                out string hipsRoot, out string[] hipsBones, out string hipsError);
            if (!torsoValid || !hipsValid)
            {
                Debug.LogError("[NudeBody] Incompatible donor skeleton: " +
                    (!torsoValid ? torsoError : hipsError) + ". No changes made.");
                return;
            }

            List<CharacterVisualOptionDefinition> torsoOptions = EligibleHumanBase(torsoSlot);
            List<CharacterVisualOptionDefinition> hipOptions = EligibleHumanBase(hipSlot);
            if (torsoOptions.Count == 0 || hipOptions.Count == 0)
            {
                Debug.LogError("[NudeBody] Existing Human Base body options not found. No changes made.");
                return;
            }

            Undo.RecordObject(profile, "Replace Human Base Torso/Hips with Nude Meshes");
            foreach (CharacterVisualOptionDefinition option in torsoOptions)
                ReplaceMesh(option, torso, torsoRoot, torsoBones);
            foreach (CharacterVisualOptionDefinition option in hipOptions)
                ReplaceMesh(option, hips, hipsRoot, hipsBones);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            Debug.Log("[NudeBody] Applied: replaced " + torsoOptions.Count + " existing Human Base torso option(s) and " +
                hipOptions.Count + " hip option(s). Original slot/option IDs and clothing behavior retained. " +
                "Test with a newly reconstructed player (re-enter world or restart Play Mode).");
        }

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Restore Original")]
        public static void RestoreOriginal()
        {
            // The canonical builder restores references from the original modular catalog.
            CharacterVisualCatalogBuilder.RebuildFromMenu();
            Debug.Log("[NudeBody] Original catalog rebuild requested; verify the Console and generated profile.");
        }

        private static bool Matches(SkinnedMeshRenderer renderer, string token) =>
            renderer.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
            renderer.sharedMesh.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static List<CharacterVisualOptionDefinition> EligibleHumanBase(CharacterVisualSlotDefinition slot) =>
            (slot.options ?? Array.Empty<CharacterVisualOptionDefinition>())
            .Where(option => option != null && option.mesh != null && !option.equipmentDriven &&
                !string.IsNullOrEmpty(option.rendererPath) &&
                option.rendererPath.IndexOf("SK_HUMN_BASE_", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        private static void CollectBones(Transform current, string path, Dictionary<string, string> result)
        {
            // Names are canonical in the editable skeleton. Fail if an ambiguous name is later required.
            if (!result.ContainsKey(current.name))
                result.Add(current.name, path);
            foreach (Transform child in current)
                CollectBones(child, string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name, result);
        }

        private static bool TryMapBones(SkinnedMeshRenderer donor, Dictionary<string, string> canonical,
            out string root, out string[] mapped, out string error)
        {
            root = "";
            error = null;
            Transform[] sourceBones = donor.bones ?? Array.Empty<Transform>();
            mapped = new string[sourceBones.Length];
            if (sourceBones.Length == 0 || donor.sharedMesh == null ||
                donor.sharedMesh.bindposes.Length != sourceBones.Length)
            {
                error = donor.name + ": invalid skinning bone/bindpose count";
                return false;
            }

            if (donor.rootBone != null && !TryFindCanonicalParent(donor.rootBone, canonical, out root))
            {
                error = donor.name + ": no matching root/ancestor for " + donor.rootBone.name;
                return false;
            }

            // A missing donor helper may only be rebound if NO vertex has nonzero weight on it.
            bool[] weighted = new bool[sourceBones.Length];
            BoneWeight[] weights = donor.sharedMesh.boneWeights;
            for (int i = 0; i < weights.Length; ++i)
            {
                BoneWeight w = weights[i];
                Mark(w.boneIndex0, w.weight0);
                Mark(w.boneIndex1, w.weight1);
                Mark(w.boneIndex2, w.weight2);
                Mark(w.boneIndex3, w.weight3);
            }
            void Mark(int index, float weight)
            {
                if (weight > 0.000001f && index >= 0 && index < weighted.Length)
                    weighted[index] = true;
            }

            for (int i = 0; i < sourceBones.Length; ++i)
            {
                Transform donorBone = sourceBones[i];
                if (donorBone == null)
                {
                    error = donor.name + ": null donor bone at index " + i;
                    return false;
                }
                if (canonical.TryGetValue(donorBone.name, out mapped[i]))
                    continue;
                if (weighted[i])
                {
                    error = donor.name + ": missing WEIGHTED bone " + donorBone.name +
                            " (requires FBX skinning repair, not a name substitution)";
                    return false;
                }
                if (!TryFindCanonicalParent(donorBone.parent, canonical, out mapped[i]))
                {
                    error = donor.name + ": missing unweighted helper " + donorBone.name + " without a compatible ancestor";
                    return false;
                }
                Debug.Log("[NudeBody] Ignoring unweighted helper " + donorBone.name + " -> " + mapped[i]);
            }
            return true;
        }

        private static bool TryFindCanonicalParent(Transform bone, Dictionary<string, string> canonical, out string path)
        {
            for (Transform current = bone; current != null; current = current.parent)
                if (canonical.TryGetValue(current.name, out path))
                    return true;
            path = null;
            return false;
        }

        private static void ReplaceMesh(CharacterVisualOptionDefinition option, SkinnedMeshRenderer donor,
            string root, string[] bones)
        {
            option.mesh = donor.sharedMesh;
            // Keep the original Human Base skin/palette material; FBX import may use generic placeholders.
            if (option.materials == null || option.materials.Length == 0 || option.materials.All(m => m == null))
                option.materials = donor.sharedMaterials;
            option.rootBonePath = root;
            option.bonePaths = bones;
            option.localPosition = donor.transform.localPosition;
            option.localRotation = donor.transform.localRotation;
            option.localScale = donor.transform.localScale;
            option.localBounds = donor.localBounds;
        }
    }
}
#endif
