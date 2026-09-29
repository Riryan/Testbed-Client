using System;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only camera presentation modes. These are deliberately semantic rather
    /// than weapon-specific so the same camera shell can support MMO, TPS, FPS-like,
    /// vehicle, social, and other play styles without changing movement authority.
    /// </summary>
    public enum PlayerCameraPresentationState : byte
    {
        Normal = 0,
        Fire = 1,
        Aim = 2,
        Scope = 3,
        Vehicle = 4,
        Dead = 5,
    }

    [Serializable]
    public sealed class PlayerCameraPresentationProfile
    {
        [Tooltip("Additional camera-local offset applied after the canonical locomotion camera. X=right, Y=up, Z=forward.")]
        public Vector3 cameraLocalOffset = Vector3.zero;

        [Tooltip("Target camera field of view. Set to 0 or less to preserve the gameplay camera's baseline FOV.")]
        public float fieldOfView = 0f;

        [Tooltip("Seconds used to blend local offset and FOV into this state.")]
        [Min(0.01f)]
        public float blendSeconds = 0.18f;

        [Tooltip("Uses the existing combat-camera facing contract while this state is active. This affects local facing intent only; the GameServer remains authoritative.")]
        public bool useCombatFacing;

        public static PlayerCameraPresentationProfile NormalDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = Vector3.zero,
                fieldOfView = 0f,
                blendSeconds = 0.18f,
                useCombatFacing = false,
            };
        }

        public static PlayerCameraPresentationProfile FireDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = new Vector3(0.05f, 0f, 0.10f),
                fieldOfView = 0f,
                blendSeconds = 0.10f,
                useCombatFacing = true,
            };
        }

        public static PlayerCameraPresentationProfile AimDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = new Vector3(0.14f, 0.02f, 0.45f),
                fieldOfView = 52f,
                blendSeconds = 0.14f,
                useCombatFacing = true,
            };
        }

        public static PlayerCameraPresentationProfile ScopeDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = new Vector3(0.18f, 0.02f, 0.72f),
                fieldOfView = 32f,
                blendSeconds = 0.10f,
                useCombatFacing = true,
            };
        }

        public static PlayerCameraPresentationProfile VehicleDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = new Vector3(0f, 0.30f, -0.90f),
                fieldOfView = 68f,
                blendSeconds = 0.28f,
                useCombatFacing = false,
            };
        }

        public static PlayerCameraPresentationProfile DeadDefault()
        {
            return new PlayerCameraPresentationProfile
            {
                cameraLocalOffset = new Vector3(0f, 0.65f, -1.35f),
                fieldOfView = 64f,
                blendSeconds = 0.35f,
                useCombatFacing = false,
            };
        }
    }
}
