using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;

namespace Common.Services;

public class CryptoService : ICryptoService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string? EncryptText(string? plainText, byte[] secretKey)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return plainText;

        byte[] key = SHA256.HashData(secretKey);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[TagSize];
        byte[] cipherText = new byte[plainBytes.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plainBytes, cipherText, tag);
        }

        byte[] result = new byte[NonceSize + TagSize + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipherText, 0, result, NonceSize + TagSize, cipherText.Length);

        return Convert.ToBase64String(result);
    }

    public string? DecryptText(string? cipherText, byte[] secretKey)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return cipherText;

        byte[] fullCipher = Convert.FromBase64String(cipherText);

        if (fullCipher.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Invalid cipher text.");
        }

        byte[] key = SHA256.HashData(secretKey);

        var nonce = fullCipher.AsSpan(0, NonceSize);
        var tag = fullCipher.AsSpan(NonceSize, TagSize);
        var cipherBytes = fullCipher.AsSpan(NonceSize + TagSize);

        byte[] decryptedBytes = new byte[cipherBytes.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Decrypt(nonce, cipherBytes, tag, decryptedBytes);
        }

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}
