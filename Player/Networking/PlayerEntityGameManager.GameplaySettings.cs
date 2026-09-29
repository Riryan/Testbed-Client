using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Shared.Abilities;
using Game.Shared.Content;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Shared;
using UnityEngine;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<GameplaySettingsSnapshotMessage> GameplaySettingsSnapshotReceived;
        public event Action<GameplaySettingsDeltaMessage> GameplaySettingsDeltaReceived;

        private const uint GameplaySettingsCacheMagic = 0x53474D4D; // "MMGS" little-endian
        private const ushort GameplaySettingsCacheVersion = 2;
        private const int GameplaySettingsCacheMaxBytes = 2 * 1024 * 1024;

        private bool _gameplaySettingsReconciliationPending;
        private bool _gameplaySettingsSnapshotRequestInFlight;
        private bool _hasClientGameplaySettingsSnapshot;
        private GameplaySettingsSnapshotMessage _clientGameplaySettingsSnapshot;

        private void RegisterGameplaySettingsMessages()
        {
            RegisterRequestToServer<GameplaySettingsSnapshotRequestMessage, GameplaySettingsSnapshotMessage>(
                GameplaySettingsRequestTypes.Snapshot,
                HandleGameplaySettingsSnapshotRequest);
            RegisterClientMessage(GameplaySettingsMessageTypes.Snapshot, HandleGameplaySettingsSnapshotPush);
            RegisterClientMessage(GameplaySettingsMessageTypes.Delta, HandleGameplaySettingsDelta);
        }

        public async UniTask<GameplaySettingsSnapshotMessage> RequestGameplaySettingsAsync(
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return GameplaySettingsSnapshotMessage.Failed("client is not connected");
            if (_gameplaySettingsSnapshotRequestInFlight)
                return GameplaySettingsSnapshotMessage.Failed("gameplay settings snapshot request is already pending locally");

            _gameplaySettingsSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<GameplaySettingsSnapshotMessage> response =
                    await ClientSendRequestAsync<GameplaySettingsSnapshotRequestMessage, GameplaySettingsSnapshotMessage>(
                        GameplaySettingsRequestTypes.Snapshot,
                        new GameplaySettingsSnapshotRequestMessage
                        {
                            knownRevision = PlayerGameplaySettingsRuntime.Revision,
                        },
                        millisecondsTimeout);

                GameplaySettingsSnapshotMessage snapshot = response.IsSuccess
                    ? response.Response
                    : GameplaySettingsSnapshotMessage.Failed(
                        $"gameplay settings snapshot request failed: {response.ResponseCode}");

                if (snapshot.IsNotModified)
                {
                    _gameplaySettingsReconciliationPending = false;
                    return snapshot;
                }

                if (snapshot.success && ApplyAndCacheGameplaySettingsSnapshot(snapshot))
                {
                    _gameplaySettingsReconciliationPending = false;
                    RefreshPlayerItemPresentationFromGameplaySettings();
                    RefreshWorldItemPresentationFromGameplaySettings();
                    GameplaySettingsSnapshotReceived?.Invoke(snapshot);
                    UnityEngine.Debug.Log(
                        $"[Gameplay Settings] Snapshot revision {snapshot.revision}: " +
                        $"move={snapshot.moveSpeed:0.###}, sprint={snapshot.sprintSpeed:0.###}, " +
                        $"gravity={snapshot.gravity:0.###}, jump={snapshot.jumpSpeed:0.###}");
                }

                return snapshot;
            }
            finally
            {
                _gameplaySettingsSnapshotRequestInFlight = false;
            }
        }

        private UniTaskVoid HandleGameplaySettingsSnapshotRequest(
            RequestHandlerData handler,
            GameplaySettingsSnapshotRequestMessage request,
            RequestProceedResultDelegate<GameplaySettingsSnapshotMessage> result)
        {
            if (_characterSessionRuntimeHost == null || _characterSessionRuntimeHost.Content == null)
            {
                result(AckResponseCode.Success,
                    GameplaySettingsSnapshotMessage.Failed("gameplay settings are unavailable"));
                return default;
            }

            long currentRevision = _characterSessionRuntimeHost.Content.Revision;
            GameplaySettingsSnapshotMessage response =
                request.knownRevision > 0 && request.knownRevision == currentRevision
                    ? GameplaySettingsSnapshotMessage.NotModified(currentRevision)
                    : BuildGameplaySettingsSnapshot(_characterSessionRuntimeHost.Content);

            result(AckResponseCode.Success, response);
            return default;
        }

        private void HandleGameplaySettingsSnapshotPush(MessageHandlerData handler)
        {
            GameplaySettingsSnapshotMessage snapshot = handler.ReadMessage<GameplaySettingsSnapshotMessage>();
            if (snapshot.IsNotModified || !snapshot.success || !ApplyAndCacheGameplaySettingsSnapshot(snapshot))
                return;
            _gameplaySettingsReconciliationPending = false;
            RefreshPlayerItemPresentationFromGameplaySettings();
            RefreshWorldItemPresentationFromGameplaySettings();
            GameplaySettingsSnapshotReceived?.Invoke(snapshot);
        }

        private void HandleGameplaySettingsDelta(MessageHandlerData handler)
        {
            GameplaySettingsDeltaMessage delta = handler.ReadMessage<GameplaySettingsDeltaMessage>();

            long currentRevision = PlayerGameplaySettingsRuntime.Revision;
            if (delta.revision <= currentRevision)
                return;

            // ReliableOrdered should prevent gaps. If one is observed anyway, fail closed to
            // a full snapshot rather than applying a delta against the wrong base revision.
            if (currentRevision <= 0 || delta.revision != currentRevision + 1)
            {
                ReconcileGameplaySettingsAsync().Forget();
                return;
            }

            if (!PlayerGameplaySettingsRuntime.ApplyDelta(delta))
            {
                ReconcileGameplaySettingsAsync().Forget();
                return;
            }

            UpdateCachedGameplaySettingsAfterDelta(delta, currentRevision);
            GameplaySettingsDeltaReceived?.Invoke(delta);
            UnityEngine.Debug.Log(
                $"[Gameplay Settings] Delta revision {delta.revision}, mask={delta.ChangeMask}, " +
                $"move={PlayerGameplaySettingsRuntime.MoveSpeed:0.###}, " +
                $"sprint={PlayerGameplaySettingsRuntime.SprintSpeed:0.###}");
        }

        private async UniTaskVoid ReconcileGameplaySettingsAsync()
        {
            if (_gameplaySettingsReconciliationPending || !IsClientConnected)
                return;

            _gameplaySettingsReconciliationPending = true;
            try
            {
                await RequestGameplaySettingsAsync();
            }
            finally
            {
                _gameplaySettingsReconciliationPending = false;
            }
        }

        private async UniTask PrepareGameplaySettingsCacheForAdmissionAsync(int millisecondsTimeout)
        {
            if (IsServer || !IsClientConnected)
                return;

            RestoreClientGameplaySettingsCache();
            if (PlayerGameplaySettingsRuntime.Revision <= 0)
                return;

            // Reuse request 450 as a tiny revision probe before Ready. A current cache gets
            // a not-modified response and the standalone server records that this session
            // already owns the current public catalog. A stale/unsupported probe simply
            // falls back to the established full Settings baseline after Ready.
            await RequestGameplaySettingsAsync(Math.Min(millisecondsTimeout, 3000));
        }

        private void ResetClientGameplaySettingsRequestAdmission()
        {
            _gameplaySettingsReconciliationPending = false;
            _gameplaySettingsSnapshotRequestInFlight = false;
        }

        private void RestoreClientGameplaySettingsCache()
        {
            PlayerGameplaySettingsRuntime.Reset();
            _hasClientGameplaySettingsSnapshot = false;
            _clientGameplaySettingsSnapshot = default;

            string path = GameplaySettingsCachePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                byte[] payload;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != GameplaySettingsCacheMagic ||
                        reader.ReadUInt16() != GameplaySettingsCacheVersion ||
                        reader.ReadUInt16() != PlayerEntityProtocol.Version)
                    {
                        DeleteGameplaySettingsCache(path);
                        return;
                    }

                    int length = reader.ReadInt32();
                    if (length <= 0 || length > GameplaySettingsCacheMaxBytes ||
                        stream.Length - stream.Position != length)
                    {
                        DeleteGameplaySettingsCache(path);
                        return;
                    }

                    payload = reader.ReadBytes(length);
                    if (payload.Length != length)
                    {
                        DeleteGameplaySettingsCache(path);
                        return;
                    }
                }

                var netReader = new NetDataReader(payload);
                GameplaySettingsSnapshotMessage snapshot = default;
                snapshot.Deserialize(netReader);
                if (!snapshot.success || snapshot.revision <= 0 ||
                    !PlayerGameplaySettingsRuntime.ApplySnapshot(snapshot))
                {
                    PlayerGameplaySettingsRuntime.Reset();
                    DeleteGameplaySettingsCache(path);
                    return;
                }

                _clientGameplaySettingsSnapshot = snapshot;
                _hasClientGameplaySettingsSnapshot = true;
            }
            catch
            {
                PlayerGameplaySettingsRuntime.Reset();
                _hasClientGameplaySettingsSnapshot = false;
                _clientGameplaySettingsSnapshot = default;
                DeleteGameplaySettingsCache(path);
            }
        }

        private bool ApplyAndCacheGameplaySettingsSnapshot(GameplaySettingsSnapshotMessage snapshot)
        {
            if (!PlayerGameplaySettingsRuntime.ApplySnapshot(snapshot))
                return false;

            _clientGameplaySettingsSnapshot = snapshot;
            _hasClientGameplaySettingsSnapshot = true;
            SaveClientGameplaySettingsCache(snapshot);
            return true;
        }

        private void UpdateCachedGameplaySettingsAfterDelta(
            GameplaySettingsDeltaMessage delta,
            long previousRevision)
        {
            if (!_hasClientGameplaySettingsSnapshot ||
                _clientGameplaySettingsSnapshot.revision != previousRevision)
            {
                _hasClientGameplaySettingsSnapshot = false;
                _clientGameplaySettingsSnapshot = default;
                DeleteGameplaySettingsCache(GameplaySettingsCachePath());
                return;
            }

            GameplaySettingsSnapshotMessage snapshot = _clientGameplaySettingsSnapshot;
            GameplaySettingsChangeMask mask = delta.ChangeMask;
            snapshot.revision = delta.revision;
            if ((mask & GameplaySettingsChangeMask.MoveSpeed) != 0)
                snapshot.moveSpeed = delta.moveSpeed;
            if ((mask & GameplaySettingsChangeMask.SprintSpeed) != 0)
                snapshot.sprintSpeed = delta.sprintSpeed;
            if ((mask & GameplaySettingsChangeMask.Gravity) != 0)
                snapshot.gravity = delta.gravity;
            if ((mask & GameplaySettingsChangeMask.JumpSpeed) != 0)
                snapshot.jumpSpeed = delta.jumpSpeed;
            if ((mask & GameplaySettingsChangeMask.BasicAttackInterval) != 0)
                snapshot.basicAttackInterval = delta.basicAttackInterval;
            if ((mask & GameplaySettingsChangeMask.ResourceRates) != 0)
                snapshot.resourceRates = MergeGameplayResourceRates(snapshot.resourceRates, delta.resourceRates);

            _clientGameplaySettingsSnapshot = snapshot;
            SaveClientGameplaySettingsCache(snapshot);
        }

        private static GameplayResourceRateWire[] MergeGameplayResourceRates(
            GameplayResourceRateWire[] existing,
            GameplayResourceRateWire[] changed)
        {
            GameplayResourceRateWire[] source = existing ?? Array.Empty<GameplayResourceRateWire>();
            GameplayResourceRateWire[] updates = changed ?? Array.Empty<GameplayResourceRateWire>();
            if (updates.Length == 0)
                return source;

            var merged = new GameplayResourceRateWire[source.Length + updates.Length];
            if (source.Length > 0)
                Array.Copy(source, merged, source.Length);
            int count = source.Length;

            for (int i = 0; i < updates.Length; ++i)
            {
                GameplayResourceRateWire update = updates[i];
                int index = -1;
                for (int j = 0; j < count; ++j)
                {
                    if (merged[j].resourceId == update.resourceId)
                    {
                        index = j;
                        break;
                    }
                }

                if (index >= 0)
                    merged[index] = update;
                else
                    merged[count++] = update;
            }

            if (count == merged.Length)
                return merged;
            Array.Resize(ref merged, count);
            return merged;
        }

        private void SaveClientGameplaySettingsCache(GameplaySettingsSnapshotMessage snapshot)
        {
            if (!snapshot.success || snapshot.revision <= 0)
                return;

            string path = GameplaySettingsCachePath();
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var netWriter = new NetDataWriter();
                snapshot.Serialize(netWriter);
                byte[] payload = netWriter.CopyData();
                if (payload.Length <= 0 || payload.Length > GameplaySettingsCacheMaxBytes)
                    return;

                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                    return;
                Directory.CreateDirectory(directory);
                string temp = path + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(GameplaySettingsCacheMagic);
                    writer.Write(GameplaySettingsCacheVersion);
                    writer.Write(PlayerEntityProtocol.Version);
                    writer.Write(payload.Length);
                    writer.Write(payload);
                }

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // Local presentation/reference cache failure must never block admission.
            }
        }

        private string GameplaySettingsCachePath()
        {
            // Gameplay definitions are environment-wide rather than character- or map-specific.
            // Scope by the authentication environment so compatible GameServers in the same
            // deployment reuse one local catalog without crossing Local/Live boundaries.
            string scope = (AuthenticationServiceBaseUrl ?? string.Empty)
                .Trim()
                .TrimEnd('/')
                .ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(scope))
                return string.Empty;

            string scopeHash;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(scope));
                scopeHash = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }

            return Path.Combine(
                Application.persistentDataPath,
                "GameplaySettingsCache",
                scopeHash + ".bin");
        }

        private static void DeleteGameplaySettingsCache(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                string temp = path + ".tmp";
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Local cache cleanup is best effort only.
            }
        }

        private static GameplaySettingsSnapshotMessage BuildGameplaySettingsSnapshot(
            GameplayContentCatalog content)
        {
            if (content == null)
                return GameplaySettingsSnapshotMessage.Failed("gameplay settings are unavailable");

            MovementRulesDefinition movement =
                content.GetMovementRules() ?? new MovementRulesDefinition();
            CombatRulesDefinition combat =
                content.GetCombatRules() ?? new CombatRulesDefinition();
            CharacterResourceDefinition[] resources =
                content.GetResources() ?? Array.Empty<CharacterResourceDefinition>();
            AbilityDefinition[] abilities =
                content.GetAbilities() ?? Array.Empty<AbilityDefinition>();
            StatusEffectDefinition[] statuses =
                content.GetStatusEffects() ?? Array.Empty<StatusEffectDefinition>();
            ItemDefinition[] items =
                content.GetItems() ?? Array.Empty<ItemDefinition>();
            EquipmentSlotDefinition[] equipmentSlots =
                content.GetEquipmentSlotsOrdered() ?? Array.Empty<EquipmentSlotDefinition>();

            var rates = new GameplayResourceRateWire[resources.Length];
            for (int i = 0; i < resources.Length; ++i)
            {
                rates[i] = new GameplayResourceRateWire
                {
                    resourceId = (ushort)resources[i].id,
                    displayName = resources[i].displayName ?? resources[i].id.ToString(),
                    minimum = resources[i].minimum,
                    replication = (byte)resources[i].replication,
                    ratePerSecond = resources[i].ratePerSecond,
                };
            }

            var abilityRefs = new GameplayAbilityReferenceWire[abilities.Length];
            for (int i = 0; i < abilities.Length; ++i)
            {
                AbilityDefinition ability = abilities[i];
                abilityRefs[i] = new GameplayAbilityReferenceWire
                {
                    wireId = ability.wireId,
                    definitionId = ability.definitionId ?? string.Empty,
                    presentationId = ability.presentationId,
                    deliveryType = (byte)ability.deliveryType,
                    projectileMode = (byte)ability.visibleProjectileMode,
                    projectilePresentationId = ability.projectilePresentationId,
                    castTimeSeconds = ability.castTimeSeconds,
                    cooldownSeconds = ability.cooldownSeconds,
                };
            }

            var abilityPresentations = new GameplayAbilityPresentationWire[abilities.Length];
            for (int i = 0; i < abilities.Length; ++i)
            {
                AbilityDefinition ability = abilities[i];
                abilityPresentations[i] = new GameplayAbilityPresentationWire
                {
                    wireId = ability.wireId,
                    displayName = ability.displayName ?? ability.definitionId ?? string.Empty,
                    description = ability.description ?? string.Empty,
                    iconKey = ability.iconKey ?? string.Empty,
                    sortOrder = ability.sortOrder,
                    category = (byte)ability.category,
                    targetMode = (byte)ability.targetMode,
                    targetRelation = (byte)ability.targetRelation,
                    resourceId = (ushort)ability.resourceId,
                    resourceCost = ability.resourceCost,
                    range = ability.range,
                };
            }

            var statusRefs = new GameplayStatusReferenceWire[statuses.Length];
            for (int i = 0; i < statuses.Length; ++i)
            {
                StatusEffectDefinition status = statuses[i];
                statusRefs[i] = new GameplayStatusReferenceWire
                {
                    wireId = status.wireId,
                    definitionId = status.definitionId ?? string.Empty,
                    displayName = status.displayName ?? status.definitionId ?? string.Empty,
                    classification = (byte)status.classification,
                    presentationId = status.presentationId,
                };
            }

            var slotRefs = new GameplayEquipmentSlotReferenceWire[equipmentSlots.Length];
            for (int i = 0; i < equipmentSlots.Length; ++i)
            {
                EquipmentSlotDefinition slot = equipmentSlots[i];
                slotRefs[i] = new GameplayEquipmentSlotReferenceWire
                {
                    dataId = slot.dataId,
                    slotId = slot.slotId ?? string.Empty,
                    displayName = slot.displayName ?? slot.slotId ?? string.Empty,
                    order = slot.order,
                };
            }

            var itemRefs = new GameplayItemReferenceWire[items.Length];
            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                string[] allowed = item.allowedEquipmentSlots ?? Array.Empty<string>();
                var allowedIds = new ushort[allowed.Length];
                int allowedCount = 0;
                for (int j = 0; j < allowed.Length; ++j)
                {
                    if (content.TryGetEquipmentSlot(allowed[j], out EquipmentSlotDefinition slot) && slot.dataId != 0)
                        allowedIds[allowedCount++] = slot.dataId;
                }
                if (allowedCount != allowedIds.Length)
                    Array.Resize(ref allowedIds, allowedCount);

                itemRefs[i] = new GameplayItemReferenceWire
                {
                    dataId = item.dataId,
                    definitionId = item.definitionId ?? string.Empty,
                    displayName = item.displayName ?? item.definitionId ?? string.Empty,
                    presentationId = item.presentationId,
                    maxDurability = item.maxDurability,
                    unitWeight = item.weight,
                    canUse = item.consumeQuantity > 0 && (item.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length > 0,
                    consumeQuantity = item.consumeQuantity,
                    allowedSlotDataIds = allowedIds,
                };
            }

            return new GameplaySettingsSnapshotMessage
            {
                success = true,
                error = string.Empty,
                revision = content.Revision,
                moveSpeed = movement.moveSpeed,
                sprintSpeed = movement.sprintSpeed,
                gravity = movement.gravity,
                jumpSpeed = movement.jumpSpeed,
                basicAttackInterval = BasicAttackCadenceTiming.Clamp(combat.basicAttackInterval),
                resourceRates = rates,
                abilities = abilityRefs,
                abilityPresentations = abilityPresentations,
                statuses = statusRefs,
                items = itemRefs,
                equipmentSlots = slotRefs,
            };
        }
    }
}
