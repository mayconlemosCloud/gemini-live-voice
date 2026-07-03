using System.Runtime.InteropServices;
using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Sets the Windows default communication endpoints to the virtual cable while a translation
/// session is running, so meeting apps that follow the system default (WhatsApp, Teams, …) route
/// audio through the bridge without per-app fiddling. The previous defaults are captured and
/// restored on <see cref="Restore"/> so the user's machine returns to normal when they stop.
///
/// Uses the undocumented-but-stable IPolicyConfig COM interface (the same one the Sound control
/// panel uses); there is no public API to change the default endpoint programmatically.
/// </summary>
public sealed class DefaultDeviceManager
{
    // (deviceId, role) pairs we changed, with the value to restore. Captured at SetDefaults time.
    private readonly List<(string id, ERole role)> _toRestoreRender = new();
    private readonly List<(string id, ERole role)> _toRestoreCapture = new();
    private bool _applied;

    // Roles we override together so every app — multimedia, comms and "console" — follows the cable.
    private static readonly ERole[] AllRoles = { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications };

    /// <summary>
    /// Make <paramref name="renderId"/> the default render endpoint and, if given,
    /// <paramref name="captureId"/> the default capture endpoint. No-op for a null/blank id.
    /// </summary>
    public void SetDefaults(string? renderId, string? captureId)
    {
        try
        {
            var cfg = (IPolicyConfig)new CPolicyConfigClient();
            using var en = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(renderId))
                CaptureAndSet(cfg, en, DataFlow.Render, renderId!, _toRestoreRender);
            if (!string.IsNullOrWhiteSpace(captureId))
                CaptureAndSet(cfg, en, DataFlow.Capture, captureId!, _toRestoreCapture);

            _applied = true;
        }
        catch (Exception ex)
        {
            Log.Error("Default", "Não foi possível definir o dispositivo padrão automaticamente", ex);
        }
    }

    private static void CaptureAndSet(IPolicyConfig cfg, MMDeviceEnumerator en, DataFlow flow,
        string targetId, List<(string, ERole)> restore)
    {
        foreach (var role in AllRoles)
        {
            // Remember the current default so Stop puts it back exactly as it was.
            try
            {
                if (en.HasDefaultAudioEndpoint(flow, ToNAudioRole(role)))
                {
                    using var cur = en.GetDefaultAudioEndpoint(flow, ToNAudioRole(role));
                    if (cur.ID != targetId) restore.Add((cur.ID, role));
                }
            }
            catch { /* no current default for this role — nothing to restore */ }

            Marshal.ThrowExceptionForHR(cfg.SetDefaultEndpoint(targetId, role));
        }
        Log.Info("Default", $"Default {flow} → {Describe(en, targetId)} (console/multimídia/comunicação).");
    }

    /// <summary>Restores the defaults captured by <see cref="SetDefaults"/>. Safe to call twice.</summary>
    public void Restore()
    {
        if (!_applied) return;
        _applied = false;
        try
        {
            var cfg = (IPolicyConfig)new CPolicyConfigClient();
            foreach (var (id, role) in _toRestoreRender) Try(cfg, id, role);
            foreach (var (id, role) in _toRestoreCapture) Try(cfg, id, role);
            Log.Info("Default", "Dispositivos padrão restaurados.");
        }
        catch (Exception ex)
        {
            Log.Error("Default", "Falha ao restaurar o dispositivo padrão", ex);
        }
        finally
        {
            _toRestoreRender.Clear();
            _toRestoreCapture.Clear();
        }

        static void Try(IPolicyConfig cfg, string id, ERole role)
        {
            try { cfg.SetDefaultEndpoint(id, role); } catch { /* device gone — skip */ }
        }
    }

    private static string Describe(MMDeviceEnumerator en, string id)
    {
        try { using var d = en.GetDevice(id); return d.FriendlyName; } catch { return id; }
    }

    private static Role ToNAudioRole(ERole r) => r switch
    {
        ERole.eMultimedia => Role.Multimedia,
        ERole.eCommunications => Role.Communications,
        _ => Role.Console,
    };

    // ---- COM interop: IPolicyConfig ----
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is needed; the rest are declared to keep the vtable layout
        // correct so the slot we call resolves to the right method.
        int GetMixFormat(string id, IntPtr format);
        int GetDeviceFormat(string id, bool def, IntPtr format);
        int ResetDeviceFormat(string id);
        int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string id, bool def, IntPtr def_, IntPtr min);
        int SetProcessingPeriod(string id, IntPtr period);
        int GetShareMode(string id, IntPtr mode);
        int SetShareMode(string id, IntPtr mode);
        int GetPropertyValue(string id, bool store, IntPtr key, IntPtr value);
        int SetPropertyValue(string id, bool store, IntPtr key, IntPtr value);
        int SetDefaultEndpoint(string id, ERole role);
        int SetEndpointVisibility(string id, bool visible);
    }
}
