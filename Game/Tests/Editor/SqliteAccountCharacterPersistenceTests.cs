using System;
using System.IO;
using System.Threading;
using Game.Server.Application.Authentication;
using Game.Server.Application.Characters;
using Game.Server.Domain.Characters;
using Game.Server.Persistence;
using Game.Shared.Authentication;
using Game.Shared.Identity;
using Game.Shared.Characters;
using Game.Shared.World;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class SqliteAccountCharacterPersistenceTests
    {
        private string _directory;
        private string _databasePath;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "SqliteAccountCharacterPersistenceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "character-session.sqlite3");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        [Test]
        public void Account_CreateThenReopen_LoginKeepsStableAccountId()
        {
            AccountId createdId;
            string verifier = AccountCredentialPolicy.ComputePasswordVerifier("Persistent_1", "Secret42!");

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                var accounts = new AccountAuthenticationService(store.Accounts);
                AccountAuthenticationResult created = accounts
                    .CreateAsync("Persistent_1", verifier, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.IsTrue(created.Success);
                Assert.IsTrue(created.AccountCreated);
                createdId = created.AccountId;
            }

            using (var reopened = new SqlitePersistenceStore(_databasePath))
            {
                var accounts = new AccountAuthenticationService(reopened.Accounts);
                AccountAuthenticationResult login = accounts
                    .LoginAsync("Persistent_1", verifier, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.IsTrue(login.Success);
                Assert.IsFalse(login.AccountCreated);
                Assert.AreEqual(createdId, login.AccountId);
            }
        }

        [Test]
        public void Account_CreateIsExplicitAndDuplicateNameIsRejected()
        {
            string verifier = AccountCredentialPolicy.ComputePasswordVerifier("Explicit", "Password1!");
            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                var accounts = new AccountAuthenticationService(store.Accounts);
                Assert.IsFalse(accounts.LoginAsync("Explicit", verifier, CancellationToken.None)
                    .GetAwaiter().GetResult().Success);
                Assert.IsTrue(accounts.CreateAsync("Explicit", verifier, CancellationToken.None)
                    .GetAwaiter().GetResult().Success);
                Assert.IsFalse(accounts.CreateAsync("Explicit", verifier, CancellationToken.None)
                    .GetAwaiter().GetResult().Success);
            }
        }

        [Test]
        public void Character_CreateThenReopen_ListAndLoadPersist()
        {
            var accountId = new AccountId(77);
            var location = new CharacterLocationState(
                "MMOPlayerEntityTest",
                string.Empty,
                new WorldPosition(3f, 4f, 5f),
                90f);
            CharacterId characterId;
            CharacterPresentationPreferences presentation = CharacterPresentationPreferences.CreateDefault();
            presentation.movementStyle = 211;

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                var created = store.Characters.TryCreateAsync(
                    accountId,
                    "Alice Smith",
                    location,
                    CharacterAppearanceRecipe.CreateDefault(),
                    presentation,
                    CharacterService.DefaultCharacterLimit,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(created.Success);
                characterId = created.CharacterId;
            }

            using (var reopened = new SqlitePersistenceStore(_databasePath))
            {
                var list = reopened.Characters.ListForAccountAsync(accountId, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.AreEqual(1, list.Count);
                Assert.AreEqual(characterId, list[0].CharacterId);

                var record = reopened.Characters.LoadForAccountAsync(accountId, characterId, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.NotNull(record);
                Assert.AreEqual("Alice Smith", record.Name);
                Assert.AreEqual(location, record.Location);
                Assert.NotNull(record.PresentationPreferences);
                Assert.AreEqual(211, record.PresentationPreferences.movementStyle);
            }
        }

        [Test]
        public void Character_NameUniquenessIsGlobalAndCaseInsensitive()
        {
            var location = new CharacterLocationState(
                "World",
                string.Empty,
                new WorldPosition(0f, 0f, 0f),
                0f);

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                Assert.IsTrue(store.Characters.TryCreateAsync(
                    new AccountId(1), "Unique Name", location, CharacterAppearanceRecipe.CreateDefault(), CharacterPresentationPreferences.CreateDefault(), CharacterService.DefaultCharacterLimit, CancellationToken.None)
                    .GetAwaiter().GetResult().Success);

                var duplicate = store.Characters.TryCreateAsync(
                    new AccountId(2), "unique name", location, CharacterAppearanceRecipe.CreateDefault(), CharacterPresentationPreferences.CreateDefault(), CharacterService.DefaultCharacterLimit, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.IsFalse(duplicate.Success);
                Assert.IsTrue(duplicate.NameAlreadyExists);
            }
        }
        [Test]
        public void Character_CreateEnforcesPerAccountLimitInsideRepositoryWriteBoundary()
        {
            var accountId = new AccountId(9);
            var location = new CharacterLocationState(
                "World",
                string.Empty,
                new WorldPosition(0f, 0f, 0f),
                0f);

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                for (int i = 0; i < CharacterService.DefaultCharacterLimit; ++i)
                {
                    var created = store.Characters.TryCreateAsync(
                        accountId,
                        "Hero " + (char)('A' + i),
                        location,
                        CharacterAppearanceRecipe.CreateDefault(),
                        CharacterPresentationPreferences.CreateDefault(),
                        CharacterService.DefaultCharacterLimit,
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert.IsTrue(created.Success);
                }

                var overLimit = store.Characters.TryCreateAsync(
                    accountId,
                    "Hero Z",
                    location,
                    CharacterAppearanceRecipe.CreateDefault(),
                    CharacterPresentationPreferences.CreateDefault(),
                    CharacterService.DefaultCharacterLimit,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsFalse(overLimit.Success);
                Assert.IsTrue(overLimit.CharacterLimitReached);
            }
        }

        [Test]
        public void Character_SavePersistsNewestRevisionAcrossReopen()
        {
            var accountId = new AccountId(12);
            var initial = new CharacterLocationState(
                "World",
                string.Empty,
                new WorldPosition(1f, 2f, 3f),
                10f);
            var newest = new CharacterLocationState(
                "World",
                string.Empty,
                new WorldPosition(7f, 8f, 9f),
                45f);
            CharacterId characterId;

            using (var store = new SqlitePersistenceStore(_databasePath))
            {
                var created = store.Characters.TryCreateAsync(
                    accountId,
                    "Save Hero",
                    initial,
                    CharacterAppearanceRecipe.CreateDefault(),
                    CharacterPresentationPreferences.CreateDefault(),
                    CharacterService.DefaultCharacterLimit,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(created.Success);
                characterId = created.CharacterId;

                store.Characters.SaveAsync(
                    new Game.Server.Application.Persistence.CharacterPersistenceRecord(
                        accountId, characterId, "Save Hero", newest, 2),
                    CancellationToken.None).GetAwaiter().GetResult();

                // A late stale snapshot must not overwrite the newer revision.
                store.Characters.SaveAsync(
                    new Game.Server.Application.Persistence.CharacterPersistenceRecord(
                        accountId, characterId, "Save Hero", initial, 1),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            using (var reopened = new SqlitePersistenceStore(_databasePath))
            {
                var record = reopened.Characters.LoadForAccountAsync(
                    accountId, characterId, CancellationToken.None).GetAwaiter().GetResult();
                Assert.NotNull(record);
                Assert.AreEqual(2, record.Revision);
                Assert.AreEqual(newest, record.Location);
            }
        }

    }
}
