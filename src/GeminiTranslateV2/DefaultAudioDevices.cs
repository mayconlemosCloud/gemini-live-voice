using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GeminiTranslateV2;

/// <summary>
/// Troca os dispositivos padrão do Windows enquanto a tradução roda, para você não precisar
/// configurar entrada/saída dentro do Teams, WhatsApp, Meet etc. — esses apps seguem o padrão
/// do sistema. Usa IPolicyConfig, a COM não documentada que o próprio painel "Som" chama;
/// não exige elevação.
/// </summary>
public static class DefaultAudioDevices
{
    private static readonly Guid PolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid PolicyConfigVistaClsid = new("294935ce-f637-4e7c-a41b-ab255460b862");

    /// <summary>
    /// Define o endpoint padrão para um papel (Console/Multimedia/Communications).
    /// Duas gerações da COM convivem: o Windows 11 recente só expõe a variante "Vista"
    /// (a clássica devolve E_NOINTERFACE), então tentamos as duas.
    /// </summary>
    public static void Set(string deviceId, Role role)
    {
        if (TryWith<IPolicyConfig>(PolicyConfigClsid, cfg => cfg.SetDefaultEndpoint(deviceId, (int)role))) return;
        if (TryWith<IPolicyConfigVista>(PolicyConfigVistaClsid, cfg => cfg.SetDefaultEndpoint(deviceId, (int)role))) return;
        throw new InvalidOperationException("este Windows não expõe PolicyConfig — não dá para trocar o dispositivo padrão.");
    }

    /// <summary>Cria a coclass, chama <paramref name="call"/> e devolve false só se a interface não existir aqui.</summary>
    private static bool TryWith<T>(Guid clsid, Func<T, int> call) where T : class
    {
        var type = Type.GetTypeFromCLSID(clsid);
        if (type is null) return false;

        object? obj;
        try { obj = Activator.CreateInstance(type); }
        catch (COMException) { return false; }
        if (obj is null) return false;

        try
        {
            if (obj is not T cfg) return false;   // E_NOINTERFACE: tenta a próxima geração
            Marshal.ThrowExceptionForHR(call(cfg));
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(obj);
        }
    }

    /// <summary>Define os três papéis de uma vez — apps de chamada usam Communications, players usam Multimedia.</summary>
    public static void SetAllRoles(string deviceId)
    {
        foreach (var role in AllRoles) Set(deviceId, role);
    }

    public static readonly Role[] AllRoles = { Role.Console, Role.Multimedia, Role.Communications };

    /// <summary>
    /// Acha o lado de captura de um cabo virtual a partir do lado de reprodução.
    /// Ex.: "CABLE Input (VB-Audio Virtual Cable)" → "CABLE Output (VB-Audio Virtual Cable)".
    /// Casa primeiro pelo nome do adaptador (mesmo driver dos dois lados), depois cai para a
    /// heurística Input→Output do nome amigável.
    /// </summary>
    public static MMDevice? FindCaptureCounterpart(MMDeviceEnumerator enumerator, MMDevice render)
    {
        var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        string adapter = SafeAdapterName(render);
        if (!string.IsNullOrWhiteSpace(adapter))
        {
            var byAdapter = captures.Where(c => SafeAdapterName(c).Equals(adapter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byAdapter.Count == 1) return byAdapter[0];
            // Vários endpoints do mesmo driver (ex.: VB-Cable A+B): desempata pelo nome.
            if (byAdapter.Count > 1)
            {
                var swapped = SwapInputForOutput(render.FriendlyName);
                var exact = byAdapter.FirstOrDefault(c => c.FriendlyName.Equals(swapped, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
                return byAdapter[0];
            }
        }

        var name = SwapInputForOutput(render.FriendlyName);
        return captures.FirstOrDefault(c => c.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string SwapInputForOutput(string friendlyName)
    {
        // "CABLE Input (VB-Audio Virtual Cable)" / "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)"
        int i = friendlyName.IndexOf("Input", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? friendlyName : friendlyName.Remove(i, "Input".Length).Insert(i, "Output");
    }

    /// <summary>Nome do adaptador (PKEY_DeviceInterface_FriendlyName). Alguns drivers não expõem.</summary>
    private static string SafeAdapterName(MMDevice d)
    {
        try { return d.DeviceFriendlyName ?? ""; } catch { return ""; }
    }
}

/// <summary>
/// Aplica os padrões desejados e devolve os antigos no Dispose — parar a tradução (ou fechar
/// o app) tem que deixar o Windows exatamente como estava.
/// </summary>
public sealed class DefaultDeviceScope : IDisposable
{
    private readonly List<(DataFlow Flow, Role Role, string DeviceId)> _previous = new();
    private bool _disposed;

    private DefaultDeviceScope() { }

    /// <param name="renderDeviceId">Cabo onde os apps devem TOCAR o áudio (o que a tradução escuta). Null = não mexe na saída.</param>
    /// <param name="captureDeviceId">Cabo de onde os apps devem OUVIR sua voz traduzida. Null = não mexe na entrada.</param>
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
            // Falhou no meio do caminho: desfaz o que já tinha trocado antes de propagar.
            scope.Dispose();
            throw;
        }
        return scope;
    }

    private void Apply(MMDeviceEnumerator enumerator, DataFlow flow, string deviceId)
    {
        foreach (var role in DefaultAudioDevices.AllRoles)
        {
            // Guarda o atual ANTES de trocar; se não houver padrão para esse papel, só não restauramos.
            try
            {
                using var current = enumerator.GetDefaultAudioEndpoint(flow, role);
                if (current.ID != deviceId) _previous.Add((flow, role, current.ID));
            }
            catch (Exception ex)
            {
                Log.Write("Padrão", $"não consegui ler o padrão atual ({flow}/{role}): {ex.Message}");
            }

            DefaultAudioDevices.Set(deviceId, role);
        }

        string name;
        try { using var d = enumerator.GetDevice(deviceId); name = d.FriendlyName; } catch { name = deviceId; }
        Log.Write("Padrão", $"{(flow == DataFlow.Render ? "saída" : "entrada")} padrão do Windows → {name}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ordem inversa: o último papel gravado é o primeiro a voltar.
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

[ComImport, Guid("f8679f50-850a-45de-be9b-c4c70b8bb2b0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    // Só SetDefaultEndpoint interessa, mas a ordem da vtable precisa ser respeitada:
    // os métodos anteriores existem apenas para ocupar seus slots.
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility();
}

/// <summary>
/// Variante "Vista" (CLSID 294935ce-…): é a única presente no Windows 11 atual.
/// Mesma vtable da clássica, porém sem ResetDeviceFormat — daí um slot a menos antes
/// de SetDefaultEndpoint.
/// </summary>
[ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility();
}
