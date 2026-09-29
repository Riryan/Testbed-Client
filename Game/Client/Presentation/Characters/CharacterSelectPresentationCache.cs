using Game.Shared.Characters;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Character Select convenience wrapper for movement/presentation preferences.
    /// The cache is local-only and never overrides authoritative server revisions.
    /// </summary>
    public static class CharacterSelectPresentationCache
    {
        private const string LegacyKeyPrefix = "MMO.CharacterSelect.Presentation.v1.";

        public static bool TryLoad(long characterId, out CharacterPresentationPreferences preferences)
        {
            preferences = null;
            if (characterId <= 0)
                return false;

            string key = LegacyKeyPrefix + characterId;
            string json = PlayerPrefs.GetString(key, string.Empty);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    CharacterPresentationPreferences legacy =
                        JsonUtility.FromJson<CharacterPresentationPreferences>(json);
                    if (legacy != null && legacy.IsValid(out _))
                    {
                        CharacterVisualLocalCache.SavePresentation(characterId, legacy);
                        PlayerPrefs.DeleteKey(key);
                        preferences = legacy.Clone();
                        return true;
                    }
                }
                catch
                {
                    // Fall through to the binary cache.
                }
            }

            return CharacterVisualLocalCache.TryLoadPresentation(characterId, out preferences);
        }

        public static void Save(long characterId, CharacterPresentationPreferences preferences) =>
            CharacterVisualLocalCache.SavePresentation(characterId, preferences);
    }
}
