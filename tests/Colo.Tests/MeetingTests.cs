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

    /// <summary>WAV estéreo: 0–2 s habla el micrófono, 2–4 s suena el PC (seconds en total, repitiendo).</summary>
    string StereoWav(double seconds = 4, string? dir = null)
    {
        var path = Path.Combine(dir ?? _dir, "audio.wav");
        using var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 2));
        for (var i = 0; i < (int)(seconds * 16000); i++)
        {
            var v = (float)(0.25 * Math.Sin(2 * Math.PI * 200 * i / 16000.0));
            var mine = i % 64000 < 32000;
            w.WriteSample(mine ? v : 0.002f);
            w.WriteSample(mine ? 0.001f : v);
        }
        return path;
    }

    [Fact]
    public void Audio_is_read_while_recording_and_split_by_channel()
    {
        var wav = StereoWav();
        Assert.Equal(64000, MeetingAudio.Frames(wav));
        var (mic, pc) = MeetingAudio.Read(wav, 16000, 32000);
        Assert.Equal(32000, mic.Length);
        Assert.True(MeetingAudio.MicShare(mic, pc, 0, 16000) > 0.9);   // 1–2 s: micrófono
        Assert.True(MeetingAudio.MicShare(mic, pc, 16000, 32000) < 0.1); // 2–3 s: PC
        var all = MeetingAudio.Read(wav, 0, 64000);
        var turns = MeetingAudio.ChannelTurns(all.Mic, all.Pc);
        Assert.Equal([0, 1], turns.Select(t => t.Speaker));
        Assert.Equal(2.0, turns[0].End, 1);
        // El corte cae en lo callado: silencio entre 1,2 y 2 s.
        var mono = MeetingAudio.Mix(all.Mic, all.Pc);
        for (var i = 19200; i < 32000; i++) mono[i] = 0;
        Assert.InRange(MeetingAudio.QuietCut(mono, 32000, 16000) / 16000.0, 1.2, 2.0);
    }

    [Fact]
    public void Voices_are_matched_named_learned_and_merged()
    {
        var people = new People(Path.Combine(_dir, "people.json"));
        var t = new MeetingTranscript();
        float[] ana = [1, 0, 0.1f], bruno = [0, 1, 0.1f], anaAgain = [0.9f, 0.1f, 0.1f];
        var s1 = t.Assign(ana, 20, people);
        var s2 = t.Assign(bruno, 10, people);
        Assert.Equal(["S1", "S2"], t.Speakers.Select(s => s.Id));
        Assert.Same(s1, t.Assign(anaAgain, 5, people));            // la misma voz en otro trozo
        s1.Seconds = 60; s1.MicSeconds = 55; s2.Seconds = 30;
        t.Lines = [new(0, 2, "S1", "Bom dia."), new(2, 3, "S1", "Vamos lá."), new(3, 5, "S2", "Oi."), new(5, 6, "S3", "Eu também.")];
        t.Speakers.Add(new MeetingSpeaker { Id = "S3", Voice = [0.1f, 0.1f, 1], Seconds = 5 });
        Assert.Equal("[0:00] S1: Bom dia. Vamos lá.\n[0:03] S2: Oi.\n[0:05] S3: Eu também.", t.Compact());
        Assert.Contains("S1 = sin nombre · habla 1 min · 92% por el micrófono", t.Legend());

        t.Rename("S1", "Ana", people, T0);
        t.Rename("S3", "Bruno", people, T0);
        t.Rename("S2", "bruno", people, T0);                        // la misma persona: se juntan
        Assert.Equal(["S1", "S2"], t.Speakers.Select(s => s.Id));
        Assert.Equal(35, t.Speakers[1].Seconds);
        Assert.All(t.Lines.Where(l => l.Start >= 3), l => Assert.Equal("S2", l.Speaker));
        Assert.Contains("**Ana:** Bom dia. Vamos lá.", t.Markdown(T0));
        // En la reunión siguiente se reconocen solos.
        var next = new MeetingTranscript();
        var known = next.Assign([0.95f, 0.05f, 0.1f], 10, new People(Path.Combine(_dir, "people.json")));
        Assert.Equal(("Ana", true), (known.Name, known.Auto));
        Assert.Equal(["Ana", "Bruno"], people.Names);
    }

    const string Minutes = """
Aqui está: {"title":"Migração do Tower Finder","summary":"Decidimos migrar o banco.","project":"tower finder",
"project_status":"migrando","participants":["Ana","Gabriel"],"decisions":["Postgres em produção na sexta"],
"pending":[{"text":"Definir a janela de manutenção","who":"Ana"}],
"my_tasks":[{"text":"Mandar o plano de migração","due":"2026-11-06 12:00"}],
"others_tasks":[{"text":"Criar o usuário read-only","who":"Gabriel","due":""}],
"follow_ups":[{"text":"Ver se os relatórios rodam no Postgres","who":"Ana","when":"2026-11-10 10:00"}],
"reminders":[{"text":"Conferir backup","remind":"2026-11-05 09:00"}]}
""";

    [Fact]
    public void Minutes_go_to_the_right_project_in_the_agenda_and_redoing_does_not_duplicate()
    {
        var agenda = new Agenda(Path.Combine(_dir, "agenda.json"));
        agenda.Project("Tower Finder", "produção", "", "TF", T0);
        var job = new MeetingJob("1", _dir, T0, 1800);
        var m = MeetingText.ParseMinutes(Minutes)!;
        var (md, tasks, done) = MeetingText.Apply(m, job, agenda, T0);
        Assert.Equal(4, tasks);
        Assert.Single(agenda.Projects);
        Assert.Equal("migrando", agenda.Projects[0].Status);
        Assert.All(agenda.Items, i => Assert.Equal("Tower Finder", i.Project));
        Assert.Equal("Gabriel", agenda.Items.Single(i => i.Text.StartsWith("Criar")).Who);
        Assert.Equal(new DateTime(2026, 11, 10, 10, 0, 0), agenda.Items.Single(i => i.Text.StartsWith("Ver se")).Remind);
        Assert.StartsWith("reunión 03/11 15:00 «Migração", agenda.Items[0].Source);
        Assert.Contains("## Tarefas dos outros\n- Gabriel: Criar o usuário read-only", md.Replace("\r", ""));
        Assert.Contains("## Pendências\n- Ana: Definir a janela de manutenção", md.Replace("\r", ""));
        Assert.Contains("**Participantes:** Ana, Gabriel", md);
        Assert.Equal(["Ana: Definir a janela de manutenção"], done.Pending);
        Assert.Contains("Gabriel: Criar o usuário read-only", done.Tasks!);
        // Rehacer (con nombres nuevos): lo abierto de antes sale, lo hecho se queda.
        agenda.Done(agenda.Items.Single(i => i.Text == "Conferir backup").Id.ToString(), T0);
        Assert.Equal(3, agenda.RemoveOpenFrom(MeetingText.SourcePrefix(job)));
        MeetingText.Apply(m, job, agenda, T0);
        Assert.Equal(4, agenda.Items.Count);                         // lo hecho no vuelve
        Assert.Null(MeetingText.ParseMinutes("sin json"));

        var t = new MeetingTranscript { Mode = "channels", Lines = [new(1, 2, "S1", "oi")] };
        var req = MeetingText.MinutesRequest(new Settings { Model = "m" }, t, job, agenda);
        var text = req["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Tower Finder · migrando", text);
        Assert.Contains("S1 = micrófono del usuario", text);
        Assert.Contains("[0:01] S1: oi", text);
        // Reunión larga: notas de los bloques y solo el final transcrito.
        t.Notes = ["- se habló del banco"];
        t.Lines.Add(new(1300, 1302, "S1", "e no final"));
        text = MeetingText.MinutesRequest(new Settings { Model = "m" }, t, job, agenda)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Notas del bloque 1 (0–20 min):\n- se habló del banco", text.Replace("\r", ""));
        Assert.DoesNotContain("[0:01] S1: oi", text);
        Assert.Contains("e no final", text);
    }

    /// <summary>Voces de mentira: un turno por canal y una huella distinta para cada uno.</summary>
    sealed class FakeVoices : IVoices
    {
        public List<Turn> Turns(float[] mono)
        {
            var r = new List<Turn>();
            for (var s = 0.0; s + 0.5 < mono.Length / 16000.0; s += 2) r.Add(new Turn(s, Math.Min(s + 2, mono.Length / 16000.0), (int)(s / 2) % 2));
            return r;
        }
        // El canal se nota en la fase de la mezcla: el micrófono empieza en un múltiplo de 4 s.
        public float[] Embed(float[] mono) => mono.Length > 0 && Math.Abs(mono[0]) > 0.00075 ? [0, 1] : [1, 0];
    }

    static (Settings, AiClient, List<string>) Fakes(string minutes)
    {
        var settings = new Settings { Provider = Provider.OpenAI, ApiKey = "k", Model = "m", TranscribeModel = "whisper-1" };
        var sse = "data: " + new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["item"] = new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = minutes }) },
        }.ToJsonString() + "\n\ndata: {\"type\":\"response.completed\",\"response\":{}}\n\n";
        var calls = new List<string>();
        var http = new FakeHandler((r, _) =>
        {
            calls.Add(r.RequestUri!.AbsolutePath);
            return r.RequestUri!.AbsolutePath.EndsWith("/audio/transcriptions")
                ? (HttpStatusCode.OK, """{"text":"vamos migrar o banco para o postgres"}""")
                : (HttpStatusCode.OK, sse);
        });
        return (settings, new AiClient(settings, new HttpClient(http)), calls);
    }

    [Fact]
    public async Task Whole_path_voices_transcript_minutes_clips_and_search()
    {
        StereoWav();
        var (settings, ai, _) = Fakes(Minutes);
        var agenda = new Agenda(Path.Combine(_dir, "agenda.json"));
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        agenda.MoreSearch = meetings.Search;
        meetings.Add(_dir, T0, 4);
        var notices = new List<string>();
        var work = new MeetingWork(meetings, settings, ai, agenda) { Voices = _ => Task.FromResult<IVoices?>(new FakeVoices()), People = new People(Path.Combine(_dir, "people.json")) };
        work.Notice += (t, _) => notices.Add(t);
        await work.RunAsync();

        var job = meetings.All.Single();
        Assert.Equal("done", job.Status);
        Assert.Equal("Migração do Tower Finder", job.Title);
        var t = MeetingTranscript.Load(_dir);
        Assert.Equal("voices", t.Mode);
        Assert.Equal(4, t.Covered, 1);
        Assert.Equal(["S1", "S2"], t.Lines.Select(l => l.Speaker));
        Assert.True(t.Speakers[0].MicSeconds > 1.8 && t.Speakers[1].MicSeconds < 0.2);
        Assert.NotNull(work.Clip(job.Id, "S2"));
        Assert.Null(work.Clip(job.Id, "../S2"));
        Assert.Contains("vamos migrar o banco", File.ReadAllText(Path.Combine(_dir, "transcricao.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "ata.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "audio.wav")), "sin .m4a el WAV se queda");
        Assert.StartsWith("Acta lista: «Migração do Tower Finder» · 4 cosas a la agenda", notices.Single());
        Assert.Empty(meetings.Pending());
        // Consultar después: por el acta (quién quedó con qué) y por lo que se dijo.
        Assert.Contains("Pendiente: Ana: Definir a janela de manutenção", agenda.Search("Ana janela", T0));
        Assert.Contains("en la transcripción: …", agenda.Search("vamos migrar", T0));
        Assert.Contains("## Decisões", meetings.Read("tower finder"));
        Assert.Contains("con acta", meetings.List(T0));
        // Nombrar una voz y rehacer: sin duplicar en la agenda.
        work.Rename(job.Id, "S2", "Gabriel");
        Assert.Equal("ok", await work.RedoAsync(job.Id));
        Assert.Equal(4, agenda.Items.Count);
        Assert.Contains("**Gabriel:**", File.ReadAllText(Path.Combine(_dir, "transcricao.md")));
        Assert.True(meetings.Delete(job.Id));
        Assert.False(Directory.Exists(_dir));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task Transcribes_while_recording_in_chunks_and_the_rest_at_the_end()
    {
        var dir = Path.Combine(_dir, "larga");
        Directory.CreateDirectory(dir);
        StereoWav(400, dir);
        var (settings, ai, calls) = Fakes(Minutes);
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        var work = new MeetingWork(meetings, settings, ai, new Agenda(Path.Combine(_dir, "agenda.json")));  // sin voces: por canales
        var billed = new Dictionary<string, double>();
        work.Audio += (m, sec) => { lock (billed) billed[m] = billed.GetValueOrDefault(m) + sec; };
        await work.LiveAsync(dir, T0);
        var t = MeetingTranscript.Load(dir);
        Assert.Equal("channels", t.Mode);
        Assert.InRange(t.Covered, 330, 361);            // dos trozos de ~3 min; los últimos ~40 s esperan
        var before = calls.Count;
        meetings.Add(dir, T0, 400);
        await work.RunAsync();
        t = MeetingTranscript.Load(dir);
        Assert.Equal(400, t.Covered, 0);
        Assert.Equal("done", meetings.All.Single().Status);
        Assert.InRange(calls.Count - before, 2, 60);     // solo el final y el acta (los 400 s serían ~200)
        Assert.Equal(["Micrófono", "Computador"], t.Speakers.Select(s => s.Name));
        Assert.InRange(billed["whisper-1"], 390, 470);                // todo el audio enviado (con su margen), para el costo
    }

    [Fact]
    public async Task Silent_recording_gets_no_invented_minutes_and_deleted_ones_stay_deleted()
    {
        using (var w = new WaveFileWriter(Path.Combine(_dir, "audio.wav"), new WaveFormat(16000, 16, 2)))
            w.Write(new byte[16000 * 4 * 10], 0, 16000 * 4 * 10);
        var (settings, ai, calls) = Fakes(Minutes);
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        var job = meetings.Add(_dir, T0, 10);
        string? notice = null;
        var work = new MeetingWork(meetings, settings, ai, new Agenda(Path.Combine(_dir, "agenda.json")));
        work.Notice += (t, _) => notice = t;
        await work.RunAsync();
        Assert.Empty(calls);                                         // ni transcripción ni acta
        Assert.Equal(("done", "Reunión sin habla"), (meetings.All.Single().Status, meetings.All.Single().Title));
        Assert.Contains("no tiene habla", notice);
        Assert.True(meetings.Delete(job.Id));
        meetings.Update(job with { Error = "x" });                   // lo que estaba en marcha al borrar
        Assert.Empty(meetings.All);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Con los modelos de verdad (COLO_VOICES = carpeta con segmentacion.onnx, voz.onnx y four.wav).</summary>
    [Fact]
    public void Real_voices_split_the_four_speaker_sample()
    {
        var dir = Environment.GetEnvironmentVariable("COLO_VOICES");
        if (dir is null || !File.Exists(Path.Combine(dir, "four.wav"))) return;
        using var r = new WaveFileReader(Path.Combine(dir, "four.wav"));
        var bytes = new byte[r.Length];
        r.ReadExactly(bytes);
        var x = new float[bytes.Length / 2];
        for (var i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        var v = new SherpaVoices(new VoiceKit(dir));
        var turns = v.Turns(x);
        Assert.Equal(4, turns.Select(t => t.Speaker).Distinct().Count());
        // Las huellas de cada voz, en dos mitades distintas, se reconocen entre sí.
        var t = new MeetingTranscript();
        foreach (var turn in turns) t.Assign(v.Embed(x[(int)(turn.Start * 16000)..(int)(turn.End * 16000)]), turn.End - turn.Start, null);
        Assert.InRange(t.Speakers.Count, 4, 5);
    }

    /// <summary>Lo que trae una reunión corta de verdad: pocos segundos, silencio, ruido, una voz sola.</summary>
    [Fact]
    public void Real_voices_survive_short_silent_and_noisy_audio()
    {
        var dir = Environment.GetEnvironmentVariable("COLO_VOICES");
        if (dir is null || !File.Exists(Path.Combine(dir, "four.wav"))) return;
        using var r = new WaveFileReader(Path.Combine(dir, "four.wav"));
        var bytes = new byte[r.Length];
        r.ReadExactly(bytes);
        var x = new float[bytes.Length / 2];
        for (var i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        var v = new SherpaVoices(new VoiceKit(dir));
        var rnd = new Random(1);
        var noise = new float[8 * 16000];
        for (var i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() - 0.5) * 0.02f;
        foreach (var clip in new[] { new float[8 * 16000], new float[16000], noise, x[..(8 * 16000)], x[..(3 * 16000)], new float[200 * 16000] })
        {
            var turns = v.Turns(clip);
            foreach (var g in turns.GroupBy(t => t.Speaker))
            {
                var a = Math.Min((int)(g.First().Start * 16000), clip.Length);
                v.Embed(clip[a..Math.Clamp((int)(g.First().End * 16000), a, clip.Length)]);
            }
        }
        Assert.Empty(v.Embed(new float[100]));
    }

    [Fact]
    public void Killed_recording_is_recovered_with_its_real_length()
    {
        var root = Path.Combine(_dir, "Reuniões");
        var dir = Path.Combine(root, "2026-11-03 1500");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "audio.wav");
        using (var w = new WaveFileWriter(wav, new WaveFormat(16000, 16, 2)))
        {
            w.Write(new byte[64000 * 2], 0, 64000 * 2);
            w.Flush();
            w.Write(new byte[64000 * 3], 0, 64000 * 3); // 3 s más que la cabecera no sabe (sin Flush)
            // Como si el proceso muriera aquí: se copia el archivo tal cual está antes de cerrar.
            w.Flush();
        }
        // Cabecera vieja (2 s) sobre 5 s de datos.
        var bytes = File.ReadAllBytes(wav);
        var at = System.Text.Encoding.ASCII.GetString(bytes, 0, 100).IndexOf("data") + 4;
        using (var f = new FileStream(wav, FileMode.Open))
        {
            f.Position = at; f.Write(BitConverter.GetBytes(64000 * 2));
        }
        Directory.CreateDirectory(Path.Combine(root, "ya-con-acta"));
        File.WriteAllText(Path.Combine(root, "ya-con-acta", "audio.wav"), "x");
        File.WriteAllText(Path.Combine(root, "ya-con-acta", "ata.md"), "x");
        var meetings = new Meetings(Path.Combine(_dir, "meetings.json"));
        Assert.Empty(meetings.Recover(root, dir)); // la que se está grabando ahora no se toca
        Assert.Equal([dir], meetings.Recover(root));
        Assert.Equal(5, meetings.All.Single().Seconds, 1);
        using (var r = new WaveFileReader(wav)) Assert.Equal(5, r.TotalTime.TotalSeconds, 1);
        Assert.Empty(meetings.Recover(root)); // no se añade dos veces
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
        Assert.Equal("recorded", job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("no hay con qué transcribir", error);
        Assert.Contains("falló: no hay con qué transcribir", meetings.List(T0));
        Assert.Single(meetings.Pending());
    }
}
