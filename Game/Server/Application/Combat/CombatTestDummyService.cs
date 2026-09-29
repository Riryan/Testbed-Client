using System;
using System.Collections.Generic;
using System.Threading;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.Stats;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;

namespace Game.Server.Application.Combat
{
    public enum CombatTestDummyPreset : byte
    {
        NormalDefense = 0,
        ConductivePlate = 1,
        PoisonResistant = 2,
        PoisonImmune = 3,
        FireWeak = 4,
        Invulnerable = 5,
    }

    public readonly struct CombatTestDummyView
    {
        public long StableId { get; }
        public string Label { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public PlayerRuntime Runtime { get; }
        public CombatTestDummyPreset Preset { get; }

        public CombatTestDummyView(long stableId, string label, string mapId, string instanceId, PlayerRuntime runtime, CombatTestDummyPreset preset)
        {
            StableId = stableId;
            Label = label ?? string.Empty;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Runtime = runtime;
            Preset = preset;
        }
    }

    /// <summary>
    /// Authoritative runtimes for baked scene-placeable combat fixtures. A dummy deliberately
    /// uses the normal PlayerRuntime combat/resource/status pipeline, but is never a player session.
    /// </summary>
    public sealed class CombatTestDummyService
    {
        private sealed class Entry
        {
            public long StableId;
            public string Label = string.Empty;
            public string MapId = string.Empty;
            public string InstanceId = string.Empty;
            public ServerCombatTestDummyDefinition Definition;
            public PlayerRuntime Runtime;
            public CombatTestDummyPreset Preset;
        }

        private readonly Dictionary<string, Entry> _byWorldId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<PlayerRuntime, Entry> _byRuntime = new Dictionary<PlayerRuntime, Entry>();
        private readonly Dictionary<long, PlayerRuntime> _byCharacterId = new Dictionary<long, PlayerRuntime>();
        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly StatusEffectService _statuses;
        private long _nextSyntheticId = 8_000_000_000_000_000_000L;

        public CombatTestDummyService(
            ServerMapCatalog maps,
            GameplayContentCatalog content,
            CharacterResourceService resources,
            StatusEffectService statuses)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
            if (maps == null)
                return;

            foreach (ServerMapSnapshot map in maps.Snapshots)
            {
                if (map == null)
                    continue;
                ServerWorldInteractableDefinition[] interactables = map.interactables ?? Array.Empty<ServerWorldInteractableDefinition>();
                for (int i = 0; i < interactables.Length; ++i)
                {
                    ServerWorldInteractableDefinition source = interactables[i];
                    ServerCombatTestDummyDefinition definition = source?.combatTestDummy;
                    if (source == null || definition == null || !definition.enabled || source.stableId <= 0)
                        continue;

                    var entry = new Entry
                    {
                        StableId = source.stableId,
                        Label = string.IsNullOrWhiteSpace(source.label) ? "Combat Test Dummy" : source.label,
                        MapId = map.mapId ?? string.Empty,
                        InstanceId = map.instanceId ?? string.Empty,
                        Definition = definition,
                        Preset = CombatTestDummyPreset.NormalDefense,
                    };
                    entry.Runtime = CreateRuntime(entry, source.pose);
                    _byWorldId[Key(entry.MapId, entry.InstanceId, entry.StableId)] = entry;
                    _byRuntime[entry.Runtime] = entry;
                    _byCharacterId[entry.Runtime.CharacterId.Value] = entry.Runtime;
                }
            }
        }

        public int Count => _byRuntime.Count;

        public IEnumerable<PlayerRuntime> Runtimes
        {
            get
            {
                foreach (Entry entry in _byRuntime.Values)
                    yield return entry.Runtime;
            }
        }

        public bool TryGet(string mapId, string instanceId, long stableId, out CombatTestDummyView view)
        {
            if (_byWorldId.TryGetValue(Key(mapId, instanceId, stableId), out Entry entry))
            {
                view = ToView(entry);
                return true;
            }
            view = default;
            return false;
        }

        public bool TryGetByRuntime(PlayerRuntime runtime, out CombatTestDummyView view)
        {
            if (runtime != null && _byRuntime.TryGetValue(runtime, out Entry entry))
            {
                view = ToView(entry);
                return true;
            }
            view = default;
            return false;
        }

        public PlayerRuntime ResolveRuntime(long characterId) =>
            _byCharacterId.TryGetValue(characterId, out PlayerRuntime runtime) ? runtime : null;

        public string ExecuteDeveloperAction(CombatTestDummyView view, InteractionActionId actionId, double now, out bool success)
        {
            success = false;
            if (view.Runtime == null || !_byRuntime.TryGetValue(view.Runtime, out Entry entry))
                return "combat test dummy is unavailable";

            switch (actionId)
            {
                case InteractionActionId.CombatDummyResetHealth:
                    success = ResetHealth(entry.Runtime);
                    return success ? "health reset to authoritative maximum" : "health reset failed";
                case InteractionActionId.CombatDummyClearStatuses:
                    int removed = ClearStatuses(entry.Runtime);
                    success = true;
                    return $"cleared {removed} active status effect(s)";
                case InteractionActionId.CombatDummyNormalDefense:
                    ApplyPreset(entry, CombatTestDummyPreset.NormalDefense);
                    success = true;
                    return "preset: Normal Defense";
                case InteractionActionId.CombatDummyConductivePlate:
                    ApplyPreset(entry, CombatTestDummyPreset.ConductivePlate);
                    success = true;
                    return "preset: Conductive Plate (Electric x1.75)";
                case InteractionActionId.CombatDummyPoisonResistant:
                    ApplyPreset(entry, CombatTestDummyPreset.PoisonResistant);
                    success = true;
                    return "preset: Poison Resistant (Poison x0.50)";
                case InteractionActionId.CombatDummyPoisonImmune:
                    ApplyPreset(entry, CombatTestDummyPreset.PoisonImmune);
                    success = true;
                    return "preset: Poison Immune (Poison x0.00)";
                case InteractionActionId.CombatDummyFireWeak:
                    ApplyPreset(entry, CombatTestDummyPreset.FireWeak);
                    success = true;
                    return "preset: Fire Weak (Fire x1.50)";
                case InteractionActionId.CombatDummyInvulnerable:
                    ApplyPreset(entry, CombatTestDummyPreset.Invulnerable);
                    success = true;
                    return "preset: Invulnerable";
                case InteractionActionId.CombatDummyShowStats:
                    success = true;
                    return Describe(entry);
                default:
                    return "unsupported combat test dummy action";
            }
        }

        public void HandleKilled(PlayerRuntime runtime, CombatDamageResult result)
        {
            if (runtime == null || !_byRuntime.TryGetValue(runtime, out Entry entry) ||
                entry.Definition == null || !entry.Definition.resetOnDefeat)
                return;
            ResetHealth(runtime);
        }

        private PlayerRuntime CreateRuntime(Entry entry, ServerPose pose)
        {
            long ordinal = Interlocked.Increment(ref _nextSyntheticId);
            long accountValue = 7_000_000_000_000_000_000L + (ordinal - 8_000_000_000_000_000_000L);
            var runtime = new PlayerRuntime(
                new AccountId(accountValue),
                new CharacterId(ordinal),
                PlayerSessionId.New(),
                new CharacterState(entry.Label),
                new CharacterLocationState(entry.MapId, entry.InstanceId, pose.ToWorldPosition(), pose.yaw),
                0);

            StatsState stats = BuildStats(entry, CombatTestDummyPreset.NormalDefense);
            runtime.InitializePlayerItemSystems(new InventoryState(1, 0), new EquipmentState(0), stats);
            runtime.InitializeCharacterResources(_resources.CreateInitialState(stats, 0, null));
            return runtime;
        }

        private void ApplyPreset(Entry entry, CombatTestDummyPreset preset)
        {
            if (entry?.Runtime == null)
                return;
            entry.Preset = preset;
            entry.Runtime.SetInvincible(preset == CombatTestDummyPreset.Invulnerable);
            entry.Runtime.TryReplaceCalculatedStats(0, BuildStats(entry, preset));
        }

        private StatsState BuildStats(Entry entry, CombatTestDummyPreset preset)
        {
            int health = Math.Max(1, entry?.Definition?.healthMaximum ?? 500);
            float armor = Math.Max(0f, entry?.Definition?.armor ?? 0f);
            var values = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Health.Max", health),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", armor),
                new KeyValuePair<string, float>("AttackPower", 0f),
            };

            AddResponse(values, entry?.Definition?.electricDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.ConductivePlate ? 1.75f : 1f);
            AddResponse(values, entry?.Definition?.poisonDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.PoisonResistant ? 0.50f :
                preset == CombatTestDummyPreset.PoisonImmune ? 0f : 1f);
            AddResponse(values, entry?.Definition?.fireDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.FireWeak ? 1.50f : 1f);
            return new StatsState(values);
        }

        private void AddResponse(List<KeyValuePair<string, float>> values, string damageTypeDefinitionId, float multiplier)
        {
            if (values == null || string.IsNullOrWhiteSpace(damageTypeDefinitionId) ||
                !_content.TryGetDamageType(damageTypeDefinitionId.Trim(), out DamageTypeDefinition type) || type == null || type.wireId == 0)
                return;
            values.Add(new KeyValuePair<string, float>(CombatDerivedStatIds.DamageResponseMultiplier(type.wireId), multiplier));
        }

        private bool ResetHealth(PlayerRuntime runtime)
        {
            if (runtime == null || !runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health))
                return false;
            return _resources.Set(runtime, CharacterResourceId.Health, health.Maximum, CharacterResourceChangeReason.Administrative).Success;
        }

        private int ClearStatuses(PlayerRuntime runtime)
        {
            if (runtime == null)
                return 0;
            int removed = 0;
            StatusEffectInstanceState[] active = runtime.CaptureStatusEffects().Snapshot();
            for (int i = 0; i < active.Length; ++i)
            {
                if (_statuses.Remove(runtime, active[i].DefinitionId, StatusEffectChangeReason.Administrative).Success)
                    removed++;
            }
            return removed;
        }

        private string Describe(Entry entry)
        {
            PlayerRuntime runtime = entry.Runtime;
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health);
            int statuses = runtime.CaptureStatusEffects().Snapshot().Length;
            return $"{entry.Label}: HP {health.Current}/{health.Maximum}, Armor {runtime.GetStat("Armor", 0f):0.##}, " +
                   $"Fire x{GetResponse(runtime, entry.Definition?.fireDamageTypeDefinitionId):0.##}, " +
                   $"Electric x{GetResponse(runtime, entry.Definition?.electricDamageTypeDefinitionId):0.##}, " +
                   $"Poison x{GetResponse(runtime, entry.Definition?.poisonDamageTypeDefinitionId):0.##}, " +
                   $"Invincible={runtime.Combat.Invincible}, Statuses={statuses}";
        }

        private float GetResponse(PlayerRuntime runtime, string damageTypeDefinitionId)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(damageTypeDefinitionId) ||
                !_content.TryGetDamageType(damageTypeDefinitionId.Trim(), out DamageTypeDefinition type) || type == null || type.wireId == 0)
                return 1f;
            return runtime.GetStat(CombatDerivedStatIds.DamageResponseMultiplier(type.wireId), 1f);
        }

        private static CombatTestDummyView ToView(Entry entry) =>
            new CombatTestDummyView(entry.StableId, entry.Label, entry.MapId, entry.InstanceId, entry.Runtime, entry.Preset);

        private static string Key(string mapId, string instanceId, long stableId) =>
            ServerMapId.Normalize(mapId) + "\n" + (instanceId ?? string.Empty).Trim() + "\n" + stableId;
    }
}
