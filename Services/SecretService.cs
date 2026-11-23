using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoidCode.Services;

/// <summary>
/// Stores provider credentials encrypted for the current Windows user.
/// The encrypted payload is intentionally kept separate from settings.json so
/// Settings can be shared or inspected without exposing the API key.
/// </summary>
public static class SecretService
{
    // Same profile root as the rest of the stores (StorageService.Root), resolved per access so the
    // VOIDCODE_STORAGE_ROOT / UseRoot overrides reach the credentials directory too. The encrypted
    // Payload stays separate from settings.json so settings can be inspected without the key.
    private static string SecretDirectory => Path.Combine(StorageService.Root, "credentials");
    private static string ApiKeyFile => Path.Combine(SecretDirectory, "provider-key.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoidCode.Provider.ApiKey.v1");

    public static bool HasApiKey => File.Exists(ApiKeyFile);

    public static bool SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ClearApiKey();
            return true;
        }

        try
        {
            Directory.CreateDirectory(SecretDirectory);
            var plainBytes = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                var tempPath = ApiKeyFile + ".tmp";
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, ApiKeyFile, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
            // The live key is registered so it can never reach a log entry.
            LoggingService.RegisterSecret(apiKey);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not protect the provider API key.", ex, nameof(SecretService));
            return false;
        }
    }

    public static string LoadApiKey()
    {
        try
        {
            if (!File.Exists(ApiKeyFile)) return "";
            var protectedBytes = File.ReadAllBytes(ApiKeyFile);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var key = Encoding.UTF8.GetString(plainBytes);
                LoggingService.RegisterSecret(key);
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not read the protected provider API key.", ex, nameof(SecretService));
            return "";
        }
    }

    public static void ClearApiKey()
    {
        try
        {
            if (File.Exists(ApiKeyFile)) File.Delete(ApiKeyFile);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not remove the protected provider API key.", ex, nameof(SecretService));
        }
    }
}
