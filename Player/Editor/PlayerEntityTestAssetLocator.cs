using System;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Player.Editor
{
    /// <summary>
    /// Editor-only discovery for PlayerEntity test content.
    ///
    /// No consumer depends on a fixed Assets/... path. Existing scene/prefab assets
    /// are discovered through AssetDatabase. When new test content must be created,
    /// its Generated folder is derived from the current physical location of the
    /// Testing.Player assembly definition, so moving that assembly tree does not
    /// require changing this code.
    /// </summary>
    internal static class PlayerEntityTestAssetLocator
    {
        private const string TestingAssemblyFileName = "Testing.Player.asmdef";
        private const string TestingAssemblyFilePrefix = "Testing.Player";
        private const string PlayerPrefabFileName = "MMOPlayerEntity.prefab";
        private const string SceneFileName = "MMOPlayerEntityTest.unity";

        public static string GeneratedFolder =>
            GetTestingContentRoot() + "/Generated";

        public static string PlayerPrefabPath =>
            FindExactAssetPath(PlayerPrefabFileName, "t:Prefab") ??
            GeneratedFolder + "/" + PlayerPrefabFileName;

        public static string ScenePath =>
            FindExactAssetPath(SceneFileName, "t:Scene") ??
            GeneratedFolder + "/" + SceneFileName;

        public static void EnsureGeneratedFolder()
        {
            EnsureFolder(GeneratedFolder);
        }

        private static string GetTestingContentRoot()
        {
            string[] assemblyPaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) &&
                               path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                .Where(path =>
                {
                    string fileName = Path.GetFileName(path);
                    return fileName.StartsWith(TestingAssemblyFilePrefix, StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string mainAssemblyPath = assemblyPaths.FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                TestingAssemblyFileName,
                StringComparison.OrdinalIgnoreCase));

            if (mainAssemblyPath == null)
            {
                throw new InvalidOperationException(
                    "Could not locate Testing.Player.asmdef. " +
                    "PlayerEntity test content location cannot be resolved until that assembly exists.");
            }

            string[] assemblyFolders = assemblyPaths
                .Select(path => Path.GetDirectoryName(path)?.Replace('\\', '/'))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string root = assemblyFolders.Length > 1
                ? FindCommonFolder(assemblyFolders)
                : Path.GetDirectoryName(Path.GetDirectoryName(mainAssemblyPath))?.Replace('\\', '/');

            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("Testing.Player assembly tree has no valid common asset folder.");

            return root;
        }

        private static string FindCommonFolder(string[] folders)
        {
            string[][] parts = folders
                .Select(folder => folder.Split('/'))
                .ToArray();

            int commonLength = parts.Min(part => part.Length);
            int index = 0;
            while (index < commonLength)
            {
                string candidate = parts[0][index];
                if (parts.Any(part => !string.Equals(part[index], candidate, StringComparison.OrdinalIgnoreCase)))
                    break;
                index++;
            }

            if (index == 0)
                return null;

            return string.Join("/", parts[0].Take(index));
        }

        private static string FindExactAssetPath(string fileName, string typeFilter)
        {
            string preferred = GeneratedFolder + "/" + fileName;
            if (AssetDatabase.LoadMainAssetAtPath(preferred) != null)
                return preferred;

            string searchName = Path.GetFileNameWithoutExtension(fileName);
            string[] candidates = AssetDatabase.FindAssets(searchName + " " + typeFilter)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return candidates.Length > 0 ? candidates[0] : null;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string[] segments = folderPath.Split('/');
            if (segments.Length == 0 || !string.Equals(segments[0], "Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Generated test-content folder must resolve inside Assets: {folderPath}");
            }

            string current = segments[0];
            for (int i = 1; i < segments.Length; ++i)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }
    }
}
