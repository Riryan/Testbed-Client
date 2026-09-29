using System;
using Game.Shared.Interactions;
using UnityEngine;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Client presentation marker for world/scene targets. It is intentionally authority-free.
    /// Future NPC/Monster/Population/prop presentation can attach/configure this same component.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientInteractionTargetMarker : MonoBehaviour
    {
        [SerializeField] private InteractionTargetKind targetKind = InteractionTargetKind.SceneObject;
        [SerializeField] private long primaryId;
        [SerializeField] private uint networkObjectId;
        [SerializeField] private ushort generation;
        [SerializeField] private string displayName;
        [SerializeField] private InteractionPublicDefinition[] publicInteractions = Array.Empty<InteractionPublicDefinition>();

        public InteractionTargetKind TargetKind => targetKind;
        public long PrimaryId => primaryId;
        public uint NetworkObjectId => networkObjectId;
        public ushort Generation => generation;
        public string DisplayName => displayName;
        public InteractionPublicDefinition[] PublicInteractions => publicInteractions ?? Array.Empty<InteractionPublicDefinition>();
        public bool HasPublicInteractions => publicInteractions != null && publicInteractions.Length > 0;

        public void Configure(
            InteractionTargetKind kind,
            long id,
            string label,
            uint objectId = 0,
            ushort objectGeneration = 0,
            InteractionPublicDefinition[] safeInteractions = null)
        {
            targetKind = kind;
            primaryId = id;
            networkObjectId = objectId;
            generation = objectGeneration;
            displayName = label ?? string.Empty;
            if (safeInteractions != null)
                publicInteractions = safeInteractions;
        }

        public ClientInteractionTarget CaptureTarget() =>
            new ClientInteractionTarget(
                targetKind,
                primaryId,
                networkObjectId,
                generation,
                string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName,
                transform.position,
                this);
    }
}
