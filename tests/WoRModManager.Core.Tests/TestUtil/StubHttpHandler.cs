namespace WoRModManager.Core.Tests.TestUtil;

// Replays queued responders in order; the last one repeats. Never touches the network.
public sealed class StubHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _q = new(responders);
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var f = _q.Count > 1 ? _q.Dequeue() : _q.Peek();
        return Task.FromResult(f(request));
    }
}
