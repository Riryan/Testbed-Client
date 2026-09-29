using Game.Shared.Interactions;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    public static class DummyDoorBuilder
    {
        [MenuItem("MMO Tools/World/Create Dummy Door Test")]
        private static void CreateDummyDoor()
        {
            GameObject root = new GameObject("Dummy Door Test");
            Undo.RegisterCreatedObjectUndo(root, "Create Dummy Door Test");

            if (SceneView.lastActiveSceneView != null)
            {
                Vector3 pivot = SceneView.lastActiveSceneView.pivot;
                root.transform.position = new Vector3(pivot.x, Mathf.Max(0f, pivot.y), pivot.z);
            }

            WorldObject worldObject = Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "dummy_door";
            interactable.kind = ServerWorldInteractableKind.Door;
            interactable.gameplayProfileId = "door_dummy_test";
            interactable.label = "Dummy Door";
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.dynamicBlocker = true;
            interactable.blockerKind = ServerDynamicBlockerKind.Door;
            interactable.blockerEnabledByDefault = true;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "open",
                    actionId = InteractionActionId.Open,
                    displayLabel = "Open / Close",
                    categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f,
                    maximumFacingAngle = 180f,
                    exclusiveOccupancy = false,
                    looping = false,
                    fixedDurationSeconds = 0f,
                    lockMovement = false,
                    lockRotation = false,
                    cancelOnDamage = true,
                    cancelOnMovement = true,
                    cancelOnTargetUnavailable = true,
                    consentMode = InteractionConsentMode.None,
                    contentLevel = InteractionContentLevel.General,
                    feature = InteractionFeature.WorldObjects,
                }
            };

            GameObject leftPost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(leftPost, "Create Dummy Door Left Post");
            leftPost.name = "Frame Left";
            leftPost.transform.SetParent(root.transform, false);
            leftPost.transform.localPosition = new Vector3(-0.85f, 1.2f, 0.18f);
            leftPost.transform.localScale = new Vector3(0.18f, 2.6f, 0.2f);
            Object.DestroyImmediate(leftPost.GetComponent<Collider>());

            GameObject rightPost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(rightPost, "Create Dummy Door Right Post");
            rightPost.name = "Frame Right";
            rightPost.transform.SetParent(root.transform, false);
            rightPost.transform.localPosition = new Vector3(0.85f, 1.2f, 0.18f);
            rightPost.transform.localScale = new Vector3(0.18f, 2.6f, 0.2f);
            Object.DestroyImmediate(rightPost.GetComponent<Collider>());

            GameObject header = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(header, "Create Dummy Door Header");
            header.name = "Frame Header";
            header.transform.SetParent(root.transform, false);
            header.transform.localPosition = new Vector3(0f, 2.48f, 0.18f);
            header.transform.localScale = new Vector3(1.88f, 0.18f, 0.2f);
            Object.DestroyImmediate(header.GetComponent<Collider>());

            GameObject opening = new GameObject("Door Pivot");
            Undo.RegisterCreatedObjectUndo(opening, "Create Dummy Door Pivot");
            opening.transform.SetParent(root.transform, false);
            opening.transform.localPosition = new Vector3(-0.65f, 1.15f, 0f);

            GameObject leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(leaf, "Create Dummy Door Leaf");
            leaf.name = "Door Leaf";
            leaf.transform.SetParent(opening.transform, false);
            leaf.transform.localPosition = new Vector3(0.65f, 0f, 0f);
            leaf.transform.localScale = new Vector3(1.3f, 2.3f, 0.14f);

            GameObject blockerAnchor = new GameObject("Server Blocker Anchor");
            Undo.RegisterCreatedObjectUndo(blockerAnchor, "Create Dummy Door Blocker Anchor");
            blockerAnchor.transform.SetParent(root.transform, false);
            blockerAnchor.transform.localPosition = new Vector3(0f, 1.15f, 0f);

            interactable.blockerAnchor = blockerAnchor.transform;
            interactable.blockerSize = new Vector3(1.35f, 2.35f, 0.26f);

            WorldDoorPresentation presentation = Undo.AddComponent<WorldDoorPresentation>(root);
            SerializedObject serialized = new SerializedObject(presentation);
            serialized.FindProperty("doorPivot").objectReferenceValue = opening.transform;
            serialized.FindProperty("openYawDegrees").floatValue = 90f;
            serialized.FindProperty("openSeconds").floatValue = 0.35f;
            serialized.FindProperty("visuallyOpenByDefault").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            Debug.Log(
                "[Dummy Door] Created. Run Server World Bake V2 before Play/Client build so the door receives " +
                "its stable ID and dynamic blocker. In client, right-click the Door Leaf and choose Open / Close.");
        }
    }
}
