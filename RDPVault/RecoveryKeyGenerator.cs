using System;
using System.Security.Cryptography;
using System.Text;

namespace RDPVault;

/// <summary>
/// ISSUE #2 - REPLACES THE OLD "RecoveryKeyGenerator".
///
/// What was wrong before: the old class produced 24 words drawn at random from a
/// 230-word stub list, it was never called from anywhere in the app, and - most
/// importantly - the phrase was not connected to the vault in any way. Typing it
/// back in could never have unlocked anything. The documentation advertised it as
/// "the ultimate offline backup". It was decoration.
///
/// What this is: a real 256-bit recovery secret that is bound to the vault's master
/// key at creation time (see VaultCrypto.CreateVault -> file.Recovery). Losing the
/// master password is now survivable.
///
/// Format: 52 characters of Crockford Base32, shown in 13 groups of 4:
///     R7K4-9M2X-...-QW3T
/// Crockford's alphabet omits I, L, O and U, and the reader maps the shapes people
/// confuse anyway (I/l -> 1, O -> 0, U -> V) so a slightly sloppy transcription off
/// paper still works.
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // 32 chars, no I L O U
    private const int EntropyBytes = 32;   // 256 bits
    private const int CodeChars = 52;      // ceil(256 / 5)
    private const int GroupSize = 4;

    /// <summary>Generates a fresh code, already formatted with dashes for printing.</summary>
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

    /// <summary>
    /// Turns whatever the user typed into the exact string the KDF expects.
    /// Case, spaces, dashes and the classic look-alike characters are all forgiven.
    /// </summary>
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

    /// <summary>Cheap client-side sanity check before spending a second on Argon2.</summary>
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
