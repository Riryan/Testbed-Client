using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace Player.Shared
{
    [Serializable]
    public struct MovementCommand : INetSerializable, IEquatable<MovementCommand>
    {
        public uint sequence;
        public sbyte inputX;
        public sbyte inputZ;
        public byte yaw;
        public byte flags;
        public byte combatFlags;

        public Vector2 Input => new Vector2(PlayerEntityQuantization.DequantizeInput(inputX), PlayerEntityQuantization.DequantizeInput(inputZ));

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedUInt(sequence);
            writer.Put(inputX);
            writer.Put(inputZ);
            writer.Put(yaw);
            writer.Put(flags);
            writer.Put(combatFlags);
        }

        public void Deserialize(NetDataReader reader)
        {
            sequence = reader.GetPackedUInt();
            inputX = reader.GetSByte();
            inputZ = reader.GetSByte();
            yaw = reader.GetByte();
            flags = reader.GetByte();
            combatFlags = reader.GetByte();
        }

        public bool Equals(MovementCommand other) => sequence == other.sequence && inputX == other.inputX && inputZ == other.inputZ && yaw == other.yaw && flags == other.flags && combatFlags == other.combatFlags;
        public override bool Equals(object obj) => obj is MovementCommand other && Equals(other);
        public override int GetHashCode() => unchecked((int)sequence * 397 ^ inputX ^ (inputZ << 8) ^ (yaw << 16) ^ flags ^ (combatFlags << 24));
    }

    [Serializable]
    public struct PlayerEntitySnapshot : INetSerializable, IEquatable<PlayerEntitySnapshot>
    {
        public uint entityId;
        public ushort generation;
        public uint serverTick;
        public int positionX;
        public int positionY;
        public int positionZ;
        public byte yaw;
        public ushort moveSpeed;
        public short verticalSpeed;
        public int castingSkillHash;
        public byte moveState;
        public byte actionState;
        public byte actionId;
        public byte flags;

        public Vector3 Position => PlayerEntityQuantization.DequantizePosition(positionX, positionY, positionZ);
        public float YawDegrees => PlayerEntityQuantization.DequantizeYaw(yaw);
        public float MoveSpeed => PlayerEntityQuantization.DequantizeUnsignedSpeed(moveSpeed);
        public float VerticalSpeed => PlayerEntityQuantization.DequantizeSignedSpeed(verticalSpeed);

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedUInt(entityId);
            writer.Put(generation);
            writer.PutPackedUInt(serverTick);
            writer.PutPackedInt(positionX);
            writer.PutPackedInt(positionY);
            writer.PutPackedInt(positionZ);
            writer.Put(yaw);
            writer.Put(moveSpeed);
            writer.Put(verticalSpeed);
            writer.Put(castingSkillHash);
            writer.Put(moveState);
            writer.Put(actionState);
            writer.Put(actionId);
            writer.Put(flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            entityId = reader.GetPackedUInt();
            generation = reader.GetUShort();
            serverTick = reader.GetPackedUInt();
            positionX = reader.GetPackedInt();
            positionY = reader.GetPackedInt();
            positionZ = reader.GetPackedInt();
            yaw = reader.GetByte();
            moveSpeed = reader.GetUShort();
            verticalSpeed = reader.GetShort();
            castingSkillHash = reader.GetInt();
            moveState = reader.GetByte();
            actionState = reader.GetByte();
            actionId = reader.GetByte();
            flags = reader.GetByte();
        }

        public bool Equals(PlayerEntitySnapshot other)
        {
            return entityId == other.entityId && generation == other.generation && serverTick == other.serverTick &&
                   positionX == other.positionX && positionY == other.positionY && positionZ == other.positionZ && yaw == other.yaw &&
                   moveSpeed == other.moveSpeed && verticalSpeed == other.verticalSpeed &&
                   castingSkillHash == other.castingSkillHash && moveState == other.moveState && actionState == other.actionState && actionId == other.actionId && flags == other.flags;
        }

        public override bool Equals(object obj) => obj is PlayerEntitySnapshot other && Equals(other);
        public override int GetHashCode() => unchecked((int)entityId * 397 ^ generation ^ (int)serverTick);
    }


}
