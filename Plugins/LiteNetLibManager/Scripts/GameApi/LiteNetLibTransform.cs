using LiteNetLib.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace LiteNetLibManager
{
    public class LiteNetLibTransform : LiteNetLibBehaviour
    {
        private static readonly NetDataWriter s_ExtraWriter = new NetDataWriter();
        private static readonly NetDataReader s_ExtraReader = new NetDataReader();
        public delegate void WriteSyncBufferDelegate(NetDataWriter writer, uint tick);
        public delegate void ReadInterpBufferDelegate(NetDataReader reader, uint tick);
        public delegate bool ValidateInterpolationDelegate(TransformData interpFromData, TransformData interpToData, TransformData currentData, float interpTime);
        public delegate void InterpolateDelegate(TransformData interpFromData, TransformData interpToData, float interpTime);

        [System.Flags]
        public enum SyncTransformState : uint
        {
            None = 0,
            PositionX = 1 << 0,
            PositionY = 1 << 1,
            PositionZ = 1 << 2,
            EulerAnglesX = 1 << 3,
            EulerAnglesY = 1 << 4,
            EulerAnglesZ = 1 << 5,
            ScaleX = 1 << 6,
            ScaleY = 1 << 7,
            ScaleZ = 1 << 8,
        }

        [System.Serializable]
        public struct TransformData : INetSerializable
        {
            public uint Tick;
            public SyncTransformState SyncData;
            public Vector3 Position;
            public Vector3 EulerAngles;
            public Vector3 Scale;
            public byte[] Extra;

            public void Deserialize(NetDataReader reader)
            {
                Tick = reader.GetPackedUInt();
                SyncData = (SyncTransformState)reader.GetPackedUInt();

                Position = new Vector3(
                    !((SyncData & SyncTransformState.PositionX) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.PositionY) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.PositionZ) != 0) ? 0f : reader.GetFloat());

                EulerAngles = new Vector3(
                    !((SyncData & SyncTransformState.EulerAnglesX) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.EulerAnglesY) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.EulerAnglesZ) != 0) ? 0f : reader.GetFloat());

                Scale = new Vector3(
                    !((SyncData & SyncTransformState.ScaleX) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.ScaleY) != 0) ? 0f : reader.GetFloat(),
                    !((SyncData & SyncTransformState.ScaleZ) != 0) ? 0f : reader.GetFloat());

                Extra = null;
                byte extraLength = reader.GetByte();
                if (extraLength > 0)
                {
                    Extra = new byte[extraLength];
                    for (byte i = 0; i < extraLength; ++i)
                    {
                        Extra[i] = reader.GetByte();
                    }
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                writer.PutPackedUInt(Tick);
                writer.PutPackedUInt((uint)SyncData);

                if (((SyncData & SyncTransformState.PositionX) != 0))
                    writer.Put(Position.x);
                if (((SyncData & SyncTransformState.PositionY) != 0))
                    writer.Put(Position.y);
                if (((SyncData & SyncTransformState.PositionZ) != 0))
                    writer.Put(Position.z);

                if (((SyncData & SyncTransformState.EulerAnglesX) != 0))
                    writer.Put(EulerAngles.x);
                if (((SyncData & SyncTransformState.EulerAnglesY) != 0))
                    writer.Put(EulerAngles.y);
                if (((SyncData & SyncTransformState.EulerAnglesZ) != 0))
                    writer.Put(EulerAngles.z);

                if (((SyncData & SyncTransformState.ScaleX) != 0))
                    writer.Put(Scale.x);
                if (((SyncData & SyncTransformState.ScaleY) != 0))
                    writer.Put(Scale.y);
                if (((SyncData & SyncTransformState.ScaleZ) != 0))
                    writer.Put(Scale.z);

                byte extraLength = 0;
                if (Extra != null && Extra.Length > 0)
                {
                    extraLength = (byte)Extra.Length;
                    writer.Put(extraLength);
                    for (byte i = 0; i < extraLength; ++i)
                    {
                        writer.Put(Extra[i]);
                    }
                }
                else
                {
                    writer.Put(extraLength);
                }
            }

            public Vector3 GetPosition(Vector3 defaultPosition)
            {
                return new Vector3(
                    !((SyncData & SyncTransformState.PositionX) != 0) ? defaultPosition.x : Position.x,
                    !((SyncData & SyncTransformState.PositionY) != 0) ? defaultPosition.y : Position.y,
                    !((SyncData & SyncTransformState.PositionZ) != 0) ? defaultPosition.z : Position.z);
            }

            public Vector3 GetEulerAngles(Vector3 defaultEulerAngles)
            {
                return new Vector3(
                    !((SyncData & SyncTransformState.EulerAnglesX) != 0) ? defaultEulerAngles.x : EulerAngles.x,
                    !((SyncData & SyncTransformState.EulerAnglesY) != 0) ? defaultEulerAngles.y : EulerAngles.y,
                    !((SyncData & SyncTransformState.EulerAnglesZ) != 0) ? defaultEulerAngles.z : EulerAngles.z);
            }

            public Vector3 GetScale(Vector3 defaultScale)
            {
                return new Vector3(
                    !((SyncData & SyncTransformState.ScaleX) != 0) ? defaultScale.x : Scale.x,
                    !((SyncData & SyncTransformState.ScaleY) != 0) ? defaultScale.y : Scale.y,
                    !((SyncData & SyncTransformState.ScaleZ) != 0) ? defaultScale.z : Scale.z);
            }
        }

        public class SyncTransforms : SortedList<uint, TransformData>, INetSerializable
        {
            public void Serialize(NetDataWriter writer)
            {
                writer.Put(Count);
                foreach (var entry in this)
                {
                    entry.Value.Serialize(writer);
                }
            }

            public void Deserialize(NetDataReader reader)
            {
                Clear();
                int count = reader.GetInt();
                for (int i = 0; i < count; ++i)
                {
                    TransformData entry = reader.Get<TransformData>();
                    Add(entry.Tick, entry);
                }
            }
        }

        public class SyncTransformsField : LiteNetLibSyncField<SyncTransforms>
        {
            public SyncTransformsField()
            {
                _value = new SyncTransforms();
            }

            internal override void SerializeValue(NetDataWriter writer)
            {
                _value.Serialize(writer);
            }

            internal override void DeserializeValue(NetDataReader reader)
            {
                _value.Deserialize(reader);
            }

            protected override bool IsValueChanged(SyncTransforms oldValue, SyncTransforms newValue)
            {
                return true;
            }
        }

        [Header("Sync Settings")]
        [Tooltip("If this is TRUE, transform data will be sent from owner client to server to update to another clients")]
        [FormerlySerializedAs("ownerClientCanSendTransform")]
        public bool syncByOwnerClient;
        [Tooltip("Whats will be synced?")]
        public SyncTransformState syncData = SyncTransformState.PositionX | SyncTransformState.PositionY | SyncTransformState.PositionZ | SyncTransformState.EulerAnglesY;
        [Tooltip("If distance between current frame and previous frame is greater than this value, then it will determine that changes occurs and will sync transform later")]
        [Min(0.01f)]
        public float positionThreshold = 0.01f;
        [Tooltip("If angle between current frame and previous frame is greater than this value, then it will determine that changes occurs and will sync transform later")]
        [Min(0.01f)]
        public float eulerAnglesThreshold = 1f;
        [Tooltip("If distance between current frame and previous frame is greater than this value, then it will determine that changes occurs and will sync transform later")]
        [Min(0.01f)]
        public float scaleThreshold = 0.1f;
        [Tooltip("Ticks for interpolation")]
        [Min(1)]
        public uint interpolationTicks = 2;
        [Tooltip("Number of recent outbound transform snapshots included in each sync payload. Two gives one current and one redundant historical sample.")]
        [Range(1, 8)]
        public int outboundSnapshotHistory = 2;

        public event WriteSyncBufferDelegate onWriteSyncBuffer;
        public event ReadInterpBufferDelegate onReadInterpBuffer;
        public event ValidateInterpolationDelegate onValidateInterpolation;
        public event InterpolateDelegate onInterpolate;

        private TransformData _prevSyncData;
        private TransformData _interpFromData;
        private TransformData _interpToData;
        private uint _prevInterpFromTick;
        private float _startInterpTime;
        private float _endInterpTime;

        private readonly SyncTransforms _clientSyncBuffers = new SyncTransforms();
        private readonly SyncTransformsField _syncBuffers = new SyncTransformsField()
        {
            syncMode = LiteNetLibSyncFieldMode.ServerToClients,
        };
        private SortedList<uint, TransformData> _interpBuffers = new SortedList<uint, TransformData>();

        private LogicUpdater _logicUpdater = null;
        private uint _interpTick;
        public uint InitialInterpTick { get; private set; }
        public uint RenderTick => _interpTick - interpolationTicks;

        private void Awake()
        {
            _syncBuffers.onChange += OnSyncBuffersChanged;
        }

        private void OnDestroy()
        {
            LiteNetLibTransformInterpolationSystem.Unregister(this);
            _syncBuffers.onChange -= OnSyncBuffersChanged;
        }

        public override void OnIdentityInitialize()
        {
            if (_logicUpdater == null)
            {
                _logicUpdater = Manager.LogicUpdater;
                _logicUpdater.OnTick += LogicUpdater_OnTick;
            }
            _interpFromData = _interpToData = new TransformData()
            {
                Position = transform.position,
                EulerAngles = transform.eulerAngles,
                Scale = transform.localScale,
            };
            ResetBuffersAndStates();
            LiteNetLibTransformInterpolationSystem.Register(this);
        }

        public override void OnIdentityDestroy()
        {
            LiteNetLibTransformInterpolationSystem.Unregister(this);
            if (_logicUpdater != null)
                _logicUpdater.OnTick -= LogicUpdater_OnTick;
        }

        public override void OnSetOwnerClient(bool isOwnerClient)
        {
            ResetBuffersAndStates();
        }

        private void ResetBuffersAndStates()
        {
            _clientSyncBuffers.Clear();
            _syncBuffers.Value.Clear();
            _interpBuffers.Clear();
            _interpTick = InitialInterpTick = 0;
            _prevSyncData = new TransformData()
            {
                Tick = Manager.LocalTick,
                SyncData = syncData,
                Position = transform.position,
                EulerAngles = transform.eulerAngles,
                Scale = transform.localScale,
            };
        }

        private void OnApplicationPause(bool pause)
        {
            if (pause)
                return;
            ResetBuffersAndStates();
        }

        private bool HasPositionChanged(Vector3 current, Vector3 previous)
        {
            float distanceSqr = 0f;
            float delta;

            if ((syncData & SyncTransformState.PositionX) != 0)
            {
                delta = current.x - previous.x;
                distanceSqr += delta * delta;
            }
            if ((syncData & SyncTransformState.PositionY) != 0)
            {
                delta = current.y - previous.y;
                distanceSqr += delta * delta;
            }
            if ((syncData & SyncTransformState.PositionZ) != 0)
            {
                delta = current.z - previous.z;
                distanceSqr += delta * delta;
            }

            return distanceSqr > positionThreshold * positionThreshold;
        }

        private bool HasEulerAnglesChanged(Vector3 current, Vector3 previous)
        {
            if ((syncData & SyncTransformState.EulerAnglesX) != 0 &&
                Mathf.Abs(Mathf.DeltaAngle(previous.x, current.x)) > eulerAnglesThreshold)
                return true;
            if ((syncData & SyncTransformState.EulerAnglesY) != 0 &&
                Mathf.Abs(Mathf.DeltaAngle(previous.y, current.y)) > eulerAnglesThreshold)
                return true;
            if ((syncData & SyncTransformState.EulerAnglesZ) != 0 &&
                Mathf.Abs(Mathf.DeltaAngle(previous.z, current.z)) > eulerAnglesThreshold)
                return true;
            return false;
        }

        private bool HasScaleChanged(Vector3 current, Vector3 previous)
        {
            float distanceSqr = 0f;
            float delta;

            if ((syncData & SyncTransformState.ScaleX) != 0)
            {
                delta = current.x - previous.x;
                distanceSqr += delta * delta;
            }
            if ((syncData & SyncTransformState.ScaleY) != 0)
            {
                delta = current.y - previous.y;
                distanceSqr += delta * delta;
            }
            if ((syncData & SyncTransformState.ScaleZ) != 0)
            {
                delta = current.z - previous.z;
                distanceSqr += delta * delta;
            }

            return distanceSqr > scaleThreshold * scaleThreshold;
        }

        private void LogicUpdater_OnTick(LogicUpdater updater)
        {
            _interpTick++;

            TransformData transformData = _prevSyncData;
            Vector3 currentPosition = transform.position;
            Vector3 currentEulerAngles = transform.eulerAngles;
            Vector3 currentScale = transform.localScale;
            bool changed =
                HasPositionChanged(currentPosition, transformData.Position) ||
                HasEulerAnglesChanged(currentEulerAngles, transformData.EulerAngles) ||
                HasScaleChanged(currentScale, transformData.Scale);

            if (!changed)
                return;

            transformData.Tick = updater.LocalTick;
            transformData.SyncData = syncData;
            transformData.Position = currentPosition;
            transformData.EulerAngles = currentEulerAngles;
            transformData.Scale = currentScale;
            _prevSyncData = transformData;

            transformData.Tick = updater.LocalTick;
            if (!syncByOwnerClient && IsServer)
            {
                StoreSyncBuffer(_syncBuffers.Value, transformData, outboundSnapshotHistory);
                _syncBuffers.MarkAsChanged();
            }
            else if (syncByOwnerClient && IsOwnedByServer)
            {
                StoreSyncBuffer(_syncBuffers.Value, transformData, outboundSnapshotHistory);
                _syncBuffers.MarkAsChanged();
            }
            else if (syncByOwnerClient && IsOwnerClient)
            {
                StoreSyncBuffer(_clientSyncBuffers, transformData, outboundSnapshotHistory);
                RPC(OwnerSyncTransform, 0, LiteNetLib.DeliveryMethod.Unreliable, _clientSyncBuffers);
            }
        }

        internal void UpdateInterpolation()
        {
            if (!IsSpawned)
                return;
            if (!syncByOwnerClient && !IsServer)
            {
                InterpolateTransform();
            }
            if (syncByOwnerClient && !IsOwnedByServer && !IsOwnerClient)
            {
                InterpolateTransform();
            }
        }

        private void InterpolateTransform()
        {
            if (_interpBuffers.Count < 2)
            {
                _prevInterpFromTick = 0;
                return;
            }

            float currentTime = Time.time;
            uint renderTick = RenderTick;

            // Find two ticks around renderTick
            uint interpFromTick = 0;
            uint interpToTick = 0;

            for (int i = _interpBuffers.Count - 1; i >= 1; --i)
            {
                uint tick1 = _interpBuffers.Keys[i - 1];
                uint tick2 = _interpBuffers.Keys[i];
                TransformData data1 = _interpBuffers[tick1];
                TransformData data2 = _interpBuffers[tick2];

                if (tick1 <= renderTick && renderTick <= tick2)
                {
                    interpFromTick = tick1;
                    interpToTick = tick2;
                    _interpFromData = new TransformData()
                    {
                        Tick = data1.Tick,
                        Position = data1.GetPosition(transform.position),
                        EulerAngles = data1.GetEulerAngles(transform.eulerAngles),
                        Scale = data1.GetScale(transform.localScale),
                    };
                    _interpToData = new TransformData()
                    {
                        Tick = data2.Tick,
                        Position = data2.GetPosition(transform.position),
                        EulerAngles = data2.GetEulerAngles(transform.eulerAngles),
                        Scale = data2.GetScale(transform.localScale),
                    };
                    if (_prevInterpFromTick != interpFromTick)
                    {
                        _startInterpTime = currentTime;
                        _endInterpTime = currentTime + (_logicUpdater.DeltaTimeF * (tick2 - tick1));
                        _prevInterpFromTick = interpFromTick;
                    }
                    break;
                }
            }

            float t = Mathf.InverseLerp(_startInterpTime, _endInterpTime, currentTime);
            Quaternion fromRot = Quaternion.Euler(_interpFromData.EulerAngles);
            Quaternion toRot = Quaternion.Euler(_interpToData.EulerAngles);
            Quaternion currentRot = Quaternion.Slerp(fromRot, toRot, t);
            TransformData currentInterp = new TransformData()
            {
                Position = Vector3.Lerp(_interpFromData.Position, _interpToData.Position, t),
                EulerAngles = currentRot.eulerAngles,
                Scale = Vector3.Lerp(_interpFromData.Scale, _interpToData.Scale, t),
            };
            if (onValidateInterpolation != null && !onValidateInterpolation.Invoke(_interpFromData, _interpToData, currentInterp, t))
            {
                // Not pass the validation
                return;
            }
            transform.position = currentInterp.Position;
            transform.eulerAngles = currentInterp.EulerAngles;
            transform.localScale = currentInterp.Scale;
            onInterpolate?.Invoke(_interpFromData, _interpToData, t);
        }

        [ServerRpc]
        private void OwnerSyncTransform(SyncTransforms data)
        {
            if (!syncByOwnerClient && IsServer)
                return;
            StoreInterpolateBuffers(data, 30);
            if (!IsOwnerClient && _interpBuffers.Count > 0)
            {
                uint interpTick = _interpBuffers.Keys[_interpBuffers.Count - 1];
                if (Player != null)
                    interpTick += LogicUpdater.TimeToTick(Player.Rtt / 2, _logicUpdater.DeltaTime);
                if (_interpTick > interpTick && _interpTick - interpTick > 2)
                    _interpTick = InitialInterpTick = interpTick;
                if (interpTick > _interpTick && interpTick - _interpTick > 2)
                    _interpTick = InitialInterpTick = interpTick;
            }
            // Sync to other clients immediately
            foreach (var entry in data)
            {
                StoreSyncBuffer(_syncBuffers.Value, entry.Value, outboundSnapshotHistory);
            }
            _syncBuffers.MarkAsChanged();
        }

        private void OnSyncBuffersChanged(bool initial, SyncTransforms oldValue, SyncTransforms newValue)
        {
            if (IsServer)
                return;
            if (syncByOwnerClient && IsOwnerClient)
                return;
            StoreInterpolateBuffers(newValue, 30);
            if (_interpBuffers.Count > 0)
            {
                uint interpTick = _interpBuffers.Keys[_interpBuffers.Count - 1];
                interpTick += LogicUpdater.TimeToTick(Manager.Rtt / 2, _logicUpdater.DeltaTime);
                if (_interpTick > interpTick && _interpTick - interpTick > 2)
                    _interpTick = InitialInterpTick = interpTick;
                if (interpTick > _interpTick && interpTick - _interpTick > 2)
                    _interpTick = InitialInterpTick = interpTick;
            }
        }

        private void StoreInterpolateBuffers(SyncTransforms data, int maxBuffers = 3)
        {
            foreach (var entry in data)
            {
                if (_interpBuffers.ContainsKey(entry.Key))
                    continue;
                if (entry.Value.Extra != null)
                {
                    s_ExtraReader.SetSource(entry.Value.Extra);
                    onReadInterpBuffer?.Invoke(s_ExtraReader, entry.Key);
                }
                _interpBuffers.Add(entry.Key, entry.Value);
            }
            // Prune old ticks (keep last N)
            while (_interpBuffers.Count > maxBuffers)
            {
                _interpBuffers.RemoveAt(0);
            }
        }

        private void StoreSyncBuffer(SortedList<uint, TransformData> buffers, TransformData entry, int maxBuffers)
        {
            maxBuffers = Mathf.Max(1, maxBuffers);
            if (!buffers.ContainsKey(entry.Tick))
            {
                s_ExtraWriter.Reset();
                onWriteSyncBuffer?.Invoke(s_ExtraWriter, entry.Tick);
                if (s_ExtraWriter.Length > 0)
                    entry.Extra = s_ExtraWriter.CopyData();
                buffers.Add(entry.Tick, entry);
            }
            // Prune old ticks (keep last N)
            while (buffers.Count > maxBuffers)
            {
                buffers.RemoveAt(0);
            }
        }
    }
}
