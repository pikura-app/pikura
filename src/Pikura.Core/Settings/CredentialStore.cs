using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Pikura.Core.Settings;

/// <summary>
/// Encrypts and decrypts sensitive credential strings (PHPSESSID, RefreshToken).
/// On Windows uses DPAPI (machine+user scope) so only the current user on the
/// current machine can decrypt. On macOS/Linux falls back to AES-256-GCM with
/// a key derived from a machine-specific entropy file stored alongside settings.
/// Encrypted values are stored as Base64 strings prefixed with "ENC:".
/// Plaintext values (legacy migration) are returned as-is and re-encrypted on
/// the next <see cref="SettingsService.Save"/> call.
/// </summary>
public static class CredentialStore
{
    private const string Prefix = "ENC:";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Encrypts <paramref name="plaintext"/> and returns a Base64-encoded cipher string.</summary>
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already encrypted

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DpapiProtect(bytes)
            : AesProtect(bytes);

        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>Decrypts a previously <see cref="Protect"/>ed string back to plaintext.</summary>
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value; // plaintext / legacy

        var cipher = Convert.FromBase64String(value[Prefix.Length..]);
        var plain = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DpapiUnprotect(cipher)
            : AesUnprotect(cipher);

        return Encoding.UTF8.GetString(plain);
    }

    // ── Windows DPAPI ─────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);

    // ── Cross-platform AES-256-GCM fallback ──────────────────────────────────

    private static readonly Lazy<byte[]> _aesKey = new(DeriveAesKey);

    private static byte[] DeriveAesKey()
    {
        var keyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura", ".keyfile");

        byte[] entropy;
        if (File.Exists(keyFile))
        {
            entropy = File.ReadAllBytes(keyFile);
        }
        else
        {
            entropy = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllBytes(keyFile, entropy);
            // Restrict to owner only on Unix
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Stretch with PBKDF2 using machine name + user name as password
        var password = Encoding.UTF8.GetBytes(
            Environment.MachineName + ":" + Environment.UserName);
        return Rfc2898DeriveBytes.Pbkdf2(password, entropy, 200_000, HashAlgorithmName.SHA256, 32);
    }

    private static byte[] AesProtect(byte[] plaintext)
    {
        var nonce  = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag    = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_aesKey.Value, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        // Layout: [nonce(12)][tag(16)][cipher]
        var result = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, nonce.Length);
        cipher.CopyTo(result, nonce.Length + tag.Length);
        return result;
    }

    private static byte[] AesUnprotect(byte[] blob)
    {
        int nonceLen = AesGcm.NonceByteSizes.MaxSize;
        int tagLen   = AesGcm.TagByteSizes.MaxSize;

        var nonce  = blob[..nonceLen];
        var tag    = blob[nonceLen..(nonceLen + tagLen)];
        var cipher = blob[(nonceLen + tagLen)..];
        var plain  = new byte[cipher.Length];

        using var aes = new AesGcm(_aesKey.Value, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
