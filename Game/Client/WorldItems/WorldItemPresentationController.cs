using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Client.UI.Interactions;
using Game.Shared.Interactions;
using Player.Networking;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Client.WorldItems
{
    /// <summary>
    /// Client-only presentation for authoritative standalone-GameServer world items.
    ///
    /// The authoritative coordinates live on each presentation root. Only child visuals
    /// bob/spin, so inspecting the root transform always shows the exact replicated world
    /// position. A client-only trigger is used for pointer selection; it has no networking,
    /// gameplay collision, pickup authority, or persistence ownership.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldItemPresentationController : MonoBehaviour
    {
        private sealed class PresentedItem
        {
            public GameObject Root;
            public Transform Marker;
            public Transform LabelTransform;
            public TextMesh Label;
            public ClientInteractionTargetMarker TargetMarker;
            public long Revision;
        }

        private const int MaxVisibleItems = 256;
        private const float MarkerBaseHeight = 0.32f;
        private const float MarkerBobHeight = 0.06f;
        private const float MarkerBobSpeed = 2.2f;
        private const float MarkerSpinDegreesPerSecond = 65f;
        private const float LabelHeight = 0.82f;
        private const float LabelVisibleDistance = 14f;
        private const float CameraRefreshSeconds = 1f;

        private readonly Dictionary<long, PresentedItem> _presented =
            new Dictionary<long, PresentedItem>();
        private readonly List<WorldItemWire> _scratch = new List<WorldItemWire>();
        private readonly HashSet<long> _desired = new HashSet<long>();
        private readonly Stack<PresentedItem> _pool = new Stack<PresentedItem>(64);

        private PlayerEntityGameManager _manager;
        private Transform _worldRoot;
        private Transform _localPlayerTransform;
        private Camera _cachedCamera;
        private float _nextCameraRefreshAt;
        private bool _bound;

        public void Bind(PlayerEntityGameManager manager)
        {
#if UNITY_SERVER
            return;
#else
            if (_manager == manager && _bound)
            {
                if (manager != null && manager.LatestWorldItems.success)
                    ApplySnapshot(manager.LatestWorldItems);
                return;
            }

            Unbind();
            _manager = manager;
            if (_manager == null)
                return;

            EnsureWorldRoot();
            _manager.WorldItemsChangedReceived += OnWorldItemsChanged;
            _manager.ClientWorldEntered += OnClientWorldEntered;
            _manager.ClientConnectionClosed += OnClientConnectionClosed;
            _bound = true;

            if (_manager.LatestWorldItems.success)
                ApplySnapshot(_manager.LatestWorldItems);
#endif
        }

        public void Unbind()
        {
#if !UNITY_SERVER
            if (_bound && _manager != null)
            {
                _manager.WorldItemsChangedReceived -= OnWorldItemsChanged;
                _manager.ClientWorldEntered -= OnClientWorldEntered;
                _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            }
#endif
            _bound = false;
            _manager = null;
            ClearVisuals();
        }

        private void OnDestroy()
        {
            Unbind();
            if (_worldRoot != null)
                Destroy(_worldRoot.gameObject);
        }

        private void LateUpdate()
        {
#if !UNITY_SERVER
            if (_presented.Count == 0)
                return;

            Camera camera = GetCachedCamera();
            float time = Time.time;
            float delta = Time.deltaTime;
            float labelDistanceSq = LabelVisibleDistance * LabelVisibleDistance;
            foreach (KeyValuePair<long, PresentedItem> pair in _presented)
            {
                PresentedItem presented = pair.Value;
                if (presented?.Root == null)
                    continue;

                if (presented.Marker != null)
                {
                    Vector3 local = presented.Marker.localPosition;
                    local.y = MarkerBaseHeight + Mathf.Sin(time * MarkerBobSpeed + (pair.Key & 31L)) * MarkerBobHeight;
                    presented.Marker.localPosition = local;
                    presented.Marker.Rotate(0f, MarkerSpinDegreesPerSecond * delta, 0f, Space.Self);
                }

                if (presented.LabelTransform != null)
                {
                    bool labelVisible = camera != null &&
                        (presented.LabelTransform.position - camera.transform.position).sqrMagnitude <= labelDistanceSq;
                    if (presented.LabelTransform.gameObject.activeSelf != labelVisible)
                        presented.LabelTransform.gameObject.SetActive(labelVisible);

                    if (labelVisible)
                    {
                        Vector3 awayFromCamera = presented.LabelTransform.position - camera.transform.position;
                        if (awayFromCamera.sqrMagnitude > 0.0001f)
                            presented.LabelTransform.rotation = Quaternion.LookRotation(awayFromCamera.normalized, Vector3.up);
                    }
                }
            }
#endif
        }

#if !UNITY_SERVER
        private void OnClientWorldEntered(long _)
        {
            // The standalone GameServer pushes the authoritative world-item baseline when
            // the player becomes Ready. Networking caches that snapshot even if presentation
            // initialization races admission, so bind from the cache instead of requesting it
            // a second time. Reconciliation remains available for actual revision gaps.
            if (_manager != null && _manager.LatestWorldItems.success)
                ApplySnapshot(_manager.LatestWorldItems);
        }

        private void OnClientConnectionClosed(ClientDisconnectNotice _)
        {
            ClearVisuals();
            _localPlayerTransform = null;
            _cachedCamera = null;
        }

        private void OnWorldItemsChanged(WorldItemsSnapshotMessage snapshot)
        {
            ApplySnapshot(snapshot);
        }

        private void ApplySnapshot(WorldItemsSnapshotMessage snapshot)
        {
            if (!snapshot.success)
            {
                ClearVisuals();
                return;
            }

            EnsureWorldRoot();
            WorldItemWire[] source = snapshot.items ?? Array.Empty<WorldItemWire>();
            _scratch.Clear();
            for (int i = 0; i < source.Length; ++i)
                if (source[i].itemInstanceId > 0)
                    _scratch.Add(source[i]);

            if (_scratch.Count > MaxVisibleItems)
            {
                Vector3 reference = FindLocalPlayerPosition();
                _scratch.Sort((a, b) =>
                {
                    float da = DistanceSquared(a, reference);
                    float db = DistanceSquared(b, reference);
                    int compare = da.CompareTo(db);
                    return compare != 0 ? compare : a.itemInstanceId.CompareTo(b.itemInstanceId);
                });
                _scratch.RemoveRange(MaxVisibleItems, _scratch.Count - MaxVisibleItems);
            }

            _desired.Clear();
            for (int i = 0; i < _scratch.Count; ++i)
            {
                WorldItemWire item = _scratch[i];
                _desired.Add(item.itemInstanceId);
                if (!_presented.TryGetValue(item.itemInstanceId, out PresentedItem presented) || presented?.Root == null)
                {
                    presented = CreatePresentation(item);
                    _presented[item.itemInstanceId] = presented;
                }
                RefreshPresentation(presented, item);
            }

            if (_presented.Count > _desired.Count)
            {
                var remove = new List<long>();
                foreach (long id in _presented.Keys)
                    if (!_desired.Contains(id))
                        remove.Add(id);
                for (int i = 0; i < remove.Count; ++i)
                    RemovePresentation(remove[i]);
            }
        }

        private PresentedItem CreatePresentation(WorldItemWire item)
        {
            if (_pool.Count > 0)
            {
                PresentedItem pooled = _pool.Pop();
                pooled.Revision = -1;
                if (pooled.Root != null)
                {
                    pooled.Root.SetActive(true);
                    pooled.Root.transform.SetParent(_worldRoot, false);
                }
                if (pooled.LabelTransform != null)
                    pooled.LabelTransform.gameObject.SetActive(false);
                return pooled;
            }

            var root = new GameObject($"WorldItem_{item.itemInstanceId}");
            root.transform.SetParent(_worldRoot, false);

            GameObject markerObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            markerObject.name = "LootMarker";
            markerObject.transform.SetParent(root.transform, false);
            markerObject.transform.localScale = new Vector3(0.20f, 0.20f, 0.20f);
            markerObject.transform.localPosition = new Vector3(0f, MarkerBaseHeight, 0f);
            Collider collider = markerObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }

            // A larger client-only trigger makes the small debug loot marker practical to
            // right-click. It never participates in authoritative collision, pickup range,
            // damage, ownership, or persistence.
            SphereCollider interactionCollider = root.AddComponent<SphereCollider>();
            interactionCollider.isTrigger = true;
            interactionCollider.center = new Vector3(0f, 0.35f, 0f);
            interactionCollider.radius = 0.65f;
            Renderer renderer = markerObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            var labelObject = new GameObject("LootLabel");
            labelObject.transform.SetParent(root.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, LabelHeight, 0f);
            TextMesh label = labelObject.AddComponent<TextMesh>();
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 32;
            label.characterSize = 0.035f;
            label.richText = false;

            ClientInteractionTargetMarker targetMarker = root.AddComponent<ClientInteractionTargetMarker>();
            targetMarker.Configure(
                InteractionTargetKind.NetworkWorldObject,
                item.itemInstanceId,
                SafeName(item));

            return new PresentedItem
            {
                Root = root,
                Marker = markerObject.transform,
                LabelTransform = labelObject.transform,
                Label = label,
                TargetMarker = targetMarker,
                Revision = -1,
            };
        }

        private static void RefreshPresentation(PresentedItem presented, WorldItemWire item)
        {
            presented.Root.name = $"WorldItem_{item.itemInstanceId}_{SafeName(item)}";
            presented.Root.transform.position = new Vector3(item.positionX, item.positionY, item.positionZ);
            presented.TargetMarker?.Configure(
                InteractionTargetKind.NetworkWorldObject,
                item.itemInstanceId,
                SafeName(item));

            if (presented.Revision == item.itemRevision)
                return;

            presented.Revision = item.itemRevision;
            string quantity = item.quantity > 1 ? $" x{item.quantity}" : string.Empty;
            presented.Label.text = $"{SafeName(item)}{quantity}\nID {item.itemInstanceId}\n({item.positionX:0.00}, {item.positionY:0.00}, {item.positionZ:0.00})";
        }

        private void RemovePresentation(long id)
        {
            if (!_presented.TryGetValue(id, out PresentedItem presented))
                return;
            _presented.Remove(id);
            ReturnToPool(presented);
        }

        private void ReturnToPool(PresentedItem presented)
        {
            if (presented?.Root == null)
                return;
            presented.Revision = -1;
            if (presented.Label != null)
                presented.Label.text = string.Empty;
            presented.Root.SetActive(false);
            if (_pool.Count < MaxVisibleItems)
                _pool.Push(presented);
            else
                Destroy(presented.Root);
        }

        private void ClearVisuals()
        {
            foreach (PresentedItem presented in _presented.Values)
                ReturnToPool(presented);
            _presented.Clear();
            _desired.Clear();
            _scratch.Clear();
        }

        private void EnsureWorldRoot()
        {
            if (_worldRoot != null)
                return;
            var root = new GameObject("__AuthoritativeWorldItems_ClientPresentation");
            _worldRoot = root.transform;
        }

        private static string SafeName(WorldItemWire item)
        {
            if (!string.IsNullOrWhiteSpace(item.displayName))
                return item.displayName;
            if (!string.IsNullOrWhiteSpace(item.definitionId))
                return item.definitionId;
            return "World Item";
        }

        private static float DistanceSquared(WorldItemWire item, Vector3 position)
        {
            float dx = item.positionX - position.x;
            float dy = item.positionY - position.y;
            float dz = item.positionZ - position.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private Vector3 FindLocalPlayerPosition()
        {
            if (_localPlayerTransform != null)
                return _localPlayerTransform.position;

            PlayerEntityNetwork[] entities = FindObjectsByType<PlayerEntityNetwork>(FindObjectsSortMode.None);
            for (int i = 0; i < entities.Length; ++i)
            {
                PlayerEntityNetwork entity = entities[i];
                if (entity != null && entity.IsSpawned && entity.IsOwnerClient)
                {
                    _localPlayerTransform = entity.transform;
                    return _localPlayerTransform.position;
                }
            }
            return Vector3.zero;
        }

        private Camera GetCachedCamera()
        {
            if (_cachedCamera != null)
                return _cachedCamera;
            if (Time.unscaledTime < _nextCameraRefreshAt)
                return null;
            _nextCameraRefreshAt = Time.unscaledTime + CameraRefreshSeconds;
            _cachedCamera = Camera.main;
            return _cachedCamera;
        }
#endif
    }
}
