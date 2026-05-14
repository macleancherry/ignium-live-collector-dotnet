using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetEnv;
using iRSDKSharp;

// Load environment variables
DotEnv.Load();

var config = new Config();
config.Validate();

var collector = new Collector(config);
await collector.RunAsync();

public class Config
{
    public string IngestUrl { get; set; } = Environment.GetEnvironmentVariable("LIVE_API_INGEST_URL") ?? "";
    public string IngestToken { get; set; } = Environment.GetEnvironmentVariable("LIVE_API_INGEST_TOKEN") ?? "";
    public int PollIntervalSeconds { get; set; } = int.Parse(Environment.GetEnvironmentVariable("POLL_INTERVAL_SECONDS") ?? "10");
    public int TimeoutSeconds { get; set; } = int.Parse(Environment.GetEnvironmentVariable("REQUEST_TIMEOUT_SECONDS") ?? "10");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(IngestUrl) || string.IsNullOrWhiteSpace(IngestToken))
            throw new InvalidOperationException("LIVE_API_INGEST_URL and LIVE_API_INGEST_TOKEN are required");
        if (PollIntervalSeconds < 5)
            throw new InvalidOperationException("POLL_INTERVAL_SECONDS must be >= 5");
    }
}

public class Collector
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly iRSDK _irSdk;

    public Collector(Config config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
        _irSdk = new iRSDK();
    }

    public async Task RunAsync()
    {
        Console.WriteLine("[collector] starting with {0}s polling", _config.PollIntervalSeconds);

        while (true)
        {
            try
            {
                if (!_irSdk.IsConnected)
                {
                    Console.WriteLine("[collector] waiting for iRacing session");
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }

                var sessionInfo = _irSdk.GetSessionInfo();
                var telemetry = _irSdk.GetTelemetry();

                if (sessionInfo == null || telemetry == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }

                var rows = BuildRows(sessionInfo, telemetry);
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

    private List<DriverSnapshot> BuildRows(SessionInfo sessionInfo, Telemetry telemetry)
    {
        var rows = new List<DriverSnapshot>();
        var now = DateTime.UtcNow.ToString("O");

        var sessionId = sessionInfo.WeekendInfo?.SessionID?.ToString() ?? "0";
        var subsessionId = sessionInfo.WeekendInfo?.SubSessionID?.ToString() ?? "0";
        var drivers = sessionInfo.DriverInfo?.Drivers ?? new();

        foreach (var driver in drivers)
        {
            var carIdx = driver.CarIdx;
            var customerId = driver.UserID;

            if (carIdx < 0 || customerId <= 0)
                continue;

            var position = telemetry.CarIdxPosition?[carIdx];
            var classPosition = telemetry.CarIdxClassPosition?[carIdx];
            var lapCompleted = telemetry.CarIdxLapCompleted?[carIdx];

            if (position == null || classPosition == null || lapCompleted == null || position < 0 || classPosition < 0 || lapCompleted < 0)
                continue;

            rows.Add(new DriverSnapshot
            {
                SessionId = sessionId,
                SubsessionId = subsessionId,
                CustomerId = customerId,
                DriverName = driver.UserName ?? $"User-{customerId}",
                CarNumber = driver.CarNumber ?? "",
                Position = (int)position,
                ClassPosition = (int)classPosition,
                Lap = (int)lapCompleted,
                LastLap = NormalizeTime(telemetry.CarIdxLastLapTime?[carIdx]),
                BestLap = NormalizeTime(telemetry.CarIdxBestLapTime?[carIdx]),
                Interval = NormalizeTime(telemetry.CarIdxF2Time?[carIdx]),
                Gap = NormalizeTime(telemetry.CarIdxEstTime?[carIdx]),
                UpdatedAt = now
            });
        }

        return rows.OrderBy(r => r.Position).ToList();
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
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

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

[JsonSerializable]
public class IngestPayload
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "irsdk";

    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = "";

    [JsonPropertyName("rows")]
    public List<DriverSnapshot> Rows { get; set; } = new();
}

[JsonSerializable]
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
