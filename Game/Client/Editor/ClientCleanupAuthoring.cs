#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Single Editor entry point for the client cleanup patch. It deliberately composes the
    /// existing authoring/validation systems instead of introducing another runtime UI builder.
    /// </summary>
    public static class ClientCleanupAuthoring
    {
        [MenuItem("MMO Tools/UI/Apply Client Cleanup + Validate")]
        public static void ApplyAndValidate()
        {
            ClientUIFullScaffoldBuilder.BuildOrRepairFullMasterUi();
            CharacterCreatorAuthoredUiRepair.InstallOrRepair();
            ClientSocialEconomyUIAuthoring.Validate();
            ClientUIFullScaffoldBuilder.ValidateFullMasterUi();
            Debug.Log("[ClientUI] Client cleanup authoring + validation completed. Existing UI prefab, canonical Social binder, and Character Creator surface are current.");
        }
    }
}
#endif
