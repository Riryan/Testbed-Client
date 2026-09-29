using System;
using System.Security.Cryptography;
using Cysharp.Threading.Tasks;
using Game.Shared.Backend;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Client.Networking
{
    /// <summary>
    /// Public HTTPS authentication client. Password material exists only for the
    /// duration of this HTTPS request and is never sent over LiteNetLib gameplay packets.
    /// </summary>
    public sealed class BackendAuthenticationClient
    {
        private const string InstallIdPlayerPrefsKey = "MMO.InstallId.V1";


        [Serializable]
        public sealed class BackendServiceHealth
        {
            public string status = string.Empty;
            public bool gameServerAvailable;

            public bool IsHealthy =>
                string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
        }

        public async UniTask<BackendServiceHealth> CheckHealthAsync(
            string baseUrl,
            string certificateSha256,
            int timeoutSeconds = 8)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            using (var request = UnityWebRequest.Get(baseUrl.TrimEnd('/') + "/health"))
            {
                request.timeout = Math.Max(3, timeoutSeconds);
                string pin = NormalizeSha256(certificateSha256);
                if (!string.IsNullOrEmpty(pin))
                    request.certificateHandler = new PinnedCertificateHandler(pin);

                try
                {
                    await request.SendWebRequest().ToUniTask();
                }
                catch
                {
                    return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                    return null;

                string text = request.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(text))
                    return null;

                try
                {
                    return JsonUtility.FromJson<BackendServiceHealth>(text);
                }
                catch
                {
                    return null;
                }
            }
        }
        public async UniTask<BackendAuthResponse> LoginAsync(
            string baseUrl,
            string certificateSha256,
            string account,
            string password,
            int timeoutSeconds = 15) =>
            await SendAsync(
                baseUrl,
                certificateSha256,
                "/v1/accounts/login",
                account,
                password,
                timeoutSeconds);

        public async UniTask<BackendAuthResponse> CreateAsync(
            string baseUrl,
            string certificateSha256,
            string account,
            string password,
            int timeoutSeconds = 15) =>
            await SendAsync(
                baseUrl,
                certificateSha256,
                "/v1/accounts/create",
                account,
                password,
                timeoutSeconds);

        private static async UniTask<BackendAuthResponse> SendAsync(
            string baseUrl,
            string certificateSha256,
            string path,
            string account,
            string password,
            int timeoutSeconds)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Failed("authentication service is not configured");

            var body = new BackendAccountRequest
            {
                account = account ?? string.Empty,
                password = password ?? string.Empty,
                deviceId = GetOrCreateInstallId(),
            };

            string json = JsonUtility.ToJson(body);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

            using (var request = new UnityWebRequest(baseUrl.TrimEnd('/') + path, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Math.Max(5, timeoutSeconds);

                string pin = NormalizeSha256(certificateSha256);
                if (!string.IsNullOrEmpty(pin))
                    request.certificateHandler = new PinnedCertificateHandler(pin);

                try
                {
                    await request.SendWebRequest().ToUniTask();
                }
                catch (UnityWebRequestException)
                {
                    // UniTask throws for HTTP 4xx/5xx as well as transport failures.
                    // Keep the server's generic JSON auth response when one exists so
                    // duplicate-account/wrong-password/rate-limit failures are not
                    // misreported as a dead authentication service.
                }
                catch
                {
                    return Failed("authentication service unavailable");
                }

                if (request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError)
                {
                    return Failed("authentication service unavailable");
                }

                string responseText = request.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(responseText))
                    return Failed("authentication service returned no response");

                BackendAuthResponse response;
                try
                {
                    response = JsonUtility.FromJson<BackendAuthResponse>(responseText);
                }
                catch
                {
                    return Failed("authentication service returned an invalid response");
                }

                if (response == null)
                    return Failed("authentication service returned an invalid response");

                if (request.result != UnityWebRequest.Result.Success && response.success)
                    return Failed("authentication service rejected the request");

                return response;
            }
        }

        private static string GetOrCreateInstallId()
        {
            string existing = PlayerPrefs.GetString(InstallIdPlayerPrefsKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(existing) && existing.Length <= 128)
                return existing;

            string created = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(InstallIdPlayerPrefsKey, created);
            PlayerPrefs.Save();
            return created;
        }

        private static BackendAuthResponse Failed(string error) =>
            new BackendAuthResponse
            {
                success = false,
                error = error ?? "authentication unavailable",
                admissionToken = string.Empty,
                expiresUtcTicks = 0,
            };

        private static string NormalizeSha256(string value) =>
            (value ?? string.Empty)
                .Replace(":", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();

        // The shipped Gateway certificate is a self-signed, application-pinned identity.
        // Unity delegates certificate acceptance to this handler on supported desktop/mobile
        // platforms. Trust is therefore intentionally based on the exact SHA-256 certificate
        // pin, not the certificate's NotBefore/NotAfter lifetime. This keeps an otherwise
        // unchanged, still-pinned Gateway identity usable after its X.509 date expires while
        // continuing to reject every certificate whose bytes do not match the shipped pin.
        // Do not replace this with a broad "accept all certificates" handler.
        private sealed class PinnedCertificateHandler : CertificateHandler
        {
            private readonly string _expectedSha256;

            public PinnedCertificateHandler(string expectedSha256)
            {
                _expectedSha256 = expectedSha256;
            }

            protected override bool ValidateCertificate(byte[] certificateData)
            {
                if (certificateData == null || certificateData.Length == 0)
                    return false;

                byte[] hash;
                using (SHA256 sha = SHA256.Create())
                    hash = sha.ComputeHash(certificateData);

                string actual = BitConverter.ToString(hash).Replace("-", string.Empty);
                return string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
