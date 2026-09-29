using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Game.Shared.Content;
using UnityEngine;

namespace Game.Client.Content
{
    /// <summary>
    /// Static client-owned presentation catalog for recovered gameplay. This deliberately
    /// reads only UI-facing authoring fields, so opening UI never asks the server for recipes,
    /// track names, faction names, or jurisdiction names.
    /// </summary>
    public static class ClientRecoveryContentCache
    {
        private static readonly Dictionary<ushort, RecipeDefinition> RecipesById = new Dictionary<ushort, RecipeDefinition>();
        private static readonly Dictionary<ushort, ProgressTrackDefinition> TracksById = new Dictionary<ushort, ProgressTrackDefinition>();
        private static readonly Dictionary<ushort, FactionDefinition> FactionsById = new Dictionary<ushort, FactionDefinition>();
        private static readonly Dictionary<ushort, JurisdictionDefinition> JurisdictionsById = new Dictionary<ushort, JurisdictionDefinition>();
        private static CraftingStationDefinition[] _stations = Array.Empty<CraftingStationDefinition>();
        private static bool _loaded;
        private static string _loadError = string.Empty;

        public static bool Loaded => _loaded;
        public static string LoadError => _loadError;
        public static CraftingStationDefinition[] Stations => _stations;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadBeforeScene()
        {
#if !UNITY_SERVER
            TryLoad();
#endif
        }

        public static bool TryLoad()
        {
            RecipesById.Clear();
            TracksById.Clear();
            FactionsById.Clear();
            JurisdictionsById.Clear();
            _stations = Array.Empty<CraftingStationDefinition>();
            _loaded = false;
            _loadError = string.Empty;

            try
            {
                string root = Path.Combine(Application.streamingAssetsPath, "MMOContent");
                CraftingCatalogDocument crafting = ReadOptional<CraftingCatalogDocument>(Path.Combine(root, "Crafting", "Recipes.json"));
                CraftingCatalogDocument recoveryCrafting = ReadOptional<CraftingCatalogDocument>(Path.Combine(root, "Crafting", "RecoveryRecipes.json"));
                ProgressCatalogDocument progress = ReadOptional<ProgressCatalogDocument>(Path.Combine(root, "Skills", "Skills.json"));
                ProgressCatalogDocument recoveryProgress = ReadOptional<ProgressCatalogDocument>(Path.Combine(root, "Skills", "RecoverySkills.json"));
                FactionCatalogDocument factions = ReadOptional<FactionCatalogDocument>(Path.Combine(root, "Factions", "Factions.json"));
                FactionCatalogDocument recoveryFactions = ReadOptional<FactionCatalogDocument>(Path.Combine(root, "Factions", "RecoveryFactions.json"));

                if (crafting == null && recoveryCrafting == null)
                    throw new FileNotFoundException("No client Crafting catalog was found under StreamingAssets/MMOContent/Crafting.");
                if (progress == null && recoveryProgress == null)
                    throw new FileNotFoundException("No client Skills catalog was found under StreamingAssets/MMOContent/Skills.");

                var recipes = ConvertRecipes(Concat(crafting?.recipes, recoveryCrafting?.recipes));
                var tracks = ConvertTracks(Concat(progress?.skills, recoveryProgress?.skills));
                var factionDefs = ConvertFactions(Concat(factions?.factions, recoveryFactions?.factions));
                var jurisdictionDefs = ConvertJurisdictions(Concat(factions?.jurisdictions, recoveryFactions?.jurisdictions));
                var snapshot = new GameplayContentSnapshot
                {
                    progressTracks = tracks,
                    recipes = recipes,
                    craftingStations = Concat(crafting?.stations, recoveryCrafting?.stations),
                    factions = factionDefs,
                    jurisdictions = jurisdictionDefs,
                };

                // Top-level IDs use the same deterministic routine as the server. Client
                // presentation never resolves server-only reward/item/predicate references.
                RecoveryContentDataIds.EnsureAssigned(snapshot);

                Index(snapshot.recipes, x => x.dataId, RecipesById);
                Index(snapshot.progressTracks, x => x.dataId, TracksById);
                Index(snapshot.factions, x => x.dataId, FactionsById);
                Index(snapshot.jurisdictions, x => x.dataId, JurisdictionsById);
                _stations = snapshot.craftingStations ?? Array.Empty<CraftingStationDefinition>();
                _loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                Debug.LogWarning($"[RecoveryContent] Client static recovery catalogs were not loaded: {_loadError}");
                return false;
            }
        }

        public static bool TryGetRecipe(ushort dataId, out RecipeDefinition definition) => RecipesById.TryGetValue(dataId, out definition);
        public static bool TryGetTrack(ushort dataId, out ProgressTrackDefinition definition) => TracksById.TryGetValue(dataId, out definition);

        public static ProgressTrackDefinition[] GetProgressTracks()
        {
            if (!_loaded)
                TryLoad();

            var result = new List<ProgressTrackDefinition>(TracksById.Count);
            foreach (ProgressTrackDefinition track in TracksById.Values)
                if (track != null)
                    result.Add(track);

            result.Sort((a, b) =>
            {
                int kind = a.kind.CompareTo(b.kind);
                if (kind != 0) return kind;
                return string.Compare(
                    a.displayName ?? a.definitionId,
                    b.displayName ?? b.definitionId,
                    StringComparison.OrdinalIgnoreCase);
            });
            return result.ToArray();
        }

        public static bool TryGetFaction(ushort dataId, out FactionDefinition definition) => FactionsById.TryGetValue(dataId, out definition);
        public static bool TryGetJurisdiction(ushort dataId, out JurisdictionDefinition definition) => JurisdictionsById.TryGetValue(dataId, out definition);

        public static RecipeDefinition[] GetKnownRecipes(ushort[] knownRecipeDataIds)
        {
            ushort[] ids = knownRecipeDataIds ?? Array.Empty<ushort>();
            var result = new List<RecipeDefinition>(ids.Length);
            for (int i = 0; i < ids.Length; ++i)
                if (RecipesById.TryGetValue(ids[i], out RecipeDefinition recipe) && recipe != null)
                    result.Add(recipe);
            result.Sort((a, b) => string.Compare(a.displayName ?? a.definitionId, b.displayName ?? b.definitionId, StringComparison.OrdinalIgnoreCase));
            return result.ToArray();
        }

        private static RecipeDefinition[] ConvertRecipes(RecipeClientDefinition[] source)
        {
            source = source ?? Array.Empty<RecipeClientDefinition>();
            var result = new RecipeDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                RecipeClientDefinition x = source[i] ?? new RecipeClientDefinition();
                result[i] = new RecipeDefinition
                {
                    dataId = x.dataId,
                    definitionId = x.definitionId ?? string.Empty,
                    displayName = x.displayName ?? string.Empty,
                    ingredients = x.ingredients ?? Array.Empty<RecipeIngredientDefinition>(),
                    stationTag = x.stationTag ?? string.Empty,
                    toolTag = x.toolTag ?? string.Empty,
                    learnByDefault = x.learnByDefault,
                };
            }
            return result;
        }

        private static ProgressTrackDefinition[] ConvertTracks(ProgressTrackClientDefinition[] source)
        {
            source = source ?? Array.Empty<ProgressTrackClientDefinition>();
            var result = new ProgressTrackDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                ProgressTrackClientDefinition x = source[i] ?? new ProgressTrackClientDefinition();
                result[i] = new ProgressTrackDefinition
                {
                    dataId = x.dataId,
                    definitionId = x.definitionId ?? string.Empty,
                    displayName = x.displayName ?? string.Empty,
                    kind = ParseTrackKind(x.kind),
                    maximumValue = x.maximumValue > 0 ? x.maximumValue : 100,
                };
            }
            return result;
        }

        private static FactionDefinition[] ConvertFactions(FactionClientDefinition[] source)
        {
            source = source ?? Array.Empty<FactionClientDefinition>();
            var result = new FactionDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                FactionClientDefinition x = source[i] ?? new FactionClientDefinition();
                result[i] = new FactionDefinition { dataId = x.dataId, definitionId = x.definitionId ?? string.Empty, displayName = x.displayName ?? string.Empty };
            }
            return result;
        }

        private static JurisdictionDefinition[] ConvertJurisdictions(JurisdictionClientDefinition[] source)
        {
            source = source ?? Array.Empty<JurisdictionClientDefinition>();
            var result = new JurisdictionDefinition[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                JurisdictionClientDefinition x = source[i] ?? new JurisdictionClientDefinition();
                result[i] = new JurisdictionDefinition { dataId = x.dataId, definitionId = x.definitionId ?? string.Empty, displayName = x.displayName ?? string.Empty };
            }
            return result;
        }

        private static ProgressTrackKind ParseTrackKind(object value)
        {
            if (value is string text && Enum.TryParse(text, true, out ProgressTrackKind parsed)) return parsed;
            try
            {
                int numeric = Convert.ToInt32(value);
                if (Enum.IsDefined(typeof(ProgressTrackKind), numeric)) return (ProgressTrackKind)numeric;
            }
            catch { }
            return ProgressTrackKind.General;
        }

        private static T ReadRequired<T>(string path) where T : class
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"Required client recovery catalog was not found: {path}", path);
            T value = Read<T>(path);
            if (value == null) throw new InvalidOperationException($"Client recovery catalog '{path}' was empty or invalid.");
            return value;
        }

        private static T ReadOptional<T>(string path) where T : class => File.Exists(path) ? Read<T>(path) : null;

        private static T Read<T>(string path) where T : class
        {
            using (FileStream stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                return serializer.ReadObject(stream) as T;
            }
        }

        private static T[] Concat<T>(T[] first, T[] second)
        {
            first = first ?? Array.Empty<T>();
            second = second ?? Array.Empty<T>();
            if (first.Length == 0) return second;
            if (second.Length == 0) return first;
            var result = new T[first.Length + second.Length];
            Array.Copy(first, 0, result, 0, first.Length);
            Array.Copy(second, 0, result, first.Length, second.Length);
            return result;
        }

        private static void Index<T>(T[] source, Func<T, ushort> id, Dictionary<ushort, T> destination) where T : class
        {
            source = source ?? Array.Empty<T>();
            for (int i = 0; i < source.Length; ++i)
            {
                T value = source[i];
                if (value == null) continue;
                ushort dataId = id(value);
                if (dataId != 0) destination[dataId] = value;
            }
        }

        [DataContract]
        private sealed class CraftingCatalogDocument
        {
            [DataMember(Name = "recipes")] public RecipeClientDefinition[] recipes = Array.Empty<RecipeClientDefinition>();
            [DataMember(Name = "stations")] public CraftingStationDefinition[] stations = Array.Empty<CraftingStationDefinition>();
        }

        [DataContract]
        private sealed class RecipeClientDefinition
        {
            [DataMember(Name = "dataId")] public ushort dataId;
            [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
            [DataMember(Name = "displayName")] public string displayName = string.Empty;
            [DataMember(Name = "ingredients")] public RecipeIngredientDefinition[] ingredients = Array.Empty<RecipeIngredientDefinition>();
            [DataMember(Name = "stationTag")] public string stationTag = string.Empty;
            [DataMember(Name = "toolTag")] public string toolTag = string.Empty;
            [DataMember(Name = "learnByDefault")] public bool learnByDefault;
        }

        [DataContract]
        private sealed class ProgressCatalogDocument
        {
            [DataMember(Name = "skills")] public ProgressTrackClientDefinition[] skills = Array.Empty<ProgressTrackClientDefinition>();
        }

        [DataContract]
        private sealed class ProgressTrackClientDefinition
        {
            [DataMember(Name = "dataId")] public ushort dataId;
            [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
            [DataMember(Name = "displayName")] public string displayName = string.Empty;
            [DataMember(Name = "kind")] public object kind;
            [DataMember(Name = "maximumValue")] public int maximumValue = 100;
        }

        [DataContract]
        private sealed class FactionCatalogDocument
        {
            [DataMember(Name = "factions")] public FactionClientDefinition[] factions = Array.Empty<FactionClientDefinition>();
            [DataMember(Name = "jurisdictions")] public JurisdictionClientDefinition[] jurisdictions = Array.Empty<JurisdictionClientDefinition>();
        }

        [DataContract]
        private sealed class FactionClientDefinition
        {
            [DataMember(Name = "dataId")] public ushort dataId;
            [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
            [DataMember(Name = "displayName")] public string displayName = string.Empty;
        }

        [DataContract]
        private sealed class JurisdictionClientDefinition
        {
            [DataMember(Name = "dataId")] public ushort dataId;
            [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
            [DataMember(Name = "displayName")] public string displayName = string.Empty;
        }
    }
}
