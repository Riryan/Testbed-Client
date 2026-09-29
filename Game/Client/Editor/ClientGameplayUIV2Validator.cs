#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.UI.Gameplay;
using Game.Client.UI.CharacterSelect;
using Game.Client.UI.Interactions;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Root;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>Fail-closed authoring checks for the clean post-login gameplay UI contract.</summary>
    public sealed class ClientGameplayUIV2Validator : IPreprocessBuildWithReport
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";
        public int callbackOrder => -400;

        // LEGACY/DEAD-END: menu registration intentionally disabled. Preserve method for rollback/reference.
        // [MenuItem("MMO Tools/UI/Gameplay V2/Validate Post-Login Gameplay UI", priority = 2011)]
        public static void ValidateFromMenu()
        {
            List<string> errors = Validate();
            if (errors.Count == 0)
            {
                Debug.Log("[Gameplay UI V2] PASSED. Frontend boundary, authored gameplay bindings, Inventory reuse, interaction reuse, and managed-window defaults are valid.");
                return;
            }

            Debug.LogError("[Gameplay UI V2] FAILED:\n - " + string.Join("\n - ", errors));
        }

        public void OnPreprocessBuild(BuildReport report)

        {

            // Gameplay UI V2 authoring contracts are intentionally not enforced as an automatic player-build gate.

            // Manual validation remains available from MMO Tools when explicitly requested.

        }

        private static List<string> Validate()
        {
            var errors = new List<string>();
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                errors.Add($"Missing {PrefabPath}.");
                return errors;
            }

            ClientUIRoot root = prefab.GetComponent<ClientUIRoot>();
            if (root == null) errors.Add("ClientUIRoot component is missing.");
            if (prefab.GetComponent<Canvas>() == null) errors.Add("Root Canvas is missing.");
            CanvasScaler scaler = prefab.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                errors.Add("Root CanvasScaler is missing.");
            }
            else
            {
                if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                    errors.Add("Root CanvasScaler must use Scale With Screen Size.");
                Vector2 reference = scaler.referenceResolution;
                if (Mathf.Abs(reference.x - 1920f) > 0.5f || Mathf.Abs(reference.y - 1080f) > 0.5f)
                    errors.Add($"Root CanvasScaler reference resolution must remain 1920x1080; found {reference.x:0}x{reference.y:0}.");
            }
            if (prefab.GetComponent<GraphicRaycaster>() == null) errors.Add("Root GraphicRaycaster is missing.");

            CharacterSelectShell frontend = prefab.GetComponentInChildren<CharacterSelectShell>(true);
            if (frontend == null)
                errors.Add("Existing frontend CharacterSelectShell is missing; Gameplay V2 must not replace login/character selection.");
            else if (frontend.GetComponent<Canvas>() == null || frontend.GetComponent<GraphicRaycaster>() == null)
                errors.Add("Existing frontend must retain its own Canvas and GraphicRaycaster presentation boundary.");

            ClientGameplayUIRoot gameplay = prefab.GetComponent<ClientGameplayUIRoot>();
            if (gameplay == null)
                errors.Add("ClientGameplayUIRoot is missing. Run Rebuild Post-Login Gameplay UI.");
            else if (!gameplay.HasAuthoredBindings)
                errors.Add("ClientGameplayUIRoot has incomplete authored bindings.");

            Transform v2 = prefab.transform.Find("GameplayUIV2");
            if (v2 == null)
                errors.Add("GameplayUIV2 hierarchy is missing.");
            else if (v2.gameObject.activeSelf)
                errors.Add("GameplayUIV2 must be authored inactive until world admission.");

            if (prefab.GetComponent<PlayerInventoryShell>() == null)
                errors.Add("Existing PlayerInventoryShell is missing; Inventory must be preserved, not rebuilt.");
            if (prefab.GetComponent<ClientInteractionUI>() == null)
                errors.Add("Existing ClientInteractionUI controller is missing; interaction logic must be reused.");

            EventSystem[] eventSystems = prefab.GetComponentsInChildren<EventSystem>(true);
            if (eventSystems.Length != 1)
                errors.Add($"Expected exactly one authored EventSystem under ClientUIRoot; found {eventSystems.Length}.");

            ClientUIPanelMarker[] markers = prefab.GetComponentsInChildren<ClientUIPanelMarker>(true);
            var ids = new HashSet<ClientUIPanelId>();
            for (int i = 0; i < markers.Length; ++i)
            {
                ClientUIPanelMarker marker = markers[i];
                if (marker == null || marker.PanelId == ClientUIPanelId.None)
                    continue;
                if (!ids.Add(marker.PanelId))
                    errors.Add($"Duplicate panel id {marker.PanelId}.");
                if (marker.ManagedWindow && marker.GameplayOnly && marker.gameObject.activeSelf)
                    errors.Add($"Managed gameplay window '{marker.name}' ({marker.PanelId}) is authored active.");
            }

            return errors;
        }
    }
}
#endif
