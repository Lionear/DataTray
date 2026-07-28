using System.Security.Cryptography;
using System.Text;

namespace DataTray.Core.Security;

/// <summary>
/// Crypto primitives for the optional master password: PBKDF2 key derivation and AES-GCM encryption of
/// individual secrets, plus a verifier so a typed password can be checked without storing the key. Secret
/// blobs carry a short marker so plaintext (feature off / pre-encryption) is distinguishable per value —
/// which keeps enable/disable migrations robust even if interrupted.
/// </summary>
public static class MasterPasswordCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;               // AES-256
    private const int Iterations = 210_000;       // PBKDF2-HMAC-SHA256
    private const string SecretMarker = "menc1:"; // prefix on encrypted secret values

    /// <summary>
    /// The constant encrypted by <see cref="CreateVerifier"/> and re-derived by <see cref="CheckVerifier"/>.
    /// Changing it invalidates every stored verifier, so every existing master password stops unlocking with
    /// no way back — the secrets themselves stay encrypted under a key this value has no part in. It was safe
    /// to rebrand only because the feature is opt-in and no released build had it enabled anywhere. From here
    /// on it is fixed: a future change needs a second, legacy-accepting constant and a re-issue on unlock.
    /// The <c>-v1</c> suffix exists for exactly that.
    /// </summary>
    private static readonly byte[] VerifierPlaintext = Encoding.UTF8.GetBytes("datatray-master-verify-v1");

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize));

    public static byte[] DeriveKey(string password, string saltBase64) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Convert.FromBase64String(saltBase64),
            Iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>Encrypt a fixed constant so a later typed password can be verified by re-deriving the key
    /// and attempting to decrypt it.</summary>
    public static string CreateVerifier(byte[] key) => EncryptRaw(key, VerifierPlaintext);

    public static bool CheckVerifier(byte[] key, string verifierBase64)
    {
        try
        {
            return DecryptRaw(key, verifierBase64).AsSpan().SequenceEqual(VerifierPlaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsEncrypted(string value) => value.StartsWith(SecretMarker, StringComparison.Ordinal);

    public static string EncryptSecret(byte[] key, string plaintext) =>
        SecretMarker + EncryptRaw(key, Encoding.UTF8.GetBytes(plaintext));

    public static string DecryptSecret(byte[] key, string markedValue) =>
        Encoding.UTF8.GetString(DecryptRaw(key, markedValue[SecretMarker.Length..]));

    private static string EncryptRaw(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, cipher, tag);

        var buffer = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(buffer, 0);
        tag.CopyTo(buffer, NonceSize);
        cipher.CopyTo(buffer, NonceSize + TagSize);
        return Convert.ToBase64String(buffer);
    }

    private static byte[] DecryptRaw(byte[] key, string base64)
    {
        var buffer = Convert.FromBase64String(base64);
        var nonce = buffer.AsSpan(0, NonceSize);
        var tag = buffer.AsSpan(NonceSize, TagSize);
        var cipher = buffer.AsSpan(NonceSize + TagSize);

        var plaintext = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plaintext);
        return plaintext;
    }
}
