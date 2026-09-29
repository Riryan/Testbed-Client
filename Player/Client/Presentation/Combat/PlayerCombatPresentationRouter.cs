using System;
using System.Collections.Generic;
using Game.Shared.Abilities;
using Game.Shared.StatusEffects;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Converts compact semantic combat wire events into local Unity presentation. No method
    /// here is allowed to mutate authoritative gameplay state.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9500)]
    public sealed class PlayerCombatPresentationRouter : MonoBehaviour
    {
        public static event Action<CombatPresentationCueWire, PlayerEntityClient, PlayerEntityClient> CueReceived;
        public static event Action<CombatDamageWire> ParticipantDamageReceived;
        public static event Action<CombatPresentationCueWire, PlayerEntityClient> ActionBodyPresented;

        [Tooltip("Optional explicit catalog. If empty, Resources/CombatPresentationCatalog is used when present.")]
        public CombatPresentationCatalog catalog;

        private struct PendingFirePulse
        {
            public double DueAt;
            public CombatPresentationCueWire Cue;
        }

        private PlayerEntityGameManager _manager;
        private readonly Dictionary<ulong, GameObject> _statusLoops = new Dictionary<ulong, GameObject>();
        private readonly List<PendingFirePulse> _pendingFirePulses = new List<PendingFirePulse>(32);

        private void Awake()
        {
            if (catalog == null)
                catalog = Resources.Load<CombatPresentationCatalog>("CombatPresentationCatalog");
        }

        private void OnEnable() => BindManager();

        private void Update()
        {
            if (_manager == null)
                BindManager();
            FlushDueFirePulses();
        }

        private void OnDisable()
        {
            UnbindManager();
            foreach (GameObject loop in _statusLoops.Values)
                if (loop != null) Destroy(loop);
            _statusLoops.Clear();
            _pendingFirePulses.Clear();
        }

        private void BindManager()
        {
            PlayerEntityGameManager next = FindFirstObjectByType<PlayerEntityGameManager>();
            if (ReferenceEquals(next, _manager))
                return;
            UnbindManager();
            _manager = next;
            if (_manager == null)
                return;

            _manager.CombatPresentationBatchReceived += OnCombatPresentationBatch;
            _manager.CombatFireCycleBatchReceived += OnCombatFireCycleBatch;
            _manager.LocalFireTriggerPredicted += OnLocalFireTriggerPredicted;
            _manager.LocalBasicAttackPredicted += OnLocalBasicAttackPredicted;
            _manager.LocalReloadAccepted += OnLocalReloadAccepted;
            _manager.PlayerCombatDamageReceived += OnParticipantDamage;
            _manager.PlayerAbilityCastStateReceived += OnOwnerAbilityState;
            _manager.PlayerStatusEffectDeltaReceived += OnOwnerStatusDelta;
        }

        private void UnbindManager()
        {
            if (_manager == null)
                return;
            _manager.CombatPresentationBatchReceived -= OnCombatPresentationBatch;
            _manager.CombatFireCycleBatchReceived -= OnCombatFireCycleBatch;
            _manager.LocalFireTriggerPredicted -= OnLocalFireTriggerPredicted;
            _manager.LocalBasicAttackPredicted -= OnLocalBasicAttackPredicted;
            _manager.LocalReloadAccepted -= OnLocalReloadAccepted;
            _manager.PlayerCombatDamageReceived -= OnParticipantDamage;
            _manager.PlayerAbilityCastStateReceived -= OnOwnerAbilityState;
            _manager.PlayerStatusEffectDeltaReceived -= OnOwnerStatusDelta;
            _manager = null;
        }

        private void OnCombatPresentationBatch(PlayerCombatPresentationBatchMessage batch)
        {
            CombatPresentationCueWire[] cues = batch.cues ?? Array.Empty<CombatPresentationCueWire>();
            for (int i = 0; i < cues.Length; ++i)
                Present(cues[i]);
        }

        private void OnCombatFireCycleBatch(PlayerCombatFireCycleBatchMessage batch)
        {
            CombatFireCycleWire[] cycles = batch.cycles ?? Array.Empty<CombatFireCycleWire>();
            for (int i = 0; i < cycles.Length; ++i)
                QueueFirePulses(cycles[i].Source, cycles[i].semanticId, cycles[i].sequence, cycles[i].RoundsFired, cycles[i].CycleSpanSeconds);
        }

        private void OnLocalBasicAttackPredicted(BasicAttackMode mode, BasicAttackInputKind inputKind, ushort semanticId)
        {
            if (!TryResolveOwner(out PlayerEntityClient owner) || owner.NetworkBridge == null)
                return;
            Present(new CombatPresentationCueWire
            {
                kind = (byte)CombatPresentationCueKind.Action,
                source = new PlayerTargetReferenceWire
                {
                    objectId = owner.NetworkBridge.ObjectId,
                    generation = owner.NetworkBridge.Generation,
                },
                semanticId = semanticId,
                sequence = unchecked((ushort)Environment.TickCount),
                flags = 0,
            });
        }

        private void OnLocalReloadAccepted()
        {
            if (TryResolveOwner(out PlayerEntityClient owner))
                owner.PresentReloadCombatAction();
        }

        private void OnLocalFireTriggerPredicted(ushort semanticId, byte roundsFired, FirearmFireMode fireMode, float roundsPerSecond)
        {
            if (roundsFired == 0 || !TryResolveOwner(out PlayerEntityClient owner) || owner.NetworkBridge == null)
                return;
            float rps = Mathf.Max(FirearmCadenceTiming.MinimumRoundsPerSecond, roundsPerSecond);
            float span = roundsFired > 1 ? Mathf.Max(0.02f, roundsFired / rps) : 0.02f;
            QueueFirePulses(
                new PlayerTargetReferenceWire { objectId = owner.NetworkBridge.ObjectId, generation = owner.NetworkBridge.Generation },
                semanticId, unchecked((ushort)Environment.TickCount), roundsFired, span);
        }

        private void QueueFirePulses(PlayerTargetReferenceWire source, ushort semanticId, ushort sequence, int rounds, float spanSeconds)
        {
            int count = Mathf.Clamp(rounds, 0, 31);
            if (count <= 0)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            double spacing = count > 1 ? Math.Max(0.001, spanSeconds / count) : 0d;
            for (int i = 0; i < count; ++i)
            {
                var cue = new CombatPresentationCueWire
                {
                    kind = (byte)CombatPresentationCueKind.Action,
                    source = source,
                    semanticId = semanticId,
                    sequence = unchecked((ushort)(sequence + i)),
                    flags = 0,
                };
                if (i == 0) Present(cue);
                else _pendingFirePulses.Add(new PendingFirePulse { DueAt = now + (spacing * i), Cue = cue });
            }
        }

        private void FlushDueFirePulses()
        {
            if (_pendingFirePulses.Count == 0)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = _pendingFirePulses.Count - 1; i >= 0; --i)
            {
                PendingFirePulse pulse = _pendingFirePulses[i];
                if (pulse.DueAt > now)
                    continue;
                _pendingFirePulses.RemoveAt(i);
                Present(pulse.Cue);
            }
        }

        private void OnParticipantDamage(PlayerCombatDamageEventMessage message)
        {
            ParticipantDamageReceived?.Invoke(message.damage);
        }

        private void OnOwnerAbilityState(PlayerAbilityCastStateMessage message)
        {
            if (!message.success || _manager == null || !_manager.IsLocalPlayerReference(message.source) ||
                !PlayerGameplaySettingsRuntime.TryGetAbility(message.abilityWireId, out GameplayAbilityReferenceWire ability) ||
                ability.presentationId == 0)
                return;

            CombatPresentationCueKind kind;
            switch (message.Phase)
            {
                case AbilityPresentationPhase.CastStarted:
                    kind = CombatPresentationCueKind.AbilityStart;
                    break;
                case AbilityPresentationPhase.CastCompleted:
                    kind = CombatPresentationCueKind.AbilityRelease;
                    break;
                case AbilityPresentationPhase.CastCancelled:
                    kind = CombatPresentationCueKind.AbilityCancel;
                    break;
                default:
                    return;
            }

            Present(new CombatPresentationCueWire
            {
                kind = (byte)kind,
                source = message.source,
                target = message.target,
                semanticId = ability.presentationId,
                sequence = unchecked((ushort)message.castSequence),
                flags = message.target.IsValid ? (byte)CombatPresentationCueFlags.HasTarget : (byte)0,
            });
        }

        private void OnOwnerStatusDelta(PlayerStatusEffectDeltaMessage delta)
        {
            if (!PlayerGameplaySettingsRuntime.TryGetStatus(delta.statusWireId, out GameplayStatusReferenceWire status) ||
                status.presentationId == 0 || !TryResolveOwner(out PlayerEntityClient owner))
                return;

            bool removed = delta.Kind == StatusEffectChangeKind.Removed ||
                           delta.Kind == StatusEffectChangeKind.Expired ||
                           delta.Kind == StatusEffectChangeKind.ClearedOnDeath;
            Present(new CombatPresentationCueWire
            {
                kind = (byte)(removed ? CombatPresentationCueKind.StatusRemoved : CombatPresentationCueKind.StatusApplied),
                source = new PlayerTargetReferenceWire
                {
                    objectId = owner.NetworkBridge != null ? owner.NetworkBridge.ObjectId : 0,
                    generation = owner.NetworkBridge != null ? owner.NetworkBridge.Generation : (ushort)0,
                },
                semanticId = status.presentationId,
                sequence = unchecked((ushort)delta.statusRevision),
            });
        }

        private void Present(CombatPresentationCueWire cue)
        {
            PlayerEntityClient source = Resolve(cue.source);
            PlayerEntityClient target = cue.HasTarget ? Resolve(cue.target) : null;
            CueReceived?.Invoke(cue, source, target);

            CombatPresentationCueKind kind = (CombatPresentationCueKind)cue.kind;
            if (kind == CombatPresentationCueKind.Action)
            {
                // Body presentation is always triggered before optional observers. A client-only
                // feedback system must never be able to suppress the actual attack animation.
                source?.PresentPrimaryCombatAction(cue.sequence);
                ActionBodyPresented?.Invoke(cue, source);
            }

            // The semantic presentation catalog is optional FX/audio/recoil decoration. Basic
            // attack body animation above is canonical PlayerHumanoid presentation and must not
            // disappear merely because an action has presentationId=0 or no catalog entry.
            if (catalog == null || !catalog.TryGet(cue.semanticId, out CombatPresentationProfile profile))
                return;

            if (kind == CombatPresentationCueKind.VisibleProjectile)
            {
                SpawnVisibleProjectile(source, target, profile);
                return;
            }

            if (kind == CombatPresentationCueKind.StatusApplied)
            {
                ApplyStatusLoop(source, cue, profile);
                Trigger(source, profile.statusApplyTrigger);
                return;
            }

            if (kind == CombatPresentationCueKind.StatusRemoved)
            {
                RemoveStatusLoop(source, cue.semanticId);
                Trigger(source, profile.statusRemoveTrigger);
                return;
            }

            string sourceTrigger = kind switch
            {
                CombatPresentationCueKind.Action => profile.actionTrigger,
                CombatPresentationCueKind.AbilityStart => profile.abilityStartTrigger,
                CombatPresentationCueKind.AbilityRelease => profile.abilityReleaseTrigger,
                CombatPresentationCueKind.AbilityCancel => profile.abilityCancelTrigger,
                CombatPresentationCueKind.StreamStart => profile.actionTrigger,
                _ => string.Empty,
            };

            Trigger(source, sourceTrigger);
            if (kind == CombatPresentationCueKind.Action || kind == CombatPresentationCueKind.Impact)
                Trigger(target, profile.impactTrigger);

            SpawnFx(profile.sourceVfxPrefab, source != null ? source.PresentationTransform : null);
            if (target != null && (kind == CombatPresentationCueKind.Action || kind == CombatPresentationCueKind.Impact))
                SpawnFx(profile.targetVfxPrefab, target.PresentationTransform);

            if (profile.audioClip != null && source != null && source.PresentationTransform != null)
                AudioSource.PlayClipAtPoint(profile.audioClip, source.PresentationTransform.position, PlayerAudioConfig.SfxVolume);

            if (profile.applyOwnerRecoil && source != null && source.IsOwnerClient)
            {
                PlayerEntityPresentationFeatureBridge bridge = source.GetComponent<PlayerEntityPresentationFeatureBridge>();
                bridge?.RecoilSway?.AddRecoil(
                    profile.recoilPitch,
                    profile.recoilYaw,
                    profile.recoilRoll,
                    profile.weaponKick);
            }
        }

        private void SpawnVisibleProjectile(
            PlayerEntityClient source,
            PlayerEntityClient target,
            CombatPresentationProfile profile)
        {
            if (source == null || target == null ||
                source.PresentationTransform == null || target.PresentationTransform == null ||
                profile.visibleProjectilePrefab == null)
                return;

            GameObject instance = Instantiate(
                profile.visibleProjectilePrefab,
                source.PresentationTransform.position,
                Quaternion.identity);
            ClientVisibleProjectilePresentation mover = instance.GetComponent<ClientVisibleProjectilePresentation>();
            if (mover == null)
                mover = instance.AddComponent<ClientVisibleProjectilePresentation>();
            mover.Initialize(target.PresentationTransform, profile.projectileSpeed, profile.projectileMaximumLifetime);
        }

        private void ApplyStatusLoop(PlayerEntityClient source, CombatPresentationCueWire cue, CombatPresentationProfile profile)
        {
            if (source == null || source.PresentationTransform == null || profile.statusLoopPrefab == null)
                return;
            ulong key = StatusKey(source, cue.semanticId);
            if (_statusLoops.TryGetValue(key, out GameObject existing) && existing != null)
                return;
            GameObject loop = Instantiate(profile.statusLoopPrefab, source.PresentationTransform);
            _statusLoops[key] = loop;
        }

        private void RemoveStatusLoop(PlayerEntityClient source, ushort semanticId)
        {
            if (source == null)
                return;
            ulong key = StatusKey(source, semanticId);
            if (!_statusLoops.TryGetValue(key, out GameObject loop))
                return;
            _statusLoops.Remove(key);
            if (loop != null) Destroy(loop);
        }

        private static ulong StatusKey(PlayerEntityClient source, ushort semanticId)
        {
            uint objectId = source.NetworkBridge != null ? source.NetworkBridge.ObjectId : 0;
            ushort generation = source.NetworkBridge != null ? source.NetworkBridge.Generation : (ushort)0;
            return ((ulong)objectId << 32) | ((ulong)generation << 16) | semanticId;
        }

        private static void Trigger(PlayerEntityClient client, string trigger)
        {
            if (client == null || client.PresentationTransform == null || string.IsNullOrWhiteSpace(trigger))
                return;
            Animator animator = client.PresentationTransform.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.SetTrigger(trigger);
        }

        private static void SpawnFx(GameObject prefab, Transform anchor)
        {
            if (prefab == null || anchor == null)
                return;
            Instantiate(prefab, anchor.position, anchor.rotation);
        }

        private static PlayerEntityClient Resolve(PlayerTargetReferenceWire reference)
        {
            if (!reference.IsValid)
                return null;
            var active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
            {
                PlayerEntityClient client = active[i];
                if (client?.NetworkBridge == null)
                    continue;
                if (client.NetworkBridge.ObjectId == reference.objectId &&
                    client.NetworkBridge.Generation == reference.generation)
                    return client;
            }
            return null;
        }

        private static bool TryResolveOwner(out PlayerEntityClient owner)
        {
            var active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
            {
                if (active[i] != null && active[i].IsOwnerClient)
                {
                    owner = active[i];
                    return true;
                }
            }
            owner = null;
            return false;
        }
    }

    internal static class PlayerCombatPresentationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindFirstObjectByType<PlayerCombatPresentationRouter>() != null)
                return;
            var go = new GameObject("Client Combat Presentation Router");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerCombatPresentationRouter>();
        }
    }
}
