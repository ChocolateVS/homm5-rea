using Microsoft.Win32.SafeHandles;

namespace H5.Core.IO;

/// <summary>
/// Random-access, read-only byte source behind a <see cref="PakArchive"/>. Desktop uses a file
/// (<see cref="FileRandomAccessSource"/>); the web build wraps a browser <c>File</c>/<c>Blob</c> and
/// reads slices on demand (data.pak is 1.4 GB — it is never loaded whole). Browser blobs can only be
/// read asynchronously on the main thread, so a source may report <see cref="SupportsSyncRead"/> = false;
/// <see cref="PakFileSystem"/> then serves synchronous reads from data prefetched with
/// <see cref="PakFileSystem.PrefetchAsync"/>.
/// </summary>
public interface IRandomAccessSource : IDisposable
{
    /// <summary>Display name / path (diagnostics and <see cref="PakFileSystem.SourceOf"/>).</summary>
    string Name { get; }
    long Length { get; }
    bool SupportsSyncRead { get; }
    /// <summary>Fills <paramref name="buffer"/> with the bytes at <paramref name="offset"/>; throws on a short read.</summary>
    void ReadAt(long offset, Span<byte> buffer);
    ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);
}

/// <summary>File-backed source using positional reads (no shared stream position, thread-safe).</summary>
public sealed class FileRandomAccessSource : IRandomAccessSource
{
    readonly SafeFileHandle _handle;
    public string Name { get; }
    public long Length { get; }
    public bool SupportsSyncRead => true;

    public FileRandomAccessSource(string path)
    {
        Name = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer[done..], offset + done);
            if (n <= 0) throw new EndOfStreamException($"{Name}: short read at {offset + done}");
            done += n;
        }
    }

    public async ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = await RandomAccess.ReadAsync(_handle, buffer[done..], offset + done, ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException($"{Name}: short read at {offset + done}");
            done += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>In-memory source (tests, small archives).</summary>
public sealed class MemoryRandomAccessSource(byte[] data, string name = "memory", bool supportsSyncRead = true) : IRandomAccessSource
{
    public string Name => name;
    public long Length => data.LongLength;
    public bool SupportsSyncRead => supportsSyncRead;
    /// <summary>Number of read calls served (tests check that reads are sliced, not whole-file).</summary>
    public int ReadCount { get; private set; }
    public long BytesRead { get; private set; }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        if (!supportsSyncRead) throw new NotSupportedException($"{name}: synchronous reads are not supported");
        Copy(offset, buffer);
    }

    public ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        Copy(offset, buffer.Span);
        return ValueTask.CompletedTask;
    }

    void Copy(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > data.LongLength)
            throw new EndOfStreamException($"{name}: read past end at {offset}");
        data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        ReadCount++;
        BytesRead += buffer.Length;
    }

    public void Dispose() { }
}

/// <summary>A synchronous read was requested for pak data that is only available asynchronously and has not been prefetched.</summary>
public sealed class PakEntryNotReadyException(string path)
    : InvalidOperationException($"Pak entry not prefetched (async-only source): {path}")
{
    public string Path { get; } = path;
}
