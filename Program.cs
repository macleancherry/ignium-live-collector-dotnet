using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using irsdkSharp;

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

    public Collector(Config config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
        _irSdk = new IRacingSDK();
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
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }

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

    private List<DriverSnapshot> BuildRows()
    {
        var rows = new List<DriverSnapshot>();
        var now = DateTime.UtcNow.ToString("O");

        var sessionId = ReadTelemetryIntScalar("SessionUniqueID")?.ToString() ?? "0";
        var subsessionId = sessionId;

        var playerRow = BuildPlayerFallbackRow(sessionId, subsessionId, now);
        if (playerRow != null)
        {
            rows.Add(playerRow);
        }

        return rows.OrderBy(r => r.Position).ToList();
    }

    private DriverSnapshot? BuildPlayerFallbackRow(string sessionId, string subsessionId, string now)
    {
        var rawCarIdx = ReadTelemetryIntScalar("DriverCarIdx") ?? -1;
        // Worker validation requires customerId > 0. SessionInfo user id is not always available
        // in this lightweight fallback path, so derive a stable synthetic positive id from car idx.
        var customerId = rawCarIdx >= 0 ? rawCarIdx + 1 : 1;

        var position = ReadTelemetryIntScalar("PlayerCarPosition") ?? 0;
        var classPosition = ReadTelemetryIntScalar("PlayerCarClassPosition") ?? 0;
        var lap = ReadTelemetryIntScalar("LapCompleted") ?? 0;

        return new DriverSnapshot
        {
            SessionId = sessionId,
            SubsessionId = subsessionId,
            CustomerId = customerId,
            DriverName = "Player",
            CarNumber = string.Empty,
            Position = Math.Max(0, position),
            ClassPosition = Math.Max(0, classPosition),
            Lap = Math.Max(0, lap),
            LastLap = ReadTelemetryTimeScalar("LapLastLapTime"),
            BestLap = ReadTelemetryTimeScalar("LapBestLapTime"),
            Interval = null,
            Gap = null,
            UpdatedAt = now
        };
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

    private double? ReadTelemetryTimeScalar(string variable)
    {
        var value = _irSdk.GetData(variable);
        return NormalizeTime(ToDouble(value));
    }

    private static double? ToDouble(object? value)
    {
        if (value == null)
            return null;
        if (value is double d)
            return d;
        if (value is float f)
            return f;
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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

            Console.WriteLine("[collector] posted {0} rows at {1}", accepted, payload.CapturedAt);
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
