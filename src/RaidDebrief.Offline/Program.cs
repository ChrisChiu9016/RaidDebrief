using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using RaidDebrief.Core;
using RaidDebrief.Offline;
using RaidDebrief.UI;

var options = OfflineHostOptions.Parse(args);
var record = CaptureJson.Load(options.FixturePath);
if (options.Verify)
{
    var report = ReplayVerifier.Verify(record, options.SeekIterations);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var state = new ReplayHostState(new ReplaySession(record), new SvgArenaRenderer());

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls(options.Url);
builder.Services.Configure<JsonOptions>(json =>
    json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton(state);

var app = builder.Build();
app.MapGet("/", () => Results.Content(ReplayPage.Html, "text/html; charset=utf-8"));
app.MapGet("/api/metadata", (ReplayHostState replay) => Results.Json(replay.Metadata));
app.MapPost("/api/control", (ReplayControl command, ReplayHostState replay) =>
{
    try
    {
        return Results.Json(replay.Apply(command));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

Console.WriteLine(
    $"Raid Debrief Offline Replay: {record.CaptureId} | {state.Metadata.DurationMilliseconds} ms | {options.Url}");
await app.RunAsync();

internal sealed record OfflineHostOptions(
    string FixturePath,
    string Url,
    bool Verify,
    int SeekIterations)
{
    public static OfflineHostOptions Parse(string[] arguments)
    {
        string? fixturePath = null;
        var url = "http://127.0.0.1:5198";
        var verify = false;
        var seekIterations = 20_000;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--fixture" when index + 1 < arguments.Length:
                    fixturePath = arguments[++index];
                    break;
                case "--url" when index + 1 < arguments.Length:
                    url = arguments[++index];
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--seek-iterations" when index + 1 < arguments.Length
                    && int.TryParse(arguments[index + 1], out var parsedSeekIterations):
                    seekIterations = parsedSeekIterations;
                    index++;
                    break;
                default:
                    throw new ArgumentException(Usage);
            }
        }

        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            throw new ArgumentException(Usage);
        }

        if (seekIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                seekIterations,
                "Seek iterations must be positive.");
        }

        return new OfflineHostOptions(Path.GetFullPath(fixturePath), url, verify, seekIterations);
    }
    private const string Usage =
        "Usage: RaidDebrief.Offline --fixture <PullRecord.json|PullRecord.json.gz> " +
        "[--url http://127.0.0.1:5198] [--verify] [--seek-iterations 20000]";
}

internal sealed class ReplayHostState
{
    private readonly object gate = new();
    private readonly ReplaySession session;
    private readonly SvgArenaRenderer renderer;

    public ReplayHostState(ReplaySession session, SvgArenaRenderer renderer)
    {
        this.session = session;
        this.renderer = renderer;
        this.Metadata = CreateMetadata(session);
    }

    public ReplayMetadata Metadata { get; }

    public ReplayFrame Apply(ReplayControl command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (this.gate)
        {
            switch (command.Action.ToLowerInvariant())
            {
                case "play":
                    this.session.Play();
                    break;
                case "pause":
                    this.session.Pause();
                    break;
                case "seek":
                    this.session.Seek(command.TimestampMilliseconds
                        ?? throw new ArgumentException("Seek requires timestampMilliseconds."));
                    break;
                case "advance":
                    this.session.Advance(command.ElapsedMilliseconds
                        ?? throw new ArgumentException("Advance requires elapsedMilliseconds."));
                    break;
                default:
                    throw new ArgumentException($"Unknown replay action '{command.Action}'.");
            }

            return this.CreateFrame();
        }
    }

    private static ReplayMetadata CreateMetadata(ReplaySession session)
    {
        var entries = session.Timeline.Events;
        var events = new ReplayEvent[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            ref readonly var entry = ref entries[index];
            events[index] = new ReplayEvent(
                entry.TimestampMilliseconds,
                entry.ObservedEvent.Type.ToString(),
                entry.OriginalRecordedIndex);
        }

        return new ReplayMetadata(
            session.Record.CaptureId,
            session.DurationMilliseconds,
            events);
    }

    private ReplayFrame CreateFrame()
    {
        var actors = this.session.Scene.Actors;
        var playerCount = 0;
        var battleNpcCount = 0;
        for (var index = 0; index < actors.Length; index++)
        {
            if (actors[index].Kind == ArenaActorMarkerKind.Player)
            {
                playerCount++;
            }
            else
            {
                battleNpcCount++;
            }
        }

        var events = this.session.EventsThroughCurrentTime;
        string? lastEvent = null;
        if (events.Length > 0)
        {
            ref readonly var entry = ref events[^1];
            lastEvent = $"{FormatTimestamp(entry.TimestampMilliseconds)}  {entry.ObservedEvent.Type}";
        }

        return new ReplayFrame(
            this.session.CurrentTimeMilliseconds,
            this.session.DurationMilliseconds,
            this.session.IsPlaying,
            actors.Length,
            playerCount,
            battleNpcCount,
            this.session.Scene.Waymarks.Length,
            events.Length,
            lastEvent,
            this.renderer.Render(this.session.Scene));
    }

    private static string FormatTimestamp(long timestampMilliseconds) =>
        TimeSpan.FromMilliseconds(timestampMilliseconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
}

internal sealed record ReplayControl(
    string Action,
    long? TimestampMilliseconds = null,
    long? ElapsedMilliseconds = null);

internal sealed record ReplayMetadata(
    Guid CaptureId,
    long DurationMilliseconds,
    ReplayEvent[] Events);

internal sealed record ReplayEvent(
    long TimestampMilliseconds,
    string Type,
    int OriginalRecordedIndex);

internal sealed record ReplayFrame(
    long TimestampMilliseconds,
    long DurationMilliseconds,
    bool IsPlaying,
    int ActorCount,
    int PlayerCount,
    int BattleNpcCount,
    int WaymarkCount,
    int EventCount,
    string? LastEvent,
    string Svg);

internal static class ReplayPage
{
    public const string Html = """
        <!doctype html>
        <html lang="zh-Hant">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>Raid Debrief Offline Replay</title>
          <style>
            :root { color-scheme: dark; font-family: Inter, "Noto Sans TC", sans-serif; }
            * { box-sizing: border-box; }
            body { margin: 0; background: #090d14; color: #e6edf7; }
            main { width: min(1180px, calc(100vw - 32px)); margin: 20px auto; }
            header { display: flex; justify-content: space-between; align-items: end; gap: 16px; margin-bottom: 14px; }
            h1 { margin: 0; font-size: 22px; font-weight: 650; }
            #capture { color: #8290a6; font-family: ui-monospace, monospace; font-size: 12px; }
            .layout { display: grid; grid-template-columns: minmax(480px, 1fr) 300px; gap: 14px; }
            .panel { background: #111823; border: 1px solid #263246; border-radius: 10px; box-shadow: 0 12px 35px #0007; }
            #arena { min-height: 640px; display: grid; place-items: center; padding: 12px; overflow: hidden; }
            #arena svg { width: 100%; height: auto; max-height: calc(100vh - 160px); }
            aside { padding: 16px; }
            .time { font: 650 28px ui-monospace, monospace; margin-bottom: 4px; }
            .status { color: #91a0b7; min-height: 20px; font-size: 13px; }
            .stats { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin: 18px 0; }
            .stat { background: #0b111a; border-radius: 7px; padding: 9px; }
            .stat b { display: block; font: 650 19px ui-monospace, monospace; }
            .stat span { color: #7f8da3; font-size: 12px; }
            .controls { display: flex; gap: 8px; margin-top: 14px; }
            button { border: 1px solid #35455e; border-radius: 7px; background: #1b2637; color: #e6edf7; padding: 8px 15px; cursor: pointer; }
            button:hover { background: #263650; }
            input[type=range] { width: 100%; accent-color: #61a7ff; }
            canvas { width: 100%; height: 66px; background: #090e16; border: 1px solid #263246; border-radius: 6px; cursor: crosshair; }
            .label { color: #8d9ab0; font-size: 12px; margin: 16px 0 6px; }
            @media (max-width: 880px) { .layout { grid-template-columns: 1fr; } #arena { min-height: 480px; } }
          </style>
        </head>
        <body>
          <main>
            <header><h1>Raid Debrief · Offline Replay</h1><div id="capture"></div></header>
            <div class="layout">
              <section id="arena" class="panel" aria-label="2D arena replay"></section>
              <aside class="panel">
                <div id="time" class="time">00:00.000</div>
                <div id="lastEvent" class="status">尚無事件</div>
                <div class="controls">
                  <button id="play" type="button">Play</button>
                  <button id="pause" type="button">Pause</button>
                </div>
                <div class="label">Timeline / Scrub</div>
                <input id="scrub" type="range" min="0" max="0" value="0" step="1" aria-label="Replay timeline">
                <canvas id="timeline" aria-label="Recorded events; click to seek"></canvas>
                <div class="stats">
                  <div class="stat"><b id="players">0</b><span>Players</span></div>
                  <div class="stat"><b id="npcs">0</b><span>Battle NPCs</span></div>
                  <div class="stat"><b id="waymarks">0</b><span>Waymarks</span></div>
                  <div class="stat"><b id="events">0</b><span>Events through time</span></div>
                </div>
              </aside>
            </div>
          </main>
          <script type="module">
            const arena = document.querySelector('#arena');
            const scrub = document.querySelector('#scrub');
            const timeline = document.querySelector('#timeline');
            const context = timeline.getContext('2d');
            let metadata;
            let frame;
            let advancing = false;
            let lastAdvance = performance.now();
            let seekGeneration = 0;

            const formatTime = milliseconds => {
              const total = Math.max(0, Math.round(milliseconds));
              const minutes = Math.floor(total / 60000);
              const seconds = Math.floor(total / 1000) % 60;
              const millis = total % 1000;
              return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
            };

            async function command(action, values = {}) {
              const response = await fetch('/api/control', {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ action, ...values }),
              });
              if (!response.ok) throw new Error(await response.text());
              return response.json();
            }

            function displayFrame(next) {
              frame = next;
              arena.innerHTML = next.svg;
              scrub.value = next.timestampMilliseconds;
              document.querySelector('#time').textContent = formatTime(next.timestampMilliseconds);
              document.querySelector('#lastEvent').textContent = next.lastEvent ?? '尚無事件';
              document.querySelector('#players').textContent = next.playerCount;
              document.querySelector('#npcs').textContent = next.battleNpcCount;
              document.querySelector('#waymarks').textContent = next.waymarkCount;
              document.querySelector('#events').textContent = next.eventCount;
              drawTimeline(next.timestampMilliseconds);
            }

            function drawTimeline(timestamp) {
              const ratio = devicePixelRatio || 1;
              const width = Math.max(1, timeline.clientWidth);
              const height = Math.max(1, timeline.clientHeight);
              timeline.width = Math.round(width * ratio);
              timeline.height = Math.round(height * ratio);
              context.setTransform(ratio, 0, 0, ratio, 0, 0);
              context.clearRect(0, 0, width, height);
              context.fillStyle = '#0b111a';
              context.fillRect(0, 0, width, height);
              const duration = Math.max(1, metadata.durationMilliseconds);
              for (const event of metadata.events) {
                const x = event.timestampMilliseconds / duration * width;
                context.fillStyle = event.type === 'Death' ? '#ff6478' : event.type === 'DutyCompleted' ? '#67e39a' : '#52749e';
                context.fillRect(x, 10, 1, height - 20);
              }
              const playhead = timestamp / duration * width;
              context.fillStyle = '#ffffff';
              context.fillRect(playhead - 1, 0, 2, height);
            }

            document.querySelector('#play').addEventListener('click', async () => {
              displayFrame(await command('play'));
              lastAdvance = performance.now();
            });
            document.querySelector('#pause').addEventListener('click', async () => displayFrame(await command('pause')));
            scrub.addEventListener('input', () => document.querySelector('#time').textContent = formatTime(Number(scrub.value)));
            scrub.addEventListener('change', async () => {
              const generation = ++seekGeneration;
              const next = await command('seek', { timestampMilliseconds: Number(scrub.value) });
              if (generation === seekGeneration) displayFrame(next);
            });
            timeline.addEventListener('click', async event => {
              const bounds = timeline.getBoundingClientRect();
              const timestamp = Math.round((event.clientX - bounds.left) / bounds.width * metadata.durationMilliseconds);
              displayFrame(await command('seek', { timestampMilliseconds: timestamp }));
            });
            addEventListener('resize', () => frame && drawTimeline(frame.timestampMilliseconds));

            async function loop(now) {
              if (frame?.isPlaying && !advancing && now - lastAdvance >= 50) {
                advancing = true;
                const elapsed = Math.max(1, Math.round(now - lastAdvance));
                lastAdvance = now;
                try { displayFrame(await command('advance', { elapsedMilliseconds: elapsed })); }
                finally { advancing = false; }
              }
              requestAnimationFrame(loop);
            }

            metadata = await fetch('/api/metadata').then(response => response.json());
            document.querySelector('#capture').textContent = metadata.captureId;
            scrub.max = metadata.durationMilliseconds;
            displayFrame(await command('seek', { timestampMilliseconds: 0 }));
            requestAnimationFrame(loop);
          </script>
        </body>
        </html>
        """;
}
