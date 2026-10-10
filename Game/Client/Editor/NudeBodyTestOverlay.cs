#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    // Test-only installation into the existing visual profile. No runtime/network subsystem.
    public static class NudeBodyTestOverlay
    {
        private const string ProfilePath = "Assets/Game/Client/Resources/MMO/Characters/CharacterVisualProfile.asset";
        private const ushort TorsoId = 65010;
        private const ushort HipsId = 65017;
        private const string DefaultsKey = "NudeBodyTestOverlay_OriginalDefaults";

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Install")]
        public static void Install()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
            if (profile == null || profile.EditableBasePrefab == null ||
                !profile.TryGetSlot(10, out var torsoSlot) || !profile.TryGetSlot(17, out var hipsSlot))
            {
                Debug.LogError("[NudeBodyTest] Missing existing profile, editable base, or body slots 10/17.");
                return;
            }
            var path = AssetDatabase.FindAssets("SIDEKICK NUDE_WITH_SHAPEKEYS t:Model")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("SIDEKICK NUDE_WITH_SHAPEKEYS.fbx", StringComparison.OrdinalIgnoreCase));
            var source = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (source == null)
            {
                Debug.LogError("[NudeBodyTest] Import SIDEKICK NUDE_WITH_SHAPEKEYS.fbx into Assets first.");
                return;
            }
            var renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null).ToArray();
            var torso = renderers.FirstOrDefault(r => r.name.IndexOf("10TORS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      r.name.IndexOf("torso", StringComparison.OrdinalIgnoreCase) >= 0);
            var hips = renderers.FirstOrDefault(r => r.name.IndexOf("17HIPS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      r.name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      r.name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0);
            if (renderers.Length != 2 || torso == null || hips == null || torso == hips)
            {
                Debug.LogError("[NudeBodyTest] Expected two skinned renderers with 10TORS and 17HIPS slot names. Found: " +
                    string.Join(", ", renderers.Select(r => r.name)));
                return;
            }
            var bonePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            void Walk(Transform transform, string relative)
            {
                if (!bonePaths.ContainsKey(transform.name)) bonePaths.Add(transform.name, relative);
                foreach (Transform child in transform)
                    Walk(child, string.IsNullOrEmpty(relative) ? child.name : relative + "/" + child.name);
            }
            Walk(profile.EditableBasePrefab.transform, "");
            CharacterVisualOptionDefinition Option(SkinnedMeshRenderer renderer, ushort id, string name)
            {
                var bones = renderer.bones ?? Array.Empty<Transform>();
                var paths = new string[bones.Length];
                // Some donor FBXs export attachment/end bones absent from the Player rig.
                // They may be mapped to an ancestor ONLY if no vertices are weighted to them.
                // Weighted missing bones are rejected: remapping those would deform the mesh.
                var weighted = new HashSet<int>();
                var influences = renderer.sharedMesh.boneWeights;
                for (int v = 0; v < influences.Length; v++)
                {
                    BoneWeight weight = influences[v];
                    if (weight.weight0 > 0f) weighted.Add(weight.boneIndex0);
                    if (weight.weight1 > 0f) weighted.Add(weight.boneIndex1);
                    if (weight.weight2 > 0f) weighted.Add(weight.boneIndex2);
                    if (weight.weight3 > 0f) weighted.Add(weight.boneIndex3);
                }
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform donorBone = bones[i];
                    if (donorBone != null && bonePaths.TryGetValue(donorBone.name, out paths[i]))
                        continue;
                    if (weighted.Contains(i))
                        throw new InvalidOperationException("Weighted donor bone missing from Player rig for " + name +
                            ": " + (donorBone == null ? "null" : donorBone.name) +
                            ". The mesh needs a compatible rig or weight transfer; not safe to substitute.");
                    Transform ancestor = donorBone == null ? null : donorBone.parent;
                    while (ancestor != null && !bonePaths.TryGetValue(ancestor.name, out paths[i]))
                        ancestor = ancestor.parent;
                    if (ancestor == null)
                        throw new InvalidOperationException("Unweighted donor bone has no matching Player ancestor for " + name +
                            ": " + (donorBone == null ? "null" : donorBone.name));
                    Debug.Log("[NudeBodyTest] Unweighted donor helper bone " + donorBone.name +
                        " mapped to existing " + ancestor.name + " (no vertex deformation).");
                }
                string root = "";
                if (renderer.rootBone != null && !bonePaths.TryGetValue(renderer.rootBone.name, out root))
                    throw new InvalidOperationException("Missing root bone: " + renderer.rootBone.name);
                return new CharacterVisualOptionDefinition
                {
                    optionId = id,
                    displayName = name,
                    rendererPath = path + "/" + renderer.name,
                    mesh = renderer.sharedMesh,
                    materials = renderer.sharedMaterials,
                    rootBonePath = root,
                    bonePaths = paths,
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
            CharacterVisualOptionDefinition torsoOption, hipsOption;
            try { torsoOption = Option(torso, TorsoId, "Nude Torso"); hipsOption = Option(hips, HipsId, "Nude Hips"); }
            catch (Exception ex) { Debug.LogError("[NudeBodyTest] No changes made: " + ex.Message); return; }
            if (!EditorPrefs.HasKey(DefaultsKey))
                EditorPrefs.SetString(DefaultsKey, torsoSlot.defaultOptionId + "," + hipsSlot.defaultOptionId);
            Undo.RecordObject(profile, "Install nude body test options");
            SetOption(torsoSlot, torsoOption);
            SetOption(hipsSlot, hipsOption);
            torsoSlot.defaultOptionId = TorsoId;
            hipsSlot.defaultOptionId = HipsId;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            Debug.Log("[NudeBodyTest] Installed. Test undressing torso and hips independently in Play Mode.");
        }

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Remove")]
        public static void Remove()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
            if (profile == null || !profile.TryGetSlot(10, out var torso) || !profile.TryGetSlot(17, out var hips))
            {
                Debug.LogError("[NudeBodyTest] Profile/body slots missing; cannot remove test options.");
                return;
            }
            Undo.RecordObject(profile, "Remove nude body test options");
            torso.options = (torso.options ?? Array.Empty<CharacterVisualOptionDefinition>())
                .Where(x => x != null && x.optionId != TorsoId).ToArray();
            hips.options = (hips.options ?? Array.Empty<CharacterVisualOptionDefinition>())
                .Where(x => x != null && x.optionId != HipsId).ToArray();
            string[] defaults = EditorPrefs.GetString(DefaultsKey, "").Split(',');
            if (defaults.Length == 2 && ushort.TryParse(defaults[0], out var oldTorso) && ushort.TryParse(defaults[1], out var oldHips))
            {
                torso.defaultOptionId = oldTorso;
                hips.defaultOptionId = oldHips;
            }
            else
            {
                Debug.LogWarning("[NudeBodyTest] Saved original defaults unavailable. Restore CharacterVisualProfile.asset from your backup or version control.");
            }
            EditorPrefs.DeleteKey(DefaultsKey);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            Debug.Log("[NudeBodyTest] Removed test options. Restore the original presenter source from Git if needed.");
        }

        private static void SetOption(CharacterVisualSlotDefinition slot, CharacterVisualOptionDefinition option)
        {
            var values = new List<CharacterVisualOptionDefinition>(slot.options ?? Array.Empty<CharacterVisualOptionDefinition>());
            var index = values.FindIndex(x => x != null && x.optionId == option.optionId);
            if (index >= 0) values[index] = option; else values.Add(option);
            slot.options = values.ToArray();
        }
    }
}
#endif
