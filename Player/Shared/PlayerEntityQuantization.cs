using UnityEngine;

namespace Player.Shared
{
    public static class PlayerEntityQuantization
    {
        public const float PositionPrecision = 0.01f;
        public const float SpeedPrecision = 0.01f;
        public const float InputPrecision = 1f / 127f;

        public static int QuantizePosition(float value)
        {
            double scaled = value / PositionPrecision;
            if (scaled >= int.MaxValue) return int.MaxValue;
            if (scaled <= int.MinValue) return int.MinValue;
            return Mathf.RoundToInt((float)scaled);
        }

        public static float DequantizePosition(int value) => value * PositionPrecision;
        public static Vector3 DequantizePosition(int x, int y, int z) => new Vector3(DequantizePosition(x), DequantizePosition(y), DequantizePosition(z));

        public const int YawBits = 8;
        public const int YawSteps = 1 << YawBits;
        public const float YawStepDegrees = 360f / YawSteps;

        public static byte QuantizeYaw(float yaw)
        {
            float normalized = Mathf.Repeat(yaw, 360f) / 360f;
            int quantized = Mathf.RoundToInt(normalized * YawSteps) & (YawSteps - 1);
            return (byte)quantized;
        }

        public static float DequantizeYaw(byte yaw) => yaw * YawStepDegrees;

        public static ushort QuantizeUnsignedSpeed(float speed) => (ushort)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, speed) / SpeedPrecision), 0, ushort.MaxValue);
        public static short QuantizeSignedSpeed(float speed) => (short)Mathf.Clamp(Mathf.RoundToInt(speed / SpeedPrecision), short.MinValue, short.MaxValue);
        public static float DequantizeUnsignedSpeed(ushort speed) => speed * SpeedPrecision;
        public static float DequantizeSignedSpeed(short speed) => speed * SpeedPrecision;
        public static sbyte QuantizeInput(float value) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 127f), -127, 127);
        public static float DequantizeInput(sbyte value) => value * InputPrecision;
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
