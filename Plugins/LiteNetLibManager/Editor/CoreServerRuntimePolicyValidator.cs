using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LiteNetLibManager.Editor
{
    /// <summary>
    /// Enforces the authoritative-server execution model for assemblies whose asmdef name
    /// ends in ".Server" or equals "Server". Files explicitly containing
    /// [CoreRuntimeBypassAllowed(...)] are treated as reviewed exceptions.
    /// </summary>
    public sealed class CoreServerRuntimePolicyValidator : IPreprocessBuildWithReport
    {
        private static readonly Regex[] s_DisallowedRuntimePatterns =
        {
            new Regex(@"\bvoid\s+Update\s*\(", RegexOptions.Compiled),
            new Regex(@"\bvoid\s+FixedUpdate\s*\(", RegexOptions.Compiled),
            new Regex(@"\bvoid\s+LateUpdate\s*\(", RegexOptions.Compiled),
            new Regex(@"\bInvokeRepeating\s*\(", RegexOptions.Compiled),
            new Regex(@"\bStartCoroutine\s*\(", RegexOptions.Compiled),
            new Regex(@"\bTask\s*\.\s*Run\s*\(", RegexOptions.Compiled),
            new Regex(@"\bnew\s+Thread\s*\(", RegexOptions.Compiled),
            new Regex(@"\bnew\s+(?:System\.Threading\.)?Timer\s*\(", RegexOptions.Compiled),
            new Regex(@"\bwhile\s*\(\s*true\s*\)", RegexOptions.Compiled),
            new Regex(@"\bfor\s*\(\s*;\s*;\s*\)", RegexOptions.Compiled),
        };

        private static readonly Regex[] s_DisallowedPresentationPatterns =
        {
            new Regex(@"\bUnityEngine\.UI\b", RegexOptions.Compiled),
            new Regex(@"\bAnimator\b", RegexOptions.Compiled),
            new Regex(@"\bCamera\b", RegexOptions.Compiled),
            new Regex(@"\bAudioSource\b", RegexOptions.Compiled),
            new Regex(@"\bRenderer\b", RegexOptions.Compiled),
            new Regex(@"\bParticleSystem\b", RegexOptions.Compiled),
        };

        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            List<string> violations = ValidateProject();
            if (violations.Count > 0)
                throw new BuildFailedException(BuildMessage(violations));
        }

        [MenuItem("Tools/LiteNetLibManager/Validate Server Runtime Boundaries")]
        public static void ValidateFromMenu()
        {
            List<string> violations = ValidateProject();
            if (violations.Count == 0)
            {
                Debug.Log("[CoreServerRuntimePolicyValidator] Server runtime boundary validation passed.");
                return;
            }
            Debug.LogError(BuildMessage(violations));
        }

        private static List<string> ValidateProject()
        {
            var violations = new List<string>();
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            string[] asmdefPaths = Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories);

            foreach (string asmdefPath in asmdefPaths)
            {
                string json = File.ReadAllText(asmdefPath);
                string assemblyName = ExtractAssemblyName(json);
                if (!IsServerAssembly(assemblyName))
                    continue;

                string assemblyDirectory = Path.GetDirectoryName(asmdefPath);
                if (string.IsNullOrEmpty(assemblyDirectory))
                    continue;

                foreach (string csPath in EnumerateOwnedCsFiles(assemblyDirectory))
                {
                    string raw = File.ReadAllText(csPath);
                    if (raw.IndexOf("CoreRuntimeBypassAllowed", StringComparison.Ordinal) >= 0)
                        continue;

                    string source = StripCommentsAndStrings(raw);
                    foreach (Regex pattern in s_DisallowedRuntimePatterns)
                    {
                        if (pattern.IsMatch(source))
                            violations.Add($"{Relative(projectRoot, csPath)}: unmanaged runtime loop/API matches /{pattern}/");
                    }
                    foreach (Regex pattern in s_DisallowedPresentationPatterns)
                    {
                        if (pattern.IsMatch(source))
                            violations.Add($"{Relative(projectRoot, csPath)}: presentation dependency matches /{pattern}/");
                    }
                }
            }

            return violations.Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> EnumerateOwnedCsFiles(string assemblyDirectory)
        {
            foreach (string csPath in Directory.GetFiles(assemblyDirectory, "*.cs", SearchOption.AllDirectories))
            {
                string directory = Path.GetDirectoryName(csPath);
                bool nestedAsmdef = false;
                while (!string.IsNullOrEmpty(directory) &&
                       !string.Equals(directory, assemblyDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.GetFiles(directory, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        nestedAsmdef = true;
                        break;
                    }
                    directory = Path.GetDirectoryName(directory);
                }
                if (!nestedAsmdef)
                    yield return csPath;
            }
        }

        private static bool IsServerAssembly(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return string.Equals(name, "Server", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".Server", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractAssemblyName(string json)
        {
            Match match = Regex.Match(json ?? string.Empty, "\\\"name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"");
            return match.Success ? match.Groups["name"].Value : string.Empty;
        }

        private static string StripCommentsAndStrings(string source)
        {
            if (string.IsNullOrEmpty(source))
                return string.Empty;
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            source = Regex.Replace(source, @"//.*?$", " ", RegexOptions.Multiline);
            source = Regex.Replace(source, "@\"(?:\"\"|[^\"])*\"", "\"\"", RegexOptions.Singleline);
            source = Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
            return source;
        }

        private static string Relative(string root, string path)
        {
            if (string.IsNullOrEmpty(root))
                return path;
            Uri rootUri = new Uri(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            Uri pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string BuildMessage(List<string> violations)
        {
            return "Server runtime boundary validation failed:\n- " + string.Join("\n- ", violations);
        }
    }
}
