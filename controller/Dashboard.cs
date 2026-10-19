using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class Dashboard
{
    internal static JsonObject? CompletedDecision(JsonObject? latest, JsonObject? completed, string? session)
    {
        foreach (var candidate in new[] { latest, completed })
            if (candidate?["request"] != null && candidate["response"] != null && (session == null || candidate["snapshot"]?["session_id"]?.GetValue<string>() == session))
                return candidate.DeepClone().AsObject();
        return null;
    }
    internal static async Task ExportAsync(Options options)
    {
        if (options.Input == null) throw new ArgumentException("export requires --input <recording.jsonl>");
        var decisions = new Dictionary<string, JsonObject>();
        foreach (var line in File.ReadLines(options.Input).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var record = JsonNode.Parse(line)!.AsObject();
            if (record["snapshot"] is not JsonObject snapshot || record["request"] == null) continue;
            var key = snapshot["session_id"]?.GetValue<string>() + ":" + snapshot["sequence"]?.ToJsonString();
            decisions[key] = record;
        }
        if (decisions.Count == 0) throw new InvalidDataException("Recording has no observation/request pairs to replay.");
        var timeline = new JsonArray(decisions.Values.Select(r => (JsonNode)r.DeepClone()).ToArray());
        string? png = null;
        // Exported replays deliberately omit local screenshots. A neighboring
        // capture.png may contain private desktop pixels and is never bundled.
        var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "dashboard.html"));
        html = html.Replace("Raw observations and controller decisions", "Recorded observations and decisions · offline replay");
        html = html.Replace("<main>", """
        <div style="max-width:1440px;margin:20px auto 0;padding:0 32px;display:flex;gap:16px;align-items:center;flex-wrap:wrap">
        <label for="replay-position">Decision</label><input id="replay-position" type="range" min="0" value="0" style="flex:1;min-width:150px;accent-color:var(--accent)"><output id="replay-label"></output>
        <button id="replay-play" style="background:var(--fg);color:var(--bg);padding:8px 18px;border:0;border-radius:3px;cursor:pointer">Play</button>
        </div><main>
        """);
        var start = html.IndexOf("async function poll()", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Dashboard replay insertion point missing.");
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        var payload = new JsonObject { ["decisions"] = timeline, ["image"] = png }.ToJsonString();
        // Default JSON escaping preserves script boundaries, including any '<' in source data.
        var replay = "const replayData=" + payload + ";\n" + """
        const timeline=replayData.decisions;let replayIndex=0,playTimer=null;
        const slider=$('replay-position');slider.max=timeline.length-1;
        function showReplay(){const d=timeline[replayIndex];render({state:d.snapshot,decision:d});$('connection').textContent='Replay · '+(replayIndex+1)+' / '+timeline.length+' · #'+d.snapshot.sequence;$('replay-label').textContent=(replayIndex+1)+' / '+timeline.length;slider.value=replayIndex;}
        function stopPlayback(){if(playTimer){clearInterval(playTimer);playTimer=null}$('replay-play').textContent='Play';}
        slider.addEventListener('input',()=>{stopPlayback();replayIndex=Number(slider.value);showReplay()});
        $('replay-play').addEventListener('click',()=>{if(playTimer){stopPlayback();return}if(replayIndex===timeline.length-1)replayIndex=0;$('replay-play').textContent='Pause';showReplay();playTimer=setInterval(()=>{if(replayIndex>=timeline.length-1){stopPlayback();return}replayIndex++;showReplay()},650)});
        if(replayData.image){$('game-capture').src=replayData.image;$('game-capture').hidden=false;$('capture-status').textContent='Final game image (static). The terrain and decisions below follow the selected recording.'}else{$('capture-status').textContent='No final game image was available during export.'}
        showReplay();
        """;
        html = html[..start] + replay + html[end..];
        html = html.Replace("Read-only monitor. Inputs expire automatically. F8 disables bridge control; Ctrl+C stops a trial. A stale display never implies an active controller.", "Offline recording. Playback changes the displayed observation and decision only; it sends no requests and does not control the game.");
        var destination = Path.Combine(options.Output, "replay.html");
        await File.WriteAllTextAsync(destination, html, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Exported {timeline.Count} decisions with {(png == null ? "no" : "one static final")} game image: {Path.GetFullPath(destination)}");
    }

    internal static async Task RunAsync(Options options)
    {
        // Kestrel binds port 0 atomically: the OS chooses a free loopback port.
        // No HTTP.sys URL reservation or public interface is required.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var expectedOrigin = $"http://127.0.0.1:{context.Connection.LocalPort}";
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != context.Connection.LocalPort
                || (context.Request.Headers.TryGetValue("Origin", out var origin)
                    && (origin.Count != 1 || origin[0] != expectedOrigin)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'";
            await next();
        });
        app.MapGet("/", async () => Results.Content(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "dashboard.html")), "text/html"));
        app.MapGet("/data", () =>
        {
            var data = new JsonObject();
            try { data["state"] = Controller.ReadState(options.State); } catch (IOException) { }
            JsonObject? latest = null, completed = null;
            try { latest = Controller.ReadState(Path.Combine(options.Output, "latest.json")); } catch (IOException) { }
            try { completed = Controller.ReadState(Path.Combine(options.Output, "last-completed.json")); } catch (IOException) { }
            data["decision"] = CompletedDecision(latest, completed, data["state"]?["session_id"]?.GetValue<string>());
            if (latest?["status"]?.GetValue<string>() == "awaiting_api" && latest["response"] == null && latest["snapshot"]?["session_id"]?.GetValue<string>() == data["state"]?["session_id"]?.GetValue<string>()) data["pending"] = latest.DeepClone();
            try { data["run"] = Controller.ReadState(Path.Combine(options.Output, "run-status.json")); } catch (IOException) { }
            var capture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.State))!, "capture.png");
            if (File.Exists(capture)) data["capture_utc"] = File.GetLastWriteTimeUtc(capture).ToString("O");
            return Results.Json(data);
        });
        app.MapGet("/capture.png", async () =>
        {
            var capture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.State))!, "capture.png");
            try
            {
                await using var stream = new FileStream(capture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16 * 1024 * 1024) return Results.StatusCode(413);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                return Results.Bytes(bytes.ToArray(), "image/png");
            }
            catch (IOException) { return Results.NotFound(); }
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await app.StartAsync(cancellation.Token);
            Console.WriteLine($"Read-only dashboard: {app.Urls.Single()}/ (stops after {options.Seconds}s; Ctrl+C stops sooner)");
            await Task.Delay(Timeout.Infinite, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; await app.StopAsync(); }
    }
}
