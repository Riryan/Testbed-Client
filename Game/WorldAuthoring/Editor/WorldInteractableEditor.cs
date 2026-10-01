using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Keeps production WorldInteractable authoring unchanged, but gives the generated Harvest
    /// Actor Dummy a test-fixture view instead of exposing every generic world-object option first.
    /// The canonical WorldInteractable remains the only backing data.
    /// </summary>
    [CustomEditor(typeof(WorldInteractable))]
    public sealed class WorldInteractableEditor : UnityEditor.Editor
    {
        private const string HarvestActorDummyDefinitionId = "harvest_actor_dummy";
        private bool _showAdvanced;

        public override void OnInspectorGUI()
        {
            WorldInteractable interactable = (WorldInteractable)target;
            if (!IsHarvestActorDummy(interactable))
            {
                DrawDefaultInspector();
                return;
            }

            serializedObject.Update();

            EditorGUILayout.LabelField("Harvest Actor Dummy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This is a generated test fixture. Normal setup is: position it, save the scene, " +
                "run Server World Bake V2, then test it. The generic WorldInteractable fields are " +
                "still canonical data, but they are hidden below unless you intentionally need them.",
                MessageType.Info);

            SerializedProperty label = serializedObject.FindProperty("label");
            SerializedProperty enabledByDefault = serializedObject.FindProperty("enabledByDefault");
            SerializedProperty interactions = serializedObject.FindProperty("interactions");

            EditorGUILayout.PropertyField(label, new GUIContent("Label"));
            EditorGUILayout.PropertyField(enabledByDefault, new GUIContent("Enabled"));

            if (interactions != null && interactions.isArray && interactions.arraySize > 0)
            {
                SerializedProperty first = interactions.GetArrayElementAtIndex(0);
                SerializedProperty duration = first.FindPropertyRelative("fixedDurationSeconds");
                if (duration != null)
                    EditorGUILayout.PropertyField(duration, new GUIContent("Test Duration (Seconds)"));
            }

            EditorGUILayout.Space(5f);
            DrawFixtureStatus(interactable);

            EditorGUILayout.Space(5f);
            if (GUILayout.Button("Restore Harvest Dummy Defaults"))
            {
                serializedObject.ApplyModifiedProperties();
                Undo.RecordObject(interactable, "Restore Harvest Actor Dummy Defaults");
                WorldInteractableBuilder.ConfigureHarvestActorDummyDefaults(
                    interactable,
                    string.IsNullOrWhiteSpace(interactable.label) ? "Harvest Actor Dummy" : interactable.label,
                    "recovery_salvage");
                EditorUtility.SetDirty(interactable);
                serializedObject.Update();
            }

            EditorGUILayout.Space(6f);
            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                "Advanced Canonical Data",
                true);

            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "These are the normal production WorldInteractable fields. The Harvest Actor Dummy preset " +
                    "does not require manual edits here for its baseline test configuration.",
                    MessageType.None);

                DrawPropertiesExcluding(
                    serializedObject,
                    "m_Script",
                    "label",
                    "enabledByDefault");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static bool IsHarvestActorDummy(WorldInteractable interactable)
        {
            return interactable != null &&
                   string.Equals(
                       interactable.definitionId,
                       HarvestActorDummyDefinitionId,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        private static void DrawFixtureStatus(WorldInteractable interactable)
        {
            WorldObject worldObject = interactable.GetComponent<WorldObject>();
            if (worldObject == null)
            {
                EditorGUILayout.HelpBox(
                    "WorldObject is missing. Use Restore Harvest Dummy Defaults or recreate the fixture.",
                    MessageType.Error);
            }
            else if (worldObject.StableId > 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LongField("Baked Stable ID", worldObject.StableId);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "World identity has not been baked yet. This is expected before Server World Bake V2; " +
                    "you do not enter a Stable ID or WorldObject Definition ID manually.",
                    MessageType.None);
            }

            Animator[] animators = interactable.GetComponentsInChildren<Animator>(true);
            if (animators == null || animators.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Animator was found under the dummy visual. Recreate the fixture if the Player visual failed to instantiate.",
                    MessageType.Warning);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.IntField("Visual Animators Found", animators.Length);
                }
            }
        }
    }
}
