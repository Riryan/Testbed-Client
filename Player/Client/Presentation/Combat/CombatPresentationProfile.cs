using System;
using System.Collections.Generic;
using UnityEngine;

namespace Player.Client.Presentation
{
    [Serializable]
    public sealed class CombatPresentationProfile
    {
        [Min(1)] public ushort presentationId;
        [Header("Animator Triggers")]
        public string actionTrigger;
        public string abilityStartTrigger;
        public string abilityReleaseTrigger;
        public string abilityCancelTrigger;
        public string impactTrigger;
        public string statusApplyTrigger;
        public string statusRemoveTrigger;

        [Header("FX / Audio")]
        public GameObject sourceVfxPrefab;
        public GameObject targetVfxPrefab;
        public GameObject statusLoopPrefab;
        public GameObject visibleProjectilePrefab;
        public AudioClip audioClip;
        [Min(0.1f)] public float projectileSpeed = 18f;
        [Min(0.1f)] public float projectileMaximumLifetime = 8f;

        [Header("Owner Recoil (presentation only)")]
        public bool applyOwnerRecoil;
        public float recoilPitch = 2f;
        public float recoilYaw = 0.35f;
        public float recoilRoll;
        [Range(0f, 1f)] public float weaponKick = 1f;
    }

    /// <summary>
    /// Client-only semantic-id to Unity presentation mapping. The standalone GameServer
    /// never references these assets and remains authoritative for all gameplay outcomes.
    /// </summary>
    [CreateAssetMenu(menuName = "MMO/Combat Presentation Catalog", fileName = "CombatPresentationCatalog")]
    public sealed class CombatPresentationCatalog : ScriptableObject
    {
        public CombatPresentationProfile[] profiles = Array.Empty<CombatPresentationProfile>();
        private Dictionary<ushort, CombatPresentationProfile> _lookup;

        public bool TryGet(ushort presentationId, out CombatPresentationProfile profile)
        {
            if (_lookup == null)
                Rebuild();
            profile = null;
            return presentationId != 0 && _lookup.TryGetValue(presentationId, out profile);
        }

        private void OnEnable() => Rebuild();
        private void OnValidate() => Rebuild();

        private void Rebuild()
        {
            _lookup = new Dictionary<ushort, CombatPresentationProfile>();
            CombatPresentationProfile[] source = profiles ?? Array.Empty<CombatPresentationProfile>();
            for (int i = 0; i < source.Length; ++i)
            {
                CombatPresentationProfile profile = source[i];
                if (profile == null || profile.presentationId == 0)
                    continue;
                _lookup[profile.presentationId] = profile;
            }
        }
    }
}
