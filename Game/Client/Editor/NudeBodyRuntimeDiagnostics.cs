#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    // TEMPORARY READ-ONLY DIAGNOSTIC. Delete this file to remove it completely.
    internal static class NudeBodyRuntimeDiagnostics
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Prefix = "[NudeBodyDiag]";

        [MenuItem("MMO Tools/Characters/Authoring/Nude Body Test/Inspect Live Meshes")]
        private static void Inspect()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning(Prefix + " Enter Play Mode and enter the world before running this inspection.");
                return;
            }

            var sb = new StringBuilder(8192);
            var all = Resources.FindObjectsOfTypeAll<ModularCharacterAppearancePresenter>();
            int count = 0;
            foreach (var presenter in all)
            {
                if (presenter == null || !presenter.gameObject.scene.IsValid() ||
                    !presenter.gameObject.scene.isLoaded || !presenter.gameObject.activeInHierarchy)
                    continue;

                count++;
                if (count > 12) { sb.AppendLine("More than 12 presenters found; output limited."); break; }
                sb.AppendLine("=== Presenter " + count + ": " + FullPath(presenter.transform) + " ===");
                var profile = ReadField<CharacterVisualProfile>(presenter, "_profile");
                var recipe = ReadField<CharacterAppearanceRecipe>(presenter, "_lastAppearance");
                var current = ReadField<Dictionary<ushort, SkinnedMeshRenderer>>(presenter, "_rendererBySlot");
                var options = ReadField<Dictionary<ushort, CharacterVisualOptionDefinition>>(presenter, "_activeOptionBySlot");
                sb.AppendLine("VisualProfileId=" + presenter.VisualProfileId + "; profile=" + (profile != null ? profile.name : "NULL") +
                    "; recipe=" + (recipe != null ? "present" : "NULL"));
                foreach (ushort slotId in new ushort[] { 10, 17 })
                {
                    ushort selected = 0;
                    bool explicitChoice = false;
                    if (recipe != null && recipe.meshes != null)
                        foreach (var choice in recipe.meshes)
                            if (choice.slotId == slotId) { selected = choice.optionId; explicitChoice = true; break; }
                    CharacterVisualSlotDefinition slot = null;
                    if (profile != null) profile.TryGetSlot(slotId, out slot);
                    if (!explicitChoice && slot != null) selected = slot.defaultOptionId;
                    CharacterVisualOptionDefinition catalog = null;
                    if (profile != null && selected != 0) profile.TryGetOption(slotId, selected, out catalog);
                    SkinnedMeshRenderer renderer = null;
                    if (current != null) current.TryGetValue(slotId, out renderer);
                    CharacterVisualOptionDefinition applied = null;
                    if (options != null) options.TryGetValue(slotId, out applied);
                    sb.AppendLine("Slot " + slotId + " (" + (slotId == 10 ? "TORSO" : "HIPS") + ")" +
                        " defaultId=" + (slot != null ? slot.defaultOptionId.ToString() : "missing") +
                        " selectedId=" + selected + " explicitSaved=" + explicitChoice);
                    sb.AppendLine("  Catalog selected=" + MeshName(catalog != null ? catalog.mesh : null) +
                        " | Applied option=" + MeshName(applied != null ? applied.mesh : null));
                    sb.AppendLine("  LIVE renderer=" + (renderer != null ? FullPath(renderer.transform) : "NULL") +
                        " enabled=" + (renderer != null && renderer.enabled) +
                        " mesh=" + MeshName(renderer != null ? renderer.sharedMesh : null));
                }

                var renderers = presenter.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                int shown = 0;
                sb.AppendLine("  Enabled renderers (including equipment / extra body geometry):");
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled || renderer.sharedMesh == null) continue;
                    if (++shown > 65) { sb.AppendLine("  ...truncated after 65 enabled renderers"); break; }
                    sb.AppendLine("   " + renderer.name + " -> " + MeshName(renderer.sharedMesh));
                }
                sb.AppendLine("  Enabled skinned renderer count=" + shown);
            }
            if (count == 0) sb.AppendLine("No active ModularCharacterAppearancePresenter found in loaded scenes.");
            Debug.Log(Prefix + " LIVE INSPECTION\n" + sb);
        }

        private static T ReadField<T>(object obj, string name) where T : class
        {
            var field = obj.GetType().GetField(name, InstancePrivate);
            return field != null ? field.GetValue(obj) as T : null;
        }

        private static string MeshName(Mesh mesh) => mesh != null ? mesh.name : "NULL";

        private static string FullPath(Transform t)
        {
            if (t == null) return "NULL";
            var names = new Stack<string>();
            while (t != null) { names.Push(t.name); t = t.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
