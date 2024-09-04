using System;
using System.IO;
using System.Security.Cryptography;

namespace SteamTokenDumper;
internal static class CryptoHelper
{
    /// <summary>
    /// Performs an encryption using AES/CBC/PKCS7 with an input byte array and key, with a random IV prepended using AES/ECB/None
    /// </summary>
    public static byte[] SymmetricEncryptWithIV(byte[] input, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);

        using var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = 256;

        byte[] cryptedIv;

        // encrypt iv using ECB and provided key
#pragma warning disable CA5358 // Review cipher mode usage with cryptography experts
        aes.Mode = CipherMode.ECB;
#pragma warning restore CA5358 // Review cipher mode usage with cryptography experts
        aes.Padding = PaddingMode.None;

#pragma warning disable CA5401 // Do not use CreateEncryptor with non-default IV
        using (var aesTransform = aes.CreateEncryptor(key, null))
        {
            cryptedIv = aesTransform.TransformFinalBlock(iv, 0, iv.Length);
        }
#pragma warning restore CA5401 // Do not use CreateEncryptor with non-default IV

        // encrypt input plaintext with CBC using the generated (plaintext) IV and the provided key
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

#pragma warning disable CA5401 // Do not use CreateEncryptor with non-default IV
        using (var aesTransform = aes.CreateEncryptor(key, iv))
        using (var ms = new MemoryStream())
        using (var cs = new CryptoStream(ms, aesTransform, CryptoStreamMode.Write))
        {
            cs.Write(input, 0, input.Length);
            cs.FlushFinalBlock();

            var cipherText = ms.ToArray();

            // final output is 16 byte ecb crypted IV + cbc crypted plaintext
            var output = new byte[cryptedIv.Length + cipherText.Length];

            Array.Copy(cryptedIv, 0, output, 0, cryptedIv.Length);
            Array.Copy(cipherText, 0, output, cryptedIv.Length, cipherText.Length);

            return output;
        }
#pragma warning restore CA5401 // Do not use CreateEncryptor with non-default IV
    }

    /// <summary>
    /// Performs an encryption using AES/CBC/PKCS7 with an input byte array and key, with a random IV prepended using AES/ECB/None
    /// </summary>
    public static byte[] SymmetricEncrypt(byte[] input, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);

        var iv = RandomNumberGenerator.GetBytes(16);
        return SymmetricEncryptWithIV(input, key, iv);
    }
}
