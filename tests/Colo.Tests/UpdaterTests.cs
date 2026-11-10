using System.Net;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class UpdaterTests
{
    const string Latest = """
        {"tag_name":"v1.28.0","name":"Colo 1.28.0","assets":[
          {"name":"ColoSetup.exe","size":71000000,"browser_download_url":"https://github.com/x/ColoSetup.exe"},
          {"name":"Colo.exe","size":12,"browser_download_url":"https://github.com/x/Colo.exe"}]}
        """;

    [Fact]
    public void Picks_the_exe_of_the_latest_release_and_compares_versions()
    {
        var r = Updater.Parse(Latest)!;
        Assert.Equal(new Version(1, 28, 0), r.Version);
        Assert.Equal("https://github.com/x/Colo.exe", r.Url);
        Assert.Equal(12, r.Size);
        Assert.True(Updater.IsNewer(r, new Version(1, 27, 1)));
        Assert.False(Updater.IsNewer(r, new Version(1, 28, 0)));
        // La versión del .exe puede venir con cuatro números.
        Assert.False(Updater.IsNewer(r, new Version(1, 28, 0, 0)));
        Assert.False(Updater.IsNewer(r, new Version(1, 29)));
        Assert.Equal(new Version(1, 27, 0), Updater.VersionOf("1.27"));
        Assert.Null(Updater.VersionOf("latest"));
        // Sin Colo.exe en el release no hay actualización.
        Assert.Null(Updater.Parse("""{"tag_name":"v2.0.0","assets":[{"name":"ColoSetup.exe","size":5,"browser_download_url":"u"}]}"""));
    }

    [Fact]
    public async Task Latest_release_comes_from_the_github_api()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, Latest));
        var r = await Updater.LatestAsync(new HttpClient(handler), default);
        Assert.Equal(new Version(1, 28, 0), r!.Version);
        var req = Assert.Single(handler.Seen).Req;
        Assert.Equal("https://api.github.com/repos/Villamediana/Own-Computer-Use/releases/latest", req.RequestUri!.ToString());
        Assert.NotEmpty(req.Headers.UserAgent);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Updater.LatestAsync(new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.Forbidden, "{}"))), default));
    }

    [Fact]
    public async Task Download_keeps_only_a_whole_exe()
    {
        var dir = Directory.CreateTempSubdirectory("colo-up").FullName;
        try
        {
            var path = Path.Combine(dir, "Colo.new.exe");
            var r = Updater.Parse(Latest)!;
            var seen = new List<double>();
            await Updater.DownloadAsync(new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.OK, "MZ0123456789"))), r, path, seen.Add, default);
            Assert.Equal("MZ0123456789", File.ReadAllText(path));
            Assert.Equal(1, seen[^1]);

            // A medias (otro tamaño) o algo que no es un programa (una página de error): no queda nada.
            File.Delete(path);
            await Assert.ThrowsAsync<IOException>(() =>
                Updater.DownloadAsync(new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.OK, "MZ01"))), r, path, null, default));
            await Assert.ThrowsAsync<IOException>(() =>
                Updater.DownloadAsync(new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.OK, "<html>hola</"))), r, path, null, default));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Swap_puts_the_new_exe_in_place_and_cleanup_removes_the_old()
    {
        var dir = Directory.CreateTempSubdirectory("colo-up").FullName;
        try
        {
            var exe = Path.Combine(dir, "Colo.exe");
            var fresh = Path.Combine(dir, "Colo.new.exe");
            File.WriteAllText(exe, "viejo");
            File.WriteAllText(fresh, "nuevo");
            Updater.Swap(exe, fresh);
            Assert.Equal("nuevo", File.ReadAllText(exe));
            Assert.Equal("viejo", File.ReadAllText(Updater.OldPath(exe)));
            Assert.False(File.Exists(fresh));
            Assert.True(Updater.Cleanup(exe));
            Assert.False(File.Exists(Updater.OldPath(exe)));

            // Si el nuevo no está, el actual vuelve a su sitio.
            Assert.ThrowsAny<IOException>(() => Updater.Swap(exe, fresh));
            Assert.Equal("nuevo", File.ReadAllText(exe));
            Assert.False(File.Exists(Updater.OldPath(exe)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
