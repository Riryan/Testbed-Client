using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Shared.Interactions;
using Game.Shared.Population;
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

        public static bool TryBakeCurrentScene(bool rebuildNavMesh, bool saveScene, out Result result)
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

            // Fail fast on ServerSpawn authoring before doing NavMesh/collision work. Duplicate
            // spawn identities are repaired deterministically here using the same scene-authoring
            // pattern already used by Population Route Markers.
            if (!ValidateSpawnAuthoring(scene, repairDuplicateIds: true, logSuccess: false))
                return false;

            // Rebuild an authored NavMesh when one exists. If this scene has no NavMesh
            // authoring setup at all, fall back to a temporary server-only walk bake below.
            if (rebuildNavMesh && map.BakeNavMesh)
                TryRebuildNavMesh(scene);

            NavMeshData temporaryWalkData = null;
            NavMeshDataInstance temporaryWalkInstance = default;
            bool temporaryWalkAdded = false;

            if (map.BakeNavMesh && !HasNavMeshTriangles())
            {
                if (!TryBuildTemporaryWalkSurface(scene, map, out temporaryWalkData, out temporaryWalkInstance))
                    return false;
                temporaryWalkAdded = true;
            }

            var triangles = new List<ServerCollisionTriangle>(8192);
            ServerCollisionTriangle[] supportTriangles;
            try
            {
                if (!ExportNavMesh(map, triangles))
                    return false;

                // Keep the navigable support surface separate from the broader static
                // collision package. The shared client world uses this for local support
                // queries while all exported collision remains available for obstruction.
                supportTriangles = triangles.ToArray();
            }
            finally
            {
                if (temporaryWalkAdded)
                {
                    temporaryWalkInstance.Remove();
                    if (temporaryWalkData != null)
                        Object.DestroyImmediate(temporaryWalkData);
                }
            }

            if (map.ExportStaticCollision)
                ExportStaticCollision(map, scene, triangles);

            if (!ExportInteractables(scene, out ServerWorldInteractableDefinition[] interactables, out ServerDynamicBlocker[] blockers))
                return false;
            // Population authoring is exported before spawns so any deterministic Route Marker
            // duplicate-ID repair is complete before ServerSpawn references are converted to
            // stable route-node IDs.
            if (!ExportPopulation(
                    scene,
                    out ServerPopulationRouteNode[] populationNodes,
                    out ServerPopulationRouteEdge[] populationEdges,
                    out ServerPopulationPortal[] populationPortals))
            {
                return false;
            }
            if (!ExportSpawns(scene, out ServerSpawnAnchor[] spawns))
                return false;

            if (!TryBakeServerNavMesh(scene, map, out ServerNavMeshInfo navMesh))
                return false;

            string output = OutputPath(map.MapId, map.InstanceId);
            Directory.CreateDirectory(ServerMapsFolder);
            long revision = Math.Max(1, ReadPreviousRevision(output) + 1);
            long bakedUtcTicks = DateTime.UtcNow.Ticks;

            if (!TryWriteSharedWorld(
                    map,
                    revision,
                    bakedUtcTicks,
                    supportTriangles,
                    triangles.ToArray(),
                    blockers,
                    interactables,
                    out ServerSharedWorldInfo sharedWorld))
            {
                return false;
            }

            var snapshot = new ServerMapSnapshot
            {
                formatVersion = ServerMapFormat.Version,
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
                collisionTriangles = triangles.ToArray(),
                dynamicBlockers = blockers,
                traversalLinks = Array.Empty<ServerTraversalLink>(),
                spawnAnchors = spawns,
                interactables = interactables,
                populationNodes = populationNodes,
                populationEdges = populationEdges,
                populationPortals = populationPortals,
            };

            snapshot.contentHash = string.Empty;
            string unhashed = JsonUtility.ToJson(snapshot, false);
            snapshot.contentHash = Sha256(unhashed);
            string json = JsonUtility.ToJson(snapshot, true);
            File.WriteAllText(output, json, new UTF8Encoding(false));

            if (saveScene && scene.isDirty)
                EditorSceneManager.SaveScene(scene);

            result = new Result(map.MapId, output, triangles.Count, interactables.Length, blockers.Length, spawns.Length);
            Debug.Log(
                $"[Server World Bake] {map.MapId} -> {output}\n" +
                $"  triangles={triangles.Count:n0}, interactables={interactables.Length:n0}, blockers={blockers.Length:n0}, spawns={spawns.Length:n0}, revision={snapshot.bakeRevision}\n" +
                $"  populationNodes={populationNodes.Length:n0}, populationEdges={populationEdges.Length:n0}, populationPortals={populationPortals.Length:n0}\n" +
                $"  serverNavMesh={navMesh.fileName}, hash={ShortHash(navMesh.contentHash)}\n" +
                $"  sharedWorld={sharedWorld.fileName}, hash={ShortHash(sharedWorld.contentHash)}");
            return true;
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
                string fileName = mapStem + ".sharedworld.json";
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
                string json = JsonUtility.ToJson(shared, true);
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(serverPath, json, utf8);
                File.WriteAllText(clientPath, json, utf8);

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
                    "Other collider types should use a simple server proxy.");
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
                0,2,1, 0,3,2,
                4,5,6, 4,6,7,
                0,1,5, 0,5,4,
                3,7,6, 3,6,2,
                0,4,7, 0,7,3,
                1,2,6, 1,6,5,
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

        private static bool ExportNavMesh(ServerMap map, List<ServerCollisionTriangle> output)
        {
            NavMeshTriangulation nav = NavMesh.CalculateTriangulation();
            if (nav.vertices == null || nav.indices == null || nav.indices.Length < 3)
            {
                Debug.LogError(
                    $"[Server World Bake] Map '{map.MapId}' has no baked NavMesh triangulation. " +
                    "The server needs a walk surface for authoritative grounding.");
                return false;
            }

            int triangleCount = nav.indices.Length / 3;
            for (int triangle = 0; triangle < triangleCount; ++triangle)
            {
                int baseIndex = triangle * 3;
                Vector3 a = nav.vertices[nav.indices[baseIndex]];
                Vector3 b = nav.vertices[nav.indices[baseIndex + 1]];
                Vector3 c = nav.vertices[nav.indices[baseIndex + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (normal.y < 0f)
                {
                    Vector3 swap = b;
                    b = c;
                    c = swap;
                    normal = -normal;
                }

                int area = nav.areas != null && triangle < nav.areas.Length ? nav.areas[triangle] : 0;
                ServerSurfaceFlags flags = map.FlagsForNavArea(area);
                flags |= ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground;
                output.Add(Triangle(StableId($"{map.MapId}/nav/{triangle}"), a, b, c, normal, flags));
            }
            return true;
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
                if (!collider.gameObject.isStatic && surface == null)
                    continue;
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
                long id = StableId(marker.BakeId);
                if (!ids.Add(id))
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
                    nodeType = marker.nodeType,
                    isDestination = marker.isDestination,
                    destinationTags = marker.destinationTags,
                    destinationGroup = marker.destinationGroup ?? string.Empty,
                    hardRestricted = marker.hardRestricted,
                    allowedNpcTypes = marker.allowedNpcTypes,
                    minimumWaitSeconds = Mathf.Max(0f, marker.minimumWaitSeconds),
                    maximumWaitSeconds = Mathf.Max(marker.minimumWaitSeconds, marker.maximumWaitSeconds),
                    actionId = (byte)Mathf.Clamp(marker.actionId, 0, 255),
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
                    if (explicitOneWayPairs.Contains(UndirectedPopulationPair(fromId, toId)))
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

            // Route Path is the fast road/sidewalk workflow: direct child markers connect in
            // sibling order, with optional looping and one-way flow.
            PopulationRoutePath[] paths = Object.FindObjectsByType<PopulationRoutePath>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var pathMarkers = new List<PopulationRouteMarker>(64);
            for (int i = 0; i < paths.Length; ++i)
            {
                PopulationRoutePath path = paths[i];
                if (path == null || path.gameObject.scene != scene || !path.gameObject.activeInHierarchy || !path.connectChildrenInOrder)
                    continue;
                path.CollectDirectMarkers(pathMarkers);
                for (int m = 1; m < pathMarkers.Count; ++m)
                    AddPathPopulationEdge(path, pathMarkers[m - 1], pathMarkers[m], markerIds, explicitOneWayPairs, edgeOutput, edgeKeys);
                if (path.loop && pathMarkers.Count > 2)
                    AddPathPopulationEdge(path, pathMarkers[pathMarkers.Count - 1], pathMarkers[0], markerIds, explicitOneWayPairs, edgeOutput, edgeKeys);
            }

            // Explicit one-way links are applied last and intentionally override bidirectional
            // auto/path links for that pair.
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

            PopulationPortal[] portalSources = Object.FindObjectsByType<PopulationPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();
            var portalOutput = new List<ServerPopulationPortal>(portalSources.Length);
            var portalIds = new HashSet<long>();
            var portalRouteNodes = new HashSet<long>();
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

                    EditorUtility.SetDirty(source);
                    EditorSceneManager.MarkSceneDirty(scene);
                    Debug.LogWarning(
                        $"[Server World Bake] Repaired duplicated Population Portal stable ID {duplicateId} at '{HierarchyPath(source.transform)}'; new stable ID is {id}. Save the scene to persist the repaired authoring ID.");
                }
                if (source.routeMarker == null || !markerIds.TryGetValue(source.routeMarker, out long routeNodeId))
                {
                    Debug.LogError($"[Server World Bake] Population Portal '{HierarchyPath(source.transform)}' requires a Route Marker from the same scene.");
                    nodes = Array.Empty<ServerPopulationRouteNode>();
                    edges = Array.Empty<ServerPopulationRouteEdge>();
                    portals = Array.Empty<ServerPopulationPortal>();
                    return false;
                }
                if (!portalRouteNodes.Add(routeNodeId))
                {
                    Debug.LogError($"[Server World Bake] Multiple Population Portals reference route node {routeNodeId}. Use one doorway portal per route marker.");
                    nodes = Array.Empty<ServerPopulationRouteNode>();
                    edges = Array.Empty<ServerPopulationRouteEdge>();
                    portals = Array.Empty<ServerPopulationPortal>();
                    return false;
                }

                long doorWorldObjectId = source.doorWorldObject != null
                    ? StableId(source.doorWorldObject.BakeId)
                    : 0L;
                portalOutput.Add(new ServerPopulationPortal
                {
                    stableId = id,
                    label = source.label ?? source.gameObject.name,
                    mode = source.mode,
                    portalType = source.portalType,
                    tags = source.tags != PopulationDestinationTag.None ? source.tags : TagsForPopulationPortalType(source.portalType),
                    allowedNpcTypes = source.allowedNpcTypes,
                    interiorSpawn = Pose(source.EffectiveInterior),
                    approach = Pose(source.EffectiveApproach),
                    interaction = Pose(source.EffectiveInteraction),
                    threshold = Pose(source.EffectiveThreshold),
                    exterior = Pose(source.EffectiveExterior),
                    routeNodeId = routeNodeId,
                    doorWorldObjectId = doorWorldObjectId,
                    minimumRespawnDelay = Mathf.Max(0f, source.minimumRespawnDelay),
                    maximumRespawnDelay = Mathf.Max(source.minimumRespawnDelay, source.maximumRespawnDelay),
                    blockedRetryDelay = Mathf.Max(0.1f, source.blockedRetryDelay),
                    minimumSpawnInterval = Mathf.Max(0f, source.minimumSpawnInterval),
                    maximumSpawnInterval = Mathf.Max(source.minimumSpawnInterval, source.maximumSpawnInterval),
                    spawnBurstLimit = Mathf.Max(1, source.spawnBurstLimit),
                    maximumActiveNearby = Mathf.Max(1, source.maximumActiveNearby),
                    activeNearbyRadius = Mathf.Max(0.5f, source.activeNearbyRadius),
                    exitClearanceRadius = Mathf.Max(0.1f, source.exitClearanceRadius),
                    requireDifferentDestination = source.requireDifferentDestination,
                    suppressWhenVisibleToPlayers = source.suppressWhenVisibleToPlayers,
                });
            }

            nodes = nodeOutput.ToArray();
            edges = edgeOutput.ToArray();
            portals = portalOutput.ToArray();
            return true;
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
            out ServerSpawn[] spawns)
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
            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];
                int count = spawn.kind == ServerSpawnKind.Population
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
                else if (spawn.routeNodeId > 0 && !routeIds.Contains(spawn.routeNodeId))
                {
                    Debug.LogError(
                        $"[Server World Bake] Spawn '{HierarchyPath(spawn.transform)}' has manual routeNodeId {spawn.routeNodeId}, " +
                        "but no active Population Route Marker in this scene bakes to that ID.");
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
            }

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

        private static bool ExportSpawns(Scene scene, out ServerSpawnAnchor[] anchors)
        {
            var output = new List<ServerSpawnAnchor>();
            if (!TryPrepareSpawns(scene, repairDuplicateIds: true, out ServerSpawn[] spawns))
            {
                anchors = Array.Empty<ServerSpawnAnchor>();
                return false;
            }

            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];

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

            anchors = output.ToArray();
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
                ServerMapSnapshot old = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(file));
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
