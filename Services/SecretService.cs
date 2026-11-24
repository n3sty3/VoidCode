using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoidCode.Services;

/// <summary>
/// Stores provider credentials encrypted for the current OS user:
/// Windows DPAPI on Windows, macOS Keychain (with AES-256-GCM 0600 fallback for headless/isolated
/// profiles) on macOS, and AES-256-GCM 0600 storage on Linux.
/// </summary>
public static class SecretService
{
    private static string SecretDirectory => Path.Combine(StorageService.Root, "credentials");
    private static string ApiKeyFile => Path.Combine(SecretDirectory, "provider-key.dat");
    private static string KeychainMarkerFile => Path.Combine(SecretDirectory, "provider-key.keychain");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoidCode.Provider.ApiKey.v1");

    public static bool HasApiKey => File.Exists(ApiKeyFile) || File.Exists(KeychainMarkerFile);

    private static string MacKeychainServiceName()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StorageService.Root))).Substring(0, 12);
        return $"dev.voidcode.ide.apikey.{hash}";
    }

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

            if (OperatingSystem.IsWindows())
            {
                var plainBytes = Encoding.UTF8.GetBytes(apiKey);
                try
                {
                    var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                    WriteAtomicBytes(ApiKeyFile, protectedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
            else if (OperatingSystem.IsMacOS() && TrySaveToMacKeychain(apiKey))
            {
                WriteAtomicBytes(KeychainMarkerFile, Encoding.UTF8.GetBytes("keychain-v1"));
                if (File.Exists(ApiKeyFile)) File.Delete(ApiKeyFile);
            }
            else
            {
                SaveAesGcmFallback(apiKey);
                if (File.Exists(KeychainMarkerFile)) File.Delete(KeychainMarkerFile);
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
            if (OperatingSystem.IsWindows())
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

            if (OperatingSystem.IsMacOS() && File.Exists(KeychainMarkerFile))
            {
                var keychainKey = TryLoadFromMacKeychain();
                if (!string.IsNullOrEmpty(keychainKey))
                {
                    LoggingService.RegisterSecret(keychainKey);
                    return keychainKey;
                }
            }

            if (File.Exists(ApiKeyFile))
            {
                var key = LoadAesGcmFallback();
                if (!string.IsNullOrEmpty(key)) LoggingService.RegisterSecret(key);
                return key;
            }

            return "";
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
            if (File.Exists(KeychainMarkerFile)) File.Delete(KeychainMarkerFile);
            if (OperatingSystem.IsMacOS()) TryDeleteFromMacKeychain();
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not remove the protected provider API key.", ex, nameof(SecretService));
        }
    }

    private static bool TrySaveToMacKeychain(string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOIDCODE_STORAGE_ROOT")))
        {
            // Keep isolated test/portable profiles completely self-contained on disk.
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("add-generic-password");
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(MacKeychainServiceName());
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(Environment.UserName);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(apiKey);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? TryLoadFromMacKeychain()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(MacKeychainServiceName());
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(Environment.UserName);
            psi.ArgumentList.Add("-w");
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDeleteFromMacKeychain()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("delete-generic-password");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(MacKeychainServiceName());
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(Environment.UserName);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch (Exception) { /* keychain item may not exist */ }
    }

    private static byte[] DeriveLocalKey()
    {
        var material = $"{Environment.UserName}|{Environment.MachineName}|{StorageService.Root}|VoidCode.Provider.ApiKey.v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    private static void SaveAesGcmFallback(string apiKey)
    {
        var key = DeriveLocalKey();
        var plain = Encoding.UTF8.GetBytes(apiKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Entropy);
            var blob = new byte[4 + nonce.Length + tag.Length + cipher.Length];
            Encoding.ASCII.GetBytes("VCG1", 0, 4, blob, 0);
            Buffer.BlockCopy(nonce, 0, blob, 4, nonce.Length);
            Buffer.BlockCopy(tag, 0, blob, 4 + nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, blob, 4 + nonce.Length + tag.Length, cipher.Length);
            WriteAtomicBytes(ApiKeyFile, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string LoadAesGcmFallback()
    {
        var blob = File.ReadAllBytes(ApiKeyFile);
        if (blob.Length < 4 + 12 + 16 || blob[0] != 'V' || blob[1] != 'C' || blob[2] != 'G' || blob[3] != '1')
        {
            return "";
        }

        var key = DeriveLocalKey();
        var nonce = blob.AsSpan(4, 12);
        var tag = blob.AsSpan(16, 16);
        var cipher = blob.AsSpan(32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Entropy);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void WriteAtomicBytes(string destination, byte[] data)
    {
        var tempPath = destination + ".tmp";
        File.WriteAllBytes(tempPath, data);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception) { /* filesystem may not support POSIX mode bits */ }
        }
        File.Move(tempPath, destination, true);
    }
}
