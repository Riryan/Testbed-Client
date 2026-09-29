using System;
using Game.Shared.Interactions;
using Game.Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring
{
    [AddComponentMenu("MMO/World Interactable")]
    [RequireComponent(typeof(WorldObject))]
    public sealed class WorldInteractable : MonoBehaviour
    {
        [Serializable]
        public sealed class ParticipantRole
        {
            public string roleId = "Primary";
            public string displayLabel = "Primary";
            public ServerInteractionPresentationRole presentationRole = ServerInteractionPresentationRole.Primary;
            public bool required = true;
        }

        [Serializable]
        public sealed class Interaction
        {
            public string definitionId = "use";
            [Tooltip("Small stable top-level interaction category. Specific behavior stays in actionId/profile data.")]
            public InteractionCategoryId categoryId = InteractionCategoryId.Use;
            public InteractionActionId actionId = InteractionActionId.Use;
            public string displayLabel = "Use";
            [Min(0.1f)] public float maximumUseDistance = InteractionRangePolicy.WorldObjectUseRange;
            [Range(0f, 180f)] public float maximumFacingAngle = 180f;
            public bool exclusiveOccupancy;
            public bool looping;
            [Min(0f)] public float fixedDurationSeconds;
            public bool lockMovement;
            public bool lockRotation;
            public bool cancelOnDamage = true;
            public bool cancelOnMovement = true;
            public bool cancelOnTargetUnavailable = true;
            public InteractionConsentMode consentMode;
            public InteractionContentLevel contentLevel = InteractionContentLevel.General;
            public InteractionFeature feature = InteractionFeature.WorldObjects;
            public ParticipantRole[] participantRoles = Array.Empty<ParticipantRole>();
        }

        [Serializable]
        public sealed class Slot
        {
            public string slotId = "Primary";
            public string roleId = "Primary";
            public ServerInteractionPresentationRole presentationRole = ServerInteractionPresentationRole.Primary;
            public Transform anchor;
            public Transform approach;
            public Transform exit;
            public Transform leftHandTarget;
            public Transform rightHandTarget;
            public Transform leftFootTarget;
            public Transform rightFootTarget;
        }

        [Header("Identity")]
        [SerializeField, HideInInspector] private string bakeId = string.Empty;
        public string definitionId = "world_object";
        public string label = "World Object";
        public ServerWorldInteractableKind kind = ServerWorldInteractableKind.Generic;
        [Tooltip("Optional authoritative gameplay profile ID. Semantic/string ID shared by server content and world authoring.")]
        public string gameplayProfileId = string.Empty;
        [Tooltip("Optional loot-table semantic ID used by searchable/harvest world objects.")]
        public string lootTableId = string.Empty;
        [Tooltip("Optional crafting-station semantic ID used by crafting world objects.")]
        public string craftingStationId = string.Empty;
        [Tooltip("Optional faction semantic ID used by recruiter/authority world objects.")]
        public string factionDefinitionId = string.Empty;

        [Header("Observation")]
        public bool surveillanceCamera;
        [Min(0.5f)] public float surveillanceRadius = 12f;
        [Min(0)] public int surveillanceEvidence = 1;

        [Header("State")]
        public bool persistentState;
        public bool enabledByDefault = true;

        [Header("Interactions")]
        public Interaction[] interactions = Array.Empty<Interaction>();
        public Slot[] slots = Array.Empty<Slot>();

        [Header("Dynamic Blocker")]
        public bool dynamicBlocker;
        public ServerDynamicBlockerKind blockerKind = ServerDynamicBlockerKind.Door;
        public Transform blockerAnchor;
        public Vector3 blockerSize = new Vector3(1f, 2f, 0.2f);
        public bool blockerEnabledByDefault = true;

        public string BakeId => bakeId;

        private void Reset()
        {
            EnsureBakeId();
            interactions = new[]
            {
                new Interaction
                {
                    definitionId = "use",
                    categoryId = InteractionCategoryId.Use,
                    actionId = InteractionActionId.Use,
                    displayLabel = "Use",
                }
            };
        }

        private void OnValidate()
        {
            EnsureBakeId();
            definitionId = string.IsNullOrWhiteSpace(definitionId) ? "world_object" : ServerMap.NormalizeId(definitionId);
            label = string.IsNullOrWhiteSpace(label) ? gameObject.name : label.Trim();
            gameplayProfileId = string.IsNullOrWhiteSpace(gameplayProfileId) ? string.Empty : gameplayProfileId.Trim();
            lootTableId = string.IsNullOrWhiteSpace(lootTableId) ? string.Empty : lootTableId.Trim();
            craftingStationId = string.IsNullOrWhiteSpace(craftingStationId) ? string.Empty : craftingStationId.Trim();
            factionDefinitionId = string.IsNullOrWhiteSpace(factionDefinitionId) ? string.Empty : factionDefinitionId.Trim();
            surveillanceRadius = Mathf.Max(0.5f, surveillanceRadius);
            surveillanceEvidence = Mathf.Max(0, surveillanceEvidence);
            blockerSize.x = Mathf.Max(0.01f, blockerSize.x);
            blockerSize.y = Mathf.Max(0.01f, blockerSize.y);
            blockerSize.z = Mathf.Max(0.01f, blockerSize.z);
            interactions ??= Array.Empty<Interaction>();
            for (int i = 0; i < interactions.Length; ++i)
            {
                Interaction interaction = interactions[i];
                if (interaction == null) continue;
                if (interaction.categoryId == InteractionCategoryId.None)
                    interaction.categoryId = InteractionCategoryCatalog.ForTargetAction(InteractionTargetKind.SceneObject, interaction.actionId);
                interaction.definitionId = string.IsNullOrWhiteSpace(interaction.definitionId)
                    ? interaction.actionId.ToString().ToLowerInvariant()
                    : interaction.definitionId.Trim();
                interaction.displayLabel = string.IsNullOrWhiteSpace(interaction.displayLabel)
                    ? interaction.actionId.ToString()
                    : interaction.displayLabel.Trim();
            }
            slots ??= Array.Empty<Slot>();
        }

        private void EnsureBakeId()
        {
            if (string.IsNullOrWhiteSpace(bakeId))
                bakeId = Guid.NewGuid().ToString("N");
        }
    }
}
