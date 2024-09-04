using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SteamTokenDumper;

/// <summary>
/// AES/CBC/PKCS7, with a random IV prepended using AES/ECB/None
/// </summary>
internal static class CryptoHelper
{
    private static readonly byte[] EncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(nameof(SteamTokenDumper), GetMachineGuid())));

    public static byte[] SymmetricEncrypt(ReadOnlySpan<byte> input)
    {
        using var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = 256;
        aes.Key = EncryptionKey;

        Span<byte> iv = stackalloc byte[16];
        RandomNumberGenerator.Fill(iv);

        var encryptedIv = aes.EncryptEcb(iv, PaddingMode.None);
        var encryptedText = aes.EncryptCbc(input, iv, PaddingMode.PKCS7);

        // final output is 16 byte ecb crypted IV + cbc crypted plaintext
        var output = new byte[encryptedIv.Length + encryptedText.Length];

        Array.Copy(encryptedIv, output, encryptedIv.Length);
        Array.Copy(encryptedText, 0, output, encryptedIv.Length, encryptedText.Length);

        return output;
    }

    public static byte[] SymmetricDecrypt(ReadOnlySpan<byte> input)
    {
        using var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = 256;
        aes.Key = EncryptionKey;

        // first 16 bytes of input is the ECB encrypted IV
        Span<byte> iv = stackalloc byte[16];
        aes.DecryptEcb(input[..iv.Length], iv, PaddingMode.None);

        return aes.DecryptCbc(input[iv.Length..], iv, PaddingMode.PKCS7);
    }

    private static string GetMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var localKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            if (localKey == null)
            {
                return null;
            }

            var guid = localKey.GetValue("MachineGuid");
            return guid?.ToString();
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
