namespace Common.Interfaces;

public interface ICryptoService
{
    string? EncryptText(string? plainText, string context);
    string? DecryptText(string? cipherText, string context);
}
