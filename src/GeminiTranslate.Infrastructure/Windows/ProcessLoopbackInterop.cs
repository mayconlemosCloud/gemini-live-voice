using System.Runtime.InteropServices;

namespace GeminiTranslate.Infrastructure.Windows;

/// <summary>
/// Process Loopback Capture do Windows (10 2004+): ativa um IAudioClient limitado a um único
/// processo, e opcionalmente a seus filhos, em vez do sistema inteiro — via
/// ActivateAudioInterfaceAsync no dispositivo virtual conhecido <c>VAD\Process_Loopback</c>.
/// </summary>
/// <remarks>
/// Nenhum pacote NuGet expõe isto: o NAudio, na versão usada aqui, não expõe. Os GUIDs abaixo
/// foram copiados literalmente das definições internas de interop do próprio NAudio — que não
/// são públicas, e portanto não reutilizáveis diretamente — em vez de digitados de memória,
/// justamente para evitar uma divergência silenciosa de vtable ou IID. Validado isoladamente
/// contra um processo real, capturando áudio não-silencioso com duração e taxa esperadas, antes
/// de ser ligado ao app.
/// </remarks>
public static class ProcessLoopbackInterop
{
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";

    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeProcessTree = 0;
    private const int ExcludeProcessTree = 1;

    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;

    private const int WaveFormatIeeeFloat = 3;
    private const ushort VariantTypeBlob = 65;

    /// <summary>Buffer de 100 ms: apertado, orientado a evento, sem polling.</summary>
    private const long BufferDurationHns = 1_000_000;

    /// <summary>Taxa fixada pela API — GetMixFormat devolve E_NOTIMPL neste dispositivo.</summary>
    public const int SampleRate = 48000;

    /// <summary>Canais fixados pela API.</summary>
    public const int Channels = 2;

    /// <summary>Bits por amostra: IEEE float.</summary>
    public const int BitsPerSample = 32;

    /// <summary>Sinaliza a conclusão da ativação assíncrona da interface de áudio.</summary>
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        /// <summary>Chamado pelo sistema quando a ativação termina.</summary>
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Resultado de uma ativação assíncrona de interface de áudio.</summary>
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        /// <summary>Recupera o HRESULT e a interface ativada.</summary>
        void GetActivateResult(
            [Out] out int activateResult,
            [Out, MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
    }

    /// <summary>
    /// Cliente de áudio WASAPI. A ordem dos métodos precisa espelhar a vtable COM real, porque o
    /// COM despacha por índice de slot.
    /// </summary>
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        /// <summary>Inicializa o fluxo com formato, modo e flags.</summary>
        int Initialize(int shareMode, int streamFlags, long bufferDurationHns, long periodicityHns,
            IntPtr format, [MarshalAs(UnmanagedType.LPStruct)] Guid audioSessionGuid);

        /// <summary>Tamanho do buffer do dispositivo, em quadros.</summary>
        int GetBufferSize(out uint bufferSize);

        /// <summary>Latência do fluxo, em unidades de 100 ns.</summary>
        long GetStreamLatency();

        /// <summary>Quadros ainda não consumidos no buffer.</summary>
        int GetCurrentPadding(out int currentPadding);

        /// <summary>Verifica se um formato é aceito.</summary>
        int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);

        /// <summary>Formato do mix do dispositivo.</summary>
        int GetMixFormat(out IntPtr deviceFormat);

        /// <summary>Períodos padrão e mínimo do dispositivo.</summary>
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        /// <summary>Começa a capturar.</summary>
        int Start();

        /// <summary>Para de capturar.</summary>
        int Stop();

        /// <summary>Reinicia o fluxo.</summary>
        int Reset();

        /// <summary>Registra o evento sinalizado quando há buffer pronto.</summary>
        int SetEventHandle(IntPtr eventHandle);

        /// <summary>Obtém uma interface de serviço associada, como IAudioCaptureClient.</summary>
        int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object servicePointer);
    }

    /// <summary>Leitura dos pacotes capturados.</summary>
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        /// <summary>Empresta o próximo bloco de amostras.</summary>
        void GetBuffer(out IntPtr dataBuffer, out int framesToRead, out int bufferFlags,
            out long devicePosition, out long qpcPosition);

        /// <summary>Devolve o bloco emprestado.</summary>
        void ReleaseBuffer(int framesRead);

        /// <summary>Quadros disponíveis no próximo pacote.</summary>
        void GetNextPacketSize(out int framesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT com um blob. O ponteiro fica alinhado em 8 bytes no x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IActivateAudioInterfaceAsyncOperation> _completed = new();

        public Task<IActivateAudioInterfaceAsyncOperation> Result => _completed.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) =>
            _completed.SetResult(activateOperation);
    }

    /// <summary>
    /// Ativa a captura de loopback do processo indicado e devolve o par já inicializado. O
    /// chamador é dono do ciclo de vida: Start, Stop e liberação.
    /// </summary>
    /// <param name="processId">Processo cujo áudio será capturado.</param>
    /// <param name="includeProcessTree">Se os processos filhos também entram na captura.</param>
    public static async Task<(IAudioClient Client, IAudioCaptureClient Capture)> OpenAsync(
        uint processId, bool includeProcessTree = true)
    {
        var client = await ActivateAsync(processId, includeProcessTree);
        Initialize(client);

        int hr = client.GetService(AudioCaptureClientIid, out var captureService);
        if (hr != 0) throw new COMException("IAudioClient.GetService (IAudioCaptureClient) falhou", hr);

        return (client, (IAudioCaptureClient)captureService);
    }

    private static async Task<IAudioClient> ActivateAsync(uint processId, bool includeProcessTree)
    {
        var loopbackParams = new ProcessLoopbackParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = processId,
            ProcessLoopbackMode = includeProcessTree ? IncludeProcessTree : ExcludeProcessTree
        };

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ProcessLoopbackParams>());
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(loopbackParams, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                VariantType = VariantTypeBlob,
                Size = (uint)Marshal.SizeOf<ProcessLoopbackParams>(),
                Data = paramsPtr
            }, variantPtr, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, AudioClientIid, variantPtr, handler, out _);

            var operation = await handler.Result;
            operation.GetActivateResult(out int hr, out object activated);
            if (hr != 0) throw new COMException("ActivateAudioInterfaceAsync (process loopback) falhou", hr);

            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
            Marshal.FreeHGlobal(variantPtr);
        }
    }

    private static void Initialize(IAudioClient client)
    {
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatIeeeFloat,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
            BlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
            BitsPerSample = BitsPerSample,
            ExtraSize = 0
        };

        IntPtr formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        int hr;
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);
            hr = client.Initialize(ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDurationHns, 0, formatPtr, Guid.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        if (hr != 0) throw new COMException("IAudioClient.Initialize (process loopback) falhou", hr);
    }
}
