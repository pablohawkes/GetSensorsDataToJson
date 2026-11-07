using LibreHardwareMonitor.Hardware;
using log4net.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
//using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;


[assembly: log4net.Config.XmlConfigurator(ConfigFile = "log4net.config")]


namespace GetSensorsDataToJson;

internal static class Program
{
    private static readonly log4net.ILog log = log4net.LogManager.GetLogger("LogFileLogger");
    //private static readonly log4net.ILog logSensorsData = log4net.LogManager.GetLogger("SensorsDataLogger");

    internal const string PipeName = "GetSensorsDataToJson";
    internal const string MutexName = @"Global\GetSensorsDataToJson.Server";
    internal const string ServiceName = "GetSensorsDataToJson";
    private static AppConfig Config { get; set; } = new AppConfig();

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Esta aplicación sólo puede ejecutarse en Windows.");
            return 2;
        }

        if (args.Any(a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(GetPackageVersion());
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase)))
            return await RunServerAsync().ConfigureAwait(false);

        string command = args.Any(a => string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase))
            ? "STATUS"
            : args.Any(a => string.Equals(a, "--stop", StringComparison.OrdinalIgnoreCase))
                ? "STOP"
                : "GET";

        return await RunClientAsync(
            command,
            startServerIfMissing: command != "STOP")
            .ConfigureAwait(false);
    }

    private static string GetPackageVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        string? version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString();
        }

        return version ?? "unknown";
    }

    private static async Task<int> RunClientAsync(string command, bool startServerIfMissing)
    {
        int totalTimeoutMs = Config.SensorsData_TotalTimeout_Seconds * 1_000;// Settings.GetInt("GETTEMP_CLIENT_TIMEOUT_MS", 30_000, 1_000, 120_000);
        using var timeout = new CancellationTokenSource(totalTimeoutMs);

        try
        {
            // Fast path: the resident process is already running.
            string? response = await PipeClient.TryRequestAsync(command, 350, timeout.Token).ConfigureAwait(false);
            if (response is not null)
            {
                Console.WriteLine(response);
                return 0;
            }

            // --stop must never create a server merely to stop it.
            if (!startServerIfMissing)
            {
                Console.WriteLine("{\"stopping\":false,\"reason\":\"not_running\"}");
                return 1;
            }

            StartDetachedServer();

            // Cold-start path: wait only for the bounded client timeout.
            while (!timeout.IsCancellationRequested)
            {
                response = await PipeClient.TryRequestAsync(command, 500, timeout.Token).ConfigureAwait(false);
                if (response is not null)
                {
                    Console.WriteLine(response);
                    return 0;
                }

                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The fallback below keeps the output compatible with the original Zabbix item.
        }
        catch (Exception ex)
        {
            log.Error("Client failure", ex);
        }

        Console.WriteLine(command switch
        {
            "GET" => "[]",
            "STOP" => "{\"stopping\":false,\"reason\":\"not_running\"}",
            _ => "{\"available\":false}"
        });
        return 1;
    }

    private static void StartDetachedServer()
    {
        try
        {
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("No se pudo determinar la ruta del proceso.");

            string applicationDirectory = AppContext.BaseDirectory;

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            // Framework-dependent execution uses dotnet.exe as ProcessPath.
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(applicationDirectory);
            //startInfo.ArgumentList.Add(entryAssembly);

            startInfo.ArgumentList.Add("--server");

            using Process? child = Process.Start(startInfo);
            if (child is null)
                throw new InvalidOperationException("No se pudo iniciar el proceso residente.");

            // Do not leave the child attached to Zabbix's stdout/stderr handles.
            child.StandardInput.Close();
        }
        catch (Exception ex)
        {
            log.Error("Could not start resident process", ex);
        }
    }

    private static async Task<int> RunServerAsync()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
                return 0;
        }
        catch (UnauthorizedAccessException)
        {
            // Fallback for a restricted interactive account. The Windows service normally
            // runs as LocalSystem and can create the global mutex.
            mutex = new Mutex(initiallyOwned: true, "GetSensorsDataToJson.v23.Server", out bool createdNew);
            if (!createdNew)
                return 0;
        }

        using (mutex)
        {
            try
            {
                HostApplicationBuilder builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
                builder.Services.AddSingleton<SnapshotCache>();
                builder.Services.AddHostedService<HardwareMonitorWorker>();
                builder.Services.AddHostedService<PipeServerWorker>();

                using IHost host = builder.Build();
                try
                {
                    log.Info($"Resident process starting as {WindowsIdentity.GetCurrent().Name}.");
                }
                catch
                {
                    log.Info($"Resident process starting...");
                }

                await host.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("Resident process stopped unexpectedly", ex);
                return 1;
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
                log.Info("Resident process stopped.");
            }
        }
    }
}

internal static class PipeClient
{
    public static async Task<string?> TryRequestAsync(string command, int connectTimeoutMs, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: Program.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous,
            impersonationLevel: System.Security.Principal.TokenImpersonationLevel.Identification);

        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Byte;

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

internal sealed class SnapshotCache
{
    private readonly object _statusLock = new();
    private readonly TaskCompletionSource _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _json = "[]";
    private DateTimeOffset? _lastSuccessUtc;
    private string? _lastError;

    private static AppConfig Config { get; set; } = new AppConfig();

    public string Json => Volatile.Read(ref _json);

    public void Publish(string json)
    {
        Volatile.Write(ref _json, json);
        lock (_statusLock)
        {
            _lastSuccessUtc = DateTimeOffset.UtcNow;
            _lastError = null;
        }
        _firstSnapshot.TrySetResult();
    }

    public void RecordError(Exception ex)
    {
        lock (_statusLock)
            _lastError = ex.GetBaseException().Message;
    }

    public async Task WaitForFirstSnapshotAsync(TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        try
        {
            await _firstSnapshot.Task.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Return the initial [] cache rather than exceeding Zabbix's timeout.
        }
    }

    public string GetStatusJson()
    {
        DateTimeOffset? lastSuccess;
        string? lastError;
        lock (_statusLock)
        {
            lastSuccess = _lastSuccessUtc;
            lastError = _lastError;
        }

        var status = new
        {
            available = lastSuccess.HasValue,
            lastSuccessUtc = lastSuccess,
            ageSeconds = lastSuccess.HasValue
                ? Math.Max(0, (long)(DateTimeOffset.UtcNow - lastSuccess.Value).TotalSeconds)
                : (long?)null,
            lastError,
            refreshSeconds = Config.SensorsData_Refresh_Seconds //Settings.GetInt("GETTEMP_REFRESH_SECONDS", 60, 10, 3600) 
        };

        return JsonSerializer.Serialize(status, JsonDefaults.Options);
    }
}

internal sealed class PipeServerWorker(SnapshotCache cache, IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly log4net.ILog log = log4net.LogManager.GetLogger("LogFileLogger");
    //private static readonly log4net.ILog logSensorsData = log4net.LogManager.GetLogger("SensorsDataLogger");

    private static AppConfig Config { get; set; } = new AppConfig();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.Info("Named-pipe server ready.");

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    Program.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, stoppingToken);
                pipe = null; // Ownership transferred to HandleClientAsync.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                log.Error("Pipe accept failure", ex);
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                string? command = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
                string response;
                bool stopAfterResponse = false;

                switch (command?.Trim().ToUpperInvariant())
                {
                    case "GET":
                        int initialWaitMs = Config.SensorsData_TotalTimeout_Seconds * 1_000;
                        await cache.WaitForFirstSnapshotAsync(TimeSpan.FromMilliseconds(initialWaitMs), stoppingToken)
                            .ConfigureAwait(false);
                        response = cache.Json;
                        break;

                    case "STATUS":
                        response = cache.GetStatusJson();
                        break;

                    case "STOP":
                        response = "{\"stopping\":true}";
                        stopAfterResponse = true;
                        break;

                    default:
                        response = "{\"error\":\"unknown command\"}";
                        break;
                }

                await writer.WriteLineAsync(response.AsMemory(), stoppingToken).ConfigureAwait(false);
                await writer.FlushAsync(stoppingToken).ConfigureAwait(false);

                if (stopAfterResponse)
                {
                    log.Info("Stop requested through the named pipe.");
                    lifetime.StopApplication();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // The caller may have reached its own timeout and disconnected.
            }
            catch (Exception ex)
            {
                log.Error("Pipe client failure", ex);
            }
        }
    }
}

internal sealed class HardwareMonitorWorker(SnapshotCache cache) : BackgroundService
{
    private Computer? _computer;
    private static readonly log4net.ILog log = log4net.LogManager.GetLogger("LogFileLogger");
    private static readonly log4net.ILog logSensorsData = log4net.LogManager.GetLogger("SensorsDataLogger");
    private AppConfig Config { get; set; } = new AppConfig();

    private readonly Stopwatch _minMaxResetTimer = Stopwatch.StartNew();


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int refreshSeconds = Config.SensorsData_Refresh_Seconds;
        log.Info($"Hardware refresh interval: {refreshSeconds} seconds.");

        // Do not block host/pipe startup while the first hardware probe is running.
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                try
                {
                    EnsureComputerOpen();
                    _computer!.Accept(new UpdateVisitor());

                    var values = new List<HardwareSensorValue>();

                    foreach (IHardware hardware in _computer.Hardware)
                        CollectDataSensors(hardware, values);

                    Console.WriteLine($"Collected {values.Count} temperature sensors.");

                    string json = JsonSerializer.Serialize(values, JsonDefaults.Options);

                    if (Config.Log_SensorData_ToFile)
                        logSensorsData.Info(json);

                    cache.Publish(json);

                    ResetMinMaxIfRequired();
                }
                catch (Exception ex)
                {
                    cache.RecordError(ex);

                    log.Error("Hardware refresh failed; keeping the last successful cache", ex);
                    CloseComputer();
                }

                TimeSpan remaining = TimeSpan.FromSeconds(refreshSeconds) - elapsed.Elapsed;
                if (remaining < TimeSpan.FromMilliseconds(100))
                    remaining = TimeSpan.FromMilliseconds(100);

                try
                {
                    await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            CloseComputer();
        }
    }
    /*
    private void ResetMinMaxIfRequired()
    {
        int resetSeconds = Config.SensorsData_ResetMinMax_Seconds;

        if (resetSeconds <= 0)
            return;

        TimeSpan elapsed = DateTimeOffset.UtcNow - _lastMinMaxResetUtc;

        if (elapsed < TimeSpan.FromSeconds(resetSeconds))
            return;

        if (_computer is null)
            return;

        try
        {
            foreach (IHardware hardware in _computer.Hardware)
                ResetMinMax(hardware);

            _lastMinMaxResetUtc = DateTimeOffset.UtcNow;

            log.Info($"Sensor MIN/MAX values reset after {resetSeconds} seconds.");
        }
        catch (Exception ex)
        {
            log.Error("Could not reset sensor MIN/MAX values.", ex);
        }
    }
    */
    private void ResetMinMaxIfRequired()
    {
        int resetSeconds = Config.SensorsData_ResetMinMax_Seconds;

        if (resetSeconds <= 0)
            return;

        if (_minMaxResetTimer.Elapsed < TimeSpan.FromSeconds(resetSeconds))
            return;

        if (_computer is null)
            return;

        try
        {

            foreach (IHardware hardware in _computer.Hardware)
                ResetMinMax(hardware);

            _minMaxResetTimer.Restart();

            log.Info($"Sensor MIN/MAX values reset after {resetSeconds} seconds.");
        }
        catch (Exception ex)
        {
            log.Error("Could not reset sensor MIN/MAX values.", ex);
        }
    }

    private static bool IsValidSensorForSession(ISensor sensor)
    {
        if (sensor.Value is null)
            return false;
        if (!sensor.Value.HasValue)
            return false;

        if (!( // Only include:
            sensor.SensorType == SensorType.Voltage ||
            sensor.SensorType == SensorType.Temperature
            ))
        {
            return false;
        }

        /*
        Clock, // MHz
        Conductivity, // µS/cm
        Control, // %
        Current, // A
        Data, // B
        Energy, // milliwatt-hour (mWh)
        Factor, // 1
        Fan, // RPM
        Flow, // L/h
        Frequency, // Hz
        Humidity // %
        Level, // %
        Load, // %
        Power, // W
        Noise, // dBA
        Temperature, // °C
        Throughput, // B/s
        TimeSpan, // Seconds
        Timing, // ns
        Voltage, // V
        */

        //All OK:
        return true;
    }

    private static void ResetMinMax(IHardware hardware)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            try
            {
                if (!IsValidSensorForSession(sensor))
                    return;

                sensor.ResetMin();
                sensor.ResetMax();
            }
            catch
            {
                // A sensor may not support resetting MIN/MAX values.
            }
        }

        foreach (IHardware child in hardware.SubHardware)
            ResetMinMax(child);
    }

    public override void Dispose()
    {
        CloseComputer();
        base.Dispose();
    }

    private void EnsureComputerOpen()
    {
        if (_computer is not null)
            return;


        var computer = new Computer
        {
            //IsPowerMonitorEnabled = true,
            //IsNetworkEnabled = true,
            //IsPsuEnabled = true,
            //IsMemoryEnabled = true,
            //IsMotherboardEnabled = true,
            //IsControllerEnabled = true,
            //IsBatteryEnabled = true,
            //IsPowerMonitorEnabled = true,

            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
        };

        try
        {
            // LibreHardwareMonitor opens/installs its low-level driver here. Keeping this
            // Computer open avoids loading and unloading that driver on every Zabbix poll.
            computer.Open();
            _computer = computer;

            log.Info("LibreHardwareMonitor opened successfully.");
        }
        catch
        {
            try { computer.Close(); } catch { }
            throw;
        }
    }

    private void CloseComputer()
    {
        Computer? computer = Interlocked.Exchange(ref _computer, null);
        if (computer is null)
            return;

        try
        {
            computer.Close();
        }
        catch (Exception ex)
        {
            log.Error("LibreHardwareMonitor close failure", ex);
        }
    }

    private static void CollectDataSensors(IHardware hardware, ICollection<HardwareSensorValue> destination)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            Console.WriteLine(sensor.Identifier.ToString());

            if (!IsValidSensorForSession(sensor))
                continue;

            var hardwareType = "ERROR";
            try
            {
                hardwareType = sensor.Hardware.HardwareType.ToString();
            }
            catch
            {
                // Ignore errors in sensor type retrieval.
            }

            var hardwareName = "ERROR";
            try
            {
                hardwareName = sensor.Hardware.Name;
            }
            catch
            {
                // Ignore errors in sensor type retrieval.
            }

            var sensorIdentifier = "ERROR";
            try
            {
                sensorIdentifier = sensor.Identifier.ToString();
            }
            catch
            {
                // Ignore errors in sensor type retrieval.
            }

            var value = "ERROR";
            try
            {

                if (
                    /*
                    sensor.SensorType == SensorType.Data ||
                    sensor.SensorType == SensorType.Fan ||
                    sensor.SensorType == SensorType.TimeSpan ||
                    sensor.SensorType == SensorType.Clock ||
                    */
                    sensor.SensorType == SensorType.Temperature
                    )
                {
                    if (sensor.Value != null)
                    {
                        var valueInt = Convert.ToInt32(sensor.Value.Value, CultureInfo.InvariantCulture);
                        value = valueInt.ToString(CultureInfo.InvariantCulture);
                    }
                }

                else
                {
                    if (sensor.Value != null)
                    {
                        var valueFloat = Math.Round(Convert.ToDouble(sensor.Value.Value, CultureInfo.InvariantCulture), 3);
                        value = valueFloat.ToString("0.000", CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
                // Ignore errors in sensor identifier retrieval.
            }

            var Name = "ERROR";
            try
            {
                Name = sensor.Name;
            }
            catch
            {
                // Ignore errors in sensor name retrieval.
            }

            var minValue = "ERROR";
            try
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    var minValueInt = Convert.ToInt32(sensor.Min, CultureInfo.InvariantCulture);
                    minValue = minValueInt.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var minValueFloat = Math.Round(Convert.ToDouble(sensor.Min, CultureInfo.InvariantCulture), 3);
                    minValue = minValueFloat.ToString("0.000", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Ignore errors in sensor identifier retrieval.
            }

            var maxValue = "ERROR";
            try
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    var maxValueInt = Convert.ToInt32(sensor.Max, CultureInfo.InvariantCulture);
                    maxValue = maxValueInt.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var maxValueFloat = Math.Round(Convert.ToDouble(sensor.Max, CultureInfo.InvariantCulture), 3);
                    maxValue = maxValueFloat.ToString("0.000", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Ignore errors in sensor identifier retrieval.
            }

            var sensorType = "ERROR";
            try
            {
                sensorType = sensor.SensorType.ToString();
            }
            catch
            {
                // Ignore errors in sensor type retrieval.*
            }

            destination.Add(new HardwareSensorValue
            {
                HardwareName = hardwareName,
                HardwareType = hardwareType,

                SensorIdentifier = sensorIdentifier,
                SensorName = Name,
                SensorType = sensorType,

                Value = value,
                MinValue = minValue,
                MaxValue = maxValue,
            });
        }

        foreach (IHardware child in hardware.SubHardware)
            CollectDataSensors(child, destination);
    }
}

internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware child in hardware.SubHardware)
            child.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

internal sealed class HardwareSensorValue
{
    // Keep the original misspelled field name for backward compatibility with Zabbix.

    [JsonPropertyName("HardwareName")]
    public required string HardwareName { get; init; }

    [JsonPropertyName("HardwareType")]
    public required string HardwareType { get; init; }


    [JsonPropertyName("SensorIdentifier")]
    public required string SensorIdentifier { get; init; }

    [JsonPropertyName("SensorName")]
    public required string SensorName { get; init; }

    [JsonPropertyName("SensorType")]
    public required string SensorType { get; init; }

    [JsonPropertyName("Value")]
    public required string Value { get; init; }

    [JsonPropertyName("MinValue")]
    public required string MinValue { get; init; }

    [JsonPropertyName("MaxValue")]
    public required string MaxValue { get; init; }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
