using Game.Server.Application.Authentication;
using Game.Server.Application.Characters;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Application.Sessions;
using Game.Shared.Authentication;
using Game.Shared.Identity;
using Game.Shared.Sessions;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class AccountAuthenticationSessionTests
    {
        private static PlayerSessionService CreateSessionService() =>
            new PlayerSessionService(
                new PlayerSessionRegistry(),
                new CharacterService(
                    new InMemoryCharacterRepository(),
                    new CharacterValidator(),
                    new CharacterRuntimeFactory()),
                new InMemoryCharacterLeaseService(),
                new DirtyPlayerTracker());

        [Test]
        public void CredentialPolicy_MatchesProductionVerifierFormat()
        {
            string verifier = AccountCredentialPolicy.ComputePasswordVerifier("TestUser", "password");

            Assert.AreEqual("157E6464DDF54F0CE5F50731A56775675B2E4B21", verifier);
            Assert.IsTrue(AccountCredentialPolicy.IsVerifierWellFormed(verifier));
            Assert.IsTrue(AccountCredentialPolicy.IsAllowedAccountName("abc_123"));
            Assert.IsFalse(AccountCredentialPolicy.IsAllowedAccountName("bad-name"));

            // Existing accounts may have been created before the stronger creation
            // minimum, so login remains backward-compatible while new accounts require
            // the current minimum.
            Assert.IsTrue(AccountCredentialPolicy.IsAllowedPasswordForLogin("x"));
            Assert.IsFalse(AccountCredentialPolicy.IsAllowedPasswordForCreation("x"));
            Assert.IsTrue(AccountCredentialPolicy.IsAllowedPasswordForCreation("Password1!"));
        }

        [Test]
        public void InMemoryAccounts_LoginOrCreate_ReturnsStableNumericAccountId()
        {
            var accounts = new InMemoryAccountAuthenticationService();
            string verifier = AccountCredentialPolicy.ComputePasswordVerifier("Alpha_1", "Secret42!");

            AccountAuthenticationResult first = accounts.LoginOrCreate("Alpha_1", verifier);
            AccountAuthenticationResult second = accounts.LoginOrCreate("Alpha_1", verifier);

            Assert.IsTrue(first.Success);
            Assert.IsTrue(first.AccountCreated);
            Assert.IsTrue(first.AccountId.IsValid);
            Assert.IsTrue(second.Success);
            Assert.IsFalse(second.AccountCreated);
            Assert.AreEqual(first.AccountId, second.AccountId);
        }

        [Test]
        public void InMemoryAccounts_WrongVerifier_DoesNotAuthenticateExistingAccount()
        {
            var accounts = new InMemoryAccountAuthenticationService();
            string correct = AccountCredentialPolicy.ComputePasswordVerifier("Bravo", "CorrectPassword");
            string wrong = AccountCredentialPolicy.ComputePasswordVerifier("Bravo", "WrongPassword");

            Assert.IsTrue(accounts.LoginOrCreate("Bravo", correct).Success);
            Assert.IsFalse(accounts.LoginOrCreate("Bravo", wrong).Success);
        }

        [Test]
        public void FailedAuthentication_CanReturnExactSessionToConnectedForRetry()
        {
            PlayerSessionService sessions = CreateSessionService();
            PlayerSession session = sessions.Open(new ConnectionKey(910));

            Assert.IsTrue(sessions.BeginAuthentication(session.Handle));
            Assert.AreEqual(PlayerSessionState.Authenticating, session.State);
            Assert.IsTrue(sessions.CancelAuthentication(session.Handle));
            Assert.AreEqual(PlayerSessionState.Connected, session.State);
            Assert.IsFalse(session.AccountId.IsValid);
        }

        [Test]
        public void StaleAuthenticationHandle_CannotAuthenticateReplacementGeneration()
        {
            PlayerSessionService sessions = CreateSessionService();
            var connection = new ConnectionKey(911);

            PlayerSession oldSession = sessions.Open(connection);
            PlayerSessionHandle stale = oldSession.Handle;
            Assert.IsTrue(sessions.BeginDisconnect(stale));
            Assert.IsTrue(sessions.Close(stale));

            PlayerSession replacement = sessions.Open(connection);
            Assert.NotNull(replacement);
            Assert.AreNotEqual(stale.SessionId, replacement.SessionId);

            Assert.IsFalse(sessions.BeginAuthentication(stale));
            Assert.IsFalse(sessions.CompleteAuthentication(stale, new AccountId(77)));
            Assert.AreEqual(PlayerSessionState.Connected, replacement.State);
            Assert.IsFalse(replacement.AccountId.IsValid);
        }
    }
}
