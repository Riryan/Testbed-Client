#if UNITY_EDITOR
using UnityEditor;

namespace Game.Client.Editor
{
    /// <summary>
    /// Compatibility wrapper for the retired V2 overlay installer.
    /// Routes the old menu commands into the current integrated workflow.
    /// </summary>
    public static class OutfitAuthoringIntegritySceneInstaller
    {
        [MenuItem("MMO Tools/Characters/Outfit Builder/Install or Repair Integrity Overlay")]
        public static void InstallOrRepair()
        {
            OutfitAuthoringIntegratedSceneInstaller.InstallOrRepair();
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Validate Integrity Overlay")]
        public static void Validate()
        {
            OutfitAuthoringIntegratedSceneInstaller.Validate();
        }
    }
}
#endif
