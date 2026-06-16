using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Benchmarks.Providers;

public sealed class ExternalToolBenchmarkProvider : IBenchmarkProvider
{
    private static string BenchDir => Path.Combine(AppContext.BaseDirectory, "bench");

    private readonly Dictionary<string, string> _collectedTools = new();
    public IReadOnlyDictionary<string, string> CollectedTools => _collectedTools;

    private static string ToolPath(string tool, string exe)
        => Path.Combine(BenchDir, tool, exe);

    private static BenchmarkSubScore MissingTool(string key, string label, string toolName)
        => new() { Key = key, Label = label, Score = 0, Detail = $"tool not bundled: {toolName}" };

    private static BenchmarkSubScore ParseFailure(string key, string label, string detail)
        => new() { Key = key, Label = label, Score = 0, Detail = detail };

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string exe, string args, IProgress<BenchmarkPhaseProgress> progress,
        string phase, string phaseDetail, double phaseStartPercent, double phaseEndPercent,
        double wallSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        proc.Start();
        try
        {
            proc.PriorityClass = ProcessPriorityClass.High;
        }
        catch { }

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdoutSb.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderrSb.AppendLine(e.Data); } };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        double hardCap = Math.Max(60, wallSeconds * 3);
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(hardCap));
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(ct);
        });
        using var hardReg = hardTimeout.Token.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            stderrSb.AppendLine($"[nexus] hard timeout after {hardCap}s");
            tcs.TrySetResult(-1);
        });

        while (!tcs.Task.IsCompleted)
        {
            double elapsed = sw.Elapsed.TotalSeconds;
            double pct = wallSeconds > 0
                ? phaseStartPercent + (phaseEndPercent - phaseStartPercent) * Math.Min(1, elapsed / wallSeconds)
                : phaseStartPercent;
            progress.Report(new BenchmarkPhaseProgress
            {
                Phase = phase,
                Detail = phaseDetail,
                Percent = pct,
            });
            await Task.WhenAny(tcs.Task, Task.Delay(500, ct));
        }

        ct.ThrowIfCancellationRequested();
        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private static string RunVersionProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "";
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);
            return stdout + stderr;
        }
        catch
        {
            return "";
        }
    }

    public async Task<BenchmarkSubScore> RunCpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        const string exe = "primesieve.exe";
        const string tool = "primesieve";
        var exePath = ToolPath(tool, exe);

        if (!File.Exists(exePath))
        {
            return MissingTool("cpu", "CPU", tool);
        }

        try
        {
            var verOut = RunVersionProcess(exePath, "--version");
            var verMatch = Regex.Match(verOut, @"primesieve\s+([\d.]+)", RegexOptions.IgnoreCase);
            _collectedTools["cpu"] = verMatch.Success ? $"primesieve {verMatch.Groups[1].Value}" : "primesieve";

            progress.Report(new BenchmarkPhaseProgress { Phase = "cpu", Detail = "single-core", Percent = 0 });
            var (singleExit, singleOut, singleErr) = await RunProcessAsync(
                exePath, "1e10 -t 1 --time", progress, "cpu", "single-core", 0, 0.4, 30, ct);

            double singlePrimesPerSec = 0;
            if (singleExit == 0)
            {
                singlePrimesPerSec = ParsePrimesPerSec(singleOut + singleErr);
            }

            progress.Report(new BenchmarkPhaseProgress { Phase = "cpu", Detail = "all-core", Percent = 0.4 });
            var (multiExit, multiOut, multiErr) = await RunProcessAsync(
                exePath, "1e10 --time", progress, "cpu", "all-core", 0.4, 1.0, 30, ct);

            double allCorePrimesPerSec = 0;
            if (multiExit == 0)
            {
                allCorePrimesPerSec = ParsePrimesPerSec(multiOut + multiErr);
            }

            if (allCorePrimesPerSec <= 0 && singlePrimesPerSec <= 0)
            {
                return ParseFailure("cpu", "CPU",
                    $"primesieve parse failed. stdout={singleOut.Trim()} stderr={singleErr.Trim()}");
            }

            double raw = allCorePrimesPerSec > 0 ? allCorePrimesPerSec : singlePrimesPerSec;
            double score = Scoring.Score(raw, Scoring.BaselineCpuPrimesPerSec);

            return new BenchmarkSubScore
            {
                Key = "cpu",
                Label = "CPU",
                Score = score,
                RawValue = Math.Round(raw / 1_000_000_000d, 3),
                RawUnit = "primes/s",
                Detail = singlePrimesPerSec > 0
                    ? $"single {Math.Round(singlePrimesPerSec / 1_000_000_000d, 3)} Gprimes/s | all-core {Math.Round(raw / 1_000_000_000d, 3)} Gprimes/s"
                    : $"all-core {Math.Round(raw / 1_000_000_000d, 3)} Gprimes/s",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ParseFailure("cpu", "CPU", $"error: {ex.Message}");
        }
    }

    internal static double ParsePrimesPerSec(string output)
    {
        var mCount = Regex.Match(output, @"Primes:\s*([\d,]+)", RegexOptions.IgnoreCase);
        var mSecs = Regex.Match(output, @"Seconds:\s*([\d.]+)", RegexOptions.IgnoreCase);
        if (!mCount.Success || !mSecs.Success)
        {
            return 0;
        }

        if (!double.TryParse(mCount.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var primes))
        {
            return 0;
        }

        if (!double.TryParse(mSecs.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            return 0;
        }

        return primes / seconds;
    }

    public async Task<BenchmarkSubScore> RunGpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        const string clpeakTool = "clpeak";
        const string clpeakExe = "clpeak.exe";
        const string vkpeakTool = "vkpeak";
        const string vkpeakExe = "vkpeak.exe";

        var clpeakPath = ToolPath(clpeakTool, clpeakExe);
        var vkpeakPath = ToolPath(vkpeakTool, vkpeakExe);

        bool hasClpeak = File.Exists(clpeakPath);
        bool hasVkpeak = File.Exists(vkpeakPath);

        if (!hasClpeak && !hasVkpeak)
        {
            return MissingTool("gpu", "GPU (compute)", $"{clpeakTool}/{vkpeakTool}");
        }

        double gflops = 0;
        double memGbPerSec = 0;
        string? versionStr = null;
        string detail = "";

        try
        {
            if (hasClpeak)
            {
                var tmpJson = Path.Combine(Path.GetTempPath(), $"nexus-clpeak-{Guid.NewGuid():N}.json");
                try
                {
                    progress.Report(new BenchmarkPhaseProgress { Phase = "gpu", Detail = "clpeak (OpenCL)", Percent = 0 });
                    var (exit, stdout, stderr) = await RunProcessAsync(
                        clpeakPath, $"--json-file \"{tmpJson}\"", progress, "gpu", "clpeak (OpenCL)", 0, 0.8, 60, ct);

                    if (exit == 0 && File.Exists(tmpJson))
                    {
                        var json = await File.ReadAllTextAsync(tmpJson, ct);
                        (gflops, memGbPerSec, versionStr) = ParseClpeakJson(json);
                    }

                    if (gflops <= 0)
                    {
                        detail = $"clpeak exit={exit} parse-failed";
                    }
                }
                finally
                {
                    try { File.Delete(tmpJson); } catch { }
                }
            }

            if (hasVkpeak && gflops <= 0)
            {
                progress.Report(new BenchmarkPhaseProgress { Phase = "gpu", Detail = "vkpeak (Vulkan)", Percent = 0.8 });
                var (vkExit, vkOut, vkErr) = await RunProcessAsync(
                    vkpeakPath, "0", progress, "gpu", "vkpeak (Vulkan)", 0.8, 1.0, 30, ct);

                if (vkExit == 0)
                {
                    gflops = ParseVkpeakGflops(vkOut + vkErr);
                    if (gflops > 0 && versionStr is null)
                    {
                        versionStr = ParseVkpeakVersion(vkOut + vkErr);
                    }
                    if (gflops > 0)
                    {
                        detail = "Vulkan (vkpeak)";
                    }
                }
            }

            if (gflops <= 0)
            {
                return ParseFailure("gpu", "GPU (compute)",
                    string.IsNullOrEmpty(detail) ? "gpu tools returned no parseable result" : detail);
            }

            if (versionStr is not null)
            {
                _collectedTools["gpu"] = versionStr;
            }

            double score = Scoring.Score(gflops, Scoring.BaselineGpuGflops);
            string detailStr = memGbPerSec > 0
                ? $"{Math.Round(gflops, 1)} GFLOPS sp | {Math.Round(memGbPerSec, 1)} GB/s mem"
                : $"{Math.Round(gflops, 1)} GFLOPS sp";

            return new BenchmarkSubScore
            {
                Key = "gpu",
                Label = "GPU (compute)",
                Score = score,
                RawValue = Math.Round(gflops, 1),
                RawUnit = "GFLOPS",
                Detail = detailStr,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ParseFailure("gpu", "GPU (compute)", $"error: {ex.Message}");
        }
    }

    internal static (double gflops, double memGbPerSec, string? version) ParseClpeakJson(string json)
    {
        try
        {
            var result = System.Text.Json.JsonSerializer.Deserialize(json,
                Nexus.Service.Serialization.AppJsonContext.Default.ClpeakResult);
            if (result?.Entries is null)
            {
                return (0, 0, null);
            }

            double bestGflops = 0;
            double bestMem = 0;
            foreach (var entry in result.Entries)
            {
                // clpeak enumerates a "CPU" pseudo-device alongside GPUs; exclude
                // it so a GPU-less machine reports 0 GPU, not the CPU's compute.
                if (string.Equals(entry.Backend, "CPU", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // Unsupported entries carry a status + reason and no value.
                if (entry.Status is not null)
                {
                    continue;
                }
                // The benchmark name is in `test`; `category` is only the coarse
                // group ("fp_compute" / "bandwidth").
                string test = entry.Test ?? "";
                string unit = entry.Unit ?? "";
                if (string.Equals(test, "single_precision_compute", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(unit, "gflops", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Value > bestGflops)
                    {
                        bestGflops = entry.Value;
                    }
                }
                else if (string.Equals(test, "global_memory_bandwidth", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(unit, "gbps", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Value > bestMem)
                    {
                        bestMem = entry.Value;
                    }
                }
            }
            return (bestGflops, bestMem, result.ClpeakVersion);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    private static double ParseVkpeakGflops(string output)
    {
        double best = 0;
        foreach (Match m in Regex.Matches(output, @"fp32[\w-]*\s*=\s*([\d.]+)\s*GFLOPS", RegexOptions.IgnoreCase))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) && v > best)
            {
                best = v;
            }
        }
        return best;
    }

    private static string? ParseVkpeakVersion(string output)
    {
        var m = Regex.Match(output, @"vkpeak\s+([\d\.]+)", RegexOptions.IgnoreCase);
        return m.Success ? $"vkpeak {m.Groups[1].Value}" : "vkpeak";
    }

    public async Task<BenchmarkSubScore> RunRamAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        const string tool = "stream";
        const string exe = "stream.exe";
        var exePath = ToolPath(tool, exe);

        if (!File.Exists(exePath))
        {
            return MissingTool("ram", "RAM", tool);
        }

        try
        {
            _collectedTools["ram"] = "STREAM";

            progress.Report(new BenchmarkPhaseProgress { Phase = "ram", Detail = "STREAM Triad", Percent = 0 });
            var (exit, stdout, stderr) = await RunProcessAsync(
                exePath, "", progress, "ram", "STREAM Triad", 0, 1.0, 15, ct);

            double gbPerSec = ParseStreamTriad(stdout + stderr);

            if (gbPerSec <= 0)
            {
                var truncated = stdout.Trim();
                if (truncated.Length > 200)
                {
                    truncated = truncated.Substring(0, 200);
                }

                return ParseFailure("ram", "RAM",
                    $"STREAM parse failed (exit={exit}). stdout={truncated}");
            }

            double score = Scoring.Score(gbPerSec, Scoring.BaselineRamGbPerSec);
            return new BenchmarkSubScore
            {
                Key = "ram",
                Label = "RAM",
                Score = score,
                RawValue = Math.Round(gbPerSec, 2),
                RawUnit = "GB/s",
                Detail = $"STREAM Triad {Math.Round(gbPerSec, 2)} GB/s",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ParseFailure("ram", "RAM", $"error: {ex.Message}");
        }
    }

    // STREAM prints "Triad:     NNNNN.N     ..." (MB/s in first data column).
    internal static double ParseStreamTriad(string output)
    {
        var m = Regex.Match(output, @"^Triad\s*:\s*([\d\.]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mbPerSec) &&
            mbPerSec > 0)
        {
            return mbPerSec / 1000d;
        }
        return 0;
    }

    public async Task<BenchmarkSubScore> RunStorageAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        const string tool = "diskspd";
        const string exe = "diskspd.exe";
        var exePath = ToolPath(tool, exe);

        if (!File.Exists(exePath))
        {
            return MissingTool("storage", "Storage", tool);
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"nexus-diskspd-{Guid.NewGuid():N}.bin");

        // Guards a future caller-supplied path; the temp path never starts with these prefixes.
        if (tempFile.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            tempFile.StartsWith("#", StringComparison.Ordinal))
        {
            return ParseFailure("storage", "Storage", $"unsafe target path rejected: {tempFile}");
        }

        try
        {
            progress.Report(new BenchmarkPhaseProgress { Phase = "storage", Detail = "DiskSpd seq+rand", Percent = 0 });

            var args = $"-b1M -d15 -o1 -t1 -Sh -Zr -L -Rxml -c512M -w25 \"{tempFile}\"";
            var (exit, stdout, stderr) = await RunProcessAsync(
                exePath, args, progress, "storage", "DiskSpd seq+rand", 0, 0.7, 20, ct);

            var args4k = $"-b4K -d10 -o32 -t4 -Sh -Zr -L -r -Rxml \"{tempFile}\"";
            var (exit4k, stdout4k, stderr4k) = await RunProcessAsync(
                exePath, args4k, progress, "storage", "DiskSpd 4K rand", 0.7, 1.0, 15, ct);

            double seqMbPerSec = 0;
            double randIops = 0;
            double latencyMs = 0;

            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                (seqMbPerSec, _, _) = ParseDiskSpdXml(stdout);
                var verEl = TryParseDiskSpdVersion(stdout);
                _collectedTools["storage"] = verEl is not null ? $"diskspd {verEl}" : "diskspd";
            }
            else
            {
                _collectedTools["storage"] = "diskspd";
            }

            if (exit4k == 0 && !string.IsNullOrWhiteSpace(stdout4k))
            {
                (_, randIops, latencyMs) = ParseDiskSpdXml(stdout4k);
            }

            if (seqMbPerSec <= 0 && randIops <= 0)
            {
                return ParseFailure("storage", "Storage",
                    $"DiskSpd parse failed (seq exit={exit}, 4k exit={exit4k})");
            }

            double score = Scoring.Score(seqMbPerSec > 0 ? seqMbPerSec : 1000, Scoring.BaselineStorageMbPerSec);
            string detailStr = $"seq {Math.Round(seqMbPerSec)} MB/s";
            if (randIops > 0)
            {
                detailStr += $" | 4K {Math.Round(randIops)} IOPS";
            }

            if (latencyMs > 0)
            {
                detailStr += $" | lat {Math.Round(latencyMs, 2)} ms";
            }

            return new BenchmarkSubScore
            {
                Key = "storage",
                Label = "Storage",
                Score = score,
                RawValue = Math.Round(seqMbPerSec, 1),
                RawUnit = "MB/s",
                Detail = detailStr,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ParseFailure("storage", "Storage", $"error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static string? TryParseDiskSpdVersion(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants("Version").FirstOrDefault()?.Value;
        }
        catch
        {
            return null;
        }
    }

    internal static (double seqMbPerSec, double iops, double latencyMs) ParseDiskSpdXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            double totalBytes = 0;
            double totalIops = 0;
            double totalLatMs = 0;
            double durationSec = 0;

            // DiskSpd emits two <TimeSpan> nodes: the Profile/config one (carries
            // <Duration>, no <TestTimeSeconds>) and the results one. Read
            // TestTimeSeconds directly so we get the results node, not the config.
            var durEl = doc.Descendants("TestTimeSeconds").FirstOrDefault();
            if (durEl is not null &&
                double.TryParse(durEl.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                durationSec = d;
            }

            foreach (var thread in doc.Descendants("Thread"))
            {
                foreach (var target in thread.Descendants("Target"))
                {
                    var bytesEl = target.Element("BytesCount");
                    if (bytesEl is not null &&
                        double.TryParse(bytesEl.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var b))
                    {
                        totalBytes += b;
                    }
                    var iopsEl = target.Element("IOCount");
                    if (iopsEl is not null &&
                        double.TryParse(iopsEl.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var i))
                    {
                        totalIops += i;
                    }
                }
            }

            var latEl = doc.Descendants("AverageLatencyMilliseconds").FirstOrDefault();
            if (latEl is not null &&
                double.TryParse(latEl.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat))
            {
                totalLatMs = lat;
            }

            double seqMbPerSec = durationSec > 0 ? totalBytes / durationSec / (1024d * 1024) : 0;
            double iopsPerSec = durationSec > 0 ? totalIops / durationSec : 0;

            return (seqMbPerSec, iopsPerSec, totalLatMs);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
