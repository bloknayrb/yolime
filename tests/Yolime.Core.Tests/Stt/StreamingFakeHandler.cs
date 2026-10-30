using System.Net;
using System.Text;

namespace Yolime.Core.Tests.Stt;

/// <summary>
/// A handler that reads each request body as it arrives (through <c>CopyToAsync(stream, ct)</c>, as the real handler
/// does), so a test can see bytes reach the "server" before the session completes, and can tell a body that ended from
/// one that was torn down.
/// </summary>
public sealed class StreamingFakeHandler : HttpMessageHandler
{
    private readonly object _gate = new();

    /// <summary>Answers once the body has ended. Return null to throw a network error instead.</summary>
    public Func<int, HttpResponseMessage?> Respond { get; set; } = _ => Ok("hello");

    /// <summary>If set, request N is answered at once with this, without waiting for its body.</summary>
    public Func<int, HttpResponseMessage?>? RespondEarly { get; set; }

    /// <summary>If set for request N, the connection drops after that many body bytes, before the body ends.</summary>
    public Func<int, int?>? DropAfterBytes { get; set; }

    /// <summary>Set when a <see cref="DropAfterBytes"/> drop has happened.</summary>
    public TaskCompletionSource Dropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<Recorded> Requests { get; } = [];

    public static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent($$"""{"text":"{{text}}"}""", Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("""{"error":{"message":"x"}}""", Encoding.UTF8, "application/json") };

    public Recorded Request(int i)
    {
        lock (_gate) return Requests[i];
    }

    public int Count
    {
        get { lock (_gate) return Requests.Count; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var rec = new Recorded(request);
        int index;
        lock (_gate)
        {
            index = Requests.Count;
            Requests.Add(rec);
        }
        if (DropAfterBytes?.Invoke(index) is { } drop)
        {
            var reading = rec.ReadAsync(ct);
            await rec.WaitForBytes(drop);
            // A dead connection: the next write fails, as a real socket's does.
            rec.Drop();
            _ = reading.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            Dropped.TrySetResult();
            throw new HttpRequestException("connection reset");
        }
        if (RespondEarly?.Invoke(index) is { } early)
        {
            // The server answered before the body ended: the rest of the body is still read, then dropped.
            _ = rec.ReadAsync(ct);
            return early;
        }
        await rec.ReadAsync(ct);
        ct.ThrowIfCancellationRequested();
        return Respond(index) ?? throw new HttpRequestException("connection reset");
    }

    public sealed class Recorded(HttpRequestMessage request)
    {
        private readonly RecordingStream _body = new();

        public HttpRequestMessage Message { get; } = request;

        /// <summary>No Content-Length: sent chunked, as a stream of unknown length is.</summary>
        public bool Chunked { get; } = request.Content?.Headers.ContentLength is null;
        public int Flushes => _body.Flushes;
        public byte[] Body => _body.Bytes;
        public string BodyText => Encoding.Latin1.GetString(Body);

        /// <summary>The body ended normally (it wasn't torn down).</summary>
        public TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForBytes(int n) => _body.WaitFor(n);

        internal void Drop() => _body.Dropped = true;

        internal async Task ReadAsync(CancellationToken ct)
        {
            try
            {
                await Message.Content!.CopyToAsync(_body, ct);
                Ended.TrySetResult(true);
            }
            catch (Exception) when (!Ended.Task.IsCompleted)
            {
                Ended.TrySetResult(false);
                throw;
            }
        }
    }

    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _bytes = new();
        private readonly List<(int N, TaskCompletionSource Tcs)> _waiters = [];
        private int _flushes;

        public int Flushes => Volatile.Read(ref _flushes);

        public volatile bool Dropped;

        public byte[] Bytes
        {
            get { lock (_gate) return _bytes.ToArray(); }
        }

        public Task WaitFor(int n)
        {
            lock (_gate)
            {
                if (_bytes.Length >= n) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((n, tcs));
                return tcs.Task;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Dropped) throw new IOException("connection reset");
            lock (_gate)
            {
                _bytes.Write(buffer);
                foreach (var w in _waiters.Where(w => _bytes.Length >= w.N).ToList())
                {
                    w.Tcs.TrySetResult();
                    _waiters.Remove(w);
                }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush()
        {
            if (Dropped) throw new IOException("connection reset");
            Interlocked.Increment(ref _flushes);
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            Flush();
            return Task.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
