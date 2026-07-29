using System.Reflection;
using System.Text;
using DataTray.Core.Security;

namespace DataTray.Core.Tests.Security;

public class MasterPasswordCryptoTests
{
    private static byte[] Key(string password = "correct horse") =>
        MasterPasswordCrypto.DeriveKey(password, "AAAAAAAAAAAAAAAAAAAAAA==");

    [Fact]
    public void VerifierPlaintextIsPinned()
    {
        // Reaches for the private field on purpose. The value is never exposed through the public surface,
        // yet changing it silently locks every user out of their own secrets — a find-and-replace over the
        // old brand name would have done exactly that. Pinning the bytes turns that into a red build.
        var field = typeof(MasterPasswordCrypto)
            .GetField("VerifierPlaintext", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var actual = Assert.IsType<byte[]>(field!.GetValue(null));
        Assert.Equal("datatray-master-verify-v1", Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void VerifierAcceptsTheSamePasswordAndRejectsAnother()
    {
        var verifier = MasterPasswordCrypto.CreateVerifier(Key());

        Assert.True(MasterPasswordCrypto.CheckVerifier(Key(), verifier));
        Assert.False(MasterPasswordCrypto.CheckVerifier(Key("battery staple"), verifier));
    }

    [Fact]
    public void CheckVerifierRejectsGarbageInsteadOfThrowing()
    {
        Assert.False(MasterPasswordCrypto.CheckVerifier(Key(), "not base64 at all"));
        Assert.False(MasterPasswordCrypto.CheckVerifier(Key(), Convert.ToBase64String(new byte[40])));
    }

    [Fact]
    public void SecretRoundTripsAndIsMarkedAsEncrypted()
    {
        var encrypted = MasterPasswordCrypto.EncryptSecret(Key(), "hunter2");

        Assert.True(MasterPasswordCrypto.IsEncrypted(encrypted));
        Assert.False(MasterPasswordCrypto.IsEncrypted("hunter2"));
        Assert.Equal("hunter2", MasterPasswordCrypto.DecryptSecret(Key(), encrypted));
    }

    [Fact]
    public void DeriveKeyIsDeterministicPerSaltAndPassword()
    {
        Assert.Equal(Key(), Key());
        Assert.NotEqual(Key(), MasterPasswordCrypto.DeriveKey("correct horse", MasterPasswordCrypto.NewSalt()));
    }
}
