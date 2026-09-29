using System;
using Game.Shared.Actors;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-side presentation filter only. Real entitlement/faction enforcement must also
    /// occur on the authoritative server when those account systems are wired.
    /// </summary>
    public static class CharacterCreatorAccessPolicy
    {
        public static Func<bool> PremiumAccessResolver;
        public static Func<ActorFaction> FactionResolver;

        public static bool HasPremiumAccess
        {
            get
            {
                try { return PremiumAccessResolver != null && PremiumAccessResolver(); }
                catch (Exception ex)
                {
                    Debug.LogError("[CharacterCreator] Premium access resolver failed: " + ex.Message);
                    return false;
                }
            }
        }

        public static ActorFaction Faction
        {
            get
            {
                try { return FactionResolver != null ? FactionResolver() : ActorFaction.Human; }
                catch (Exception ex)
                {
                    Debug.LogError("[CharacterCreator] Faction resolver failed: " + ex.Message);
                    return ActorFaction.Human;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            PremiumAccessResolver = null;
            FactionResolver = null;
        }
    }
}
