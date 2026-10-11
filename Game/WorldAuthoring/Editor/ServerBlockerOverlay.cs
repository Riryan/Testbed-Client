using System;
using System.IO;
using System.Collections.Generic;
using Game.Server.Application.World;
using Game.Shared.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Diagnostic view over the exact baked geometry queried by ServerCollisionWorld.
    /// No gameplay changes, network traffic, or new walkability algorithm.
    /// </summary>
    [InitializeOnLoad]
    internal static class ServerBlockerOverlay
    {
        private const string EnabledKey = "MMO.ServerBlockerOverlay.Enabled";
        private const string RadiusKey = "MMO.ServerBlockerOverlay.Radius";
        private const string HeightKey = "MMO.ServerBlockerOverlay.Height";
        private const string SlopeKey = "MMO.ServerBlockerOverlay.Slope";
        private const string ReachKey = "MMO.ServerBlockerOverlay.Search";
        private static bool _enabled;
        private static float _radius;
        private static float _height;
        private static float _slope;
        private static float _reach;
        private static string _loadedPath;
        private static DateTime _writeTime;
        private static ServerMapSnapshot _snapshot;
        private static ServerCollisionWorld _world;
        private static Vector3 _hit;
        private static bool _hasHit;
        private static bool _walkable;
        private static string _reason = "Move mouse over baked geometry";
        private static ServerCollisionTriangle _surface;
        private static bool _hasSurface;
        private static bool _hasStart;
        private static Vector3 _start;
        private static string _travelResult = "Set Start to test actual server movement";
        private static Vector3 _blockedAt;
        private static bool _isRouteBlocked;
        private static float _stepHeight = .4f;
        private static float _groundSnap = .5f;
        private static bool _inspectionLocked;
        private static Rect _panel = new Rect(16f, 65f, 480f, 300f);
        private static Vector2 _reportScroll;
        private const int PanelId = 903114;
        private static Vector3 _inspectionPosition;
        private static string _inspectionReport = "Click Inspect Rejection on a red point.";
        private static readonly List<InspectedTriangle> InspectionTriangles = new List<InspectedTriangle>(5);
        private struct InspectedTriangle
        {
            public ServerCollisionTriangle Triangle;
            public float Distance;
            public bool Inside;
            public bool SlopeOk;
            public bool HeightOk;
            public float Height;
            public float Slope;
        }

        static ServerBlockerOverlay()
        {
            _enabled = EditorPrefs.GetBool(EnabledKey, false);
            _radius = EditorPrefs.GetFloat(RadiusKey, .35f);
            _height = EditorPrefs.GetFloat(HeightKey, 1.8f);
            _slope = EditorPrefs.GetFloat(SlopeKey, 50f);
            _reach = EditorPrefs.GetFloat(ReachKey, .85f);
            SceneView.duringSceneGui += Draw;
            EditorSceneManager.activeSceneChangedInEditMode += (a, b) => Reset();
        }

        [MenuItem("MMO Tools/World/Server Blocker Probe (Toggle)")]
        private static void Toggle()
        {
            _enabled = !_enabled;
            EditorPrefs.SetBool(EnabledKey, _enabled);
            if (!_enabled) Reset();
            SceneView.RepaintAll();
        }

        [MenuItem("MMO Tools/World/Server Blocker Probe (Toggle)", true)]
        private static bool ValidateToggle()
        {
            Menu.SetChecked("MMO Tools/World/Server Blocker Probe (Toggle)", _enabled);
            return true;
        }

        private static void Reset()
        {
            _snapshot = null;
            _world = null;
            _loadedPath = null;
            _hasHit = false;
            _hasSurface = false;
            _inspectionLocked = false;
            InspectionTriangles.Clear();
        }

        private static void Load()
        {
            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            if (map == null || string.IsNullOrWhiteSpace(map.MapId))
            {
                Reset();
                _reason = "No ServerMap in active scene";
                return;
            }
            string suffix = string.IsNullOrWhiteSpace(map.InstanceId) ? "" : "__" + ServerMap.NormalizeId(map.InstanceId);
            string path = Path.Combine(ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");
            if (!File.Exists(path))
            {
                Reset();
                _reason = "No baked server map. Use Bake Current Scene.";
                return;
            }
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (path == _loadedPath && stamp == _writeTime && _world != null) return;
            Reset();
            try
            {
                ServerMapSnapshot snapshot = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(path));
                if (snapshot == null || snapshot.formatVersion != ServerMapFormat.Version)
                {
                    _reason = "Invalid/stale baked map format. Re-bake Current Scene.";
                    return;
                }
                _world = new ServerCollisionWorld(snapshot);
                _snapshot = snapshot;
                _loadedPath = path;
                _writeTime = stamp;
                _reason = "Move mouse over baked geometry";
            }
            catch (Exception ex)
            {
                _reason = "Baked map load failed: " + ex.Message;
            }
        }

        private static void Draw(SceneView view)
        {
            if (!_enabled) return;
            Load();
            Event evt = Event.current;
            if (_world != null && !_inspectionLocked && evt.type == EventType.MouseDown &&
                evt.button == 0 && !evt.alt && !evt.control && !evt.command &&
                !_panel.Contains(evt.mousePosition) && GUIUtility.hotControl == 0)
            {
                Probe(HandleUtility.GUIPointToWorldRay(evt.mousePosition));
                if (_hasHit)
                {
                    _inspectionLocked = true;
                    _inspectionPosition = _hit;
                    Inspect(_inspectionPosition);
                    evt.Use();
                    view.Repaint();
                }
            }
            if (!_inspectionLocked && _world != null && (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag) &&
                !_panel.Contains(evt.mousePosition))
            {
                Probe(HandleUtility.GUIPointToWorldRay(evt.mousePosition));
                view.Repaint();
            }
            if (_hasHit && _world != null)
            {
                var center = _hit + Vector3.up * (_height * .5f);
                Handles.zTest = CompareFunction.Always;
                Handles.color = _walkable ? new Color(.1f, 1f, .25f, .95f) : Color.red;
                Handles.DrawWireDisc(_hit + Vector3.up * .025f, Vector3.up, _radius);
                Handles.DrawWireDisc(_hit + Vector3.up * _height, Vector3.up, _radius);
                Handles.DrawLine(_hit + Vector3.right * _radius, _hit + Vector3.up * _height + Vector3.right * _radius);
                Handles.DrawLine(_hit - Vector3.right * _radius, _hit + Vector3.up * _height - Vector3.right * _radius);
                Handles.DrawLine(_hit + Vector3.forward * _radius, _hit + Vector3.up * _height + Vector3.forward * _radius);
                Handles.DrawLine(_hit - Vector3.forward * _radius, _hit + Vector3.up * _height - Vector3.forward * _radius);
                if (!_walkable || _isRouteBlocked) DrawNearbyCollision();
                if (_hasStart) DrawRoute();
                if (_inspectionLocked) DrawInspection();
                if (_hasSurface)
                {
                    Handles.color = Color.yellow;
                    DrawTriangle(_surface, 4f);
                }
                Handles.Label(center + Vector3.right * (_radius + .1f), _hasStart ? (_isRouteBlocked ? "SERVER MOVE: BLOCKED" : "SERVER MOVE: REACHABLE") : (_walkable ? "GROUND OK (not movement tested)" : "GROUND REJECTED"), EditorStyles.whiteBoldLabel);
            }
            Handles.BeginGUI();
            float panelHeight = _inspectionLocked ? 455f : 265f;
            _panel.width = Mathf.Min(480f, Mathf.Max(200f, view.position.width));
            _panel.height = Mathf.Min(panelHeight, Mathf.Max(100f, view.position.height - 22f));
            _panel.x = Mathf.Clamp(_panel.x, 0f, Mathf.Max(0f, view.position.width - _panel.width));
            _panel.y = Mathf.Clamp(_panel.y, 0f, Mathf.Max(0f, view.position.height - _panel.height - 22f));
            _panel = GUI.Window(PanelId, _panel, DrawPanel, "Server collision probe — drag here");
            Handles.EndGUI();
        }

        private static void DrawPanel(int id)
        {
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Server collision probe", EditorStyles.boldLabel);
            if (GUILayout.Button("OFF", GUILayout.Width(52))) Toggle();
            GUILayout.EndHorizontal();
            GUILayout.Label(_reason, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label(_travelResult, EditorStyles.wordWrappedMiniLabel);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_inspectionLocked ? "Refresh Inspection" : "Inspect Rejection"))
            {
                if (_hasHit && _snapshot != null)
                {
                    _inspectionLocked = true;
                    _inspectionPosition = _hit;
                    Inspect(_inspectionPosition);
                }
            }
            if (GUILayout.Button("Resume Hover"))
            {
                _inspectionLocked = false;
                InspectionTriangles.Clear();
                _reportScroll = Vector2.zero;
            }
            GUILayout.EndHorizontal();
            if (_inspectionLocked)
            {
                _reportScroll = GUILayout.BeginScrollView(_reportScroll, GUILayout.Height(195));
                GUILayout.TextArea(_inspectionReport, GUI.skin.textArea, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Set Start (hover point)"))
            {
                if (_hasHit && _walkable) { _start = _hit; _hasStart = true; CheckRoute(); }
            }
            if (GUILayout.Button("Clear Start")) { _hasStart = false; _isRouteBlocked = false; _travelResult = "Set Start to test actual server movement"; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Radius", GUILayout.Width(44));
            float radius = EditorGUILayout.FloatField(_radius, GUILayout.Width(50));
            GUILayout.Label("Height", GUILayout.Width(42));
            float height = EditorGUILayout.FloatField(_height, GUILayout.Width(50));
            GUILayout.Label("Slope", GUILayout.Width(38));
            float slope = EditorGUILayout.FloatField(_slope, GUILayout.Width(50));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Climb", GUILayout.Width(44));
            float climb = EditorGUILayout.FloatField(_stepHeight, GUILayout.Width(55));
            GUILayout.Label("Snap", GUILayout.Width(36));
            float snap = EditorGUILayout.FloatField(_groundSnap, GUILayout.Width(55));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(climb, _stepHeight) || !Mathf.Approximately(snap, _groundSnap))
            { _stepHeight = Mathf.Clamp(climb, 0f, 4f); _groundSnap = Mathf.Clamp(snap, .05f, 4f); CheckRoute(); }
            GUILayout.Label("Ground OK does NOT mean reachable. Set Start for movement path test.", EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label("Uses baked server geometry, not Unity scene colliders.", EditorStyles.miniLabel);
            if (!Mathf.Approximately(radius, _radius) || !Mathf.Approximately(height, _height) || !Mathf.Approximately(slope, _slope))
            {
                _radius = Mathf.Clamp(radius, .05f, 4f);
                _height = Mathf.Clamp(height, _radius * 2f, 6f);
                _slope = Mathf.Clamp(slope, 0f, 89f);
                EditorPrefs.SetFloat(RadiusKey, _radius);
                EditorPrefs.SetFloat(HeightKey, _height);
                EditorPrefs.SetFloat(SlopeKey, _slope);
                if (_hasHit) Check(_hit);
            }
            GUI.DragWindow(new Rect(0f, 0f, _panel.width, 20f));
        }

        private static void Probe(Ray ray)
        {
            _hasHit = false;
            _hasSurface = false;
            if (_snapshot == null) return;
            float best = float.MaxValue;
            // Pick actual baked solid geometry, including invisible server blockers.
            ServerCollisionTriangle[] triangles = _snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
            for (int i = 0; i < triangles.Length; i++)
            {
                if (RayTriangle(ray, triangles[i], out float t) && t > 0 && t < best)
                {
                    best = t;
                    _surface = triangles[i];
                    _hasSurface = true;
                }
            }
            if (!_hasSurface)
            {
                // Movement triangles can exist without a corresponding solid triangle.
                triangles = _snapshot.movementTriangles ?? Array.Empty<ServerCollisionTriangle>();
                for (int i = 0; i < triangles.Length; i++)
                {
                    if (RayTriangle(ray, triangles[i], out float t) && t > 0 && t < best)
                    {
                        best = t;
                        _surface = triangles[i];
                        _hasSurface = true;
                    }
                }
            }
            if (!_hasSurface) { _reason = "No baked surface under mouse"; return; }
            _hit = ray.GetPoint(best);
            _hasHit = true;
            Check(_hit);
        }

        private static void Check(Vector3 p)
        {
            var feet = new WorldPosition(p.x, p.y, p.z);
            var capsule = new ServerCapsule(_radius, _height);
            if (!_world.TryFindGround(feet, _radius, .6f, .6f, _slope, out ServerGroundHit ground))
            {
                _walkable = false;
                _reason = "BLOCKED: no walkable supporting ground within 0.6m / slope limit";
                CheckRoute();
                return;
            }
            _hit = new Vector3(ground.Position.X, ground.Position.Y, ground.Position.Z);
            if (!_world.IsStandingCapsuleClear(ground.Position, capsule, _slope))
            {
                _walkable = false;
                _reason = "BLOCKED: standing capsule fails server collision / slope check";
                CheckRoute();
                return;
            }
            _walkable = true;
            _reason = string.Format("GROUND OK: ({0:0.00}, {1:0.00}, {2:0.00}), surface #{3}",
                _hit.x, _hit.y, _hit.z, ground.SurfaceId);
            CheckRoute();
        }

        private static void CheckRoute()
        {
            _isRouteBlocked = false;
            if (!_hasStart || !_hasHit || _world == null) return;
            if (!_walkable) { _isRouteBlocked = true; _blockedAt = _hit; _travelResult = "BLOCKED: destination has no valid standing support"; return; }
            float dx = _hit.x - _start.x, dz = _hit.z - _start.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            int segments = Mathf.CeilToInt(length / Mathf.Max(.10f, _radius * .5f));
            if (segments > 512) { _travelResult = "Route too long for bounded diagnostic (max 512 samples)"; return; }
            WorldPosition current = new WorldPosition(_start.x, _start.y, _start.z);
            var capsule = new ServerCapsule(_radius, _height);
            for (int i = 1; i <= segments; ++i)
            {
                float x = Mathf.Lerp(_start.x, _hit.x, i / (float)segments);
                float z = Mathf.Lerp(_start.z, _hit.z, i / (float)segments);
                WorldPosition next = _world.ResolveHorizontalMove(current, x-current.X, z-current.Z,
                    capsule, _stepHeight, _groundSnap, _slope, true, out bool blocked);
                float distance = Mathf.Sqrt((next.X-x)*(next.X-x)+(next.Z-z)*(next.Z-z));
                if (blocked || distance > .015f)
                {
                    _isRouteBlocked = true;
                    _blockedAt = new Vector3(x, current.Y, z);
                    _travelResult = string.Format("MOVE BLOCKED at ({0:0.00}, {1:0.00}, {2:0.00}); stopped/slide or failed ground, climb, snap, or dynamic blocker check", x,current.Y,z);
                    return;
                }
                current = next;
            }
            _travelResult = "MOVEMENT REACHABLE along tested straight line (grounded ServerCollisionWorld)";
        }

        private static void DrawRoute()
        {
            Handles.zTest = CompareFunction.Always;
            Handles.color = _isRouteBlocked ? Color.red : Color.green;
            Handles.DrawAAPolyLine(4f, _start + Vector3.up * .12f, (_isRouteBlocked ? _blockedAt : _hit) + Vector3.up * .12f);
            Handles.DrawWireDisc(_start + Vector3.up * .04f, Vector3.up, _radius);
            if (_isRouteBlocked) Handles.DrawWireDisc(_blockedAt + Vector3.up * .04f, Vector3.up, _radius);
        }

        // Diagnostic only: duplicates the server's explicit triangle containment and vertical
        // eligibility rules without modifying movement code or the collision world.
        private static void Inspect(Vector3 feet)
        {
            InspectionTriangles.Clear();
            var triangles = _snapshot.movementTriangles ?? Array.Empty<ServerCollisionTriangle>();
            float ring = _radius * .6f;
            float minNormalY = Mathf.Cos(_slope * Mathf.Deg2Rad);
            float up = .6f, down = .6f; // Exactly the hover probe's ground query window.
            int[] sampleValid = new int[5];
            int[] sampleInside = new int[5];
            int[] sampleSlope = new int[5];
            int[] sampleHeight = new int[5];
            var candidates = new List<InspectedTriangle>();
            for (int i = 0; i < triangles.Length; i++)
            {
                ServerCollisionTriangle t = triangles[i];
                float slope = Mathf.Acos(Mathf.Clamp(t.normalY, -1f, 1f)) * Mathf.Rad2Deg;
                for (int k = 0; k < 5; k++)
                {
                    float x = feet.x + (k == 1 ? ring : k == 2 ? -ring : 0f);
                    float z = feet.z + (k == 3 ? ring : k == 4 ? -ring : 0f);
                    bool inside = TryHeight(t, x, z, out float height);
                    if (!inside) continue;
                    sampleInside[k]++;
                    bool slopeOk = t.normalY >= minNormalY;
                    bool heightOk = height >= feet.y - down && height <= feet.y + up;
                    if (slopeOk) sampleSlope[k]++;
                    if (heightOk) sampleHeight[k]++;
                    if (slopeOk && heightOk) sampleValid[k]++;
                }
                // Rank triangles near the center by horizontal distance to their edges.
                float distance = DistanceToTriangleXZ(t, feet.x, feet.z);
                if (distance > Mathf.Max(3f, _radius * 4f)) continue;
                bool centerInside = TryHeight(t, feet.x, feet.z, out float centerHeight);
                float h = centerInside ? centerHeight : (t.ay + t.by + t.cy) / 3f;
                candidates.Add(new InspectedTriangle
                {
                    Triangle = t, Distance = distance, Inside = centerInside,
                    Slope = slope, Height = h, SlopeOk = t.normalY >= minNormalY,
                    HeightOk = h >= feet.y - down && h <= feet.y + up
                });
            }
            candidates.Sort((a, b) =>
            {
                int compare = a.Distance.CompareTo(b.Distance);
                return compare != 0 ? compare : Mathf.Abs(a.Height - feet.y).CompareTo(Mathf.Abs(b.Height - feet.y));
            });
            for (int i = 0; i < Mathf.Min(5, candidates.Count); i++) InspectionTriangles.Add(candidates[i]);
            string[] labels = { "Center", "+X", "-X", "+Z", "-Z" };
            var sb = new System.Text.StringBuilder(1152);
            sb.AppendFormat("Point ({0:F3}, {1:F3}, {2:F3})\n", feet.x, feet.y, feet.z);
            sb.AppendFormat("Walk triangles: {0}, slope <= {1:F1} deg, Y +/-0.6m\n", triangles.Length, _slope);
            sb.AppendLine("Footprint samples (inside / slope-pass / height-pass / valid):");
            for (int k = 0; k < 5; k++)
                sb.AppendFormat(" {0}: {1} / {2} / {3} / {4}\n", labels[k], sampleInside[k], sampleSlope[k], sampleHeight[k], sampleValid[k]);
            int successfulSamples = 0;
            for (int k = 0; k < 5; k++) if (sampleValid[k] > 0) successfulSamples++;
            bool supported = sampleValid[0] > 0 || successfulSamples >= 2;
            sb.AppendLine(supported ? "SUPPORT FOUND (check movement step/snap or blockers)" : "NO ACCEPTED SUPPORT at tested samples");
            sb.AppendLine("Nearest movement triangles (not physical colliders):");
            if (InspectionTriangles.Count == 0) sb.AppendLine(" NONE within search radius - likely missing baked coverage");
            for (int i = 0; i < InspectionTriangles.Count; i++)
            {
                var c = InspectionTriangles[i];
                string reason = !c.Inside ? "OUTSIDE" : !c.SlopeOk ? "SLOPE" : !c.HeightOk ? "HEIGHT" : "ELIGIBLE";
                sb.AppendFormat(" {0}: ID {1} distXZ {2:F3}m {3}\n", i+1, c.Triangle.surfaceId, c.Distance, reason);
                sb.AppendFormat("    Y {0:F3}m slope {1:F1} deg\n", c.Height, c.Slope);
            }
            sb.AppendLine("Uses current baked snapshot. No live runtime blocker state.");
            _inspectionReport = sb.ToString();
        }

        private static bool TryHeight(ServerCollisionTriangle t, float x, float z, out float y)
        {
            float v0x = t.bx-t.ax, v0z = t.bz-t.az;
            float v1x = t.cx-t.ax, v1z = t.cz-t.az;
            float v2x = x-t.ax, v2z = z-t.az;
            float den = v0x*v1z-v1x*v0z;
            if (Mathf.Abs(den) <= .0000001f) { y = 0f; return false; }
            float u = (v2x*v1z-v1x*v2z)/den;
            float v = (v0x*v2z-v2x*v0z)/den;
            const float epsilon = .0005f;
            if (u < -epsilon || v < -epsilon || u+v > 1f+epsilon) { y = 0f; return false; }
            y = t.ay+u*(t.by-t.ay)+v*(t.cy-t.ay);
            return !float.IsNaN(y) && !float.IsInfinity(y);
        }

        private static float DistanceToTriangleXZ(ServerCollisionTriangle t, float x, float z)
        {
            if (TryHeight(t, x, z, out _)) return 0f;
            Vector2 p = new Vector2(x,z);
            Vector2 a = new Vector2(t.ax,t.az), b = new Vector2(t.bx,t.bz), c = new Vector2(t.cx,t.cz);
            return Mathf.Min(DistanceSegment(p,a,b), Mathf.Min(DistanceSegment(p,b,c),DistanceSegment(p,c,a)));
        }

        private static float DistanceSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 v = b-a;
            float sq = v.sqrMagnitude;
            float t = sq < 1e-10f ? 0f : Mathf.Clamp01(Vector2.Dot(p-a,v)/sq);
            return Vector2.Distance(p,a+t*v);
        }

        private static void DrawInspection()
        {
            Handles.zTest = CompareFunction.Always;
            for (int i = InspectionTriangles.Count-1; i >= 0; i--)
            {
                var candidate = InspectionTriangles[i];
                Handles.color = i == 0 ? Color.cyan :
                    candidate.Inside && candidate.SlopeOk && candidate.HeightOk ? Color.green : Color.red;
                DrawTriangle(candidate.Triangle, i == 0 ? 5f : 2f);
            }
            Handles.color = Color.yellow;
            Handles.DrawWireDisc(_inspectionPosition+Vector3.up*.05f, Vector3.up, .12f);
        }

        private static bool RayTriangle(Ray ray, ServerCollisionTriangle t, out float distance)
        {
            distance = 0;
            Vector3 a = new Vector3(t.ax, t.ay, t.az);
            Vector3 b = new Vector3(t.bx, t.by, t.bz);
            Vector3 c = new Vector3(t.cx, t.cy, t.cz);
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-7f) return false;
            float inv = 1f / det;
            Vector3 s = ray.origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(ray.direction, q) * inv;
            if (v < 0 || u + v > 1) return false;
            distance = Vector3.Dot(e2, q) * inv;
            return distance >= 0;
        }

        private static void DrawNearbyCollision()
        {
            // Highlight nearby physical geometry only. This is not an exact collision attribution:
            // ServerCollisionWorld owns the authoritative overlap test.
            var triangles = _snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
            Handles.color = new Color(1f, .15f, .1f, .96f);
            int count = 0;
            float r = _radius + .45f;
            float bottom = _hit.y - .12f, top = _hit.y + _height + .12f;
            for (int i = 0; i < triangles.Length && count < 350; i++)
            {
                var t = triangles[i];
                float minX = Mathf.Min(t.ax, Mathf.Min(t.bx, t.cx));
                float maxX = Mathf.Max(t.ax, Mathf.Max(t.bx, t.cx));
                float minZ = Mathf.Min(t.az, Mathf.Min(t.bz, t.cz));
                float maxZ = Mathf.Max(t.az, Mathf.Max(t.bz, t.cz));
                float minY = Mathf.Min(t.ay, Mathf.Min(t.by, t.cy));
                float maxY = Mathf.Max(t.ay, Mathf.Max(t.by, t.cy));
                if (maxX < _hit.x-r || minX > _hit.x+r || maxZ < _hit.z-r || minZ > _hit.z+r || maxY < bottom || minY > top) continue;
                DrawTriangle(t, 2f);
                count++;
            }
            var blockers = _snapshot.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>();
            foreach (var b in blockers)
            {
                if (b == null || !b.enabledByDefault) continue;
                Vector3 at = new Vector3(b.pose.x, b.pose.y, b.pose.z);
                if ((at - _hit).sqrMagnitude > 20f * 20f) continue;
                using (new Handles.DrawingScope(Matrix4x4.TRS(at, Quaternion.Euler(0f, b.pose.yaw, 0f), Vector3.one)))
                    Handles.DrawWireCube(Vector3.up * (b.sizeY * .5f), new Vector3(b.sizeX, b.sizeY, b.sizeZ));
            }
        }

        private static void DrawTriangle(ServerCollisionTriangle t, float width)
        {
            Vector3 a = new Vector3(t.ax,t.ay,t.az);
            Vector3 b = new Vector3(t.bx,t.by,t.bz);
            Vector3 c = new Vector3(t.cx,t.cy,t.cz);
            Handles.DrawAAPolyLine(width, a, b, c, a);
        }
    }
}
