using System.Diagnostics;
using System.Text.Json;

namespace ProBackUp;
// One budget for every destination write in a backup, shared across files and ZIP output.
internal sealed class UploadBandwidth
{
    private readonly double bytesPerSecond;
    private readonly Func<double> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim reservations = new(1, 1);
    private double next;
    internal bool Enabled => bytesPerSecond > 0;
    internal int ChunkSize => (int)Math.Clamp(bytesPerSecond / 20, 1, 65536);
    internal UploadBandwidth(BackupJob job) : this(job.LimitUploadSpeed ? job.MaxUploadMbps : 0) { }
    internal UploadBandwidth(decimal mbps, Func<double>? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        if (mbps < 0 || mbps > 100000) throw new ArgumentOutOfRangeException(nameof(mbps));
        bytesPerSecond = (double)(mbps * 1000000m / 8m);
        var timer = Stopwatch.StartNew(); now = clock ?? (() => timer.Elapsed.TotalSeconds);
        delay = wait ?? ((duration, token) => Task.Delay(duration, token));
    }
    internal async Task Pace(int bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (!Enabled || bytes == 0) return;
        double seconds;
        await reservations.WaitAsync(token);
        try { var current = now(); next = Math.Max(current, next) + bytes / bytesPerSecond; seconds = next - current; }
        finally { reservations.Release(); }
        // No initial burst or saved credit after hashing/verification/idle time.
        if (seconds > 0) await delay(TimeSpan.FromSeconds(seconds), token);
    }
    internal Stream Open(string path, CancellationToken token) => Wrap(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, Enabled ? 1 : 131072, FileOptions.Asynchronous | (Enabled ? FileOptions.WriteThrough : FileOptions.None)), token);
    internal Stream Wrap(Stream output, CancellationToken token) => Enabled ? new PacedStream(output, this, token) : output;
    internal async Task WriteManifest<T>(string path, T manifest, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = Open(temp, token)) { await JsonSerializer.SerializeAsync(output, manifest, Store.Json, token); await output.FlushAsync(token); }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private sealed class PacedStream(Stream inner, UploadBandwidth bandwidth, CancellationToken backupToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { backupToken.ThrowIfCancellationRequested(); inner.Flush(); }
        public override Task FlushAsync(CancellationToken token) { backupToken.ThrowIfCancellationRequested(); return inner.FlushAsync(token); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), backupToken).AsTask().GetAwaiter().GetResult();
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, backupToken);
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, bandwidth.ChunkSize);
                await bandwidth.Pace(count, linked.Token);
                await inner.WriteAsync(buffer[..count], linked.Token); buffer = buffer[count..];
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
