using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.World;
using SQLite;

namespace Game.Server.Persistence
{
    public sealed class SqliteCharacterRepository : ICharacterRepository
    {
        private readonly SqlitePersistenceStore _store;

        internal SqliteCharacterRepository(SqlitePersistenceStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(
            AccountId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid)
                return Task.FromResult((IReadOnlyList<CharacterSummary>)new CharacterSummary[0]);

            IReadOnlyList<CharacterSummary> result = _store.Execute(conn =>
            {
                List<SqlitePersistenceStore.CharacterRow> rows = conn.Query<SqlitePersistenceStore.CharacterRow>(
                    "SELECT * FROM characters WHERE accountId=? ORDER BY characterId ASC",
                    accountId.Value);
                var summaries = new CharacterSummary[rows.Count];
                for (int i = 0; i < rows.Count; ++i)
                {
                    SqlitePersistenceStore.CharacterRow row = rows[i];
                    summaries[i] = new CharacterSummary(new CharacterId(row.characterId), row.name, row.mapId);
                }
                return summaries;
            });

            return Task.FromResult(result);
        }

        public Task<CharacterPersistenceRecord> LoadForAccountAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid || !characterId.IsValid)
                return Task.FromResult<CharacterPersistenceRecord>(null);

            CharacterPersistenceRecord result = _store.Execute(conn =>
            {
                SqlitePersistenceStore.CharacterRow row = conn.FindWithQuery<SqlitePersistenceStore.CharacterRow>(
                    "SELECT * FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
                    characterId.Value,
                    accountId.Value);
                return row == null ? null : ToRecord(row);
            });
            return Task.FromResult(result);
        }

        public Task<CharacterCreatePersistenceResult> TryCreateAsync(
            AccountId accountId,
            string name,
            CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            int maxCharactersForAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid || string.IsNullOrWhiteSpace(name) || maxCharactersForAccount <= 0)
                return Task.FromResult(CharacterCreatePersistenceResult.Failed());

            string key = NormalizeNameKey(name);
            CharacterCreatePersistenceResult result = _store.Execute(conn =>
            {
                CharacterCreatePersistenceResult transactionResult = CharacterCreatePersistenceResult.Failed();
                try
                {
                    conn.RunInTransaction(() =>
                    {
                        if (conn.ExecuteScalar<int>(
                                "SELECT COUNT(1) FROM characters WHERE nameKey=?", key) > 0)
                        {
                            transactionResult = CharacterCreatePersistenceResult.DuplicateName();
                            return;
                        }

                        int ownedCount = conn.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM characters WHERE accountId=?", accountId.Value);
                        if (ownedCount >= maxCharactersForAccount)
                        {
                            transactionResult = CharacterCreatePersistenceResult.LimitReached();
                            return;
                        }

                        long now = DateTime.UtcNow.Ticks;
                        var row = new SqlitePersistenceStore.CharacterRow
                        {
                            accountId = accountId.Value,
                            name = name,
                            nameKey = key,
                            mapId = initialLocation.MapId,
                            instanceId = initialLocation.InstanceId ?? string.Empty,
                            positionX = initialLocation.Position.X,
                            positionY = initialLocation.Position.Y,
                            positionZ = initialLocation.Position.Z,
                            yawDegrees = initialLocation.YawDegrees,
                            revision = 0,
                            schemaVersion = CharacterPersistenceRecord.CurrentSchemaVersion,
                            appearanceStateJson = SerializeAppearance(initialAppearance),
                            presentationStateJson = SerializePresentation(initialPresentation),
                            createdUtcTicks = now,
                            updatedUtcTicks = now,
                        };

                        if (conn.Insert(row) == 1 && row.characterId > 0)
                            transactionResult = CharacterCreatePersistenceResult.Created(new CharacterId(row.characterId));
                    });
                }
                catch (SQLiteException)
                {
                    // The unique index remains authoritative if another process races us.
                    if (conn.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM characters WHERE nameKey=?", key) > 0)
                    {
                        return CharacterCreatePersistenceResult.DuplicateName();
                    }
                    return CharacterCreatePersistenceResult.Failed();
                }

                return transactionResult;
            });

            return Task.FromResult(result);
        }

        public Task SaveAsync(CharacterPersistenceRecord record, CancellationToken cancellationToken)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            cancellationToken.ThrowIfCancellationRequested();

            _store.Execute(conn =>
            {
                // Stale snapshots are ignored exactly like the in-memory implementation.
                conn.Execute(
                    "UPDATE characters SET name=?, nameKey=?, mapId=?, instanceId=?, positionX=?, positionY=?, positionZ=?, yawDegrees=?, revision=?, schemaVersion=?, appearanceStateJson=?, presentationStateJson=?, updatedUtcTicks=? " +
                    "WHERE characterId=? AND accountId=? AND revision<=?",
                    record.Name,
                    NormalizeNameKey(record.Name),
                    record.Location.MapId,
                    record.Location.InstanceId ?? string.Empty,
                    record.Location.Position.X,
                    record.Location.Position.Y,
                    record.Location.Position.Z,
                    record.Location.YawDegrees,
                    record.Revision,
                    record.SchemaVersion,
                    SerializeAppearance(record.Appearance),
                    SerializePresentation(record.PresentationPreferences),
                    DateTime.UtcNow.Ticks,
                    record.CharacterId.Value,
                    record.AccountId.Value,
                    record.Revision);
            });

            return Task.CompletedTask;
        }

        private static CharacterPersistenceRecord ToRecord(SqlitePersistenceStore.CharacterRow row)
        {
            var location = new CharacterLocationState(
                row.mapId,
                row.instanceId ?? string.Empty,
                new WorldPosition(row.positionX, row.positionY, row.positionZ),
                row.yawDegrees);
            return new CharacterPersistenceRecord(
                new AccountId(row.accountId),
                new CharacterId(row.characterId),
                row.name,
                location,
                row.revision,
                row.schemaVersion,
                PlayerDirtyFlags.None,
                null,
                0,
                null,
                DeserializeAppearance(row.appearanceStateJson),
                DeserializePresentation(row.presentationStateJson));
        }

        private static string SerializeAppearance(CharacterAppearanceRecipe appearance)
        {
            CharacterAppearanceRecipe value = appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(CharacterAppearanceRecipe));
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static CharacterAppearanceRecipe DeserializeAppearance(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return CharacterAppearanceRecipe.CreateDefault();

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                using (var stream = new MemoryStream(data))
                {
                    var serializer = new DataContractJsonSerializer(typeof(CharacterAppearanceRecipe));
                    CharacterAppearanceRecipe value = serializer.ReadObject(stream) as CharacterAppearanceRecipe;
                    return value != null && value.IsValid(out _)
                        ? value
                        : CharacterAppearanceRecipe.CreateDefault();
                }
            }
            catch
            {
                return CharacterAppearanceRecipe.CreateDefault();
            }
        }

        private static string SerializePresentation(CharacterPresentationPreferences preferences)
        {
            CharacterPresentationPreferences value =
                preferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(CharacterPresentationPreferences));
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static CharacterPresentationPreferences DeserializePresentation(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return CharacterPresentationPreferences.CreateDefault();

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                using (var stream = new MemoryStream(data))
                {
                    var serializer = new DataContractJsonSerializer(typeof(CharacterPresentationPreferences));
                    CharacterPresentationPreferences value =
                        serializer.ReadObject(stream) as CharacterPresentationPreferences;
                    return value != null && value.IsValid(out _)
                        ? value
                        : CharacterPresentationPreferences.CreateDefault();
                }
            }
            catch
            {
                return CharacterPresentationPreferences.CreateDefault();
            }
        }

        private static string NormalizeNameKey(string name) =>
            name.Trim().ToUpperInvariant();
    }
}
