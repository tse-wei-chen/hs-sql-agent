namespace Common.Interfaces;

public interface ICryptoService
{
    string? EncryptText(string? plainText, byte[] secretKey);
    string? DecryptText(string? cipherText, byte[] secretKey);
}
