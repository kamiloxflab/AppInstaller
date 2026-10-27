using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    public static class Net
    {
        static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(() =>
        {
            // net48 → SecurityProtocol = SystemDefault (TLS 1.2/1.3 wg systemu)
            var h = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AppInstaller/" + Config.AppVersion + " (+https://github.com)");
            return c;
        });

        public static HttpClient Client { get { return _client.Value; } }

        public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct, int timeoutSec = 30)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (url.Contains("api.github.com")) req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        public static async Task<string> GetStringAsync(string url, CancellationToken ct, int timeoutSec = 30)
        {
            var b = await GetBytesAsync(url, ct, timeoutSec).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(b);
        }

        /// <summary>Pobiera plik z raportowaniem postępu (0–100, -1 gdy rozmiar nieznany). Zwraca końcowy adres (po przekierowaniach).</summary>
        public static async Task<Uri> DownloadAsync(string url, string target, Action<int> progress, CancellationToken ct)
        {
            var tmp = target + ".part";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int last = -2, n;
                    while (true)
                    {
                        // ochrona przed "zawieszonym" połączeniem: 60 s bez danych = błąd
                        using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            rcts.CancelAfter(TimeSpan.FromSeconds(60));
                            var readTask = src.ReadAsync(buf, 0, buf.Length, rcts.Token);
                            var finished = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, rcts.Token)).ConfigureAwait(false);
                            if (finished != readTask)
                            {
                                ct.ThrowIfCancellationRequested();
                                throw new TimeoutException("Download stalled");
                            }
                            n = await readTask.ConfigureAwait(false);
                        }
                        if (n == 0) break;
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        int p = total.HasValue && total.Value > 0 ? (int)(done * 100 / total.Value) : -1;
                        if (p != last && progress != null) { last = p; progress(p); }
                    }
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return resp.RequestMessage != null ? resp.RequestMessage.RequestUri : new Uri(url);
            }
        }

        public static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(name.Length - 120) : name;
        }
    }
}
