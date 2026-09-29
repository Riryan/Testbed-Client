using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    /// <summary>
    /// Bounded, scheduler-governed MMO AOI.
    ///
    /// Design goals:
    /// - one observer anchor per connection
    /// - spatial hash by logical partition + sub-channel
    /// - exact range checks after cell lookup
    /// - bounded candidate checks, observer fan-out and subscription diffs
    /// - target work spread cooperatively through CoreRuntimeScheduler
    /// - no whole-world RebuildAll() spike
    /// - pinned/mandatory observers win before nearest observers
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Network/LiteNetLibManager/MMO Spatial Interest Manager")]
    public sealed class SpatialInterestManager : BaseInterestManager, ICoreBudgetedWorkSystem
    {
        [Header("Spatial Grid")]
        [Min(1f)] public float cellSize = 40f;
        public bool useDistanceCheck = true;
        public bool requireSameUnityScene = false;
        [Range(1, 32)] public int maxCellRadius = 8;

        public enum ProjectionMode
        {
            XZ_3D,
            XY_2D,
        }

        public ProjectionMode projectionMode = ProjectionMode.XZ_3D;

        [Header("Bounded Fan-out")]
        [Tooltip("Normal maximum observers retained for one identity.")]
        [Min(1)] public int maxObserversPerEntity = 128;
        [Tooltip("Absolute ceiling for spatially selected observers, cap overrides and mandatory/pinned observers. AlwaysVisible identities bypass fan-out selection caps by contract; use CoreGlobalBroadcaster for non-entity global state.")]
        [Min(1)] public int hardMaxObserversPerEntity = 512;
        [Tooltip("Hard ceiling on candidate observer checks for one spatial target rebuild. AlwaysVisible targets intentionally bypass this total cap and are amortized only by candidateChecksPerWorkUnit so every eligible observer is eventually considered.")]
        [Min(1)] public int maxCandidateChecksPerTarget = 4096;
        [Tooltip("Candidate checks performed by one cooperative scheduler work unit.")]
        [Range(1, 512)] public int candidateChecksPerWorkUnit = 64;
        [Tooltip("Maximum existing subscribers sampled during one target rebuild. Excess legacy subscribers converge over later passes.")]
        [Min(1)] public int maxExistingSubscribersSnapshot = 2048;
        [Tooltip("Subscription additions/removals performed by one cooperative scheduler work unit.")]
        [Range(1, 512)] public int subscriptionChangesPerWorkUnit = 64;

        [Header("Observer Anchor Maintenance")]
        [Tooltip("Hard ceiling on owned network objects inspected when selecting a connection AOI anchor.")]
        [Range(1, 2048)] public int maxOwnedObjectsScannedForAnchor = 256;
        [Tooltip("Maximum explicit observer refresh requests queued from movement/ownership/spawn hooks.")]
        [Min(1)] public int maxPendingObserverRefreshes = 4096;
        [Tooltip("Observer anchors checked for cell/partition movement per maintenance work unit.")]
        [Range(1, 256)] public int observerRefreshPerWorkUnit = 16;
        [Tooltip("Completed target rebuilds between observer-grid maintenance units.")]
        [Range(1, 64)] public int targetRebuildsPerObserverRefresh = 4;
        [Tooltip("Maximum spatial cells retained in the local pool after becoming empty.")]
        [Min(0)] public int maxPooledCells = 2048;

        [Header("Core Runtime Governance")]
        [Tooltip("Existing bounded CoreRuntimeScheduler channel used by AOI.")]
        public string schedulerChannel = "Gameplay";
        [Range(1, 1024)] public int maxWorkUnitsPerSchedulerTick = 256;
        [Tooltip("Single cooperative AOI work unit over this cost accumulates scheduler overrun strikes.")]
        [Min(0.05f)] public float workUnitInvocationLimitMilliseconds = 2.0f;

        [Header("Pinned / Mandatory Visibility")]
        [Tooltip("Maximum explicit pinned observers retained for one target.")]
        [Range(1, 2048)] public int maxPinnedObserversPerEntity = 256;
        [Tooltip("Global admission limit for explicit pin relationships.")]
        [Min(1)] public int maxTotalPinnedObservers = 16384;
        [Tooltip("Pinned observers still have to be in the same logical partition. Owner visibility is always preserved.")]
        public bool pinsRequireSamePartition = true;

        [Header("Diagnostics")]
        public bool warnOnHardLimit = false;
        [Min(0.1f)] public float warningCooldownSeconds = 5f;

        [SerializeField] private int runtimeTargetCount;
        [SerializeField] private int runtimeObserverCount;
        [SerializeField] private int runtimeGridCellCount;
        [SerializeField] private long processedTargetRebuilds;
        [SerializeField] private long candidateChecks;
        [SerializeField] private long candidateLimitHits;
        [SerializeField] private long cellRadiusClampHits;
        [SerializeField] private long softCapHits;
        [SerializeField] private long hardCapHits;
        [SerializeField] private long subscriptionAdds;
        [SerializeField] private long subscriptionRemoves;
        [SerializeField] private long observerCellMoves;
        [SerializeField] private long observerRefreshQueueRejections;
        [SerializeField] private int maximumObservedSubscribers;
        [SerializeField] private int totalPinnedObservers;

        public int RuntimeTargetCount => runtimeTargetCount;
        public int RuntimeObserverCount => runtimeObserverCount;
        public int RuntimeGridCellCount => runtimeGridCellCount;
        public long ProcessedTargetRebuilds => processedTargetRebuilds;
        public long CandidateChecks => candidateChecks;
        public long CandidateLimitHits => candidateLimitHits;
        public long CellRadiusClampHits => cellRadiusClampHits;
        public long SoftCapHits => softCapHits;
        public long HardCapHits => hardCapHits;
        public long SubscriptionAdds => subscriptionAdds;
        public long SubscriptionRemoves => subscriptionRemoves;
        public long ObserverCellMoves => observerCellMoves;
        public long ObserverRefreshQueueRejections => observerRefreshQueueRejections;
        public int MaximumObservedSubscribers => maximumObservedSubscribers;
        public int TotalPinnedObservers => totalPinnedObservers;

        public string Name => "MMO Spatial AOI";

        private sealed class TargetEntry
        {
            public LiteNetLibIdentity Identity;
            public IMMOInterestPartitionProvider PartitionProvider;
            public IMMOObserverCapPolicy CapPolicy;
        }

        private sealed class ObserverEntry
        {
            public long ConnectionId;
            public LiteNetLibIdentity Anchor;
            public IMMOInterestPartitionProvider PartitionProvider;
            public SpatialCellKey CellKey;
            public int IndexInCell = -1;
            public bool InGrid;
        }

        private sealed class SpatialCell
        {
            public readonly List<long> Connections = new List<long>(16);
        }

        private sealed class PinBucket
        {
            public readonly Dictionary<long, int> Priorities = new Dictionary<long, int>();
        }

        private readonly struct PartitionKey : IEquatable<PartitionKey>
        {
            public readonly int MapId;
            public readonly int InstanceId;
            public readonly int SceneHandle;
            public readonly string SubChannelId;

            public PartitionKey(int mapId, int instanceId, int sceneHandle, string subChannelId)
            {
                MapId = mapId;
                InstanceId = instanceId;
                SceneHandle = sceneHandle;
                SubChannelId = subChannelId ?? string.Empty;
            }

            public bool Equals(PartitionKey other)
            {
                return MapId == other.MapId &&
                       InstanceId == other.InstanceId &&
                       SceneHandle == other.SceneHandle &&
                       string.Equals(SubChannelId, other.SubChannelId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is PartitionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + MapId;
                    hash = hash * 31 + InstanceId;
                    hash = hash * 31 + SceneHandle;
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(SubChannelId ?? string.Empty);
                    return hash;
                }
            }
        }

        private readonly struct SpatialCellKey : IEquatable<SpatialCellKey>
        {
            public readonly PartitionKey Partition;
            public readonly int X;
            public readonly int Y;

            public SpatialCellKey(PartitionKey partition, int x, int y)
            {
                Partition = partition;
                X = x;
                Y = y;
            }

            public bool Equals(SpatialCellKey other)
            {
                return X == other.X && Y == other.Y && Partition.Equals(other.Partition);
            }

            public override bool Equals(object obj)
            {
                return obj is SpatialCellKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Partition.GetHashCode();
                    hash = hash * 397 ^ X;
                    hash = hash * 397 ^ Y;
                    return hash;
                }
            }
        }

        private struct SelectedObserver
        {
            public long ConnectionId;
            public int Priority;
            public float DistanceSqr;
            public bool Mandatory;
        }

        private enum RebuildStage : byte
        {
            None,
            Spatial,
            AlwaysVisible,
            ApplyRemovals,
            ApplyAdds,
        }

        private readonly List<TargetEntry> _targets = new List<TargetEntry>(1024);
        private readonly Dictionary<uint, int> _targetIndices = new Dictionary<uint, int>(1024);
        private readonly Dictionary<long, ObserverEntry> _observers = new Dictionary<long, ObserverEntry>(256);
        private readonly List<long> _observerConnections = new List<long>(256);
        private readonly Dictionary<long, int> _observerIndices = new Dictionary<long, int>(256);
        private readonly Dictionary<long, LiteNetLibIdentity> _explicitObserverAnchors = new Dictionary<long, LiteNetLibIdentity>(256);
        private readonly Queue<long> _pendingObserverRefreshes = new Queue<long>(256);
        private readonly HashSet<long> _pendingObserverRefreshSet = new HashSet<long>();
        private readonly Dictionary<SpatialCellKey, SpatialCell> _grid = new Dictionary<SpatialCellKey, SpatialCell>(1024);
        private readonly Stack<SpatialCell> _cellPool = new Stack<SpatialCell>(256);
        private readonly Dictionary<uint, PinBucket> _pins = new Dictionary<uint, PinBucket>();

        private readonly List<long> _existingSubscribers = new List<long>(512);
        private readonly HashSet<long> _desiredConnections = new HashSet<long>();
        private readonly List<long> _alwaysVisibleDesiredConnections = new List<long>(512);
        private SelectedObserver[] _selectedHeap = Array.Empty<SelectedObserver>();
        private readonly Dictionary<long, int> _selectedIndices = new Dictionary<long, int>(512);
        private Vector2Int[] _cellOffsets = Array.Empty<Vector2Int>();

        private ICoreScheduledSystemHandle _schedulerHandle;
        private int _targetCursor;
        private int _observerRefreshCursor;
        private int _completedTargetsSinceObserverRefresh;
        private bool _forceObserverRefresh = true;
        private double _nextWarningTime;

        // Active target rebuild state. Observer-grid mutation is paused while this is active,
        // so indexed cell lists are stable across cooperative work units.
        private TargetEntry _activeTarget;
        private RebuildStage _stage;
        private PartitionKey _activePartition;
        private Vector3 _activePosition;
        private SpatialCellKey _activeCenterCell;
        private float _activeRangeSqr;
        private int _activeCellRadius;
        private int _activeOffsetCursor;
        private SpatialCell _activeCell;
        private int _activeCellObserverCursor;
        private int _activeCandidateChecks;
        private int _activeSoftCap;
        private int _activeHeapCount;
        private int _activeAlwaysVisibleObserverCursor;
        private int _activeExistingSubscriberCursor;
        private int _activeSelectedAddCursor;

        public override void Setup(LiteNetLibGameManager manager)
        {
            base.Setup(manager);
            BuildCellOffsets();
            EnsureHeapCapacity();
            EnsureSchedulerRegistration();
        }

        private void OnEnable()
        {
            if (Manager != null)
                EnsureSchedulerRegistration();
        }

        private void OnDisable()
        {
            if (_schedulerHandle != null)
            {
                _schedulerHandle.Unregister();
                _schedulerHandle = null;
            }
        }

        private void OnDestroy()
        {
            if (_schedulerHandle != null)
            {
                _schedulerHandle.Unregister();
                _schedulerHandle = null;
            }
        }

        private void EnsureSchedulerRegistration()
        {
            if (Manager == null || _schedulerHandle != null && _schedulerHandle.IsRegistered)
                return;

            _schedulerHandle = Manager.CoreScheduler.RegisterWorkSystem(
                this,
                schedulerChannel,
                Mathf.Max(1, maxWorkUnitsPerSchedulerTick),
                true,
                Math.Max(2.0, workUnitInvocationLimitMilliseconds));
        }

        public override void UpdateInterestManagementImmediate()
        {
            _targetCursor = 0;
            _forceObserverRefresh = true;
        }

        /// <summary>
        /// Intentionally lightweight. The actual AOI work is executed by the core
        /// scheduler after the authoritative network tick and under its global/channel budgets.
        /// </summary>
        public override void UpdateInterestManagement(float deltaTime)
        {
            if (_schedulerHandle == null || !_schedulerHandle.IsRegistered)
                EnsureSchedulerRegistration();
        }

        public override void ResetState()
        {
            AbortActiveTarget();
            _targets.Clear();
            _targetIndices.Clear();

            _observers.Clear();
            _observerConnections.Clear();
            _observerIndices.Clear();
            _pendingObserverRefreshes.Clear();
            _pendingObserverRefreshSet.Clear();
            _explicitObserverAnchors.Clear();

            foreach (SpatialCell cell in _grid.Values)
            {
                cell.Connections.Clear();
                if (_cellPool.Count < Mathf.Max(0, maxPooledCells))
                    _cellPool.Push(cell);
            }
            _grid.Clear();

            _pins.Clear();
            totalPinnedObservers = 0;
            _targetCursor = 0;
            _observerRefreshCursor = 0;
            _completedTargetsSinceObserverRefresh = 0;
            _forceObserverRefresh = true;
            UpdateRuntimeCounts();
        }

        public override void NotifyNewObject(LiteNetLibIdentity newObject)
        {
            if (!IsServer || newObject == null)
                return;

            RegisterTarget(newObject);
            if (newObject.ConnectionId >= 0)
            {
                if (newObject.GetComponent<InterestObserverAnchor>() != null)
                    TrySetObserverAnchor(newObject.ConnectionId, newObject);
                TryMarkObserverDirty(newObject.ConnectionId);
            }
        }

        public override void NotifyObjectDestroyed(LiteNetLibIdentity destroyedObject)
        {
            if (destroyedObject == null)
                return;

            long connectionId = destroyedObject.ConnectionId;
            if (connectionId >= 0 && _explicitObserverAnchors.TryGetValue(connectionId, out LiteNetLibIdentity explicitAnchor) && explicitAnchor == destroyedObject)
                _explicitObserverAnchors.Remove(connectionId);
            RemoveTarget(destroyedObject.ObjectId);
            RemovePinsForTarget(destroyedObject.ObjectId);
            if (connectionId >= 0)
                TryMarkObserverDirty(connectionId);
        }

        public override void NotifyObjectOwnerChanged(LiteNetLibIdentity identity, long oldConnectionId, long newConnectionId)
        {
            if (oldConnectionId >= 0)
            {
                if (_explicitObserverAnchors.TryGetValue(oldConnectionId, out LiteNetLibIdentity oldAnchor) && oldAnchor == identity)
                    _explicitObserverAnchors.Remove(oldConnectionId);
                TryMarkObserverDirty(oldConnectionId);
            }
            if (newConnectionId >= 0)
            {
                if (identity != null && identity.GetComponent<InterestObserverAnchor>() != null)
                    TrySetObserverAnchor(newConnectionId, identity);
                TryMarkObserverDirty(newConnectionId);
            }
        }

        public bool TrySetObserverAnchor(long connectionId, LiteNetLibIdentity identity)
        {
            if (connectionId < 0 || identity == null || identity.IsDestroyed || !identity.IsSpawned || identity.ConnectionId != connectionId)
                return false;
            _explicitObserverAnchors[connectionId] = identity;
            TryMarkObserverDirty(connectionId);
            return true;
        }

        public void ClearObserverAnchor(long connectionId)
        {
            if (_explicitObserverAnchors.Remove(connectionId))
                TryMarkObserverDirty(connectionId);
        }

        public override bool ShouldSubscribe(LiteNetLibIdentity subscriber, LiteNetLibIdentity target, bool checkRange = true)
        {
            if (subscriber == null || target == null || subscriber.ConnectionId < 0)
                return false;
            if (subscriber.ConnectionId == target.ConnectionId)
                return true;
            if (target.IsHideFrom(subscriber))
                return false;

            IMMOInterestPartitionProvider subscriberProvider = FindInterface<IMMOInterestPartitionProvider>(subscriber);
            IMMOInterestPartitionProvider targetProvider = FindInterface<IMMOInterestPartitionProvider>(target);
            if (!GetPartitionKey(subscriber, subscriberProvider).Equals(GetPartitionKey(target, targetProvider)))
                return false;

            if (!checkRange || target.AlwaysVisible)
                return true;

            float range = Mathf.Max(0f, GetVisibleRange(target));
            return IsWithinRange(target.transform.position, subscriber.transform.position, range * range);
        }

        public bool HasPendingWork
        {
            get
            {
                return Manager != null && Manager.IsServer && (_activeTarget != null || _targets.Count > 0 || _observers.Count > 0 || _pendingObserverRefreshes.Count > 0);
            }
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (Manager == null || !Manager.IsServer)
                return;

            // Once a target rebuild starts, finish it cooperatively before mutating
            // observer cell lists so indexed cell traversal remains stable.
            if (_activeTarget != null)
            {
                ProcessActiveTargetWorkUnit();
                return;
            }

            if (_pendingObserverRefreshes.Count > 0)
            {
                ProcessPendingObserverRefreshBatch();
                return;
            }

            if (_observers.Count > 0 && (_forceObserverRefresh || _completedTargetsSinceObserverRefresh >= Mathf.Max(1, targetRebuildsPerObserverRefresh)))
            {
                RefreshObserverBatch();
                _forceObserverRefresh = false;
                _completedTargetsSinceObserverRefresh = 0;
                return;
            }

            if (_targets.Count > 0)
            {
                BeginNextTarget();
                if (_activeTarget != null)
                    ProcessActiveTargetWorkUnit();
                return;
            }

            if (_observers.Count > 0)
                RefreshObserverBatch();
        }

        /// <summary>
        /// Marks a connection AOI anchor dirty. Authoritative movement/teleport systems
        /// should call this when a player crosses a meaningful spatial boundary. The
        /// actual grid mutation is deferred to a scheduler-governed work unit. Periodic
        /// maintenance remains as a safety net if callers do not mark movement.
        /// </summary>
        public bool TryMarkObserverDirty(long connectionId)
        {
            if (connectionId < 0 || _pendingObserverRefreshSet.Contains(connectionId))
                return connectionId >= 0;
            if (_pendingObserverRefreshes.Count >= Mathf.Max(1, maxPendingObserverRefreshes))
            {
                observerRefreshQueueRejections++;
                return false;
            }
            _pendingObserverRefreshSet.Add(connectionId);
            _pendingObserverRefreshes.Enqueue(connectionId);
            return true;
        }

        private void ProcessPendingObserverRefreshBatch()
        {
            int budget = Mathf.Max(1, observerRefreshPerWorkUnit);
            int processed = 0;
            while (processed++ < budget && _pendingObserverRefreshes.Count > 0)
            {
                long connectionId = _pendingObserverRefreshes.Dequeue();
                _pendingObserverRefreshSet.Remove(connectionId);
                RefreshObserverAnchor(connectionId);
            }
        }

        /// <summary>
        /// Explicitly pin a connection to a target. Pins bypass distance but, by default,
        /// still respect logical partition and hide/sub-channel rules. Pins are mandatory
        /// when selecting inside the soft cap and are themselves admission-bounded.
        /// </summary>
        public bool TryPinObserver(uint targetObjectId, long connectionId, int priority = 1000)
        {
            if (connectionId < 0 || targetObjectId == 0)
                return false;

            if (!_pins.TryGetValue(targetObjectId, out PinBucket bucket))
            {
                if (totalPinnedObservers >= Mathf.Max(1, maxTotalPinnedObservers))
                    return false;
                bucket = new PinBucket();
                _pins.Add(targetObjectId, bucket);
            }

            if (bucket.Priorities.ContainsKey(connectionId))
            {
                bucket.Priorities[connectionId] = priority;
                return true;
            }

            if (bucket.Priorities.Count >= Mathf.Max(1, maxPinnedObserversPerEntity) ||
                totalPinnedObservers >= Mathf.Max(1, maxTotalPinnedObservers))
                return false;

            bucket.Priorities.Add(connectionId, priority);
            totalPinnedObservers++;
            return true;
        }

        public bool UnpinObserver(uint targetObjectId, long connectionId)
        {
            if (!_pins.TryGetValue(targetObjectId, out PinBucket bucket) || !bucket.Priorities.Remove(connectionId))
                return false;

            totalPinnedObservers = Mathf.Max(0, totalPinnedObservers - 1);
            if (bucket.Priorities.Count == 0)
                _pins.Remove(targetObjectId);
            return true;
        }

        public void ClearPinnedObservers(uint targetObjectId)
        {
            RemovePinsForTarget(targetObjectId);
        }

        private void RegisterTarget(LiteNetLibIdentity identity)
        {
            if (identity == null || identity.ObjectId == 0 || _targetIndices.ContainsKey(identity.ObjectId))
                return;

            TargetEntry entry = new TargetEntry
            {
                Identity = identity,
                PartitionProvider = FindInterface<IMMOInterestPartitionProvider>(identity),
                CapPolicy = FindInterface<IMMOObserverCapPolicy>(identity),
            };
            _targetIndices.Add(identity.ObjectId, _targets.Count);
            _targets.Add(entry);
            UpdateRuntimeCounts();
        }

        private void RemoveTarget(uint objectId)
        {
            if (!_targetIndices.TryGetValue(objectId, out int index))
                return;

            if (_activeTarget != null && _activeTarget.Identity != null && _activeTarget.Identity.ObjectId == objectId)
                AbortActiveTarget();

            int last = _targets.Count - 1;
            TargetEntry moved = _targets[last];
            _targets[index] = moved;
            _targets.RemoveAt(last);
            _targetIndices.Remove(objectId);
            if (index < _targets.Count && moved.Identity != null)
                _targetIndices[moved.Identity.ObjectId] = index;

            if (_targetCursor > index)
                _targetCursor--;
            if (_targetCursor >= _targets.Count)
                _targetCursor = 0;
            UpdateRuntimeCounts();
        }

        private void RemovePinsForTarget(uint objectId)
        {
            if (_pins.TryGetValue(objectId, out PinBucket bucket))
            {
                totalPinnedObservers = Mathf.Max(0, totalPinnedObservers - bucket.Priorities.Count);
                _pins.Remove(objectId);
            }
        }

        private void RefreshObserverAnchor(long connectionId)
        {
            if (connectionId < 0)
                return;

            if (!Manager.TryGetPlayer(connectionId, out LiteNetLibPlayer player) || !player.IsReady)
            {
                RemoveObserver(connectionId);
                return;
            }

            LiteNetLibIdentity anchor = FindBestObserverAnchor(player);
            if (anchor == null)
            {
                RemoveObserver(connectionId);
                return;
            }

            if (!_observers.TryGetValue(connectionId, out ObserverEntry entry))
            {
                entry = new ObserverEntry { ConnectionId = connectionId };
                _observers.Add(connectionId, entry);
                _observerIndices.Add(connectionId, _observerConnections.Count);
                _observerConnections.Add(connectionId);
            }

            if (entry.Anchor != anchor)
            {
                if (entry.InGrid)
                    RemoveObserverFromCell(entry);
                entry.Anchor = anchor;
                entry.PartitionProvider = FindInterface<IMMOInterestPartitionProvider>(anchor);
            }

            UpdateObserverCell(entry, true);
            _forceObserverRefresh = true;
            UpdateRuntimeCounts();
        }

        private LiteNetLibIdentity FindBestObserverAnchor(LiteNetLibPlayer player)
        {
            if (_explicitObserverAnchors.TryGetValue(player.ConnectionId, out LiteNetLibIdentity explicitAnchor))
            {
                if (explicitAnchor != null && !explicitAnchor.IsDestroyed && explicitAnchor.IsSpawned && explicitAnchor.ConnectionId == player.ConnectionId)
                    return explicitAnchor;
                _explicitObserverAnchors.Remove(player.ConnectionId);
            }

            LiteNetLibIdentity preferred = null;
            LiteNetLibIdentity fallback = null;
            uint fallbackId = uint.MaxValue;

            int scanned = 0;
            int scanLimit = Mathf.Max(1, maxOwnedObjectsScannedForAnchor);
            foreach (KeyValuePair<uint, LiteNetLibIdentity> kvp in player.SpawnedObjects)
            {
                if (scanned++ >= scanLimit)
                    break;
                LiteNetLibIdentity identity = kvp.Value;
                if (identity == null || identity.IsDestroyed || !identity.IsSpawned)
                    continue;

                if (identity.GetComponent<InterestObserverAnchor>() != null)
                {
                    if (preferred == null || identity.ObjectId < preferred.ObjectId)
                        preferred = identity;
                    continue;
                }

                if (identity.ObjectId < fallbackId)
                {
                    fallbackId = identity.ObjectId;
                    fallback = identity;
                }
            }

            return preferred != null ? preferred : fallback;
        }

        private void RemoveObserver(long connectionId)
        {
            if (!_observers.TryGetValue(connectionId, out ObserverEntry entry))
                return;

            if (entry.InGrid)
                RemoveObserverFromCell(entry);
            _observers.Remove(connectionId);
            if (_observerIndices.TryGetValue(connectionId, out int index))
            {
                int last = _observerConnections.Count - 1;
                long moved = _observerConnections[last];
                _observerConnections[index] = moved;
                _observerConnections.RemoveAt(last);
                _observerIndices.Remove(connectionId);
                if (index < _observerConnections.Count)
                    _observerIndices[moved] = index;
                if (_observerRefreshCursor > index)
                    _observerRefreshCursor--;
                if (_observerRefreshCursor >= _observerConnections.Count)
                    _observerRefreshCursor = 0;
            }
            UpdateRuntimeCounts();
        }

        private void RefreshObserverBatch()
        {
            int budget = Mathf.Max(1, observerRefreshPerWorkUnit);
            int processed = 0;
            while (processed < budget && _observerConnections.Count > 0)
            {
                if (_observerRefreshCursor >= _observerConnections.Count)
                    _observerRefreshCursor = 0;

                long connectionId = _observerConnections[_observerRefreshCursor];
                _observerRefreshCursor++;
                processed++;

                if (!_observers.TryGetValue(connectionId, out ObserverEntry entry))
                    continue;

                if (!Manager.TryGetPlayer(connectionId, out LiteNetLibPlayer player) || !player.IsReady ||
                    entry.Anchor == null || entry.Anchor.IsDestroyed || !entry.Anchor.IsSpawned || entry.Anchor.ConnectionId != connectionId)
                {
                    RefreshObserverAnchor(connectionId);
                    continue;
                }

                UpdateObserverCell(entry, false);
            }
            UpdateRuntimeCounts();
        }

        private void UpdateObserverCell(ObserverEntry entry, bool force)
        {
            if (entry == null || entry.Anchor == null)
                return;

            SpatialCellKey newKey = GetSpatialCellKey(entry.Anchor, entry.PartitionProvider);
            if (!force && entry.InGrid && entry.CellKey.Equals(newKey))
                return;

            if (entry.InGrid)
                RemoveObserverFromCell(entry);

            SpatialCell cell = GetOrCreateCell(newKey);
            entry.CellKey = newKey;
            entry.IndexInCell = cell.Connections.Count;
            entry.InGrid = true;
            cell.Connections.Add(entry.ConnectionId);
            observerCellMoves++;
        }

        private SpatialCell GetOrCreateCell(SpatialCellKey key)
        {
            if (_grid.TryGetValue(key, out SpatialCell cell))
                return cell;

            cell = _cellPool.Count > 0 ? _cellPool.Pop() : new SpatialCell();
            cell.Connections.Clear();
            _grid.Add(key, cell);
            return cell;
        }

        private void RemoveObserverFromCell(ObserverEntry entry)
        {
            if (!entry.InGrid)
                return;

            if (_grid.TryGetValue(entry.CellKey, out SpatialCell cell))
            {
                int index = entry.IndexInCell;
                int last = cell.Connections.Count - 1;
                if (index >= 0 && index <= last)
                {
                    long movedConnection = cell.Connections[last];
                    cell.Connections[index] = movedConnection;
                    cell.Connections.RemoveAt(last);
                    if (index < cell.Connections.Count && _observers.TryGetValue(movedConnection, out ObserverEntry movedEntry))
                        movedEntry.IndexInCell = index;
                }

                if (cell.Connections.Count == 0)
                {
                    _grid.Remove(entry.CellKey);
                    if (_cellPool.Count < Mathf.Max(0, maxPooledCells))
                        _cellPool.Push(cell);
                }
            }

            entry.InGrid = false;
            entry.IndexInCell = -1;
        }

        private void BeginNextTarget()
        {
            while (_targets.Count > 0)
            {
                if (_targetCursor >= _targets.Count)
                    _targetCursor = 0;

                TargetEntry entry = _targets[_targetCursor++];
                if (entry == null || entry.Identity == null || entry.Identity.IsDestroyed || !entry.Identity.IsSpawned)
                    continue;

                _activeTarget = entry;
                _activePosition = entry.Identity.transform.position;
                _activePartition = GetPartitionKey(entry.Identity, entry.PartitionProvider);
                _activeCenterCell = GetSpatialCellKey(_activePartition, _activePosition);
                float range = Mathf.Max(0f, GetVisibleRange(entry.Identity));
                _activeRangeSqr = range * range;
                int requestedRadius = entry.Identity.AlwaysVisible ? 0 : Mathf.CeilToInt(range / Mathf.Max(1f, cellSize));
                _activeCellRadius = Mathf.Clamp(requestedRadius, 0, Mathf.Max(1, maxCellRadius));
                if (!entry.Identity.AlwaysVisible && requestedRadius > _activeCellRadius)
                    cellRadiusClampHits++;

                _activeSoftCap = ResolveSoftCap(entry);
                EnsureHeapCapacity();
                _selectedIndices.Clear();
                _activeHeapCount = 0;
                _activeCandidateChecks = 0;
                _activeOffsetCursor = 0;
                _activeCell = null;
                _activeCellObserverCursor = 0;
                _activeAlwaysVisibleObserverCursor = 0;
                _existingSubscribers.Clear();
                _desiredConnections.Clear();
                _alwaysVisibleDesiredConnections.Clear();
                _activeExistingSubscriberCursor = 0;
                _activeSelectedAddCursor = 0;

                // AlwaysVisible has literal semantics: every eligible observer is
                // scanned cooperatively and admitted without spatial fan-out caps.
                // Owner/pin priority selection is only meaningful for spatial targets.
                if (!entry.Identity.AlwaysVisible)
                    AddOwnerAndPins(entry);

                _stage = entry.Identity.AlwaysVisible
                    ? RebuildStage.AlwaysVisible
                    : RebuildStage.Spatial;
                return;
            }
        }

        private void AddOwnerAndPins(TargetEntry entry)
        {
            LiteNetLibIdentity target = entry.Identity;
            if (target.ConnectionId >= 0 && _observers.TryGetValue(target.ConnectionId, out ObserverEntry owner) && owner.Anchor != null)
                ConsiderObserver(owner, int.MaxValue, true, true, true);

            if (!_pins.TryGetValue(target.ObjectId, out PinBucket bucket))
                return;

            int processed = 0;
            int hardPinLimit = Mathf.Max(1, maxPinnedObserversPerEntity);
            foreach (KeyValuePair<long, int> kvp in bucket.Priorities)
            {
                if (processed++ >= hardPinLimit)
                    break;
                if (!_observers.TryGetValue(kvp.Key, out ObserverEntry observer) || observer.Anchor == null)
                    continue;
                if (pinsRequireSamePartition && !GetPartitionKey(observer.Anchor, observer.PartitionProvider).Equals(_activePartition))
                    continue;
                ConsiderObserver(observer, kvp.Value, true, true, !pinsRequireSamePartition);
            }
        }

        private void ProcessActiveTargetWorkUnit()
        {
            if (_activeTarget == null || _activeTarget.Identity == null || _activeTarget.Identity.IsDestroyed || !_activeTarget.Identity.IsSpawned)
            {
                AbortActiveTarget();
                return;
            }

            switch (_stage)
            {
                case RebuildStage.Spatial:
                    ProcessSpatialCandidates();
                    break;
                case RebuildStage.AlwaysVisible:
                    ProcessAlwaysVisibleCandidates();
                    break;
                case RebuildStage.ApplyRemovals:
                    ApplyRemovalBatch();
                    break;
                case RebuildStage.ApplyAdds:
                    ApplyAdditionBatch();
                    break;
                default:
                    AbortActiveTarget();
                    break;
            }
        }

        private void ProcessSpatialCandidates()
        {
            int unitBudget = Mathf.Max(1, candidateChecksPerWorkUnit);
            int checkedThisUnit = 0;
            int hardCandidateLimit = Mathf.Max(1, maxCandidateChecksPerTarget);

            while (checkedThisUnit < unitBudget && _activeCandidateChecks < hardCandidateLimit)
            {
                if (_activeCell != null && _activeCellObserverCursor < _activeCell.Connections.Count)
                {
                    long connectionId = _activeCell.Connections[_activeCellObserverCursor++];
                    checkedThisUnit++;
                    _activeCandidateChecks++;
                    candidateChecks++;
                    if (_observers.TryGetValue(connectionId, out ObserverEntry observer))
                        ConsiderObserver(observer, 0, false, false, false);
                    continue;
                }

                _activeCell = null;
                _activeCellObserverCursor = 0;

                bool foundCell = false;
                while (_activeOffsetCursor < _cellOffsets.Length)
                {
                    Vector2Int offset = _cellOffsets[_activeOffsetCursor++];
                    if (Mathf.Abs(offset.x) > _activeCellRadius || Mathf.Abs(offset.y) > _activeCellRadius)
                        continue;

                    SpatialCellKey key = new SpatialCellKey(
                        _activePartition,
                        _activeCenterCell.X + offset.x,
                        _activeCenterCell.Y + offset.y);
                    if (_grid.TryGetValue(key, out SpatialCell cell) && cell.Connections.Count > 0)
                    {
                        _activeCell = cell;
                        foundCell = true;
                        break;
                    }
                }

                if (!foundCell)
                {
                    BeginApplyStage();
                    return;
                }
            }

            if (_activeCandidateChecks >= hardCandidateLimit)
            {
                candidateLimitHits++;
                WarnHardLimit("candidate observer checks", hardCandidateLimit);
                BeginApplyStage();
            }
        }

        private void ProcessAlwaysVisibleCandidates()
        {
            int unitBudget = Mathf.Max(1, candidateChecksPerWorkUnit);
            int checkedThisUnit = 0;

            // IMPORTANT CONTRACT:
            // AlwaysVisible does not stop at maxCandidateChecksPerTarget and does
            // not use maxObserversPerEntity/hardMaxObserversPerEntity. The work is
            // still cooperatively amortized by candidateChecksPerWorkUnit, so large
            // populations cannot create one monolithic scheduler invocation.
            while (checkedThisUnit < unitBudget &&
                   _activeAlwaysVisibleObserverCursor < _observerConnections.Count)
            {
                long connectionId =
                    _observerConnections[_activeAlwaysVisibleObserverCursor++];

                checkedThisUnit++;
                _activeCandidateChecks++;
                candidateChecks++;

                if (_observers.TryGetValue(
                        connectionId,
                        out ObserverEntry observer))
                {
                    AddAlwaysVisibleObserver(observer);
                }
            }

            if (_activeAlwaysVisibleObserverCursor >= _observerConnections.Count)
                BeginApplyStage();
        }

        private void AddAlwaysVisibleObserver(ObserverEntry observer)
        {
            if (observer == null ||
                observer.Anchor == null ||
                observer.Anchor.IsDestroyed ||
                !observer.Anchor.IsSpawned)
            {
                return;
            }

            if (!Manager.TryGetPlayer(
                    observer.ConnectionId,
                    out LiteNetLibPlayer player) ||
                !player.IsReady)
            {
                return;
            }

            LiteNetLibIdentity target = _activeTarget.Identity;

            if (observer.ConnectionId != target.ConnectionId)
            {
                if (!GetPartitionKey(
                        observer.Anchor,
                        observer.PartitionProvider).Equals(_activePartition))
                {
                    return;
                }

                if (target.IsHideFrom(observer.Anchor))
                    return;
            }

            if (_desiredConnections.Add(observer.ConnectionId))
                _alwaysVisibleDesiredConnections.Add(observer.ConnectionId);
        }

        internal static bool CandidateHardLimitApplies(bool alwaysVisible)
        {
            return !alwaysVisible;
        }

        private void ConsiderObserver(ObserverEntry observer, int priority, bool mandatory, bool bypassDistance, bool bypassPartition)
        {
            if (observer == null || observer.Anchor == null || observer.Anchor.IsDestroyed || !observer.Anchor.IsSpawned)
                return;
            if (!Manager.TryGetPlayer(observer.ConnectionId, out LiteNetLibPlayer player) || !player.IsReady)
                return;

            LiteNetLibIdentity target = _activeTarget.Identity;
            if (observer.ConnectionId != target.ConnectionId)
            {
                if (!bypassPartition && !GetPartitionKey(observer.Anchor, observer.PartitionProvider).Equals(_activePartition))
                    return;
                if (target.IsHideFrom(observer.Anchor))
                    return;
            }

            float distanceSqr = DistanceSqr(_activePosition, observer.Anchor.transform.position);
            if (!bypassDistance && useDistanceCheck && !target.AlwaysVisible && distanceSqr > _activeRangeSqr)
                return;

            AddSelectedObserver(new SelectedObserver
            {
                ConnectionId = observer.ConnectionId,
                Priority = priority,
                DistanceSqr = distanceSqr,
                Mandatory = mandatory || observer.ConnectionId == target.ConnectionId,
            });
        }

        private void AddSelectedObserver(SelectedObserver candidate)
        {
            int hardCap = Mathf.Max(1, hardMaxObserversPerEntity);
            int softCap = Mathf.Clamp(_activeSoftCap, 1, hardCap);

            if (_selectedIndices.TryGetValue(candidate.ConnectionId, out int existingIndex))
            {
                SelectedObserver existing = _selectedHeap[existingIndex];
                bool improved = false;
                if (candidate.Mandatory && !existing.Mandatory)
                {
                    existing.Mandatory = true;
                    improved = true;
                }
                if (candidate.Priority > existing.Priority)
                {
                    existing.Priority = candidate.Priority;
                    improved = true;
                }
                if (candidate.DistanceSqr < existing.DistanceSqr)
                {
                    existing.DistanceSqr = candidate.DistanceSqr;
                    improved = true;
                }
                if (improved)
                {
                    _selectedHeap[existingIndex] = existing;
                    HeapifyDown(existingIndex);
                }
                return;
            }

            int allowed = softCap;
            if (candidate.Mandatory && _activeHeapCount >= allowed)
            {
                if (_activeHeapCount < hardCap && AllSelectedMandatory())
                    allowed = _activeHeapCount + 1;
            }

            if (_activeHeapCount < allowed)
            {
                int index = _activeHeapCount++;
                _selectedHeap[index] = candidate;
                _selectedIndices[candidate.ConnectionId] = index;
                HeapifyUp(index);
                if (_activeHeapCount == softCap)
                    softCapHits++;
                return;
            }

            if (_activeHeapCount <= 0)
                return;

            SelectedObserver worst = _selectedHeap[0];
            if (IsBetter(candidate, worst))
            {
                _selectedIndices.Remove(worst.ConnectionId);
                _selectedHeap[0] = candidate;
                _selectedIndices[candidate.ConnectionId] = 0;
                HeapifyDown(0);
                return;
            }

            if (candidate.Mandatory && _activeHeapCount >= hardCap)
            {
                hardCapHits++;
                WarnHardLimit("observer hard cap", hardCap);
            }
        }

        private bool AllSelectedMandatory()
        {
            for (int i = 0; i < _activeHeapCount; ++i)
            {
                if (!_selectedHeap[i].Mandatory)
                    return false;
            }
            return true;
        }

        private static bool IsBetter(SelectedObserver a, SelectedObserver b)
        {
            if (a.Mandatory != b.Mandatory)
                return a.Mandatory;
            if (a.Priority != b.Priority)
                return a.Priority > b.Priority;
            if (!Mathf.Approximately(a.DistanceSqr, b.DistanceSqr))
                return a.DistanceSqr < b.DistanceSqr;
            return a.ConnectionId < b.ConnectionId;
        }

        private static bool IsWorse(SelectedObserver a, SelectedObserver b)
        {
            return IsBetter(b, a);
        }

        private void HeapifyUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (!IsWorse(_selectedHeap[index], _selectedHeap[parent]))
                    break;
                SwapHeap(index, parent);
                index = parent;
            }
        }

        private void HeapifyDown(int index)
        {
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= _activeHeapCount)
                    return;
                int right = left + 1;
                int worst = left;
                if (right < _activeHeapCount && IsWorse(_selectedHeap[right], _selectedHeap[left]))
                    worst = right;
                if (!IsWorse(_selectedHeap[worst], _selectedHeap[index]))
                    return;
                SwapHeap(index, worst);
                index = worst;
            }
        }

        private void SwapHeap(int a, int b)
        {
            SelectedObserver temp = _selectedHeap[a];
            _selectedHeap[a] = _selectedHeap[b];
            _selectedHeap[b] = temp;
            _selectedIndices[_selectedHeap[a].ConnectionId] = a;
            _selectedIndices[_selectedHeap[b].ConnectionId] = b;
        }

        private void BeginApplyStage()
        {
            LiteNetLibIdentity target = _activeTarget.Identity;

            // AlwaysVisible candidates were added directly while scanning because
            // they intentionally bypass selection/fan-out caps. Spatial targets
            // continue to use the bounded priority heap.
            if (!target.AlwaysVisible)
            {
                _desiredConnections.Clear();
                for (int i = 0; i < _activeHeapCount; ++i)
                    _desiredConnections.Add(_selectedHeap[i].ConnectionId);
            }

            if (target.ConnectionId >= 0)
                _desiredConnections.Add(target.ConnectionId);

            _existingSubscribers.Clear();
            int snapshotLimit = Mathf.Max(1, maxExistingSubscribersSnapshot);
            int sampled = 0;
            foreach (long subscriber in target.Subscribers)
            {
                if (sampled++ >= snapshotLimit)
                    break;
                _existingSubscribers.Add(subscriber);
            }

            _activeExistingSubscriberCursor = 0;
            _activeSelectedAddCursor = 0;
            _stage = RebuildStage.ApplyRemovals;
        }

        private void ApplyRemovalBatch()
        {
            LiteNetLibIdentity target = _activeTarget.Identity;
            int budget = Mathf.Max(1, subscriptionChangesPerWorkUnit);
            int changes = 0;

            while (_activeExistingSubscriberCursor < _existingSubscribers.Count && changes < budget)
            {
                long connectionId = _existingSubscribers[_activeExistingSubscriberCursor++];
                if (_desiredConnections.Contains(connectionId) || connectionId == target.ConnectionId)
                    continue;

                if (Manager.TryGetPlayer(connectionId, out LiteNetLibPlayer player))
                {
                    player.Unsubscribe(target.ObjectId);
                    subscriptionRemoves++;
                    changes++;
                }
            }

            if (_activeExistingSubscriberCursor >= _existingSubscribers.Count)
                _stage = RebuildStage.ApplyAdds;
        }

        private void ApplyAdditionBatch()
        {
            LiteNetLibIdentity target = _activeTarget.Identity;
            int budget = Mathf.Max(1, subscriptionChangesPerWorkUnit);
            int changes = 0;
            int desiredCount = target.AlwaysVisible
                ? _alwaysVisibleDesiredConnections.Count
                : _activeHeapCount;

            while (_activeSelectedAddCursor < desiredCount && changes < budget)
            {
                long connectionId = target.AlwaysVisible
                    ? _alwaysVisibleDesiredConnections[_activeSelectedAddCursor++]
                    : _selectedHeap[_activeSelectedAddCursor++].ConnectionId;

                if (target.HasSubscriber(connectionId))
                    continue;

                if (Manager.TryGetPlayer(
                        connectionId,
                        out LiteNetLibPlayer player) &&
                    player.IsReady)
                {
                    player.Subscribe(target.ObjectId);
                    subscriptionAdds++;
                    changes++;
                }
            }

            // Owner may have been preserved outside the normal observer list.
            if (_activeSelectedAddCursor >= desiredCount)
            {
                if (target.ConnectionId >= 0 && !target.HasSubscriber(target.ConnectionId) &&
                    Manager.TryGetPlayer(target.ConnectionId, out LiteNetLibPlayer owner) && owner.IsReady)
                {
                    owner.Subscribe(target.ObjectId);
                    subscriptionAdds++;
                }

                int count = target.CountSubscribers();
                if (count > maximumObservedSubscribers)
                    maximumObservedSubscribers = count;
                processedTargetRebuilds++;
                _completedTargetsSinceObserverRefresh++;
                AbortActiveTarget(false);
            }
        }

        private void AbortActiveTarget(bool resetBuffers = true)
        {
            _activeTarget = null;
            _stage = RebuildStage.None;
            _activeCell = null;
            _activeCellObserverCursor = 0;
            _activeOffsetCursor = 0;
            _activeAlwaysVisibleObserverCursor = 0;
            _activeCandidateChecks = 0;
            _activeExistingSubscriberCursor = 0;
            _activeSelectedAddCursor = 0;
            if (resetBuffers)
            {
                _selectedIndices.Clear();
                _desiredConnections.Clear();
                _alwaysVisibleDesiredConnections.Clear();
                _existingSubscribers.Clear();
                _activeHeapCount = 0;
            }
        }

        private int ResolveSoftCap(TargetEntry entry)
        {
            int cap = Mathf.Max(1, maxObserversPerEntity);
            if (entry.CapPolicy != null && entry.CapPolicy.ObserverCapOverride > 0)
                cap = entry.CapPolicy.ObserverCapOverride;
            return Mathf.Clamp(cap, 1, Mathf.Max(1, hardMaxObserversPerEntity));
        }

        private PartitionKey GetPartitionKey(LiteNetLibIdentity identity, IMMOInterestPartitionProvider provider)
        {
            int mapId = provider != null ? provider.InterestMapId : 0;
            int instanceId = provider != null ? provider.InterestInstanceId : 0;
            int sceneHandle = requireSameUnityScene && identity != null ? identity.gameObject.scene.handle : 0;
            string subChannelId = identity == null ? string.Empty : identity.SubChannelId;
            return new PartitionKey(mapId, instanceId, sceneHandle, subChannelId);
        }

        private SpatialCellKey GetSpatialCellKey(LiteNetLibIdentity identity, IMMOInterestPartitionProvider provider)
        {
            return GetSpatialCellKey(GetPartitionKey(identity, provider), identity.transform.position);
        }

        private SpatialCellKey GetSpatialCellKey(PartitionKey partition, Vector3 position)
        {
            float size = Mathf.Max(1f, cellSize);
            int x;
            int y;
            if (projectionMode == ProjectionMode.XZ_3D)
            {
                x = Mathf.FloorToInt(position.x / size);
                y = Mathf.FloorToInt(position.z / size);
            }
            else
            {
                x = Mathf.FloorToInt(position.x / size);
                y = Mathf.FloorToInt(position.y / size);
            }
            return new SpatialCellKey(partition, x, y);
        }

        private float DistanceSqr(Vector3 a, Vector3 b)
        {
            if (projectionMode == ProjectionMode.XZ_3D)
            {
                float dx = a.x - b.x;
                float dz = a.z - b.z;
                return dx * dx + dz * dz;
            }
            else
            {
                float dx = a.x - b.x;
                float dy = a.y - b.y;
                return dx * dx + dy * dy;
            }
        }

        private bool IsWithinRange(Vector3 a, Vector3 b, float rangeSqr)
        {
            return !useDistanceCheck || DistanceSqr(a, b) <= rangeSqr;
        }

        private void BuildCellOffsets()
        {
            int radius = Mathf.Max(1, maxCellRadius);
            int side = radius * 2 + 1;
            List<Vector2Int> offsets = new List<Vector2Int>(side * side);
            for (int x = -radius; x <= radius; ++x)
            {
                for (int y = -radius; y <= radius; ++y)
                    offsets.Add(new Vector2Int(x, y));
            }
            offsets.Sort((a, b) =>
            {
                int da = a.x * a.x + a.y * a.y;
                int db = b.x * b.x + b.y * b.y;
                if (da != db)
                    return da.CompareTo(db);
                int ax = Mathf.Abs(a.x) + Mathf.Abs(a.y);
                int bx = Mathf.Abs(b.x) + Mathf.Abs(b.y);
                return ax.CompareTo(bx);
            });
            _cellOffsets = offsets.ToArray();
        }

        private void EnsureHeapCapacity()
        {
            int capacity = Mathf.Max(1, hardMaxObserversPerEntity);
            if (_selectedHeap.Length < capacity)
                _selectedHeap = new SelectedObserver[capacity];
        }

        private static T FindInterface<T>(LiteNetLibIdentity identity) where T : class
        {
            if (identity == null)
                return null;
            // Component.GetComponents<T>() allocates an array. The non-generic Type
            // lookup supports interface types without creating a component array.
            Component component = identity.GetComponent(typeof(T));
            return component as T;
        }

        private void UpdateRuntimeCounts()
        {
            runtimeTargetCount = _targets.Count;
            runtimeObserverCount = _observers.Count;
            runtimeGridCellCount = _grid.Count;
        }

        private void WarnHardLimit(string limitName, int limit)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!warnOnHardLimit)
                return;
            double now = Time.realtimeSinceStartup;
            if (now < _nextWarningTime)
                return;
            _nextWarningTime = now + Math.Max(0.1, warningCooldownSeconds);
            Debug.LogWarning($"[MMOSpatialInterestManager] Bounded AOI limit reached: {limitName}={limit}. Work was truncated/deferred to protect server frame time.", this);
#endif
        }
    }
}
