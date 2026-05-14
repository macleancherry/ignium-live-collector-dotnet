using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using irsdkSharp;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var config = ConfigBootstrap.LoadOrPrompt();
config.Validate();

var collector = new Collector(config);
await collector.RunAsync();

public class Config
{
    public string IngestUrl { get; set; } = "";
    public string IngestToken { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 10;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(IngestUrl) || string.IsNullOrWhiteSpace(IngestToken))
            throw new InvalidOperationException("LIVE_API_INGEST_URL and LIVE_API_INGEST_TOKEN are required");
        if (PollIntervalSeconds < 5)
            throw new InvalidOperationException("POLL_INTERVAL_SECONDS must be >= 5");
    }
}

public static class ConfigBootstrap
{
    public static Config LoadOrPrompt()
    {
        var exeDir = AppContext.BaseDirectory;
        var envPath = Path.Combine(exeDir, ".env");
        var map = LoadEnvMap(envPath);

        map.TryGetValue("LIVE_API_INGEST_URL", out var ingestUrl);
        map.TryGetValue("LIVE_API_INGEST_TOKEN", out var ingestToken);
        var poll = ParseInt(map, "POLL_INTERVAL_SECONDS", 10);
        var timeout = ParseInt(map, "REQUEST_TIMEOUT_SECONDS", 10);

        if (!string.IsNullOrWhiteSpace(ingestUrl) && !string.IsNullOrWhiteSpace(ingestToken))
        {
            return new Config
            {
                IngestUrl = ingestUrl,
                IngestToken = ingestToken,
                PollIntervalSeconds = poll,
                TimeoutSeconds = timeout
            };
        }

        Console.WriteLine("[setup] First run setup required.");
        Console.WriteLine("[setup] Enter values once; they will be saved next to the executable.");

        var url = PromptRequired("LIVE_API_INGEST_URL", ingestUrl);
        var token = PromptRequired("LIVE_API_INGEST_TOKEN", ingestToken);
        var pollInput = PromptOptional("POLL_INTERVAL_SECONDS", poll.ToString(CultureInfo.InvariantCulture));
        var timeoutInput = PromptOptional("REQUEST_TIMEOUT_SECONDS", timeout.ToString(CultureInfo.InvariantCulture));

        if (!int.TryParse(pollInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out poll) || poll < 5)
        {
            poll = 10;
        }

        if (!int.TryParse(timeoutInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout < 1)
        {
            timeout = 10;
        }

        var lines = new[]
        {
            $"LIVE_API_INGEST_URL={url}",
            $"LIVE_API_INGEST_TOKEN={token}",
            $"POLL_INTERVAL_SECONDS={poll}",
            $"REQUEST_TIMEOUT_SECONDS={timeout}"
        };

        File.WriteAllLines(envPath, lines, Encoding.ASCII);
        Console.WriteLine("[setup] Saved configuration to .env");

        return new Config
        {
            IngestUrl = url,
            IngestToken = token,
            PollIntervalSeconds = poll,
            TimeoutSeconds = timeout
        };
    }

    private static Dictionary<string, string> LoadEnvMap(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line.Substring(0, idx).Trim();
            var value = line[(idx + 1)..].Trim();
            map[key] = value;
        }

        return map;
    }

    private static int ParseInt(Dictionary<string, string> map, string key, int fallback)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string PromptRequired(string key, string? current)
    {
        while (true)
        {
            if (string.IsNullOrWhiteSpace(current))
            {
                Console.Write($"{key}: ");
            }
            else
            {
                Console.Write($"{key} [{current}]: ");
            }

            var value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return current;
                }

                Console.WriteLine($"[setup] {key} is required.");
                continue;
            }

            return value.Trim();
        }
    }

    private static string PromptOptional(string key, string current)
    {
        Console.Write($"{key} [{current}]: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }
}

public class Collector
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly IRacingSDK _irSdk;
    private readonly IDeserializer _yamlDeserializer;
    
    private int _lastSessionInfoUpdate = -1;
    private SessionInfo? _cachedSessionInfo;
    private readonly Dictionary<int, DriverData> _driverCache = new();

    public Collector(Config config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
        _irSdk = new IRacingSDK();
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task RunAsync()
    {
        Console.WriteLine("[collector] starting with {0}s polling", _config.PollIntervalSeconds);

        while (true)
        {
            try
            {
                if (!_irSdk.IsConnected())
                {
                    Console.WriteLine("[collector] waiting for iRacing session");
                    _lastSessionInfoUpdate = -1;
                    _cachedSessionInfo = null;
                    _driverCache.Clear();
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }

                UpdateSessionInfo();
                var rows = BuildRows();
                if (rows.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }
                await PostRows(rows);

                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[collector] error: {0}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
            }
        }
    }

    private void UpdateSessionInfo()
    {
        var updateCounter = ReadTelemetryIntScalar("SessionInfoUpdate") ?? 0;
        if (updateCounter == _lastSessionInfoUpdate)
            return;

        _lastSessionInfoUpdate = updateCounter;
        try
        {
            var sessionInfoYaml = _irSdk.GetData("SessionInfoString") as string;
            if (string.IsNullOrWhiteSpace(sessionInfoYaml))
            {
                Console.WriteLine("[collector] warning: empty SessionInfoString");
                return;
            }

            _cachedSessionInfo = _yamlDeserializer.Deserialize<SessionInfo>(sessionInfoYaml);
            if (_cachedSessionInfo?.DriverInfo?.Drivers == null || _cachedSessionInfo.DriverInfo.Drivers.Count == 0)
            {
                // Some SDK payloads are wrapped in a top-level SessionInfo node.
                var wrapped = _yamlDeserializer.Deserialize<SessionInfoEnvelope>(sessionInfoYaml);
                if (wrapped?.SessionInfo != null)
                {
                    _cachedSessionInfo = wrapped.SessionInfo;
                }
            }

            _driverCache.Clear();

            if (_cachedSessionInfo?.DriverInfo?.Drivers != null)
            {
                for (int i = 0; i < _cachedSessionInfo.DriverInfo.Drivers.Count; i++)
                {
                    var driver = _cachedSessionInfo.DriverInfo.Drivers[i];
                    if (driver.CarIdx >= 0)
                    {
                        _driverCache[driver.CarIdx] = driver;
                    }

                    if (!_driverCache.ContainsKey(i))
                    {
                        _driverCache[i] = driver;
                    }
                }
            }

            Console.WriteLine("[collector] updated session info with {0} drivers", _driverCache.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[collector] warning: failed to parse SessionInfoString: {0}", ex.Message);
        }
    }

    private List<DriverSnapshot> BuildRows()
    {
        var rows = new List<DriverSnapshot>();
        var now = DateTime.UtcNow.ToString("O");

        var sessionId = ReadTelemetryIntScalar("SessionUniqueID")?.ToString() ?? "0";
        var subsessionId = _cachedSessionInfo?.WeekendInfo?.SubSessionID.ToString() ?? sessionId;

        // Read all CarIdx arrays
        var positions = ReadIntArray("CarIdxPosition");
        var classPositions = ReadIntArray("CarIdxClassPosition");
        var lapsCompleted = ReadIntArray("CarIdxLapCompleted");
        var lastLapTimes = ReadDoubleArray("CarIdxLastLapTime");
        var bestLapTimes = ReadDoubleArray("CarIdxBestLapTime");
        var f2Times = ReadDoubleArray("CarIdxF2Time");
        var estTimes = ReadDoubleArray("CarIdxEstTime");

        if (positions == null || positions.Length == 0)
        {
            return rows; // No cars on track
        }

        // Build driver snapshots
        for (int carIdx = 0; carIdx < positions.Length; carIdx++)
        {
            var position = positions[carIdx];
            if (position < 0) continue; // Car not active in session

            var driverName = "Unknown";
            var carNumber = "";
            var customerId = carIdx + 1; // Fallback synthetic ID

            if (_driverCache.TryGetValue(carIdx, out var driverInfo))
            {
                if (!string.IsNullOrWhiteSpace(driverInfo.UserName))
                    driverName = driverInfo.UserName;
                if (!string.IsNullOrWhiteSpace(driverInfo.CarNumber))
                    carNumber = driverInfo.CarNumber;
                if (driverInfo.UserID > 0)
                    customerId = driverInfo.UserID;
            }

            var classPosition = classPositions?[carIdx] ?? 0;
            var lap = lapsCompleted?[carIdx] ?? 0;
            var lastLap = lastLapTimes?[carIdx];
            var bestLap = bestLapTimes?[carIdx];
            var gap = f2Times?[carIdx]; // Time delta to leader
            double? interval = null;

            rows.Add(new DriverSnapshot
            {
                SessionId = sessionId,
                SubsessionId = subsessionId,
                CustomerId = customerId,
                DriverName = driverName,
                CarNumber = carNumber,
                Position = Math.Max(0, position),
                ClassPosition = Math.Max(0, classPosition),
                Lap = Math.Max(0, lap),
                LastLap = NormalizeTime(lastLap),
                BestLap = NormalizeTime(bestLap),
                Gap = NormalizeTime(gap),
                Interval = interval,
                UpdatedAt = now
            });
        }

        // Compute intervals (gap to car ahead in same class)
        for (int i = 1; i < rows.Count; i++)
        {
            var current = rows[i];
            var ahead = rows.FirstOrDefault(r => r.ClassPosition == current.ClassPosition - 1);
            if (ahead != null && ahead.LastLap.HasValue && current.LastLap.HasValue)
            {
                // Simple interval: difference in last lap times
                var delta = current.LastLap.Value - ahead.LastLap.Value;
                current.Interval = NormalizeTime(delta);
            }
        }

        return rows;
    }

    private int[]? ReadIntArray(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null)
            return null;
        if (value is int[] intArr)
            return intArr;
        return null;
    }

    private double[]? ReadDoubleArray(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null)
            return null;
        if (value is double[] dblArr)
            return dblArr;
        if (value is float[] fltArr)
            return Array.ConvertAll(fltArr, x => (double)x);
        return null;
    }

    private int? ReadTelemetryIntScalar(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null)
            return null;
        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private double? NormalizeTime(double? value)
    {
        if (value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;
        if (value.Value <= 0 || value.Value >= 1800)
            return null;
        return Math.Round(value.Value, 3);
    }

    private async Task PostRows(List<DriverSnapshot> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("[collector] no valid rows");
            return;
        }

        var payload = new IngestPayload
        {
            Source = "irsdk",
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Rows = rows
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, _config.IngestUrl)
            {
                Content = content
            };
            request.Headers.Add("Authorization", $"Bearer {_config.IngestToken}");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine("[collector] ingest failed {0}: {1}", (int)response.StatusCode, body);
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<JsonElement>(responseBody);
            var accepted = responseObj.TryGetProperty("accepted", out var acceptedProp) ? acceptedProp.GetInt32() : rows.Count;

            Console.WriteLine("[collector] posted {0} rows", accepted);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[collector] error posting: {0}", ex.Message);
        }
    }
}

public class IngestPayload
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "irsdk";

    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = "";

    [JsonPropertyName("rows")]
    public List<DriverSnapshot> Rows { get; set; } = new();
}

public class DriverSnapshot
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("subsessionId")]
    public string SubsessionId { get; set; } = "";

    [JsonPropertyName("customerId")]
    public int CustomerId { get; set; }

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = "";

    [JsonPropertyName("carNumber")]
    public string CarNumber { get; set; } = "";

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("classPosition")]
    public int ClassPosition { get; set; }

    [JsonPropertyName("lap")]
    public int Lap { get; set; }

    [JsonPropertyName("lastLap")]
    public double? LastLap { get; set; }

    [JsonPropertyName("bestLap")]
    public double? BestLap { get; set; }

    [JsonPropertyName("interval")]
    public double? Interval { get; set; }

    [JsonPropertyName("gap")]
    public double? Gap { get; set; }

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}

// YAML models for iRSDK SessionInfoString
public class SessionInfo
{
    public WeekendInfo? WeekendInfo { get; set; }
    public DriverInfo? DriverInfo { get; set; }
    public List<SessionData>? Sessions { get; set; }
}

public class SessionInfoEnvelope
{
    public SessionInfo? SessionInfo { get; set; }
}

public class WeekendInfo
{
    public string? TrackName { get; set; }
    public string? TrackConfigName { get; set; }
    public string? SeriesName { get; set; }
    public int SubSessionID { get; set; }
    public int SessionID { get; set; }
}

public class DriverInfo
{
    public int DriverCount { get; set; }
    public List<DriverData>? Drivers { get; set; }
}

public class DriverData
{
    public int CarIdx { get; set; }
    public int UserID { get; set; }
    public string? UserName { get; set; }
    public string? Abbrev { get; set; }
    public string? Initials { get; set; }
    public string? CarNumber { get; set; }
    public int CarNumberRaw { get; set; }
    public string? CarScreenName { get; set; }
    public int CarClassID { get; set; }
}

public class SessionData
{
    public string? SessionType { get; set; }
    public int SessionNum { get; set; }
}
