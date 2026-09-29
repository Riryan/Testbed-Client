using System;
using System.IO;
using SQLite;

namespace Game.Server.Persistence
{
    /// <summary>
    /// Local SQLite adapter retained for migration and persistence regression tests.
    /// Production runtime composition no longer references this assembly; BackendServer
    /// owns the deployed database while application/domain code remains SQLite-free.
    /// </summary>
    public sealed class SqlitePersistenceStore : IDisposable
    {
        private readonly object _gate = new object();
        private SQLiteConnection _connection;

        public string DatabasePath { get; }
        public SqliteAccountRepository Accounts { get; }
        public SqliteCharacterRepository Characters { get; }

        public SqlitePersistenceStore(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            DatabasePath = Path.GetFullPath(databasePath);
            string directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            try
            {
                _connection = new SQLiteConnection(DatabasePath, true)
                {
                    BusyTimeout = TimeSpan.FromSeconds(5)
                };
                InitializeSchema();
                Accounts = new SqliteAccountRepository(this);
                Characters = new SqliteCharacterRepository(this);
            }
            catch
            {
                _connection?.Dispose();
                _connection = null;
                throw;
            }
        }

        internal T Execute<T>(Func<SQLiteConnection, T> operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            lock (_gate)
            {
                if (_connection == null)
                    throw new ObjectDisposedException(nameof(SqlitePersistenceStore));
                return operation(_connection);
            }
        }

        internal void Execute(Action<SQLiteConnection> operation)
        {
            Execute(conn =>
            {
                operation(conn);
                return true;
            });
        }

        private void InitializeSchema()
        {
            Execute(conn =>
            {
                conn.Execute("PRAGMA foreign_keys=ON");
                conn.CreateTable<AccountRow>();
                conn.CreateTable<CharacterRow>();
                conn.CreateIndex("idx_accounts_name_key", "accounts", "nameKey", true);
                conn.CreateIndex("idx_characters_name_key", "characters", "nameKey", true);
                conn.CreateIndex("idx_characters_account_id", "characters", "accountId", false);
            });
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_connection == null)
                    return;
                _connection.Dispose();
                _connection = null;
            }
        }

        [Table("accounts")]
        internal sealed class AccountRow
        {
            [PrimaryKey, AutoIncrement]
            public long accountId { get; set; }
            [NotNull]
            public string name { get; set; }
            [NotNull]
            public string nameKey { get; set; }
            [NotNull]
            public string passwordVerifier { get; set; }
            public long createdUtcTicks { get; set; }
            public long lastLoginUtcTicks { get; set; }
        }

        [Table("characters")]
        internal sealed class CharacterRow
        {
            [PrimaryKey, AutoIncrement]
            public long characterId { get; set; }
            public long accountId { get; set; }
            [NotNull]
            public string name { get; set; }
            [NotNull]
            public string nameKey { get; set; }
            [NotNull]
            public string mapId { get; set; }
            [NotNull]
            public string instanceId { get; set; }
            public float positionX { get; set; }
            public float positionY { get; set; }
            public float positionZ { get; set; }
            public float yawDegrees { get; set; }
            public long revision { get; set; }
            public int schemaVersion { get; set; }
            public string appearanceStateJson { get; set; }
            public string presentationStateJson { get; set; }
            public long createdUtcTicks { get; set; }
            public long updatedUtcTicks { get; set; }
        }
    }
}
