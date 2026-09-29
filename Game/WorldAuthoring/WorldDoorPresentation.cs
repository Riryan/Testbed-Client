using System.Collections.Generic;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Client-safe presentation only. Authoritative open/closed state comes from the
    /// standalone GameServer through the baked WorldInteractable stable ID.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldObject))]
    public sealed class WorldDoorPresentation : MonoBehaviour
    {
        private static readonly Dictionary<long, WorldDoorPresentation> ByStableId =
            new Dictionary<long, WorldDoorPresentation>();

        [Header("Door Visual")]
        [SerializeField] private Transform doorPivot;
        [SerializeField] private float openYawDegrees = 90f;
        [SerializeField, Min(0.05f)] private float openSeconds = 0.35f;
        [SerializeField] private bool visuallyOpenByDefault;

        private WorldObject _worldObject;
        private Quaternion _closedLocalRotation;
        private Quaternion _openLocalRotation;
        private bool _targetOpen;
        private bool _initialized;
        private long _registeredStableId;

        public long StableId => _worldObject != null ? _worldObject.StableId : 0L;
        public bool TargetOpen => _targetOpen;

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            Initialize();
            Register();
        }

        private void OnDisable()
        {
            Unregister();
        }

        private void OnDestroy()
        {
            Unregister();
        }

        private void Update()
        {
            if (!_initialized || doorPivot == null)
                return;

            Quaternion target = _targetOpen ? _openLocalRotation : _closedLocalRotation;
            float degreesPerSecond = Mathf.Max(1f, Mathf.Abs(openYawDegrees)) / Mathf.Max(0.05f, openSeconds);
            doorPivot.localRotation = Quaternion.RotateTowards(
                doorPivot.localRotation,
                target,
                degreesPerSecond * Time.deltaTime);
        }

        public void SetOpen(bool open, bool immediate = false)
        {
            Initialize();
            _targetOpen = open;
            if (immediate && doorPivot != null)
                doorPivot.localRotation = open ? _openLocalRotation : _closedLocalRotation;
        }

        public static bool TryGet(long stableId, out WorldDoorPresentation presentation)
        {
            presentation = null;
            if (stableId <= 0)
                return false;

            return ByStableId.TryGetValue(stableId, out presentation) && presentation != null;
        }

        private void Initialize()
        {
            if (_initialized)
                return;

            _worldObject = GetComponent<WorldObject>();
            if (doorPivot == null)
                doorPivot = transform;

            _closedLocalRotation = doorPivot.localRotation;
            _openLocalRotation = _closedLocalRotation * Quaternion.Euler(0f, openYawDegrees, 0f);
            _targetOpen = visuallyOpenByDefault;
            doorPivot.localRotation = _targetOpen ? _openLocalRotation : _closedLocalRotation;
            _initialized = true;
        }

        private void Register()
        {
            Unregister();
            long stableId = StableId;
            if (stableId <= 0)
                return;
            ByStableId[stableId] = this;
            _registeredStableId = stableId;
        }

        private void Unregister()
        {
            if (_registeredStableId > 0 &&
                ByStableId.TryGetValue(_registeredStableId, out WorldDoorPresentation current) &&
                current == this)
            {
                ByStableId.Remove(_registeredStableId);
            }
            _registeredStableId = 0;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            openSeconds = Mathf.Max(0.05f, openSeconds);
        }
#endif
    }
}
