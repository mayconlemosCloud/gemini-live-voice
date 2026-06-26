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

    public static AudioDeviceInfo? DefaultDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return new AudioDeviceInfo(device.ID, device.FriendlyName, flow);
    }
}
