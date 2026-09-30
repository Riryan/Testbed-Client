using System;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        private long _clientCharacterRosterAccountId;

        private void RestoreCharacterRosterCache(long accountId, long authoritativeRevision)
        {
            ResetClientCharacterListCache();
            _clientCharacterRosterAccountId = accountId;

            if (accountId <= 0 || authoritativeRevision == 0)
                return;

            if (!TryLoadOwnerStateCache(
                    AuthenticationServiceBaseUrl,
                    accountId,
                    "roster",
                    out CharacterListResponseMessage cached) ||
                !cached.success)
                return;

            long cachedRevision = CharacterRosterStateRevision.Compute(
                cached.characters ?? Array.Empty<CharacterSessionCharacterSummary>());
            if (cachedRevision != authoritativeRevision)
                return;

            _latestCharacterList = cached;
            _hasCharacterListCache = true;
        }

        private void PersistCharacterRosterCache()
        {
            if (_clientCharacterRosterAccountId <= 0 || !HasCharacterListCache)
                return;

            SaveOwnerStateCache(
                AuthenticationServiceBaseUrl,
                _clientCharacterRosterAccountId,
                "roster",
                _latestCharacterList);
        }
    }
}
