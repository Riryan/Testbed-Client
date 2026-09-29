using System;
using Game.Shared.Interactions;
using UnityEngine;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Client-only presentation target. NetworkObjectId/Generation are retained for
    /// current PlayerEntity requests while PrimaryId carries durable/non-network ids
    /// such as WorldItem instance ids. The server remains authoritative for execution.
    /// </summary>
    public readonly struct ClientInteractionTarget
    {
        public InteractionTargetKind Kind { get; }
        public long PrimaryId { get; }
        public uint NetworkObjectId { get; }
        public ushort Generation { get; }
        public string DisplayName { get; }
        public Vector3 WorldPosition { get; }
        public UnityEngine.Object SourceObject { get; }

        public bool IsValid =>
            Kind != InteractionTargetKind.None &&
            (PrimaryId > 0 || NetworkObjectId > 0);

        public ClientInteractionTarget(
            InteractionTargetKind kind,
            long primaryId,
            uint networkObjectId,
            ushort generation,
            string displayName,
            Vector3 worldPosition,
            UnityEngine.Object sourceObject)
        {
            Kind = kind;
            PrimaryId = primaryId;
            NetworkObjectId = networkObjectId;
            Generation = generation;
            DisplayName = displayName ?? string.Empty;
            WorldPosition = worldPosition;
            SourceObject = sourceObject;
        }
    }

    public readonly struct ClientInteractionActionView
    {
        public InteractionCategoryId CategoryId { get; }
        public InteractionActionId ActionId { get; }
        public string CategoryPath { get; }
        public string Label { get; }
        public string Description { get; }
        public bool Enabled { get; }
        public string DisabledReason { get; }
        public InteractionConsentMode ConsentMode { get; }
        public InteractionContentLevel ContentLevel { get; }
        public int SortOrder { get; }
        public bool IsPlanned { get; }
        public float MaximumUseDistance { get; }

        public ClientInteractionActionView(
            InteractionActionId actionId,
            string categoryPath,
            string label,
            string description,
            bool enabled,
            string disabledReason,
            InteractionConsentMode consentMode,
            InteractionContentLevel contentLevel,
            int sortOrder,
            bool isPlanned,
            InteractionCategoryId categoryId = InteractionCategoryId.None,
            float maximumUseDistance = 0f)
        {
            CategoryId = categoryId == InteractionCategoryId.None
                ? InteractionCategoryCatalog.DefaultForAction(actionId)
                : categoryId;
            ActionId = actionId;
            CategoryPath = InteractionCategoryCatalog.Label(CategoryId);
            if (string.IsNullOrWhiteSpace(CategoryPath))
                CategoryPath = string.IsNullOrWhiteSpace(categoryPath) ? "Interact" : categoryPath.Trim();
            Label = string.IsNullOrWhiteSpace(label) ? actionId.ToString() : label.Trim();
            Description = description ?? string.Empty;
            Enabled = enabled;
            DisabledReason = disabledReason ?? string.Empty;
            ConsentMode = consentMode;
            ContentLevel = contentLevel;
            SortOrder = sortOrder;
            IsPlanned = isPlanned;
            MaximumUseDistance = maximumUseDistance;
        }
    }

    public readonly struct ClientInteractionConsentRequestView
    {
        public uint SessionId { get; }
        public InteractionActionId ActionId { get; }
        public string Title { get; }
        public string Message { get; }
        public string RequesterName { get; }
        public double ExpiresAtRealtime { get; }
        public bool CanAccept { get; }

        public ClientInteractionConsentRequestView(
            uint sessionId,
            InteractionActionId actionId,
            string title,
            string message,
            string requesterName,
            double expiresAtRealtime,
            bool canAccept = true)
        {
            SessionId = sessionId;
            ActionId = actionId;
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            RequesterName = requesterName ?? string.Empty;
            ExpiresAtRealtime = expiresAtRealtime;
            CanAccept = canAccept;
        }
    }
}
