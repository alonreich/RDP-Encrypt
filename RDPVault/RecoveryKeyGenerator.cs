using System;
using System.Security.Cryptography;
using System.Text;

namespace RDPVault;

public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EntropyBytes = 32;
    private const int CodeChars = 52;
    private const int GroupSize = 4;

    public static string Generate()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(EntropyBytes);
        try
        {
            return Format(Base32Encode(entropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(CodeChars);
        foreach (char raw in input)
        {
            char c = char.ToUpperInvariant(raw);
            c = c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => c
            };
            if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool LooksWellFormed(string input) => Normalize(input).Length == CodeChars;

    public static string Format(string bare)
    {
        var sb = new StringBuilder(bare.Length + bare.Length / GroupSize);
        for (int i = 0; i < bare.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) sb.Append('-');
            sb.Append(bare[i]);
        }
        return sb.ToString();
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder(CodeChars);
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }
}
