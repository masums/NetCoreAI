using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Hardware;

/// <summary>
/// Portable probe: OS/arch/cores from the runtime, RAM from GC memory info, NVIDIA GPUs via nvidia-smi
/// (present with any CUDA driver), other GPUs via OS-specific listings. Results are cached for 30 s.
/// </summary>
internal sealed class HardwareProbe(ILogger<HardwareProbe> logger) : IHardwareProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HardwareInfo? _cached;

    public async ValueTask<HardwareInfo> ProbeAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _cached is { } c && DateTimeOffset.UtcNow - c.ProbedAt < CacheDuration)
        {
            return c;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cached is { } c2 && DateTimeOffset.UtcNow - c2.ProbedAt < CacheDuration)
            {
                return c2;
            }

            var gpus = await ProbeGpusAsync(cancellationToken).ConfigureAwait(false);
            var gc = GC.GetGCMemoryInfo();
            var total = gc.TotalAvailableMemoryBytes;
            var available = GetAvailableRam(total);

            _cached = new HardwareInfo
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                LogicalCores = Environment.ProcessorCount,
                TotalRamBytes = total,
                AvailableRamBytes = available,
                Gpus = gpus,
                HasNpu = DetectNpu(),
            };
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static long GetAvailableRam(long total)
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        var kb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
                        return kb * 1024;
                    }
                }
            }

            if (OperatingSystem.IsWindows())
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    return (long)status.ullAvailPhys;
                }
            }
        }
        catch (Exception)
        {
            // fall through to heuristic
        }

        return (long)(total * 0.6);
    }

    private async Task<IReadOnlyList<GpuInfo>> ProbeGpusAsync(CancellationToken cancellationToken)
    {
        var gpus = new List<GpuInfo>();
        try
        {
            var nvidia = await RunAsync("nvidia-smi", "--query-gpu=index,name,memory.total,memory.used,driver_version --format=csv,noheader,nounits", cancellationToken).ConfigureAwait(false);
            if (nvidia is not null)
            {
                foreach (var line in nvidia.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    var totalMb = long.Parse(parts[2], CultureInfo.InvariantCulture);
                    var usedMb = long.Parse(parts[3], CultureInfo.InvariantCulture);
                    gpus.Add(new GpuInfo(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], GpuVendor.Nvidia, totalMb * 1_048_576, (totalMb - usedMb) * 1_048_576, [ExecutionProvider.Cuda, ExecutionProvider.Vulkan, ExecutionProvider.DirectML])
                    {
                        DriverVersion = parts.Length > 4 ? parts[4] : null,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "nvidia-smi probe failed");
        }

        if (gpus.Count == 0 && OperatingSystem.IsWindows())
        {
            try
            {
                var output = await RunAsync("powershell", "-NoProfile -NonInteractive -NoLogo -InputFormat None -OutputFormat Text -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,DriverVersion | ConvertTo-Csv -NoTypeInformation\"", cancellationToken).ConfigureAwait(false);
                if (output is not null)
                {
                    var i = 0;
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
                    {
                        var parts = line.Split("\",\"").Select(p => p.Trim('"')).ToArray();
                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        var name = parts[0];
                        var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                            : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                            : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                            : name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Qualcomm : GpuVendor.Unknown;
                        long? vram = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
                        gpus.Add(new GpuInfo(i++, name, vendor, vram, null, [ExecutionProvider.DirectML, ExecutionProvider.Vulkan]) { DriverVersion = parts.Length > 2 ? parts[2] : null });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Windows GPU probe failed");
            }
        }

        if (gpus.Count == 0 && OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            // Apple silicon: unified memory; Metal is always available.
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            gpus.Add(new GpuInfo(0, "Apple GPU (unified memory)", GpuVendor.Apple, total, null, [ExecutionProvider.Metal]));
        }

        return gpus;
    }

    private static bool DetectNpu()
    {
        // Best-effort: Windows on ARM (Qualcomm) or Intel Core Ultra expose NPUs; refined in a later WP.
        return OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.Arm64;
    }

    private static async Task<string?> RunAsync(string file, string args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardInput = true,   // never let a tool wait on the host's stdin
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // tool not installed
        }

        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Drain both streams concurrently so neither pipe can fill and block the child.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
