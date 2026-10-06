#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    public sealed partial class CharacterVisualPaletteAuthoringWindow
    {
        /// <summary>
        /// Outfit Builder -> existing Character Visual palette scanner bridge.
        /// Uses canonical slotId/optionId pairs instead of runtime renderer names.
        /// Existing Primary/Secondary/Accent assignments are preserved.
        /// Only a missing Primary assignment may be auto-authored, using the same
        /// non-skin Primary suggestion policy already used by the palette inspector.
        /// </summary>
        public static int EnsurePrimaryDyeForOutfit(
            CharacterWearablePartSelection[] parts)
        {
            if (parts == null || parts.Length == 0)
                return 0;

            CharacterVisualProfile profile =
                AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                    CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
                return 0;

            CharacterVisualPaletteAuthoringWindow worker =
                CreateInstance<CharacterVisualPaletteAuthoringWindow>();
            if (worker == null)
                return 0;

            worker._profile = profile;

            int ready = 0;
            bool changed = false;
            var seen = new HashSet<ulong>();

            try
            {
                for (int i = 0; i < parts.Length; ++i)
                {
                    CharacterWearablePartSelection part = parts[i];
                    if (part.slotId == 0 || part.optionId == 0)
                        continue;

                    ulong key = ((ulong)part.slotId << 32) | part.optionId;
                    if (!seen.Add(key) ||
                        !profile.TryGetSlot(
                            part.slotId,
                            out CharacterVisualSlotDefinition slot) ||
                        slot == null ||
                        !profile.TryGetOption(
                            part.slotId,
                            part.optionId,
                            out CharacterVisualOptionDefinition option) ||
                        option == null ||
                        option.mesh == null)
                        continue;

                    if (HasOutfitDyeChannel(
                        option.paletteCells,
                        CharacterVisualPaletteChannel.Primary))
                    {
                        ready++;
                        continue;
                    }

                    GameObject scanObject = new GameObject("[Outfit Dye Scan]");
                    scanObject.hideFlags = HideFlags.HideAndDontSave;

                    try
                    {
                        SkinnedMeshRenderer renderer =
                            scanObject.AddComponent<SkinnedMeshRenderer>();
                        renderer.sharedMesh = option.mesh;
                        renderer.sharedMaterials =
                            option.materials ?? Array.Empty<Material>();

                        worker.RestorePreview();
                        worker._renderer = renderer;
                        worker._materialIndex = 0;
                        worker._resolvedSlot = slot;
                        worker._resolvedOption = option;
                        worker._candidates.Clear();
                        worker._currentScanSignature = string.Empty;

                        worker.Scan();

                        PaletteCellCandidate candidate =
                            FindOutfitPrimaryCandidate(
                                worker._candidates,
                                option.paletteCells);

                        if (candidate == null)
                            continue;

                        Undo.RecordObject(
                            profile,
                            "Author Outfit Primary Dye Cell");

                        var cells =
                            new List<CharacterVisualPaletteCellDefinition>(
                                option.paletteCells ??
                                Array.Empty<CharacterVisualPaletteCellDefinition>());

                        cells.Add(ToDefinition(
                            candidate,
                            CharacterVisualPaletteChannel.Primary));

                        SortCells(cells);

                        option.paletteCells = cells.ToArray();
                        option.paletteReviewConfirmed = false;
                        option.paletteReviewFingerprint = string.Empty;
                        option.paletteScanSignature =
                            !string.IsNullOrEmpty(worker._currentScanSignature)
                                ? worker._currentScanSignature
                                : BuildSavedPaletteCellSignature(
                                    option.paletteCells);

                        changed = true;
                        ready++;
                    }
                    finally
                    {
                        DestroyImmediate(scanObject);
                    }
                }

                if (changed)
                {
                    EditorUtility.SetDirty(profile);
                    AssetDatabase.SaveAssets();
                }

                return ready;
            }
            finally
            {
                worker.RestorePreview();
                DestroyImmediate(worker);
            }
        }

        private static PaletteCellCandidate FindOutfitPrimaryCandidate(
            IList<PaletteCellCandidate> candidates,
            CharacterVisualPaletteCellDefinition[] existing)
        {
            if (candidates == null)
                return null;

            // Pass 1: use the scanner's normal best non-skin suggestion.
            for (int i = 0; i < candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = candidates[i];
                if (candidate == null ||
                    !candidate.isSuggested ||
                    candidate.skinReferences > 0 ||
                    OutfitCellAlreadyAssigned(existing, candidate))
                    continue;

                return candidate;
            }

            // Pass 2: if the normal suggestion was already assigned to another channel,
            // choose the strongest remaining non-skin cell without overwriting it.
            PaletteCellCandidate best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = candidates[i];
                if (candidate == null ||
                    candidate.skinReferences > 0 ||
                    OutfitCellAlreadyAssigned(existing, candidate))
                    continue;

                int score = candidate.nonSkinReferences > 0
                    ? candidate.nonSkinReferences
                    : candidate.totalReferences;

                if (best == null || score > bestScore ||
                    (score == bestScore &&
                     CompareCandidates(candidate, best) < 0))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private static bool OutfitCellAlreadyAssigned(
            CharacterVisualPaletteCellDefinition[] existing,
            PaletteCellCandidate candidate)
        {
            CharacterVisualPaletteCellDefinition[] cells =
                existing ?? Array.Empty<CharacterVisualPaletteCellDefinition>();

            for (int i = 0; i < cells.Length; ++i)
            {
                if (cells[i].IsValid && SameCell(cells[i], candidate))
                    return true;
            }

            return false;
        }

        private static bool HasOutfitDyeChannel(
            CharacterVisualPaletteCellDefinition[] cells,
            CharacterVisualPaletteChannel channel)
        {
            CharacterVisualPaletteCellDefinition[] values =
                cells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();

            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].IsValid &&
                    values[i].channel == channel)
                    return true;
            }

            return false;
        }
    }
}
#endif
