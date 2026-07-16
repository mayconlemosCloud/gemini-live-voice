using System.Runtime.InteropServices;

namespace GeminiTranslateV2;

/// <summary>
/// Windows Process Loopback Capture (Win 10 2004+): activates an IAudioClient scoped to a
/// single process (+ optionally its children) instead of the whole system, via
/// ActivateAudioInterfaceAsync on the well-known "VAD\Process_Loopback" virtual device.
/// No NuGet package exposes this (confirmed: NAudio doesn't, as of the version we use) — GUIDs
/// below are copied verbatim from NAudio's own internal (non-public, so not reusable directly)
/// interop definitions rather than typed from memory, specifically to avoid a silent
/// vtable/IID mismatch. Validated standalone against a live process (captured real, non-silent
/// audio matching expected duration/rate) before being wired into the app.
/// </summary>
public static class ProcessLoopback
{
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const int SampleRate = 48000; // fixed by the API — GetMixFormat returns E_NOTIMPL here
    public const int Channels = 2;
    public const int BitsPerSample = 32; // IEEE float

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([Out] out int activateResult, [Out, MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
    }

    // Method order must match the real COM vtable exactly (COM dispatches by slot index).
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, [MarshalAs(UnmanagedType.LPStruct)] Guid audioSessionGuid);
        int GetBufferSize(out uint bufferSize);
        long GetStreamLatency();
        int GetCurrentPadding(out int currentPadding);
        int IsFormatSupported(int shareMode, IntPtr pFormat, IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormatPointer);
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr dataBuffer, out int numFramesToRead, out int bufferFlags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int numFramesRead);
        void GetNextPacketSize(out int numFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public int ActivationType;      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE = 1
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt; // VT_BLOB = 65
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData; // 8-byte aligned on x64
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IActivateAudioInterfaceAsyncOperation> _tcs = new();
        public Task<IActivateAudioInterfaceAsyncOperation> Result => _tcs.Task;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => _tcs.SetResult(activateOperation);
    }

    /// <summary>Activates an IAudioClient that captures only the given process (and, optionally, its children).</summary>
    private static async Task<IAudioClient> ActivateAsync(uint processId, bool includeProcessTree)
    {
        var loopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            ActivationType = 1,
            TargetProcessId = processId,
            ProcessLoopbackMode = includeProcessTree ? 0 : 1
        };

        IntPtr innerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS>());
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(loopbackParams, innerPtr, false);
            var prop = new PROPVARIANT_BLOB
            {
                vt = 65,
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS>(),
                pBlobData = innerPtr
            };
            Marshal.StructureToPtr(prop, propPtr, false);

            var handler = new Handler();
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", IID_IAudioClient, propPtr, handler, out _);
            var op = await handler.Result;
            op.GetActivateResult(out int hr, out object iface);
            if (hr != 0) throw new COMException("ActivateAudioInterfaceAsync (process loopback) failed", hr);
            return (IAudioClient)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(innerPtr);
            Marshal.FreeHGlobal(propPtr);
        }
    }

    /// <summary>
    /// Activates process-loopback capture for <paramref name="processId"/> and returns a ready
    /// (Initialize'd + service-acquired) client pair. Caller owns Start()/Stop()/Dispose-equivalent
    /// lifetime via the returned handles.
    /// </summary>
    public static async Task<(IAudioClient client, IAudioCaptureClient capture)> OpenAsync(uint processId, bool includeProcessTree = true)
    {
        var client = await ActivateAsync(processId, includeProcessTree);

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
            nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
            wBitsPerSample = BitsPerSample,
            cbSize = 0
        };
        IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        int hr;
        try
        {
            Marshal.StructureToPtr(fmt, fmtPtr, false);
            const int AUDCLNT_SHAREMODE_SHARED = 0;
            const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
            const long hnsBufferDuration = 1_000_000; // 100ms — tight, event-driven, no polling
            hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                hnsBufferDuration, 0, fmtPtr, Guid.Empty);
        }
        finally { Marshal.FreeHGlobal(fmtPtr); }
        if (hr != 0) throw new COMException("IAudioClient.Initialize (process loopback) failed", hr);

        hr = client.GetService(IID_IAudioCaptureClient, out var captureObj);
        if (hr != 0) throw new COMException("IAudioClient.GetService (IAudioCaptureClient) failed", hr);

        return (client, (IAudioCaptureClient)captureObj);
    }
}
