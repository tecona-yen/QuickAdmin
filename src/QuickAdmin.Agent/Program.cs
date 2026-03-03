using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QuickAdmin.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var basePath = AppContext.BaseDirectory;
var dataPath = Path.Combine(basePath, "config", "quickadmin.json");
var logPath = Path.Combine(basePath, "logs", "agent.log");
var configStore = new ConfigStore(dataPath);
var terminalStore = new TerminalSessionStore();

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(terminalStore);
builder.Services.AddSingleton(new AgentLogger(logPath));

builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 8610));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTimeOffset.UtcNow }));

app.MapGet("/performance", () =>
{
    var cpu = Math.Round(Random.Shared.NextDouble() * 100, 2);
    var memInfo = GC.GetGCMemoryInfo();
    var usedMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2);
    var totalMb = Math.Round(memInfo.TotalAvailableMemoryBytes / 1024d / 1024d, 2);
    return Results.Ok(new { cpu, memUsedMb = usedMb, memTotalMb = totalMb, netRxKbps = 0.0, netTxKbps = 0.0 });
});

app.MapGet("/processes", () =>
{
    var items = Process.GetProcesses().OrderBy(p => p.ProcessName).Take(200).Select(p =>
    {
        DateTime? start = null;
        try { start = p.StartTime; } catch { }
        return new { name = p.ProcessName, pid = p.Id, memoryMb = Math.Round(p.WorkingSet64 / 1024d / 1024d, 2), startTime = start };
    });
    return Results.Ok(items);
});

app.MapPost("/processes/{pid:int}/kill", (int pid, AgentLogger logger) =>
{
    Process.GetProcessById(pid).Kill(true);
    logger.Write($"Killed process {pid}");
    return Results.Ok();
});

app.MapGet("/network/adapters", () =>
{
    var adapters = NetworkInterface.GetAllNetworkInterfaces().Select(n => new
    {
        id = n.Id,
        name = n.Name,
        status = n.OperationalStatus.ToString(),
        mac = n.GetPhysicalAddress().ToString(),
        ip = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? ""
    });
    return Results.Ok(adapters);
});

app.MapPost("/network/adapters/{id}/toggle", async (string id, AgentLogger logger) =>
{
    var script = $"$adapter=Get-NetAdapter -InterfaceGuid '{id}'; if ($adapter.Status -eq 'Up') {{ Disable-NetAdapter -Name $adapter.Name -Confirm:$false }} else {{ Enable-NetAdapter -Name $adapter.Name -Confirm:$false }}";
    var result = await ExecShell("pwsh", $"-NoProfile -Command \"{script}\"");
    logger.Write($"Toggled adapter {id}");
    return Results.Ok(new { result.ExitCode, result.Output, result.Error });
});

app.MapPost("/system/shutdown", async (AgentLogger logger) =>
{
    logger.Write("Shutdown requested");
    var result = await ExecShell("shutdown", "/s /t 5");
    return Results.Ok(result);
});

app.MapPost("/system/restart", async (AgentLogger logger) =>
{
    logger.Write("Restart requested");
    var result = await ExecShell("shutdown", "/r /t 5");
    return Results.Ok(result);
});

app.MapPost("/wol/send", async (WolTarget target, AgentLogger logger) =>
{
    var mac = target.Mac.Replace(":", "").Replace("-", "");
    var macBytes = Enumerable.Range(0, mac.Length / 2).Select(x => Convert.ToByte(mac.Substring(x * 2, 2), 16)).ToArray();
    var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(macBytes, 16).SelectMany(x => x)).ToArray();
    using var client = new UdpClient();
    client.EnableBroadcast = true;
    await client.SendAsync(packet, packet.Length, target.Host, target.Port);
    logger.Write($"Sent WOL to {target.Name} {target.Mac}");
    return Results.Ok();
});

app.MapGet("/iis/status", async () =>
{
    var result = await ExecShell("pwsh", "-NoProfile -Command \"Get-Service W3SVC -ErrorAction SilentlyContinue | Select-Object Name,Status | ConvertTo-Json -Compress\"");
    if (string.IsNullOrWhiteSpace(result.Output)) return Results.Ok(new { installed = false });
    return Results.Ok(new { installed = true, raw = result.Output });
});

app.MapPost("/iis/action/{verb}", async (string verb, AgentLogger logger) =>
{
    var cmd = verb.ToLowerInvariant() switch
    {
        "start" => "Start-Service W3SVC",
        "stop" => "Stop-Service W3SVC -Force",
        _ => "Restart-Service W3SVC -Force"
    };
    var result = await ExecShell("pwsh", $"-NoProfile -Command \"{cmd}\"");
    logger.Write($"IIS action {verb}");
    return Results.Ok(result);
});

app.MapPost("/terminal/start", (TerminalStartRequest req, TerminalSessionStore store) =>
{
    var id = store.Start(req.Shell);
    return Results.Ok(new { id });
});

app.MapPost("/terminal/{id}/input", (string id, TerminalInput req, TerminalSessionStore store) =>
{
    store.SendInput(id, req.Input);
    return Results.Ok();
});

app.MapGet("/terminal/{id}/output", (string id, TerminalSessionStore store) => Results.Ok(store.ReadOutput(id)));

app.MapPost("/terminal/{id}/stop", (string id, TerminalSessionStore store) =>
{
    store.Stop(id);
    return Results.Ok();
});

app.Run();

static async Task<(int ExitCode, string Output, string Error)> ExecShell(string file, string args)
{
    var p = new Process
    {
        StartInfo = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    p.Start();
    var output = await p.StandardOutput.ReadToEndAsync();
    var error = await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (p.ExitCode, output, error);
}

record TerminalStartRequest(string Shell);
record TerminalInput(string Input);

class TerminalSessionStore
{
    private readonly Dictionary<string, TerminalSession> _sessions = new();
    public string Start(string shell)
    {
        var id = Guid.NewGuid().ToString("N");
        var session = new TerminalSession(shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh" : "cmd.exe");
        _sessions[id] = session;
        return id;
    }
    public void SendInput(string id, string input)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        s.Process.StandardInput.WriteLine(input);
    }
    public List<string> ReadOutput(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return [];
        return s.Drain();
    }
    public void Stop(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        s.Dispose();
        _sessions.Remove(id);
    }
}

class TerminalSession : IDisposable
{
    private readonly List<string> _buffer = [];
    private readonly object _gate = new();
    public Process Process { get; }
    public TerminalSession(string shell)
    {
        Process = new Process
        {
            StartInfo = new ProcessStartInfo(shell)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        Process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_gate) _buffer.Add(e.Data); };
        Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_gate) _buffer.Add("ERR: " + e.Data); };
        Process.Start();
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }
    public List<string> Drain()
    {
        lock (_gate)
        {
            var copy = _buffer.ToList();
            _buffer.Clear();
            return copy;
        }
    }
    public void Dispose()
    {
        if (!Process.HasExited) Process.Kill(true);
        Process.Dispose();
    }
}

class AgentLogger
{
    private readonly string _path;
    private readonly object _gate = new();
    public AgentLogger(string path) => _path = path;
    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}");
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2_000_000)
            {
                var archive = _path.Replace(".log", $"-{DateTime.UtcNow:yyyyMMddHHmmss}.log");
                File.Move(_path, archive, true);
            }
        }
    }
}
