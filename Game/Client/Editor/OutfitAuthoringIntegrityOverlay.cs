#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Game.Client.Editor
{
    /// <summary>
    /// Compatibility shim for the retired standalone integrity EditorWindow.
    /// The integrity workflow now lives inside OutfitAuthoring.unity.
    /// </summary>
    public static class OutfitAuthoringIntegrityOverlay
    {
        private const string ScenePath =
            "Assets/Game/Client/Editor/OutfitAuthoring/OutfitAuthoring.unity";

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open Integrity Overlay")]
        public static void Open()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Selection.activeObject = null;
        }
    }
}
#endif
