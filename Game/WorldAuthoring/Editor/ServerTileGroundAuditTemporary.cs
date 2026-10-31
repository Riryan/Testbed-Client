#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Temporary, read-only Editor audit of authored flat BoxCollider floors against the
    /// EXISTING server map and client shared-world bake. It does not bake, modify assets,
    /// add components, send messages, or run during gameplay. Remove this .cs file after
    /// investigating the problematic tile.
    /// </summary>
    public sealed class ServerTileGroundAuditTemporary : EditorWindow
    {
        private const float CapsuleRadius = 0.35f;
        private const float CapsuleHeight = 1.80f;
        private const float MaxSlope = 50f;
        private const float StepHeight = 0.40f;
        private const float SnapDown = 0.50f;
        private const float GroundTolerance = 0.08f;
        private const float ProbeUpDown = 0.60f;
        private const float InsideEdgeOffset = 0.25f;
        private const float CrossingDistance = 0.45f;

        private enum State : byte { Pending, Good, Bad }
        private sealed class Tile
        {
            public BoxCollider Box;
            public Vector3 Center;
            public Vector3[] Corners;
            public State Result;
            public string Reason = "Not scanned";
            public float HalfX;
            public float HalfZ;
            public float TopY;
            public float ScaleX;
            public float ScaleZ;
        }

        private readonly List<Tile> _tiles = new List<Tile>(1024);
        private readonly List<Tile> _badTiles = new List<Tile>(128);
        private ServerCollisionWorld _serverWorld;
        private ServerCollisionWorld _clientWorld;
        private ServerMap _map;
        private string _serverMapPath;
        private string _clientMapPath;
        private string _status = "Press Scan Current Baked Data. No rebake or scene changes.";
        private int _cursor;
        private int _good;
        private bool _running;
        private bool _draw = true;
        private bool _labels = true;
        private Vector2 _scroll;
        private Scene _sourceScene;

        [MenuItem("MMO Tools/World/Tile Ground Audit (Temporary)")]
        public static void Open()
        {
            var window = GetWindow<ServerTileGroundAuditTemporary>("Tile Ground Audit");
            window.minSize = new Vector2(430f, 365f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DrawOverlay;
            EditorApplication.update += ProcessBatch;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawOverlay;
            EditorApplication.update -= ProcessBatch;
            _running = false;
            _tiles.Clear();
            _badTiles.Clear();
            _serverWorld = null;
            _clientWorld = null;
            SceneView.RepaintAll();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Flat-floor tile coverage — read-only", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "GREEN = both baked worlds support the floor and its tested crossings. " +
                "RED = missing/invalid baked support, blocked standing capsule, or rejected " +
                "crossing between neighboring flat tiles. GREY = not checked yet. " +
                "Only eligible flat BoxColliders are colored. This does not modify scenes, " +
                "prefabs, maps or gameplay code.", MessageType.Info);

            using (new EditorGUI.DisabledScope(_running))
            {
                if (GUILayout.Button("Scan Current Baked Data", GUILayout.Height(30)))
                    StartScan();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear Overlay")) ClearResults();
            _draw = EditorGUILayout.ToggleLeft("Show in Scene", _draw, GUILayout.Width(120));
            _labels = EditorGUILayout.ToggleLeft("Red labels", _labels, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(_status, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Tiles: " + _tiles.Count + "    Checked: " + _cursor +
                "    Green: " + _good + "    Red: " + _badTiles.Count);
            if (_tiles.Count != 0)
            {
                Rect bar = GUILayoutUtility.GetRect(1f, 16f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(bar, (float)_cursor / _tiles.Count,
                    _running ? "Checking baked ground" : "Scan results");
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Red tiles — select/frame to inspect", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _badTiles.Count; i++)
            {
                Tile tile = _badTiles[i];
                if (tile.Box == null) continue;
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Frame", GUILayout.Width(54))) FrameTile(tile);
                EditorGUILayout.LabelField(tile.Box.name + " @ " +
                    tile.Center.x.ToString("0.0") + "," +
                    tile.Center.z.ToString("0.0"), GUILayout.Width(220));
                EditorGUILayout.LabelField(tile.Reason, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                "Green from this audit means the sampled baked geometry passed, not that " +
                "every client input or GameServer runtime condition has been tested. " +
                "If the problem tile is green, preserve the bake and investigate the " +
                "runtime movement/position-reporting path. Close this window to remove " +
                "the colors; delete this temporary .cs file when finished.", MessageType.None);
        }

        private void StartScan()
        {
            ClearResults();
            _sourceScene = SceneManager.GetActiveScene();
            if (!_sourceScene.IsValid() || !_sourceScene.isLoaded)
            {
                _status = "ERROR: No loaded active scene.";
                return;
            }

            _map = ServerWorldBake.FindMapInActiveScene();
            if (_map == null)
            {
                _status = "ERROR: No ServerMap component in the active scene (inactive is allowed).";
                return;
            }
            if (string.IsNullOrWhiteSpace(_map.MapId))
            {
                _status = "ERROR: ServerMap MapId is empty.";
                return;
            }

            string mapStem = ServerMapId.Normalize(_map.MapId);
            string instance = ServerMapId.Normalize(_map.InstanceId);
            if (!string.IsNullOrWhiteSpace(instance)) mapStem += "__" + instance;

            _serverMapPath = Path.Combine(ServerWorldBake.ServerMapsFolder,
                mapStem + ".servermap.json");
            _clientMapPath = Path.Combine(ServerWorldBake.ClientSharedWorldFolder,
                mapStem + ".sharedworld.json.gz");

            try
            {
                if (!File.Exists(_serverMapPath))
                    throw new FileNotFoundException("No existing server map bake", _serverMapPath);
                if (!File.Exists(_clientMapPath))
                    throw new FileNotFoundException("No existing client shared-world bake", _clientMapPath);

                ServerMapSnapshot server = JsonUtility.FromJson<ServerMapSnapshot>(
                    File.ReadAllText(_serverMapPath));
                if (server == null || server.formatVersion != ServerMapFormat.Version)
                    throw new InvalidDataException("Server map is missing or has an incompatible format version.");
                _serverWorld = new ServerCollisionWorld(server);

                SharedWorldSnapshot shared;
                using (var stream = File.OpenRead(_clientMapPath))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                    shared = JsonUtility.FromJson<SharedWorldSnapshot>(reader.ReadToEnd());

                if (shared == null || shared.formatVersion != SharedWorldFormat.Version ||
                    shared.supportTriangles == null || shared.supportTriangles.Length == 0)
                    throw new InvalidDataException("Client shared-world file has missing or incompatible support geometry.");

                // Match SharedMovementQueries' canonical world representation exactly.
                _clientWorld = new ServerCollisionWorld(new ServerMapSnapshot
                {
                    mapId = shared.mapId,
                    instanceId = shared.instanceId,
                    movementTriangles = shared.supportTriangles,
                    collisionTriangles = shared.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>(),
                    dynamicBlockers = shared.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>()
                });
            }
            catch (Exception ex)
            {
                _serverWorld = null;
                _clientWorld = null;
                _status = "ERROR: " + ex.Message + "\n" +
                    "No assets were changed. Existing baked data is required for this audit.";
                return;
            }

            CollectTiles(_sourceScene, _map);
            _cursor = 0;
            _running = _tiles.Count > 0;
            _status = _running
                ? "Checking authored flat BoxCollider floors against BOTH existing baked worlds."
                : "No eligible flat BoxCollider tiles found on ServerMap collision layers.";
            SceneView.RepaintAll();
        }

        private void CollectTiles(Scene scene, ServerMap map)
        {
            BoxCollider[] boxes = UnityEngine.Object.FindObjectsByType<BoxCollider>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (BoxCollider box in boxes)
            {
                if (box == null || !box.enabled || box.isTrigger ||
                    !box.gameObject.activeInHierarchy || box.gameObject.scene != scene ||
                    (map.CollisionLayers.value & (1 << box.gameObject.layer)) == 0 ||
                    box.GetComponentInParent<ServerBakeIgnore>() != null)
                    continue;
                WorldInteractable interactable = box.GetComponentInParent<WorldInteractable>();
                if (interactable != null && interactable.dynamicBlocker)
                    continue;
                ServerSurface surface = box.GetComponentInParent<ServerSurface>();
                if (surface != null && (surface.flags & ServerSurfaceFlags.Walkable) == 0)
                    continue;

                Transform tr = box.transform;
                Vector3 scale = tr.lossyScale;
                float scaleX = Mathf.Abs(scale.x);
                float scaleY = Mathf.Abs(scale.y);
                float scaleZ = Mathf.Abs(scale.z);
                float width = box.size.x * scaleX;
                float depth = box.size.z * scaleZ;
                float thickness = box.size.y * scaleY;
                if (Vector3.Dot(tr.up, Vector3.up) < 0.98f ||
                    width < 0.8f || depth < 0.8f ||
                    thickness > Mathf.Max(0.35f, Mathf.Min(width, depth) * 0.25f) ||
                    scaleX < 0.001f || scaleZ < 0.001f)
                    continue;

                var tile = new Tile
                {
                    Box = box,
                    HalfX = box.size.x * 0.5f,
                    HalfZ = box.size.z * 0.5f,
                    ScaleX = scaleX,
                    ScaleZ = scaleZ,
                    Corners = new Vector3[4],
                    Result = State.Pending
                };
                tile.Center = Point(tile, 0f, 0f);
                tile.TopY = tile.Center.y;
                tile.Corners[0] = Point(tile, -1f, -1f) + Vector3.up * 0.045f;
                tile.Corners[1] = Point(tile, 1f, -1f) + Vector3.up * 0.045f;
                tile.Corners[2] = Point(tile, 1f, 1f) + Vector3.up * 0.045f;
                tile.Corners[3] = Point(tile, -1f, 1f) + Vector3.up * 0.045f;
                _tiles.Add(tile);
            }
        }

        private static Vector3 Point(Tile tile, float normalizedX, float normalizedZ)
        {
            BoxCollider box = tile.Box;
            return box.transform.TransformPoint(new Vector3(
                box.center.x + normalizedX * tile.HalfX,
                box.center.y + box.size.y * 0.5f,
                box.center.z + normalizedZ * tile.HalfZ));
        }

        private void ProcessBatch()
        {
            if (!_running) return;
            if (_sourceScene != SceneManager.GetActiveScene())
            {
                _running = false;
                _status = "Scene changed; press Scan Current Baked Data again.";
                Repaint();
                return;
            }
            // Bounded Editor work so scenes with thousands of floor tiles remain responsive.
            int end = Mathf.Min(_cursor + 8, _tiles.Count);
            while (_cursor < end)
            {
                Tile tile = _tiles[_cursor++];
                if (tile.Box == null)
                {
                    tile.Result = State.Bad;
                    tile.Reason = "Object removed during scan";
                }
                else if (CheckTile(tile, out string reason))
                {
                    tile.Result = State.Good;
                    tile.Reason = "Baked support and crossing OK";
                    _good++;
                }
                else
                {
                    tile.Result = State.Bad;
                    tile.Reason = reason;
                }
                if (tile.Result == State.Bad) _badTiles.Add(tile);
            }

            if (_cursor == _tiles.Count)
            {
                _running = false;
                _status = "Finished: " + _good + " green, " + _badTiles.Count +
                    " red. Read each red tile's reason below.";
            }
            Repaint();
            SceneView.RepaintAll();
        }

        private bool CheckTile(Tile tile, out string reason)
        {
            // Center and eight near-edge/corner samples catch isolated missing baked
            // triangles as well as tile seams. Do not infer support from visual meshes.
            float insetX = Mathf.Clamp(InsideEdgeOffset / (tile.HalfX * tile.ScaleX),
                0.01f, 0.5f);
            float insetZ = Mathf.Clamp(InsideEdgeOffset / (tile.HalfZ * tile.ScaleZ),
                0.01f, 0.5f);
            float ex = 1f - insetX;
            float ez = 1f - insetZ;
            float[] xs = { 0f, ex, -ex, 0f, 0f, ex, ex, -ex, -ex };
            float[] zs = { 0f, 0f, 0f, ez, -ez, ez, -ez, ez, -ez };

            for (int i = 0; i < xs.Length; i++)
            {
                Vector3 sample = Point(tile, xs[i], zs[i]);
                if (!ValidateBoth(sample, tile.TopY, out reason))
                {
                    reason = "Surface sample " + i + ": " + reason;
                    return false;
                }
            }

            // Test only edges with another AUTHORING tile physically adjacent at
            // the same elevation. Exposed edges are not falsely marked as seams.
            for (int edge = 0; edge < 4; edge++)
            {
                Vector3 normal = edge == 0 ? tile.Box.transform.right :
                    edge == 1 ? -tile.Box.transform.right :
                    edge == 2 ? tile.Box.transform.forward : -tile.Box.transform.forward;
                Vector3 edgeCenter = edge == 0 ? Point(tile, 1f, 0f) :
                    edge == 1 ? Point(tile, -1f, 0f) :
                    edge == 2 ? Point(tile, 0f, 1f) : Point(tile, 0f, -1f);
                Vector3 inside = edgeCenter - normal * CrossingDistance;
                Vector3 outside = edgeCenter + normal * CrossingDistance;
                if (!HasFlatAuthoredNeighbor(tile, outside)) continue;

                if (!ValidateBoth(outside, tile.TopY, out reason))
                {
                    reason = "Adjacent tile baked test (edge " + edge + "): " + reason;
                    return false;
                }
                if (!ValidateBoth(inside, tile.TopY, out reason))
                {
                    reason = "Before crossing (edge " + edge + "): " + reason;
                    return false;
                }
                if (!CheckCrossing(_serverWorld, inside, outside, out reason))
                {
                    reason = "SERVER crossing edge " + edge + ": " + reason;
                    return false;
                }
                if (!CheckCrossing(_clientWorld, inside, outside, out reason))
                {
                    reason = "CLIENT crossing edge " + edge + ": " + reason;
                    return false;
                }
            }
            reason = "OK";
            return true;
        }

        private bool HasFlatAuthoredNeighbor(Tile tile, Vector3 otherSide)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile candidate = _tiles[i];
                if (candidate == tile || candidate.Box == null ||
                    Mathf.Abs(candidate.TopY - otherSide.y) > GroundTolerance)
                    continue;
                Vector3 local = candidate.Box.transform.InverseTransformPoint(otherSide);
                Vector3 center = candidate.Box.center;
                if (Mathf.Abs(local.x - center.x) <= candidate.HalfX + 0.001f &&
                    Mathf.Abs(local.z - center.z) <= candidate.HalfZ + 0.001f)
                    return true;
            }
            return false;
        }

        private bool ValidateBoth(Vector3 point, float authoredTop, out string reason)
        {
            if (!ValidatePoint(_serverWorld, point, authoredTop, "SERVER", out reason))
                return false;
            if (!ValidatePoint(_clientWorld, point, authoredTop, "CLIENT", out reason))
                return false;
            reason = "OK";
            return true;
        }

        private static bool ValidatePoint(ServerCollisionWorld world, Vector3 point,
            float authoredTop, string side, out string reason)
        {
            if (!world.TryFindGround(new WorldPosition(point.x, point.y, point.z),
                    CapsuleRadius, ProbeUpDown, ProbeUpDown, MaxSlope,
                    out ServerGroundHit support))
            {
                reason = side + " baked walkable ground MISSING";
                return false;
            }
            if (Mathf.Abs(support.Position.Y - authoredTop) > GroundTolerance)
            {
                reason = side + " baked height differs from collider (" +
                    support.Position.Y.ToString("0.000") + " vs " + authoredTop.ToString("0.000") + ")";
                return false;
            }
            if (!world.IsStandingCapsuleClear(support.Position,
                    new ServerCapsule(CapsuleRadius, CapsuleHeight), MaxSlope))
            {
                reason = side + " standing capsule BLOCKED";
                return false;
            }
            reason = "OK";
            return true;
        }

        private static bool CheckCrossing(ServerCollisionWorld world, Vector3 inside,
            Vector3 outside, out string reason)
        {
            // This is the existing canonical GroundedMove that the client uses.
            // Do not substitute NavMeshAgent or a different movement solver.
            WorldPosition from = new WorldPosition(inside.x, inside.y, inside.z);
            float dx = outside.x - inside.x;
            float dz = outside.z - inside.z;
            WorldPosition next = GroundedMove.Resolve(world, from, dx, dz,
                new ServerCapsule(CapsuleRadius, CapsuleHeight), StepHeight,
                SnapDown, MaxSlope, out _);
            float remaining = Mathf.Sqrt((next.X - outside.x) * (next.X - outside.x) +
                (next.Z - outside.z) * (next.Z - outside.z));
            if (remaining > 0.07f)
            {
                reason = "GroundedMove stopped short by " + remaining.ToString("0.000") + "m";
                return false;
            }
            reason = "OK";
            return true;
        }

        private void DrawOverlay(SceneView view)
        {
            if (!_draw || _tiles.Count == 0 || Event.current.type != EventType.Repaint)
                return;
            CompareFunction before = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
            foreach (Tile tile in _tiles)
            {
                if (tile.Box == null || tile.Corners == null) continue;
                Color outline = tile.Result == State.Good
                    ? new Color(0.03f, 0.95f, 0.20f, 0.96f)
                    : tile.Result == State.Bad
                        ? new Color(1f, 0.12f, 0.08f, 1f)
                        : new Color(0.8f, 0.8f, 0.8f, 0.85f);
                Color fill = outline;
                fill.a = tile.Result == State.Bad ? 0.34f : 0.16f;
                Handles.DrawSolidRectangleWithOutline(tile.Corners, fill, outline);
                if (_labels && tile.Result == State.Bad &&
                    (view.camera == null ||
                     Vector3.Distance(view.camera.transform.position, tile.Center) < 110f))
                    Handles.Label(tile.Center + Vector3.up * 0.20f,
                        tile.Box.name + ": " + tile.Reason);
            }
            Handles.zTest = before;
        }

        private static void FrameTile(Tile tile)
        {
            if (tile.Box == null) return;
            Selection.activeGameObject = tile.Box.gameObject;
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.Frame(new Bounds(tile.Center,
                    new Vector3(tile.HalfX * tile.ScaleX * 2f + 2f,
                        4f, tile.HalfZ * tile.ScaleZ * 2f + 2f)), false);
                view.Repaint();
            }
        }

        private void ClearResults()
        {
            _running = false;
            _cursor = 0;
            _good = 0;
            _tiles.Clear();
            _badTiles.Clear();
            _serverWorld = null;
            _clientWorld = null;
            _map = null;
            _status = "Cleared; no scene or baked files were changed.";
            SceneView.RepaintAll();
        }
    }
}
#endif
