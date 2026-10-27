using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    public class ProcResult
    {
        public int ExitCode;
        public string Output;
    }

    public static class Proc
    {
        /// <summary>Uruchamia proces bez okna, zbiera wyjście, respektuje anulowanie i limit czasu.</summary>
        public static Task<ProcResult> RunAsync(string file, string args, CancellationToken ct, TimeSpan? timeout = null, Action<string> onLine = null, bool capture = true)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(file, args ?? "")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = capture,
                    RedirectStandardError = capture,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                };
                if (capture)
                {
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                }
                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler h = (s, e) =>
                    {
                        if (e.Data == null) return;
                        lock (sb) sb.AppendLine(e.Data);
                        if (onLine != null) onLine(e.Data);
                    };
                    if (capture) { p.OutputDataReceived += h; p.ErrorDataReceived += h; }
                    p.Start();
                    if (capture) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }

                    var limit = timeout ?? Config.InstallerTimeout;
                    var sw = Stopwatch.StartNew();
                    while (!p.WaitForExit(250))
                    {
                        if (ct.IsCancellationRequested || sw.Elapsed > limit)
                        {
                            try { p.Kill(); } catch { }
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            throw new TimeoutException("Process timeout: " + file);
                        }
                    }
                    p.WaitForExit();
                    string output;
                    lock (sb) output = sb.ToString();
                    return new ProcResult { ExitCode = p.ExitCode, Output = output };
                }
            });
        }

        /// <summary>Uruchamia skrypt PowerShell (Windows PowerShell 5.1, zawsze obecny) przez -EncodedCommand.</summary>
        public static Task<ProcResult> PowerShellAsync(string script, CancellationToken ct, TimeSpan? timeout = null)
        {
            var prolog = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                         "[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072;\n";
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(prolog + script));
            var ps = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            return RunAsync(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, ct, timeout ?? TimeSpan.FromMinutes(15));
        }

        public static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }
    }
}
