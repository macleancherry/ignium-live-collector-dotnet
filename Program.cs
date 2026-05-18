using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using irsdkSharp;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;

var config = ConfigBootstrap.LoadOrPrompt();
config.Validate();

var collector = new Collector(config);
await collector.RunAsync();

public class Config
{
    public string IngestUrl { get; set; } = "";
    public string IngestToken { get; set; } = "";
    public string DebugRawUrl { get; set; } = "";
    public bool DebugRawEnabled { get; set; }
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
        map.TryGetValue("LIVE_API_DEBUG_RAW_URL", out var debugRawUrl);
        var debugRawEnabled = ParseBool(map, "DEBUG_RAW_ENABLED", false);
        var poll = ParseInt(map, "POLL_INTERVAL_SECONDS", 10);
        var timeout = ParseInt(map, "REQUEST_TIMEOUT_SECONDS", 10);

        if (string.IsNullOrWhiteSpace(debugRawUrl) && !string.IsNullOrWhiteSpace(ingestUrl))
        {
            debugRawUrl = ingestUrl.Replace("/api/live/ingest", "/api/live/debug/raw", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(ingestUrl) && !string.IsNullOrWhiteSpace(ingestToken))
        {
            if (debugRawEnabled && ShouldDisableDebugNow())
            {
                debugRawEnabled = false;
                map["DEBUG_RAW_ENABLED"] = "false";
                map["LIVE_API_INGEST_URL"] = ingestUrl;
                map["LIVE_API_INGEST_TOKEN"] = ingestToken;
                map["LIVE_API_DEBUG_RAW_URL"] = debugRawUrl ?? "";
                map["POLL_INTERVAL_SECONDS"] = poll.ToString(CultureInfo.InvariantCulture);
                map["REQUEST_TIMEOUT_SECONDS"] = timeout.ToString(CultureInfo.InvariantCulture);
                SaveEnvMap(envPath, map);
                Console.WriteLine("[setup] DEBUG_RAW_ENABLED was set to false for subsequent runs.");
            }

            return new Config
            {
                IngestUrl = ingestUrl,
                IngestToken = ingestToken,
                DebugRawUrl = debugRawUrl ?? "",
                DebugRawEnabled = debugRawEnabled,
                PollIntervalSeconds = poll,
                TimeoutSeconds = timeout
            };
        }

        Console.WriteLine("[setup] First run setup required.");
        Console.WriteLine("[setup] Enter values once; they will be saved next to the executable.");

        var url = PromptRequired("LIVE_API_INGEST_URL", ingestUrl);
        var token = PromptRequired("LIVE_API_INGEST_TOKEN", ingestToken);
        var debugRawEnabledInput = PromptOptional("DEBUG_RAW_ENABLED", debugRawEnabled ? "true" : "false");
        var debugRawUrlInput = PromptOptional("LIVE_API_DEBUG_RAW_URL", debugRawUrl ?? "");
        var pollInput = PromptOptional("POLL_INTERVAL_SECONDS", poll.ToString(CultureInfo.InvariantCulture));
        var timeoutInput = PromptOptional("REQUEST_TIMEOUT_SECONDS", timeout.ToString(CultureInfo.InvariantCulture));

        debugRawEnabled = bool.TryParse(debugRawEnabledInput, out var parsedDebug) && parsedDebug;
        debugRawUrl = debugRawUrlInput?.Trim();
        if (string.IsNullOrWhiteSpace(debugRawUrl))
        {
            debugRawUrl = url.Replace("/api/live/ingest", "/api/live/debug/raw", StringComparison.OrdinalIgnoreCase);
        }

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
            $"DEBUG_RAW_ENABLED={(debugRawEnabled ? "true" : "false")}",
            $"LIVE_API_DEBUG_RAW_URL={debugRawUrl}",
            $"POLL_INTERVAL_SECONDS={poll}",
            $"REQUEST_TIMEOUT_SECONDS={timeout}"
        };

        File.WriteAllLines(envPath, lines, Encoding.ASCII);
        Console.WriteLine("[setup] Saved configuration to .env");

        return new Config
        {
            IngestUrl = url,
            IngestToken = token,
            DebugRawEnabled = debugRawEnabled,
            DebugRawUrl = debugRawUrl ?? "",
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

    private static bool ShouldDisableDebugNow()
    {
        Console.Write("[setup] DEBUG_RAW_ENABLED is currently true. Disable debug mode? (Y/n): ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) || string.Equals(input, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveEnvMap(string path, Dictionary<string, string> map)
    {
        var lines = map
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();

        File.WriteAllLines(path, lines, Encoding.ASCII);
    }

    private static bool ParseBool(Dictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return bool.TryParse(value, out var parsed) ? parsed : fallback;
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
    private string _lastSessionInfoYaml = "";
    private readonly Dictionary<int, DriverData> _driverCache = new();
    private readonly Dictionary<int, PitState> _pitState = new();
    private readonly Dictionary<int, BestLapState> _bestLapState = new();

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
                    _lastSessionInfoYaml = "";
                    _driverCache.Clear();
                    _pitState.Clear();
                    _bestLapState.Clear();
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }

                UpdateSessionInfo();
                if (_config.DebugRawEnabled)
                {
                    await PostDebugRawSnapshot();
                }

                var rows = BuildRows();
                if (rows.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds));
                    continue;
                }
                var sessionMeta = BuildSessionMeta();
                await PostRows(rows, sessionMeta);

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
        var updateCounterRaw = ReadTelemetryIntScalar("SessionInfoUpdate");
        var updateCounter = updateCounterRaw ?? -1;
        var hasCachedYaml = !string.IsNullOrWhiteSpace(_lastSessionInfoYaml);
        if (updateCounter == _lastSessionInfoUpdate && hasCachedYaml)
            return;

        _lastSessionInfoUpdate = updateCounter;
        try
        {
            var sessionInfoYaml = TryReadSessionInfoYaml();
            if (string.IsNullOrWhiteSpace(sessionInfoYaml))
            {
                Console.WriteLine("[collector] warning: empty session info payload (keys tried: SessionInfoString, SessionInfo)");
                return;
            }

            _lastSessionInfoYaml = sessionInfoYaml;

            var sanitizedYaml = SanitizeSessionInfoYaml(sessionInfoYaml);

            var parseExceptionMessage = string.Empty;
            _cachedSessionInfo = null;
            try
            {
                _cachedSessionInfo = _yamlDeserializer.Deserialize<SessionInfo>(sanitizedYaml);
                if (_cachedSessionInfo?.DriverInfo?.Drivers == null || _cachedSessionInfo.DriverInfo.Drivers.Count == 0)
                {
                    // Some SDK payloads are wrapped in a top-level SessionInfo node.
                    var wrapped = _yamlDeserializer.Deserialize<SessionInfoEnvelope>(sanitizedYaml);
                    if (wrapped?.SessionInfo != null)
                    {
                        _cachedSessionInfo = wrapped.SessionInfo;
                    }
                }
            }
            catch (Exception ex)
            {
                parseExceptionMessage = ex.Message;
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

            if (_driverCache.Count == 0)
            {
                if (!TryPopulateDriverCacheFromYamlTree(sanitizedYaml) && !TryPopulateDriverCacheFromYamlTree(sessionInfoYaml))
                {
                    if (!TryPopulateDriverCacheFromYamlText(sanitizedYaml))
                    {
                        TryPopulateDriverCacheFromYamlText(sessionInfoYaml);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(parseExceptionMessage))
            {
                Console.WriteLine("[collector] warning: failed to parse SessionInfoString: {0}", parseExceptionMessage);
            }

            Console.WriteLine("[collector] updated session info with {0} drivers", _driverCache.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[collector] warning: failed to parse SessionInfoString: {0}", ex.Message);
        }
    }

    private string TryReadSessionInfoYaml()
    {
        var byReflection = TryReadSessionInfoViaSdkAccessors();
        if (!string.IsNullOrWhiteSpace(byReflection))
        {
            return byReflection;
        }

        var sessionInfoString = TryReadTelemetryString("SessionInfoString");
        if (!string.IsNullOrWhiteSpace(sessionInfoString))
        {
            return sessionInfoString;
        }

        var sessionInfo = TryReadTelemetryString("SessionInfo");
        if (!string.IsNullOrWhiteSpace(sessionInfo))
        {
            return sessionInfo;
        }

        return "";
    }

    private string? TryReadSessionInfoViaSdkAccessors()
    {
        try
        {
            var sdkType = _irSdk.GetType();

            foreach (var propertyName in new[]
            {
                "SessionInfoString",
                "SessionInfo",
                "SessionInfoYaml",
                "SessionInfoStr",
                "RawSessionInfo"
            })
            {
                var prop = sdkType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop?.CanRead == true)
                {
                    var value = prop.GetValue(_irSdk) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            foreach (var methodName in new[]
            {
                "GetSessionInfoString",
                "GetSessionInfo",
                "GetSessionInfoStr",
                "ReadSessionInfo",
                "ReadSessionInfoString"
            })
            {
                var method = sdkType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    var result = method.Invoke(_irSdk, null) as string;
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
            }
        }
        catch
        {
            // Best-effort probe only.
        }

        return null;
    }

    private string? TryReadTelemetryString(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null)
            return null;

        if (value is string str)
            return str;

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string SanitizeSessionInfoYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return yaml;
        }

        var sanitized = yaml.Replace("\0", string.Empty);

        sanitized = Regex.Replace(
            sanitized,
            @"(?m)^(\s*(?:TeamName|UserName|AbbrevName|Initials):\s*)(.*)$",
            match =>
            {
                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value.TrimEnd();
                if (value.Length == 0)
                {
                    return key;
                }

                if ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'")))
                {
                    return key + value;
                }

                var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return key + "\"" + escaped + "\"";
            });

        sanitized = Regex.Replace(
            sanitized,
            @"(?m)^(\s*[A-Za-z0-9_]+:\s*)(,.*)$",
            "$1\"$2\"");

        return sanitized;
    }

    private bool TryPopulateDriverCacheFromYamlTree(string sessionInfoYaml)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(sessionInfoYaml);
            stream.Load(reader);

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
                return false;

            var sessionNode = GetMappingNode(root, "SessionInfo") ?? root;
            var driverInfoNode = GetMappingNode(sessionNode, "DriverInfo");
            if (driverInfoNode == null)
                return false;

            var driversNode = GetSequenceNode(driverInfoNode, "Drivers");
            if (driversNode == null)
                return false;

            var idx = 0;
            foreach (var child in driversNode.Children)
            {
                if (child is not YamlMappingNode driverNode)
                    continue;

                var driver = new DriverData
                {
                    CarIdx = GetInt(driverNode, "CarIdx"),
                    UserID = GetInt(driverNode, "UserID"),
                    UserName = GetString(driverNode, "UserName"),
                    CarNumber = GetString(driverNode, "CarNumber"),
                    TeamName = GetString(driverNode, "TeamName"),
                    IRating = GetInt(driverNode, "IRating"),
                    CarClassID = GetInt(driverNode, "CarClassID"),
                    CarClassShortName = GetString(driverNode, "CarClassShortName"),
                    CarPath = GetString(driverNode, "CarPath"),
                    CarScreenNameShort = GetString(driverNode, "CarScreenNameShort"),
                    LicString = GetString(driverNode, "LicString")
                };

                if (driver.CarIdx >= 0)
                {
                    _driverCache[driver.CarIdx] = driver;
                }

                if (!_driverCache.ContainsKey(idx))
                {
                    _driverCache[idx] = driver;
                }

                idx++;
            }

            return _driverCache.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryPopulateDriverCacheFromYamlText(string sessionInfoYaml)
    {
        try
        {
            var matches = Regex.Matches(
                sessionInfoYaml,
                @"(?ms)^\s*-\s*CarIdx:\s*(?<carIdx>-?\d+)(?<body>.*?)(?=^\s*-\s*CarIdx:|\z)");

            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["carIdx"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var carIdx))
                {
                    continue;
                }

                var body = match.Groups["body"].Value;
                var userId = ExtractYamlInt(body, "UserID") ?? 0;

                var driver = new DriverData
                {
                    CarIdx = carIdx,
                    UserID = userId,
                    UserName = ExtractYamlString(body, "UserName"),
                    TeamName = ExtractYamlString(body, "TeamName"),
                    CarNumber = ExtractYamlString(body, "CarNumber"),
                    IRating = ExtractYamlInt(body, "IRating") ?? 0,
                    CarClassID = ExtractYamlInt(body, "CarClassID") ?? 0,
                    CarClassShortName = ExtractYamlString(body, "CarClassShortName"),
                    CarPath = ExtractYamlString(body, "CarPath"),
                    CarScreenNameShort = ExtractYamlString(body, "CarScreenNameShort"),
                    CarScreenName = ExtractYamlString(body, "CarScreenName"),
                    LicString = ExtractYamlString(body, "LicString")
                };

                if (driver.CarIdx >= 0)
                {
                    _driverCache[driver.CarIdx] = driver;
                }
            }

            return _driverCache.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractYamlString(string text, string key)
    {
        var pattern = $@"(?m)^\s*{Regex.Escape(key)}:\s*(?<value>.*)$";
        var match = Regex.Match(text, pattern);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if ((raw.StartsWith("\"") && raw.EndsWith("\"")) || (raw.StartsWith("'") && raw.EndsWith("'")))
        {
            raw = raw.Substring(1, raw.Length - 2);
        }

        return raw.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static int? ExtractYamlInt(string text, string key)
    {
        var raw = ExtractYamlString(text, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static YamlMappingNode? GetMappingNode(YamlMappingNode node, string key)
    {
        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value as YamlMappingNode;
            }
        }

        return null;
    }

    private static YamlSequenceNode? GetSequenceNode(YamlMappingNode node, string key)
    {
        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value as YamlSequenceNode;
            }
        }

        return null;
    }

    private static string? GetString(YamlMappingNode node, string key)
    {
        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return (entry.Value as YamlScalarNode)?.Value;
            }
        }

        return null;
    }

    private static int GetInt(YamlMappingNode node, string key)
    {
        var raw = GetString(node, key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
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
        var onPitRoad = ReadIntArray("CarIdxOnPitRoad");
        var f2Times = ReadDoubleArray("CarIdxF2Time");
        var estTimes = ReadDoubleArray("CarIdxEstTime");
        var trackSurfaces = ReadIntArray("CarIdxTrackSurface");
        var lapDistPcts = ReadDoubleArray("CarIdxLapDistPct");

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
            string? teamName = null;
            var carNumber = "";
            var customerId = carIdx + 1; // Fallback synthetic ID
            int? iRating = null;
            int? classId = null;
            string? classShortName = null;
            DriverData? driverInfo = null;

            if (_driverCache.TryGetValue(carIdx, out var cachedDriverInfo))
            {
                driverInfo = cachedDriverInfo;
                if (!string.IsNullOrWhiteSpace(driverInfo.UserName))
                    driverName = driverInfo.UserName;
                if (!string.IsNullOrWhiteSpace(driverInfo.TeamName))
                    teamName = driverInfo.TeamName;
                if (!string.IsNullOrWhiteSpace(driverInfo.CarNumber))
                    carNumber = driverInfo.CarNumber;
                if (driverInfo.UserID > 0)
                    customerId = driverInfo.UserID;
                if (driverInfo.IRating > 0)
                    iRating = driverInfo.IRating;
                if (driverInfo.CarClassID > 0)
                    classId = driverInfo.CarClassID;
                if (!string.IsNullOrWhiteSpace(driverInfo.CarClassShortName))
                    classShortName = driverInfo.CarClassShortName;
            }

            classShortName = ResolveClassShortName(classId, classShortName, driverInfo);

            var classPosition = classPositions?[carIdx] ?? 0;
            var lap = lapsCompleted?[carIdx] ?? 0;
            var lastLap = lastLapTimes?[carIdx];
            var bestLap = bestLapTimes?[carIdx];
            var gap = NormalizeTime(f2Times?[carIdx]);
            double? interval = null;

            var inPits = (onPitRoad?[carIdx] ?? 0) > 0;
            if (!_pitState.TryGetValue(carIdx, out var pitState))
            {
                pitState = new PitState();
                _pitState[carIdx] = pitState;
            }

            if (!pitState.WasInPits && inPits)
            {
                pitState.LastPitLap = Math.Max(1, lap);
            }

            if (pitState.WasInPits && !inPits)
            {
                pitState.OutLapAtLap = Math.Max(1, lap);
            }

            var outLap = !inPits && pitState.OutLapAtLap.HasValue && lap == pitState.OutLapAtLap.Value;
            if (pitState.OutLapAtLap.HasValue && lap > pitState.OutLapAtLap.Value)
            {
                pitState.OutLapAtLap = null;
            }
            pitState.WasInPits = inPits;

            if (!_bestLapState.TryGetValue(carIdx, out var bestState))
            {
                bestState = new BestLapState();
                _bestLapState[carIdx] = bestState;
            }

            var normalizedBestLap = NormalizeTime(bestLap);
            if (normalizedBestLap.HasValue)
            {
                if (!bestState.BestLap.HasValue || normalizedBestLap.Value < bestState.BestLap.Value - 0.0005)
                {
                    bestState.BestLap = normalizedBestLap.Value;
                    bestState.BestLapNumber = Math.Max(1, lap);
                }
            }

            var bestLapNumber = bestState.BestLapNumber;
            var normalizedLastLap = NormalizeTime(lastLap);
            bool? lastLapValid = null;
            if (lap > 1)
            {
                lastLapValid = normalizedLastLap.HasValue;
            }

            var trackSurface = trackSurfaces != null && carIdx < trackSurfaces.Length
                ? TrackSurfaceLabel(trackSurfaces[carIdx])
                : null;

            double? lapDistPct = null;
            if (lapDistPcts != null && carIdx < lapDistPcts.Length)
            {
                var rawDist = lapDistPcts[carIdx];
                if (rawDist >= 0.0 && rawDist <= 1.0)
                    lapDistPct = Math.Round(rawDist, 4);
            }

            var licString = driverInfo?.LicString;

            rows.Add(new DriverSnapshot
            {
                SessionId = sessionId,
                SubsessionId = subsessionId,
                CustomerId = customerId,
                DriverName = driverName,
                TeamName = teamName,
                CarNumber = carNumber,
                Position = Math.Max(0, position),
                ClassPosition = Math.Max(0, classPosition),
                ClassId = classId,
                ClassShortName = classShortName,
                IRating = iRating,
                Lap = Math.Max(0, lap),
                LastLap = normalizedLastLap,
                LastLapValid = lastLapValid,
                BestLap = normalizedBestLap,
                BestLapNumber = bestLapNumber,
                Gap = gap,
                Interval = interval,
                InPits = inPits,
                OutLap = outLap,
                LastPitLap = pitState.LastPitLap,
                TrackSurface = trackSurface,
                LapDistPct = lapDistPct,
                LicString = licString,
                UpdatedAt = now
            });
        }

        // Compute interval as time delta to car ahead in class using gap-to-leader values.
        foreach (var classGroup in rows
            .Where(r => r.ClassPosition > 0)
            .GroupBy(r => r.ClassId ?? -1))
        {
            var ordered = classGroup.OrderBy(r => r.ClassPosition).ThenBy(r => r.Position).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var ahead = ordered[i - 1];
                if (current.Gap.HasValue && ahead.Gap.HasValue)
                {
                    current.Interval = NormalizeTime(current.Gap.Value - ahead.Gap.Value);
                }
                else
                {
                    var currentEst = estTimes != null && current.Position > 0 && current.Position - 1 < estTimes.Length
                        ? NormalizeTime(estTimes[current.Position - 1])
                        : null;
                    var aheadEst = estTimes != null && ahead.Position > 0 && ahead.Position - 1 < estTimes.Length
                        ? NormalizeTime(estTimes[ahead.Position - 1])
                        : null;
                    if (currentEst.HasValue && aheadEst.HasValue)
                    {
                        current.Interval = NormalizeTime(currentEst.Value - aheadEst.Value);
                    }
                }
            }
        }

        return rows;
    }

    private static string? TrackSurfaceLabel(int value) => value switch
    {
        0 => "OffTrack",
        1 => "InPitStall",
        2 => "ApproachingPits",
        3 => "OnTrack",
        _ => null
    };

    private SessionMetaSnapshot BuildSessionMeta()
    {
        var sessionFlags = ReadTelemetryIntScalar("SessionFlags");
        var sessionTimeRemain = ReadTelemetryDoubleScalar("SessionTimeRemain");
        var sessionLapsRemain = ReadTelemetryIntScalar("SessionLapsRemain");
        var raceLaps = ReadTelemetryIntScalar("RaceLaps");

        return new SessionMetaSnapshot
        {
            SessionFlags = sessionFlags,
            SessionTimeRemain = sessionTimeRemain is double d && d > 0 && d < 86400 ? Math.Round(d, 1) : null,
            SessionLapsRemain = sessionLapsRemain,
            RaceLaps = raceLaps,
            TrackName = _cachedSessionInfo?.WeekendInfo?.TrackName,
            TrackConfig = _cachedSessionInfo?.WeekendInfo?.TrackConfigName
        };
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

    private double? ReadTelemetryDoubleScalar(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null) return null;
        if (value is double d) return d;
        if (value is float f) return (double)f;
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
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

    private static string? ResolveClassShortName(int? classId, string? classShortName, DriverData? driverInfo)
    {
        if (!string.IsNullOrWhiteSpace(classShortName))
        {
            var trimmed = classShortName.Trim();
            if (!trimmed.StartsWith("CarClassRelSpeed", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        if (classId == 4029)
            return "GTP";
        if (classId == 2523)
            return "LMP2";
        if (classId == 4011)
            return "IMSA23";

        var carPath = driverInfo?.CarPath;
        if (!string.IsNullOrWhiteSpace(carPath))
        {
            var path = carPath.ToLowerInvariant();
            if (path.Contains("gtp", StringComparison.Ordinal) || path.Contains("lmdh", StringComparison.Ordinal))
                return "GTP";
            if (path.Contains("p217", StringComparison.Ordinal) || path.Contains("lmp2", StringComparison.Ordinal))
                return "LMP2";
            if (path.Contains("gt3", StringComparison.Ordinal))
                return "IMSA23";
        }

        var screen = driverInfo?.CarScreenNameShort ?? driverInfo?.CarScreenName;
        if (!string.IsNullOrWhiteSpace(screen))
        {
            if (screen.Contains("GTP", StringComparison.OrdinalIgnoreCase) || screen.Contains("Hybrid V8", StringComparison.OrdinalIgnoreCase))
                return "GTP";
            if (screen.Contains("LMP2", StringComparison.OrdinalIgnoreCase))
                return "LMP2";
            if (screen.Contains("GT3", StringComparison.OrdinalIgnoreCase))
                return "IMSA23";
        }

        return null;
    }

    private async Task PostRows(List<DriverSnapshot> rows, SessionMetaSnapshot? sessionMeta = null)
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
            Rows = rows,
            SessionMeta = sessionMeta
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

    private async Task PostDebugRawSnapshot()
    {
        if (string.IsNullOrWhiteSpace(_config.DebugRawUrl))
        {
            return;
        }

        var payload = new DebugRawEnvelope
        {
            Source = "irsdk-debug",
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Payload = new DebugRawSnapshot
            {
                SessionInfoUpdate = ReadTelemetryIntScalar("SessionInfoUpdate"),
                SessionUniqueId = ReadTelemetryIntScalar("SessionUniqueID"),
                SessionInfoYaml = _lastSessionInfoYaml,
                SessionInfoYamlChars = _lastSessionInfoYaml.Length,
                DebugNotes = string.IsNullOrWhiteSpace(_lastSessionInfoYaml)
                    ? "Session info YAML is empty in cache"
                    : "Session info YAML captured",
                VariableProbes = new List<DebugVarProbe>
                {
                    BuildVarProbe("SessionInfoUpdate"),
                    BuildVarProbe("SessionInfoString"),
                    BuildVarProbe("SessionInfo"),
                    BuildVarProbe("SessionUniqueID"),
                    BuildVarProbe("DriverInfo"),
                    BuildVarProbe("WeekendInfo")
                },
                CarIdxPosition = ReadIntArray("CarIdxPosition"),
                CarIdxClassPosition = ReadIntArray("CarIdxClassPosition"),
                CarIdxLapCompleted = ReadIntArray("CarIdxLapCompleted"),
                CarIdxLastLapTime = ReadDoubleArray("CarIdxLastLapTime"),
                CarIdxBestLapTime = ReadDoubleArray("CarIdxBestLapTime"),
                CarIdxF2Time = ReadDoubleArray("CarIdxF2Time"),
                CarIdxEstTime = ReadDoubleArray("CarIdxEstTime"),
                CachedDriverCount = _driverCache.Count,
                ParsedDriverCache = _driverCache.Values
                    .Select(x => new DebugDriver
                    {
                        CarIdx = x.CarIdx,
                        UserId = x.UserID,
                        UserName = x.UserName ?? "",
                        CarNumber = x.CarNumber ?? ""
                    })
                    .OrderBy(x => x.CarIdx)
                    .ToList()
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, _config.DebugRawUrl)
            {
                Content = content
            };
            request.Headers.Add("Authorization", $"Bearer {_config.IngestToken}");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine("[collector] debug raw post failed {0}: {1}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[collector] debug raw error: {0}", ex.Message);
        }
    }

    private DebugVarProbe BuildVarProbe(string variable)
    {
        var value = _irSdk.GetData(variable);
        if (value == null)
        {
            return new DebugVarProbe
            {
                Name = variable,
                Exists = false,
                Type = null,
                Preview = null,
                Length = null
            };
        }

        return new DebugVarProbe
        {
            Name = variable,
            Exists = true,
            Type = value.GetType().FullName,
            Preview = BuildValuePreview(value),
            Length = GetValueLength(value)
        };
    }

    private static string BuildValuePreview(object value)
    {
        if (value is string str)
        {
            return Truncate(str.Replace("\r", "").Replace("\n", "\\n"), 240);
        }

        if (value is int[] intArr)
        {
            return $"int[{intArr.Length}] first=[{string.Join(",", intArr.Take(8))}]";
        }

        if (value is float[] floatArr)
        {
            return $"float[{floatArr.Length}] first=[{string.Join(",", floatArr.Take(4).Select(x => x.ToString("F3", CultureInfo.InvariantCulture)))}]";
        }

        if (value is double[] doubleArr)
        {
            return $"double[{doubleArr.Length}] first=[{string.Join(",", doubleArr.Take(4).Select(x => x.ToString("F3", CultureInfo.InvariantCulture)))}]";
        }

        return Truncate(value.ToString() ?? "", 240);
    }

    private static int? GetValueLength(object value)
    {
        if (value is string str)
            return str.Length;
        if (value is Array arr)
            return arr.Length;
        return null;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;
        return value.Substring(0, maxChars);
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

    [JsonPropertyName("sessionMeta")]
    public SessionMetaSnapshot? SessionMeta { get; set; }
}

public class SessionMetaSnapshot
{
    [JsonPropertyName("sessionFlags")]
    public int? SessionFlags { get; set; }

    [JsonPropertyName("sessionTimeRemain")]
    public double? SessionTimeRemain { get; set; }

    [JsonPropertyName("sessionLapsRemain")]
    public int? SessionLapsRemain { get; set; }

    [JsonPropertyName("raceLaps")]
    public int? RaceLaps { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("trackConfig")]
    public string? TrackConfig { get; set; }
}

public class DebugRawEnvelope
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "irsdk-debug";

    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = "";

    [JsonPropertyName("payload")]
    public DebugRawSnapshot Payload { get; set; } = new();
}

public class DebugRawSnapshot
{
    [JsonPropertyName("sessionInfoUpdate")]
    public int? SessionInfoUpdate { get; set; }

    [JsonPropertyName("sessionUniqueId")]
    public int? SessionUniqueId { get; set; }

    [JsonPropertyName("sessionInfoYaml")]
    public string SessionInfoYaml { get; set; } = "";

    [JsonPropertyName("sessionInfoYamlChars")]
    public int SessionInfoYamlChars { get; set; }

    [JsonPropertyName("debugNotes")]
    public string DebugNotes { get; set; } = "";

    [JsonPropertyName("variableProbes")]
    public List<DebugVarProbe> VariableProbes { get; set; } = new();

    [JsonPropertyName("carIdxPosition")]
    public int[]? CarIdxPosition { get; set; }

    [JsonPropertyName("carIdxClassPosition")]
    public int[]? CarIdxClassPosition { get; set; }

    [JsonPropertyName("carIdxLapCompleted")]
    public int[]? CarIdxLapCompleted { get; set; }

    [JsonPropertyName("carIdxLastLapTime")]
    public double[]? CarIdxLastLapTime { get; set; }

    [JsonPropertyName("carIdxBestLapTime")]
    public double[]? CarIdxBestLapTime { get; set; }

    [JsonPropertyName("carIdxF2Time")]
    public double[]? CarIdxF2Time { get; set; }

    [JsonPropertyName("carIdxEstTime")]
    public double[]? CarIdxEstTime { get; set; }

    [JsonPropertyName("cachedDriverCount")]
    public int CachedDriverCount { get; set; }

    [JsonPropertyName("parsedDriverCache")]
    public List<DebugDriver> ParsedDriverCache { get; set; } = new();
}

public class DebugVarProbe
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("preview")]
    public string? Preview { get; set; }
}

public class DebugDriver
{
    [JsonPropertyName("carIdx")]
    public int CarIdx { get; set; }

    [JsonPropertyName("userId")]
    public int UserId { get; set; }

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = "";

    [JsonPropertyName("carNumber")]
    public string CarNumber { get; set; } = "";
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

    [JsonPropertyName("teamName")]
    public string? TeamName { get; set; }

    [JsonPropertyName("carNumber")]
    public string CarNumber { get; set; } = "";

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("classPosition")]
    public int ClassPosition { get; set; }

    [JsonPropertyName("classId")]
    public int? ClassId { get; set; }

    [JsonPropertyName("classShortName")]
    public string? ClassShortName { get; set; }

    [JsonPropertyName("iRating")]
    public int? IRating { get; set; }

    [JsonPropertyName("lap")]
    public int Lap { get; set; }

    [JsonPropertyName("lastLap")]
    public double? LastLap { get; set; }

    [JsonPropertyName("lastLapValid")]
    public bool? LastLapValid { get; set; }

    [JsonPropertyName("bestLap")]
    public double? BestLap { get; set; }

    [JsonPropertyName("bestLapNumber")]
    public int? BestLapNumber { get; set; }

    [JsonPropertyName("interval")]
    public double? Interval { get; set; }

    [JsonPropertyName("gap")]
    public double? Gap { get; set; }

    [JsonPropertyName("inPits")]
    public bool InPits { get; set; }

    [JsonPropertyName("outLap")]
    public bool OutLap { get; set; }

    [JsonPropertyName("lastPitLap")]
    public int? LastPitLap { get; set; }

    [JsonPropertyName("trackSurface")]
    public string? TrackSurface { get; set; }

    [JsonPropertyName("lapDistPct")]
    public double? LapDistPct { get; set; }

    [JsonPropertyName("licString")]
    public string? LicString { get; set; }

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
    public string? TeamName { get; set; }
    public string? Abbrev { get; set; }
    public string? AbbrevName { get; set; }
    public string? Initials { get; set; }
    public string? CarNumber { get; set; }
    public int CarNumberRaw { get; set; }
    public string? CarPath { get; set; }
    public string? CarScreenName { get; set; }
    public string? CarScreenNameShort { get; set; }
    public int CarClassID { get; set; }
    public string? CarClassShortName { get; set; }
    public int IRating { get; set; }
    public string? LicString { get; set; }
}

public class PitState
{
    public bool WasInPits { get; set; }
    public int? LastPitLap { get; set; }
    public int? OutLapAtLap { get; set; }
}

public class BestLapState
{
    public double? BestLap { get; set; }
    public int? BestLapNumber { get; set; }
}

public class SessionData
{
    public string? SessionType { get; set; }
    public int SessionNum { get; set; }
}
