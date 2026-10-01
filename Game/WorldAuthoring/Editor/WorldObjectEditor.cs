using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// WorldObject identity is bake output, not normal author input. Present it as status rather
    /// than an empty editable Definition Id field that implies setup is required.
    /// </summary>
    [CustomEditor(typeof(WorldObject))]
    public sealed class WorldObjectEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            WorldObject worldObject = (WorldObject)target;

            if (worldObject.StableId <= 0)
            {
                EditorGUILayout.HelpBox(
                    "No manual setup required. Server World Bake V2 assigns this WorldObject's identity " +
                    "from the canonical WorldInteractable data.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "Baked world identity. Read-only; rebake after changing the authored WorldInteractable.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LongField("Stable ID", worldObject.StableId);
                EditorGUILayout.TextField("Definition ID", worldObject.DefinitionId);
            }
        }
    }
}
