using Game.Shared.Interactions;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Keeps ordinary production WorldInteractable authoring unchanged while giving development
    /// fixtures a small task-oriented Inspector.
    /// </summary>
    [CustomEditor(typeof(WorldInteractable))]
    public sealed class WorldInteractableEditor : UnityEditor.Editor
    {
        private const string HarvestActorDummyDefinitionId = "harvest_actor_dummy";

        public override void OnInspectorGUI()
        {
            WorldInteractable interactable = (WorldInteractable)target;

            if (InteractionTestDummyTool.IsFixture(interactable))
            {
                DrawInteractionTestDummy(interactable);
                return;
            }

            if (IsHarvestActorDummy(interactable))
            {
                DrawHarvestActorDummy(interactable);
                return;
            }

            DrawDefaultInspector();
        }

        private void DrawInteractionTestDummy(WorldInteractable interactable)
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Interaction Test Dummy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Setup: choose a name, choose a test group, choose an interaction, then run Server World Bake V2. " +
                "The dummy auto-accepts only for this development fixture; production consent rules are unchanged.",
                MessageType.Info);

            string currentLabel =
                string.IsNullOrWhiteSpace(interactable.label)
                    ? "Interaction Test Dummy"
                    : interactable.label;

            EditorGUI.BeginChangeCheck();
            string nextLabel = EditorGUILayout.TextField("Name", currentLabel);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(interactable, "Rename Interaction Test Dummy");
                interactable.label =
                    string.IsNullOrWhiteSpace(nextLabel)
                        ? "Interaction Test Dummy"
                        : nextLabel.Trim();
                interactable.gameObject.name = interactable.label;
                EditorUtility.SetDirty(interactable);
            }

            InteractionActionId currentAction =
                interactable.interactions != null &&
                interactable.interactions.Length > 0 &&
                interactable.interactions[0] != null
                    ? interactable.interactions[0].actionId
                    : InteractionActionId.Hug;

            InteractionTestDummyTool.TestGroup currentGroup =
                InteractionTestDummyTool.GroupFor(currentAction);

            string[] groupLabels =
            {
                InteractionTestDummyTool.GroupLabel(InteractionTestDummyTool.TestGroup.Basic),
                InteractionTestDummyTool.GroupLabel(InteractionTestDummyTool.TestGroup.SocialPlayer),
                InteractionTestDummyTool.GroupLabel(InteractionTestDummyTool.TestGroup.Vampire),
                InteractionTestDummyTool.GroupLabel(InteractionTestDummyTool.TestGroup.Hunter),
            };

            EditorGUI.BeginChangeCheck();
            int selectedGroupIndex = EditorGUILayout.Popup(
                "Test Group",
                (int)currentGroup,
                groupLabels);
            if (EditorGUI.EndChangeCheck())
            {
                InteractionTestDummyTool.TestGroup selectedGroup =
                    (InteractionTestDummyTool.TestGroup)selectedGroupIndex;
                InteractionActionId[] groupActions =
                    InteractionTestDummyTool.ActionsFor(selectedGroup);
                InteractionActionId nextAction =
                    groupActions.Length > 0
                        ? groupActions[0]
                        : InteractionActionId.Inspect;

                Undo.RecordObject(interactable, "Change Interaction Test Group");
                InteractionTestDummyTool.ConfigureAction(interactable, nextAction);
                EditorUtility.SetDirty(interactable);
                currentAction = nextAction;
                currentGroup = selectedGroup;
            }

            InteractionActionId[] actions =
                InteractionTestDummyTool.ActionsFor(currentGroup);
            string[] actionLabels = new string[actions.Length];
            int selectedActionIndex = 0;

            for (int i = 0; i < actions.Length; ++i)
            {
                actionLabels[i] = InteractionTestDummyTool.ActionLabel(actions[i]);
                if (actions[i] == currentAction)
                    selectedActionIndex = i;
            }

            EditorGUI.BeginChangeCheck();
            selectedActionIndex = EditorGUILayout.Popup(
                "Interaction",
                selectedActionIndex,
                actionLabels);
            if (EditorGUI.EndChangeCheck() &&
                selectedActionIndex >= 0 &&
                selectedActionIndex < actions.Length)
            {
                Undo.RecordObject(interactable, "Change Interaction Test Action");
                currentAction = actions[selectedActionIndex];
                InteractionTestDummyTool.ConfigureAction(interactable, currentAction);
                EditorUtility.SetDirty(interactable);
            }

            EditorGUILayout.Space(4f);

            byte initiator =
                InteractionPresentationWire.EncodeInteraction(
                    currentAction,
                    receiver: false);
            byte receiver =
                InteractionPresentationWire.EncodeInteraction(
                    currentAction,
                    receiver: true);
            double duration =
                InteractionPresentationWire.DevelopmentDurationSeconds(currentAction);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    "Player Animation",
                    InteractionPresentationWire.DebugLabel(initiator));
                EditorGUILayout.TextField(
                    "Dummy Animation",
                    InteractionPresentationWire.DebugLabel(receiver));
                EditorGUILayout.DoubleField(
                    "Auto Duration",
                    duration);
            }

            EditorGUILayout.Space(4f);
            DrawInteractionFixtureStatus(interactable);

            if (!IsInteractionFixtureHealthy(interactable))
            {
                EditorGUILayout.Space(4f);
                if (GUILayout.Button("Repair Interaction Test Dummy"))
                {
                    serializedObject.ApplyModifiedProperties();
                    InteractionTestDummyTool.Repair(interactable);
                    serializedObject.Update();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawInteractionFixtureStatus(WorldInteractable interactable)
        {
            WorldObject worldObject = interactable.GetComponent<WorldObject>();
            bool hasPresentation =
                interactable.GetComponent<Game.Client.UI.Interactions.InteractionTestDummyPresentation>() != null;
            Animator[] animators = interactable.GetComponentsInChildren<Animator>(true);
            bool hasAnimator = false;
            for (int i = 0; i < animators.Length; ++i)
            {
                if (animators[i] != null &&
                    animators[i].runtimeAnimatorController != null)
                {
                    hasAnimator = true;
                    break;
                }
            }

            if (worldObject == null || !hasPresentation || !hasAnimator)
            {
                EditorGUILayout.HelpBox(
                    "NEEDS REPAIR — required fixture component or receiver Animator is missing.",
                    MessageType.Error);
                return;
            }

            if (worldObject.StableId <= 0)
            {
                EditorGUILayout.HelpBox(
                    "READY TO BAKE — save the scene, then run Server World Bake V2.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "READY — baked identity is present. Start the normal client/server and interact with the dummy.",
                MessageType.Info);
        }

        private static bool IsInteractionFixtureHealthy(WorldInteractable interactable)
        {
            if (interactable == null ||
                interactable.GetComponent<WorldObject>() == null ||
                interactable.GetComponent<Game.Client.UI.Interactions.InteractionTestDummyPresentation>() == null ||
                interactable.interactions == null ||
                interactable.interactions.Length != 1 ||
                interactable.interactions[0] == null ||
                interactable.interactions[0].actionId == InteractionActionId.None ||
                interactable.slots == null ||
                interactable.slots.Length == 0 ||
                interactable.slots[0] == null ||
                interactable.slots[0].anchor == null)
            {
                return false;
            }

            Animator[] animators = interactable.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; ++i)
            {
                if (animators[i] != null &&
                    animators[i].runtimeAnimatorController != null)
                    return true;
            }

            return false;
        }

        private static bool IsHarvestActorDummy(WorldInteractable interactable)
        {
            return interactable != null &&
                   string.Equals(
                       interactable.definitionId,
                       HarvestActorDummyDefinitionId,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        private void DrawHarvestActorDummy(WorldInteractable interactable)
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Harvest Actor Dummy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Legacy Harvest-specific test fixture. Position it, save the scene, run Server World Bake V2, then test.",
                MessageType.Info);

            SerializedProperty label = serializedObject.FindProperty("label");
            if (label != null)
                EditorGUILayout.PropertyField(label, new GUIContent("Name"));

            WorldObject worldObject = interactable.GetComponent<WorldObject>();
            if (worldObject != null && worldObject.StableId > 0)
            {
                EditorGUILayout.HelpBox("READY — baked identity is present.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("READY TO BAKE — run Server World Bake V2.", MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
