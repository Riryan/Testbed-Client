#if UNITY_EDITOR
using System.IO;
using Player.Client.Presentation;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    public static class InteractionAnimationCatalogAuthoring
    {
        private const string Folder = "Assets/Resources/MMO/Interactions";
        private const string AssetPath = Folder + "/InteractionAnimationCatalog.asset";

        [MenuItem("MMO Tools/Presentation/Interaction Animations/Create Default Catalog V1")]
        public static void CreateDefaultCatalog()
        {
            InteractionAnimationCatalog existing =
                AssetDatabase.LoadAssetAtPath<InteractionAnimationCatalog>(AssetPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                Debug.Log(
                    "[Interaction Animation] Catalog already exists; preserving authored mappings: " +
                    AssetPath);
                return;
            }

            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/Resources/MMO");
            EnsureFolder(Folder);

            var catalog = ScriptableObject.CreateInstance<InteractionAnimationCatalog>();
            catalog.SetBindingsForEditor(
                InteractionAnimationCatalog.CreateDefaultBindingsForEditor());

            AssetDatabase.CreateAsset(catalog, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);

            Debug.Log(
                "[Interaction Animation] Created default local presentation catalog at " +
                AssetPath +
                ". Bind presentation ids to states/triggers that already exist in the canonical PlayerHumanoid controller.");
        }

        [MenuItem("MMO Tools/Presentation/Interaction Animations/Select Catalog")]
        public static void SelectCatalog()
        {
            InteractionAnimationCatalog catalog =
                AssetDatabase.LoadAssetAtPath<InteractionAnimationCatalog>(AssetPath);
            if (catalog == null)
            {
                Debug.LogWarning(
                    "[Interaction Animation] Catalog does not exist yet. Run Create Default Catalog V1.");
                return;
            }

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
