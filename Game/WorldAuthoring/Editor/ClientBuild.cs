using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    public sealed class ClientBuild : EditorWindow
    {
        private const string OutputPref = "MMO.ClientBuild.Output";
        private string outputPath;
        private bool rebuildNavMesh = true;
        private float populationPortalMarkerRadius = 12f;
        private bool showPopulationRouteAdvanced;
        private Vector2 scroll;

        [MenuItem("Tools/MMO/Build Client")]
        public static void Open() => GetWindow<ClientBuild>("Build Client");

        private void OnEnable()
        {
            string defaultName = string.IsNullOrWhiteSpace(Application.productName) ? "MMOClient" : Application.productName;
            outputPath = EditorPrefs.GetString(OutputPref, $"Builds/Client/{SafeFileName(defaultName)}.exe");
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Client Build", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Client-only build flow. Server World Bake V2 emits Detour navigation plus exact pre-erosion support/edge/collision data. " +
                "The client-safe shared world package is copied into StreamingAssets so legitimate clients can predict walls, ground height, and true ledges locally while the standalone GameServer remains authoritative. " +
                "No scene Unity NavMesh setup is required: exact Player support and standalone Detour data are baked directly from scene geometry.",
                MessageType.Info);

            EditorGUILayout.Space(6);
            DrawCurrentScene();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Population Graph", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("1. AUTO CONNECT", GUILayout.Height(40)))
                    BuildPopulationGraph();
                if (GUILayout.Button("2. EDIT GRAPH", GUILayout.Height(40)))
                    PopulationBakedGraphEditor.Open(SceneManager.GetActiveScene());
                if (GUILayout.Button("3. SAVE GRAPH", GUILayout.Height(40)))
                {
                    PopulationBakedGraphEditor.SaveOpenGraph(SceneManager.GetActiveScene(), out string status);
                    ShowNotification(new GUIContent(status));
                }
            }
            EditorGUILayout.LabelField("Edit: click node A, click node B, then Pair or Clear in the Scene view. " +
                "AUTO CONNECT already bakes and saves the current world.", EditorStyles.wordWrappedMiniLabel);

            // World Bake must never be hidden inside Advanced. AUTO CONNECT bakes the
            // current scene, but explicit bake actions remain accessible and obvious.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake Current Scene"))
                    BakeCurrent();
                if (GUILayout.Button("Bake Enabled Client Scenes"))
                    BakeEnabled();
            }

            showPopulationRouteAdvanced = EditorGUILayout.Foldout(
                showPopulationRouteAdvanced, "Advanced route tools", true);
            if (showPopulationRouteAdvanced)
            {
                EditorGUILayout.LabelField("Server World Bake", EditorStyles.boldLabel);
                rebuildNavMesh = EditorGUILayout.ToggleLeft(
                    "Rebuild authored Unity NavMesh too (optional preview/client use)", rebuildNavMesh);
                EditorGUILayout.LabelField("Server Maps", ServerWorldBake.ServerMapsFolder,
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4);
                DrawPopulationSpawnSummary();
                EditorGUILayout.Space(4);
                populationPortalMarkerRadius = EditorGUILayout.Slider(
                    "Portal Marker Radius", populationPortalMarkerRadius, 1f, 50f);
                if (GUILayout.Button("Find Nearby Portal Markers"))
                {
                    if (PopulationRouteAuthoringTools.AssignNearbyPortalMarkersActiveScene(
                            populationPortalMarkerRadius, out int assigned, out int unresolved))
                        ShowNotification(new GUIContent(
                            $"Portals: {assigned} assigned, {unresolved} unresolved"));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Preview Automatic Base Graph"))
                    {
                        if (PopulationRouteGraphBuilder.BuildPreview(
                                SceneManager.GetActiveScene(), out int edges, out int rejected))
                            ShowNotification(new GUIContent($"Graph: {edges} edges, {rejected} rejected"));
                    }
                    if (GUILayout.Button("Clear Graph Preview"))
                    {
                        PopulationRouteGraphBuilder.ClearPreview(SceneManager.GetActiveScene());
                        ShowNotification(new GUIContent("Population graph preview cleared"));
                    }
                }
                if (GUILayout.Button("Validate Population Routes"))
                {
                    if (PopulationRouteGraphBuilder.BuildPreview(
                            SceneManager.GetActiveScene(), out int edges, out int rejected))
                        ShowNotification(new GUIContent(
                            $"Population graph valid: {edges} edges, {rejected} rejected"));
                }
                if (GUILayout.Button("Open Server Maps Folder"))
                {
                    Directory.CreateDirectory(ServerWorldBake.ServerMapsFolder);
                    EditorUtility.RevealInFinder(ServerWorldBake.ServerMapsFolder);
                }
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Client", EditorStyles.boldLabel);
            outputPath = EditorGUILayout.TextField("Output", outputPath);
            EditorPrefs.SetString(OutputPref, outputPath ?? string.Empty);

            EditorGUILayout.HelpBox(
                "Build Client Only performs: Save -> build the Standalone Windows client from the currently enabled Build Settings scenes. " +
                "It reuses the existing baked StreamingAssets/MMOWorld data and does not rebake or export Server Maps. Use it when world/server bake content has not changed.\n\n" +
                "Bake Server Maps + Build Client performs: Save -> bake all enabled scenes that contain ServerMap -> export server map + client-safe shared world data -> build the client. " +
                "Apartment/property scenes should be enabled in Build Settings if they are shipped as normal client scenes. A scene not in Build Settings can still be baked with Bake Current Scene.",
                MessageType.None);

            GUI.backgroundColor = new Color(0.75f, 1f, 0.75f);
            if (GUILayout.Button("Build Client Only", GUILayout.Height(36)))
                BuildClientOnly();

            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("Bake Server Maps + Build Client", GUILayout.Height(30)))
                BakeAndBuild();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Removed Old Workflow", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This tool does not build or launch a Unity Server, bot build, Host mode, or synthetic test server. " +
                "GameServer/GatewayServer are standalone .NET projects and are built separately.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private static void DrawPopulationSpawnSummary()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            EditorGUILayout.LabelField("Population Spawn Summary", EditorStyles.boldLabel);

            if (!PopulationSpawnAuthoringSummary.TryGetSceneSummary(
                    scene,
                    out PopulationSpawnAuthoringSummary.SceneSummary summary))
            {
                EditorGUILayout.HelpBox(
                    "Population summary is not available for the current scene.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("Spawn Sources", summary.SpawnSources.ToString("n0"));
            EditorGUILayout.LabelField("Total Population Actors", summary.TotalActors.ToString("n0"));
            EditorGUILayout.LabelField("Portal-backed Actors", summary.PortalBackedActors.ToString("n0"));
            EditorGUILayout.LabelField("Direct-route Actors", summary.DirectActors.ToString("n0"));
            EditorGUILayout.LabelField("Active Population Portals", summary.ActivePortals.ToString("n0"));

            if (GUILayout.Button("Refresh Population Counts"))
                PopulationSpawnAuthoringSummary.RefreshNow();

            if (summary.TotalActors == 0)
            {
                EditorGUILayout.HelpBox(
                    "No active, server-enabled Population spawns are authored in the current scene.",
                    MessageType.Info);
            }
        }

        private static void DrawCurrentScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            EditorGUILayout.LabelField("Current Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path, EditorStyles.wordWrappedMiniLabel);

            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            if (map != null)
            {
                EditorGUILayout.LabelField("Map ID", map.MapId);
                EditorGUILayout.LabelField("Kind", map.MapKind.ToString());
                EditorGUILayout.LabelField("Movement", map.WorldRules.movementProfile.ToString());
                EditorGUILayout.LabelField("Combat", map.WorldRules.combatAllowed ? "Allowed" : "Disabled");
                return;
            }

            EditorGUILayout.HelpBox("This scene has no ServerMap. It will be skipped by the server world bake.", MessageType.Warning);
            if (GUILayout.Button("Add Server Map To Scene"))
            {
                GameObject root = new GameObject("Server Map");
                Undo.RegisterCreatedObjectUndo(root, "Add Server Map");
                Undo.AddComponent<ServerMap>(root);
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // BUILD GRAPH uses the existing authoritative world bake rather than creating
        // a second graph file or overwriting an unsaved baked-graph editing session.
        private void BuildPopulationGraph()
        {
            Scene active = SceneManager.GetActiveScene();
            if (!PopulationBakedGraphEditor.SaveOpenEditsBeforeBake(active))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            if (!ServerWorldBake.TryBakeCurrentScene(rebuildNavMesh, true, out _))
            {
                ShowNotification(new GUIContent("Graph bake failed — check Console"));
                return;
            }
            PopulationRouteGraphBuilder.ClearPreview(active);
            PopulationBakedGraphEditor.RefreshOpenEditorsAfterBake(active);
            ShowNotification(new GUIContent("Graph generated — click EDIT GRAPH"));
        }

        private void BakeCurrent() => BuildPopulationGraph();

        private void BakeEnabled()
        {
            if (!PopulationBakedGraphEditor.SaveOpenEditsBeforeBake(SceneManager.GetActiveScene()))
                return;
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh, out List<ServerWorldBake.Result> results))
                return;
            ShowNotification(new GUIContent($"Baked {results.Count} server map(s)"));
        }

        private void BuildClientOnly()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            BuildClientPlayer(null);
        }

        private void BakeAndBuild()
        {
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh, out List<ServerWorldBake.Result> results))
                return;
            BuildClientPlayer(results.Count);
        }

        private void BuildClientPlayer(int? bakedMapCount)
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(x => x.enabled && !string.IsNullOrWhiteSpace(x.path))
                .Select(x => x.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Client Build] No enabled scenes in Build Settings.");
                return;
            }

            string path = string.IsNullOrWhiteSpace(outputPath) ? "Builds/Client/MMOClient.exe" : outputPath.Trim();
            if (!Path.IsPathRooted(path))
                path = Path.Combine(ServerWorldBake.ProjectRoot, path);
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            StandaloneBuildSubtarget previous = EditorUserBuildSettings.standaloneBuildSubtarget;
            try
            {
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    locationPathName = path,
                    options = BuildOptions.None,
                });

                if (report.summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError($"[Client Build] Build failed: {report.summary.result}");
                    return;
                }

                string serverBakeSummary = bakedMapCount.HasValue ? bakedMapCount.Value.ToString() : "skipped";
                Debug.Log(
                    $"[Client Build] Complete: {path}\n" +
                    $"  scenes={scenes.Length}, serverMapsBaked={serverBakeSummary}, size={report.summary.totalSize / (1024d * 1024d):0.0} MB");
            }
            finally
            {
                EditorUserBuildSettings.standaloneBuildSubtarget = previous;
            }
        }

        private static string SafeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }
    }
}
