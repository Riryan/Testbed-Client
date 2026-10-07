using System;
using System.Collections.Generic;
using Game.Client.Content;
using Game.Shared.Content;
using Player.Networking;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Projects the already-authoritative progression cache into client milestone rows.
    /// No request path, polling loop, or gameplay authority is introduced.
    /// </summary>
    public static class AchievementMilestoneProjection
    {
        public readonly struct MilestoneRow
        {
            public readonly string Category;
            public readonly string Name;
            public readonly int Current;
            public readonly int Maximum;
            public readonly int NextThreshold;
            public readonly bool Complete;

            public MilestoneRow(
                string category,
                string name,
                int current,
                int maximum,
                int nextThreshold,
                bool complete)
            {
                Category = category ?? string.Empty;
                Name = name ?? string.Empty;
                Current = Math.Max(0, current);
                Maximum = Math.Max(1, maximum);
                NextThreshold = Math.Max(0, nextThreshold);
                Complete = complete;
            }
        }

        private static readonly int[] PercentMilestones = { 25, 50, 75, 100 };
        private static readonly int[] LevelMilestones = { 5, 10, 25, 50, 100 };
        private static readonly int[] CountMilestones = { 1, 5, 10, 25 };

        public static List<MilestoneRow> Build(ProgressionSnapshotMessage snapshot)
        {
            var rows = new List<MilestoneRow>(48);

            if (!snapshot.success)
                return rows;

            AddThresholdRow(
                rows,
                "Character",
                "Character Level",
                Math.Max(1, snapshot.level),
                LevelMilestones);

            AddThresholdRow(
                rows,
                "Knowledge",
                "Known Recipes",
                snapshot.knownRecipeDataIds?.Length ?? 0,
                CountMilestones);

            AddThresholdRow(
                rows,
                "Knowledge",
                "Known Abilities",
                snapshot.knownAbilityWireIds?.Length ?? 0,
                CountMilestones);

            var values = new Dictionary<ushort, int>();
            ProgressTrackWire[] tracks = snapshot.tracks ?? Array.Empty<ProgressTrackWire>();

            for (int i = 0; i < tracks.Length; ++i)
                values[tracks[i].dataId] = Math.Max(0, tracks[i].value);

            ProgressTrackDefinition[] definitions =
                ClientRecoveryContentCache.GetProgressTracks();

            for (int i = 0; i < definitions.Length; ++i)
            {
                ProgressTrackDefinition definition = definitions[i];

                if (definition == null ||
                    definition.dataId == 0 ||
                    !IsVisibleForFaction(definition, snapshot.factionDataId))
                    continue;

                int maximum = Math.Max(1, definition.maximumValue);
                int current =
                    values.TryGetValue(definition.dataId, out int value)
                        ? value
                        : 0;

                AddThresholdRow(
                    rows,
                    TrackCategory(definition.kind),
                    string.IsNullOrWhiteSpace(definition.displayName)
                        ? definition.definitionId
                        : definition.displayName,
                    current,
                    BuildPercentThresholds(maximum),
                    maximum);
            }

            rows.Sort(CompareRows);
            return rows;
        }

        /// <summary>
        /// Other-faction achievements are omitted completely.
        /// They are not shown locked, disabled, or greyed out.
        /// </summary>
        public static bool IsVisibleForFaction(
            ProgressTrackDefinition definition,
            ushort factionDataId)
        {
            if (definition == null)
                return false;

            UnlockPredicateDefinition[] predicates =
                definition.unlockPredicates ?? Array.Empty<UnlockPredicateDefinition>();

            bool hasFactionPredicate = false;

            for (int i = 0; i < predicates.Length; ++i)
            {
                UnlockPredicateDefinition predicate = predicates[i];

                if (predicate == null ||
                    predicate.kind != UnlockPredicateKind.Faction)
                    continue;

                hasFactionPredicate = true;

                if (predicate.dataId == 0 ||
                    factionDataId == 0 ||
                    predicate.dataId != factionDataId)
                    return false;
            }

            if (hasFactionPredicate)
                return true;

            // Fail closed if faction content was authored without its predicate.
            // Current recovery data already predicates Vampire Feeding, but Hunter
            // Investigation currently has no explicit faction predicate.
            string id = definition.definitionId ?? string.Empty;

            if (definition.kind == ProgressTrackKind.Hunter ||
                id.StartsWith("hunter.", StringComparison.OrdinalIgnoreCase))
                return IsFaction(factionDataId, "faction.hunter");

            if (definition.kind == ProgressTrackKind.Vampire ||
                id.StartsWith("vampire.", StringComparison.OrdinalIgnoreCase))
                return IsFaction(factionDataId, "faction.vampire");

            return true;
        }

        private static bool IsFaction(
            ushort factionDataId,
            string expectedDefinitionId)
        {
            if (factionDataId == 0 ||
                string.IsNullOrWhiteSpace(expectedDefinitionId) ||
                !ClientRecoveryContentCache.TryGetFaction(
                    factionDataId,
                    out FactionDefinition faction) ||
                faction == null)
                return false;

            return string.Equals(
                faction.definitionId,
                expectedDefinitionId,
                StringComparison.OrdinalIgnoreCase);
        }

        private static int[] BuildPercentThresholds(int maximum)
        {
            maximum = Math.Max(1, maximum);
            var result = new int[PercentMilestones.Length];

            for (int i = 0; i < PercentMilestones.Length; ++i)
            {
                int threshold =
                    (int)Math.Ceiling(
                        maximum * (PercentMilestones[i] / 100d));

                result[i] =
                    Math.Min(
                        maximum,
                        Math.Max(1, threshold));
            }

            result[result.Length - 1] = maximum;
            return result;
        }

        private static void AddThresholdRow(
            List<MilestoneRow> rows,
            string category,
            string name,
            int current,
            int[] thresholds,
            int explicitMaximum = 0)
        {
            thresholds = thresholds ?? Array.Empty<int>();

            if (thresholds.Length == 0)
                return;

            int next = thresholds[thresholds.Length - 1];

            for (int i = 0; i < thresholds.Length; ++i)
            {
                if (current < thresholds[i])
                {
                    next = thresholds[i];
                    break;
                }
            }

            int maximum =
                explicitMaximum > 0
                    ? explicitMaximum
                    : thresholds[thresholds.Length - 1];

            bool complete =
                current >= thresholds[thresholds.Length - 1];

            rows.Add(
                new MilestoneRow(
                    category,
                    name,
                    current,
                    maximum,
                    complete
                        ? thresholds[thresholds.Length - 1]
                        : next,
                    complete));
        }

        private static string TrackCategory(
            ProgressTrackKind kind)
        {
            switch (kind)
            {
                case ProgressTrackKind.Mastery:
                    return "Mastery";

                case ProgressTrackKind.Profession:
                    return "Profession";

                case ProgressTrackKind.Hunter:
                    return "Hunter";

                case ProgressTrackKind.Vampire:
                    return "Vampire";

                default:
                    return "General";
            }
        }

        private static int CompareRows(
            MilestoneRow left,
            MilestoneRow right)
        {
            int category =
                string.Compare(
                    left.Category,
                    right.Category,
                    StringComparison.OrdinalIgnoreCase);

            if (category != 0)
                return category;

            return string.Compare(
                left.Name,
                right.Name,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
