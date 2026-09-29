using Game.Client.UI.Interactions;
using Game.Shared.Interactions;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    public static class WorldInteractableTools
    {
        [MenuItem("MMO Tools/World/Add World Interactable/Harvest Node")]
        public static void AddHarvestNode()
        {
            GameObject target = Selection.activeGameObject;
            if (target == null)
            {
                target = new GameObject("Recovery Salvage");
                Undo.RegisterCreatedObjectUndo(target, "Create Harvest Node");
                Selection.activeGameObject = target;
            }

            WorldObject worldObject = target.GetComponent<WorldObject>();
            if (worldObject == null) worldObject = Undo.AddComponent<WorldObject>(target);

            WorldInteractable interactable = target.GetComponent<WorldInteractable>();
            if (interactable == null) interactable = Undo.AddComponent<WorldInteractable>(target);

            ClientInteractionTargetMarker marker = target.GetComponent<ClientInteractionTargetMarker>();
            if (marker == null) marker = Undo.AddComponent<ClientInteractionTargetMarker>(target);

            Undo.RecordObject(interactable, "Configure Harvest Node");
            interactable.definitionId = "recovery_salvage";
            interactable.label = "Recovery Salvage";
            interactable.kind = Game.Shared.World.ServerWorldInteractableKind.HarvestNode;
            interactable.gameplayProfileId = "recovery_salvage";
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "harvest",
                    actionId = InteractionActionId.Harvest,
                    displayLabel = "Harvest",
                    categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = InteractionRangePolicy.WorldObjectUseRange,
                    maximumFacingAngle = 180f,
                    exclusiveOccupancy = true,
                    looping = false,
                    fixedDurationSeconds = 3f,
                    lockMovement = false,
                    lockRotation = false,
                    cancelOnDamage = true,
                    cancelOnMovement = true,
                    cancelOnTargetUnavailable = true,
                    consentMode = InteractionConsentMode.None,
                    contentLevel = InteractionContentLevel.General,
                    feature = InteractionFeature.Harvesting,
                }
            };
            interactable.slots = System.Array.Empty<WorldInteractable.Slot>();
            EditorUtility.SetDirty(interactable);
            EditorUtility.SetDirty(marker);

            Debug.Log("[MMO Tools] Harvest node configured. Server World Bake will assign the scene interaction stable ID to the client marker.", target);
        }
    }
}
