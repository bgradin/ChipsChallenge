using System.Text;

namespace Win16_Runtime;

/// <summary>Task-owned read-only HFILE compatibility. Paths come from original callers;
/// no game files, data formats or file contents are synthesized.</summary>
public sealed class Win16TaskFiles : IDisposable
{
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#74",
        "KERNEL.#81",
        "KERNEL.#82",
        "KERNEL.#84"
    };

    private readonly SegmentMemory _memory;
    private readonly string _directory;
    private readonly Dictionary<ushort, FileStream> _streams = new();
    private int _nextHandle = 3;
    private bool _disposed;
    public int OpenCount => _streams.Count;

    public Win16TaskFiles(SegmentMemory memory, string directory)
    {
        _memory = memory;
        _directory = Path.GetFullPath(directory);
    }

    private string Resolve(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            path = path.Replace('\\', Path.DirectorySeparatorChar);
        }

        return Path.GetFullPath(path, _directory);
    }

    private void ValidateBuffer(uint pointer, int bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        _memory.ValidateBufferWrite((ushort)(pointer >> 16), (ushort)pointer, bytes);
    }

    private static ushort DosError(Exception error) => error switch
    {
        FileNotFoundException => 2,
        DirectoryNotFoundException => 3,
        UnauthorizedAccessException => 5,
        PathTooLongException => 3,
        _ => 5
    };

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var contract = Win16ExportContracts.Get(identity);
        var saved = state;

        uint Arg(int n) => contract.Argument(n, _memory, saved);

        uint result = 0xffff;
        if (identity == "KERNEL.#74")
        {
            uint ofstruct = Arg(1);
            ValidateBuffer(ofstruct, 136);
            string name = Win16TaskKernelServices.ReadString(_memory, Arg(0));
            uint style = Arg(2);
            var output = new byte[136];
            output[0] = 136;
            output[1] = 1;
            if (_nextHandle > 0x7fff || _streams.Count >= 4096 || (style & ~0x70u) != 0 || (style & 0x70) > 0x40)
            {
                output[2] = 5;
                Win16TaskKernelServices.WriteBuffer(_memory, ofstruct, output);
            }
            else
            {
                FileStream? stream = null;
                try
                {
                    string path = Resolve(name);
                    byte[] pathBytes = Encoding.Latin1.GetBytes(path);
                    if (pathBytes.Length > 127)
                    {
                        throw new PathTooLongException();
                    }

                    var share = (style & 0x70) switch
                    {
                        0x10 => FileShare.None,
                        0x20 => FileShare.Read,
                        0x30 => FileShare.Write,
                        _ => FileShare.ReadWrite
                    };
                    _streams.EnsureCapacity(_streams.Count + 1);
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
                    pathBytes.CopyTo(output, 8);
                    Win16TaskKernelServices.WriteBuffer(_memory, ofstruct, output);
                    ushort handle = (ushort)_nextHandle++;
                    _streams.Add(handle, stream);
                    stream = null;
                    result = handle;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    output[2] = (byte)DosError(error);
                    Win16TaskKernelServices.WriteBuffer(_memory, ofstruct, output);
                }
                finally
                {
                    stream?.Dispose();
                }
            }
        }
        else
        {
            ushort handle = (ushort)Arg(0);
            if (identity == "KERNEL.#84")
            {
                result = uint.MaxValue;
            }

            if (_streams.TryGetValue(handle, out var stream))
            {
                if (identity == "KERNEL.#81")
                {
                    _streams.Remove(handle);
                    try
                    {
                        stream.Dispose();
                        result = 0;
                    }
                    catch (IOException)
                    {
                        result = 0xffff;
                    }
                }
                else if (identity == "KERNEL.#82")
                {
                    uint buffer = Arg(1);
                    int count = (ushort)Arg(2);
                    ValidateBuffer(buffer, count);
                    var bytes = new byte[count];
                    long before = stream.Position;
                    try
                    {
                        int read = stream.Read(bytes);
                        if (read != 0)
                        {
                            Win16TaskKernelServices.WriteBuffer(_memory, buffer, bytes.AsSpan(0, read));
                        }

                        result = (uint)read;
                    }
                    catch (IOException)
                    {
                        stream.Position = before;
                        result = 0xffff;
                    }
                }
                else if (identity == "KERNEL.#84")
                {
                    int displacement = unchecked((int)Arg(1));
                    uint origin = Arg(2);
                    long before = stream.Position;
                    if (origin <= 2)
                    {
                        try
                        {
                            long position = stream.Seek(displacement, (SeekOrigin)origin);
                            if (position <= int.MaxValue)
                            {
                                result = (uint)position;
                            }
                            else
                            {
                                stream.Position = before;
                            }
                        }
                        catch (IOException)
                        {
                            stream.Position = before;
                        }
                    }
                }
                else
                {
                    throw new NotSupportedException(identity);
                }
            }
        }

        state.AX = (ushort)result;
        if (contract.ReturnBytes == 4)
        {
            state.DX = (ushort)(result >> 16);
        }

        return contract.CleanupBytes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var stream in _streams.Values)
        {
            stream.Dispose();
        }

        _streams.Clear();
        _disposed = true;
    }
}
