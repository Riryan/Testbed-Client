using System;
using System.Collections.Generic;
using Game.Client.Content;
using Game.Shared.Content;
using Player.Networking;

namespace Game.Client.UI.Achievements
{
    /// <summary>
    /// Client-only projection of already-authoritative progression state into milestone rows.
    /// It never sends a request and never owns gameplay state. Definitions come from the
    /// client-known content catalog; values come from PlayerEntityGameManager.LatestProgression.
    /// </summary>
    public static class AchievementMilestoneProjection
    {
        public readonly struct MilestoneRow
        {
            public readonly string Key;
            public readonly string Category;
            public readonly string Name;
            public readonly int Current;
            public readonly int Maximum;
            public readonly int NextThreshold;
            public readonly int CompletedSteps;
            public readonly int TotalSteps;
            public readonly bool Complete;

            public MilestoneRow(
                string key,
                string category,
                string name,
                int current,
                int maximum,
                int nextThreshold,
                int completedSteps,
                int totalSteps,
                bool complete)
            {
                Key = key ?? string.Empty;
                Category = category ?? string.Empty;
                Name = name ?? string.Empty;
                Current = Math.Max(0, current);
                Maximum = Math.Max(1, maximum);
                NextThreshold = Math.Max(0, nextThreshold);
                CompletedSteps = Math.Max(0, completedSteps);
                TotalSteps = Math.Max(1, totalSteps);
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
                "level",
                "Character",
                "Character Level",
                Math.Max(1, snapshot.level),
                LevelMilestones);

            AddThresholdRow(
                rows,
                "recipes",
                "Knowledge",
                "Known Recipes",
                snapshot.knownRecipeDataIds?.Length ?? 0,
                CountMilestones);

            AddThresholdRow(
                rows,
                "abilities",
                "Knowledge",
                "Known Abilities",
                snapshot.knownAbilityWireIds?.Length ?? 0,
                CountMilestones);

            var values = new Dictionary<ushort, int>();
            ProgressTrackWire[] tracks = snapshot.tracks ?? Array.Empty<ProgressTrackWire>();
            for (int i = 0; i < tracks.Length; ++i)
                values[tracks[i].dataId] = Math.Max(0, tracks[i].value);

            ProgressTrackDefinition[] definitions = ClientRecoveryContentCache.GetProgressTracks();
            for (int i = 0; i < definitions.Length; ++i)
            {
                ProgressTrackDefinition definition = definitions[i];
                if (definition == null || definition.dataId == 0)
                    continue;

                int maximum = Math.Max(1, definition.maximumValue);
                int current = values.TryGetValue(definition.dataId, out int value) ? value : 0;
                int[] thresholds = BuildPercentThresholds(maximum);
                string name = string.IsNullOrWhiteSpace(definition.displayName)
                    ? definition.definitionId
                    : definition.displayName;

                AddThresholdRow(
                    rows,
                    $"track:{definition.dataId}",
                    TrackCategory(definition.kind),
                    name,
                    current,
                    thresholds,
                    maximum);
            }

            rows.Sort(CompareRows);
            return rows;
        }

        public static HashSet<string> CompletedKeys(ProgressionSnapshotMessage snapshot)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            List<MilestoneRow> rows = Build(snapshot);
            for (int i = 0; i < rows.Count; ++i)
            {
                MilestoneRow row = rows[i];
                int[] thresholds = ThresholdsFor(row);
                for (int j = 0; j < thresholds.Length; ++j)
                {
                    if (row.Current >= thresholds[j])
                        result.Add($"{row.Key}@{thresholds[j]}");
                }
            }
            return result;
        }

        public static string MilestoneLabel(string completedKey, ProgressionSnapshotMessage snapshot)
        {
            if (string.IsNullOrWhiteSpace(completedKey))
                return "Milestone reached";

            int split = completedKey.LastIndexOf('@');
            if (split <= 0 || !int.TryParse(completedKey.Substring(split + 1), out int threshold))
                return "Milestone reached";

            string key = completedKey.Substring(0, split);
            List<MilestoneRow> rows = Build(snapshot);
            for (int i = 0; i < rows.Count; ++i)
            {
                if (string.Equals(rows[i].Key, key, StringComparison.Ordinal))
                    return $"{rows[i].Name}: {threshold:N0}";
            }

            return $"Milestone reached: {threshold:N0}";
        }

        public static float Normalized(MilestoneRow row)
        {
            if (row.Maximum <= 0)
                return 0f;
            return Math.Max(0f, Math.Min(1f, row.Current / (float)row.Maximum));
        }

        private static int[] ThresholdsFor(MilestoneRow row)
        {
            if (string.Equals(row.Key, "level", StringComparison.Ordinal))
                return LevelMilestones;
            if (string.Equals(row.Key, "recipes", StringComparison.Ordinal) ||
                string.Equals(row.Key, "abilities", StringComparison.Ordinal))
                return CountMilestones;
            return BuildPercentThresholds(row.Maximum);
        }

        private static int[] BuildPercentThresholds(int maximum)
        {
            maximum = Math.Max(1, maximum);
            var result = new int[PercentMilestones.Length];
            int previous = 0;
            for (int i = 0; i < PercentMilestones.Length; ++i)
            {
                int threshold = (int)Math.Ceiling(maximum * (PercentMilestones[i] / 100d));
                threshold = Math.Min(maximum, Math.Max(1, threshold));
                result[i] = threshold;
                previous = threshold;
            }
            result[result.Length - 1] = maximum;
            return result;
        }

        private static void AddThresholdRow(
            List<MilestoneRow> rows,
            string key,
            string category,
            string name,
            int current,
            int[] thresholds,
            int explicitMaximum = 0)
        {
            thresholds = thresholds ?? Array.Empty<int>();
            if (thresholds.Length == 0)
                return;

            int completed = 0;
            int next = thresholds[thresholds.Length - 1];
            for (int i = 0; i < thresholds.Length; ++i)
            {
                if (current >= thresholds[i])
                    completed++;
                else
                {
                    next = thresholds[i];
                    break;
                }
            }

            int maximum = explicitMaximum > 0 ? explicitMaximum : thresholds[thresholds.Length - 1];
            bool complete = current >= thresholds[thresholds.Length - 1];
            rows.Add(new MilestoneRow(
                key,
                category,
                name,
                current,
                maximum,
                complete ? thresholds[thresholds.Length - 1] : next,
                completed,
                thresholds.Length,
                complete));
        }

        private static string TrackCategory(ProgressTrackKind kind)
        {
            switch (kind)
            {
                case ProgressTrackKind.Mastery: return "Combat / Mastery";
                case ProgressTrackKind.Profession: return "Profession";
                case ProgressTrackKind.Hunter: return "Hunter";
                case ProgressTrackKind.Vampire: return "Vampire";
                default: return "General";
            }
        }

        private static int CompareRows(MilestoneRow left, MilestoneRow right)
        {
            int category = string.Compare(left.Category, right.Category, StringComparison.OrdinalIgnoreCase);
            if (category != 0)
                return category;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
