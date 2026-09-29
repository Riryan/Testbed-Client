#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Player.Editor
{
    /// <summary>
    /// Deploys the public BackendServer development-certificate SHA-256 fingerprint
    /// beside every standalone Windows client build. This is client configuration,
    /// not a secret. Server credentials and private certificate material are never copied.
    ///
    /// This postprocessor intentionally runs independently of the MMO build window so
    /// Build Profiles / Build Settings / scripted BuildPipeline builds all get the same
    /// authentication-pin deployment behavior.
    /// </summary>
    public sealed class MMOClientAuthPinBuildPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null)
                return;

            if (report.summary.platform != BuildTarget.StandaloneWindows64)
                return;

            // BuildSummary in the installed Unity 6 API does not expose subtarget.
            // The project's existing build tooling already uses the supported
            // EditorUserBuildSettings.standaloneBuildSubtarget API, so use the same
            // canonical source to keep client auth configuration out of legacy
            // Unity Server-subtarget builds.
            if (EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string source = Path.Combine(projectRoot, "Server", "Data", "backend-cert.sha256");
            if (!File.Exists(source))
            {
                throw new BuildFailedException(
                    "Client build cannot be finalized because Server/Data/backend-cert.sha256 is missing. " +
                    "Start BackendServer once so it generates the public certificate fingerprint, then rebuild the client.");
            }

            string outputPath = report.summary.outputPath;
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new BuildFailedException("Client build output path was empty; cannot deploy Backend certificate fingerprint.");

            string buildDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(buildDirectory))
                throw new BuildFailedException($"Could not determine client build directory from '{outputPath}'.");

            string destinationDirectory = Path.Combine(buildDirectory, "Server", "Data");
            string destination = Path.Combine(destinationDirectory, "backend-cert.sha256");

            Directory.CreateDirectory(destinationDirectory);
            File.Copy(source, destination, true);

            if (!File.Exists(destination))
                throw new BuildFailedException($"Backend certificate fingerprint copy did not produce '{destination}'.");

            string sourceValue = File.ReadAllText(source).Trim();
            string destinationValue = File.ReadAllText(destination).Trim();
            if (string.IsNullOrWhiteSpace(destinationValue) ||
                !string.Equals(sourceValue, destinationValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    $"Backend certificate fingerprint verification failed after copying to '{destination}'.");
            }

            Debug.Log($"[MMO Client Build] Deployed Backend certificate fingerprint: {destination}");
        }
    }
}
#endif
