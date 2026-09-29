using System.Globalization;

namespace Game.Shared.Combat
{
    /// <summary>
    /// Internal authoritative derived-stat keys. They are calculated when equipment/status
    /// membership changes so combat resolution does not scan item/status tags per hit.
    /// </summary>
    public static class CombatDerivedStatIds
    {
        public static string DamageResponseMultiplier(ushort damageTypeId) =>
            "Combat.ResponseMultiplier." + damageTypeId.ToString(CultureInfo.InvariantCulture);

        public static string DamageResponseFlat(ushort damageTypeId) =>
            "Combat.ResponseFlat." + damageTypeId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Client presentation stance family. This is intentionally derived from already-replicated
    /// equipment presentation/content ids and never requires its own runtime network field.
    /// </summary>
    public enum HumanoidCombatStance : byte
    {
        None = 0,
        Pistol = 1,
        Rifle = 2,
        AssaultRifle = 3,
        DualPistol = 4,
        Bat = 5,
        SmallBlade = 6,
        OneHandBlade = 7,
        TwoHandBlade = 8,
        SmallBlunt = 9,
        OneHandBlunt = 10,
        TwoHandBlunt = 11,
        FistWeapon = 12,
        OneHandAxe = 13,
        TwoHandAxe = 14,
        Polearm = 15,
        Staff = 16,
        Shotgun = 17,
        SubmachineGun = 18,
        Bow = 19,
        Crossbow = 20,
        Shield = 21,
        HeavyWeapon = 22,
        DualWieldMelee = 23,
        ImprovisedOneHand = 24,
        ImprovisedTwoHand = 25,
        Stake = 26,
        NaturalWeapon = 27,
        Unarmed = 28,
        Thrown = 29,
    }

    /// <summary>
    /// Zero-wire semantic fallback for weapon presentation. Content authors may use stable
    /// definition-id tokens without adding stance bytes to gameplay settings or per-player state.
    /// </summary>
    public static class HumanoidCombatStanceResolver
    {
        public static HumanoidCombatStance ResolveFromDefinitionId(string definitionId)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
                return HumanoidCombatStance.None;

            string id = definitionId.Trim().ToLowerInvariant();
            bool weaponSemantic =
                id.Contains(".weapon.") || id.StartsWith("weapon.") ||
                id.Contains(".firearm.") || id.StartsWith("firearm.") ||
                id.Contains(".gun.") || id.StartsWith("gun.") ||
                id.Contains(".melee.") || id.StartsWith("melee.");
            if (!weaponSemantic)
                return HumanoidCombatStance.None;

            bool twoHand = ContainsAny(id,
                "twohand", "two_hand", "2h", "greatsword", "great_sword",
                "greataxe", "great_axe", "greatmace", "great_mace",
                "warhammer", "war_hammer");

            if (ContainsAny(id, "shotgun"))
                return HumanoidCombatStance.Shotgun;
            if (ContainsAny(id, "crossbow"))
                return HumanoidCombatStance.Crossbow;
            if (ContainsAny(id, "bow"))
                return HumanoidCombatStance.Bow;
            if (ContainsAny(id, "dual_pistol", "dualpistol", "dual.pistol"))
                return HumanoidCombatStance.DualPistol;
            if (ContainsAny(id, "pistol", "revolver", "handgun"))
                return HumanoidCombatStance.Pistol;
            if (ContainsAny(id, "smg", "submachine", "sub_machine"))
                return HumanoidCombatStance.SubmachineGun;
            if (ContainsAny(id, "assault_rifle", "assaultrifle", "assault.rifle"))
                return HumanoidCombatStance.AssaultRifle;
            if (ContainsAny(id, "rifle", "carbine", "longgun", "long_gun"))
                return HumanoidCombatStance.Rifle;
            if (ContainsAny(id, "minigun", "heavy_weapon", "heavyweapon"))
                return HumanoidCombatStance.HeavyWeapon;
            if (ContainsAny(id, "spear", "halberd", "polearm", "pole_arm"))
                return HumanoidCombatStance.Polearm;
            if (ContainsAny(id, "staff", "wand"))
                return HumanoidCombatStance.Staff;
            if (ContainsAny(id, "shield"))
                return HumanoidCombatStance.Shield;
            if (ContainsAny(id, "stake"))
                return HumanoidCombatStance.Stake;
            if (ContainsAny(id, "throwing", "thrown", "javelin"))
                return HumanoidCombatStance.Thrown;
            if (ContainsAny(id, "fist", "knuckle", "gauntlet"))
                return HumanoidCombatStance.FistWeapon;
            if (ContainsAny(id, "fang", "claw", "natural"))
                return HumanoidCombatStance.NaturalWeapon;
            if (ContainsAny(id, "knife", "dagger"))
                return HumanoidCombatStance.SmallBlade;
            if (ContainsAny(id, "sword", "blade"))
                return twoHand ? HumanoidCombatStance.TwoHandBlade : HumanoidCombatStance.OneHandBlade;
            if (ContainsAny(id, "axe"))
                return twoHand ? HumanoidCombatStance.TwoHandAxe : HumanoidCombatStance.OneHandAxe;
            if (ContainsAny(id, "bat"))
                return HumanoidCombatStance.Bat;
            if (ContainsAny(id, "mace", "hammer", "club", "blunt"))
                return twoHand ? HumanoidCombatStance.TwoHandBlunt : HumanoidCombatStance.OneHandBlunt;

            // Unknown authored weapons use an explicit improvised stance instead of silently
            // pretending to be a sword. This is still entirely client-resolved.
            return twoHand
                ? HumanoidCombatStance.ImprovisedTwoHand
                : HumanoidCombatStance.ImprovisedOneHand;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; ++i)
                if (value.Contains(tokens[i]))
                    return true;
            return false;
        }
    }

}
