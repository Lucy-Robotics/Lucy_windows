using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Lucy.Tests;

internal sealed class JointStates : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _stop = new();
    private volatile IReadOnlyDictionary<string, double> _latest = new Dictionary<string, double>();

    public static async Task<JointStates> ConnectAsync(Uri rosbridge)
    {
        var j = new JointStates();
        await j._ws.ConnectAsync(rosbridge, CancellationToken.None);
        var subscribe = """{"op":"subscribe","topic":"/joint_states","type":"sensor_msgs/msg/JointState","throttle_rate":100}""";
        await j._ws.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, CancellationToken.None);
        _ = j.ReceiveAsync();
        return j;
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token);
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var json = message.ToArray();
                message.SetLength(0);
                try { Update(json); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private void Update(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("msg", out var msg)) return;
        var names = msg.GetProperty("name").EnumerateArray().Select(n => n.GetString()!).ToList();
        var positions = msg.GetProperty("position").EnumerateArray().Select(p => p.GetDouble()).ToList();
        var next = new Dictionary<string, double>(_latest);
        for (var i = 0; i < Math.Min(names.Count, positions.Count); i++) next[names[i]] = positions[i];
        _latest = next;
    }

    public async Task<IReadOnlyDictionary<string, double>> WaitAsync(string what, Func<IReadOnlyDictionary<string, double>, bool> ok, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            var snapshot = _latest;
            if (ok(snapshot)) return snapshot;
            await Task.Delay(200);
        }
        throw new TimeoutException($"/joint_states never showed {what}");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ws.Dispose();
    }
}
