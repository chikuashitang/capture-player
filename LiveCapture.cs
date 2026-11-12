using System.Buffers;
using System.Threading.Channels;

namespace CapturePlayer;

// The device emits live Matroska with independent MJPEG frames and PCM. A new
// preview can join at the next cluster without reopening the hardware or replaying history.
internal sealed class LiveCapture(Stream source, bool needsKeyframe = false)
{
    const uint Segment = 0x18538067, Cluster = 0x1F43B675;
    const int QueueSize = 3, MaxElementSize = 16 * 1024 * 1024;
    readonly TaskCompletionSource<byte[]> preamble = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource clockReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate = new();
    readonly HashSet<ChannelWriter<ClusterBytes>> receivers = [];
    bool completed;
    long dropped, latestTimestamp = -1, clockOriginMs, sequence;
    internal long Dropped => Interlocked.Read(ref dropped);
    internal long LatestTimestamp => Interlocked.Read(ref latestTimestamp);
    internal async Task<double> ClockOriginAsync()
    {
        await clockReady.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        return Interlocked.Read(ref clockOriginMs) / 1000d;
    }

    internal async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            using var head = new MemoryStream();
            var header = new byte[12];
            bool live = false;
            while (true)
            {
                int idLength = await ReadVintAsync(0, 4).ConfigureAwait(false);
                int sizeLength = await ReadVintAsync(idLength, 8).ConfigureAwait(false);
                int headerLength = idLength + sizeLength;
                uint id = 0;
                for (int i = 0; i < idLength; i++) id = (id << 8) | header[i];
                if (id == Segment && !live)
                {
                    head.Write(header, 0, headerLength);
                    live = true;
                    continue;
                }
                ulong length = (uint)(header[idLength] & (0xff >> sizeLength));
                for (int i = idLength + 1; i < headerLength; i++) length = (length << 8) | header[i];
                if (length > MaxElementSize) throw new InvalidDataException("Oversize live capture element.");
                if (id == Cluster && live)
                {
                    preamble.TrySetResult(head.ToArray());
                    int total = headerLength + (int)length;
                    var data = ArrayPool<byte>.Shared.Rent(total);
                    try
                    {
                        Array.Copy(header, data, headerLength);
                        await source.ReadExactlyAsync(data.AsMemory(headerLength, (int)length)).ConfigureAwait(false);
                    }
                    catch { ArrayPool<byte>.Shared.Return(data); throw; }
                    // FFmpeg's live muxer writes a 1 ms TimestampScale and puts
                    // the timestamp first in a cluster when CRC writing is disabled.
                    if (total >= headerLength + 3 && data[headerLength] == 0xe7 && (data[headerLength + 1] & 0x80) != 0)
                    {
                        int timestampLength = data[headerLength + 1] & 0x7f;
                        if (timestampLength is > 0 and <= 8 && total >= headerLength + 2 + timestampLength)
                        {
                            long timestamp = 0;
                            for (int i = 0; i < timestampLength; i++) timestamp = (timestamp << 8) | data[headerLength + 2 + i];
                            if (timestamp > LatestTimestamp)
                            {
                                Interlocked.Exchange(ref clockOriginMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp);
                                Interlocked.Exchange(ref latestTimestamp, timestamp);
                                clockReady.TrySetResult();
                            }
                        }
                    }
                    lock (gate)
                    {
                        if (receivers.Count == 0) ArrayPool<byte>.Shared.Return(data);
                        else
                        {
                            var cluster = new ClusterBytes(data, total, receivers.Count, sequence++,
                                !needsKeyframe || HasVideoKeyframe(data.AsSpan(headerLength, (int)length)));
                            foreach (var receiver in receivers)
                                if (!receiver.TryWrite(cluster)) cluster.Return();
                        }
                    }
                }
                else
                {
                    var data = new byte[(int)length];
                    await source.ReadExactlyAsync(data).ConfigureAwait(false);
                    if (!preamble.Task.IsCompleted)
                    {
                        if (head.Length + (long)length > 1024 * 1024) throw new InvalidDataException("Oversize live capture header.");
                        head.Write(header, 0, headerLength);
                        head.Write(data);
                    }
                }
            }

            async ValueTask<int> ReadVintAsync(int offset, int max)
            {
                await source.ReadExactlyAsync(header.AsMemory(offset, 1)).ConfigureAwait(false);
                int count = 1;
                for (int bit = 0x80; bit != 0 && (header[offset] & bit) == 0; bit >>= 1) count++;
                if (count > max) throw new InvalidDataException("Invalid live capture element header.");
                if (count > 1) await source.ReadExactlyAsync(header.AsMemory(offset + 1, count - 1)).ConfigureAwait(false);
                return count;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; }
        finally
        {
            preamble.TrySetException(failure ?? new EndOfStreamException());
            lock (gate)
            {
                completed = true;
                foreach (var receiver in receivers) receiver.TryComplete();
            }
        }
    }

    // FFmpeg writes the first mapped track (video) as track 1. A SimpleBlock
    // with the keyframe bit set can start decoding after a skipped cluster.
    static bool HasVideoKeyframe(ReadOnlySpan<byte> cluster)
    {
        while (!cluster.IsEmpty)
        {
            int idSize = VintLength(cluster[0]);
            if (idSize is < 1 or > 4 || cluster.Length <= idSize) return false;
            int sizeSize = VintLength(cluster[idSize]);
            if (sizeSize < 1 || cluster.Length < idSize + sizeSize) return false;
            ulong size = (uint)(cluster[idSize] & (0xff >> sizeSize));
            for (int i = 1; i < sizeSize; i++) size = (size << 8) | cluster[idSize + i];
            var payload = cluster[(idSize + sizeSize)..];
            if (size > (ulong)payload.Length) return false;
            if (idSize == 1 && cluster[0] == 0xa3 && size >= 4 && payload[0] == 0x81 && (payload[3] & 0x80) != 0)
                return true;
            cluster = payload[(int)size..];
        }
        return false;

        static int VintLength(byte first)
        {
            for (int size = 1, mask = 0x80; mask != 0; size++, mask >>= 1)
                if ((first & mask) != 0) return size;
            return -1;
        }
    }

    internal async Task FeedAsync(Stream target, CancellationToken cancel)
    {
        var queue = Channel.CreateBounded<ClusterBytes>(new BoundedChannelOptions(QueueSize)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        }, discarded => { discarded.Return(); Interlocked.Increment(ref dropped); });
        lock (gate)
        {
            if (completed) queue.Writer.TryComplete();
            else receivers.Add(queue.Writer);
        }
        try
        {
            await target.WriteAsync(await preamble.Task.WaitAsync(cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
            long? previous = null;
            bool waitForKeyframe = needsKeyframe;
            await foreach (var cluster in queue.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                try
                {
                    if (needsKeyframe && previous is long last && cluster.Sequence != last + 1) waitForKeyframe = true;
                    previous = cluster.Sequence;
                    if (waitForKeyframe && !cluster.Keyframe) { Interlocked.Increment(ref dropped); continue; }
                    waitForKeyframe = false;
                    await target.WriteAsync(cluster.Data.AsMemory(0, cluster.Length), cancel).ConfigureAwait(false);
                }
                finally { cluster.Return(); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { }
        finally
        {
            lock (gate) receivers.Remove(queue.Writer);
            while (queue.Reader.TryRead(out var cluster)) cluster.Return();
            try { target.Dispose(); } catch (IOException) { }
        }
    }

    sealed class ClusterBytes(byte[] data, int length, int readers, long sequence, bool keyframe)
    {
        internal byte[] Data { get; } = data;
        internal int Length { get; } = length;
        internal long Sequence { get; } = sequence;
        internal bool Keyframe { get; } = keyframe;
        int remaining = readers;
        internal void Return() { if (Interlocked.Decrement(ref remaining) == 0) ArrayPool<byte>.Shared.Return(Data); }
    }
}
