#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.OutfitAuthoring;
using Game.Client.Presentation.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only persistence bridge for the Play Mode Outfit Builder. It writes the existing
    /// CharacterWearableSetDefinition array on CharacterVisualProfile; it adds no runtime or wire format.
    /// </summary>
    [InitializeOnLoad]
    public static class OutfitAuthoringSaveBridge
    {
        private const string ScenePath = "Assets/Game/Client/OutfitAuthoring/Scenes/OutfitAuthoring.unity";

        static OutfitAuthoringSaveBridge()
        {
            OutfitAuthoringController.EditorSaveHandler = Save;
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open Play Mode Outfit Builder")]
        private static void OpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open And Play")]
        private static void OpenAndPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static OutfitAuthoringSaveResult Save(OutfitAuthoringSaveRequest request)
        {
            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
                return new OutfitAuthoringSaveResult(false, "CharacterVisualProfile.asset was not found. Rebuild the current visual catalog first.");

            request = request ?? new OutfitAuthoringSaveRequest();
            CharacterWearablePartSelection[] parts = request.parts ?? Array.Empty<CharacterWearablePartSelection>();
            ushort[] colors = (request.allowedColorIds ?? Array.Empty<ushort>())
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            int number = NextOutfitNumber(profile.WearableSets);
            string definitionId = $"outfit.new.{number:000}";
            string displayName = $"Outfit {number:000}";

            ushort primary = colors.Contains((ushort)1)
                ? (ushort)1
                : colors.Length > 0 ? colors[0] : (ushort)1;
            ushort secondary = colors.Contains((ushort)8)
                ? (ushort)8
                : colors.Length > 1 ? colors[1] : primary;

            var saved = new CharacterWearableSetDefinition
            {
                definitionId = definitionId,
                displayName = displayName,
                presentationId = 0,
                // Legacy metadata field. This authoring surface intentionally stores the full
                // multi-region outfit in one existing wearable definition without inventing a
                // second format. Current runtime consumers do not resolve WearableSets by region.
                region = CharacterWearableRegion.Upper,
                playerEligible = true,
                populationEligible = true,
                defaultPrimaryColorId = primary,
                defaultSecondaryColorId = secondary,
                colorUsageReviewed = true,
                playerAllowedColorIds = (ushort[])colors.Clone(),
                populationAllowedColorIds = (ushort[])colors.Clone(),
                parts = (CharacterWearablePartSelection[])parts.Clone(),
                icon = null,
            };

            var values = new List<CharacterWearableSetDefinition>(profile.WearableSets);
            values.Add(saved);

            Undo.RecordObject(profile, "Save Play Mode Outfit");
            profile.SetWearableSets(values.ToArray());
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();

            return new OutfitAuthoringSaveResult(
                true,
                $"Saved {displayName} as {definitionId} ({parts.Length} mesh override(s), {colors.Length} color(s)).");
        }

        private static int NextOutfitNumber(IReadOnlyList<CharacterWearableSetDefinition> values)
        {
            var used = new HashSet<int>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; ++i)
                {
                    string id = values[i]?.definitionId ?? string.Empty;
                    if (!id.StartsWith("outfit.new.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (int.TryParse(id.Substring("outfit.new.".Length), out int number) && number > 0)
                        used.Add(number);
                }
            }

            for (int i = 1; i < 100000; ++i)
                if (!used.Contains(i))
                    return i;
            return 99999;
        }
    }
}
#endif
