using System;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local-machine-only hotbar assignments. Slots store canonical ability definition IDs,
    /// never display names. This is presentation/input preference only and is never sent to
    /// the GameServer; activating a slot still uses the existing authoritative gameplay request.
    /// </summary>
    public enum PlayerHotbarEntryKind : byte
    {
        Empty = 0,
        BasicLight = 1,
        BasicHeavy = 2,
        Ability = 3,
    }

    public readonly struct PlayerHotbarAssignment
    {
        public PlayerHotbarEntryKind Kind { get; }
        public string AbilityDefinitionId { get; }

        public PlayerHotbarAssignment(PlayerHotbarEntryKind kind, string abilityDefinitionId = "")
        {
            Kind = kind;
            AbilityDefinitionId = abilityDefinitionId ?? string.Empty;
        }

        public bool IsEmpty => Kind == PlayerHotbarEntryKind.Empty;
        public bool IsAbility => Kind == PlayerHotbarEntryKind.Ability && !string.IsNullOrWhiteSpace(AbilityDefinitionId);
    }

    public static class PlayerHotbarConfig
    {
        public const int SlotCount = 10;
        private const string PreferencePrefix = "MMO.PlayerHotbar.V1.";

        public static event Action<int, PlayerHotbarAssignment> Changed;

        public static PlayerHotbarAssignment Get(int oneBasedSlot)
        {
            int slot = ClampSlot(oneBasedSlot);
            string prefix = PreferencePrefix + slot + ".";
            PlayerHotbarEntryKind defaultKind = DefaultKind(slot);
            int rawKind = PlayerPrefs.GetInt(prefix + "Kind", (int)defaultKind);
            PlayerHotbarEntryKind kind = defaultKind;
            if (rawKind >= byte.MinValue && rawKind <= byte.MaxValue)
            {
                byte rawKindByte = (byte)rawKind;
                if (Enum.IsDefined(typeof(PlayerHotbarEntryKind), rawKindByte))
                    kind = (PlayerHotbarEntryKind)rawKindByte;
            }
            string abilityId = kind == PlayerHotbarEntryKind.Ability
                ? PlayerPrefs.GetString(prefix + "Ability", string.Empty)
                : string.Empty;
            if (kind == PlayerHotbarEntryKind.Ability && string.IsNullOrWhiteSpace(abilityId))
                kind = PlayerHotbarEntryKind.Empty;
            return new PlayerHotbarAssignment(kind, abilityId);
        }

        public static void AssignAbility(int oneBasedSlot, string abilityDefinitionId)
        {
            if (string.IsNullOrWhiteSpace(abilityDefinitionId))
            {
                Clear(oneBasedSlot);
                return;
            }
            Save(oneBasedSlot, new PlayerHotbarAssignment(PlayerHotbarEntryKind.Ability, abilityDefinitionId.Trim()));
        }

        public static void AssignBasicLight(int oneBasedSlot) =>
            Save(oneBasedSlot, new PlayerHotbarAssignment(PlayerHotbarEntryKind.BasicLight));

        public static void AssignBasicHeavy(int oneBasedSlot) =>
            Save(oneBasedSlot, new PlayerHotbarAssignment(PlayerHotbarEntryKind.BasicHeavy));

        public static void Clear(int oneBasedSlot) =>
            Save(oneBasedSlot, new PlayerHotbarAssignment(PlayerHotbarEntryKind.Empty));

        public static void ResetAllDefaults()
        {
            for (int slot = 1; slot <= SlotCount; ++slot)
            {
                string prefix = PreferencePrefix + slot + ".";
                PlayerPrefs.DeleteKey(prefix + "Kind");
                PlayerPrefs.DeleteKey(prefix + "Ability");
            }
            PlayerPrefs.Save();
            for (int slot = 1; slot <= SlotCount; ++slot)
                Changed?.Invoke(slot, Get(slot));
        }

        private static void Save(int oneBasedSlot, PlayerHotbarAssignment assignment)
        {
            int slot = ClampSlot(oneBasedSlot);
            string prefix = PreferencePrefix + slot + ".";
            PlayerPrefs.SetInt(prefix + "Kind", (int)assignment.Kind);
            if (assignment.Kind == PlayerHotbarEntryKind.Ability && !string.IsNullOrWhiteSpace(assignment.AbilityDefinitionId))
                PlayerPrefs.SetString(prefix + "Ability", assignment.AbilityDefinitionId);
            else
                PlayerPrefs.DeleteKey(prefix + "Ability");
            PlayerPrefs.Save();
            Changed?.Invoke(slot, assignment);
        }

        private static int ClampSlot(int oneBasedSlot) => Mathf.Clamp(oneBasedSlot, 1, SlotCount);

        private static PlayerHotbarEntryKind DefaultKind(int slot)
        {
            if (slot == 1) return PlayerHotbarEntryKind.BasicLight;
            if (slot == 2) return PlayerHotbarEntryKind.BasicHeavy;
            return PlayerHotbarEntryKind.Empty;
        }
    }
}
