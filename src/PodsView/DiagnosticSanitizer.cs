using System.Text.RegularExpressions;

namespace PodsView;

/// <summary>Export a small allowlisted timing trace, not a best-effort regex scrub of raw logs.
/// Unknown events, identifiers, device names, raw payloads and free-form exceptions are omitted.</summary>
internal static class DiagnosticSanitizer
{
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    { "air", "case", "show", "hide", "startup", "stall", "session", "drop", "startup-buffer", "heartbeat", "rx", "radio", "lifecycle", "render", "adv", "parse", "watchdog", "presentation", "capture", "tick", "shapes", "present", "sys", "gap" };
    private static readonly HashSet<string> NumberKeys = new(StringComparer.Ordinal)
    { "rssi", "bound", "open", "cycle", "trusted", "fresh", "ageMs", "queueMs", "latencyMs", "paintMs", "ms",
      "pending", "expired", "popup", "lidOpen", "suspended", "closedCycle", "sinceCaseMs", "sinceOpenMs",
      "lockoutLeftMs", "toTimeoutMs", "silenceMs", "wakeMs", "wakeHold", "seq", "peer", "modelCode", "lidByte", "shape", "explicit", "believable", "callbackMs", "decisionMs", "scheduledMs", "errorCode", "major", "minor", "patch", "byteCount", "messageType", "declaredLength", "mode", "companyPrefix", "parsed", "callbacks", "apple", "rejected", "tickId", "timeoutDue", "op", "superseded", "nativeVisible", "opacityPct", "dropped", "closeEdge", "messages", "proximityOffset", "proximityMode", "framingComplete", "count",
      // 0.8.39 presentation, system and radio-gap numbers.
      "moveMs", "raised", "idleMin", "wsMb", "dpi", "frameMs", "faults", "at", "attempt", "ok", "cloaked", "topmost", "hit",
      "onScreen", "pixelDiff", "pixelCheck", "sw", "tier", "shown", "parked", "shownMs", "displayNotify", "gapMs", "others", "cycleFrom",
      "cycleTo", "openFrom", "openTo", "missed", "status" };
    private static readonly HashSet<string> WordKeys = new(StringComparer.Ordinal) { "phase", "reason", "action", "src", "tail", "block", "event", "path", "mode", "why", "state" };
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    { "radio-started", "pairing-ready", "popup", "ui", "discovery-finished", "Open", "Update", "Close", "None",
      "known", "explicit", "fresh", "change", "hold", "baseline", "static", "battery-only-model", "quiet", "stream",
      "closed-packet", "user-dismissed", "wake-quiet", "quiet-tail", "silence", "manual-timeout", "queued", "eligibility",
      "other-device", "no-reading", "ui-queue-stale", "post-filter", "other-family", "model-profile", "rejected", "suspend", "resume", "stopped", "accepted", "unclassified", "closed", "echo", "lockout", "proof", "wake-floor", "wake-repeat", "short-payload", "not-proximity", "wrong-length", "unsupported-mode", "parser-rejected", "read-error", "begin", "end", "initial", "visibility-changed", "settled", "show-return", "hide-return", "closed-case", "signal-timeout", "automatic", "manual", "exit", "seed", "save",
      // 0.8.39 words.
      "show", "hide", "park", "prepare", "frame", "verify", "retry", "refresh", "parked", "classic", "hard", "soft",
      "cloaked", "offscreen", "covered", "pixels", "-", "keepalive", "display-on", "display-change", "dwm", "render-tier",
      "display", "on", "off", "dim", "unknown", "dpi", "work-area", "popup-source", "throttle", "lock", "unlock", "logon",
      "console-connect", "console-disconnect", "session-other", "case-silence", "missed-open",
      "pixel-check-advisory", "futile", "in-ear", "out-of-case",
      // 0.8.47: the in-case door, spent cycles and late copies.
      "in-case", "spent", "late" };

    internal static string? Sanitize(string line)
    {
        var m = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([a-z-]+)(?: |$)");
        if (!m.Success || !Events.Contains(m.Groups[2].Value)) return null;
        var parts = new List<string> { m.Groups[1].Value, m.Groups[2].Value };
        foreach (Match field in Regex.Matches(line[m.Length..], @"(?:^|\s)([A-Za-z]+)=([^\s]+)"))
        {
            string key = field.Groups[1].Value, value = field.Groups[2].Value;
            if (NumberKeys.Contains(key) && Regex.IsMatch(value, @"^-?\d{1,10}$")) parts.Add(key + "=" + value);
            else if (WordKeys.Contains(key) && Words.Contains(value)) parts.Add(key + "=" + value);
        }
        return string.Join(" ", parts);
    }
}
