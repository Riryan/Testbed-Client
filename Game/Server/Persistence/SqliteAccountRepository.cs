using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Shared.Identity;
using SQLite;

namespace Game.Server.Persistence
{
    public sealed class SqliteAccountRepository : IAccountRepository
    {
        private readonly SqlitePersistenceStore _store;

        internal SqliteAccountRepository(SqlitePersistenceStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public Task<AccountPersistenceRecord> FindByNameAsync(
            string accountName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(accountName))
                return Task.FromResult<AccountPersistenceRecord>(null);

            AccountPersistenceRecord result = _store.Execute(conn =>
            {
                SqlitePersistenceStore.AccountRow row = conn.FindWithQuery<SqlitePersistenceStore.AccountRow>(
                    "SELECT * FROM accounts WHERE name=? LIMIT 1", accountName);
                return row == null ? null : ToRecord(row);
            });
            return Task.FromResult(result);
        }

        public Task<AccountCreatePersistenceResult> TryCreateAsync(
            string accountName,
            string passwordVerifier,
            long utcNowTicks,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(passwordVerifier))
                return Task.FromResult(AccountCreatePersistenceResult.Failed());

            string key = NormalizeKey(accountName);
            AccountCreatePersistenceResult result = _store.Execute(conn =>
            {
                if (conn.FindWithQuery<SqlitePersistenceStore.AccountRow>(
                        "SELECT * FROM accounts WHERE nameKey=? LIMIT 1", key) != null)
                {
                    return AccountCreatePersistenceResult.DuplicateName();
                }

                var row = new SqlitePersistenceStore.AccountRow
                {
                    name = accountName,
                    nameKey = key,
                    passwordVerifier = passwordVerifier,
                    createdUtcTicks = utcNowTicks,
                    lastLoginUtcTicks = utcNowTicks,
                };

                try
                {
                    if (conn.Insert(row) != 1 || row.accountId <= 0)
                        return AccountCreatePersistenceResult.Failed();
                    return AccountCreatePersistenceResult.Created(new AccountId(row.accountId));
                }
                catch (SQLiteException)
                {
                    // The unique index remains authoritative if another process raced us.
                    if (conn.FindWithQuery<SqlitePersistenceStore.AccountRow>(
                            "SELECT * FROM accounts WHERE nameKey=? LIMIT 1", key) != null)
                    {
                        return AccountCreatePersistenceResult.DuplicateName();
                    }
                    return AccountCreatePersistenceResult.Failed();
                }
            });

            return Task.FromResult(result);
        }

        public Task TouchLastLoginAsync(
            AccountId accountId,
            long utcNowTicks,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid)
                return Task.CompletedTask;

            _store.Execute(conn =>
                conn.Execute(
                    "UPDATE accounts SET lastLoginUtcTicks=? WHERE accountId=?",
                    utcNowTicks,
                    accountId.Value));
            return Task.CompletedTask;
        }

        private static AccountPersistenceRecord ToRecord(SqlitePersistenceStore.AccountRow row) =>
            new AccountPersistenceRecord(
                new AccountId(row.accountId),
                row.name,
                row.passwordVerifier,
                row.createdUtcTicks,
                row.lastLoginUtcTicks);

        private static string NormalizeKey(string accountName) =>
            accountName.Trim().ToUpperInvariant();
    }
}
