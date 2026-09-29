#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only icon baker for Character Creator mesh choices. Icons are generated once
    /// from the actual modular character and referenced by the client-only visual profile.
    /// </summary>
    public static class CharacterVisualThumbnailRenderer
    {
        private const int RenderVersion = 8;
        private const int Resolution = 256;
        private const string OutputRoot = "Assets/Game/Client/Generated/CharacterCreator/Thumbnails";

        public static void RebuildAll() => Generate(force: true);

        [MenuItem("MMO Tools/Characters/Maintenance/Refresh Character Thumbnails")]
        public static void GenerateMissing() => Generate(force: false);

        internal static void GenerateMissingForCatalog() => Generate(force: false);

        private static void Generate(bool force)
        {
            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(CharacterVisualCatalogBuilder.OutputAssetPath);
            GameObject editableBase = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterVisualCatalogBuilder.EditableBasePrefabPath);
            if (profile == null || editableBase == null)
            {
                Debug.LogWarning("[CharacterVisualCatalog] Build the Character Visual Catalog before generating thumbnails.");
                return;
            }

            EnsureAssetFolder(OutputRoot);
            int total = 0;
            IReadOnlyList<CharacterVisualSlotDefinition> slots = profile.Slots;
            for (int s = 0; s < slots.Count; ++s)
            {
                CharacterVisualSlotDefinition slot = slots[s];
                if (slot == null || !slot.mirrorPrimary || slot.equipmentDriven)
                    continue;
                CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int o = 0; o < options.Length; ++o)
                    if (options[o] != null && options[o].creatorSelectable && !options[o].equipmentDriven)
                        total++;
            }

            int current = 0;
            int generated = 0;
            try
            {
                for (int s = 0; s < slots.Count; ++s)
                {
                    CharacterVisualSlotDefinition slot = slots[s];
                    if (slot == null || !slot.mirrorPrimary || slot.equipmentDriven)
                        continue;
                    CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                    for (int o = 0; o < options.Length; ++o)
                    {
                        CharacterVisualOptionDefinition option = options[o];
                        if (option == null || !option.creatorSelectable || option.equipmentDriven)
                            continue;

                        current++;
                        string fingerprint = BuildFingerprint(profile, slot, option);
                        if (!force && option.thumbnail != null && option.thumbnailFingerprint == fingerprint)
                            continue;

                        EditorUtility.DisplayProgressBar(
                            "Character Creator Thumbnails",
                            slot.displayName + " / " + option.displayName,
                            total > 0 ? current / (float)total : 1f);

                        Texture2D texture = null;
                        try
                        {
                            texture = RenderOption(profile, editableBase, slot, option);
                            if (texture == null)
                                continue;
                            string folder = OutputRoot + "/" + Sanitize(slot.slotCode ?? slot.slotId.ToString());
                            EnsureAssetFolder(folder);
                            string assetPath = folder + "/" + option.optionId + ".png";
                            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                            string absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
                            File.WriteAllBytes(absolute, texture.EncodeToPNG());
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                            ConfigureSprite(assetPath);
                            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                            if (sprite == null)
                                continue;
                            option.thumbnail = sprite;
                            option.thumbnailFingerprint = fingerprint;
                            generated++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError("[CharacterVisualCatalog] Thumbnail failed for " + slot.displayName + "/" + option.displayName + "\n" + ex);
                        }
                        finally
                        {
                            if (texture != null)
                                UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (generated > 0)
            {
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
            }
            Debug.Log("[CharacterVisualCatalog] Character Creator thumbnails generated/updated: " + generated + ".");
        }

        private static Texture2D RenderOption(
            CharacterVisualProfile profile,
            GameObject editableBase,
            CharacterVisualSlotDefinition slot,
            CharacterVisualOptionDefinition option)
        {
            PreviewRenderUtility preview = new PreviewRenderUtility();
            RenderTexture readbackTarget = null;
            Material thumbnailMaterial = null;
            try
            {
                preview.camera.cameraType = CameraType.Preview;
                preview.camera.clearFlags = CameraClearFlags.Color;
                preview.camera.backgroundColor = new Color(0.08f, 0.12f, 0.15f, 1f);
                preview.camera.fieldOfView = 24f;
                preview.camera.nearClipPlane = 0.001f;
                preview.camera.farClipPlane = 30f;
                preview.camera.allowHDR = false;
                preview.camera.allowMSAA = true;

                if (preview.lights != null && preview.lights.Length >= 2)
                {
                    preview.lights[0].intensity = 2.4f;
                    preview.lights[0].color = new Color(1f, 0.97f, 0.93f, 1f);
                    preview.lights[0].shadows = LightShadows.None;
                    preview.lights[0].transform.rotation = Quaternion.Euler(30f, 150f, 0f);

                    preview.lights[1].intensity = 1.35f;
                    preview.lights[1].color = new Color(0.92f, 0.96f, 1f, 1f);
                    preview.lights[1].shadows = LightShadows.None;
                    preview.lights[1].transform.rotation = Quaternion.Euler(18f, -35f, 0f);
                }
                preview.ambientColor = new Color(0.72f, 0.72f, 0.72f, 1f);

                GameObject actor = preview.InstantiatePrefabInScene(editableBase);
                if (actor == null)
                    return null;

                actor.name = editableBase.name + "_ThumbnailPreview";
                actor.transform.position = Vector3.zero;
                actor.transform.rotation = Quaternion.identity;
                actor.transform.localScale = Vector3.one;

                Animator animator = actor.GetComponentInChildren<Animator>(true);
                if (animator != null)
                    animator.enabled = false;

                ModularCharacterAppearancePresenter presenter = actor.GetComponent<ModularCharacterAppearancePresenter>();
                if (presenter == null)
                    presenter = actor.AddComponent<ModularCharacterAppearancePresenter>();
                presenter.Configure(profile);

                CharacterAppearanceRecipe recipe = profile.CreateDefaultRecipe();
                SetMesh(recipe, slot.slotId, option.optionId);
                if (slot.mirrorSlotId != 0 &&
                    profile.TryGetMirrorOption(slot, option.optionId, out ushort mirrorRecipeOptionId))
                    SetMesh(recipe, slot.mirrorSlotId, mirrorRecipeOptionId);

                // Use a fixed readable preview palette so every thumbnail shows shape rather
                // than inheriting an arbitrary authored color. This remains local Editor-only
                // presentation data and does not affect the saved Character Creator recipe.
                SetColor(recipe, 1, 4); // warm light-medium skin
                SetColor(recipe, 2, 2); // dark brown hair/brows/facial hair
                SetColor(recipe, 3, 2); // green eyes
                SetColor(recipe, 4, 4); // warm accessory accent
                presenter.ApplyAppearance(recipe);

                // Creator thumbnails show only the mesh choice being selected (or its
                // authored mirrored pair). Do not include the whole head/body: that made
                // small options impossible to distinguish and introduced unrelated geometry
                // into the icon.
                presenter.SetOnlySlotsVisible(BuildThumbnailSlots(slot));

                thumbnailMaterial = CreateThumbnailMaterial();
                ApplyThumbnailMaterialOverride(actor, thumbnailMaterial);

                if (!TryGetThumbnailFrame(actor, out Vector3 aim, out float frameSize))
                    return null;

                float halfFov = preview.camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float distance = (frameSize * 0.5f) / Mathf.Tan(halfFov);
                float yaw = ThumbnailYaw(slot.slotId);
                Quaternion orbit = Quaternion.Euler(2f, yaw, 0f);
                preview.camera.transform.position = aim + orbit * (Vector3.forward * Mathf.Max(0.025f, distance));
                preview.camera.transform.LookAt(aim, Vector3.up);

                Rect rect = new Rect(0f, 0f, Resolution, Resolution);
                // Static preview is the asset-thumbnail path and avoids the IMGUI preview
                // footer/border artifacts that were appearing as a pale strip in prior icons.
                preview.BeginStaticPreview(rect);
                preview.Render(true, true);
                Texture2D rendered = preview.EndStaticPreview();
                if (rendered == null)
                    return null;

                Texture2D texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, false);
                RenderTexture previous = RenderTexture.active;
                try
                {
                    readbackTarget = RenderTexture.GetTemporary(
                        Resolution,
                        Resolution,
                        0,
                        RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.Default);
                    Graphics.Blit(rendered, readbackTarget);
                    RenderTexture.active = readbackTarget;
                    texture.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0, false);
                    texture.Apply(false, false);
                }
                finally
                {
                    RenderTexture.active = previous;
                }

                return texture;
            }
            finally
            {
                if (thumbnailMaterial != null)
                    UnityEngine.Object.DestroyImmediate(thumbnailMaterial);
                if (readbackTarget != null)
                    RenderTexture.ReleaseTemporary(readbackTarget);
                preview.Cleanup();
            }
        }

        private static HashSet<ushort> BuildThumbnailSlots(CharacterVisualSlotDefinition slot)
        {
            var visible = new HashSet<ushort>();
            if (slot == null)
                return visible;

            visible.Add(slot.slotId);
            if (slot.mirrorSlotId != 0)
                visible.Add(slot.mirrorSlotId);
            return visible;
        }

        private static Material CreateThumbnailMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                return null;

            var material = new Material(shader)
            {
                name = "CharacterCreatorThumbnail_Matte",
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color color = new Color(0.78f, 0.82f, 0.86f, 1f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            return material;
        }

        private static void ApplyThumbnailMaterialOverride(GameObject root, Material material)
        {
            if (root == null || material == null)
                return;

            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.sharedMesh == null)
                    continue;

                int count = Mathf.Max(1, renderer.sharedMesh.subMeshCount);
                var materials = new Material[count];
                for (int m = 0; m < count; ++m)
                    materials[m] = material;
                renderer.sharedMaterials = materials;
            }
        }

        private static void SetColor(CharacterAppearanceRecipe recipe, ushort channelId, byte paletteIndex)
        {
            if (recipe == null)
                return;
            var values = new List<CharacterColorSelection>(recipe.colors ?? Array.Empty<CharacterColorSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].channelId != channelId)
                    continue;
                values[i] = new CharacterColorSelection(channelId, CharacterColorEncoding.PaletteIndex, paletteIndex);
                recipe.colors = values.ToArray();
                return;
            }
            values.Add(new CharacterColorSelection(channelId, CharacterColorEncoding.PaletteIndex, paletteIndex));
            recipe.colors = values.ToArray();
        }

        private static float ThumbnailYaw(ushort slotId)
        {
            // Profile-specific view hints for the current Sidekick semantic slots. These are
            // presentation-only and do not enter Shared/network data. Slight angles make
            // depth-heavy pieces legible while front-facing features stay straight-on.
            switch (slotId)
            {
                case 2:  return -12f; // Hair
                case 7:  return -28f; // Ears (mirrored pair)
                case 9:  return -10f; // Facial hair
                case 35: return -24f; // Nose
                default: return 0f;
            }
        }

        private static bool TryGetThumbnailFrame(
            GameObject actor,
            out Vector3 aim,
            out float frameSize)
        {
            if (TryGetBounds(actor, out Bounds bounds))
            {
                aim = bounds.center;
                float width = Mathf.Max(0.025f, bounds.size.x);
                float height = Mathf.Max(0.025f, bounds.size.y);
                float depth = Mathf.Max(0.025f, bounds.size.z);
                frameSize = Mathf.Max(width, height, depth * 0.9f) * 1.28f;
                frameSize = Mathf.Max(frameSize, 0.10f);
                return true;
            }

            aim = Vector3.zero;
            frameSize = 0.35f;
            return false;
        }

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;
            Transform[] values = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < values.Length; ++i)
                if (values[i] != null && string.Equals(values[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return values[i];
            return null;
        }

        private static bool TryGetBounds(GameObject root, out Bounds bounds)
        {
            bounds = default(Bounds);
            bool initialized = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!initialized) { bounds = renderer.bounds; initialized = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return initialized;
        }

        private static void SetMesh(CharacterAppearanceRecipe recipe, ushort slotId, ushort optionId)
        {
            var values = new List<CharacterMeshSelection>(recipe.meshes ?? Array.Empty<CharacterMeshSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].slotId != slotId) continue;
                values[i] = new CharacterMeshSelection(slotId, optionId);
                recipe.meshes = values.ToArray();
                return;
            }
            values.Add(new CharacterMeshSelection(slotId, optionId));
            recipe.meshes = values.ToArray();
        }

        private static string BuildFingerprint(CharacterVisualProfile profile, CharacterVisualSlotDefinition slot, CharacterVisualOptionDefinition option) =>
            "v" + RenderVersion + "|" + profile.CatalogVersion + "|" + slot.slotId + "|" + option.optionId;

        private static void ConfigureSprite(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            string[] parts = assetFolder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") return;
            string current = "Assets";
            for (int i = 1; i < parts.Length; ++i)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }
    }
}
#endif
