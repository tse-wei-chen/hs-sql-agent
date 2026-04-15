using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

public class CryptoService : ICryptoService
{
	public string? EncryptText(string? plainText, byte[] secretKey)
	{
		if (string.IsNullOrWhiteSpace(plainText)) return plainText;

		using var aes = Aes.Create();
		aes.Key = SHA256.HashData(secretKey);
		aes.GenerateIV();

		using var ms = new MemoryStream();
		ms.Write(aes.IV, 0, aes.IV.Length);

		using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
		using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
		using (var sw = new StreamWriter(cs))
		{
			sw.Write(plainText);
		}

		return Convert.ToBase64String(ms.ToArray());
	}

	public string? DecryptText(string? cipherText, byte[] secretKey)
	{
		if (string.IsNullOrWhiteSpace(cipherText)) return cipherText;

		var fullCipher = Convert.FromBase64String(cipherText);

		using var aes = Aes.Create();
		aes.Key = SHA256.HashData(secretKey);

		int ivLength = aes.BlockSize / 8;
		if (fullCipher.Length < ivLength)
		{
			throw new CryptographicException("Invalid cipher text.");
		}

		var iv = fullCipher.AsSpan(0, ivLength);
		aes.IV = iv.ToArray();

		using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
		using var msDecrypt = new MemoryStream(fullCipher, ivLength, fullCipher.Length - ivLength);
		using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
		using var srDecrypt = new StreamReader(csDecrypt);

		return srDecrypt.ReadToEnd();
	}
}
