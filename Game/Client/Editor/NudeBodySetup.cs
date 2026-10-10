#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    // Temporary authoring-only mesh override. Existing body option IDs, equipment
    // resolution, saved recipes, network messages, and runtime components stay intact.
    public static class NudeBodySetup
    {
        private const string ProfilePath =
            "Assets/Game/Client/Resources/MMO/Characters/CharacterVisualProfile.asset";

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Apply")]
        public static void Apply()
        {
            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
            if (profile == null || profile.EditableBasePrefab == null ||
                !profile.TryGetSlot(10, out CharacterVisualSlotDefinition torsoSlot) ||
                !profile.TryGetSlot(17, out CharacterVisualSlotDefinition hipSlot))
            {
                Debug.LogError("[NudeBody] Build the standard Character Visual Catalog first (body slots 10/17 required).");
                return;
            }

            string path = AssetDatabase.FindAssets("SIDEKICK NUDE_WITH_SHAPEKEYS t:Model")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("SIDEKICK NUDE_WITH_SHAPEKEYS.fbx", StringComparison.OrdinalIgnoreCase));
            GameObject source = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (source == null)
            {
                Debug.LogError("[NudeBody] FBX not found under Assets. Import the Git FBX into the Unity project first.");
                return;
            }

            SkinnedMeshRenderer[] donors = source.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null).ToArray();
            SkinnedMeshRenderer torso = donors.FirstOrDefault(r => Contains(r, "10TORS") || Contains(r, "torso"));
            SkinnedMeshRenderer hips = donors.FirstOrDefault(r => Contains(r, "17HIPS") || Contains(r, "hip") || Contains(r, "pelvis"));
            if (donors.Length != 2 || torso == null || hips == null || torso == hips)
            {
                Debug.LogError("[NudeBody] Expected two skinned parts for slots 10TORS and 17HIPS; found: " +
                    string.Join(", ", donors.Select(r => r.name + "/" + r.sharedMesh.name)));
                return;
            }

            // Resolve donor bone names onto the existing canonical player skeleton.
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectBones(profile.EditableBasePrefab.transform, "", paths);
            if (!TryDescribe(torso, paths, out string torsoRoot, out string[] torsoBones) ||
                !TryDescribe(hips, paths, out string hipRoot, out string[] hipBones))
            {
                Debug.LogError("[NudeBody] Bone mismatch. No catalog changes made.");
                return;
            }

            var torsoOptions = EligibleOptions(torsoSlot);
            var hipOptions = EligibleOptions(hipSlot);
            if (torsoOptions.Count == 0 || hipOptions.Count == 0)
            {
                Debug.LogError("[NudeBody] Cannot find Human Base torso/hip options in the current catalog.");
                return;
            }

            Undo.RecordObject(profile, "Test nude body parts");
            foreach (CharacterVisualOptionDefinition option in torsoOptions)
                Replace(option, torso, torsoRoot, torsoBones);
            foreach (CharacterVisualOptionDefinition option in hipOptions)
                Replace(option, hips, hipRoot, hipBones);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            Debug.Log("[NudeBody] Applied to existing Human Base torso and hip options. " +
                "No recipe IDs changed. Test no shirt / no pants independently. " +
                "Use Nude Body Test/Restore Original to revert.");
        }

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Restore Original")]
        public static void RestoreOriginal()
        {
            // Existing canonical builder regenerates mesh, skeleton and material references
            // from the original modular source while retaining reviewed option policy.
            CharacterVisualCatalogBuilder.RebuildFromMenu();
            Debug.Log("[NudeBody] Standard catalog rebuild requested. Inspect the Console for errors.");
        }

        private static bool Contains(SkinnedMeshRenderer renderer, string word) =>
            renderer.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
            renderer.sharedMesh.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        private static List<CharacterVisualOptionDefinition> EligibleOptions(CharacterVisualSlotDefinition slot) =>
            (slot.options ?? Array.Empty<CharacterVisualOptionDefinition>())
            .Where(o => o != null && o.mesh != null && !o.equipmentDriven &&
                !string.IsNullOrEmpty(o.rendererPath) &&
                o.rendererPath.IndexOf("SK_HUMN_BASE_", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        private static void CollectBones(Transform current, string path, Dictionary<string, string> result)
        {
            if (!result.ContainsKey(current.name)) result.Add(current.name, path);
            foreach (Transform child in current)
                CollectBones(child, string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name, result);
        }

        private static bool TryDescribe(SkinnedMeshRenderer donor, Dictionary<string, string> paths,
            out string root, out string[] bones)
        {
            root = "";
            Transform[] sourceBones = donor.bones ?? Array.Empty<Transform>();
            bones = new string[sourceBones.Length];
            if (donor.rootBone != null && !paths.TryGetValue(donor.rootBone.name, out root))
                return false;
            for (int i = 0; i < sourceBones.Length; ++i)
                if (sourceBones[i] == null || !paths.TryGetValue(sourceBones[i].name, out bones[i]))
                    return false;
            return true;
        }

        private static void Replace(CharacterVisualOptionDefinition option, SkinnedMeshRenderer donor,
            string root, string[] bones)
        {
            option.mesh = donor.sharedMesh;
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
