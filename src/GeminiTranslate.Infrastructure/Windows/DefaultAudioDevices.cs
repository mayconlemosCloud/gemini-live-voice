using System.Runtime.InteropServices;
using GeminiTranslate.Core.Diagnostics;
using NAudio.CoreAudioApi;

namespace GeminiTranslate.Infrastructure.Windows;

/// <summary>
/// Troca os dispositivos padrão do Windows enquanto a tradução roda, para não ser preciso
/// configurar entrada e saída dentro do Teams, WhatsApp ou Meet — esses apps seguem o padrão do
/// sistema.
/// </summary>
/// <remarks>
/// Usa IPolicyConfig, a COM não documentada que o próprio painel "Som" chama. Não exige elevação.
/// </remarks>
public static class DefaultAudioDevices
{
    private static readonly Guid PolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid PolicyConfigVistaClsid = new("294935ce-f637-4e7c-a41b-ab255460b862");

    /// <summary>
    /// Papéis definidos de uma vez: apps de chamada usam Communications, players usam Multimedia.
    /// </summary>
    public static readonly Role[] AllRoles = [Role.Console, Role.Multimedia, Role.Communications];

    /// <summary>
    /// Define o endpoint padrão para um papel.
    /// </summary>
    /// <remarks>
    /// Duas gerações da COM convivem: o Windows 11 recente só expõe a variante "Vista" — a
    /// clássica devolve E_NOINTERFACE — então as duas são tentadas.
    /// </remarks>
    public static void Set(string deviceId, Role role)
    {
        if (TryWith<IPolicyConfig>(PolicyConfigClsid, cfg => cfg.SetDefaultEndpoint(deviceId, (int)role))) return;
        if (TryWith<IPolicyConfigVista>(PolicyConfigVistaClsid, cfg => cfg.SetDefaultEndpoint(deviceId, (int)role))) return;

        throw new InvalidOperationException(
            "este Windows não expõe PolicyConfig — não dá para trocar o dispositivo padrão.");
    }

    /// <summary>
    /// Cria a coclass, chama <paramref name="call"/>, e devolve false só quando a interface não
    /// existe nesta versão do Windows.
    /// </summary>
    private static bool TryWith<T>(Guid clsid, Func<T, int> call) where T : class
    {
        var type = Type.GetTypeFromCLSID(clsid);
        if (type is null) return false;

        object? instance;
        try { instance = Activator.CreateInstance(type); }
        catch (COMException) { return false; }
        if (instance is null) return false;

        try
        {
            if (instance is not T config) return false;

            Marshal.ThrowExceptionForHR(call(config));
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>
    /// Acha o lado de captura de um cabo virtual a partir do lado de reprodução. Por exemplo:
    /// "CABLE Input (VB-Audio Virtual Cable)" leva a "CABLE Output (VB-Audio Virtual Cable)".
    /// </summary>
    /// <remarks>
    /// Casa primeiro pelo nome do adaptador, já que os dois lados vêm do mesmo driver, e depois
    /// cai para a heurística de trocar "Input" por "Output" no nome amigável.
    /// </remarks>
    public static MMDevice? FindCaptureCounterpart(MMDeviceEnumerator enumerator, MMDevice render)
    {
        var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        var byAdapter = MatchByAdapter(captures, render);
        if (byAdapter is not null) return byAdapter;

        var swapped = SwapInputForOutput(render.FriendlyName);
        return captures.FirstOrDefault(c => c.FriendlyName.Equals(swapped, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Endpoints de captura do mesmo driver. Com vários candidatos (VB-Cable A e B, por exemplo),
    /// desempata pelo nome amigável.
    /// </summary>
    private static MMDevice? MatchByAdapter(List<MMDevice> captures, MMDevice render)
    {
        string adapter = SafeAdapterName(render);
        if (string.IsNullOrWhiteSpace(adapter)) return null;

        var candidates = captures
            .Where(c => SafeAdapterName(c).Equals(adapter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var swapped = SwapInputForOutput(render.FriendlyName);
        return candidates.FirstOrDefault(c => c.FriendlyName.Equals(swapped, StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    private static string SwapInputForOutput(string friendlyName)
    {
        int index = friendlyName.IndexOf("Input", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? friendlyName : friendlyName.Remove(index, "Input".Length).Insert(index, "Output");
    }

    /// <summary>Nome do adaptador. Alguns drivers não o expõem.</summary>
    private static string SafeAdapterName(MMDevice device)
    {
        try { return device.DeviceFriendlyName ?? ""; } catch { return ""; }
    }
}

/// <summary>
/// Aplica os padrões desejados e devolve os antigos no descarte: parar a tradução, ou fechar o
/// app, tem que deixar o Windows exatamente como estava.
/// </summary>
public sealed class DefaultDeviceScope : IDisposable
{
    private readonly List<(DataFlow Flow, Role Role, string DeviceId)> _previous = [];
    private bool _disposed;

    private DefaultDeviceScope()
    {
    }

    /// <param name="renderDeviceId">
    /// Cabo onde os apps devem TOCAR o áudio, que é o que a tradução escuta. Null não mexe na saída.
    /// </param>
    /// <param name="captureDeviceId">
    /// Cabo de onde os apps devem OUVIR a voz traduzida. Null não mexe na entrada.
    /// </param>
    public static DefaultDeviceScope Create(string? renderDeviceId, string? captureDeviceId)
    {
        var scope = new DefaultDeviceScope();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (renderDeviceId is not null) scope.Apply(enumerator, DataFlow.Render, renderDeviceId);
            if (captureDeviceId is not null) scope.Apply(enumerator, DataFlow.Capture, captureDeviceId);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
        return scope;
    }

    private void Apply(MMDeviceEnumerator enumerator, DataFlow flow, string deviceId)
    {
        foreach (var role in DefaultAudioDevices.AllRoles)
        {
            RememberCurrent(enumerator, flow, role, deviceId);
            DefaultAudioDevices.Set(deviceId, role);
        }

        Log.Write("Padrão", $"{(flow == DataFlow.Render ? "saída" : "entrada")} padrão do Windows → " +
                            NameOf(enumerator, deviceId));
    }

    /// <summary>
    /// Guarda o padrão atual ANTES de trocar. Sem padrão para esse papel, apenas não se restaura.
    /// </summary>
    private void RememberCurrent(MMDeviceEnumerator enumerator, DataFlow flow, Role role, string deviceId)
    {
        try
        {
            using var current = enumerator.GetDefaultAudioEndpoint(flow, role);
            if (current.ID != deviceId) _previous.Add((flow, role, current.ID));
        }
        catch (Exception ex)
        {
            Log.Write("Padrão", $"não consegui ler o padrão atual ({flow}/{role}): {ex.Message}");
        }
    }

    private static string NameOf(MMDeviceEnumerator enumerator, string deviceId)
    {
        try
        {
            using var device = enumerator.GetDevice(deviceId);
            return device.FriendlyName;
        }
        catch
        {
            return deviceId;
        }
    }

    /// <summary>Restaura em ordem inversa: o último papel gravado é o primeiro a voltar.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = _previous.Count - 1; i >= 0; i--)
        {
            var (flow, role, id) = _previous[i];
            try
            {
                DefaultAudioDevices.Set(id, role);
            }
            catch (Exception ex)
            {
                Log.Write("Padrão", $"falha ao restaurar {flow}/{role}: {ex.Message}");
            }
        }

        if (_previous.Count > 0) Log.Write("Padrão", "dispositivos padrão do Windows restaurados.");
    }
}

/// <summary>
/// PolicyConfig clássico. Só SetDefaultEndpoint interessa, mas a ordem da vtable precisa ser
/// respeitada: os métodos anteriores existem apenas para ocupar seus slots.
/// </summary>
[ComImport, Guid("f8679f50-850a-45de-be9b-c4c70b8bb2b0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetMixFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetDeviceFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int ResetDeviceFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetDeviceFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetProcessingPeriod();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetProcessingPeriod();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetShareMode();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetShareMode();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetPropertyValue();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetPropertyValue();

    /// <summary>Define o endpoint padrão para um papel.</summary>
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetEndpointVisibility();
}

/// <summary>
/// Variante "Vista" do PolicyConfig, a única presente no Windows 11 atual. Mesma vtable da
/// clássica, porém sem ResetDeviceFormat — daí um slot a menos antes de SetDefaultEndpoint.
/// </summary>
[ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetMixFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetDeviceFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetDeviceFormat();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetProcessingPeriod();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetProcessingPeriod();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetShareMode();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetShareMode();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int GetPropertyValue();

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetPropertyValue();

    /// <summary>Define o endpoint padrão para um papel.</summary>
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    /// <summary>Ocupa o slot de vtable correspondente.</summary>
    [PreserveSig] int SetEndpointVisibility();
}
