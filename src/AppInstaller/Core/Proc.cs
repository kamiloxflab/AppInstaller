using System;
using System.Diagnostics;
using System.Linq;
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

        /// <summary>
        /// Uruchamia program jako zalogowany użytkownik BEZ uprawnień administratora (jednorazowe zadanie Harmonogramu,
        /// poziom "Limited") i czeka na wynik. Potrzebne dla instalatorów, które odmawiają pracy "jako administrator" (Spotify).
        /// </summary>
        public static async Task<int> RunAsUserAsync(string file, string args, CancellationToken ct, TimeSpan? timeout = null)
        {
            Func<string, string> q = s => "'" + (s ?? "").Replace("'", "''") + "'";
            var minutes = (int)(timeout ?? Config.InstallerTimeout).TotalMinutes;
            var script = @"
$name = 'AppInstaller-' + [guid]::NewGuid().ToString('N')
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($user -eq 'NT AUTHORITY\SYSTEM') { Write-Output 'EXIT=SYSTEM'; exit 0 }
$a = New-ScheduledTaskAction -Execute " + q(file) + @" -Argument " + q(string.IsNullOrEmpty(args) ? " " : args) + @"
$p = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes " + minutes + @")
Register-ScheduledTask -TaskName $name -Action $a -Principal $p -Settings $s -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $name
    $deadline = (Get-Date).AddMinutes(" + minutes + @")
    do {
        Start-Sleep -Seconds 2
        $i = Get-ScheduledTaskInfo -TaskName $name
    } while (($i.LastTaskResult -eq 267009 -or $i.LastTaskResult -eq 267011) -and (Get-Date) -lt $deadline)
    Write-Output ('EXIT=' + $i.LastTaskResult)
} finally {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}";
            var r = await PowerShellAsync(script, ct, TimeSpan.FromMinutes(minutes + 2)).ConfigureAwait(false);
            var line = (r.Output ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("EXIT="));
            if (line == null) throw new Exception("Run-as-user failed: " + (r.Output ?? "").Trim());
            var val = line.Substring(5);
            if (val == "SYSTEM") throw new Exception("Cannot install for a user when running as SYSTEM");
            long code;
            if (!long.TryParse(val, out code)) throw new Exception("Run-as-user: bad result " + val);
            return unchecked((int)(uint)code);   // LastTaskResult to UInt32 — zachowaj kody ujemne (winget 0x8A15...)
        }

        public static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }
    }
}
