using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Characters;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Application.Combat;
using Game.Server.Application.Abilities;
using Game.Server.Application.Effects;
using Game.Server.Application.Interactions;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Application.Progression;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.Combat;
using Game.Shared.Abilities;
using Game.Shared.Interactions;
using Game.Shared.Sessions;
using Game.Shared.World;
using Game.UnityIntegration.Backend;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Shared.Characters;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Unity-headless composition root. Live gameplay/session state remains in the
    /// game server, while durable account/character data is accessed only through
    /// the standalone backend. Unity no longer opens the SQLite database directly.
    /// </summary>
    public sealed class CharacterSessionRuntimeHost : IDisposable
    {
        private readonly LiteNetLibGameManager _manager;
        private readonly CancellationTokenSource _backendEventsCancellation = new CancellationTokenSource();
        private Task _backendEventsTask;
        private long _pendingGameplayContentRevision;
        private int _backendEventStreamAvailable;
        private CharacterResourceRuntimeScheduler _resourceScheduler;
        private CharacterCombatStateScheduler _combatScheduler;
        private CharacterStatusEffectRuntimeScheduler _statusScheduler;
        private CharacterAbilityCastScheduler _abilityScheduler;
        private readonly object _resourceBindingGate = new object();
        private readonly System.Collections.Generic.Dictionary<PlayerRuntime, PlayerSessionHandle> _resourceBindings =
            new System.Collections.Generic.Dictionary<PlayerRuntime, PlayerSessionHandle>();
        private readonly System.Collections.Generic.Dictionary<CharacterId, PlayerSessionHandle> _resourceBindingsByCharacter =
            new System.Collections.Generic.Dictionary<CharacterId, PlayerSessionHandle>();

        public BackendInternalClient Backend { get; }
        public GameplayContentCatalog Content { get; }
        public ProgressionService Progression { get; }
        public IPlayerSystemsRepository PlayerSystemsRepository { get; }
        public PlayerItemService PlayerItems { get; }
        public CharacterResourceService Resources { get; }
        public StatusEffectService StatusEffects { get; }
        public CombatService Combat { get; }
        public GameplayEffectService Effects { get; }
        public CombatLoadoutService CombatLoadout { get; }
        public CombatReloadService Reloads { get; }
        public BasicAttackService BasicAttacks { get; }
        public AbilityService Abilities { get; }
        public InteractionService Interactions { get; }
        public event Action<PlayerSessionHandle, CharacterResourceChangeView> PlayerResourceChanged;
        public event Action<PlayerSessionHandle, StatusEffectChangeView> PlayerStatusEffectChanged;
        public event Action<PlayerSessionHandle, Game.Shared.Protocol.PlayerItemsSnapshot, Game.Shared.Protocol.PlayerItemsSnapshot> PlayerItemsChanged;
        public event Action<CombatDamageResult> CombatDamageResolved;
        public event Action<CombatHealingResult> CombatHealingResolved;
        public event Action<BasicAttackResult> BasicAttackResolved;
        public event Action<AbilityCastResult> AbilityCastStateChanged;
        public event Action<InteractionResult> InteractionResolved;
        public event Action<bool, string> BackendEventStreamAvailabilityChanged;
        public ICharacterRepository Repository { get; }
        public InMemoryCharacterLeaseService Leases { get; }
        public DirtyPlayerTracker DirtyPlayers { get; }
        public CharacterSaveService Saves { get; }
        public PlayerSessionRegistry Sessions { get; }
        public PlayerSessionService SessionService { get; }
        public PlayerWorldBindingRegistry WorldBindings { get; }
        public PlayerWorldLifecycleService WorldLifecycle { get; }
        public CanonicalPlayerSessionBridge CanonicalBridge { get; }

        public CharacterSessionRuntimeHost(
            LiteNetLibGameManager manager,
            string backendInternalUrl,
            string gameServerKey,
            int maxPendingWorldCommands = 4096)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));

            _manager = manager;
            Backend = new BackendInternalClient(
                backendInternalUrl,
                gameServerKey,
                TimeSpan.FromSeconds(10));

            // Content is a required server dependency. Startup fails closed if the backend
            // cannot provide one fully validated definition revision.
            var contentResponse = Backend.GetGameplayContentAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            if (contentResponse == null || !contentResponse.success || contentResponse.content == null)
                throw new InvalidOperationException("Backend gameplay content is unavailable.");

            Content = new GameplayContentCatalog(contentResponse.content);
            Progression = new ProgressionService(Content);
            PlayerSystemsRepository = new BackendPlayerSystemsRepository(Backend);
            Resources = new CharacterResourceService(Content);
            StatusEffects = new StatusEffectService(Content);
            PlayerItems = new PlayerItemService(
                Content,
                PlayerSystemsRepository,
                PlayerSystemsRepository as IPlayerItemLifecycleRepository,
                Resources,
                StatusEffects);
            Combat = new CombatService(Content, Resources, StatusEffects);
            Effects = new GameplayEffectService(Content, Resources, Combat, StatusEffects);
            CombatLoadout = new CombatLoadoutService(Content);
            Reloads = new CombatReloadService(Content, PlayerItems, CombatLoadout);
            BasicAttacks = new BasicAttackService(Content, Combat, Resources, CombatLoadout);
            Abilities = new AbilityService(Content, Resources, Combat, StatusEffects, Effects, Progression);
            Interactions = new InteractionService();
            Combat.DamageResolved += OnCombatDamageResolved;
            Combat.HealingResolved += OnCombatHealingResolved;
            BasicAttacks.Resolved += OnBasicAttackResolved;
            Abilities.CastStateChanged += OnAbilityCastStateChanged;
            Interactions.Resolved += OnInteractionResolved;
            PlayerItems.Changed += OnPlayerItemsChanged;
            Repository = new BackendCharacterRepository(Backend, PlayerSystemsRepository);
            Leases = new InMemoryCharacterLeaseService();
            DirtyPlayers = new DirtyPlayerTracker();
            Saves = new CharacterSaveService(Repository);
            Sessions = new PlayerSessionRegistry();

            var characterService = new CharacterService(
                Repository,
                new CharacterValidator(),
                new CharacterRuntimeFactory(Content));

            SessionService = new PlayerSessionService(
                Sessions,
                characterService,
                Leases,
                DirtyPlayers);

            WorldBindings = new PlayerWorldBindingRegistry();
            WorldLifecycle = new PlayerWorldLifecycleService(
                SessionService,
                WorldBindings,
                new ExternalHostPlayerWorldAdapter(),
                maxPendingWorldCommands);

            CanonicalBridge = new CanonicalPlayerSessionBridge(
                manager,
                SessionService,
                WorldLifecycle,
                WorldBindings);

            _backendEventsTask = RunBackendEventsAsync(_backendEventsCancellation.Token);
        }

        public PlayerSession OpenConnection(long connectionId) =>
            SessionService.Open(new ConnectionKey(connectionId));

        public bool TryGetSession(long connectionId, out PlayerSession session) =>
            SessionService.TryGetSession(new ConnectionKey(connectionId), out session);

        /// <summary>
        /// Returns the compact authoritative visual recipe for the character currently
        /// attached to this exact session. This keeps Player.Networking from depending
        /// directly on Game.Server.Domain.PlayerRuntime while still allowing the Unity
        /// hosted PlayerEntity bridge to publish server-owned appearance state.
        /// </summary>
        public bool TryGetAuthoritativeCharacterAppearance(
            PlayerSessionHandle handle,
            out long characterId,
            out string displayName,
            out CharacterAppearanceRecipe appearance,
            out CharacterPresentationPreferences presentation)
        {
            characterId = 0;
            displayName = string.Empty;
            appearance = CharacterAppearanceRecipe.CreateDefault();
            presentation = CharacterPresentationPreferences.CreateDefault();

            if (!handle.IsValid ||
                !SessionService.TryGetSession(handle, out PlayerSession session) ||
                session.Runtime == null ||
                !session.Runtime.CharacterId.IsValid)
            {
                return false;
            }

            PlayerRuntime runtime = session.Runtime;
            characterId = runtime.CharacterId.Value;
            displayName = runtime.Character?.Name ?? string.Empty;
            appearance = runtime.CaptureAppearance();
            presentation = runtime.CapturePresentationPreferences();
            return true;
        }

        /// <summary>
        /// Client-facing display identity lookup kept behind the Unity integration
        /// boundary so Player.Networking does not acquire a Game.Server.Domain reference.
        /// </summary>
        public string GetPlayerCharacterName(PlayerSessionHandle handle)
        {
            if (!handle.IsValid ||
                !SessionService.TryGetSession(handle, out PlayerSession session) ||
                session.Runtime == null ||
                session.Runtime.Character == null)
            {
                return string.Empty;
            }

            return session.Runtime.Character.Name ?? string.Empty;
        }

        public Task<AccountId> RedeemAdmissionAsync(
            string token,
            CancellationToken cancellationToken) =>
            Backend.RedeemAdmissionAsync(token, cancellationToken);

        /// <summary>
        /// True when the currently supported character checkpoint stream has work
        /// waiting to be persisted. Domain dirty-flag/runtime details stay behind
        /// the Unity integration boundary instead of leaking into networking.
        /// </summary>
        public bool HasPendingCharacterCheckpoints =>
            DirtyPlayers.GetBatch(1, CharacterSaveService.SupportedDirtyFlags).Length > 0;

        /// <summary>
        /// Persists one batch of the currently supported character checkpoint stream.
        /// </summary>
        public Task<int> SaveCharacterCheckpointBatchAsync(
            int maxCount,
            CancellationToken cancellationToken) =>
            Saves.SaveDirtyBatchAsync(DirtyPlayers, maxCount, cancellationToken);

        public Task<CharacterCreateResult> CreateCharacterAsync(
            PlayerSessionHandle handle,
            string name,
            CancellationToken cancellationToken) =>
            CreateCharacterAsync(handle, name, CharacterAppearanceRecipe.CreateDefault(), CharacterPresentationPreferences.CreateDefault(), cancellationToken);

        public Task<CharacterCreateResult> CreateCharacterAsync(
            PlayerSessionHandle handle,
            string name,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            CancellationToken cancellationToken)
        {
            // Character creation must not consume LiteNetLibAssets' rotating/random
            // SpawnPlayer selector. Persist the center of a scene spawn point as the
            // initial location; canonical SpawnPlayer still owns actual network spawning.
            LiteNetLibSpawnPoint spawnPoint = UnityEngine.Object.FindFirstObjectByType<LiteNetLibSpawnPoint>();
            Vector3 position = spawnPoint != null ? spawnPoint.transform.position : Vector3.zero;

            string mapId = string.Empty;
            if (_manager.ServerSceneInfo.HasValue)
                mapId = _manager.ServerSceneInfo.Value.sceneName;
            if (string.IsNullOrWhiteSpace(mapId))
                mapId = SceneManager.GetActiveScene().name;
            if (string.IsNullOrWhiteSpace(mapId))
                mapId = "World";

            var initialLocation = new CharacterLocationState(
                mapId,
                string.Empty,
                new WorldPosition(position.x, position.y, position.z),
                0f);

            CharacterAppearanceRecipe appearance = initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            if (!appearance.IsValid(out _))
                appearance = CharacterAppearanceRecipe.CreateDefault();

            CharacterPresentationPreferences presentation =
                initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            if (!presentation.IsValid(out _))
                presentation = CharacterPresentationPreferences.CreateDefault();

            return SessionService.CreateCharacterAsync(
                handle,
                name,
                initialLocation,
                appearance,
                presentation,
                cancellationToken);
        }

        public bool TryBeginDisconnectBeforeCanonicalDestroy(
            long connectionId,
            out PlayerSessionHandle handle)
        {
            handle = default(PlayerSessionHandle);
            if (!TryGetSession(connectionId, out PlayerSession session))
                return false;

            handle = session.Handle;
            return TryBeginDisconnectBeforeCanonicalDestroy(handle);
        }

        public bool TryBeginDisconnectBeforeCanonicalDestroy(PlayerSessionHandle handle)
        {
            if (!handle.IsValid ||
                !SessionService.TryGetSession(handle, out PlayerSession session))
            {
                return false;
            }

            PlayerSessionState state = session.State;
            if (state == PlayerSessionState.Closed)
                return false;

            if (state != PlayerSessionState.Disconnecting &&
                !SessionService.BeginDisconnect(handle))
            {
                return false;
            }

            CanonicalBridge.TryCompleteLeaveBeforeCanonicalDestroy(handle, out _);
            return true;
        }

        public async Task<bool> SaveAndCloseDisconnectedSessionAsync(
            PlayerSessionHandle handle,
            CancellationToken cancellationToken)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) ||
                session.State != PlayerSessionState.Disconnecting)
            {
                return false;
            }

            PlayerRuntime runtime = session.Runtime;
            if (runtime != null &&
                (runtime.DirtyFlags & CharacterSaveService.SupportedDirtyFlags) != 0)
            {
                await Saves.SaveAsync(runtime, cancellationToken)
                    .ConfigureAwait(false);
            }

            return SessionService.Close(handle);
        }

        /// <summary>
        /// Returns true only when the exact session generation is in-world and owns
        /// a loaded runtime. Domain details stay behind this integration boundary.
        /// </summary>
        public bool HasInWorldRuntime(PlayerSessionHandle handle)
        {
            return handle.IsValid &&
                   SessionService.TryGetSession(handle, out PlayerSession session) &&
                   session.State == Game.Shared.Sessions.PlayerSessionState.InWorld &&
                   session.Runtime != null;
        }

        public void StartGameplayScheduling()
        {
            if (_resourceScheduler == null)
            {
                _resourceScheduler = new CharacterResourceRuntimeScheduler(
                    _manager.CoreScheduler,
                    Resources,
                    runtime => Combat.IsInCombat(runtime, _manager.CoreScheduler.ServerTime));
            }
            if (_combatScheduler == null)
                _combatScheduler = new CharacterCombatStateScheduler(_manager.CoreScheduler, Combat, _resourceScheduler);
            if (_statusScheduler == null)
                _statusScheduler = new CharacterStatusEffectRuntimeScheduler(
                    _manager.CoreScheduler,
                    StatusEffects,
                    Effects,
                    ResolveActivePlayerRuntime);
            if (_abilityScheduler == null)
                _abilityScheduler = new CharacterAbilityCastScheduler(_manager.CoreScheduler, Abilities, ResolveActivePlayerRuntime);
        }

        public void StopGameplayScheduling()
        {
            _abilityScheduler?.Dispose();
            _abilityScheduler = null;
            _statusScheduler?.Dispose();
            _statusScheduler = null;
            _combatScheduler?.Dispose();
            _combatScheduler = null;
            _resourceScheduler?.Dispose();
            _resourceScheduler = null;
        }

        public bool ActivatePlayerGameplayRuntime(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return false;

            PlayerRuntime runtime = session.Runtime;
            lock (_resourceBindingGate)
            {
                if (!_resourceBindings.ContainsKey(runtime))
                {
                    _resourceBindings.Add(runtime, handle);
                    _resourceBindingsByCharacter[runtime.CharacterId] = handle;
                    runtime.ResourceChanged += OnRuntimeResourceChanged;
                    runtime.StatusEffectChanged += OnRuntimeStatusEffectChanged;
                }
            }
            _resourceScheduler?.Activate(runtime);
            _combatScheduler?.Activate(runtime);
            _statusScheduler?.Activate(runtime);
            _abilityScheduler?.Activate(runtime);
            return true;
        }

        public void DeactivatePlayerGameplayRuntime(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return;
            PlayerRuntime runtime = session.Runtime;
            _abilityScheduler?.Deactivate(runtime);
            _statusScheduler?.Deactivate(runtime);
            _combatScheduler?.Deactivate(runtime);
            _resourceScheduler?.Deactivate(runtime);
            lock (_resourceBindingGate)
            {
                if (_resourceBindings.Remove(runtime))
                {
                    _resourceBindingsByCharacter.Remove(runtime.CharacterId);
                    runtime.ResourceChanged -= OnRuntimeResourceChanged;
                    runtime.StatusEffectChanged -= OnRuntimeStatusEffectChanged;
                }
            }
        }

        public CharacterResourcesSnapshot GetPlayerResources(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return null;
            return Resources.GetSnapshot(session.Runtime);
        }

        public CharacterResourceOperationResult AddResource(PlayerSessionHandle handle, CharacterResourceId id, int amount, CharacterResourceChangeReason reason)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return CharacterResourceOperationResult.Failed(CharacterResourceOperationStatus.SessionUnavailable, "session runtime unavailable");
            return Resources.Add(session.Runtime, id, amount, reason);
        }

        public CharacterResourceOperationResult RemoveResource(PlayerSessionHandle handle, CharacterResourceId id, int amount, CharacterResourceChangeReason reason)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return CharacterResourceOperationResult.Failed(CharacterResourceOperationStatus.SessionUnavailable, "session runtime unavailable");
            return Resources.Remove(session.Runtime, id, amount, reason);
        }

        public CharacterResourceOperationResult SpendResource(PlayerSessionHandle handle, CharacterResourceId id, int amount, CharacterResourceChangeReason reason = CharacterResourceChangeReason.AbilityCost)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return CharacterResourceOperationResult.Failed(CharacterResourceOperationStatus.SessionUnavailable, "session runtime unavailable");
            return Resources.Spend(session.Runtime, id, amount, reason);
        }

        public CharacterResourceOperationResult SetResource(PlayerSessionHandle handle, CharacterResourceId id, int value, CharacterResourceChangeReason reason)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return CharacterResourceOperationResult.Failed(CharacterResourceOperationStatus.SessionUnavailable, "session runtime unavailable");
            return Resources.Set(session.Runtime, id, value, reason);
        }

        public int ApplyPlayerDeathResourceResets(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return 0;
            return Resources.ApplyDeathResets(session.Runtime);
        }

        public int ApplyPlayerRespawnResourceResets(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return 0;
            return Resources.ApplyRespawnResets(session.Runtime);
        }

        private void OnRuntimeResourceChanged(CharacterResourceChange change)
        {
            if (!Resources.ShouldReplicateToOwner(change.ResourceId))
                return;

            PlayerSessionHandle handle;
            lock (_resourceBindingGate)
            {
                if (!_resourceBindingsByCharacter.TryGetValue(change.CharacterId, out handle))
                    return;
            }
            PlayerResourceChanged?.Invoke(
                handle,
                new CharacterResourceChangeView(
                    change.Revision,
                    change.ResourceId,
                    change.Previous,
                    change.Current,
                    change.Minimum,
                    change.Maximum,
                    change.Reason));
        }

        public StatusEffectsSnapshot GetPlayerStatusEffects(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return null;
            return StatusEffects.GetSnapshot(session.Runtime);
        }

        public StatusEffectOperationResult ApplyStatusEffect(
            PlayerSessionHandle handle,
            string definitionId,
            long sourceCharacterIdValue,
            int stacks,
            StatusEffectChangeReason reason)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return StatusEffectOperationResult.Failed(StatusEffectOperationStatus.SessionUnavailable, "session runtime unavailable");
            return StatusEffects.Apply(
                session.Runtime,
                definitionId,
                sourceCharacterIdValue,
                stacks,
                _manager.CoreScheduler.ServerTime,
                reason);
        }

        public StatusEffectOperationResult RemoveStatusEffect(
            PlayerSessionHandle handle,
            string definitionId,
            StatusEffectChangeReason reason)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return StatusEffectOperationResult.Failed(StatusEffectOperationStatus.SessionUnavailable, "session runtime unavailable");
            return StatusEffects.Remove(session.Runtime, definitionId, reason);
        }

        public CombatDamageResult ApplyCombatDamage(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle,
            int amount,
            CombatDamageCause cause = CombatDamageCause.Combat)
        {
            PlayerRuntime source = null;
            if (sourceHandle.IsValid && SessionService.TryGetSession(sourceHandle, out PlayerSession sourceSession))
                source = sourceSession.Runtime;
            if (!SessionService.TryGetSession(targetHandle, out PlayerSession targetSession) || targetSession.Runtime == null)
                return Combat.ApplyStandardDamage(source, null, amount, _manager.CoreScheduler.ServerTime, cause);
            return Combat.ApplyStandardDamage(source, targetSession.Runtime, amount, _manager.CoreScheduler.ServerTime, cause);
        }

        public CombatHealingResult ApplyCombatHealing(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle,
            int amount)
        {
            PlayerRuntime source = null;
            if (sourceHandle.IsValid && SessionService.TryGetSession(sourceHandle, out PlayerSession sourceSession))
                source = sourceSession.Runtime;
            PlayerRuntime target = null;
            if (targetHandle.IsValid && SessionService.TryGetSession(targetHandle, out PlayerSession targetSession))
                target = targetSession.Runtime;
            return Combat.ApplyHealing(source, target, amount, _manager.CoreScheduler.ServerTime);
        }

        public BasicAttackResult TryPlayerBasicAttack(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle) =>
            TryPlayerBasicAttack(sourceHandle, targetHandle, BasicAttackInputKind.Light);

        public BasicAttackResult TryPlayerBasicAttack(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle,
            BasicAttackInputKind inputKind)
        {
            PlayerRuntime source = ResolveActivePlayerRuntime(sourceHandle);
            PlayerRuntime target = targetHandle.IsValid ? ResolveActivePlayerRuntime(targetHandle) : null;
            return BasicAttacks.TryAttack(source, target, inputKind, _manager.CoreScheduler.ServerTime);
        }

        public bool TryGetCombatOwnerState(PlayerSessionHandle sourceHandle, out CombatOwnerStateSnapshot state)
        {
            state = default;
            PlayerRuntime source = ResolveActivePlayerRuntime(sourceHandle);
            if (source == null)
                return false;
            state = CombatLoadout.Capture(source, _manager.CoreScheduler.ServerTime);
            return state.Available;
        }

        public AbilityCastResult BeginPlayerAbility(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle,
            string abilityDefinitionId,
            int rank,
            WorldPosition requestedPoint)
        {
            PlayerRuntime source = ResolveActivePlayerRuntime(sourceHandle);
            PlayerRuntime target = ResolveActivePlayerRuntime(targetHandle);
            return Abilities.TryBeginCast(
                source,
                target,
                abilityDefinitionId,
                rank,
                requestedPoint,
                _manager.CoreScheduler.ServerTime);
        }

        public AbilityCastResult CancelPlayerAbility(
            PlayerSessionHandle sourceHandle,
            AbilityCastFailure failure = AbilityCastFailure.Interrupted)
        {
            return Abilities.CancelCast(ResolveActivePlayerRuntime(sourceHandle), failure);
        }

        public InteractionResult ExecutePlayerInteraction(
            PlayerSessionHandle sourceHandle,
            PlayerSessionHandle targetHandle,
            InteractionActionId actionId,
            uint sequence)
        {
            return Interactions.ExecutePlayerAction(
                ResolveActivePlayerRuntime(sourceHandle),
                ResolveActivePlayerRuntime(targetHandle),
                actionId,
                sequence,
                _manager.CoreScheduler.ServerTime);
        }

        private PlayerRuntime ResolveActivePlayerRuntime(PlayerSessionHandle handle)
        {
            if (!handle.IsValid ||
                !SessionService.TryGetSession(handle, out PlayerSession session) ||
                session.State != PlayerSessionState.InWorld)
                return null;
            return session.Runtime;
        }

        private PlayerRuntime ResolveActivePlayerRuntime(long characterIdValue)
        {
            if (characterIdValue <= 0)
                return null;
            PlayerSessionHandle handle;
            lock (_resourceBindingGate)
            {
                if (!_resourceBindingsByCharacter.TryGetValue(new CharacterId(characterIdValue), out handle))
                    return null;
            }
            return ResolveActivePlayerRuntime(handle);
        }

        private void OnBasicAttackResolved(BasicAttackResult result) =>
            BasicAttackResolved?.Invoke(result);

        private void OnAbilityCastStateChanged(AbilityCastResult result) =>
            AbilityCastStateChanged?.Invoke(result);

        private void OnInteractionResolved(InteractionResult result) =>
            InteractionResolved?.Invoke(result);

        private void OnRuntimeStatusEffectChanged(StatusEffectChange change)
        {
            PlayerSessionHandle handle;
            lock (_resourceBindingGate)
            {
                if (!_resourceBindingsByCharacter.TryGetValue(change.CharacterId, out handle))
                    return;
            }

            string displayName = string.Empty;
            byte classification = 0;
            ushort presentationId = 0;
            if (!string.IsNullOrWhiteSpace(change.DefinitionId) &&
                Content.TryGetStatusEffect(change.DefinitionId, out StatusEffectDefinition definition))
            {
                displayName = definition.displayName ?? change.DefinitionId;
                classification = (byte)definition.classification;
                presentationId = definition.presentationId;
            }

            PlayerStatusEffectChanged?.Invoke(
                handle,
                new StatusEffectChangeView(
                    change.Revision,
                    change.Kind,
                    change.DefinitionId,
                    displayName,
                    classification,
                    change.Stacks,
                    change.EndTime,
                    change.Reason,
                    presentationId));
        }

        private void OnCombatDamageResolved(CombatDamageResult result) =>
            CombatDamageResolved?.Invoke(result);

        private void OnCombatHealingResolved(CombatHealingResult result) =>
            CombatHealingResolved?.Invoke(result);

        private void OnPlayerItemsChanged(
            PlayerRuntime runtime,
            Game.Shared.Protocol.PlayerItemsSnapshot previous,
            Game.Shared.Protocol.PlayerItemsSnapshot current)
        {
            if (runtime == null || previous == null || current == null)
                return;

            PlayerSessionHandle handle;
            lock (_resourceBindingGate)
            {
                if (!_resourceBindings.TryGetValue(runtime, out handle))
                    return;
            }
            PlayerItemsChanged?.Invoke(handle, previous, current);
        }

        public Game.Shared.Progression.CharacterProgressionState GetPlayerProgression(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return Game.Shared.Progression.CharacterProgressionState.CreateDefault();
            return Progression.Snapshot(session.Runtime);
        }

        public Game.Shared.Protocol.PlayerItemsSnapshot GetPlayerItems(PlayerSessionHandle handle)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return null;
            return PlayerItems.GetSnapshot(session.Runtime);
        }

        public Task<Game.Shared.Protocol.PlayerItemOperationResult> MoveInventoryAsync(PlayerSessionHandle handle, int fromIndex, int toIndex, CancellationToken cancellationToken)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return Task.FromResult(Game.Shared.Protocol.PlayerItemOperationResult.Failed(Game.Shared.Protocol.PlayerItemOperationStatus.SessionUnavailable, "session runtime unavailable"));
            return PlayerItems.MoveInventoryAsync(session.Runtime, fromIndex, toIndex, cancellationToken);
        }

        public Task<Game.Shared.Protocol.PlayerItemOperationResult> UseItemAsync(
            PlayerSessionHandle handle,
            int inventoryIndex,
            CancellationToken cancellationToken)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return Task.FromResult(Game.Shared.Protocol.PlayerItemOperationResult.Failed(
                    Game.Shared.Protocol.PlayerItemOperationStatus.SessionUnavailable,
                    "session runtime unavailable"));
            return PlayerItems.UseAsync(
                session.Runtime,
                inventoryIndex,
                _manager.CoreScheduler.ServerTime,
                cancellationToken);
        }

        public async Task<Game.Shared.Protocol.PlayerItemOperationResult> EquipItemAsync(PlayerSessionHandle handle, int inventoryIndex, string slotId, CancellationToken cancellationToken)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return Game.Shared.Protocol.PlayerItemOperationResult.Failed(Game.Shared.Protocol.PlayerItemOperationStatus.SessionUnavailable, "session runtime unavailable");

            Game.Shared.Protocol.PlayerItemOperationResult result =
                await PlayerItems.EquipAsync(session.Runtime, inventoryIndex, slotId, cancellationToken).ConfigureAwait(false);
            if (result.Success)
                Resources.RecalculateMaximums(session.Runtime, CharacterResourceChangeReason.Administrative);
            return result;
        }

        public async Task<Game.Shared.Protocol.PlayerItemOperationResult> UnequipItemAsync(PlayerSessionHandle handle, string slotId, int preferredInventoryIndex, CancellationToken cancellationToken)
        {
            if (!SessionService.TryGetSession(handle, out PlayerSession session) || session.Runtime == null)
                return Game.Shared.Protocol.PlayerItemOperationResult.Failed(Game.Shared.Protocol.PlayerItemOperationStatus.SessionUnavailable, "session runtime unavailable");

            Game.Shared.Protocol.PlayerItemOperationResult result =
                await PlayerItems.UnequipAsync(session.Runtime, slotId, preferredInventoryIndex, cancellationToken).ConfigureAwait(false);
            if (result.Success)
                Resources.RecalculateMaximums(session.Runtime, CharacterResourceChangeReason.Administrative);
            return result;
        }

        /// <summary>
        /// Called from the authoritative server update thread. A backend event only sets
        /// a pending revision from its background SSE reader; live runtime mutation remains
        /// on the GameServer thread.
        /// </summary>
        public bool TryGetPendingGameplayContentRevision(out long revision)
        {
            revision = Interlocked.Read(ref _pendingGameplayContentRevision);
            return revision > Content.Revision;
        }

        public void AcknowledgeGameplayContentRevision(long appliedRevision)
        {
            long observed = Interlocked.Read(ref _pendingGameplayContentRevision);
            while (observed > 0 && observed <= appliedRevision)
            {
                long original = Interlocked.CompareExchange(
                    ref _pendingGameplayContentRevision,
                    0,
                    observed);
                if (original == observed)
                    return;
                observed = original;
            }
        }

        public async Task<long> RefreshGameplayContentAsync(
            long announcedRevision,
            CancellationToken cancellationToken)
        {
            if (announcedRevision <= Content.Revision)
                return Content.Revision;

            var response = await Backend.GetGameplayContentAsync(cancellationToken);
            if (response == null || !response.success || response.content == null)
                return Content.Revision;
            if (response.content.revision <= Content.Revision)
                return Content.Revision;

            cancellationToken.ThrowIfCancellationRequested();

            if (response.content.revision <= Content.Revision)
                return Content.Revision;
            Content.Replace(response.content);
            PlayerSession[] sessions = Sessions.SnapshotSessions();
            for (int i = 0; i < sessions.Length; ++i)
                if (sessions[i]?.Runtime != null)
                {
                    PlayerItems.RecalculateStatsForContentChange(sessions[i].Runtime);
                    Resources.RecalculateMaximums(sessions[i].Runtime);
                    StatusEffects.ReconcileDefinitions(sessions[i].Runtime);
                    _resourceScheduler?.NotifyDefinitionsChanged(sessions[i].Runtime);
                    _combatScheduler?.NotifyDefinitionsChanged(sessions[i].Runtime);
                    _statusScheduler?.NotifyDefinitionsChanged(sessions[i].Runtime);
                }
            return Content.Revision;
        }

        private async Task RunBackendEventsAsync(CancellationToken cancellationToken)
        {
            int reconnectDelayMilliseconds = 1000;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Backend.RunContentRevisionEventStreamAsync(
                        revision =>
                        {
                            SetBackendEventStreamAvailability(
                                true,
                                "Backend services connected.");
                            QueueGameplayContentRevision(revision);
                        },
                        cancellationToken).ConfigureAwait(false);

                    SetBackendEventStreamAvailability(
                        false,
                        "Backend services connection ended. Reconnecting...");
                    reconnectDelayMilliseconds = 1000;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SetBackendEventStreamAvailability(
                        false,
                        "Backend services are unavailable. Persistence/content services are reconnecting.");
                    Debug.LogWarning($"[PlayerEntity] Backend event stream disconnected: {ex.Message}");
                }

                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    await Task.Delay(reconnectDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                reconnectDelayMilliseconds = Math.Min(reconnectDelayMilliseconds * 2, 10000);
            }
        }


        private void SetBackendEventStreamAvailability(bool available, string message)
        {
            int next = available ? 1 : 0;
            int previous = Interlocked.Exchange(ref _backendEventStreamAvailable, next);
            if (previous == next)
                return;

            BackendEventStreamAvailabilityChanged?.Invoke(
                available,
                message ?? string.Empty);
        }

        private void QueueGameplayContentRevision(long revision)
        {
            if (revision <= Content.Revision)
                return;

            long observed = Interlocked.Read(ref _pendingGameplayContentRevision);
            while (revision > observed)
            {
                long original = Interlocked.CompareExchange(
                    ref _pendingGameplayContentRevision,
                    revision,
                    observed);
                if (original == observed)
                    return;
                observed = original;
            }
        }

        public void Dispose()
        {
            _abilityScheduler?.Dispose();
            _abilityScheduler = null;
            _statusScheduler?.Dispose();
            _statusScheduler = null;
            _combatScheduler?.Dispose();
            _combatScheduler = null;
            _resourceScheduler?.Dispose();
            _resourceScheduler = null;
            Combat.DamageResolved -= OnCombatDamageResolved;
            Combat.HealingResolved -= OnCombatHealingResolved;
            BasicAttacks.Resolved -= OnBasicAttackResolved;
            Abilities.CastStateChanged -= OnAbilityCastStateChanged;
            Interactions.Resolved -= OnInteractionResolved;
            PlayerItems.Changed -= OnPlayerItemsChanged;
            lock (_resourceBindingGate)
            {
                foreach (PlayerRuntime runtime in _resourceBindings.Keys)
                {
                    runtime.ResourceChanged -= OnRuntimeResourceChanged;
                    runtime.StatusEffectChanged -= OnRuntimeStatusEffectChanged;
                }
                _resourceBindings.Clear();
                _resourceBindingsByCharacter.Clear();
            }
            _backendEventsCancellation.Cancel();
            try
            {
                _backendEventsTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerEntity] Backend event stream shutdown: {ex.Message}");
            }
            finally
            {
                _backendEventsCancellation.Dispose();
                Backend?.Dispose();
            }
        }
    }
}
