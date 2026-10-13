using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Shared.Interactions;
using Game.Shared.Population;
using Game.Server.Application.World;
using Game.Shared.World;
using Game.Client.UI.Interactions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.WorldAuthoring.Editor
{
    public static class ServerWorldBake
    {
        public readonly struct Result
        {
            public readonly string MapId;
            public readonly string OutputPath;
            public readonly int Triangles;
            public readonly int Interactables;
            public readonly int Blockers;
            public readonly int Spawns;

            public Result(string mapId, string outputPath, int triangles, int interactables, int blockers, int spawns)
            {
                MapId = mapId;
                OutputPath = outputPath;
                Triangles = triangles;
                Interactables = interactables;
                Blockers = blockers;
                Spawns = spawns;
            }
        }

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string ServerMapsFolder => Path.Combine(ProjectRoot, "Server", "Content", "Maps");
        public static string ClientSharedWorldFolder => Path.Combine(Application.dataPath, "StreamingAssets", "MMOWorld");

        // Builds the same static support/collision world used by the authoritative bake
        // for the selected Spawner's green/red placement preview. This is an in-memory
        // query only: do not run the NavMesh bake, AUTO CONNECT, or publish any files.
        // Dynamic blocker state is intentionally absent, as in portal preflight.
        public static bool TryBuildSpawnPlacementWorld(
            Scene scene, out ServerMap map, out ServerCollisionWorld world, out string error)
        {
            map = null;
            world = null;
            error = string.Empty;

            if (!scene.IsValid() || !scene.isLoaded)
            {
                error = "Spawner placement preview requires a loaded scene.";
                return false;
            }

            ServerMap[] maps = Object.FindObjectsByType<ServerMap>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene)
                .ToArray();
            if (maps.Length != 1)
            {
                error = maps.Length == 0
                    ? "No ServerMap in this scene for placement checks."
                    : "Spawner placement preview requires exactly one ServerMap in this scene.";
                return false;
            }

            ServerMap candidate = maps[0];
            if (string.IsNullOrWhiteSpace(candidate.MapId))
            {
                error = "ServerMap has no Map ID for placement checks.";
                return false;
            }

            try
            {
                var triangles = new List<ServerCollisionTriangle>(8192);
                if (!ExportMovementGeometry(candidate, scene, triangles))
                {
                    error = "No valid ground geometry for spawn placement preview.";
                    return false;
                }

                ServerCollisionTriangle[] support = triangles.ToArray();
                if (candidate.ExportStaticCollision)
                    ExportStaticCollision(candidate, scene, triangles);

                var snapshot = new ServerMapSnapshot
                {
                    mapId = candidate.MapId,
                    instanceId = candidate.InstanceId,
                    movementTriangles = support,
                    collisionTriangles = triangles.ToArray(),
                    dynamicBlockers = Array.Empty<ServerDynamicBlocker>(),
                };
                ServerCollisionWorld previewWorld = new ServerCollisionWorld(snapshot);
                map = candidate;
                world = previewWorld;
                return true;
            }
            catch (Exception ex)
            {
                error = "Spawner placement collision preview failed: " + ex.Message;
                return false;
            }
        }

        public static bool TryBakeCurrentScene(bool rebuildNavMesh, bool saveScene, out Result result,
            bool autoConnectRoutes = false)
        {
            result = default;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("[Server World Bake] No loaded active scene.");
                return false;
            }

            ServerMap[] maps = Object.FindObjectsByType<ServerMap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene)
                .ToArray();

            if (maps.Length == 0)
            {
                Debug.LogWarning($"[Server World Bake] Scene '{scene.path}' has no ServerMap component; skipped.");
                return true;
            }
            if (maps.Length != 1)
            {
                Debug.LogError($"[Server World Bake] Scene '{scene.path}' must contain exactly one ServerMap component; found {maps.Length}.");
                return false;
            }

            ServerMap map = maps[0];
            if (string.IsNullOrWhiteSpace(map.MapId))
            {
                Debug.LogError($"[Server World Bake] Scene '{scene.path}' has an empty map ID.");
                return false;
            }

            // Spawn and Population authoring are separate layers. Their validation must
            // not prevent the physical world geometry from being published.

            // Rebuild an authored NavMesh when one exists. If this scene has no NavMesh
            // authoring setup at all, fall back to a temporary server-only walk bake below.
            if (rebuildNavMesh && map.BakeNavMesh)
                TryRebuildNavMesh(scene);

            var triangles = new List<ServerCollisionTriangle>(8192);
            if (!ExportMovementGeometry(map, scene, triangles))
                return false;

            // One authoritative scene-geometry support source for server and shared client world.
            // Unity NavMesh rebuilding above is optional and never gates ground export.
            ServerCollisionTriangle[] supportTriangles = triangles.ToArray();

            if (map.ExportStaticCollision)
                ExportStaticCollision(map, scene, triangles);

            // Reuse one immutable collision array in the validator and both output packages.
            ServerCollisionTriangle[] collisionTriangles = triangles.ToArray();

            if (!ExportInteractables(scene, out ServerWorldInteractableDefinition[] interactables, out ServerDynamicBlocker[] blockers))
                return false;

            // Validate Population portal traversal against the exact movement/static geometry
            // being baked. Dynamic door blockers are intentionally excluded here: the doorway
            // traversal contract represents the passable/open state, while runtime door state
            // remains authoritative.
            var portalValidationWorld = new ServerCollisionWorld(new ServerMapSnapshot
            {
                mapId = map.MapId,
                instanceId = map.InstanceId,
                movementTriangles = supportTriangles,
                collisionTriangles = collisionTriangles,
                dynamicBlockers = Array.Empty<ServerDynamicBlocker>(),
            });

            // Normal world bakes reuse the SAVED route/spawn graph. Only an explicit
            // Auto Connect regenerates it. Both modes publish coherent layer hashes.
            string output = OutputPath(map.MapId, map.InstanceId);
            if (!TryPrepareAuthoringLayers(scene, map, portalValidationWorld, output,
                    autoConnectRoutes, out RouteLayerFile preparedRoutes, out SpawnLayerFile preparedSpawns))
                return false;

            // The navmesh and shared-world files are also referenced by the core.
            // Restore their prior versions if anything after this point fails.
            using BakeDependencyBackup dependentFiles = BakeDependencyBackup.TryCreate(map);
            if (dependentFiles == null)
                return false;

            if (!TryBakeServerNavMesh(scene, map, out ServerNavMeshInfo navMesh))
                return false;

            Directory.CreateDirectory(ServerMapsFolder);
            long revision = Math.Max(1, ReadPreviousRevision(output) + 1);
            long bakedUtcTicks = DateTime.UtcNow.Ticks;

            if (!TryWriteSharedWorld(
                    map,
                    revision,
                    bakedUtcTicks,
                    supportTriangles,
                    collisionTriangles,
                    blockers,
                    interactables,
                    out ServerSharedWorldInfo sharedWorld))
            {
                return false;
            }

            var snapshot = new ServerMapSnapshot
            {
                formatVersion = ServerMapFormat.Version,
                layerFormatVersion = 1,
                mapId = map.MapId,
                instanceId = map.InstanceId,
                bakeRevision = revision,
                bakedUtcTicks = bakedUtcTicks,
                sourceScene = scene.path ?? string.Empty,
                mapKind = map.MapKind,
                worldRules = CloneWorldRules(map.WorldRules),
                propertyTemplate = map.MapKind == ServerMapKind.PropertyTemplate
                    ? CloneProperty(map.PropertyTemplate, map.MapId)
                    : null,
                navMesh = navMesh,
                sharedWorld = sharedWorld,
                movementTriangles = supportTriangles,
                collisionTriangles = collisionTriangles,
                dynamicBlockers = blockers,
                traversalLinks = Array.Empty<ServerTraversalLink>(),
                // Separate layer files are composed by the standalone loader.
                spawnAnchors = Array.Empty<ServerSpawnAnchor>(),
                interactables = interactables,
                populationNodes = Array.Empty<ServerPopulationRouteNode>(),
                populationEdges = Array.Empty<ServerPopulationRouteEdge>(),
                populationPortals = Array.Empty<ServerPopulationPortal>(),
            };

            snapshot.contentHash = string.Empty;
            string unhashed = JsonUtility.ToJson(snapshot, false);
            snapshot.contentHash = Sha256(unhashed);
            string json = JsonUtility.ToJson(snapshot, false);
            preparedRoutes.coreContentHash = snapshot.contentHash;
            preparedSpawns.coreContentHash = snapshot.contentHash;
            if (!TryPublishCoreAndLayers(output, json, preparedRoutes, preparedSpawns))
                return false;
            dependentFiles.Commit();
            int layerSpawnCount = preparedSpawns.spawnAnchors.Length;

            if (saveScene && scene.isDirty)
                EditorSceneManager.SaveScene(scene);

            result = new Result(map.MapId, output, triangles.Count, interactables.Length, blockers.Length, layerSpawnCount);
            Debug.Log(
                $"[Server World Bake] {map.MapId} -> {output}\n" +
                $"  triangles={triangles.Count:n0}, interactables={interactables.Length:n0}, blockers={blockers.Length:n0}, spawns={layerSpawnCount:n0}, revision={snapshot.bakeRevision}\n" +
                $"  routes/spawns={(autoConnectRoutes ? "explicit auto connect" : "preserved saved graph")}, see layer logs\n" +
                $"  serverNavMesh={navMesh.fileName}, hash={ShortHash(navMesh.contentHash)}\n" +
                $"  sharedWorld={sharedWorld.fileName}, hash={ShortHash(sharedWorld.contentHash)}");
            return true;
        }

        // Read core identity/revision without materializing the full baked geometry.
        [Serializable]
        private sealed class CoreHeader
        {
            public string contentHash;
            public long bakeRevision;
        }

        [Serializable]
        private sealed class RouteLayerFile
        {
            public int formatVersion = 1;
            public string mapId;
            public string instanceId;
            public string coreContentHash;
            public ServerPopulationRouteNode[] populationNodes;
            public ServerPopulationRouteEdge[] populationEdges;
            public ServerPopulationPortal[] populationPortals;
            public PopulationBakedGraphEdits editorEdits = new PopulationBakedGraphEdits();
        }

        [Serializable]
        private sealed class SpawnLayerFile
        {
            public int formatVersion = 1;
            public string mapId;
            public string instanceId;
            public string coreContentHash;
            public ServerSpawnAnchor[] spawnAnchors;
        }

        // Keep binary/nav packages coherent with the core on any failed publication.
        // These are file backups only during the bake; no extra gameplay data or
        // network protocol is introduced.
        private sealed class BakeDependencyBackup : IDisposable
        {
            private readonly string directory;
            private readonly string[] files;
            private readonly bool[] existed;
            private bool committed;

            private BakeDependencyBackup(ServerMap map)
            {
                string stem = MapFileStem(map.MapId, map.InstanceId);
                files = new[]
                {
                    Path.Combine(ServerMapsFolder, stem + ".servernavmesh.bin"),
                    Path.Combine(ServerMapsFolder, stem + ".sharedworld.json"),
                    Path.Combine(ClientSharedWorldFolder, stem + ".sharedworld.json"),
                    Path.Combine(ServerMapsFolder, stem + ".sharedworld.json.gz"),
                    Path.Combine(ClientSharedWorldFolder, stem + ".sharedworld.json.gz"),
                };
                existed = new bool[files.Length];
                directory = Path.Combine(ProjectRoot, "Library", "MMO World Bake Rollback",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    for (int i = 0; i < files.Length; ++i)
                    {
                        existed[i] = File.Exists(files[i]);
                        if (existed[i]) File.Copy(files[i], Path.Combine(directory, i.ToString()), false);
                    }
                }
                catch
                {
                    try { Directory.Delete(directory, true); } catch { }
                    throw;
                }
            }

            public static BakeDependencyBackup TryCreate(ServerMap map)
            {
                try { return new BakeDependencyBackup(map); }
                catch (Exception ex)
                {
                    Debug.LogError($"[Server World Bake] Unable to back up dependent world files; bake cancelled: {ex}");
                    return null;
                }
            }

            public void Commit()
            {
                committed = true;
                // Delete old uncompressed copies after a complete successful publication.
                for (int i = 0; i < files.Length; ++i)
                {
                    if (!files[i].EndsWith(".sharedworld.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        if (File.Exists(files[i]))
                            File.Delete(files[i]);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Server World Bake] Could not remove obsolete package '{files[i]}': {ex.Message}");
                    }
                }
            }

            public void Dispose()
            {
                if (!committed)
                {
                    for (int i = 0; i < files.Length; ++i)
                    {
                        try
                        {
                            if (existed[i]) File.Copy(Path.Combine(directory, i.ToString()), files[i], true);
                            else if (File.Exists(files[i])) File.Delete(files[i]);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[Server World Bake] CRITICAL: Could not restore dependent world file '{files[i]}': {ex}");
                        }
                    }
                }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (Exception ex) { Debug.LogWarning($"[Server World Bake] World-bake backup cleanup failed: {ex.Message}"); }
            }
        }

        // Normal world bakes are never an implicit route-regeneration operation.
        // Read the existing authored layers, then update only their core hash when
        // publishing. Saved connections, portal identities, and spawn anchors stay put.
        private static bool TryReadSavedLayers(
            ServerMap map, string corePath, out RouteLayerFile routes, out SpawnLayerFile spawns)
        {
            routes = null;
            spawns = null;
            string stem = corePath.EndsWith(".servermap.json", StringComparison.OrdinalIgnoreCase)
                ? corePath.Substring(0, corePath.Length - ".servermap.json".Length)
                : Path.Combine(Path.GetDirectoryName(corePath), Path.GetFileNameWithoutExtension(corePath));
            string routesPath = stem + ".routes.json";
            string spawnsPath = stem + ".spawns.json";
            if (!File.Exists(routesPath) || !File.Exists(spawnsPath))
            {
                Debug.LogError("[Server World Bake] No complete saved Population graph for this map. " +
                    "Open Spawns / Routes and run AUTO CONNECT once to create it. " +
                    "Existing maps will NOT silently regenerate their routes.");
                return false;
            }
            try
            {
                routes = JsonUtility.FromJson<RouteLayerFile>(File.ReadAllText(routesPath));
                spawns = JsonUtility.FromJson<SpawnLayerFile>(File.ReadAllText(spawnsPath));
                if (routes == null || spawns == null || routes.formatVersion != 1 ||
                    spawns.formatVersion != 1 ||
                    !string.Equals(routes.mapId, map.MapId, StringComparison.Ordinal) ||
                    !string.Equals(spawns.mapId, map.MapId, StringComparison.Ordinal) ||
                    !string.Equals(routes.instanceId ?? string.Empty, map.InstanceId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(spawns.instanceId ?? string.Empty, map.InstanceId ?? string.Empty, StringComparison.Ordinal) ||
                    routes.populationNodes == null || routes.populationEdges == null ||
                    routes.populationPortals == null || spawns.spawnAnchors == null)
                    throw new InvalidDataException("Saved route/spawn layers have a mismatched identity, format, or missing arrays.");

                // A stale/mixed pair must not be blessed with the new core hash.
                if (!File.Exists(corePath))
                    throw new FileNotFoundException("Prior servermap core is missing; saved routes cannot be verified.", corePath);
                CoreHeader previous = JsonUtility.FromJson<CoreHeader>(File.ReadAllText(corePath));
                if (previous == null || string.IsNullOrWhiteSpace(previous.contentHash) ||
                    !string.Equals(routes.coreContentHash, previous.contentHash, StringComparison.Ordinal) ||
                    !string.Equals(spawns.coreContentHash, previous.contentHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Saved route/spawn hashes do not match the existing servermap core.");

                Debug.Log($"[Server World Bake] Reusing saved Population graph, " +
                    $"no AUTO CONNECT: nodes={routes.populationNodes.Length}, " +
                    $"edges={routes.populationEdges.Length}, portals={routes.populationPortals.Length}, " +
                    $"spawns={spawns.spawnAnchors.Length}. No paths were regenerated or pruned.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Server World Bake] Saved Population graph is not safe to republish: {ex.Message} " +
                    "No world files were changed. Restore the matching saved map files; do not AUTO CONNECT over them.");
                routes = null;
                spawns = null;
                return false;
            }
        }

        // Explicit AUTO CONNECT may add routes, but it must never silently delete
        // published connections, portals or spawn anchors. Fail before publication;
        // EDIT + SAVE is the explicit route-removal workflow.
        private static bool CheckExistingGraphNotReduced(
            RouteLayerFile previousRoutes, SpawnLayerFile previousSpawns,
            RouteLayerFile nextRoutes, SpawnLayerFile nextSpawns)
        {
            string EdgeKey(ServerPopulationRouteEdge edge)
            {
                long a = edge.fromNodeId, b = edge.toNodeId;
                return edge.oneWay ? $"D:{a}:{b}" :
                    a < b ? $"B:{a}:{b}" : $"B:{b}:{a}";
            }
            var nextNodes = new HashSet<long>(nextRoutes.populationNodes
                .Where(x => x != null).Select(x => x.stableId));
            long[] missingNodes = previousRoutes.populationNodes
                .Where(x => x != null && !nextNodes.Contains(x.stableId))
                .Select(x => x.stableId).Distinct().Take(6).ToArray();
            var nextEdges = new HashSet<string>(nextRoutes.populationEdges
                .Where(x => x != null).Select(EdgeKey), StringComparer.Ordinal);
            string[] missingEdges = previousRoutes.populationEdges
                .Where(x => x != null).Select(EdgeKey)
                .Where(x => !nextEdges.Contains(x)).Distinct().Take(6).ToArray();
            var nextPortalIds = new HashSet<long>(nextRoutes.populationPortals
                .Where(x => x != null).Select(x => x.stableId));
            long[] missingPortals = previousRoutes.populationPortals
                .Where(x => x != null && !nextPortalIds.Contains(x.stableId))
                .Select(x => x.stableId).Distinct().Take(6).ToArray();
            var nextSpawnIds = new HashSet<long>(nextSpawns.spawnAnchors
                .Where(x => x != null).Select(x => x.stableId));
            long[] missingSpawns = previousSpawns.spawnAnchors
                .Where(x => x != null && !nextSpawnIds.Contains(x.stableId))
                .Select(x => x.stableId).Distinct().Take(6).ToArray();
            if (missingNodes.Length == 0 && missingEdges.Length == 0 &&
                missingPortals.Length == 0 && missingSpawns.Length == 0)
                return true;
            Debug.LogError("[Population AUTO CONNECT] REFUSED: the proposed regeneration " +
                "would remove saved content. None of the new route/spawn/core files were published. " +
                $"Missing node IDs: {string.Join(", ", missingNodes)}; " +
                $"edge examples: {string.Join(", ", missingEdges)}; " +
                $"portal IDs: {string.Join(", ", missingPortals)}; " +
                $"spawn IDs: {string.Join(", ", missingSpawns)}. " +
                "Edit or remove old routes explicitly in EDIT, then SAVE, before retrying.");
            return false;
        }

        // Preflight returns either the saved graph or an explicitly regenerated one.
        private static bool TryPrepareAuthoringLayers(
            Scene scene, ServerMap map, ServerCollisionWorld validationWorld,
            string corePath, bool autoConnectRoutes,
            out RouteLayerFile routes, out SpawnLayerFile spawns)
        {
            routes = null;
            spawns = null;
            if (!autoConnectRoutes)
                return TryReadSavedLayers(map, corePath, out routes, out spawns);

            string stem = corePath.EndsWith(".servermap.json", StringComparison.OrdinalIgnoreCase)
                ? corePath.Substring(0, corePath.Length - ".servermap.json".Length)
                : Path.Combine(Path.GetDirectoryName(corePath), Path.GetFileNameWithoutExtension(corePath));
            string routesPath = stem + ".routes.json";
            bool priorExists = File.Exists(routesPath) || File.Exists(stem + ".spawns.json");
            RouteLayerFile oldRoutes = null;
            SpawnLayerFile oldSpawns = null;
            if (priorExists && !TryReadSavedLayers(map, corePath, out oldRoutes, out oldSpawns))
                return false;
            try
            {
                if (!ExportPopulation(scene, map, validationWorld,
                        out ServerPopulationRouteNode[] nodes,
                        out ServerPopulationRouteEdge[] edges,
                        out ServerPopulationPortal[] portals))
                {
                    Debug.LogError("[Server World Bake] AUTO CONNECT failed route preflight; saved graph unchanged.");
                    return false;
                }
                PopulationBakedGraphEdits edits = PopulationBakedGraphEditor.ReadEdits(
                    routesPath, map.MapId, map.InstanceId);
                if (!PopulationBakedGraphEditor.ApplyEdits(
                        scene, validationWorld, edits, ref nodes, ref edges, portals))
                {
                    Debug.LogError("[Server World Bake] AUTO CONNECT failed edited-route validation; saved graph unchanged.");
                    return false;
                }
                portals = FilterPopulationPortals(scene, validationWorld, nodes, edges, portals);
                if (!ExportSpawns(scene, map, validationWorld, nodes, edges, portals,
                        out ServerSpawnAnchor[] anchors))
                {
                    Debug.LogError("[Server World Bake] AUTO CONNECT failed spawn preflight; saved graph unchanged.");
                    return false;
                }
                routes = new RouteLayerFile
                {
                    mapId = map.MapId,
                    instanceId = map.InstanceId,
                    populationNodes = nodes,
                    populationEdges = edges,
                    populationPortals = portals,
                    editorEdits = edits,
                };
                spawns = new SpawnLayerFile
                {
                    mapId = map.MapId,
                    instanceId = map.InstanceId,
                    spawnAnchors = anchors,
                };
                if (oldRoutes != null)
                {
                    if (!CheckExistingGraphNotReduced(oldRoutes, oldSpawns, routes, spawns))
                        return false;
                    // AUTO CONNECT adds connections. It must not rewrite saved link
                    // width/weight/direction or validation metadata for existing links.
                    string Key(ServerPopulationRouteEdge e)
                    {
                        long a = e.fromNodeId, b = e.toNodeId;
                        return e.oneWay ? $"D:{a}:{b}" :
                            a < b ? $"B:{a}:{b}" : $"B:{b}:{a}";
                    }
                    var oldEdges = oldRoutes.populationEdges.Where(x => x != null)
                        .GroupBy(Key).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
                    for (int i = 0; i < routes.populationEdges.Length; ++i)
                    {
                        ServerPopulationRouteEdge edge = routes.populationEdges[i];
                        if (edge != null && oldEdges.TryGetValue(Key(edge), out ServerPopulationRouteEdge saved))
                            routes.populationEdges[i] = saved;
                    }
                }
                Debug.Log($"[Population AUTO CONNECT] Prepared nodes={nodes.Length}, " +
                    $"edges={edges.Length}, portals={portals.Length}, spawns={anchors.Length}. " +
                    "Previous published route IDs were protected.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Server World Bake] AUTO CONNECT preflight failed; existing graph unchanged: {ex}");
                return false;
            }
        }

        // Publish the three interdependent JSON files as one checked operation.
        // Stage every file first; if a replacement fails, restore all previously
        // replaced files. Core goes LAST so its hash never precedes its layers.
        private static bool TryPublishCoreAndLayers(
            string corePath, string coreJson, RouteLayerFile routes, SpawnLayerFile spawns)
        {
            string stem = corePath.EndsWith(".servermap.json", StringComparison.OrdinalIgnoreCase)
                ? corePath.Substring(0, corePath.Length - ".servermap.json".Length)
                : Path.Combine(Path.GetDirectoryName(corePath), Path.GetFileNameWithoutExtension(corePath));
            string[] destinations = { stem + ".routes.json", stem + ".spawns.json", corePath };
            string[] payloads =
            {
                JsonUtility.ToJson(routes, true),
                JsonUtility.ToJson(spawns, true),
                coreJson,
            };
            string token = Guid.NewGuid().ToString("N");
            string[] temps = destinations.Select(x => x + "." + token + ".tmp").ToArray();
            string[] backups = destinations.Select(x => x + "." + token + ".bak").ToArray();
            bool[] existed = destinations.Select(File.Exists).ToArray();
            bool keepBackups = false;
            try
            {
                var utf8 = new UTF8Encoding(false);
                // Stage all three new payloads and preserve all three old versions
                // before changing ANY published file. This also protects the edit
                // record in an existing routes layer.
                for (int i = 0; i < destinations.Length; ++i)
                {
                    File.WriteAllText(temps[i], payloads[i], utf8);
                    if (existed[i]) File.Copy(destinations[i], backups[i], false);
                }
                for (int i = 0; i < destinations.Length; ++i)
                {
                    if (existed[i]) File.Replace(temps[i], destinations[i], null);
                    else File.Move(temps[i], destinations[i]);
                }
                Debug.Log($"[Server World Bake] Routes layer -> {destinations[0]}: " +
                    $"{routes.populationNodes.Length} nodes, {routes.populationEdges.Length} edges (baked edits retained)");
                Debug.Log($"[Server World Bake] Spawns layer -> {destinations[1]}: " +
                    $"{spawns.spawnAnchors.Length} anchors; matching core geometry hash published.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Server World Bake] Failed to publish core and authoring layers together: {ex}");
                // Restore ALL destinations. A Replace/Move may have changed a file
                // before reporting an error; do not rely on a committed counter.
                for (int i = destinations.Length - 1; i >= 0; --i)
                {
                    try
                    {
                        if (existed[i] && File.Exists(backups[i]))
                            File.Copy(backups[i], destinations[i], true);
                        else if (!existed[i] && File.Exists(destinations[i]))
                            File.Delete(destinations[i]);
                    }
                    catch (Exception rollbackError)
                    {
                        keepBackups = true;
                        Debug.LogError($"[Server World Bake] CRITICAL: Could not restore {destinations[i]}; backup retained at {backups[i]}: {rollbackError}");
                    }
                }
                return false;
            }
            finally
            {
                foreach (string path in temps)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception cleanupError) { Debug.LogWarning($"[Server World Bake] Could not remove temporary file '{path}': {cleanupError.Message}"); }
                }
                if (!keepBackups)
                {
                    foreach (string path in backups)
                    {
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (Exception cleanupError) { Debug.LogWarning($"[Server World Bake] Could not remove backup '{path}': {cleanupError.Message}"); }
                    }
                }
            }
        }

        public static bool BakeEnabledClientScenes(bool rebuildNavMesh, out List<Result> results)
        {
            results = new List<Result>();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            SceneSetup[] restore = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
                for (int i = 0; i < scenes.Length; ++i)
                {
                    EditorBuildSettingsScene buildScene = scenes[i];
                    if (!buildScene.enabled || string.IsNullOrWhiteSpace(buildScene.path))
                        continue;
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(buildScene.path) == null)
                        continue;

                    EditorSceneManager.OpenScene(buildScene.path, OpenSceneMode.Single);
                    ServerMap map = FindMapInActiveScene();
                    if (map == null)
                        continue;

                    if (!TryBakeCurrentScene(rebuildNavMesh, true, out Result baked))
                        return false;
                    results.Add(baked);
                }
            }
            finally
            {
                if (restore != null && restore.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(restore);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }

        public static ServerMap FindMapInActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            ServerMap[] maps = Object.FindObjectsByType<ServerMap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < maps.Length; ++i)
                if (maps[i] != null && maps[i].gameObject.scene == scene)
                    return maps[i];
            return null;
        }

        public static bool TryRebuildNavMesh(Scene scene)
        {
            try
            {
                // Prefer AI Navigation's NavMeshSurface if the package is installed.
                Type surfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
                if (surfaceType != null)
                {
                    MethodInfo build = surfaceType.GetMethod("BuildNavMesh", BindingFlags.Public | BindingFlags.Instance);
                    if (build != null)
                    {
                        Object[] surfaces = Resources.FindObjectsOfTypeAll(surfaceType);
                        int count = 0;
                        for (int i = 0; i < surfaces.Length; ++i)
                        {
                            if (!(surfaces[i] is Component component) || component.gameObject.scene != scene)
                                continue;
                            build.Invoke(surfaces[i], null);
                            ++count;
                        }
                        if (count > 0)
                        {
                            Debug.Log($"[Server World Bake] Rebuilt {count} NavMeshSurface component(s) in '{scene.name}'.");
                            return true;
                        }
                    }
                }

                // Fallback for projects using Unity's classic Navigation bake.
                Type editorBuilder = Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEditor.AIModule") ??
                    Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEditor");
                MethodInfo classicBuild = editorBuilder?.GetMethod("BuildNavMesh", BindingFlags.Public | BindingFlags.Static);
                if (classicBuild != null)
                {
                    classicBuild.Invoke(null, null);
                    Debug.Log($"[Server World Bake] Rebuilt classic NavMesh in '{scene.name}'.");
                    return true;
                }

                Debug.Log(
                    "[Server World Bake] No authored NavMesh rebuild API was found. " +
                    "The automatic temporary walk-surface bake will be used if the scene has no triangulation.");
                return false;
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogError($"[Server World Bake] NavMesh bake failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Server World Bake] NavMesh bake failed: {ex.Message}");
                return false;
            }
        }

        private static bool HasNavMeshTriangles()
        {
            NavMeshTriangulation nav = NavMesh.CalculateTriangulation();
            return nav.vertices != null && nav.indices != null && nav.indices.Length >= 3;
        }

        private static bool TryBuildTemporaryWalkSurface(
            Scene scene,
            ServerMap map,
            out NavMeshData data,
            out NavMeshDataInstance instance)
        {
            data = null;
            instance = default;

            try
            {
                if (NavMesh.GetSettingsCount() <= 0)
                {
                    Debug.LogError(
                        "[Server World Bake] Unity has no NavMesh agent settings. " +
                        "Create at least one Navigation Agent Type in Project Settings, then bake again.");
                    return false;
                }

                var sources = new List<NavMeshBuildSource>(4096);
                bool hasBounds = false;
                Bounds bounds = default;

                MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

                for (int i = 0; i < renderers.Length; ++i)
                {
                    MeshRenderer renderer = renderers[i];
                    if (!renderer.enabled || !IsWalkBakeObject(renderer, scene, map))
                        continue;

                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null)
                        continue;

                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Mesh,
                        sourceObject = mesh,
                        transform = renderer.localToWorldMatrix,
                        area = ResolveWalkArea(renderer.transform),
                    });
                    Encapsulate(ref bounds, ref hasBounds, renderer.bounds);
                }

                Terrain[] terrains = Object.FindObjectsByType<Terrain>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

                for (int i = 0; i < terrains.Length; ++i)
                {
                    Terrain terrain = terrains[i];
                    if (!terrain.enabled || !IsWalkBakeObject(terrain, scene, map) || terrain.terrainData == null)
                        continue;

                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Terrain,
                        sourceObject = terrain.terrainData,
                        transform = Matrix4x4.TRS(terrain.transform.position, Quaternion.identity, Vector3.one),
                        area = ResolveWalkArea(terrain.transform),
                    });

                    Vector3 size = terrain.terrainData.size;
                    Bounds terrainBounds = new Bounds(
                        terrain.transform.position + size * 0.5f,
                        size);
                    Encapsulate(ref bounds, ref hasBounds, terrainBounds);
                }

                // Include simple collider-only level geometry when it has no renderer source.
                // This supports invisible floors/ramps and mixed mesh/terrain projects without
                // forcing a NavMeshSurface component into every authored scene.
                Collider[] colliders = Object.FindObjectsByType<Collider>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

                for (int i = 0; i < colliders.Length; ++i)
                {
                    Collider collider = colliders[i];
                    if (!collider.enabled || !IsWalkBakeObject(collider, scene, map) || collider.isTrigger)
                        continue;
                    if (collider.GetComponent<MeshRenderer>() != null ||
                        collider.GetComponent<Terrain>() != null)
                        continue;

                    int area = ResolveWalkArea(collider.transform);
                    NavMeshBuildSource source;

                    if (collider is BoxCollider box)
                    {
                        source = new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Box,
                            transform = Matrix4x4.TRS(
                                box.transform.TransformPoint(box.center),
                                box.transform.rotation,
                                box.transform.lossyScale),
                            size = box.size,
                            area = area,
                        };
                    }
                    else if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                    {
                        source = new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Mesh,
                            sourceObject = meshCollider.sharedMesh,
                            transform = meshCollider.transform.localToWorldMatrix,
                            area = area,
                        };
                    }
                    else
                    {
                        continue;
                    }

                    sources.Add(source);
                    Encapsulate(ref bounds, ref hasBounds, collider.bounds);
                }

                if (sources.Count == 0 || !hasBounds)
                {
                    Debug.LogError(
                        $"[Server World Bake] Map '{map.MapId}' has no usable walk-bake geometry. " +
                        "The automatic bake accepts active MeshRenderers, Terrain, BoxColliders, and MeshColliders " +
                        "on the ServerMap collision layers. ServerBakeIgnore objects are excluded.");
                    return false;
                }

                // Give the builder some vertical/horizontal breathing room at scene edges.
                bounds.Expand(new Vector3(4f, 20f, 4f));

                NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(0);

                // Do not inherit an arbitrary project's first NavMesh agent dimensions.
                // The exported walk surface must match the standalone server's standard
                // humanoid motor or the bake can be eroded far more than server movement
                // expects around ramps, curbs, walls, and narrow passages.
                settings.agentRadius = 0.35f;
                settings.agentHeight = 1.80f;
                settings.agentClimb = 0.40f;
                settings.agentSlope = 50f;

                // Finer than Unity's usual radius/3 rule so curb/ramp joins retain enough
                // detail for server grounding without turning this into pathfinding data.
                settings.overrideVoxelSize = true;
                settings.voxelSize = 0.0875f;

                Debug.Log(
                    $"[Server World Bake] Temporary walk agent for '{map.MapId}': " +
                    $"radius={settings.agentRadius:0.###}m, " +
                    $"height={settings.agentHeight:0.###}m, " +
                    $"climb={settings.agentClimb:0.###}m, " +
                    $"slope={settings.agentSlope:0.#}°, " +
                    $"voxel={settings.voxelSize:0.####}m.");

                data = NavMeshBuilder.BuildNavMeshData(
                    settings,
                    sources,
                    bounds,
                    Vector3.zero,
                    Quaternion.identity);

                if (data == null)
                {
                    Debug.LogError(
                        $"[Server World Bake] Automatic walk-surface bake produced no NavMeshData for '{map.MapId}'. " +
                        "Check that the scene contains floor/street geometry large enough for the default NavMesh agent.");
                    return false;
                }

                data.name = $"ServerWalk_{map.MapId}_Temporary";
                instance = NavMesh.AddNavMeshData(data);

                if (!instance.valid || !HasNavMeshTriangles())
                {
                    if (instance.valid)
                        instance.Remove();
                    Object.DestroyImmediate(data);
                    data = null;
                    instance = default;

                    Debug.LogError(
                        $"[Server World Bake] Automatic walk-surface bake for '{map.MapId}' contained no walkable triangles. " +
                        "Common causes are geometry smaller than the current NavMesh agent radius, excessive slope, " +
                        "or the scene objects being excluded by ServerMap collision layers.");
                    return false;
                }

                Debug.Log(
                    $"[Server World Bake] '{map.MapId}' had no authored NavMesh. " +
                    $"Built a temporary server walk surface from {sources.Count:n0} scene geometry source(s). " +
                    "No NavMeshSurface component or saved NavMesh asset was required.");
                return true;
            }
            catch (Exception ex)
            {
                if (instance.valid)
                    instance.Remove();
                if (data != null)
                    Object.DestroyImmediate(data);
                data = null;
                instance = default;
                Debug.LogError($"[Server World Bake] Automatic walk-surface bake failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsWalkBakeObject(Component component, Scene scene, ServerMap map)
        {
            if (component == null || component.gameObject.scene != scene)
                return false;
            if (!component.gameObject.activeInHierarchy)
                return false;
            if ((map.CollisionLayers.value & (1 << component.gameObject.layer)) == 0)
                return false;
            if (component.GetComponentInParent<ServerBakeIgnore>() != null)
                return false;

            // Fixed and dynamic interactables are represented independently by collision /
            // blocker data. Excluding their visual meshes from the walk bake prevents the top
            // of benches, tables, beds, etc. from accidentally becoming valid standing ground.
            if (component.GetComponentInParent<WorldInteractable>() != null)
                return false;

            return true;
        }

        private static int ResolveWalkArea(Transform transform)
        {
            ServerSurface surface = transform != null ? transform.GetComponentInParent<ServerSurface>() : null;
            if (surface == null)
                return 0;

            ServerSurfaceFlags flags = surface.flags;
            string areaName = null;
            if ((flags & ServerSurfaceFlags.Water) != 0) areaName = "Water";
            else if ((flags & ServerSurfaceFlags.Road) != 0) areaName = "Road";
            else if ((flags & ServerSurfaceFlags.Sidewalk) != 0) areaName = "Sidewalk";
            else if ((flags & ServerSurfaceFlags.Interior) != 0) areaName = "Interior";
            else if ((flags & ServerSurfaceFlags.Roof) != 0) areaName = "Roof";

            if (!string.IsNullOrWhiteSpace(areaName))
            {
                int area = NavMesh.GetAreaFromName(areaName);
                if (area >= 0)
                    return area;
            }

            return 0;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool initialized, Bounds next)
        {
            if (!initialized)
            {
                bounds = next;
                initialized = true;
                return;
            }

            bounds.Encapsulate(next.min);
            bounds.Encapsulate(next.max);
        }

        [Serializable]
        private sealed class ServerNavSource
        {
            public int schemaVersion = 1;
            public string mapId = string.Empty;
            public float[] vertices = Array.Empty<float>();
            public int[] indices = Array.Empty<int>();
            public ServerNavSettings settings = new ServerNavSettings();
        }

        [Serializable]
        private sealed class ServerNavSettings
        {
            public float cellSize = 0.20f;
            public float cellHeight = 0.10f;
            public int tileSize = 128;
            public float agentHeight = 1.80f;
            public float agentRadius = 0.35f;
            public float agentMaxClimb = 0.40f;
            public float agentMaxSlope = 50f;
            public int minRegionSize = 8;
            public int mergedRegionSize = 20;
            public float edgeMaxLen = 12f;
            public float edgeMaxError = 1.3f;
            public int vertsPerPoly = 6;
            public float detailSampleDist = 6f;
            public float detailSampleMaxError = 1f;
        }

        private readonly struct SharedWorldVertexKey : IEquatable<SharedWorldVertexKey>, IComparable<SharedWorldVertexKey>
        {
            private const float Quantization = 1000f; // millimetre-stable bake key

            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public SharedWorldVertexKey(float x, float y, float z)
            {
                X = Mathf.RoundToInt(x * Quantization);
                Y = Mathf.RoundToInt(y * Quantization);
                Z = Mathf.RoundToInt(z * Quantization);
            }

            public int CompareTo(SharedWorldVertexKey other)
            {
                int x = X.CompareTo(other.X);
                if (x != 0) return x;
                int y = Y.CompareTo(other.Y);
                return y != 0 ? y : Z.CompareTo(other.Z);
            }

            public bool Equals(SharedWorldVertexKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is SharedWorldVertexKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = X;
                    hash = (hash * 397) ^ Y;
                    hash = (hash * 397) ^ Z;
                    return hash;
                }
            }

            public override string ToString() => $"{X},{Y},{Z}";
        }

        private readonly struct SharedWorldEdgeKey : IEquatable<SharedWorldEdgeKey>
        {
            public readonly SharedWorldVertexKey A;
            public readonly SharedWorldVertexKey B;

            public SharedWorldEdgeKey(Vector3 a, Vector3 b)
            {
                var first = new SharedWorldVertexKey(a.x, a.y, a.z);
                var second = new SharedWorldVertexKey(b.x, b.y, b.z);
                if (first.CompareTo(second) <= 0)
                {
                    A = first;
                    B = second;
                }
                else
                {
                    A = second;
                    B = first;
                }
            }

            public bool Equals(SharedWorldEdgeKey other) => A.Equals(other.A) && B.Equals(other.B);
            public override bool Equals(object obj) => obj is SharedWorldEdgeKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (A.GetHashCode() * 397) ^ B.GetHashCode(); }
            }

            public override string ToString() => $"{A}|{B}";
        }

        private sealed class SharedWorldEdgeAccumulator
        {
            public int Count;
            public Vector3 A;
            public Vector3 B;
            public Vector3 SurfaceNormal;
            public ServerSurfaceFlags Flags;
        }

        private static bool TryWriteSharedWorld(
            ServerMap map,
            long revision,
            long bakedUtcTicks,
            ServerCollisionTriangle[] supportTriangles,
            ServerCollisionTriangle[] collisionTriangles,
            ServerDynamicBlocker[] blockers,
            ServerWorldInteractableDefinition[] interactables,
            out ServerSharedWorldInfo info)
        {
            info = null;
            try
            {
                string mapStem = MapFileStem(map.MapId, map.InstanceId);
                string fileName = mapStem + ".sharedworld.json.gz";
                string serverPath = Path.Combine(ServerMapsFolder, fileName);
                string clientPath = Path.Combine(ClientSharedWorldFolder, fileName);

                Directory.CreateDirectory(ServerMapsFolder);
                Directory.CreateDirectory(ClientSharedWorldFolder);

                var shared = new SharedWorldSnapshot
                {
                    formatVersion = SharedWorldFormat.Version,
                    mapId = ServerMapId.Normalize(map.MapId),
                    instanceId = ServerMapId.Normalize(map.InstanceId),
                    bakeRevision = revision,
                    bakedUtcTicks = bakedUtcTicks,
                    contentHash = string.Empty,
                    supportTriangles = supportTriangles ?? Array.Empty<ServerCollisionTriangle>(),
                    surfaceEdges = BuildSharedWorldBoundaryEdges(map.MapId, supportTriangles),
                    collisionTriangles = collisionTriangles ?? Array.Empty<ServerCollisionTriangle>(),
                    dynamicBlockers = blockers ?? Array.Empty<ServerDynamicBlocker>(),
                    interactables = interactables ?? Array.Empty<ServerWorldInteractableDefinition>(),
                };

                // The embedded hash identifies the immutable payload independent of JSON
                // formatting. The manifest hash below validates the exact file bytes.
                string unhashed = JsonUtility.ToJson(shared, false);
                shared.contentHash = Sha256(unhashed);
                string json = JsonUtility.ToJson(shared, false);
                // Gzip is lossless: identifiers, floats, triangles and metadata are unchanged.
                // Copy compressed bytes to guarantee identical server/client file hashes.
                using (FileStream stream = File.Create(serverPath))
                using (var gzip = new GZipStream(stream, System.IO.Compression.CompressionLevel.Optimal))
                using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                    writer.Write(json);
                File.Copy(serverPath, clientPath, true);

                string fileHash = Sha256File(serverPath);
                if (string.IsNullOrWhiteSpace(fileHash))
                {
                    Debug.LogError($"[Server World Bake] Could not hash shared-world package: {serverPath}");
                    return false;
                }

                // Both copies must be identical; this is the client-safe immutable world
                // package that lets clean clients predict/filter without network polling.
                string clientHash = Sha256File(clientPath);
                if (!string.Equals(fileHash, clientHash, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError(
                        $"[Server World Bake] Client/server shared-world copies differ for map '{map.MapId}'.");
                    return false;
                }

                info = new ServerSharedWorldInfo
                {
                    formatVersion = SharedWorldFormat.Version,
                    fileName = fileName,
                    contentHash = fileHash,
                };

                Debug.Log(
                    $"[Shared World Bake] '{map.MapId}' support={shared.supportTriangles.Length:n0}, " +
                    $"edges={shared.surfaceEdges.Length:n0}, collision={shared.collisionTriangles.Length:n0}, " +
                    $"blockers={shared.dynamicBlockers.Length:n0}, interactables={shared.interactables.Length:n0}, " +
                    $"file={fileName}, hash={ShortHash(fileHash)}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Server World Bake] Shared-world package generation failed: {ex.Message}");
                info = null;
                return false;
            }
        }

        private static ServerSurfaceEdge[] BuildSharedWorldBoundaryEdges(
            string mapId,
            ServerCollisionTriangle[] supportTriangles)
        {
            if (supportTriangles == null || supportTriangles.Length == 0)
                return Array.Empty<ServerSurfaceEdge>();

            var edges = new Dictionary<SharedWorldEdgeKey, SharedWorldEdgeAccumulator>(supportTriangles.Length * 2);
            for (int i = 0; i < supportTriangles.Length; ++i)
            {
                ServerCollisionTriangle triangle = supportTriangles[i];
                Vector3 a = new Vector3(triangle.ax, triangle.ay, triangle.az);
                Vector3 b = new Vector3(triangle.bx, triangle.by, triangle.bz);
                Vector3 c = new Vector3(triangle.cx, triangle.cy, triangle.cz);
                Vector3 normal = new Vector3(triangle.normalX, triangle.normalY, triangle.normalZ);
                AddSharedWorldEdge(edges, a, b, normal, triangle.flags);
                AddSharedWorldEdge(edges, b, c, normal, triangle.flags);
                AddSharedWorldEdge(edges, c, a, normal, triangle.flags);
            }

            var result = new List<ServerSurfaceEdge>();
            foreach (KeyValuePair<SharedWorldEdgeKey, SharedWorldEdgeAccumulator> pair in edges)
            {
                SharedWorldEdgeAccumulator edge = pair.Value;
                if (edge.Count != 1)
                    continue;

                Vector3 direction = edge.B - edge.A;
                Vector3 outward = Vector3.Cross(direction.normalized, edge.SurfaceNormal.normalized);
                outward.y = 0f;
                if (outward.sqrMagnitude > 0.000001f)
                    outward.Normalize();

                result.Add(new ServerSurfaceEdge
                {
                    stableId = StableId($"{mapId}/shared-edge/{pair.Key}"),
                    ax = edge.A.x,
                    ay = edge.A.y,
                    az = edge.A.z,
                    bx = edge.B.x,
                    by = edge.B.y,
                    bz = edge.B.z,
                    normalX = outward.x,
                    normalY = outward.y,
                    normalZ = outward.z,
                    kind = ServerSurfaceEdgeKind.Boundary,
                    flags = edge.Flags,
                });
            }

            result.Sort((left, right) => left.stableId.CompareTo(right.stableId));
            return result.ToArray();
        }

        private static void AddSharedWorldEdge(
            Dictionary<SharedWorldEdgeKey, SharedWorldEdgeAccumulator> edges,
            Vector3 a,
            Vector3 b,
            Vector3 surfaceNormal,
            ServerSurfaceFlags flags)
        {
            var key = new SharedWorldEdgeKey(a, b);
            if (edges.TryGetValue(key, out SharedWorldEdgeAccumulator existing))
            {
                existing.Count++;
                existing.Flags |= flags;
                return;
            }

            edges.Add(key, new SharedWorldEdgeAccumulator
            {
                Count = 1,
                A = a,
                B = b,
                SurfaceNormal = surfaceNormal,
                Flags = flags,
            });
        }

        private static bool TryBakeServerNavMesh(
            Scene scene,
            ServerMap map,
            out ServerNavMeshInfo info)
        {
            info = null;

            if (!TryCollectServerNavGeometry(
                    scene,
                    map,
                    out float[] vertices,
                    out int[] indices,
                    out int sourceCount))
            {
                return false;
            }

            string mapStem = MapFileStem(map.MapId, map.InstanceId);
            string intermediateFolder = Path.Combine(
                ProjectRoot,
                "Library",
                "MMO Server NavMesh");
            Directory.CreateDirectory(intermediateFolder);
            Directory.CreateDirectory(ServerMapsFolder);

            string sourcePath = Path.Combine(
                intermediateFolder,
                mapStem + ".servernavsource.json");
            string navFileName = mapStem + ".servernavmesh.bin";
            string navPath = Path.Combine(ServerMapsFolder, navFileName);

            var source = new ServerNavSource
            {
                mapId = ServerMapId.Normalize(map.MapId),
                vertices = vertices,
                indices = indices,
                settings = new ServerNavSettings
                {
                    cellSize = map.NavCellSize,
                    cellHeight = map.NavCellHeight,
                    tileSize = map.NavTileSize,
                    agentHeight = map.NavAgentHeight,
                    agentRadius = map.NavAgentRadius,
                    agentMaxClimb = map.NavAgentMaxClimb,
                    agentMaxSlope = map.NavAgentMaxSlope,
                },
            };

            File.WriteAllText(
                sourcePath,
                JsonUtility.ToJson(source, false),
                new UTF8Encoding(false));

            string bakerProject = Path.Combine(
                ProjectRoot,
                "Server",
                "Tools",
                "ServerNavMeshBaker",
                "ServerNavMeshBaker.csproj");

            if (!File.Exists(bakerProject))
            {
                Debug.LogError(
                    $"[Server World Bake] Server NavMesh baker project is missing: {bakerProject}");
                return false;
            }

            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments =
                    $"run --project \"{bakerProject}\" -c Release -- " +
                    $"\"{sourcePath}\" \"{navPath}\"",
                WorkingDirectory = ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using Process process = Process.Start(start);
                if (process == null)
                {
                    Debug.LogError("[Server World Bake] Could not start the .NET ServerNavMeshBaker.");
                    return false;
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(300000))
                {
                    try { process.Kill(); } catch { }
                    Debug.LogError(
                        "[Server World Bake] ServerNavMeshBaker exceeded the 5 minute build timeout.");
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    Debug.LogError(
                        "[Server World Bake] ServerNavMeshBaker failed.\n" +
                        stdout + "\n" + stderr);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(stdout))
                    Debug.Log(stdout.Trim());
                if (!string.IsNullOrWhiteSpace(stderr))
                    Debug.LogWarning(stderr.Trim());
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Debug.LogError(
                    "[Server World Bake] Could not launch 'dotnet'. Install the .NET 8 SDK " +
                    $"or make sure dotnet.exe is on PATH. {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[Server World Bake] Server NavMesh bake failed: {ex.Message}");
                return false;
            }

            if (!File.Exists(navPath))
            {
                Debug.LogError(
                    $"[Server World Bake] ServerNavMeshBaker returned success but did not create: {navPath}");
                return false;
            }

            string hash = Sha256File(navPath);
            if (string.IsNullOrWhiteSpace(hash))
            {
                Debug.LogError(
                    $"[Server World Bake] Could not hash generated server NavMesh: {navPath}");
                return false;
            }

            info = new ServerNavMeshInfo
            {
                formatVersion = ServerNavMeshInfo.CurrentFormatVersion,
                fileName = navFileName,
                contentHash = hash,
                builder = "DotRecast",
                builderVersion = "2026.3.1",
                cellSize = map.NavCellSize,
                cellHeight = map.NavCellHeight,
                tileSize = map.NavTileSize,
                agentHeight = map.NavAgentHeight,
                agentRadius = map.NavAgentRadius,
                agentMaxClimb = map.NavAgentMaxClimb,
                agentMaxSlope = map.NavAgentMaxSlope,
                maxVertsPerPoly = 6,
            };

            Debug.Log(
                $"[Server NavMesh Bake] '{map.MapId}' collider sources={sourceCount:n0}, " +
                $"vertices={vertices.Length / 3:n0}, triangles={indices.Length / 3:n0}, " +
                $"agent={info.agentRadius:0.###}r/{info.agentHeight:0.###}h, " +
                $"climb={info.agentMaxClimb:0.###}, slope={info.agentMaxSlope:0.#}°, " +
                $"cell={info.cellSize:0.###}x{info.cellHeight:0.###}, tile={info.tileSize}.");

            return true;
        }

        private static bool TryCollectServerNavGeometry(
            Scene scene,
            ServerMap map,
            out float[] vertices,
            out int[] indices,
            out int sourceCount)
        {
            var verts = new List<float>(32768);
            var tris = new List<int>(32768);
            var colliderSourceObjects = new HashSet<int>();

            sourceCount = 0;
            int colliderCount = 0;
            int meshFallbackCount = 0;
            int terrainFallbackCount = 0;
            int unsupportedColliderCount = 0;
            int unreadableMeshCount = 0;

            // Preferred source: authored Unity colliders. These describe the physical
            // environment most directly and are the normal production path.
            Collider[] colliders = Object.FindObjectsByType<Collider>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < colliders.Length; ++i)
            {
                Collider collider = colliders[i];
                if (!ShouldUseNavCollider(collider, scene, map))
                    continue;

                bool added;
                if (collider is BoxCollider box)
                    added = AddNavBox(box, verts, tris);
                else if (collider is MeshCollider mesh)
                    added = AddNavMesh(mesh, verts, tris);
                else if (collider is TerrainCollider terrain)
                    added = AddNavTerrain(terrain, map.TerrainSampleStep, verts, tris);
                else
                    added = false;

                if (added)
                {
                    sourceCount++;
                    colliderCount++;
                    colliderSourceObjects.Add(collider.gameObject.GetInstanceID());
                }
                else
                {
                    unsupportedColliderCount++;
                }
            }

            // Authoring fallback: if a visible static/environment object has no usable
            // collider source on the same GameObject, use its readable MeshFilter geometry.
            //
            // This keeps existing Synty/test scenes bakeable without requiring designers to
            // manually add MeshColliders to every piece before the server-nav pipeline can be
            // validated. Once a collider exists, the collider always wins.
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < renderers.Length; ++i)
            {
                MeshRenderer renderer = renderers[i];
                if (!ShouldUseNavRenderSource(renderer, scene, map))
                    continue;

                if (colliderSourceObjects.Contains(renderer.gameObject.GetInstanceID()))
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                if (!mesh.isReadable)
                {
                    unreadableMeshCount++;
                    continue;
                }

                if (AddNavMesh(
                        mesh,
                        renderer.transform.localToWorldMatrix,
                        verts,
                        tris))
                {
                    sourceCount++;
                    meshFallbackCount++;
                }
            }

            // Terrain can be authored without a TerrainCollider while the world/nav data is
            // being built. Prefer TerrainCollider above; otherwise use TerrainData directly.
            Terrain[] terrains = Object.FindObjectsByType<Terrain>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < terrains.Length; ++i)
            {
                Terrain terrain = terrains[i];
                if (!ShouldUseNavTerrainSource(terrain, scene, map))
                    continue;

                if (terrain.GetComponent<TerrainCollider>() != null &&
                    colliderSourceObjects.Contains(terrain.gameObject.GetInstanceID()))
                {
                    continue;
                }

                if (AddNavTerrain(
                        terrain,
                        map.TerrainSampleStep,
                        verts,
                        tris))
                {
                    sourceCount++;
                    terrainFallbackCount++;
                }
            }

            if (unsupportedColliderCount > 0)
            {
                Debug.LogWarning(
                    $"[Server NavMesh Bake] {unsupportedColliderCount} eligible collider(s) were skipped. " +
                    "V1 directly supports BoxCollider, readable MeshCollider, and TerrainCollider. " +
                    "Other collider type   hould use a simple server proxy.");
            }

            if (unreadableMeshCount > 0)
            {
                Debug.LogWarning(
                    $"[Server NavMesh Bake] {unreadableMeshCount} MeshFilter source(s) were not readable and could not " +
                    "be used as the no-collider fallback. Add an authored collider/server proxy or enable Read/Write " +
                    "on those specific source meshes if they must participate in the Recast bake.");
            }

            vertices = verts.ToArray();
            indices = tris.ToArray();

            if (sourceCount == 0 || indices.Length < 3)
            {
                Debug.LogError(
                    $"[Server NavMesh Bake] Map '{map.MapId}' has no usable server navigation geometry. " +
                    $"Scanned colliders={colliders.Length:n0}, renderers={renderers.Length:n0}, terrains={terrains.Length:n0}. " +
                    "The baker accepts enabled BoxCollider, readable MeshCollider, TerrainCollider, " +
                    "or readable MeshFilter/Terrain fallbacks on ServerMap collision layers. " +
                    "Triggers, ServerBakeIgnore objects, dynamic blockers, and water-only surfaces are excluded.");
                return false;
            }

            Debug.Log(
                $"[Server NavMesh Sources] '{map.MapId}': " +
                $"colliders={colliderCount:n0}, meshFallbacks={meshFallbackCount:n0}, " +
                $"terrainFallbacks={terrainFallbackCount:n0}, total={sourceCount:n0}.");

            return true;
        }

        private static bool ShouldUseNavCollider(
            Collider collider,
            Scene scene,
            ServerMap map)
        {
            if (collider == null ||
                collider.gameObject.scene != scene ||
                !collider.enabled ||
                !collider.gameObject.activeInHierarchy ||
                collider.isTrigger)
            {
                return false;
            }

            if ((map.CollisionLayers.value & (1 << collider.gameObject.layer)) == 0)
                return false;
            if (collider.GetComponentInParent<ServerBakeIgnore>() != null)
                return false;
            if (collider is CharacterController)
                return false;

            WorldInteractable interactable =
                collider.GetComponentInParent<WorldInteractable>();
            if (interactable != null && interactable.dynamicBlocker)
                return false;

            // Flat water surfaces must not be rasterized as humanoid floor. They remain
            // available to the normal world/collision metadata path.
            ServerSurface surface =
                collider.GetComponentInParent<ServerSurface>();
            if (surface != null &&
                (surface.flags & ServerSurfaceFlags.Water) != 0 &&
                (surface.flags & ServerSurfaceFlags.Walkable) == 0)
            {
                return false;
            }

            // Do not require Unity's GameObject.isStatic flag. It is an editor optimization
            // hint, not an MMO server-authority contract. Scene membership, collision layers,
            // ServerBakeIgnore, trigger state, dynamic-blocker ownership, and ServerSurface
            // metadata are the actual bake filters.
            return true;
        }

        private static bool ShouldUseNavRenderSource(
            MeshRenderer renderer,
            Scene scene,
            ServerMap map)
        {
            if (renderer == null ||
                renderer.gameObject.scene != scene ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
            {
                return false;
            }

            if ((map.CollisionLayers.value & (1 << renderer.gameObject.layer)) == 0)
                return false;
            if (renderer.GetComponentInParent<ServerBakeIgnore>() != null)
                return false;

            WorldInteractable interactable =
                renderer.GetComponentInParent<WorldInteractable>();
            if (interactable != null && interactable.dynamicBlocker)
                return false;

            ServerSurface surface =
                renderer.GetComponentInParent<ServerSurface>();
            if (surface != null &&
                (surface.flags & ServerSurfaceFlags.Water) != 0 &&
                (surface.flags & ServerSurfaceFlags.Walkable) == 0)
            {
                return false;
            }

            return true;
        }

        private static bool ShouldUseNavTerrainSource(
            Terrain terrain,
            Scene scene,
            ServerMap map)
        {
            if (terrain == null ||
                terrain.gameObject.scene != scene ||
                !terrain.enabled ||
                !terrain.gameObject.activeInHierarchy ||
                terrain.terrainData == null)
            {
                return false;
            }

            if ((map.CollisionLayers.value & (1 << terrain.gameObject.layer)) == 0)
                return false;
            if (terrain.GetComponentInParent<ServerBakeIgnore>() != null)
                return false;

            ServerSurface surface =
                terrain.GetComponentInParent<ServerSurface>();
            if (surface != null &&
                (surface.flags & ServerSurfaceFlags.Water) != 0 &&
                (surface.flags & ServerSurfaceFlags.Walkable) == 0)
            {
                return false;
            }

            return true;
        }

        private static bool AddNavBox(
            BoxCollider box,
            List<float> vertices,
            List<int> indices)
        {
            Vector3 h = box.size * 0.5f;
            Vector3 c = box.center;
            Vector3[] local =
            {
                c + new Vector3(-h.x,-h.y,-h.z),
                c + new Vector3( h.x,-h.y,-h.z),
                c + new Vector3( h.x,-h.y, h.z),
                c + new Vector3(-h.x,-h.y, h.z),
                c + new Vector3(-h.x, h.y,-h.z),
                c + new Vector3( h.x, h.y,-h.z),
                c + new Vector3( h.x, h.y, h.z),
                c + new Vector3(-h.x, h.y, h.z),
            };

            int baseVertex = vertices.Count / 3;
            for (int i = 0; i < local.Length; ++i)
                AddNavVertex(vertices, box.transform.TransformPoint(local[i]));

            int[] boxIndices =
            {
                0,1,2, 0,2,3,
                4,6,5, 4,7,6,
                0,5,1, 0,4,5,
                3,6,7, 3,2,6,
                0,7,4, 0,3,7,
                1,6,2, 1,5,6,
            };
            for (int i = 0; i < boxIndices.Length; ++i)
                indices.Add(baseVertex + boxIndices[i]);

            return true;
        }

        private static bool AddNavMesh(
            MeshCollider collider,
            List<float> vertices,
            List<int> indices)
        {
            Mesh mesh = collider.sharedMesh;
            if (mesh == null)
                return false;
            if (!mesh.isReadable)
            {
                Debug.LogWarning(
                    $"[Server NavMesh Bake] MeshCollider '{HierarchyPath(collider.transform)}' " +
                    $"uses non-readable mesh '{mesh.name}'. Use a readable/server proxy mesh.");
                return false;
            }

            return AddNavMesh(
                mesh,
                collider.transform.localToWorldMatrix,
                vertices,
                indices);
        }

        private static bool AddNavMesh(
            Mesh mesh,
            Matrix4x4 localToWorld,
            List<float> vertices,
            List<int> indices)
        {
            if (mesh == null || !mesh.isReadable)
                return false;

            Vector3[] sourceVertices = mesh.vertices;
            int baseVertex = vertices.Count / 3;
            for (int i = 0; i < sourceVertices.Length; ++i)
                AddNavVertex(vertices, localToWorld.MultiplyPoint3x4(sourceVertices[i]));

            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                int[] sourceIndices = mesh.GetTriangles(sub);
                for (int i = 0; i < sourceIndices.Length; ++i)
                    indices.Add(baseVertex + sourceIndices[i]);
            }

            return sourceVertices.Length > 0;
        }

        private static bool AddNavTerrain(
            TerrainCollider collider,
            int step,
            List<float> vertices,
            List<int> indices)
        {
            TerrainData data = collider.terrainData;
            if (data == null)
                return false;

            int resolution = data.heightmapResolution;
            step = Mathf.Clamp(step, 1, 32);
            var xs = new List<int>();
            var zs = new List<int>();

            for (int x = 0; x < resolution - 1; x += step)
                xs.Add(x);
            xs.Add(resolution - 1);

            for (int z = 0; z < resolution - 1; z += step)
                zs.Add(z);
            zs.Add(resolution - 1);

            float[,] heights =
                data.GetHeights(0, 0, resolution, resolution);
            Vector3 size = data.size;
            Vector3 origin = collider.transform.position;

            int baseVertex = vertices.Count / 3;
            for (int zi = 0; zi < zs.Count; ++zi)
            {
                int z = zs[zi];
                for (int xi = 0; xi < xs.Count; ++xi)
                {
                    int x = xs[xi];
                    AddNavVertex(
                        vertices,
                        TerrainPoint(
                            origin,
                            size,
                            heights,
                            resolution,
                            x,
                            z));
                }
            }

            int row = xs.Count;
            for (int z = 0; z < zs.Count - 1; ++z)
            {
                for (int x = 0; x < xs.Count - 1; ++x)
                {
                    int a = baseVertex + z * row + x;
                    int b = a + 1;
                    int d = baseVertex + (z + 1) * row + x;
                    int c = d + 1;

                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(a); indices.Add(d); indices.Add(c);
                }
            }

            return true;
        }

        private static bool AddNavTerrain(
            Terrain terrain,
            int step,
            List<float> vertices,
            List<int> indices)
        {
            if (terrain == null || terrain.terrainData == null)
                return false;

            TerrainData data = terrain.terrainData;
            int resolution = data.heightmapResolution;
            step = Mathf.Clamp(step, 1, 32);

            var xs = new List<int>();
            var zs = new List<int>();
            for (int x = 0; x < resolution - 1; x += step)
                xs.Add(x);
            xs.Add(resolution - 1);

            for (int z = 0; z < resolution - 1; z += step)
                zs.Add(z);
            zs.Add(resolution - 1);

            float[,] heights =
                data.GetHeights(0, 0, resolution, resolution);
            Vector3 size = data.size;
            Vector3 origin = terrain.transform.position;

            int baseVertex = vertices.Count / 3;
            for (int zi = 0; zi < zs.Count; ++zi)
            {
                int z = zs[zi];
                for (int xi = 0; xi < xs.Count; ++xi)
                {
                    int x = xs[xi];
                    AddNavVertex(
                        vertices,
                        TerrainPoint(
                            origin,
                            size,
                            heights,
                            resolution,
                            x,
                            z));
                }
            }

            int row = xs.Count;
            for (int z = 0; z < zs.Count - 1; ++z)
            {
                for (int x = 0; x < xs.Count - 1; ++x)
                {
                    int a = baseVertex + z * row + x;
                    int b = a + 1;
                    int d = baseVertex + (z + 1) * row + x;
                    int c = d + 1;

                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(a); indices.Add(d); indices.Add(c);
                }
            }

            return true;
        }

        private static void AddNavVertex(
            List<float> vertices,
            Vector3 value)
        {
            vertices.Add(value.x);
            vertices.Add(value.y);
            vertices.Add(value.z);
        }

        private static string MapFileStem(string mapId, string instanceId)
        {
            string normalizedMap = ServerMapId.Normalize(mapId);
            string normalizedInstance = ServerMapId.Normalize(instanceId);
            return string.IsNullOrWhiteSpace(normalizedInstance)
                ? normalizedMap
                : normalizedMap + "__" + normalizedInstance;
        }

        private static string Sha256File(string path)
        {
            try
            {
                using SHA256 sha = SHA256.Create();
                using FileStream stream = File.OpenRead(path);
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; ++i)
                    builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ShortHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        // Authoritative grounding uses authored physical geometry, not Unity's polygonal
        // NavMesh.CalculateTriangulation(). That navigation triangulation may span
        // height discontinuities and generate false over-slope ground wedges.
        private static bool ExportMovementGeometry(
            ServerMap map, Scene scene, List<ServerCollisionTriangle> output)
        {
            float minimumUp = Mathf.Cos(Mathf.Clamp(map.NavAgentMaxSlope, 0f, 89f) * Mathf.Deg2Rad);
            var geometry = new List<float>(8192);
            var indices = new List<int>(8192);
            var colliderObjects = new HashSet<int>();
            int sources = 0;
            int rejectedSlope = 0;
            int rejectedDegenerate = 0;

            // Follow the existing server-nav source policy: colliders take priority;
            // readable rendered meshes are fallback only when no collider was exported.
            Collider[] colliders = Object.FindObjectsByType<Collider>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < colliders.Length; ++i)
            {
                Collider collider = colliders[i];
                if (!ShouldUseNavCollider(collider, scene, map) ||
                    collider.GetComponentInParent<WorldInteractable>() != null)
                    continue;

                geometry.Clear();
                indices.Clear();
                bool added = collider is BoxCollider box
                    ? AddNavBox(box, geometry, indices)
                    : collider is MeshCollider mesh
                        ? AddNavMesh(mesh, geometry, indices)
                        : collider is TerrainCollider terrain &&
                            AddNavTerrain(terrain, map.TerrainSampleStep, geometry, indices);
                if (!added || indices.Count < 3)
                    continue;

                colliderObjects.Add(collider.gameObject.GetInstanceID());
                ++sources;
                EmitMovementSource(map, collider.transform, sources, geometry, indices,
                    minimumUp, output, ref rejectedSlope, ref rejectedDegenerate);
            }

            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; ++i)
            {
                MeshRenderer renderer = renderers[i];
                if (!ShouldUseNavRenderSource(renderer, scene, map) ||
                    colliderObjects.Contains(renderer.gameObject.GetInstanceID()) ||
                    renderer.GetComponentInParent<WorldInteractable>() != null)
                    continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable)
                    continue;

                geometry.Clear();
                indices.Clear();
                if (!AddNavMesh(mesh, renderer.transform.localToWorldMatrix, geometry, indices) ||
                    indices.Count < 3)
                    continue;

                ++sources;
                EmitMovementSource(map, renderer.transform, sources, geometry, indices,
                    minimumUp, output, ref rejectedSlope, ref rejectedDegenerate);
            }

            Terrain[] terrains = Object.FindObjectsByType<Terrain>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < terrains.Length; ++i)
            {
                Terrain terrain = terrains[i];
                if (!ShouldUseNavTerrainSource(terrain, scene, map) ||
                    colliderObjects.Contains(terrain.gameObject.GetInstanceID()))
                    continue;

                geometry.Clear();
                indices.Clear();
                if (!AddNavTerrain(terrain, map.TerrainSampleStep, geometry, indices) ||
                    indices.Count < 3)
                    continue;

                ++sources;
                EmitMovementSource(map, terrain.transform, sources, geometry, indices,
                    minimumUp, output, ref rejectedSlope, ref rejectedDegenerate);
            }

            Debug.Log($"[Server World Bake] Authored geometry ground for '{map.MapId}': " +
                $"sources={sources:n0}, walkTriangles={output.Count:n0}, " +
                $"steepOrUnderside={rejectedSlope:n0}, degenerate={rejectedDegenerate:n0}. " +
                "Unity NavMesh triangulation is NOT used for authoritative movement support.");
            if (output.Count != 0)
                return true;

            Debug.LogError($"[Server World Bake] '{map.MapId}' has no valid upward-facing " +
                "ground triangles on eligible scene collision layers. Bake cancelled.");
            return false;
        }

        private static void EmitMovementSource(
            ServerMap map, Transform owner, int sourceIndex,
            List<float> vertices, List<int> indices, float minimumUp,
            List<ServerCollisionTriangle> output,
            ref int rejectedSlope, ref int rejectedDegenerate)
        {
            ServerSurface surface = owner.GetComponentInParent<ServerSurface>();
            ServerSurfaceFlags flags = surface != null
                ? surface.flags
                : map.FlagsForNavArea(ResolveWalkArea(owner));
            // Explicitly non-walkable surfaces must not become ground by having a top face.
            if (surface != null && (flags & ServerSurfaceFlags.Walkable) == 0)
                return;
            flags |= ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground;

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int ia = indices[i] * 3;
                int ib = indices[i + 1] * 3;
                int ic = indices[i + 2] * 3;
                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia + 2 >= vertices.Count || ib + 2 >= vertices.Count ||
                    ic + 2 >= vertices.Count)
                {
                    ++rejectedDegenerate;
                    continue;
                }
                Vector3 a = new Vector3(vertices[ia], vertices[ia + 1], vertices[ia + 2]);
                Vector3 b = new Vector3(vertices[ib], vertices[ib + 1], vertices[ib + 2]);
                Vector3 c = new Vector3(vertices[ic], vertices[ic + 1], vertices[ic + 2]);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.sqrMagnitude < 0.00000001f)
                {
                    ++rejectedDegenerate;
                    continue;
                }
                Vector3 normal = cross.normalized;
                // Never turn undersides into ground by flipping triangle normals.
                if (normal.y < minimumUp)
                {
                    ++rejectedSlope;
                    continue;
                }
                long id = StableId($"{map.MapId}/ground/{HierarchyPath(owner)}/{sourceIndex}/{i / 3}");
                output.Add(Triangle(id, a, b, c, normal, flags));
            }
        }

        private static void ExportStaticCollision(ServerMap map, Scene scene, List<ServerCollisionTriangle> output)
        {
            Collider[] colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int unsupported = 0;
            for (int i = 0; i < colliders.Length; ++i)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.gameObject.scene != scene || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger)
                    continue;
                if ((map.CollisionLayers.value & (1 << collider.gameObject.layer)) == 0)
                    continue;
                if (collider.GetComponentInParent<ServerBakeIgnore>() != null)
                    continue;

                WorldInteractable interactable = collider.GetComponentInParent<WorldInteractable>();
                if (interactable != null && interactable.dynamicBlocker)
                    continue; // represented by the runtime blocker instead of permanent collision

                ServerSurface surface = collider.GetComponentInParent<ServerSurface>();
                // A scene collider on an explicit ServerMap collision layer is authored
                // collision even when Unity's optional Static flag was not checked.
                // Exclusions are handled by ServerBakeIgnore, layer filtering, trigger
                // filtering and dynamic WorldInteractable ownership above.
                ServerSurfaceFlags flags = surface != null ? surface.flags : ServerSurfaceFlags.None;
                string key = $"{scene.path}/{HierarchyPath(collider.transform)}#{i}";

                if (collider is BoxCollider box)
                    ExportBox(box, StableId(key), flags, output);
                else if (collider is MeshCollider meshCollider)
                {
                    if (!ExportMesh(meshCollider, StableId(key), flags, output))
                        ++unsupported;
                }
                else if (collider is TerrainCollider terrain)
                    ExportTerrain(terrain, StableId(key), flags, map.TerrainSampleStep, output);
                else if (!(collider is CharacterController))
                    ++unsupported;
            }

            if (unsupported > 0)
            {
                Debug.LogWarning(
                    $"[Server World Bake] {unsupported} static collider(s) could not be exported. " +
                    "Use BoxCollider, readable MeshCollider, TerrainCollider, or a simple server collision proxy.");
            }
        }

        private static bool ExportInteractables(
            Scene scene,
            out ServerWorldInteractableDefinition[] definitions,
            out ServerDynamicBlocker[] blockers)
        {
            var defs = new List<ServerWorldInteractableDefinition>();
            var block = new List<ServerDynamicBlocker>();
            var ids = new HashSet<long>();
            WorldInteractable[] interactables = Object.FindObjectsByType<WorldInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < interactables.Length; ++i)
            {
                WorldInteractable source = interactables[i];
                if (source == null || source.gameObject.scene != scene || !source.gameObject.activeInHierarchy)
                    continue;
                if (string.IsNullOrWhiteSpace(source.BakeId))
                {
                    Debug.LogError($"[Server World Bake] Interactable '{HierarchyPath(source.transform)}' has no bake ID.");
                    definitions = Array.Empty<ServerWorldInteractableDefinition>();
                    blockers = Array.Empty<ServerDynamicBlocker>();
                    return false;
                }

                long stableId = StableId(source.BakeId);
                if (!ids.Add(stableId))
                {
                    Debug.LogError($"[Server World Bake] Duplicate interactable stable ID {stableId} at '{HierarchyPath(source.transform)}'.");
                    definitions = Array.Empty<ServerWorldInteractableDefinition>();
                    blockers = Array.Empty<ServerDynamicBlocker>();
                    return false;
                }

                if (!ValidateInteractableAuthoring(source))
                {
                    definitions = Array.Empty<ServerWorldInteractableDefinition>();
                    blockers = Array.Empty<ServerDynamicBlocker>();
                    return false;
                }

                WorldObject worldObject = source.GetComponent<WorldObject>();
                worldObject.SetBakedIdentity(stableId, source.definitionId);
                EditorUtility.SetDirty(worldObject);

                CombatTestDummy dummy = source.GetComponent<CombatTestDummy>();
                InteractionTargetKind clientTargetKind = dummy != null && dummy.enabled
                    ? InteractionTargetKind.CombatTestTarget
                    : InteractionTargetKind.SceneObject;

                // Every baked interactable carries the correct client-safe target identity.
                // Ordinary scene objects also carry immutable public actions. Combat-test targets
                // intentionally discover actions from the GameServer because their developer/feed
                // action availability is dynamic and must not be frozen into the client bake.
                ClientInteractionTargetMarker interactionMarker = source.GetComponent<ClientInteractionTargetMarker>();
                if (interactionMarker == null)
                    interactionMarker = source.gameObject.AddComponent<ClientInteractionTargetMarker>();
                interactionMarker.Configure(
                    clientTargetKind,
                    stableId,
                    string.IsNullOrWhiteSpace(source.label) ? source.gameObject.name : source.label,
                    safeInteractions: clientTargetKind == InteractionTargetKind.CombatTestTarget
                        ? Array.Empty<InteractionPublicDefinition>()
                        : ConvertPublicInteractions(source.interactions));
                EditorUtility.SetDirty(interactionMarker);

                long blockerId = 0;
                if (source.dynamicBlocker)
                {
                    blockerId = StableId(source.BakeId + "/blocker");
                    Transform anchor = source.blockerAnchor != null ? source.blockerAnchor : source.transform;
                    block.Add(new ServerDynamicBlocker
                    {
                        stableId = blockerId,
                        kind = source.blockerKind,
                        pose = Pose(anchor),
                        sizeX = Mathf.Max(0.01f, source.blockerSize.x),
                        sizeY = Mathf.Max(0.01f, source.blockerSize.y),
                        sizeZ = Mathf.Max(0.01f, source.blockerSize.z),
                        enabledByDefault = source.blockerEnabledByDefault,
                    });
                }

                ServerCombatTestDummyDefinition dummyDefinition = dummy == null ? null : new ServerCombatTestDummyDefinition
                {
                    enabled = dummy.enabled,
                    healthMaximum = Mathf.Max(1, dummy.healthMaximum),
                    armor = Mathf.Max(0f, dummy.armor),
                    resetOnDefeat = dummy.resetOnDefeat,
                    fireDamageTypeDefinitionId = dummy.fireDamageTypeDefinitionId ?? string.Empty,
                    electricDamageTypeDefinitionId = dummy.electricDamageTypeDefinitionId ?? string.Empty,
                    poisonDamageTypeDefinitionId = dummy.poisonDamageTypeDefinitionId ?? string.Empty,
                };

                defs.Add(new ServerWorldInteractableDefinition
                {
                    stableId = stableId,
                    label = string.IsNullOrWhiteSpace(source.label) ? source.gameObject.name : source.label,
                    pose = Pose(source.transform),
                    kind = source.kind,
                    gameplayProfileId = source.gameplayProfileId ?? string.Empty,
                    lootTableId = source.lootTableId ?? string.Empty,
                    craftingStationId = source.craftingStationId ?? string.Empty,
                    factionDefinitionId = source.factionDefinitionId ?? string.Empty,
                    surveillanceCamera = source.surveillanceCamera ? new ServerSurveillanceCameraDefinition
                    {
                        enabled = true,
                        radius = Mathf.Max(0.5f, source.surveillanceRadius),
                        evidence = Mathf.Max(0, source.surveillanceEvidence),
                    } : null,
                    combatTestDummy = dummyDefinition,
                    interactionDefinitions = ConvertInteractions(source.interactions),
                    slots = ConvertSlots(source.slots),
                    dynamicBlockerId = blockerId,
                    persistentState = source.persistentState,
                    enabledByDefault = source.enabledByDefault,
                });
            }

            definitions = defs.ToArray();
            blockers = block.ToArray();
            return true;
        }

        private static bool ValidateInteractableAuthoring(WorldInteractable source)
        {
            if (source == null)
                return false;

            bool hasSearch = HasInteractionAction(source.interactions, InteractionActionId.Search);
            bool hasCraft = HasInteractionAction(source.interactions, InteractionActionId.Craft);
            string path = HierarchyPath(source.transform);

            if (source.kind == ServerWorldInteractableKind.Searchable && !hasSearch)
            {
                Debug.LogError($"[Server World Bake] Searchable interactable '{path}' does not advertise the Search action.");
                return false;
            }

            if (hasSearch && source.kind != ServerWorldInteractableKind.Searchable)
            {
                Debug.LogError($"[Server World Bake] Interactable '{path}' advertises Search but is not authored as Searchable.");
                return false;
            }

            if ((source.kind == ServerWorldInteractableKind.Searchable || hasSearch) &&
                string.IsNullOrWhiteSpace(source.lootTableId))
            {
                Debug.LogError($"[Server World Bake] Searchable interactable '{path}' has no Loot Table ID.");
                return false;
            }

            if (hasCraft && string.IsNullOrWhiteSpace(source.craftingStationId))
            {
                Debug.LogError($"[Server World Bake] Craft interactable '{path}' has no Crafting Station ID.");
                return false;
            }

            return true;
        }

        private static bool HasInteractionAction(
            WorldInteractable.Interaction[] interactions,
            InteractionActionId actionId)
        {
            WorldInteractable.Interaction[] values = interactions ?? Array.Empty<WorldInteractable.Interaction>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i] != null && values[i].actionId == actionId)
                    return true;
            }
            return false;
        }

        private static bool ExportPopulation(
            Scene scene,
            ServerMap map,
            ServerCollisionWorld portalCollision,
            out ServerPopulationRouteNode[] nodes,
            out ServerPopulationRouteEdge[] edges,
            out ServerPopulationPortal[] portals)
        {
            PopulationRouteMarker[] markers = Object.FindObjectsByType<PopulationRouteMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();

            var nodeOutput = new List<ServerPopulationRouteNode>(markers.Length);
            var markerIds = new Dictionary<PopulationRouteMarker, long>(markers.Length);
            var ids = new HashSet<long>();
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                bool missingId = string.IsNullOrWhiteSpace(marker.BakeId);
                long id = missingId ? 0L : StableId(marker.BakeId);
                if (missingId || !ids.Add(id))
                {
                    // Unity's normal Duplicate operation copies serialized private fields, including
                    // PopulationRouteMarker.bakeId. Repair only the later duplicate in deterministic
                    // hierarchy order so the original marker keeps its stable identity. This happens
                    // at authoring/bake time only and adds no runtime system or network state.
                    long duplicateId = id;
                    Undo.RecordObject(marker, "Repair Population Route Marker Stable ID");

                    const int MaxRepairAttempts = 8;
                    bool repaired = false;
                    for (int attempt = 0; attempt < MaxRepairAttempts; ++attempt)
                    {
                        marker.RegenerateBakeId();
                        id = StableId(marker.BakeId);
                        if (ids.Add(id))
                        {
                            repaired = true;
                            break;
                        }
                    }

                    if (!repaired)
                    {
                        Debug.LogError($"[Server World Bake] Could not repair duplicate Population Route Marker stable ID {duplicateId} at '{HierarchyPath(marker.transform)}'.");
                        nodes = Array.Empty<ServerPopulationRouteNode>();
                        edges = Array.Empty<ServerPopulationRouteEdge>();
                        portals = Array.Empty<ServerPopulationPortal>();
                        return false;
                    }

                    // A copied prefab instance needs an explicit recorded property override;
                    // marking the scene dirty alone need not preserve its new stable ID.
                    if (PrefabUtility.IsPartOfPrefabInstance(marker))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
                    EditorUtility.SetDirty(marker);
                    EditorSceneManager.MarkSceneDirty(scene);
                    Debug.LogWarning(
                        $"[Server World Bake] Repaired duplicated Population Route Marker stable ID {duplicateId} at '{HierarchyPath(marker.transform)}'; new stable ID is {id}. Save the scene to persist the repaired authoring ID.");
                }

                markerIds.Add(marker, id);
                nodeOutput.Add(new ServerPopulationRouteNode
                {
                    stableId = id,
                    label = marker.label ?? marker.gameObject.name,
                    pose = Pose(marker.transform),
                    pathWidth = Mathf.Max(0.5f, marker.pathWidth),
                    routeWeight = Mathf.Max(0.01f, marker.routeWeight),
                    nodeType = marker.branchNode
                        ? PopulationNodeType.Intersection
                        : PopulationNodeType.Regular,
                    // Route markers are navigation topology only. Destination, restriction,
                    // wait/action and traffic-control semantics live in dedicated authoring
                    // systems rather than overloading the route node contract.
                    isDestination = false,
                    destinationTags = PopulationDestinationTag.None,
                    destinationGroup = string.Empty,
                    hardRestricted = false,
                    allowedNpcTypes = PopulationNpcTypeMask.All,
                    minimumWaitSeconds = 0f,
                    maximumWaitSeconds = 0f,
                    actionId = 0,
                });
            }

            var explicitOneWayPairs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker.oneWayOutboundLinks == null)
                    continue;
                for (int l = 0; l < marker.oneWayOutboundLinks.Count; ++l)
                {
                    PopulationRouteMarker target = marker.oneWayOutboundLinks[l];
                    if (!TryPopulationMarkerId(scene, markerIds, marker, target, out long fromId, out long toId))
                        continue;
                    explicitOneWayPairs.Add(UndirectedPopulationPair(fromId, toId));
                }
            }

            PopulationRoutePath[] routePaths = Object.FindObjectsByType<PopulationRoutePath>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();
            var pathMarkers = new List<PopulationRouteMarker>();
            var pathDirectedPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (PopulationRoutePath path in routePaths)
            {
                if (!path.connectChildrenInOrder || !path.oneWay) continue;
                path.CollectDirectMarkers(pathMarkers);
                for (int i = 1; i < pathMarkers.Count; ++i)
                    if (TryPopulationMarkerId(scene, markerIds, pathMarkers[i-1], pathMarkers[i], out long a, out long c))
                        pathDirectedPairs.Add(UndirectedPopulationPair(a,c));
                if (path.loop && pathMarkers.Count > 2 &&
                    TryPopulationMarkerId(scene, markerIds, pathMarkers[pathMarkers.Count-1], pathMarkers[0], out long end, out long start))
                    pathDirectedPairs.Add(UndirectedPopulationPair(end,start));
            }
            var edgeOutput = new List<ServerPopulationRouteEdge>(Math.Max(16, markers.Length * 2));
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

            // Explicit normal links mirror the established uMMORPG authoring rule: one authored
            // link represents a bidirectional pedestrian segment unless a one-way override exists.
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker.links == null)
                    continue;
                for (int l = 0; l < marker.links.Count; ++l)
                {
                    PopulationRouteMarker target = marker.links[l];
                    if (!TryPopulationMarkerId(scene, markerIds, marker, target, out long fromId, out long toId))
                        continue;
                    // Previously materialized links are editor bookkeeping, not durable
                    // topology. They can be stale after sibling reordering.
                    if ((marker.generatedLinks != null && marker.generatedLinks.Contains(target)) ||
                        (target.generatedLinks != null && target.generatedLinks.Contains(marker)) ||
                        explicitOneWayPairs.Contains(UndirectedPopulationPair(fromId, toId)) ||
                        pathDirectedPairs.Contains(UndirectedPopulationPair(fromId, toId)))
                        continue;
                    AddPopulationEdge(
                        edgeOutput,
                        edgeKeys,
                        fromId,
                        toId,
                        false,
                        Mathf.Max(0.5f, (marker.pathWidth + target.pathWidth) * 0.5f),
                        1f,
                        0.5f);
                }
            }

            // Ordered children are authoritative at bake time. Manual marker links
            // still join separate paths; no materialization action is required.
            int automaticEdges = 0;
            foreach (PopulationRoutePath path in routePaths)
            {
                if (!path.connectChildrenInOrder) continue;
                path.CollectDirectMarkers(pathMarkers);
                int count = pathMarkers.Count;
                int segmentCount = count > 1 ? count - 1 + (path.loop && count > 2 ? 1 : 0) : 0;
                for (int j = 0; j < segmentCount; ++j)
                {
                    PopulationRouteMarker from = pathMarkers[j % count];
                    PopulationRouteMarker to = pathMarkers[(j + 1) % count];
                    if (from == null || to == null || !from.gameObject.activeInHierarchy || !to.gameObject.activeInHierarchy ||
                        !TryPopulationMarkerId(scene, markerIds, from, to, out long a, out long c) ||
                        explicitOneWayPairs.Contains(UndirectedPopulationPair(a,c)))
                        continue;
                    int before = edgeOutput.Count;
                    AddPathPopulationEdge(path, from, to, markerIds, explicitOneWayPairs, edgeOutput, edgeKeys);
                    automaticEdges += edgeOutput.Count - before;
                }
            }
            Debug.Log($"[Server World Bake] Population ordered paths={routePaths.Length}, auto edges={automaticEdges}, total edges={edgeOutput.Count} before explicit one-way links.");

            // Explicit one-way links are applied last and intentionally override bidirectional
            // marker links for that pair.
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker.oneWayOutboundLinks == null)
                    continue;
                for (int l = 0; l < marker.oneWayOutboundLinks.Count; ++l)
                {
                    PopulationRouteMarker target = marker.oneWayOutboundLinks[l];
                    if (!TryPopulationMarkerId(scene, markerIds, marker, target, out long fromId, out long toId))
                        continue;
                    AddPopulationEdge(
                        edgeOutput,
                        edgeKeys,
                        fromId,
                        toId,
                        true,
                        Mathf.Max(0.5f, (marker.pathWidth + target.pathWidth) * 0.5f),
                        1f,
                        0.5f);
                }
            }

            // All active scene markers (including markers nested in prefab instances) are
            // graph nodes. Reject unwalkable authored edges, then add validated nearby links
            // independently of hierarchy and sibling order. The graph stays transient until
            // the routes authoring layer is written; prefab markers are never rewritten.
            if (!PopulationRouteGraphBuilder.CompleteGraph(
                    scene, portalCollision, markers, markerIds, edgeOutput, edgeKeys,
                    out int graphAutoEdges, out int rejectedEdges, true))
            {
                nodes = Array.Empty<ServerPopulationRouteNode>();
                edges = Array.Empty<ServerPopulationRouteEdge>();
                portals = Array.Empty<ServerPopulationPortal>();
                return false;
            }
            Debug.Log($"[Server World Bake] Scene-wide Population graph: " +
                $"nodes={markers.Length}, edges={edgeOutput.Count}, generated={graphAutoEdges}, " +
                $"unwalkable authored edges discarded={rejectedEdges}.");

            // CompleteGraph above is the single authority for automatic topology.
            // Do not run the legacy forced 64m component merger afterward: it can
            // reintroduce diagonal shortcuts that the local graph intentionally rejected.

            PopulationPortal[] portalSources = Object.FindObjectsByType<PopulationPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();
            var portalOutput = new List<ServerPopulationPortal>(portalSources.Length);
            var portalIds = new HashSet<long>();
            // Reuse the final automatic/explicit adjacency for every portal repair.
            var outgoingNodeIds = new HashSet<long>();
            foreach (ServerPopulationRouteEdge edge in edgeOutput)
            {
                if (edge == null) continue;
                outgoingNodeIds.Add(edge.fromNodeId);
                if (!edge.oneWay) outgoingNodeIds.Add(edge.toNodeId);
            }
            for (int i = 0; i < portalSources.Length; ++i)
            {
                PopulationPortal source = portalSources[i];
                long id = StableId(source.BakeId);
                if (!portalIds.Add(id))
                {
                    // Unity's normal Duplicate operation copies serialized private fields, including
                    // PopulationPortal.bakeId. Mirror the existing Route Marker/ServerSpawn repair
                    // policy: preserve the first portal in deterministic hierarchy order and assign
                    // only the later duplicate a fresh authoring identity. This is editor/bake-only.
                    long duplicateId = id;
                    Undo.RecordObject(source, "Repair Population Portal Stable ID");

                    const int MaxRepairAttempts = 8;
                    bool repaired = false;
                    for (int attempt = 0; attempt < MaxRepairAttempts; ++attempt)
                    {
                        source.RegenerateBakeId();
                        id = StableId(source.BakeId);
                        if (portalIds.Add(id))
                        {
                            repaired = true;
                            break;
                        }
                    }

                    if (!repaired)
                    {
                        Debug.LogError($"[Server World Bake] Could not repair duplicate Population Portal stable ID {duplicateId} at '{HierarchyPath(source.transform)}'.");
                        nodes = Array.Empty<ServerPopulationRouteNode>();
                        edges = Array.Empty<ServerPopulationRouteEdge>();
                        portals = Array.Empty<ServerPopulationPortal>();
                        return false;
                    }

                    if (PrefabUtility.IsPartOfPrefabInstance(source))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(source);
                    EditorUtility.SetDirty(source);
                    EditorSceneManager.MarkSceneDirty(scene);
                    Debug.LogWarning(
                        $"[Server World Bake] Repaired duplicated Population Portal stable ID {duplicateId} at '{HierarchyPath(source.transform)}'; new stable ID is {id}. Save the scene to persist the repaired authoring ID.");
                }
                PopulationRouteMarker resolvedRouteMarker = null;
                long routeNodeId = 0L;
                var portalNodeIds = new List<long>(8);

                if (source.routeMarker != null)
                {
                    PopulationRouteMarker primary = source.routeMarker;
                    // A reference to an inactive marker in this scene is just as unusable as
                    // a prefab-asset/cross-scene reference. The previous error conflated both.
                    if (primary.gameObject.scene != scene ||
                        !primary.gameObject.activeInHierarchy ||
                        !markerIds.TryGetValue(primary, out routeNodeId))
                    {
                        Debug.LogWarning(
                            $"[Server World Bake] Population Portal '{HierarchyPath(source.transform)}' " +
                            $"has a stale or inactive primary Route Marker '{primary.name}'. " +
                            "Searching for a nearby reachable scene-graph marker; the bad reference will " +
                            "only be replaced when a safe candidate is found.", source);
                        routeNodeId = 0L;
                    }
                    else
                    {
                        resolvedRouteMarker = primary;
                        if (!outgoingNodeIds.Contains(routeNodeId) ||
                            !PopulationRouteWalkabilityGizmos.SegmentTraversable(
                                portalCollision, scene,
                                source.transform.position, primary.transform.position))
                        {
                            Debug.LogWarning(
                                $"[Server World Bake] Population Portal '{HierarchyPath(source.transform)}' " +
                                "cannot reach its primary Route Marker; searching for a safe replacement.", source);
                            resolvedRouteMarker = null;
                            routeNodeId = 0L;
                        }
                        else
                        {
                            portalNodeIds.Add(routeNodeId);
                        }
                    }
                }

                if (source.nearbyRouteMarkers != null)
                {
                    for (int n = 0; n < source.nearbyRouteMarkers.Count; ++n)
                    {
                        PopulationRouteMarker candidate = source.nearbyRouteMarkers[n];
                        if (candidate == null ||
                            candidate.gameObject.scene != scene ||
                            !markerIds.TryGetValue(candidate, out long candidateId) ||
                            !outgoingNodeIds.Contains(candidateId) ||
                            portalNodeIds.Contains(candidateId))
                        {
                            continue;
                        }

                        if (!PopulationRouteWalkabilityGizmos.SegmentTraversable(
                                portalCollision,
                                scene,
                                source.transform.position,
                                candidate.transform.position))
                        {
                            Debug.LogWarning(
                                $"[Server World Bake] Population Portal '{HierarchyPath(source.transform)}' " +
                                $"ignored nearby Route Marker '{HierarchyPath(candidate.transform)}' because the baked movement surface cannot reach it directly.");
                            continue;
                        }

                        portalNodeIds.Add(candidateId);
                    }
                }

                if (routeNodeId <= 0)
                {
                    // Never attach a doorway to an arbitrary node across the map. First
                    // prefer explicit nearby candidates, then the nearest safe local node.
                    // The 24m limit applies only to AUTOMATIC fallback, not an already-valid
                    // authored primary; invalid references must not create long shortcuts.
                    const float MaxPortalAutoRepairMeters = 24f;
                    float bestSq = MaxPortalAutoRepairMeters * MaxPortalAutoRepairMeters;
                    IEnumerable<PopulationRouteMarker> candidates =
                        (source.nearbyRouteMarkers ?? new List<PopulationRouteMarker>())
                        .Concat(markers)
                        .Where(candidate => candidate != null &&
                            candidate.gameObject.scene == scene &&
                            candidate.gameObject.activeInHierarchy &&
                            markerIds.ContainsKey(candidate))
                        .Distinct()
                        .OrderBy(candidate =>
                        {
                            Vector3 delta = candidate.transform.position - source.transform.position;
                            return delta.x * delta.x + delta.z * delta.z;
                        });

                    // An entry needs a usable outward edge, not just an isolated waypoint.
                    foreach (PopulationRouteMarker candidate in candidates)
                    {
                        long candidateId = markerIds[candidate];
                        if (!outgoingNodeIds.Contains(candidateId)) continue;
                        Vector3 delta = candidate.transform.position - source.transform.position;
                        float horizontalSq = delta.x * delta.x + delta.z * delta.z;
                        if (horizontalSq > bestSq) continue;
                        if (!PopulationRouteWalkabilityGizmos.SegmentTraversable(
                                portalCollision, scene,
                                source.transform.position, candidate.transform.position))
                            continue;

                        bestSq = horizontalSq;
                        resolvedRouteMarker = candidate;
                        routeNodeId = candidateId;
                        break; // Deterministic nearest reachable, connected marker.
                    }

                    if (resolvedRouteMarker == null || routeNodeId <= 0)
                    {
                        Debug.LogWarning(
                            $"[Server World Bake] OMIT Population Portal '{HierarchyPath(source.transform)}': " +
                            "no reachable, connected route node within 24m. " +
                            "This portal and its Population spawn capacity will NOT be exported. " +
                            "Other portals will continue baking.", source);
                        continue;
                    }

                    if (!portalNodeIds.Contains(routeNodeId))
                        portalNodeIds.Insert(0, routeNodeId);
                }
                else
                {
                    portalNodeIds.Remove(routeNodeId);
                    portalNodeIds.Insert(0, routeNodeId);
                }

                if (!TryResolvePortalBakePose(
                        portalCollision,
                        map,
                        source,
                        "Portal Anchor",
                        source.transform,
                        out ServerPose anchor))
                {
                    Debug.LogWarning(
                        $"[Server World Bake] OMIT Population Portal '{HierarchyPath(source.transform)}': " +
                        "invalid portal ground/capsule location. Its Population spawn capacity " +
                        "will NOT be exported. Other portals will continue baking.", source);
                    continue;
                }

                // Only commit repaired references after the portal's pose also passes
                // the authoritative collision/ground validation.
                PersistPopulationPortalRouteRefs(
                    scene, portalCollision, source, resolvedRouteMarker, markerIds, outgoingNodeIds);

                portalOutput.Add(new ServerPopulationPortal
                {
                    stableId = id,
                    label = source.label ?? source.gameObject.name,
                    mode = source.mode,
                    portalType = source.portalType,
                    tags = source.tags != PopulationDestinationTag.None ? source.tags : TagsForPopulationPortalType(source.portalType),
                    allowedNpcTypes = source.allowedNpcTypes,
                    // Legacy fields intentionally carry the same single validated anchor.
                    // Runtime does not execute the old multi-point doorway sequence anymore.
                    interiorSpawn = anchor,
                    approach = anchor,
                    interaction = anchor,
                    threshold = anchor,
                    exterior = anchor,
                    routeNodeId = routeNodeId,
                    routeNodeIds = portalNodeIds.ToArray(),
                    doorWorldObjectId = 0L,
                    minimumRespawnDelay = Mathf.Max(0f, source.minimumRespawnDelay),
                    maximumRespawnDelay = Mathf.Max(source.minimumRespawnDelay, source.maximumRespawnDelay),
                    blockedRetryDelay = Mathf.Max(0.1f, source.blockedRetryDelay),
                    minimumSpawnInterval = Mathf.Max(0f, source.minimumSpawnInterval),
                    maximumSpawnInterval = Mathf.Max(source.minimumSpawnInterval, source.maximumSpawnInterval),
                    spawnBurstLimit = Mathf.Max(1, source.spawnBurstLimit),
                    maximumActiveInWorld = Mathf.Max(1, source.maximumActiveInWorld),
                    maximumActiveNearby = Mathf.Max(1, source.maximumActiveNearby),
                    activeNearbyRadius = Mathf.Max(0.5f, source.activeNearbyRadius),
                    exitClearanceRadius = Mathf.Max(0.1f, source.exitClearanceRadius),
                    requireDifferentDestination = source.requireDifferentDestination,
                    suppressWhenVisibleToPlayers = source.suppressWhenVisibleToPlayers,
                });
            }

            // Individual orphaned/invalid portal entries are filtered after saved graph
            // edits have been applied. Do not reject the entire world because one portal
            // or direct Population spawn refers to an unusable route.
            if (portalOutput.Count != portalSources.Length)
                Debug.LogWarning($"[Server World Bake] Population Portal preflight: " +
                    $"accepted={portalOutput.Count}, omitted={portalSources.Length - portalOutput.Count}. " +
                    "Omitted portals contribute zero active Population spawn anchors.");

            nodes = nodeOutput.ToArray();
            edges = edgeOutput.ToArray();
            portals = portalOutput.ToArray();
            return true;
        }

        // A portal is only publishable if it survives the FINAL edited graph and can
        // physically reach a route node with outward adjacency. Do not let a skipped
        // portal leave behind its generated active Population identities in .spawns.json.
        private static ServerPopulationPortal[] FilterPopulationPortals(
            Scene scene,
            ServerCollisionWorld collision,
            ServerPopulationRouteNode[] nodes,
            ServerPopulationRouteEdge[] edges,
            ServerPopulationPortal[] portals)
        {
            var positions = new Dictionary<long, Vector3>();
            foreach (ServerPopulationRouteNode node in nodes ?? Array.Empty<ServerPopulationRouteNode>())
            {
                if (node != null && node.stableId > 0)
                    positions[node.stableId] = new Vector3(node.pose.x, node.pose.y, node.pose.z);
            }
            var outgoing = new HashSet<long>();
            foreach (ServerPopulationRouteEdge edge in edges ?? Array.Empty<ServerPopulationRouteEdge>())
            {
                if (edge == null) continue;
                if (!positions.ContainsKey(edge.fromNodeId) ||
                    !positions.ContainsKey(edge.toNodeId))
                    continue;
                outgoing.Add(edge.fromNodeId);
                if (!edge.oneWay) outgoing.Add(edge.toNodeId);
            }
            var kept = new List<ServerPopulationPortal>();
            foreach (ServerPopulationPortal portal in portals ?? Array.Empty<ServerPopulationPortal>())
            {
                if (portal == null) continue;
                // Primary must be connected and reachable. Never invent a jump across
                // an obstructed doorway or silently activate a different route.
                if (!positions.TryGetValue(portal.routeNodeId, out Vector3 entry) ||
                    !outgoing.Contains(portal.routeNodeId) ||
                    !PopulationRouteWalkabilityGizmos.SegmentTraversable(
                        collision, scene,
                        new Vector3(portal.exterior.x, portal.exterior.y, portal.exterior.z),
                        entry))
                {
                    Debug.LogWarning(
                        $"[Server World Bake] OMIT Population Portal '{portal.label}' " +
                        $"(id={portal.stableId}, entry={portal.routeNodeId}): " +
                        "its entry is missing, isolated, or no longer traversable after graph edits. " +
                        "No spawn anchors will be generated for this portal.");
                    continue;
                }
                // Discard stale alternatives whose endpoints were deleted by graph edits;
                // they must also lead outward and be physically accessible.
                var candidates = new List<long> { portal.routeNodeId };
                if (portal.routeNodeIds != null)
                {
                    foreach (long alternate in portal.routeNodeIds)
                    {
                        if (alternate == portal.routeNodeId || candidates.Contains(alternate) ||
                            !positions.TryGetValue(alternate, out Vector3 point) ||
                            !outgoing.Contains(alternate)) continue;
                        if (PopulationRouteWalkabilityGizmos.SegmentTraversable(
                                collision, scene,
                                new Vector3(portal.exterior.x, portal.exterior.y, portal.exterior.z),
                                point))
                            candidates.Add(alternate);
                    }
                }
                portal.routeNodeIds = candidates.ToArray();
                kept.Add(portal);
            }
            if (kept.Count != (portals?.Length ?? 0))
                Debug.LogWarning($"[Server World Bake] Final Population Portal validation: " +
                    $"accepted={kept.Count}, omitted={(portals?.Length ?? 0) - kept.Count}.");
            return kept.ToArray();
        }

        private static void PersistPopulationPortalRouteRefs(
            Scene scene,
            ServerCollisionWorld collision,
            PopulationPortal portal,
            PopulationRouteMarker primary,
            Dictionary<PopulationRouteMarker, long> markerIds,
            HashSet<long> outgoingNodeIds)
        {
            var cleaned = new List<PopulationRouteMarker> { primary };
            if (portal.nearbyRouteMarkers != null)
            {
                foreach (PopulationRouteMarker marker in portal.nearbyRouteMarkers)
                {
                    if (marker == null || marker == primary || cleaned.Contains(marker) ||
                        marker.gameObject.scene != scene || !marker.gameObject.activeInHierarchy ||
                        !markerIds.TryGetValue(marker, out long markerId) ||
                        !outgoingNodeIds.Contains(markerId))
                        continue;
                    if (PopulationRouteWalkabilityGizmos.SegmentTraversable(
                            collision, scene, portal.transform.position, marker.transform.position))
                        cleaned.Add(marker);
                }
            }
            if (portal.routeMarker == primary &&
                portal.nearbyRouteMarkers != null &&
                portal.nearbyRouteMarkers.SequenceEqual(cleaned))
                return;

            Undo.RecordObject(portal, "Repair Population Portal Route References");
            PopulationRouteMarker oldPrimary = portal.routeMarker;
            portal.routeMarker = primary;
            portal.nearbyRouteMarkers = cleaned;
            if (PrefabUtility.IsPartOfPrefabInstance(portal))
                PrefabUtility.RecordPrefabInstancePropertyModifications(portal);
            EditorUtility.SetDirty(portal);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.LogWarning(
                $"[Server World Bake] Repaired Population Portal '{HierarchyPath(portal.transform)}': " +
                $"primary='{(oldPrimary != null ? oldPrimary.name : "missing")}' -> " +
                $"'{HierarchyPath(primary.transform)}', nearby={cleaned.Count}. " +
                "Scene references will be saved after a successful bake.", portal);
        }

        private static bool TryResolvePortalBakePose(
            ServerCollisionWorld collision,
            ServerMap map,
            PopulationPortal portal,
            string pointName,
            Transform authored,
            out ServerPose resolved)
        {
            resolved = default;
            if (collision == null || map == null || portal == null || authored == null)
            {
                Debug.LogError(
                    $"[Server World Bake] Population Portal '{HierarchyPath(portal != null ? portal.transform : null)}' " +
                    $"has an invalid {pointName} authoring point.");
                return false;
            }

            float radius = Mathf.Max(0.2f, map.NavAgentRadius);
            float height = Mathf.Max(radius * 2f, map.NavAgentHeight);
            float snap = Mathf.Max(0.65f, map.NavAgentMaxClimb + 0.25f);
            float slope = Mathf.Clamp(map.NavAgentMaxSlope, 0f, 89f);

            if (!collision.TryValidateSpawn(
                    Pose(authored),
                    new ServerCapsule(radius, height),
                    snap,
                    slope,
                    out resolved,
                    out string detail))
            {
                Debug.LogWarning(
                    $"[Server World Bake] Population Portal '{HierarchyPath(portal.transform)}' " +
                    $"{pointName} is not a valid grounded traversal pose: {detail}. " +
                    "This Portal will be omitted from the current bake.", portal);
                return false;
            }

            return true;
        }

        private static bool ValidatePortalTraversalSegment(
            ServerCollisionWorld collision,
            Scene scene,
            PopulationPortal portal,
            string segmentName,
            ServerPose from,
            ServerPose to)
        {
            bool valid = PopulationRouteWalkabilityGizmos.SegmentTraversable(
                collision,
                scene,
                new Vector3(from.x, from.y, from.z),
                new Vector3(to.x, to.y, to.z));

            if (!valid)
            {
                Debug.LogError(
                    $"[Server World Bake] Population Portal '{HierarchyPath(portal.transform)}' " +
                    $"segment '{segmentName}' is not directly traversable on the baked movement surface.");
            }
            return valid;
        }

        private static void AddPathPopulationEdge(
            PopulationRoutePath path,
            PopulationRouteMarker from,
            PopulationRouteMarker to,
            Dictionary<PopulationRouteMarker, long> markerIds,
            HashSet<string> explicitOneWayPairs,
            List<ServerPopulationRouteEdge> edges,
            HashSet<string> edgeKeys)
        {
            if (from == null || to == null || !markerIds.TryGetValue(from, out long fromId) || !markerIds.TryGetValue(to, out long toId))
                return;
            if (!path.oneWay && explicitOneWayPairs.Contains(UndirectedPopulationPair(fromId, toId)))
                return;
            AddPopulationEdge(
                edges,
                edgeKeys,
                fromId,
                toId,
                path.oneWay,
                Mathf.Max(0.5f, path.width),
                Mathf.Max(0.01f, path.weight),
                Mathf.Max(0f, path.minimumClearance));
        }

        private static bool ValidatePopulationEntryRouteGraph(
            Scene scene,
            PopulationRouteMarker[] markers,
            Dictionary<PopulationRouteMarker, long> markerIds,
            List<ServerPopulationRouteNode> nodes,
            List<ServerPopulationRouteEdge> edges)
        {
            var nodeIds = new HashSet<long>();
            for (int i = 0; i < nodes.Count; ++i)
            {
                ServerPopulationRouteNode node = nodes[i];
                if (node != null && node.stableId > 0)
                    nodeIds.Add(node.stableId);
            }

            var outgoingDegree = new Dictionary<long, int>();
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (edge == null)
                    continue;

                if (!nodeIds.Contains(edge.fromNodeId) || !nodeIds.Contains(edge.toNodeId))
                {
                    Debug.LogError(
                        $"[Server World Bake] Population route edge {edge.fromNodeId} -> {edge.toNodeId} " +
                        "references a route node that is not present in the baked node set.");
                    return false;
                }

                outgoingDegree.TryGetValue(edge.fromNodeId, out int fromDegree);
                outgoingDegree[edge.fromNodeId] = fromDegree + 1;

                // A normal authored Link is represented by one baked edge with oneWay=false.
                // Runtime mirrors it into the reverse adjacency, so count that reverse here too.
                if (!edge.oneWay)
                {
                    outgoingDegree.TryGetValue(edge.toNodeId, out int toDegree);
                    outgoingDegree[edge.toNodeId] = toDegree + 1;
                }
            }

            var entries = new Dictionary<long, string>();

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy &&
                    x.routeMarker != null)
                .ToArray();

            for (int i = 0; i < portals.Length; ++i)
            {
                PopulationPortal portal = portals[i];
                if (!markerIds.TryGetValue(portal.routeMarker, out long nodeId))
                    continue;

                if (!entries.ContainsKey(nodeId))
                    entries.Add(
                        nodeId,
                        $"Portal '{HierarchyPath(portal.transform)}' -> Marker '{HierarchyPath(portal.routeMarker.transform)}'");
            }

            ServerSpawn[] spawns = Object.FindObjectsByType<ServerSpawn>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy &&
                    x.enabledForServer &&
                    x.routeMarker != null)
                .ToArray();

            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];
                if (!markerIds.TryGetValue(spawn.routeMarker, out long nodeId))
                    continue;

                if (!entries.ContainsKey(nodeId))
                    entries.Add(
                        nodeId,
                        $"Spawn '{HierarchyPath(spawn.transform)}' -> Marker '{HierarchyPath(spawn.routeMarker.transform)}'");
            }

            bool valid = true;
            foreach (KeyValuePair<long, string> entry in entries)
            {
                outgoingDegree.TryGetValue(entry.Key, out int degree);
                if (degree > 0)
                    continue;

                Debug.LogError(
                    $"[Server World Bake] Population entry route is orphaned: {entry.Value}. " +
                    $"Baked nodeId={entry.Key}, outgoingDegree={degree}. " +
                    "The actor can reach this first node but has no usable baked edge to continue on. " +
                    "Check ordered Route Path children or add an explicit junction Link, then rebake.");
                valid = false;
            }

            if (valid && entries.Count > 0)
            {
                Debug.Log(
                    $"[Server World Bake] Population entry-route validation passed: " +
                    $"entries={entries.Count}, nodes={nodes.Count}, edges={edges.Count}.");
            }

            return valid;
        }

        private static bool TryPopulationMarkerId(
            Scene scene,
            Dictionary<PopulationRouteMarker, long> markerIds,
            PopulationRouteMarker from,
            PopulationRouteMarker to,
            out long fromId,
            out long toId)
        {
            fromId = 0;
            toId = 0;
            if (from == null || to == null || from == to || to.gameObject.scene != scene)
                return false;
            return markerIds.TryGetValue(from, out fromId) && markerIds.TryGetValue(to, out toId);
        }

        private static void AddPopulationEdge(
            List<ServerPopulationRouteEdge> output,
            HashSet<string> keys,
            long fromId,
            long toId,
            bool oneWay,
            float width,
            float weight,
            float minimumClearance)
        {
            if (fromId <= 0 || toId <= 0 || fromId == toId)
                return;
            string key = oneWay
                ? $"D:{fromId}:{toId}"
                : $"B:{UndirectedPopulationPair(fromId, toId)}";
            if (!keys.Add(key))
                return;
            output.Add(new ServerPopulationRouteEdge
            {
                fromNodeId = fromId,
                toNodeId = toId,
                oneWay = oneWay,
                width = Mathf.Max(0.5f, width),
                weight = Mathf.Max(0.01f, weight),
                minimumClearance = Mathf.Max(0f, minimumClearance),
                validatedClear = true,
            });
        }

        private static string UndirectedPopulationPair(long a, long b) =>
            a < b ? $"{a}:{b}" : $"{b}:{a}";

        private static PopulationDestinationTag TagsForPopulationPortalType(PopulationPortalType type)
        {
            return type switch
            {
                PopulationPortalType.Apartment => PopulationDestinationTag.Apartment | PopulationDestinationTag.Home,
                PopulationPortalType.Shop => PopulationDestinationTag.Shop,
                PopulationPortalType.Workplace => PopulationDestinationTag.Workplace,
                PopulationPortalType.Restaurant => PopulationDestinationTag.Restaurant,
                PopulationPortalType.Nightclub => PopulationDestinationTag.Nightclub,
                PopulationPortalType.Hospital => PopulationDestinationTag.Hospital,
                PopulationPortalType.Subway => PopulationDestinationTag.Subway,
                PopulationPortalType.ParkingGarage => PopulationDestinationTag.Parking,
                PopulationPortalType.Alley => PopulationDestinationTag.Alley,
                PopulationPortalType.Shelter => PopulationDestinationTag.Shelter,
                PopulationPortalType.Bench => PopulationDestinationTag.Bench | PopulationDestinationTag.Park,
                PopulationPortalType.Underpass => PopulationDestinationTag.Underpass | PopulationDestinationTag.Service,
                PopulationPortalType.Park => PopulationDestinationTag.Park,
                PopulationPortalType.AbandonedBuilding => PopulationDestinationTag.AbandonedBuilding | PopulationDestinationTag.Interior,
                PopulationPortalType.ServiceArea => PopulationDestinationTag.Service,
                _ => PopulationDestinationTag.Interior,
            };
        }

        internal static bool ValidateSpawnAuthoring(
            Scene scene,
            bool repairDuplicateIds,
            bool logSuccess = true)
        {
            bool valid = TryPrepareSpawns(scene, repairDuplicateIds, out ServerSpawn[] spawns);
            if (valid && logSuccess)
                Debug.Log($"[Server World Bake] ServerSpawn authoring valid for scene '{scene.path}' ({spawns.Length} active spawn component(s)).");
            return valid;
        }

        private static bool TryPrepareSpawns(
            Scene scene,
            bool repairDuplicateIds,
            out ServerSpawn[] spawns,
            HashSet<long> acceptedPortalIds = null,
            HashSet<long> acceptedRouteNodeIds = null,
            HashSet<long> outgoingRouteNodeIds = null,
            ServerCollisionWorld collision = null,
            ServerMap map = null,
            Dictionary<long, Vector3> finalRoutePositions = null)
        {
            spawns = Object.FindObjectsByType<ServerSpawn>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();

            PopulationRouteMarker[] routeMarkers = Object.FindObjectsByType<PopulationRouteMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .ToArray();
            var routeIds = new HashSet<long>();
            for (int i = 0; i < routeMarkers.Length; ++i)
                routeIds.Add(StableId(routeMarkers[i].BakeId));

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .ToArray();
            var portalIds = new HashSet<long>();
            for (int i = 0; i < portals.Length; ++i)
                portalIds.Add(StableId(portals[i].BakeId));

            var usedSpawnIds = new HashSet<long>();
            var acceptedSpawns = new List<ServerSpawn>(spawns.Length);
            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];
                if (acceptedPortalIds != null && spawn.kind == ServerSpawnKind.Population)
                {
                    string reason = null;
                    bool portalLinked = spawn.populationPortal != null || spawn.portalId > 0;
                    long linkedPortalId = spawn.populationPortal != null
                        ? StableId(spawn.populationPortal.BakeId)
                        : spawn.portalId;
                    long routeId = spawn.routeMarker != null
                        ? StableId(spawn.routeMarker.BakeId)
                        : spawn.routeNodeId;
                    if (portalLinked && (spawn.populationPortal != null &&
                            (spawn.populationPortal.gameObject.scene != scene ||
                             !spawn.populationPortal.gameObject.activeInHierarchy ||
                             spawn.populationPortal.mode == PopulationPortalMode.DespawnOnly) ||
                            !acceptedPortalIds.Contains(linkedPortalId)))
                        reason = "its linked Population Portal was omitted, inactive, or despawn-only";
                    else if (spawn.routeMarker != null &&
                             (spawn.routeMarker.gameObject.scene != scene ||
                              !spawn.routeMarker.gameObject.activeInHierarchy))
                        reason = "its Route Marker is outside this scene or inactive";
                    else if (!portalLinked && routeId > 0 &&
                             (acceptedRouteNodeIds == null || !acceptedRouteNodeIds.Contains(routeId) ||
                              outgoingRouteNodeIds == null || !outgoingRouteNodeIds.Contains(routeId)))
                        reason = "its direct route node is missing or has no outgoing graph edge";
                    else if (!portalLinked && routeId > 0 && collision != null &&
                             finalRoutePositions != null &&
                             finalRoutePositions.TryGetValue(routeId, out Vector3 entryPoint) &&
                             !PopulationRouteWalkabilityGizmos.SegmentTraversable(
                                 collision, scene, spawn.transform.position, entryPoint))
                        reason = "its spawn-to-route entry segment is obstructed or unwalkable";
                    else if (!portalLinked && collision != null && map != null && spawn.enabledForServer &&
                             !collision.TryValidateSpawn(
                                 Pose(spawn.transform),
                                 new ServerCapsule(Mathf.Max(0.1f, spawn.capsuleRadius),
                                     Mathf.Max(spawn.capsuleHeight, spawn.capsuleRadius * 2f)),
                                 Mathf.Max(0.1f, spawn.maximumGroundSnap),
                                 Mathf.Clamp(map.NavAgentMaxSlope, 0f, 89f),
                                 out ServerPose resolvedPose, out string spawnReason))
                        reason = "its authored spawn location has no valid grounded capsule";
                    if (reason != null)
                    {
                        Debug.LogWarning($"[Server World Bake] OMIT Population Spawn " +
                            $"'{HierarchyPath(spawn.transform)}': {reason}. " +
                            "The remaining spawn sources will still bake.", spawn);
                        continue;
                    }
                }

                bool portalBackedPopulation =
                    spawn.kind == ServerSpawnKind.Population &&
                    (spawn.populationPortal != null || spawn.portalId > 0);
                int count = spawn.kind == ServerSpawnKind.Population && !portalBackedPopulation
                    ? Mathf.Clamp(spawn.populationCount, 1, 4096)
                    : 1;

                if (string.IsNullOrWhiteSpace(spawn.BakeId))
                {
                    if (!repairDuplicateIds)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' has no bake ID.");
                        return false;
                    }

                    Undo.RecordObject(spawn, "Repair Server Spawn Stable ID");
                    spawn.RegenerateBakeId();
                    EditorUtility.SetDirty(spawn);
                    EditorSceneManager.MarkSceneDirty(scene);
                }

                if (!TryReserveSpawnIds(spawn, count, usedSpawnIds, out long[] reservedIds))
                {
                    if (!repairDuplicateIds)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' has a duplicated/colliding stable ID.");
                        return false;
                    }

                    long duplicateId = StableId(spawn.BakeId);
                    Undo.RecordObject(spawn, "Repair Server Spawn Stable ID");

                    const int MaxRepairAttempts = 8;
                    bool repaired = false;
                    for (int attempt = 0; attempt < MaxRepairAttempts; ++attempt)
                    {
                        spawn.RegenerateBakeId();
                        if (TryReserveSpawnIds(spawn, count, usedSpawnIds, out reservedIds))
                        {
                            repaired = true;
                            break;
                        }
                    }

                    if (!repaired)
                    {
                        Debug.LogError(
                            $"[Server World Bake] Could not repair duplicate ServerSpawn stable ID {duplicateId} at '{HierarchyPath(spawn.transform)}'.");
                        return false;
                    }

                    EditorUtility.SetDirty(spawn);
                    EditorSceneManager.MarkSceneDirty(scene);
                    Debug.LogWarning(
                        $"[Server World Bake] Repaired duplicated ServerSpawn stable ID {duplicateId} at '{HierarchyPath(spawn.transform)}'; " +
                        $"new base stable ID is {reservedIds[0]}. Save the scene to persist the repaired authoring ID.");
                }

                for (int r = 0; r < reservedIds.Length; ++r)
                    usedSpawnIds.Add(reservedIds[r]);

                if (spawn.routeMarker != null)
                {
                    if (spawn.routeMarker.gameObject.scene != scene)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' references a Population Route Marker from another scene.");
                        return false;
                    }
                    if (!spawn.routeMarker.gameObject.activeInHierarchy)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' references inactive Population Route Marker '{HierarchyPath(spawn.routeMarker.transform)}'.");
                        return false;
                    }
                }
                else if (spawn.routeNodeId > 0 && !routeIds.Contains(spawn.routeNodeId) &&
                         !PopulationBakedGraphEditor.RouteLayerIncludesId(scene, spawn.routeNodeId))
                {
                    Debug.LogError(
                        $"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' has manual routeNodeId {spawn.routeNodeId}, " +
                        "but neither a scene marker nor the baked Population route layer contains that node ID.");
                    return false;
                }

                if (spawn.populationPortal != null)
                {
                    if (spawn.populationPortal.gameObject.scene != scene)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' references a Population Portal from another scene.");
                        return false;
                    }
                    if (!spawn.populationPortal.gameObject.activeInHierarchy)
                    {
                        Debug.LogError($"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' references inactive Population Portal '{HierarchyPath(spawn.populationPortal.transform)}'.");
                        return false;
                    }
                }
                else if (spawn.portalId > 0 && !portalIds.Contains(spawn.portalId))
                {
                    Debug.LogError(
                        $"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' has manual portalId {spawn.portalId}, " +
                        "but no active Population Portal in this scene bakes to that ID.");
                    return false;
                }
                acceptedSpawns.Add(spawn);
            }

            spawns = acceptedSpawns.ToArray();
            return true;
        }

        private static bool TryReserveSpawnIds(
            ServerSpawn spawn,
            int count,
            HashSet<long> usedSpawnIds,
            out long[] ids)
        {
            ids = new long[Math.Max(1, count)];
            var localIds = new HashSet<long>();
            long baseId = StableId(spawn.BakeId);

            for (int instance = 0; instance < ids.Length; ++instance)
            {
                long id = instance == 0 ? baseId : StableId(baseId, $"|population:{instance}");
                if (!localIds.Add(id) || usedSpawnIds.Contains(id))
                    return false;
                ids[instance] = id;
            }

            return true;
        }

        private static bool ExportSpawns(
            Scene scene, ServerMap map, ServerCollisionWorld collision,
            ServerPopulationRouteNode[] nodes, ServerPopulationRouteEdge[] edges,
            ServerPopulationPortal[] bakedPortals, out ServerSpawnAnchor[] anchors)
        {
            var output = new List<ServerSpawnAnchor>();
            var acceptedPortalIds = new HashSet<long>(
                (bakedPortals ?? Array.Empty<ServerPopulationPortal>())
                    .Where(x => x != null).Select(x => x.stableId));
            var acceptedRouteNodeIds = new HashSet<long>(
                (nodes ?? Array.Empty<ServerPopulationRouteNode>())
                    .Where(x => x != null).Select(x => x.stableId));
            var finalRoutePositions = (nodes ?? Array.Empty<ServerPopulationRouteNode>())
                .Where(x => x != null && x.stableId > 0)
                .GroupBy(x => x.stableId)
                .ToDictionary(g => g.Key,
                    g => new Vector3(g.First().pose.x, g.First().pose.y, g.First().pose.z));
            var outgoingRouteNodeIds = new HashSet<long>();
            foreach (ServerPopulationRouteEdge edge in edges ?? Array.Empty<ServerPopulationRouteEdge>())
            {
                if (edge == null || !acceptedRouteNodeIds.Contains(edge.fromNodeId) ||
                    !acceptedRouteNodeIds.Contains(edge.toNodeId)) continue;
                outgoingRouteNodeIds.Add(edge.fromNodeId);
                if (!edge.oneWay) outgoingRouteNodeIds.Add(edge.toNodeId);
            }
            if (!TryPrepareSpawns(scene, repairDuplicateIds: true, out ServerSpawn[] spawns,
                    acceptedPortalIds, acceptedRouteNodeIds, outgoingRouteNodeIds,
                    collision, map, finalRoutePositions))
            {
                anchors = Array.Empty<ServerSpawnAnchor>();
                return false;
            }

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();

            var portalById = new Dictionary<long, PopulationPortal>();
            for (int i = 0; i < portals.Length; ++i)
            {
                PopulationPortal portal = portals[i];
                long id = StableId(portal.BakeId);
                if (id > 0 && !portalById.ContainsKey(id))
                    portalById.Add(id, portal);
            }

            PopulationPortal ResolvePortal(ServerSpawn spawn)
            {
                if (spawn == null || spawn.kind != ServerSpawnKind.Population)
                    return null;

                if (spawn.populationPortal != null &&
                    spawn.populationPortal.gameObject.scene == scene &&
                    spawn.populationPortal.gameObject.activeInHierarchy)
                {
                    return spawn.populationPortal;
                }

                if (spawn.portalId > 0 &&
                    portalById.TryGetValue(spawn.portalId, out PopulationPortal byId))
                {
                    return byId;
                }

                return null;
            }

            bool IsSpawnCapable(PopulationPortal portal) =>
                portal != null &&
                portal.mode != PopulationPortalMode.DespawnOnly;

            // Non-portal spawns retain their existing authored count semantics. Portal-backed
            // Population spawns are optional templates only; portal capacity is owned by the
            // PopulationPortal itself so duplicated/missing ServerSpawn objects cannot change
            // how many actors a doorway may have active in the world.
            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];
                PopulationPortal resolvedPortal = ResolvePortal(spawn);
                if (spawn.kind == ServerSpawnKind.Population &&
                    resolvedPortal != null &&
                    IsSpawnCapable(resolvedPortal))
                {
                    continue;
                }

                long routeNodeId = spawn.routeNodeId;
                long portalId = spawn.portalId;
                if (spawn.routeMarker != null)
                    routeNodeId = StableId(spawn.routeMarker.BakeId);

                if (spawn.populationPortal != null)
                {
                    portalId = StableId(spawn.populationPortal.BakeId);
                    if (routeNodeId <= 0 && spawn.populationPortal.routeMarker != null)
                        routeNodeId = StableId(spawn.populationPortal.routeMarker.BakeId);
                }

                int count = spawn.kind == ServerSpawnKind.Population
                    ? Mathf.Clamp(spawn.populationCount, 1, 4096)
                    : 1;
                long baseId = StableId(spawn.BakeId);
                for (int instance = 0; instance < count; ++instance)
                {
                    long id = instance == 0 ? baseId : StableId(baseId, $"|population:{instance}");
                    output.Add(new ServerSpawnAnchor
                    {
                        stableId = id,
                        label = count > 1 ? $"{spawn.label} #{instance + 1}" : spawn.label,
                        kind = spawn.kind,
                        actorKind = spawn.actorKind,
                        archetypeId = spawn.archetypeId ?? string.Empty,
                        deathLootTableId = spawn.deathLootTableId ?? string.Empty,
                        pose = Pose(spawn.transform),
                        priority = spawn.priority,
                        enabled = spawn.enabledForServer,
                        capsuleRadius = spawn.capsuleRadius,
                        capsuleHeight = spawn.capsuleHeight,
                        maximumGroundSnap = spawn.maximumGroundSnap,
                        tags = spawn.tags ?? Array.Empty<string>(),
                        routeNodeId = routeNodeId,
                        portalId = portalId,
                    });
                }
            }

            // Every ACCEPTED spawn-capable portal contributes its authored capacity.
            // Omitted portals must not leave orphaned/ghost active Population identities.
            // Existing linked Population spawns are cycled only as optional archetype/capsule
            // templates; a portal with no templates still produces generic Civilian Population.
            var scenePortalById = new Dictionary<long, PopulationPortal>();
            foreach (PopulationPortal portal in portals)
            {
                long id = StableId(portal.BakeId);
                if (!scenePortalById.ContainsKey(id)) scenePortalById.Add(id, portal);
            }
            foreach (ServerPopulationPortal baked in bakedPortals ?? Array.Empty<ServerPopulationPortal>())
            {
                if (baked == null || !scenePortalById.TryGetValue(baked.stableId, out PopulationPortal portal) ||
                    !IsSpawnCapable(portal)) continue;
                long portalId = baked.stableId;
                long routeNodeId = baked.routeNodeId;

                ServerSpawn[] templates = spawns
                    .Where(x =>
                        x != null &&
                        x.enabledForServer &&
                        x.kind == ServerSpawnKind.Population &&
                        ResolvePortal(x) == portal)
                    .ToArray();

                int capacity = Mathf.Clamp(portal.maximumActiveInWorld, 1, 4096);
                for (int instance = 0; instance < capacity; ++instance)
                {
                    ServerSpawn template = templates.Length > 0
                        ? templates[instance % templates.Length]
                        : null;

                    long id = StableId(portalId, $"|active:{instance}");
                    output.Add(new ServerSpawnAnchor
                    {
                        stableId = id,
                        label = capacity > 1
                            ? $"{portal.label} Active #{instance + 1}"
                            : $"{portal.label} Active",
                        kind = ServerSpawnKind.Population,
                        actorKind = Game.Shared.Actors.AuthoritativeActorKind.Population,
                        archetypeId = template?.archetypeId ?? string.Empty,
                        deathLootTableId = template?.deathLootTableId ?? string.Empty,
                        // Validate the anchor on the real exterior walk surface. The existing
                        // Population runtime immediately parks portal-owned identities at the
                        // interior point until cadence/density rules release them.
                        pose = Pose(portal.EffectiveExterior),
                        priority = template?.priority ?? 0,
                        enabled = true,
                        capsuleRadius = template != null ? template.capsuleRadius : 0.35f,
                        capsuleHeight = template != null ? template.capsuleHeight : 1.8f,
                        maximumGroundSnap = template != null ? template.maximumGroundSnap : 0.65f,
                        tags = template?.tags ?? Array.Empty<string>(),
                        routeNodeId = routeNodeId,
                        portalId = portalId,
                    });
                }
            }

            anchors = output.ToArray();
            Debug.Log($"[Server World Bake] Population spawn export: " +
                $"scenePortals={portals.Length}, bakedPortals={acceptedPortalIds.Count}, " +
                $"acceptedServerSpawnTemplates={spawns.Length}, exportedSpawnAnchors={anchors.Length}. " +
                "Skipped Population sources contribute no spawn anchors.");
            return true;
        }

        private static Game.Shared.World.ServerContextualInteractionDefinition[] ConvertInteractions(WorldInteractable.Interaction[] source)
        {
            source ??= Array.Empty<WorldInteractable.Interaction>();
            var result = new Game.Shared.World.ServerContextualInteractionDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                WorldInteractable.Interaction value = source[i] ?? new WorldInteractable.Interaction();
                WorldInteractable.ParticipantRole[] roles = value.participantRoles ?? Array.Empty<WorldInteractable.ParticipantRole>();
                var participantRoles = new ServerInteractionParticipantRoleDefinition[roles.Length];
                for (int r = 0; r < roles.Length; ++r)
                {
                    WorldInteractable.ParticipantRole role = roles[r] ?? new WorldInteractable.ParticipantRole();
                    participantRoles[r] = new ServerInteractionParticipantRoleDefinition
                    {
                        roleId = role.roleId ?? string.Empty,
                        displayLabel = role.displayLabel ?? string.Empty,
                        presentationRole = role.presentationRole,
                        required = role.required,
                    };
                }

                result[i] = new Game.Shared.World.ServerContextualInteractionDefinition
                {
                    definitionId = value.definitionId ?? string.Empty,
                    categoryId = value.categoryId == InteractionCategoryId.None ? InteractionCategoryId.Use : value.categoryId,
                    actionId = value.actionId,
                    displayLabel = value.displayLabel ?? string.Empty,
                    maximumUseDistance = InteractionRangePolicy.ClampWorldUseRange(value.maximumUseDistance),
                    maximumFacingAngle = Mathf.Clamp(value.maximumFacingAngle, 0f, 180f),
                    exclusiveOccupancy = value.exclusiveOccupancy,
                    looping = value.looping,
                    fixedDurationSeconds = Mathf.Max(0f, value.fixedDurationSeconds),
                    lockMovement = value.lockMovement,
                    lockRotation = value.lockRotation,
                    cancelOnDamage = value.cancelOnDamage,
                    cancelOnMovement = value.cancelOnMovement,
                    cancelOnTargetUnavailable = value.cancelOnTargetUnavailable,
                    consentMode = value.consentMode,
                    contentLevel = value.contentLevel,
                    feature = value.feature,
                    participantRoles = participantRoles,
                };
            }
            return result;
        }

        private static InteractionPublicDefinition[] ConvertPublicInteractions(WorldInteractable.Interaction[] source)
        {
            source ??= Array.Empty<WorldInteractable.Interaction>();
            var result = new InteractionPublicDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                WorldInteractable.Interaction value = source[i] ?? new WorldInteractable.Interaction();
                InteractionCategoryId categoryId = value.categoryId == InteractionCategoryId.None
                    ? InteractionCategoryId.Use
                    : value.categoryId;
                result[i] = new InteractionPublicDefinition
                {
                    categoryId = categoryId,
                    actionId = value.actionId,
                    label = string.IsNullOrWhiteSpace(value.displayLabel) ? value.actionId.ToString() : value.displayLabel.Trim(),
                    maximumUseDistance = InteractionRangePolicy.ClampWorldUseRange(value.maximumUseDistance),
                    consentMode = value.consentMode,
                    contentLevel = value.contentLevel,
                    sortOrder = i <= short.MaxValue ? (short)i : short.MaxValue,
                };
            }
            return result;
        }

        private static ServerInteractionSlotDefinition[] ConvertSlots(WorldInteractable.Slot[] source)
        {
            source ??= Array.Empty<WorldInteractable.Slot>();
            var result = new ServerInteractionSlotDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                WorldInteractable.Slot slot = source[i] ?? new WorldInteractable.Slot();
                Transform anchor = slot.anchor;
                result[i] = new ServerInteractionSlotDefinition
                {
                    slotId = slot.slotId ?? string.Empty,
                    roleId = slot.roleId ?? string.Empty,
                    presentationRole = slot.presentationRole,
                    anchor = Pose(anchor),
                    approach = Pose(slot.approach),
                    exit = Pose(slot.exit),
                    hasApproach = slot.approach != null,
                    hasExit = slot.exit != null,
                    leftHandTarget = Pose(slot.leftHandTarget),
                    rightHandTarget = Pose(slot.rightHandTarget),
                    leftFootTarget = Pose(slot.leftFootTarget),
                    rightFootTarget = Pose(slot.rightFootTarget),
                    hasLeftHandTarget = slot.leftHandTarget != null,
                    hasRightHandTarget = slot.rightHandTarget != null,
                    hasLeftFootTarget = slot.leftFootTarget != null,
                    hasRightFootTarget = slot.rightFootTarget != null,
                };
            }
            return result;
        }

        private static void ExportBox(BoxCollider box, long baseId, ServerSurfaceFlags flags, List<ServerCollisionTriangle> output)
        {
            Vector3 h = box.size * 0.5f;
            Vector3 c = box.center;
            Vector3[] local =
            {
                c + new Vector3(-h.x,-h.y,-h.z), c + new Vector3(h.x,-h.y,-h.z),
                c + new Vector3(h.x,-h.y,h.z), c + new Vector3(-h.x,-h.y,h.z),
                c + new Vector3(-h.x,h.y,-h.z), c + new Vector3(h.x,h.y,-h.z),
                c + new Vector3(h.x,h.y,h.z), c + new Vector3(-h.x,h.y,h.z),
            };
            Vector3[] v = new Vector3[8];
            for (int i = 0; i < 8; ++i) v[i] = box.transform.TransformPoint(local[i]);
            int[] t =
            {
                0,2,1, 0,3,2, 4,5,6, 4,6,7,
                0,1,5, 0,5,4, 3,7,6, 3,6,2,
                0,4,7, 0,7,3, 1,2,6, 1,6,5,
            };
            for (int i = 0; i < t.Length; i += 3)
                AddTriangle(output, StableId(baseId + "/box/" + (i / 3)), v[t[i]], v[t[i + 1]], v[t[i + 2]], flags);
        }

        private static bool ExportMesh(MeshCollider collider, long baseId, ServerSurfaceFlags flags, List<ServerCollisionTriangle> output)
        {
            Mesh mesh = collider.sharedMesh;
            if (mesh == null)
                return false;
            if (!mesh.isReadable)
            {
                Debug.LogWarning($"[Server World Bake] MeshCollider '{HierarchyPath(collider.transform)}' uses non-readable mesh '{mesh.name}'. Use a readable/server proxy mesh for static collision.");
                return false;
            }

            Vector3[] vertices = mesh.vertices;
            int triangleOrdinal = 0;
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                int[] indices = mesh.GetTriangles(sub);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    Vector3 a = collider.transform.TransformPoint(vertices[indices[i]]);
                    Vector3 b = collider.transform.TransformPoint(vertices[indices[i + 1]]);
                    Vector3 c = collider.transform.TransformPoint(vertices[indices[i + 2]]);
                    AddTriangle(output, StableId(baseId + "/mesh/" + triangleOrdinal++), a, b, c, flags);
                }
            }
            return true;
        }

        private static void ExportTerrain(TerrainCollider collider, long baseId, ServerSurfaceFlags flags, int step, List<ServerCollisionTriangle> output)
        {
            TerrainData data = collider.terrainData;
            if (data == null)
                return;
            int resolution = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, resolution, resolution);
            Vector3 size = data.size;
            Vector3 origin = collider.transform.position;
            step = Mathf.Clamp(step, 1, 32);

            int ordinal = 0;
            for (int z = 0; z < resolution - 1; z += step)
            {
                int z1 = Mathf.Min(z + step, resolution - 1);
                for (int x = 0; x < resolution - 1; x += step)
                {
                    int x1 = Mathf.Min(x + step, resolution - 1);
                    Vector3 a = TerrainPoint(origin, size, heights, resolution, x, z);
                    Vector3 b = TerrainPoint(origin, size, heights, resolution, x1, z);
                    Vector3 c = TerrainPoint(origin, size, heights, resolution, x1, z1);
                    Vector3 d = TerrainPoint(origin, size, heights, resolution, x, z1);
                    AddTriangle(output, StableId(baseId + "/terrain/" + ordinal++), a, c, b, flags);
                    AddTriangle(output, StableId(baseId + "/terrain/" + ordinal++), a, d, c, flags);
                }
            }
        }

        private static Vector3 TerrainPoint(Vector3 origin, Vector3 size, float[,] heights, int resolution, int x, int z)
        {
            float nx = x / (float)(resolution - 1);
            float nz = z / (float)(resolution - 1);
            return origin + new Vector3(nx * size.x, heights[z, x] * size.y, nz * size.z);
        }

        private static void AddTriangle(List<ServerCollisionTriangle> output, long id, Vector3 a, Vector3 b, Vector3 c, ServerSurfaceFlags flags)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (normal.sqrMagnitude < 0.5f)
                return;
            output.Add(Triangle(id, a, b, c, normal, flags));
        }

        private static ServerCollisionTriangle Triangle(long id, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, ServerSurfaceFlags flags) =>
            new ServerCollisionTriangle
            {
                surfaceId = id,
                ax = a.x, ay = a.y, az = a.z,
                bx = b.x, by = b.y, bz = b.z,
                cx = c.x, cy = c.y, cz = c.z,
                normalX = normal.x, normalY = normal.y, normalZ = normal.z,
                flags = flags,
            };

        private static ServerPose Pose(Transform transform)
        {
            if (transform == null)
                return default;
            Vector3 p = transform.position;
            return new ServerPose(p.x, p.y, p.z, transform.eulerAngles.y);
        }

        private static string OutputPath(string mapId, string instanceId)
        {
            string suffix = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : "__" + ServerMap.NormalizeId(instanceId);
            return Path.Combine(ServerMapsFolder, ServerMap.NormalizeId(mapId) + suffix + ".servermap.json");
        }

        private static long ReadPreviousRevision(string file)
        {
            try
            {
                if (!File.Exists(file)) return 0;
                CoreHeader old = JsonUtility.FromJson<CoreHeader>(File.ReadAllText(file));
                return old?.bakeRevision ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static ServerWorldRules CloneWorldRules(ServerWorldRules source) => new ServerWorldRules
        {
            movementProfile = source?.movementProfile ?? ServerMovementAuthorityProfile.StrictWorld,
            combatAllowed = source?.combatAllowed ?? true,
            pvpAllowed = source?.pvpAllowed ?? true,
            vehiclesAllowed = source?.vehiclesAllowed ?? true,
        };

        private static ServerPropertyTemplateDefinition CloneProperty(ServerPropertyTemplateDefinition source, string mapId)
        {
            source ??= new ServerPropertyTemplateDefinition();
            ServerPlaceableCategoryLimit[] limits = source.categoryLimits ?? Array.Empty<ServerPlaceableCategoryLimit>();
            var copy = new ServerPlaceableCategoryLimit[limits.Length];
            for (int i = 0; i < limits.Length; ++i)
            {
                ServerPlaceableCategoryLimit limit = limits[i] ?? new ServerPlaceableCategoryLimit();
                copy[i] = new ServerPlaceableCategoryLimit
                {
                    category = limit.category ?? string.Empty,
                    maximum = Math.Max(0, limit.maximum),
                };
            }
            return new ServerPropertyTemplateDefinition
            {
                templateId = string.IsNullOrWhiteSpace(source.templateId) ? mapId : source.templateId,
                maximumPlaceables = Math.Max(0, source.maximumPlaceables),
                maximumCollisionPlaceables = Math.Max(0, source.maximumCollisionPlaceables),
                maximumInteractivePlaceables = Math.Max(0, source.maximumInteractivePlaceables),
                maximumDynamicStatePlaceables = Math.Max(0, source.maximumDynamicStatePlaceables),
                maximumStorageContainers = Math.Max(0, source.maximumStorageContainers),
                categoryLimits = copy,
            };
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }

        internal static long StableAuthoringId(string text) => StableId(text);

        private static long StableId(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            long value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
            return value == 0 ? 1 : value;
        }

        private static long StableId(long baseId, string suffix) => StableId(baseId.ToString() + suffix);

        private static string Sha256(string text)
        {
            using SHA256 sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; ++i)
                builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
