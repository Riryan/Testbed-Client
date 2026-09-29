using Game.Shared.Characters;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Character Select convenience wrapper over the local binary visual cache.
    /// Legacy PlayerPrefs JSON is migrated on first successful read.
    /// </summary>
    public static class CharacterSelectAppearanceCache
    {
        private const string LegacyKeyPrefix = "MMO.CharacterSelect.Appearance.v1.";

        public static bool TryLoad(long characterId, out CharacterAppearanceRecipe appearance)
        {
            appearance = null;
            if (characterId <= 0)
                return false;

            string key = LegacyKeyPrefix + characterId;
            string json = PlayerPrefs.GetString(key, string.Empty);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    CharacterAppearanceRecipe legacy = JsonUtility.FromJson<CharacterAppearanceRecipe>(json);
                    if (legacy != null && legacy.IsValid(out _))
                    {
                        CharacterVisualLocalCache.SaveAppearance(characterId, legacy);
                        PlayerPrefs.DeleteKey(key);
                        appearance = legacy.Clone();
                        return true;
                    }
                }
                catch
                {
                    // Fall through to the binary cache.
                }
            }

            return CharacterVisualLocalCache.TryLoadAppearance(characterId, out appearance);
        }

        public static void Save(long characterId, CharacterAppearanceRecipe appearance) =>
            CharacterVisualLocalCache.SaveAppearance(characterId, appearance);

        public static void Delete(long characterId)
        {
            CharacterVisualLocalCache.Delete(characterId);
            if (characterId > 0)
                PlayerPrefs.DeleteKey(LegacyKeyPrefix + characterId);
        }
    }
}
