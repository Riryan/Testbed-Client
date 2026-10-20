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
    /// <summary>
    /// Canonical Build Client / Server World Bake editor. Map, routes, and spawns use
    /// their existing layer files. The targeted spawn rebuild leaves the map and the
    /// route topology alone; full bake is retained for physical-world modifications.
    /// </summary>
    public sealed class ClientBuild : EditorWindow
    {
        private const string OutputPref = "MMO.ClientBuild.Output";
        private string outputPath;
        private const bool rebuildNavMesh = false;
        private bool showSpawnsRoutes;
        private bool showPopulationRouteAdvanced;
        private Vector2 scroll;
        private string savedGraphPath;
        private DateTime savedGraphWriteUtc;
        private long savedGraphBytes = -1;
        private string savedGraphStatus;
        private int savedNodes, savedEdges, savedManual, savedPortals, savedSpawns, savedEnabledNpcActors;
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
                "World geometry, shared Routes, and Spawns have separate published files. " +
                "SAVE preserves scene and route edits. BAKE SPAWNS ONLY updates spawn anchors " +
                "and their existing route IDs without rebuilding geometry or NavMesh. " +
                "Use AUTO CONNECT if a route marker, portal entry, or route topology changed.",
                MessageType.Info);
            EditorGUILayout.Space(8);

            bool wasExpanded = showSpawnsRoutes;
            showSpawnsRoutes = EditorGUILayout.Foldout(showSpawnsRoutes, "Spawns / Routes", true);
            if (showSpawnsRoutes)
            {
                if (!wasExpanded) RefreshSavedGraph(true);
                DrawSavedGraph();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("AUTO CONNECT", GUILayout.Height(34))) AutoConnect();
                    if (GUILayout.Button("EDIT", GUILayout.Height(34)))
                        PopulationBakedGraphEditor.Open(SceneManager.GetActiveScene());
                    if (GUILayout.Button("SAVE", GUILayout.Height(34))) SaveRoutes();
                }
                EditorGUILayout.LabelField(
                    "AUTO CONNECT rebuilds route connections and then reconciles spawns. " +
                    "EDIT handles manual route changes. SAVE writes the scene and open route edits.",
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
                                $"Population graph preview: {edges} edges, {rejected} rejected"));
                    }
                    if (GUILayout.Button("Open Server Maps Folder"))
                    {
                        Directory.CreateDirectory(ServerWorldBake.ServerMapsFolder);
                        EditorUtility.RevealInFinder(ServerWorldBake.ServerMapsFolder);
                    }
                }
            }

            // Final bake actions remain AFTER spawn and route authoring.
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Final World Bake", EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(publicationStatus))
                EditorGUILayout.HelpBox(publicationStatus,
                    publicationSuccess ? MessageType.Info : MessageType.Error);
            if (GUILayout.Button("Bake Spawns Only (Fast)", GUILayout.Height(34)))
                BakeSpawnsOnly();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake Current Scene (Full Map)", GUILayout.Height(32))) BakeCurrent();
                if (GUILayout.Button("Bake Enabled Client Scenes", GUILayout.Height(32))) BakeEnabled();
            }
            EditorGUILayout.LabelField(
                "Spawn-only: no NavMesh, collision export, or shared-world rebuild. " +
                "Full map bake: geometry, collision/NavMesh, routes preserved, then spawns reconciled. " +
                "Restart GameServer to activate published changes.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Client", EditorStyles.boldLabel);
            outputPath = EditorGUILayout.TextField("Output", outputPath);
            EditorPrefs.SetString(OutputPref, outputPath ?? string.Empty);
            if (GUILayout.Button("Build Client Only", GUILayout.Height(36))) BuildClientOnly();
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
            savedNodes = savedEdges = savedManual = savedPortals = savedSpawns = savedEnabledNpcActors = 0;
            savedGraphStatus = null;
            if (!exists)
            {
                savedGraphStatus = "No saved graph for this map. Run AUTO CONNECT once.";
                return;
            }
            try
            {
                var layer = JsonUtility.FromJson<PopulationBakedGraphEditor.Layer>(
                    File.ReadAllText(path));
                if (layer == null || layer.formatVersion != 1 ||
                    layer.populationNodes == null || layer.populationEdges == null)
                    throw new InvalidDataException("Invalid saved route layer.");
                savedNodes = layer.populationNodes.Length;
                savedEdges = layer.populationEdges.Length;
                savedPortals = layer.populationPortals?.Length ?? 0;
                savedManual = layer.editorEdits?.manualEdges?.Length ?? 0;
                string spawnPath = path.Substring(0, path.Length - ".routes.json".Length) +
                    ".spawns.json";
                if (File.Exists(spawnPath))
                {
                    var spawns = JsonUtility.FromJson<SpawnSummary>(File.ReadAllText(spawnPath));
                    savedSpawns = spawns?.spawnAnchors?.Length ?? 0;
                    savedEnabledNpcActors = spawns?.spawnAnchors?.Count(x => x != null && x.enabled &&
                        (x.kind == Game.Shared.World.ServerSpawnKind.Population ||
                         x.kind == Game.Shared.World.ServerSpawnKind.Npc ||
                         x.kind == Game.Shared.World.ServerSpawnKind.Monster)) ?? 0;
                }
                savedGraphStatus = "Current saved graph loaded";
            }
            catch (Exception ex) { savedGraphStatus = "Saved graph could not be loaded: " + ex.Message; }
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
                $"Nodes: {savedNodes:n0}    Connections: {savedEdges:n0}    Manual: {savedManual:n0}");
            EditorGUILayout.LabelField($"Portals: {savedPortals:n0}    Spawn anchors: {savedSpawns:n0}    " +
                $"Enabled NPCs: {savedEnabledNpcActors:n0}");
            EditorGUILayout.LabelField("Server output folder:", ServerWorldBake.ServerMapsFolder,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("The GameServer reads these files at startup.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static bool ConfirmSavedScene()
        {
            return EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        }

        private void SetPublicationStatus(string message, bool succeeded)
        {
            publicationStatus = message;
            publicationSuccess = succeeded;
            if (succeeded) Debug.Log("[World Bake Publish] " + message);
            else Debug.LogError("[World Bake Publish] " + message);
            ShowNotification(new GUIContent(succeeded ? "Bake verified" : "Bake failed — check Console"));
            Repaint();
        }

        private void SaveRoutes()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                SetPublicationStatus("SAVE requires a saved, loaded scene.", false);
                return;
            }
            // Save button must save SCENE changes, including disabled ServerSpawns
            // and PopulationPortals, not only the route graph editor document.
            if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
            {
                SetPublicationStatus("Failed to save Unity scene; route edits not published.", false);
                return;
            }
            // The existing graph SAVE API intentionally fails if EDIT isn't open.
            // A scene-only SAVE must still work without opening an unrelated editor.
            bool graphEditorOpen = Resources.FindObjectsOfTypeAll<PopulationBakedGraphEditor>().Length != 0;
            bool saved = true;
            string status = "Unity scene saved; no route graph editor was open.";
            if (graphEditorOpen)
                saved = PopulationBakedGraphEditor.SaveOpenGraph(scene, out status);
            RefreshSavedGraph(true);
            if (!saved)
            {
                SetPublicationStatus(status + " Previous saved graph remains in place.", false);
                return;
            }
            // Graph SAVE may synchronize scene route markers. Persist them as well.
            if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
            {
                SetPublicationStatus("Scene SAVE failed after graph synchronization.", false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer disk, out string problem))
            {
                SetPublicationStatus("SAVE verification failed: " + problem, false);
                return;
            }
            SetPublicationStatus($"SCENE + ROUTES SAVED: {disk.populationNodes.Length} nodes / " +
                $"{disk.populationEdges.Length} connections. BAKE SPAWNS ONLY for spawn changes; " +
                "use FULL MAP bake for geometry changes. Restart GameServer after publishing.", true);
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
            catch (Exception ex) { error = ex.Message; return false; }
        }

        [Serializable]
        private sealed class GraphPayload
        {
            public Game.Shared.Population.ServerPopulationRouteNode[] nodes;
            public Game.Shared.Population.ServerPopulationRouteEdge[] edges;
            public Game.Shared.Population.ServerPopulationPortal[] portals;
            public PopulationBakedGraphEdits edits;
        }

        private static string GraphContent(PopulationBakedGraphEditor.Layer layer) =>
            JsonUtility.ToJson(new GraphPayload
            {
                nodes = layer.populationNodes,
                edges = layer.populationEdges,
                portals = layer.populationPortals,
                edits = layer.editorEdits,
            });

        private bool PreflightCurrent(out Scene scene)
        {
            scene = SceneManager.GetActiveScene();
            if (PopulationBakedGraphEditor.HasUnsavedOpenGraph(scene, out string dirtyReason))
            {
                SetPublicationStatus("BAKE BLOCKED: " + dirtyReason + ". Click SAVE first.", false);
                return false;
            }
            if (!ConfirmSavedScene()) return false;
            return true;
        }

        private static bool TryPreflightFullPublication(Scene scene, out string detail)
        {
            detail = string.Empty;
            string routePath = PopulationBakedGraphEditor.RoutePath(scene);
            if (string.IsNullOrWhiteSpace(routePath) ||
                !routePath.EndsWith(".routes.json", StringComparison.OrdinalIgnoreCase))
            {
                detail = "No valid route-layer output path.";
                return false;
            }
            string stem = routePath.Substring(0, routePath.Length - ".routes.json".Length);
            // Probe all THREE existing publication targets before running expensive
            // map/NavMesh/shared-world generation. This catches the persistent
            // Windows lock that previously prevented File.Replace and rollback.
            foreach (string target in new[]
            {
                stem + ".servermap.json", stem + ".routes.json", stem + ".spawns.json"
            })
            {
                if (!File.Exists(target)) continue;
                try
                {
                    using FileStream exclusive = new FileStream(
                        target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    detail = $"Cannot safely publish locked file '{target}': {ex.Message}. " +
                        "Stop the process holding the file before baking.";
                    return false;
                }
            }
            return true;
        }

        private void BakeSpawnsOnly()
        {
            if (!PreflightCurrent(out Scene scene)) return;
            if (!ServerWorldSpawnLayerBake.TryBake(scene,
                    out int total, out int actors, out string detail))
            {
                SetPublicationStatus("SPAWN BAKE FAILED: " + detail, false);
                return;
            }
            RefreshSavedGraph(true);
            SetPublicationStatus($"SPAWNS VERIFIED: {total} anchors, {actors} enabled NPCs. " +
                "Map collision, NavMesh, and shared route nodes were not rebuilt. " +
                "Restart GameServer and confirm SPAWNING COMPLETE.", true);
        }

        private void AutoConnect()
        {
            if (!EditorUtility.DisplayDialog("AUTO CONNECT Routes",
                "Rebuild/add route authoring from the scene, preserve saved manual graph edits, " +
                "and then reconcile the spawn layer to the current enabled spawners and portals. " +
                "Obsolete saved spawn identities are removed after successful validation. " +
                "Click SAVE first if you have unsaved graph edits.",
                "AUTO CONNECT", "Cancel")) return;
            if (!PreflightCurrent(out Scene scene)) return;
            if (!TryPreflightFullPublication(scene, out string lockProblem))
            {
                SetPublicationStatus("AUTO CONNECT BLOCKED: " + lockProblem, false);
                return;
            }
            if (!ServerWorldBake.TryBakeCurrentScene(rebuildNavMesh, true,
                    out _, autoConnectRoutes: true))
            {
                SetPublicationStatus("AUTO CONNECT failed; see Console. Published files may be unchanged.", false);
                return;
            }
            if (!ServerWorldSpawnLayerBake.TryBake(scene,
                    out int total, out int actors, out string detail))
            {
                SetPublicationStatus("Routes baked but spawns were not reconciled: " + detail, false);
                return;
            }
            PopulationRouteGraphBuilder.ClearPreview(scene);
            PopulationBakedGraphEditor.RefreshOpenEditorsAfterBake(scene);
            RefreshSavedGraph(true);
            SetPublicationStatus($"AUTO CONNECT VERIFIED: {total} anchors, {actors} NPCs. " +
                "Restart GameServer to load new routes/spawns.", true);
        }

        private void BakeCurrent()
        {
            if (!PreflightCurrent(out Scene scene)) return;
            if (!TryPreflightFullPublication(scene, out string lockProblem))
            {
                SetPublicationStatus("FULL BAKE BLOCKED: " + lockProblem, false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer before, out string error))
            {
                SetPublicationStatus("BAKE BLOCKED: " + error, false);
                return;
            }
            string preservedRoutes = GraphContent(before);
            if (!ServerWorldBake.TryBakeCurrentScene(rebuildNavMesh, true,
                    out ServerWorldBake.Result mapResult))
            {
                SetPublicationStatus("FULL BAKE FAILED for '" + scene.name +
                    "'. Check Console and retained backups before restarting GameServer.", false);
                return;
            }
            if (!TryReadRouteLayer(scene, out PopulationBakedGraphEditor.Layer after, out error) ||
                !string.Equals(preservedRoutes, GraphContent(after), StringComparison.Ordinal))
            {
                SetPublicationStatus("BAKE VERIFICATION FAILED: Shared routes unexpectedly changed. " +
                    "Do not restart GameServer until layers are recovered. " + error, false);
                return;
            }
            if (!ServerWorldSpawnLayerBake.TryBake(scene,
                    out int total, out int actors, out string detail))
            {
                SetPublicationStatus("MAP BAKE completed but SPAWN reconciliation failed: " + detail +
                    ". Do not assume the intended NPC count is published.", false);
                return;
            }
            PopulationBakedGraphEditor.RefreshOpenEditorsAfterBake(scene);
            RefreshSavedGraph(true);
            SetPublicationStatus($"FULL BAKE VERIFIED: {after.populationNodes.Length} nodes / " +
                $"{after.populationEdges.Length} connections / {total} spawn anchors / {actors} NPCs. " +
                $"Map: {mapResult.OutputPath}. Restart GameServer.", true);
        }

        private void BakeEnabled()
        {
            if (!PreflightCurrent(out _)) return;
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh,
                    out List<ServerWorldBake.Result> results))
            {
                SetPublicationStatus("BAKE ENABLED SCENES failed; check Console.", false);
                return;
            }
            if (!ReconcileEnabledSceneSpawns(out string detail))
            {
                SetPublicationStatus("Map bakes completed but spawn reconciliation failed: " + detail, false);
                return;
            }
            RefreshSavedGraph(true);
            SetPublicationStatus($"BAKE VERIFIED for {results.Count} enabled scenes. " +
                "Spawn layers reconciled. Restart GameServer.", true);
        }

        private static bool ReconcileEnabledSceneSpawns(out string detail)
        {
            detail = string.Empty;
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
                {
                    if (!entry.enabled || string.IsNullOrWhiteSpace(entry.path)) continue;
                    Scene scene = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Single);
                    if (ServerWorldBake.FindMapInActiveScene() == null) continue;
                    if (!ServerWorldSpawnLayerBake.TryBake(scene,
                            out _, out _, out detail)) return false;
                }
                return true;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
            finally
            {
                try { EditorSceneManager.RestoreSceneManagerSetup(setup); }
                catch (Exception ex)
                { Debug.LogError("[World Bake] Could not restore original scene setup: " + ex); }
            }
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
                EditorGUILayout.HelpBox("No active Population spawns in the current scene.", MessageType.Info);
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
            EditorGUILayout.HelpBox("This scene has no ServerMap. It will be skipped by world bake.",
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
            if (!ConfirmSavedScene()) return;
            BuildClientPlayer(null);
        }

        private void BakeAndBuild()
        {
            if (!PreflightCurrent(out _)) return;
            if (!ServerWorldBake.BakeEnabledClientScenes(rebuildNavMesh,
                    out List<ServerWorldBake.Result> results)) return;
            if (!ReconcileEnabledSceneSpawns(out string detail))
            {
                SetPublicationStatus("BAKE + BUILD stopped: spawn reconciliation failed: " + detail, false);
                return;
            }
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
