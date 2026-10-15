using System;
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
        // Unity-authored NavMesh rebuilding is not required for server/client world bake.
        private const bool rebuildNavMesh = false;
        private bool showSpawnsRoutes;
        private bool showPopulationRouteAdvanced;
        private Vector2 scroll;

        // Read the existing .routes.json only when it changes, not on every OnGUI event.
        // This is a view of saved data, not a second graph or a bake-time cache.
        private string savedGraphPath;
        private DateTime savedGraphWriteUtc;
        private long savedGraphBytes = -1;
        private string savedGraphStatus;
        private int savedNodes, savedEdges, savedManual, savedPortals, savedSpawns;
        private string publicationStatus;
        private bool publicationSuccess;

        [MenuItem("Tools/MMO/Build Client")]
        public static void Open() => GetWindow<ClientBuild>("Build Client");

        private void OnEnable()
        {
            string defaultName = string.IsNullOrWhiteSpace(Application.productName)
                ? "MMOClient" : Application.productName;
            outputPath = EditorPrefs.GetString(OutputPref,
                $"Builds/Client/{SafeFileName(defaultName)}.exe");
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawCurrentScene();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Server World Bake", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "World bakes keep the existing saved Population graph. They do not AUTO CONNECT routes. " +
                "Use Spawns / Routes only when you are authoring connections. " +
                "Unsaved graph-editor changes are NOT saved automatically; press SAVE first.",
                MessageType.None);
            EditorGUILayout.Space(8);
            bool priorExpanded = showSpawnsRoutes;
            showSpawnsRoutes = EditorGUILayout.Foldout(showSpawnsRoutes, "Spawns / Routes", true);
            if (showSpawnsRoutes)
            {
                // Opening the foldout loads the SAVED graph summary, never regenerates it.
                if (!priorExpanded) RefreshSavedGraph(true);
                DrawSavedGraph();
                if (!string.IsNullOrWhiteSpace(publicationStatus))
                    EditorGUILayout.HelpBox(publicationStatus,
                        publicationSuccess ? MessageType.None : MessageType.Error);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("AUTO CONNECT", GUILayout.Height(34)))
                        AutoConnect();
                    if (GUILayout.Button("EDIT", GUILayout.Height(34)))
                        PopulationBakedGraphEditor.Open(SceneManager.GetActiveScene());
                    if (GUILayout.Button("SAVE", GUILayout.Height(34)))
                        SaveRoutes();
                }
                EditorGUILayout.LabelField(
                    "AUTO CONNECT is only for new or changed route authoring. " +
                    "EDIT loads the saved graph; SAVE persists editor changes. " +
                    "Existing routes are not rebuilt by normal World Bake.",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4);
                DrawPopulationSpawnSummary();
                EditorGUILayout.Space(4);
                showPopulationRouteAdvanced = EditorGUILayout.Foldout(
                    showPopulationRouteAdvanced, "Advanced route tools", true);
                if (showPopulationRouteAdvanced)
                {
                    EditorGUILayout.LabelField("Server Maps", ServerWorldBake.ServerMapsFolder,
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(4);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Preview Automatic Base Graph"))
                        {
                            if (PopulationRouteGraphBuilder.BuildPreview(
                                SceneManager.GetActiveScene(), out int edges, out int rejected))
                                ShowNotification(new GUIContent(
                                    $"Graph: {edges} edges, {rejected} rejected"));
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
                                $"Population graph preview: {edges} edges, {rejected} rejected"));
                    }
                    if (GUILayout.Button("Open Server Maps Folder"))
                    {
                        Directory.CreateDirectory(ServerWorldBake.ServerMapsFolder);
                        EditorUtility.RevealInFinder(ServerWorldBake.ServerMapsFolder);
                    }
                }
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Final World Bake", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake Current Scene", GUILayout.Height(32))) BakeCurrent();
                if (GUILayout.Button("Bake Enabled Client Scenes", GUILayout.Height(32))) BakeEnabled();
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Client", EditorStyles.boldLabel);
            outputPath = EditorGUILayout.TextField("Output", outputPath);
            EditorPrefs.SetString(OutputPref, outputPath ?? string.Empty);
            if (GUILayout.Button("Build Client Only", GUILayout.Height(36)))
                BuildClientOnly();
            if (GUILayout.Button("Bake Server Maps + Build Client", GUILayout.Height(30)))
                BakeAndBuild();
            EditorGUILayout.EndScrollView();
        }

        private void RefreshSavedGraph(bool force = false)
        {
            Scene scene = SceneManager.GetActiveScene();
            string path = scene.IsValid() && scene.isLoaded
                ? PopulationBakedGraphEditor.RoutePath(scene) : null;
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
            DateTime writeUtc = exists ? File.GetLastWriteTimeUtc(path) : default;
            long bytes = exists ? new FileInfo(path).Length : -1;
            if (!force && string.Equals(path, savedGraphPath, StringComparison.Ordinal) &&
                savedGraphWriteUtc == writeUtc && savedGraphBytes == bytes)
                return;

            savedGraphPath = path;
            savedGraphWriteUtc = writeUtc;
            savedGraphBytes = bytes;
            savedNodes = savedEdges = savedManual = savedPortals = savedSpawns = 0;
            savedGraphStatus = null;
            if (!exists)
            {
                savedGraphStatus = "No saved graph for this map. Run AUTO CONNECT once to create it.";
                return;
            }
            try
            {
                var layer = JsonUtility.FromJson<PopulationBakedGraphEditor.Layer>(
                    File.ReadAllText(path));
                if (layer == null || layer.formatVersion != 1 ||
                    layer.populationNodes == null || layer.populationEdges == null)
                    throw new InvalidDataException("Invalid saved route-layer format or missing arrays.");
                savedNodes = layer.populationNodes.Length;
                savedEdges = layer.populationEdges.Length;
                savedPortals = layer.populationPortals?.Length ?? 0;
                savedManual = layer.editorEdits?.manualEdges?.Length ?? 0;
                string spawnsPath = path.Substring(0,
                    path.Length - ".routes.json".Length) + ".spawns.json";
                if (File.Exists(spawnsPath))
                {
                    // We only need a count here; no second persistent copy of spawns.
                    var spawns = JsonUtility.FromJson<SpawnSummary>(File.ReadAllText(spawnsPath));
                    savedSpawns = spawns?.spawnAnchors?.Length ?? 0;
                }
                savedGraphStatus = "Current saved graph loaded";
            }
            catch (Exception ex)
            {
                savedGraphStatus = "Saved graph could not be loaded: " + ex.Message;
            }
        }

        [Serializable]
        private sealed class SpawnSummary
        {
            public Game.Shared.World.ServerSpawnAnchor[] spawnAnchors;
        }

        private void DrawSavedGraph()
        {
            RefreshSavedGraph();
            if (savedGraphStatus != "Current saved graph loaded")
            {
                EditorGUILayout.HelpBox(savedGraphStatus ?? "No saved graph", MessageType.Warning);
                return;
            }
            EditorGUILayout.LabelField(savedGraphStatus, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Nodes: {savedNodes:n0}    Connections: {savedEdges:n0}    " +
                $"Manual: {savedManual:n0}");
            EditorGUILayout.LabelField($"Portals: {savedPortals:n0}    Spawn anchors: {savedSpawns:n0}");
            EditorGUILayout.LabelField("Server output folder:", ServerWorldBake.ServerMapsFolder,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("GameServer reads layers on STARTUP, not when you press SAVE.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void AutoConnect()
        {
            if (!EditorUtility.DisplayDialog("AUTO CONNECT Routes",
                "This explicitly regenerates Population routes from scene authoring. " +
                "The existing world bake stays unchanged until validation passes. " +
                "If a saved connection, portal, or spawn would disappear, publication is refused. " +
                "Only run this when adding or changing routes. SAVE any open graph edits first; " +
                "AUTO CONNECT reads the last saved graph from disk.",
                "AUTO CONNECT", "Cancel"))
                return;
            Scene active = SceneManager.GetActiveScene();
            // Never implicitly save/prune the open graph. AUTO CONNECT uses disk edits.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!ServerWorldBake.TryBakeCurrentScene(rebuildNavMesh, true,
                    out _, autoConnectRoutes: true))
            {
                ShowNotification(new GUIContent("AUTO CONNECT refused or failed — check Console"));
                return;
            }
            PopulationRouteGraphBuilder.ClearPreview(active);
            PopulationBakedGraphEditor.RefreshOpenEditorsAfterBake(active);
            RefreshSavedGraph(true);
            ShowNotification(new GUIContent("AUTO CONNECT completed; saved graph loaded"));
        }

        // The same map data is consumed by ServerMapDataLoader at GameServer startup.
        // Do not claim the server was updated merely because editor files were written.
        private void SetPublicationStatus(string message, bool succeeded)
        {
            publicationStatus = message;
            publicationSuccess = succeeded;
            if (succeeded) Debug.Log("[World Bake Publish] " + message);
            else Debug.LogError("[World Bake Publish] " + message);
            ShowNotification(new GUIContent(succeeded ? "Verified — see Inspector" : "Failed — see Inspector / Console"));
            Repaint();
        }

        private void SaveRoutes()
        {
            Scene scene = SceneManager.GetActiveScene();
            bool saved = PopulationBakedGraphEditor.SaveOpenGraph(scene, out string status);
            RefreshSavedGraph(true);
            if (!saved)
            {
                SetPublicationStatus(status + " Previous saved graph remains in place.", false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer disk, out string problem))
            {
                SetPublicationStatus("SAVE verification failed: " + problem, false);
                return;
            }
            SetPublicationStatus($"SAVE VERIFIED on disk: {disk.populationNodes.Length} nodes / " +
                $"{disk.populationEdges.Length} connections. Now BAKE, then restart GameServer. " +
                "Map folder: " + ServerWorldBake.ServerMapsFolder, true);
        }

        private static bool TryReadRouteLayer(Scene scene,
            out PopulationBakedGraphEditor.Layer layer, out string error)
        {
            layer = null;
            error = string.Empty;
            string path = PopulationBakedGraphEditor.RoutePath(scene);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "No saved .routes.json exists for this scene.";
                return false;
            }
            try
            {
                layer = JsonUtility.FromJson<PopulationBakedGraphEditor.Layer>(File.ReadAllText(path));
                if (layer == null || layer.formatVersion != 1 ||
                    layer.populationNodes == null || layer.populationEdges == null ||
                    layer.populationPortals == null || layer.editorEdits == null)
                    throw new InvalidDataException("Saved route layer is missing required graph arrays.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [Serializable]
        private sealed class GraphPayload
        {
            public Game.Shared.Population.ServerPopulationRouteNode[] nodes;
            public Game.Shared.Population.ServerPopulationRouteEdge[] edges;
            public Game.Shared.Population.ServerPopulationPortal[] portals;
            public PopulationBakedGraphEdits edits;
        }

        private static string GraphContent(PopulationBakedGraphEditor.Layer l) =>
            JsonUtility.ToJson(new GraphPayload
            {
                nodes = l.populationNodes,
                edges = l.populationEdges,
                portals = l.populationPortals,
                edits = l.editorEdits,
            });

        private void BakeCurrent()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (PopulationBakedGraphEditor.HasUnsavedOpenGraph(scene, out string dirtyReason))
            {
                SetPublicationStatus("BAKE BLOCKED: " + dirtyReason, false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer before, out string error))
            {
                SetPublicationStatus("BAKE BLOCKED: " + error, false);
                return;
            }
            string preservedGraph = GraphContent(before);
            // Never invoke AUTO CONNECT or implicitly save/prune routes here.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!ServerWorldBake.TryBakeCurrentScene(rebuildNavMesh, true, out ServerWorldBake.Result result))
            {
                SetPublicationStatus("BAKE FAILED for '" + scene.name +
                    "'. Original world files should remain preserved; check Console.", false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer after, out error) ||
                !string.Equals(preservedGraph, GraphContent(after), StringComparison.Ordinal))
            {
                SetPublicationStatus("BAKE VERIFICATION FAILED: Routes changed unexpectedly or are unreadable. " +
                    "Do not start the GameServer with these files. " + error, false);
                return;
            }
            PopulationBakedGraphEditor.RefreshOpenEditorsAfterBake(scene);
            RefreshSavedGraph(true);
            SetPublicationStatus($"BAKE VERIFIED: {after.populationNodes.Length} nodes / " +
                $"{after.populationEdges.Length} connections / {result.Spawns} baked spawn anchors. " +
                $"Output: {result.OutputPath}. RESTART GameServer and confirm its 'World layers' startup count. " +
                "SAVE/BAKE does NOT hot-reload running server processes.", true);
        }

        private void BakeEnabled()
        {
            Scene active = SceneManager.GetActiveScene();
            if (PopulationBakedGraphEditor.HasUnsavedOpenGraph(active, out string dirtyReason))
            {
                SetPublicationStatus("BAKE BLOCKED: " + dirtyReason, false);
                return;
            }
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh,
                out List<ServerWorldBake.Result> results))
            {
                SetPublicationStatus("BAKE ENABLED SCENES failed — check Console.", false);
                return;
            }
            RefreshSavedGraph(true);
            SetPublicationStatus($"BAKE completed for {results.Count} enabled map(s). " +
                "RESTART GameServer after confirming the correct MapDataDirectory and startup 'World layers' counts.", true);
        }

        private static void DrawPopulationSpawnSummary()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;
            EditorGUILayout.LabelField("Population Spawn Summary", EditorStyles.boldLabel);
            if (!PopulationSpawnAuthoringSummary.TryGetSceneSummary(
                scene, out PopulationSpawnAuthoringSummary.SceneSummary summary))
            {
                EditorGUILayout.HelpBox("Population summary is unavailable.", MessageType.Info);
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
                EditorGUILayout.HelpBox("No active Population spawns in the current scene.",
                    MessageType.Info);
        }

        private static void DrawCurrentScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            EditorGUILayout.LabelField("Current Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(scene.path)
                ? scene.name : scene.path, EditorStyles.wordWrappedMiniLabel);
            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            if (map != null)
            {
                EditorGUILayout.LabelField("Map ID", map.MapId);
                EditorGUILayout.LabelField("Kind", map.MapKind.ToString());
                EditorGUILayout.LabelField("Movement", map.WorldRules.movementProfile.ToString());
                EditorGUILayout.LabelField("Combat", map.WorldRules.combatAllowed ? "Allowed" : "Disabled");
                return;
            }
            EditorGUILayout.HelpBox("This scene has no ServerMap. It will be skipped by the world bake.",
                MessageType.Warning);
            if (GUILayout.Button("Add Server Map To Scene"))
            {
                GameObject root = new GameObject("Server Map");
                Undo.RegisterCreatedObjectUndo(root, "Add Server Map");
                Undo.AddComponent<ServerMap>(root);
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private void BuildClientOnly()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BuildClientPlayer(null);
        }

        private void BakeAndBuild()
        {
            if (PopulationBakedGraphEditor.HasUnsavedOpenGraph(
                    SceneManager.GetActiveScene(), out string dirtyReason))
            {
                SetPublicationStatus("BAKE + BUILD BLOCKED: " + dirtyReason, false);
                return;
            }
            // Preserve the existing on-disk graph without an implicit graph Save.
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh,
                out List<ServerWorldBake.Result> results)) return;
            BuildClientPlayer(results.Count);
        }

        private void BuildClientPlayer(int? bakedMapCount)
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(x => x.enabled && !string.IsNullOrWhiteSpace(x.path))
                .Select(x => x.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Client Build] No enabled scenes in Build Settings.");
                return;
            }
            string path = string.IsNullOrWhiteSpace(outputPath)
                ? "Builds/Client/MMOClient.exe" : outputPath.Trim();
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
                string serverBakeSummary = bakedMapCount.HasValue
                    ? bakedMapCount.Value.ToString() : "skipped";
                Debug.Log($"[Client Build] Complete: {path}\n" +
                    $"  scenes={scenes.Length}, serverMapsBaked={serverBakeSummary}, " +
                    $"size={report.summary.totalSize / (1024d * 1024d):0.0} MB");
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
