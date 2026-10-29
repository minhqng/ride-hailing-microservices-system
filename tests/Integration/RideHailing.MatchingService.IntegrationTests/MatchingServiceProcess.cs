// B-REC-01 production proof host: launches the REAL Matching service process
// (actual Program registration, broker consumers, hosted worker, Kestrel) on
// a private loopback port against the shared test PostgreSQL/Redis/RabbitMQ.
// A process kill is therefore a real process kill, not an in-memory shortcut.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class MatchingServiceProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly StringBuilder _log = new();

    private MatchingServiceProcess(Process process, string baseUrl)
    {
        _process = process;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public bool HasExited => _process.HasExited;

    public string Logs
    {
        get
        {
            lock (_gate)
            {
                return _log.ToString();
            }
        }
    }

    public static async Task<MatchingServiceProcess> StartAsync(
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var serviceDll = ResolveServiceDll();
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet) || !File.Exists(dotnet))
        {
            dotnet = "dotnet";
        }

        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(serviceDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(serviceDll);
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        foreach (var (key, value) in environment)
        {
            if (value is not null)
            {
                psi.Environment[key] = value;
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Matching service process.");
        var host = new MatchingServiceProcess(process, baseUrl);
        process.OutputDataReceived += (_, e) => host.Append(e.Data);
        process.ErrorDataReceived += (_, e) => host.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await host.WaitHealthyAsync(TimeSpan.FromSeconds(45), ct);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Matching service exited with code {_process.ExitCode} during startup.\n{Logs}");
            }

            try
            {
                using var response = await http.GetAsync(BaseUrl + "/health/live", ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Not listening yet.
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"Matching service did not become healthy.\n{Logs}");
    }

    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // Test cleanup must not mask the scenario assertion.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            _log.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string ResolveServiceDll()
    {
        var root = RepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var candidates = new[]
        {
            Path.Combine(root, "src", "Services", "Matching", "RideHailing.MatchingService",
                "bin", configuration, "net10.0", "RideHailing.MatchingService.dll"),
            Path.Combine(root, "src", "Services", "Matching", "RideHailing.MatchingService",
                "bin", "Release", "net10.0", "RideHailing.MatchingService.dll"),
            Path.Combine(root, "src", "Services", "Matching", "RideHailing.MatchingService",
                "bin", "Debug", "net10.0", "RideHailing.MatchingService.dll"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Built Matching service DLL not found. Run the Release solution build first.",
            candidates[0]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "RideHailing.Distributed.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
