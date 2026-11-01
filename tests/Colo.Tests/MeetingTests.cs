using System.Net;
using System.Text.Json.Nodes;
using Colo.Core;
using NAudio.Wave;
using Xunit;

namespace Colo.Tests;

public sealed class MeetingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"colo-meet-{Guid.NewGuid():N}");
    static readonly DateTime T0 = new(2026, 11, 3, 15, 0, 0); // martes

    public MeetingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void One_key_by_taps()
    {
        var k = new RecordKey();
        var t = T0;
        Assert.Equal(RecAction.Start, k.Tap(RecState.Idle, t));
        Assert.Equal(RecAction.None, k.Tap(RecState.Recording, t.AddMilliseconds(200)));   // doble al empezar: nada
        Assert.Equal(RecAction.Pause, k.Tap(RecState.Recording, t = t.AddSeconds(30)));
        Assert.Equal(RecAction.Resume, k.Tap(RecState.Paused, t = t.AddSeconds(10)));
        Assert.Equal(RecAction.Pause, k.Tap(RecState.Recording, t = t.AddSeconds(60)));
        Assert.Equal(RecAction.Stop, k.Tap(RecState.Paused, t.AddMilliseconds(300)));        // dos toques: para
        Assert.Equal(RecAction.Start, k.Tap(RecState.Idle, t.AddMilliseconds(600)));         // y otra vez empieza
        var k2 = new RecordKey();
        k2.Tap(RecState.Idle, T0);
        k2.Tap(RecState.Recording, T0.AddSeconds(5));                                        // pausa
        Assert.Equal(RecAction.Resume, k2.Tap(RecState.Paused, T0.AddSeconds(9)));
        Assert.Equal(RecAction.Stop, k2.Tap(RecState.Recording, T0.AddSeconds(9.3)));        // pausa/sigue + otro rápido = para
    }

    [Fact]
    public void Downsampler_keeps_length_and_level()
    {
        var src = Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 48000))).ToArray();
        var outp = new List<float>();
        var d = new Downsampler(48000);
        d.Feed(src.AsSpan(0, 10000), outp);
        d.Feed(src.AsSpan(10000), outp);
        Assert.InRange(outp.Count, 15999, 16000);
        Assert.InRange(outp.Max(), 0.45f, 0.5f);
        var o44 = new List<float>();
        new Downsampler(44100).Feed(new float[44100], o44);
        Assert.InRange(o44.Count, 15999, 16001);
        // PCM 16 estéreo → mono
        var pcm = new byte[8];
        BitConverter.TryWriteBytes(pcm.AsSpan(0), (short)16384); BitConverter.TryWriteBytes(pcm.AsSpan(2), (short)0);
        Assert.Equal(0.25f, Downsampler.Mono(pcm, 4, new WaveFormat(48000, 16, 2))[0], 3);
    }

    /// <summary>WAV estéreo: 0–2 s habla el micrófono, 2–4 s suena el PC.</summary>
    string StereoWav()
    {
        var path = Path.Combine(_dir, "audio.wav");
        using var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 2));
        for (var i = 0; i < 64000; i++)
        {
            var v = (short)(8000 * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
            w.WriteSample(i < 32000 ? v / 32768f : 0.002f);
            w.WriteSample(i < 32000 ? 0.001f : v / 32768f);
        }
        return path;
    }

    [Fact]
    public void Channels_tell_who_spoke_and_scribe_words_become_lines()
    {
        var env = MeetingAudio.Envelope(StereoWav());
        Assert.Equal("mic", MeetingAudio.Channel(env, 0.2, 1.8));
        Assert.Equal("pc", MeetingAudio.Channel(env, 2.1, 3.9));
        var chunks = MeetingAudio.MonoChunks(Path.Combine(_dir, "audio.wav"), 3).ToList();
        Assert.Equal([0.0, 3.0], chunks.Select(c => c.At));
        Assert.Equal(48000, chunks[0].Samples.Length);

        var scribe = JsonNode.Parse("""
{"text":"Bom dia. Oi, tudo bem?","words":[
 {"text":"Bom","start":0.1,"end":0.3,"type":"word","speaker_id":"speaker_0"},{"text":" ","type":"spacing","speaker_id":"speaker_0"},
 {"text":"dia.","start":0.3,"end":0.9,"type":"word","speaker_id":"speaker_0"},{"text":" ","type":"spacing"},
 {"text":"(risos)","start":1,"end":1.2,"type":"audio_event"},
 {"text":"Oi,","start":2.2,"end":2.5,"type":"word","speaker_id":"speaker_1"},{"text":" ","type":"spacing"},
 {"text":"tudo","start":2.5,"end":2.8,"type":"word","speaker_id":"speaker_1"},{"text":" ","type":"spacing"},
 {"text":"bem?","start":2.8,"end":3.2,"type":"word","speaker_id":"speaker_1"}]}
""")!;
        var u = MeetingText.FromScribe(scribe, env);
        Assert.Equal(2, u.Count);
        Assert.Equal(new Utterance(0.1, 0.9, "P1", "mic", "Bom dia."), u[0]);
        Assert.Equal(("P2", "pc", "Oi, tudo bem?"), (u[1].Speaker, u[1].Channel, u[1].Text));
        var md = MeetingText.Markdown(u, T0, 4);
        Assert.Contains("[0:00:02] P2 · pc: Oi, tudo bem?", md);
    }

    const string Minutes = """
Aqui está: {"title":"Migração do Tower Finder","summary":"Decidimos migrar o banco.","project":"tower finder",
"project_status":"migrando","decisions":["Postgres em produção na sexta"],
"my_tasks":[{"text":"Mandar o plano de migração","due":"2026-11-06 12:00"}],
"others_tasks":[{"text":"Criar o usuário read-only","who":"Gabriel","due":""}],
"reminders":[{"text":"Conferir backup","remind":"2026-11-05 09:00"}]}
""";

    [Fact]
    public void Minutes_go_to_the_right_project_in_the_agenda()
    {
        var agenda = new Agenda(Path.Combine(_dir, "agenda.json"));
        agenda.Project("Tower Finder", "produção", "", "TF", T0);
        var job = new MeetingJob("1", _dir, T0, 1800);
        var m = MeetingText.ParseMinutes(Minutes)!;
        var (md, tasks) = MeetingText.Apply(m, job, agenda, T0);
        Assert.Equal(3, tasks);
        Assert.Single(agenda.Projects);
        Assert.Equal("migrando", agenda.Projects[0].Status);
        Assert.All(agenda.Items, i => Assert.Equal("Tower Finder", i.Project));
        Assert.Equal("Gabriel", agenda.Items.Single(i => i.Text.StartsWith("Criar")).Who);
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), agenda.Items.Single(i => i.Text == "Conferir backup").Remind);
        Assert.StartsWith("reunión 03/11 15:00 «Migração", agenda.Items[0].Source);
        Assert.Contains("## Tarefas dos outros\n- Gabriel: Criar o usuário read-only", md.Replace("\r", ""));
        Assert.Null(MeetingText.ParseMinutes("sin json"));
        var req = MeetingText.MinutesRequest(new Settings { Model = "m" }, "[0:00:01] P1 · mic: oi", job, agenda);
        Assert.Contains("Tower Finder · migrando", req["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Whole_path_wav_to_transcript_to_minutes_and_search()
    {
        StereoWav();
        var settings = new Settings { Provider = Provider.OpenAI, ApiKey = "k", Model = "m", TranscribeModel = "whisper-1" };
        var sse = "data: " + new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["item"] = new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Minutes }) },
        }.ToJsonString() + "\n\ndata: {\"type\":\"response.completed\",\"response\":{}}\n\n";
        var http = new FakeHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/audio/transcriptions")
            ? (HttpStatusCode.OK, """{"text":"vamos migrar o banco para o postgres"}""")
            : (HttpStatusCode.OK, sse));
        var ai = new AiClient(settings, new HttpClient(http));
        var agenda = new Agenda(Path.Combine(_dir, "agenda.json"));
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        agenda.MoreSearch = meetings.Search;
        meetings.Add(_dir, T0, 4);
        var notices = new List<string>();
        var work = new MeetingWork(meetings, settings, ai, agenda);
        work.Notice += (t, _) => notices.Add(t);
        await work.RunAsync();

        var job = meetings.All.Single();
        Assert.Equal("done", job.Status);
        Assert.Equal("Migração do Tower Finder", job.Title);
        Assert.Contains("vamos migrar o banco", File.ReadAllText(Path.Combine(_dir, "transcricao.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "ata.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "audio.wav")), "sin .m4a el WAV se queda");
        Assert.StartsWith("Acta lista: «Migração do Tower Finder» · 3 cosas a la agenda", notices.Single());
        Assert.Empty(meetings.Pending());
        // Buscar después: por el acta y por lo que se dijo.
        Assert.Contains("Decisiones: Postgres em produção na sexta", agenda.Search("postgres produção", T0));
        Assert.Contains("en la transcripción: …", agenda.Search("vamos migrar", T0));
        Assert.Contains("con acta", meetings.List(T0));
    }

    [Fact]
    public async Task Failure_is_saved_and_retried_later()
    {
        StereoWav();
        var settings = new Settings { Provider = Provider.OpenAI, ApiKey = "k", Model = "m" }; // sin modelo de voz ni ElevenLabs
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        meetings.Add(_dir, T0, 4);
        var work = new MeetingWork(meetings, settings, new AiClient(settings), new Agenda(Path.Combine(_dir, "a.json")));
        string? error = null;
        work.Notice += (t, e) => { if (e) error = t; };
        await work.RunAsync();
        var job = new Meetings(Path.Combine(_dir, "meetings.json")).All.Single();
        Assert.Equal("encoded", job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("no hay con qué transcribir", error);
        Assert.Contains("falló: no hay con qué transcribir", meetings.List(T0));
        Assert.Single(meetings.Pending());
    }
}
