using System.Text.Json;

namespace RDPVault;

public static class VaultValidation
{
    public const int MaxFileBytes = 16 * 1024 * 1024;

    public static VaultFile Read(byte[] data)
    {
        if (data.Length is < 64 or > MaxFileBytes) throw new InvalidDataException("Choose a vault backup smaller than 16 MB.");
        using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        foreach (string required in new[] { "Kdf", "Wrap", "Data" })
            if (!root.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("This backup is missing required encrypted contents.");
        var file = JsonSerializer.Deserialize(data, VaultJsonContext.Default.VaultFile)
            ?? throw new InvalidDataException("This is not a vault backup.");
        if (file.V is not (1 or 2)) throw new InvalidDataException("This vault format is not supported by this version of RDP Vault.");
        if (file.Kdf == null || file.Kdf.Mem is < 65536 or > 262144 || file.Kdf.Iter is < 1 or > 10 || file.Kdf.Lanes is < 1 or > 16)
            throw new InvalidDataException("This vault has unsupported encryption settings.");
        RequireBytes(file.Kdf.Salt, 32);
        ValidateBlob(file.Wrap, 48);
        ValidateBlob(file.Data, null);
        if (file.Recovery != null)
        {
            RequireBytes(file.RecoverySalt, 32);
            ValidateBlob(file.Recovery, 48);
        }
        if (file.Seals == null || file.Policy == null || file.Fails == null)
            throw new InvalidDataException("This vault has a damaged security header.");
        return file;
    }

    private static void RequireBytes(string? value, int length)
    {
        try
        {
            if (value == null || Convert.FromBase64String(value).Length != length) throw new FormatException();
        }
        catch (FormatException) { throw new InvalidDataException("This backup has a damaged encryption header."); }
    }

    private static void ValidateBlob(VaultFile.WrappedBlob? blob, int? expectedLength)
    {
        if (blob == null) throw new InvalidDataException("This backup is incomplete.");
        RequireBytes(blob.Nonce, 12);
        try
        {
            int length = Convert.FromBase64String(blob.Ct).Length;
            if (length < 16 || expectedLength.HasValue && length != expectedLength) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        { throw new InvalidDataException("This backup has damaged encrypted contents."); }
    }

    public static void ValidatePayload(VaultPayload payload)
    {
        if (payload.Settings == null || payload.Profiles == null || payload.Profiles.Count > 10000)
            throw new InvalidDataException("This vault has unreadable connections or settings.");
        var ids = new HashSet<string>();
        foreach (var p in payload.Profiles)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id) || p.Name == null || p.Username == null || p.GatewayHost == null || p.Notes == null)
                throw new InvalidDataException("This vault contains an invalid connection.");
            try { _ = ConnectionEndpoint.FromProfile(p); }
            catch (Exception ex) { throw new InvalidDataException("A connection in this backup has an invalid computer address or port.", ex); }
            if (!string.IsNullOrWhiteSpace(p.GatewayHost))
            {
                if (!ConnectionEndpoint.TryParseGatewayAuthority(p.GatewayHost, out _, out string gwErr))
                    throw new InvalidDataException($"A connection in this backup has an invalid gateway address: {gwErr}");
            }
        }
    }
}
