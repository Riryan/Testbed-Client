using Game.Shared.Interactions;
using Game.Shared.World;
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

            DrawProductionInteractable(interactable);
        }

        private void DrawInteractionTestDummy(WorldInteractable interactable)
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Interaction Test Dummy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Setup: choose a name, test group, and interaction, then run Server World Bake V2. " +
                "This fixture NEVER warps or snaps the Player. After server acceptance, the Player uses normal locomotion to auto-walk to final spacing; the dummy stays planted.",
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

            if (!InteractionTestDummyTool.IsNoWarpConfigured(interactable))
            {
                EditorGUILayout.HelpBox(
                    "LEGACY WARP SETUP DETECTED — this fixture is still authored as a timed/slot interaction.",
                    MessageType.Error);

                if (GUILayout.Button("Remove Warp / Upgrade Test Dummy"))
                {
                    serializedObject.ApplyModifiedProperties();
                    InteractionTestDummyTool.Repair(interactable);
                    serializedObject.Update();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "NO WARP — range is validated normally; accepted interactions auto-walk the Player to final spacing through the existing movement path. Dummy position never changes.",
                    MessageType.None);
            }

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
                !InteractionTestDummyTool.IsNoWarpConfigured(interactable))
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

        private bool _showAdvancedAuthoring;

        private void DrawProductionInteractable(
            WorldInteractable interactable)
        {
            serializedObject.Update();

            EditorGUILayout.LabelField(
                "World Interactable",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prefab workflow: put the model/collider plus WorldInteractable on the prefab. " +
                "Drop the prefab into the scene, choose the Type / Profile / Interaction dropdowns here, " +
                "then run Server World Bake V2. Advanced server-authoring fields stay available below.",
                MessageType.Info);

            SerializedProperty label =
                serializedObject.FindProperty("label");
            SerializedProperty kind =
                serializedObject.FindProperty("kind");
            SerializedProperty gameplayProfileId =
                serializedObject.FindProperty("gameplayProfileId");
            SerializedProperty lootTableId =
                serializedObject.FindProperty("lootTableId");
            SerializedProperty craftingStationId =
                serializedObject.FindProperty("craftingStationId");
            SerializedProperty factionDefinitionId =
                serializedObject.FindProperty("factionDefinitionId");
            SerializedProperty persistentState =
                serializedObject.FindProperty("persistentState");
            SerializedProperty enabledByDefault =
                serializedObject.FindProperty("enabledByDefault");
            SerializedProperty interactions =
                serializedObject.FindProperty("interactions");

            EditorGUILayout.PropertyField(
                label,
                new GUIContent("Name"));
            EditorGUILayout.PropertyField(
                kind,
                new GUIContent("Type"));
            EditorGUILayout.PropertyField(
                gameplayProfileId,
                new GUIContent(
                    "Gameplay Profile",
                    "Existing authoritative content/profile ID. Leave blank only when this interactable type does not use one."));

            ServerWorldInteractableKind selectedKind =
                (ServerWorldInteractableKind)kind.intValue;

            bool hasSearch =
                HasAction(
                    interactable,
                    InteractionActionId.Search);
            bool hasCraft =
                HasAction(
                    interactable,
                    InteractionActionId.Craft);
            bool hasRecruit =
                HasAction(
                    interactable,
                    InteractionActionId.Recruit);

            if (selectedKind ==
                    ServerWorldInteractableKind.Searchable ||
                hasSearch)
            {
                EditorGUILayout.PropertyField(
                    lootTableId,
                    new GUIContent("Loot Table"));
            }

            if (hasCraft)
            {
                EditorGUILayout.PropertyField(
                    craftingStationId,
                    new GUIContent("Crafting Station"));
            }

            if (hasRecruit)
            {
                EditorGUILayout.PropertyField(
                    factionDefinitionId,
                    new GUIContent("Faction"));
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "Interactions",
                EditorStyles.boldLabel);

            if (interactions != null)
            {
                for (int i = 0;
                     i < interactions.arraySize;
                     ++i)
                {
                    DrawSimpleInteraction(
                        interactable,
                        interactions,
                        i);
                }
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(
                    "Add Interaction",
                    GUILayout.Width(140f)))
            {
                serializedObject.ApplyModifiedProperties();
                AddInteraction(interactable);
                serializedObject.Update();
                interactions =
                    serializedObject.FindProperty(
                        "interactions");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "State",
                EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                enabledByDefault,
                new GUIContent("Enabled"));
            EditorGUILayout.PropertyField(
                persistentState,
                new GUIContent("Persistent"));

            DrawProductionStatus(interactable);

            _showAdvancedAuthoring =
                EditorGUILayout.Foldout(
                    _showAdvancedAuthoring,
                    "Advanced Authoring",
                    true);

            if (_showAdvancedAuthoring)
            {
                EditorGUILayout.HelpBox(
                    "Raw fields are kept here for systems that need slots, observation, blockers, " +
                    "or specialist IDs. Normal prefab placement should not require this section.",
                    MessageType.None);

                DrawPropertiesExcluding(
                    serializedObject,
                    "m_Script",
                    "label",
                    "kind",
                    "gameplayProfileId",
                    "lootTableId",
                    "craftingStationId",
                    "factionDefinitionId",
                    "persistentState",
                    "enabledByDefault",
                    "interactions");
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSimpleInteraction(
            WorldInteractable interactable,
            SerializedProperty interactions,
            int index)
        {
            if (interactions == null ||
                index < 0 ||
                index >= interactions.arraySize)
            {
                return;
            }

            SerializedProperty interaction =
                interactions.GetArrayElementAtIndex(index);

            SerializedProperty actionId =
                interaction.FindPropertyRelative("actionId");
            SerializedProperty displayLabel =
                interaction.FindPropertyRelative("displayLabel");
            SerializedProperty maximumUseDistance =
                interaction.FindPropertyRelative("maximumUseDistance");
            SerializedProperty fixedDurationSeconds =
                interaction.FindPropertyRelative("fixedDurationSeconds");
            SerializedProperty exclusiveOccupancy =
                interaction.FindPropertyRelative("exclusiveOccupancy");
            SerializedProperty feature =
                interaction.FindPropertyRelative("feature");
            SerializedProperty contentLevel =
                interaction.FindPropertyRelative("contentLevel");

            EditorGUILayout.BeginVertical(
                EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                interactions.arraySize == 1
                    ? "Interaction"
                    : $"Interaction {index + 1}",
                EditorStyles.boldLabel);

            if (GUILayout.Button(
                    "Remove",
                    GUILayout.Width(70f)))
            {
                serializedObject.ApplyModifiedProperties();
                RemoveInteraction(
                    interactable,
                    index);
                serializedObject.Update();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                GUIUtility.ExitGUI();
                return;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                actionId,
                new GUIContent("Action"));

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                NormalizeInteractionForAction(
                    interactable,
                    index);
                serializedObject.Update();

                interactions =
                    serializedObject.FindProperty(
                        "interactions");
                interaction =
                    interactions.GetArrayElementAtIndex(
                        index);
                displayLabel =
                    interaction.FindPropertyRelative(
                        "displayLabel");
                maximumUseDistance =
                    interaction.FindPropertyRelative(
                        "maximumUseDistance");
                fixedDurationSeconds =
                    interaction.FindPropertyRelative(
                        "fixedDurationSeconds");
                exclusiveOccupancy =
                    interaction.FindPropertyRelative(
                        "exclusiveOccupancy");
                feature =
                    interaction.FindPropertyRelative(
                        "feature");
                contentLevel =
                    interaction.FindPropertyRelative(
                        "contentLevel");
            }

            EditorGUILayout.PropertyField(
                displayLabel,
                new GUIContent("Menu Label"));
            EditorGUILayout.PropertyField(
                feature,
                new GUIContent("Feature"));
            EditorGUILayout.PropertyField(
                maximumUseDistance,
                new GUIContent("Use Range"));
            EditorGUILayout.PropertyField(
                fixedDurationSeconds,
                new GUIContent(
                    "Duration",
                    "0 = instant authoritative action. Timed interactions use the existing interaction session path."));
            EditorGUILayout.PropertyField(
                exclusiveOccupancy,
                new GUIContent("Exclusive Use"));
            EditorGUILayout.PropertyField(
                contentLevel,
                new GUIContent("Content Level"));

            EditorGUILayout.EndVertical();
        }

        private static void NormalizeInteractionForAction(
            WorldInteractable interactable,
            int index)
        {
            if (interactable == null ||
                interactable.interactions == null ||
                index < 0 ||
                index >= interactable.interactions.Length)
            {
                return;
            }

            WorldInteractable.Interaction interaction =
                interactable.interactions[index];
            if (interaction == null)
                return;

            Undo.RecordObject(
                interactable,
                "Change World Interaction");

            InteractionActionId actionId =
                interaction.actionId;

            if (actionId == InteractionActionId.None)
                return;

            interaction.categoryId =
                InteractionCategoryCatalog.ForTargetAction(
                    InteractionTargetKind.SceneObject,
                    actionId);
            interaction.definitionId =
                actionId.ToString().ToLowerInvariant();

            // Keep the simple dropdown path aligned with the canonical bake rules.
            // These are unambiguous scene-object action/type relationships.
            if (actionId == InteractionActionId.Search)
            {
                interactable.kind =
                    ServerWorldInteractableKind.Searchable;
                interaction.feature =
                    InteractionFeature.WorldObjects;
            }
            else if (actionId == InteractionActionId.Harvest)
            {
                interactable.kind =
                    ServerWorldInteractableKind.HarvestNode;
                interaction.feature =
                    InteractionFeature.Harvesting;
                interaction.exclusiveOccupancy = true;
            }
            else if (actionId == InteractionActionId.Craft)
            {
                interaction.feature =
                    InteractionFeature.WorldObjects;
            }

            if (string.IsNullOrWhiteSpace(
                    interaction.displayLabel) ||
                string.Equals(
                    interaction.displayLabel,
                    "Use",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                interaction.displayLabel =
                    InteractionTestDummyTool.ActionLabel(
                        actionId);
            }

            EditorUtility.SetDirty(interactable);
        }

        private static bool HasAction(
            WorldInteractable interactable,
            InteractionActionId actionId)
        {
            if (interactable == null ||
                interactable.interactions == null)
            {
                return false;
            }

            for (int i = 0;
                 i < interactable.interactions.Length;
                 ++i)
            {
                WorldInteractable.Interaction interaction =
                    interactable.interactions[i];

                if (interaction != null &&
                    interaction.actionId == actionId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddInteraction(
            WorldInteractable interactable)
        {
            if (interactable == null)
                return;

            Undo.RecordObject(
                interactable,
                "Add World Interaction");

            WorldInteractable.Interaction[] current =
                interactable.interactions ??
                System.Array.Empty<
                    WorldInteractable.Interaction>();

            var next =
                new WorldInteractable.Interaction[
                    current.Length + 1];

            System.Array.Copy(
                current,
                next,
                current.Length);

            next[next.Length - 1] =
                new WorldInteractable.Interaction
                {
                    definitionId = "use",
                    categoryId =
                        InteractionCategoryId.Use,
                    actionId =
                        InteractionActionId.Use,
                    displayLabel = "Use",
                    maximumUseDistance =
                        InteractionRangePolicy
                            .WorldObjectUseRange,
                    maximumFacingAngle = 180f,
                    cancelOnDamage = true,
                    cancelOnMovement = true,
                    cancelOnTargetUnavailable = true,
                    contentLevel =
                        InteractionContentLevel.General,
                    feature =
                        InteractionFeature.WorldObjects,
                };

            interactable.interactions = next;
            EditorUtility.SetDirty(interactable);
        }

        private static void RemoveInteraction(
            WorldInteractable interactable,
            int index)
        {
            if (interactable == null ||
                interactable.interactions == null ||
                index < 0 ||
                index >= interactable.interactions.Length)
            {
                return;
            }

            Undo.RecordObject(
                interactable,
                "Remove World Interaction");

            WorldInteractable.Interaction[] current =
                interactable.interactions;

            var next =
                new WorldInteractable.Interaction[
                    current.Length - 1];

            if (index > 0)
            {
                System.Array.Copy(
                    current,
                    0,
                    next,
                    0,
                    index);
            }

            if (index < current.Length - 1)
            {
                System.Array.Copy(
                    current,
                    index + 1,
                    next,
                    index,
                    current.Length - index - 1);
            }

            interactable.interactions = next;
            EditorUtility.SetDirty(interactable);
        }

        private static void DrawProductionStatus(
            WorldInteractable interactable)
        {
            EditorGUILayout.Space(6f);

            if (interactable == null)
                return;

            Collider collider =
                interactable.GetComponentInChildren<
                    Collider>(true);

            if (collider == null)
            {
                EditorGUILayout.HelpBox(
                    "No Collider was found on the prefab. Add an appropriately sized collider to " +
                    "the model/root so the existing interaction focus ray can acquire it.",
                    MessageType.Warning);
            }

            WorldObject worldObject =
                interactable.GetComponent<WorldObject>();

            if (worldObject == null ||
                worldObject.StableId <= 0)
            {
                EditorGUILayout.HelpBox(
                    "READY TO BAKE — save the scene, then run Server World Bake V2.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "READY — baked identity is present.",
                    MessageType.Info);
            }
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
