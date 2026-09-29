using System;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Server
{
    /// <summary>
    /// REMOVE ME AFTER VALIDATION.
    ///
    /// Legacy Unity-authoritative PlayerEntity component retained only as a temporary
    /// migration marker. The canonical MMO authority now lives in the standalone .NET
    /// GameServer. This component intentionally performs no simulation, persistence,
    /// snapshot publication, appearance publication, or request handling.
    ///
    /// The canonical MMOPlayerEntity prefab and current editor installer no longer add
    /// this component. Once the client-only Unity path has passed compile/runtime checks,
    /// delete Assets/Player/Server and Player.Server.asmdef entirely if no external asset
    /// still references this script GUID.
    /// </summary>
    [Obsolete("REMOVE ME: Unity PlayerEntity authority moved to the standalone .NET GameServer.")]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlayerEntityServer : LiteNetLibBehaviour
    {
        // Intentionally empty migration stub.
    }
}
