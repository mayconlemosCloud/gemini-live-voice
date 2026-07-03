using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;

namespace GeminiLiveTranslate.Audio;

/// <summary>Lightweight description of an audio endpoint shown in the device pickers.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, DataFlow Flow)
{
    public override string ToString() => Name;
}

/// <summary>Enumerates and resolves WASAPI audio endpoints (mics and speakers).</summary>
public static class AudioDeviceService
{
    public static List<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow));
            device.Dispose();
        }
        Log.Info("Devices", $"{flow}: {result.Count} dispositivo(s) — {string.Join(", ", result.Select(d => d.Name))}");
        return result;
    }

    /// <summary>Resolves an MMDevice by id. Caller owns the returned device and must dispose it.</summary>
    public static MMDevice GetById(string id)
    {
        var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDevice(id);
        Log.Info("Devices", $"Resolvido: '{dev.FriendlyName}' ({dev.DataFlow}, estado={dev.State})");
        return dev;
    }

    /// <summary>
    /// Finds the capture endpoint that pairs with a virtual-cable render endpoint — e.g. the
    /// render "CABLE Input (VB-Audio Virtual Cable)" pairs with the capture "CABLE Output
    /// (VB-Audio Virtual Cable)". Matches on the shared brand suffix in parentheses, so the
    /// meeting app's microphone can be pointed at the side that carries your translated voice.
    /// Returns null when no matching capture device is found.
    /// </summary>
    public static AudioDeviceInfo? CaptureCounterpart(string renderId)
    {
        string? renderName;
        try { using var dev = GetById(renderId); renderName = dev.FriendlyName; }
        catch { return null; }

        var brand = ParenSuffix(renderName);
        var captures = GetDevices(DataFlow.Capture);

        // Prefer a capture device sharing the brand suffix (e.g. "(VB-Audio Virtual Cable)").
        var match = brand is not null
            ? captures.FirstOrDefault(d => string.Equals(ParenSuffix(d.Name), brand, StringComparison.OrdinalIgnoreCase))
            : null;
        // Fall back to any capture endpoint that looks like a virtual cable.
        match ??= captures.FirstOrDefault(d =>
            d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        return match;
    }

    /// <summary>Returns the "(…)" portion of a device name, or null if there is none.</summary>
    private static string? ParenSuffix(string name)
    {
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        return open >= 0 && close > open ? name.Substring(open, close - open + 1) : null;
    }

    public static AudioDeviceInfo? DefaultDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return new AudioDeviceInfo(device.ID, device.FriendlyName, flow);
    }
}
