#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// One-time authoring install for the separate Sidekick nude FBX.
    /// Reuses the existing two semantic body slots and equipment override path.
    /// No new component, network state, or runtime polling is required.
    /// </summary>
    public static class NudeBodySetup
    {
        private const string ProfilePath =
            "Assets/Game/Client/Resources/MMO/Characters/CharacterVisualProfile.asset";
        private const ushort TorsoOptionId = 65010;
        private const ushort HipOptionId = 65017;

        [MenuItem("MMO Tools/Characters/Authoring/Install Nude Torso + Hips (Test)")]
        public static void Install()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
            if (profile == null || profile.EditableBasePrefab == null)
            {
                Debug.LogError("[NudeBody] CharacterVisualProfile or editable base missing. Build the existing character visual catalog first.");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("SIDEKICK NUDE_WITH_SHAPEKEYS t:Model");
            string path = guids.Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("SIDEKICK NUDE_WITH_SHAPEKEYS.fbx", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[NudeBody] Cannot find SIDEKICK NUDE_WITH_SHAPEKEYS.fbx under Assets. Import the Git FBX into Unity Assets first.");
                return;
            }

            GameObject donor = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (donor == null)
            {
                Debug.LogError("[NudeBody] FBX model could not be loaded: " + path);
                return;
            }

            SkinnedMeshRenderer[] parts = donor.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null).ToArray();
            SkinnedMeshRenderer torso = parts.FirstOrDefault(r =>
                r.name.IndexOf("torso", StringComparison.OrdinalIgnoreCase) >= 0);
            SkinnedMeshRenderer hips = parts.FirstOrDefault(r =>
                r.name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                r.name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0);
            if (parts.Length != 2 || torso == null || hips == null || torso == hips)
            {
                Debug.LogError("[NudeBody] Expected exactly 2 skinned meshes, one named Torso and one named Hip/Pelvis. Found: " +
                    string.Join(", ", parts.Select(p => p.name + " (" + p.sharedMesh.name + ")")) +
                    ". Rename the FBX mesh objects if needed; no changes made.");
                return;
            }

            if (!profile.TryGetSlot(10, out CharacterVisualSlotDefinition torsoSlot) ||
                !profile.TryGetSlot(17, out CharacterVisualSlotDefinition hipSlot))
            {
                Debug.LogError("[NudeBody] Required body slots 10 (Torso) and 17 (Hips) are missing.");
                return;
            }

            GameObject basePrefab = profile.EditableBasePrefab;
            var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(Transform t, string relative)
            {
                if (!canonical.ContainsKey(t.name))
                    canonical.Add(t.name, relative);
                foreach (Transform child in t)
                    Visit(child, string.IsNullOrEmpty(relative) ? child.name : relative + "/" + child.name);
            }
            Visit(basePrefab.transform, string.Empty);

            CharacterVisualOptionDefinition MakeOption(SkinnedMeshRenderer renderer, ushort id, string label)
            {
                Transform[] bones = renderer.bones ?? Array.Empty<Transform>();
                var paths = new string[bones.Length];
                for (int i = 0; i < bones.Length; ++i)
                {
                    if (bones[i] == null || !canonical.TryGetValue(bones[i].name, out paths[i]))
                        throw new InvalidOperationException("Missing canonical bone for " + label + ": " +
                            (bones[i] != null ? bones[i].name : "<null>"));
                }
                string rootPath = string.Empty;
                if (renderer.rootBone != null &&
                    !canonical.TryGetValue(renderer.rootBone.name, out rootPath))
                    throw new InvalidOperationException("Missing root bone for " + label + ": " + renderer.rootBone.name);

                return new CharacterVisualOptionDefinition
                {
                    optionId = id,
                    displayName = label,
                    rendererPath = path + "/" + renderer.name,
                    mesh = renderer.sharedMesh,
                    materials = renderer.sharedMaterials,
                    bonePaths = paths,
                    rootBonePath = rootPath,
                    localPosition = renderer.transform.localPosition,
                    localRotation = renderer.transform.localRotation,
                    localScale = renderer.transform.localScale,
                    localBounds = renderer.localBounds,
                    creatorSelectable = false,
                    equipmentDriven = false,
                    usageReviewed = true,
                    playerEligible = true,
                    populationEligible = false
                };
            }

            try
            {
                CharacterVisualOptionDefinition torsoOption = MakeOption(torso, TorsoOptionId, "Nude Torso");
                CharacterVisualOptionDefinition hipOption = MakeOption(hips, HipOptionId, "Nude Hips");

                Undo.RecordObject(profile, "Install nude torso and hips test options");
                AddOrReplace(torsoSlot, torsoOption);
                AddOrReplace(hipSlot, hipOption);
                torsoSlot.defaultOptionId = TorsoOptionId;
                hipSlot.defaultOptionId = HipOptionId;
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
                Debug.Log("[NudeBody] Installed nude Torso (slot 10) and Hips (slot 17) into the existing profile. " +
                    "Enter Play Mode, remove shirt/pants independently, and confirm both changes. " +
                    "Existing character recipes selecting Human Base are redirected by the presenter.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[NudeBody] Installation aborted: " + ex.Message);
            }
        }

        private static void AddOrReplace(CharacterVisualSlotDefinition slot, CharacterVisualOptionDefinition option)
        {
            var items = new List<CharacterVisualOptionDefinition>(
                slot.options ?? Array.Empty<CharacterVisualOptionDefinition>());
            int index = items.FindIndex(x => x != null && x.optionId == option.optionId);
            if (index >= 0)
                items[index] = option;
            else
                items.Add(option);
            slot.options = items.ToArray();
        }
    }
}
#endif
