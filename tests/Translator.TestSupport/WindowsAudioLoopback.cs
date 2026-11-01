using System.Runtime.InteropServices;

namespace Translator.TestSupport;

/// <summary>Read-only WASAPI render-endpoint observer. Never changes volume, mute or device configuration.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsAudioLoopback : IDisposable
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Enumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out nint devices);

        void GetDefaultAudioEndpoint(int flow, int role, out Device device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Device
    {
        void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioClient
    {
        void Initialize(int shared, uint flags, long duration, long period, nint format, nint session);

        void GetBufferSize(out uint frames);

        void GetStreamLatency(out long latency);

        void GetCurrentPadding(out uint frames);

        [PreserveSig]
        int IsFormatSupported(int shared, nint format, out nint closest);

        void GetMixFormat(out nint format);

        void GetDevicePeriod(out long standard, out long minimum);

        void Start();

        void Stop();

        void Reset();

        void SetEventHandle(nint handle);

        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CaptureClient
    {
        void GetBuffer(out nint samples, out uint frames, out uint flags, out ulong position, out ulong timestamp);

        void ReleaseBuffer(uint frames);

        void GetNextPacketSize(out uint frames);
    }

    private readonly object _enumerator, _device, _client, _capture;
    private readonly AudioClient _audio;
    private readonly CaptureClient _reader;
    private readonly int _blockAlign, _bits;
    private readonly bool _floating;
    public int SampleRate
    {
        get;
    }
    public int Channels
    {
        get;
    }

    public WindowsAudioLoopback()
    {
        _enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!)!;
        ((Enumerator)_enumerator).GetDefaultAudioEndpoint(0, 0, out Device endpoint);
        _device = endpoint;
        Guid id = typeof(AudioClient).GUID;
        endpoint.Activate(ref id, 23, 0, out _client);
        _audio = (AudioClient)_client;
        _audio.GetMixFormat(out nint format);
        try
        {
            int tag = (ushort)Marshal.ReadInt16(format);
            Channels = (ushort)Marshal.ReadInt16(format, 2);
            SampleRate = Marshal.ReadInt32(format, 4);
            _blockAlign = (ushort)Marshal.ReadInt16(format, 12);
            _bits = (ushort)Marshal.ReadInt16(format, 14);
            if (tag == 0xfffe)
            {
                tag = Marshal.ReadInt32(format, 24);
            }

            _floating = tag == 3;
            if (!(_floating && _bits == 32 || tag == 1 && _bits is 16 or 32))
            {
                throw new NotSupportedException($"Unsupported read-only loopback format {tag}/{_bits}");
            }

            _audio.Initialize(0, 0x20000, 10000000, 0, format, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }

        id = typeof(CaptureClient).GUID;
        _audio.GetService(ref id, out _capture);
        _reader = (CaptureClient)_capture;
        _audio.Start();
    }

    public float[] ReadFor(TimeSpan duration)
    {
        var samples = new List<float>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            _reader.GetNextPacketSize(out uint count);
            while (count != 0)
            {
                _reader.GetBuffer(out nint pointer, out uint frames, out uint flags, out _, out _);
                try
                {
                    var bytes = new byte[checked((int)frames * _blockAlign)];
                    if ((flags & 2) == 0)
                    {
                        Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    }

                    for (int frame = 0; frame < frames; frame++)
                    {
                        double mono = 0;
                        for (int ch = 0; ch < Channels; ch++)
                        {
                            int at = frame * _blockAlign + ch * _bits / 8;
                            mono += _floating
                                ? BitConverter.ToSingle(bytes, at)
                                : _bits == 16
                                    ? BitConverter.ToInt16(bytes, at) / 32768.0
                                    : BitConverter.ToInt32(bytes, at) / 2147483648.0;
                        }

                        samples.Add((float)(mono / Channels));
                    }
                }
                finally
                {
                    _reader.ReleaseBuffer(frames);
                }

                _reader.GetNextPacketSize(out count);
            }

            Thread.Sleep(5);
        }
        while (watch.Elapsed < duration);
        return samples.ToArray();
    }

    public void Dispose()
    {
        try
        {
            _audio.Stop();
        }
        finally
        {
            foreach (var value in new[]
            {
                _capture,
                _client,
                _device,
                _enumerator
            }

            )
            {
                Marshal.ReleaseComObject(value);
            }
        }
    }
}
