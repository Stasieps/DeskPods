namespace PodsView;

/// <summary>Allowlisted message framing ONLY, never encrypted bytes, identity, MAC,
/// name, full hex, or a parser bypass. Normalization mirrors the existing parser.</summary>
internal static class AdvertisementDiagnostics
{
    internal static string Header(ReadOnlySpan<byte> payload)
    {
        int length = payload.Length;
        bool prefix = payload.Length >= 29 && payload[0] == 0x4C && payload[1] == 0;
        if (prefix) payload = payload[2..];
        int kind = payload.Length > 0 ? payload[0] : -1;
        int declared = payload.Length > 1 ? payload[1] : -1;
        // Mode is a framing byte only in the known proximity-pairing message.
        int mode = kind == 7 && payload.Length > 2 ? payload[2] : -1;
        int at=0, messages=0, proximityOffset=-1, proximityMode=-1;
        while (at+2 <= payload.Length)
        {
            int size=payload[at+1];
            if (size > payload.Length-at-2) break;
            messages++;
            if (payload[at]==7 && size==25 && proximityOffset<0)
            { proximityOffset=at; proximityMode=payload[at+2]; }
            at+=size+2;
        }
        return $"byteCount={length} messageType={kind} declaredLength={declared} mode={mode} companyPrefix={(prefix ? 1 : 0)} messages={messages} proximityOffset={proximityOffset} proximityMode={proximityMode} framingComplete={(at==payload.Length ? 1 : 0)}";
    }
    internal static string FailureReason(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 29 && payload[0] == 0x4C && payload[1] == 0) payload = payload[2..];
        if (payload.Length < 27) return "short-payload";
        if (payload[0] != 7) return "not-proximity";
        if (payload[1] != 25) return "wrong-length";
        if (payload[2] != 1) return "unsupported-mode";
        return "parser-rejected";
    }
}
