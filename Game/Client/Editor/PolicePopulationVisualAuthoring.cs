#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Client.Editor
{
    /// <summary>
    /// Builds the Police Population client visual through the existing CharacterVisualProfile
    /// registry. Runtime Population remains a normal PlayerEntity presentation consumer; this
    /// tool only imports the authored Cop FBX, creates one presentation prefab, and registers
    /// the existing visualProfileId carried by PlayerEntityAppearance.
    /// </summary>
    internal static class PolicePopulationVisualAuthoring
    {
        private const string ModelPath = "Assets/Game/Client/Models/Population/Cop.fbx";
        private const string GeneratedFolder = "Assets/Game/Client/Generated/Population";
        private const string PrefabPath = GeneratedFolder + "/PolicePopulationVisual.prefab";
        private const string ProfilesFolder = "Assets/Game/Client/Resources/MMO/Characters/Profiles";
        private const string ProfilePath = ProfilesFolder + "/PolicePopulationVisualProfile.asset";
        private const string MaleMeshName = "Character_Male_Police";
        private const string FemaleMeshName = "Character_Female_Police";

        [MenuItem("MMO Tools/Population/Install or Repair Police Visual")]
        private static void InstallOrRepair()
        {
            try
            {
                EnsureFolder(GeneratedFolder);
                EnsureFolder(ProfilesFolder);

                ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
                if (importer == null)
                    throw new InvalidOperationException($"Police model is missing or is not an FBX ModelImporter: {ModelPath}");

                bool reimport = false;
                if (importer.animationType != ModelImporterAnimationType.Human)
                {
                    importer.animationType = ModelImporterAnimationType.Human;
                    reimport = true;
                }
                if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                {
                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    reimport = true;
                }
                if (reimport)
                    importer.SaveAndReimport();

                GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                if (modelAsset == null)
                    throw new InvalidOperationException($"Police FBX did not import as a GameObject: {ModelPath}");

                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault(value => value != null && value.isValid && value.isHuman);
                if (avatar == null)
                {
                    throw new InvalidOperationException(
                        "Cop.fbx did not produce a valid Humanoid Avatar. Open the FBX Rig inspector, " +
                        "confirm Animation Type = Humanoid and fix any failed bone mapping, then run this tool again.");
                }

                AnimatorController controller = FindCanonicalPlayerHumanoidController();
                if (controller == null)
                {
                    throw new InvalidOperationException(
                        "Could not find the canonical PlayerHumanoid AnimatorController. " +
                        "The Police visual deliberately reuses that controller rather than creating a Police-only controller.");
                }

                GameObject prefabAsset = BuildPresentationPrefab(modelAsset, avatar, controller);
                if (prefabAsset == null)
                    throw new InvalidOperationException("Failed to create the Police Population presentation prefab.");

                CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(ProfilePath);
                if (profile == null)
                {
                    profile = ScriptableObject.CreateInstance<CharacterVisualProfile>();
                    profile.name = "PolicePopulationVisualProfile";
                    AssetDatabase.CreateAsset(profile, ProfilePath);
                }

                string fingerprint = string.Join(":",
                    "population-police-cop-v1",
                    AssetDatabase.AssetPathToGUID(ModelPath),
                    AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller)),
                    MaleMeshName);

                profile.Configure(
                    CharacterVisualProfileIds.PopulationPolice,
                    StableVersion(fingerprint),
                    fingerprint,
                    modelAsset,
                    prefabAsset,
                    Array.Empty<CharacterVisualSlotDefinition>(),
                    Array.Empty<CharacterVisualMorphDefinition>(),
                    Array.Empty<CharacterVisualColorDefinition>());

                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                CharacterVisualProfileRegistry.ResetForTestsOrReload();

                Validate(profile, prefabAsset, controller);
                Selection.activeObject = profile;
                Debug.Log(
                    $"[PolicePopulationVisual] OK. visualProfileId={CharacterVisualProfileIds.PopulationPolice}, " +
                    $"model='{ModelPath}', prefab='{PrefabPath}', profile='{ProfilePath}'.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
        }

        private static GameObject BuildPresentationPrefab(
            GameObject modelAsset,
            Avatar avatar,
            RuntimeAnimatorController controller)
        {
            GameObject instance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (instance == null)
                instance = Object.Instantiate(modelAsset);
            if (instance == null)
                return null;

            try
            {
                instance.name = "PolicePopulationVisual";
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                // Cop.fbx contains both authored Police bodies. Until a separate canonical
                // variation rule exists, use exactly one body so two skinned meshes never
                // overlap. Do not invent a client-random gender decision.
                SetNamedObjectActive(instance, MaleMeshName, true, required: true);
                SetNamedObjectActive(instance, FemaleMeshName, false, required: true);

                Animator animator = instance.GetComponentInChildren<Animator>(true);
                if (animator == null)
                    animator = instance.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;

                return PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static AnimatorController FindCanonicalPlayerHumanoidController()
        {
            string[] guids = AssetDatabase.FindAssets("t:AnimatorController");
            AnimatorController fallback = null;
            for (int i = 0; i < guids.Length; ++i)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                AnimatorController value = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (value == null)
                    continue;

                if (string.Equals(value.name, "PlayerHumanoid", StringComparison.Ordinal))
                    return value;
                if (fallback == null && value.name.IndexOf("PlayerHumanoid", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = value;
            }
            return fallback;
        }

        private static void SetNamedObjectActive(
            GameObject root,
            string objectName,
            bool active,
            bool required)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            Transform found = null;
            for (int i = 0; i < transforms.Length; ++i)
            {
                if (transforms[i] != null && string.Equals(transforms[i].name, objectName, StringComparison.Ordinal))
                {
                    found = transforms[i];
                    break;
                }
            }

            if (found == null)
            {
                if (required)
                    throw new InvalidOperationException($"Cop.fbx does not contain expected object '{objectName}'.");
                return;
            }

            found.gameObject.SetActive(active);
        }

        private static void Validate(
            CharacterVisualProfile profile,
            GameObject prefabAsset,
            RuntimeAnimatorController expectedController)
        {
            if (profile == null || profile.VisualProfileId != CharacterVisualProfileIds.PopulationPolice)
                throw new InvalidOperationException("Police visual profile id validation failed.");
            if (profile.EditableBasePrefab != prefabAsset)
                throw new InvalidOperationException("Police visual profile prefab reference validation failed.");

            Animator animator = prefabAsset.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid)
                throw new InvalidOperationException("Police presentation prefab does not contain a valid Humanoid Animator.");
            if (animator.runtimeAnimatorController != expectedController)
                throw new InvalidOperationException("Police presentation prefab is not using the canonical PlayerHumanoid controller.");

            Transform male = FindNamedTransform(prefabAsset, MaleMeshName);
            Transform female = FindNamedTransform(prefabAsset, FemaleMeshName);
            if (male == null || !male.gameObject.activeSelf)
                throw new InvalidOperationException("Police male mesh is not active in the generated prefab.");
            if (female == null || female.gameObject.activeSelf)
                throw new InvalidOperationException("Police female mesh must remain inactive until a canonical variation rule exists.");
        }

        private static Transform FindNamedTransform(GameObject root, string objectName)
        {
            if (root == null)
                return null;
            Transform[] values = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < values.Length; ++i)
                if (values[i] != null && string.Equals(values[i].name, objectName, StringComparison.Ordinal))
                    return values[i];
            return null;
        }

        private static void EnsureFolder(string assetFolder)
        {
            string normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized))
                return;

            string[] parts = normalized.Split('/');
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unity asset folder must begin with Assets/: {assetFolder}");

            string current = "Assets";
            for (int i = 1; i < parts.Length; ++i)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static uint StableVersion(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; ++i)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash == 0u ? 1u : hash;
            }
        }
    }
}
#endif
