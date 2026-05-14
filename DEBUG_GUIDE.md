# Debug Pipeline Guide

The debug pipeline captures raw iRSDK SessionInfo YAML and telemetry array data, stores it in D1, and allows inspection of exact payload structures to diagnose schema mismatches.

## Quick Start

### 1. Download v1.0.6 Collector
Once the GitHub Actions build completes, download the latest release:
- **Repo**: https://github.com/macleancherry/ignium-live-collector-dotnet/releases
- **Asset**: `IgniumLiveCollector.exe` (v1.0.6 or later)

### 2. Enable Debug Mode

Edit or create `.env` in the same directory as the executable:

```env
LIVE_API_INGEST_URL=https://ignium-live-api.maclean-cherry.workers.dev/api/live/ingest
LIVE_API_INGEST_TOKEN=your_ingest_token_here
DEBUG_RAW_ENABLED=true
LIVE_API_DEBUG_RAW_URL=https://ignium-live-api.maclean-cherry.workers.dev/api/live/debug/raw
POLL_INTERVAL_SECONDS=10
REQUEST_TIMEOUT_SECONDS=10
```

**Key Changes**:
- `DEBUG_RAW_ENABLED=true` → Activates debug payload capture and upload
- `LIVE_API_DEBUG_RAW_URL` → Already auto-filled if not specified; should match your ingest URL with `/debug/raw` suffix

### 3. Run Collector During iRacing Session

```bash
.\IgniumLiveCollector.exe
```

Console output will show:
```
[collector] posted 15 rows
[collector] debug raw post failed...  (if url is wrong)
[collector] debug raw error: ...      (if network issue)
```

If no errors, debug payloads are being captured.

### 4. Query Raw Debug Data

Once the collector has been running and capturing data during a session, query the debug endpoint:

```bash
curl -H "Authorization: Bearer your_ingest_token_here" \
  "https://ignium-live-api.maclean-cherry.workers.dev/api/live/debug/raw?limit=1"
```

Response will include:
```json
{
  "ok": true,
  "rows": [
    {
      "id": 123,
      "source": "irsdk-debug",
      "capturedAt": "2025-01-15T14:30:45.1234567Z",
      "receivedAt": "2025-01-15T14:30:45.9876543Z",
      "payloadJson": "{...full debug payload...}"
    }
  ],
  "count": 1,
  "generatedAt": "2025-01-15T14:31:00Z"
}
```

### 5. Analyze Raw Payload

Parse the `payloadJson` field from the response. It contains:

```json
{
  "source": "irsdk-debug",
  "capturedAt": "2025-01-15T14:30:45Z",
  "payload": {
    "sessionInfoUpdate": 42,
    "sessionUniqueId": 12345678,
    "sessionInfoYaml": "\"WeekendInfo:\\n  SubSessionID: 123456789\\n  ...(full YAML)\"",
    "carIdxPosition": [0, 1, 2, ...],
    "carIdxClassPosition": [0, 1, 2, ...],
    "carIdxLapCompleted": [5, 5, 4, ...],
    "carIdxLastLapTime": [87.234, 87.456, null, ...],
    "carIdxBestLapTime": [86.123, 86.789, 88.234, ...],
    "carIdxF2Time": [...],
    "carIdxEstTime": [...],
    "parsedDriverCache": [
      {"carIdx": 0, "userId": 1234567, "userName": "Driver Name", "carNumber": "01"},
      {"carIdx": 1, "userId": 7654321, "userName": "Driver 2", "carNumber": "02"},
      ...
    ]
  }
}
```

**Key Fields**:
- `sessionInfoYaml`: The raw SessionInfo YAML string from iRSDK (JSON-escaped)
  - Extract this and pretty-print to see the actual YAML structure
  - Compare against expected schema to find missing/extra properties
- `parsedDriverCache`: Shows what the collector was able to extract
  - If empty or incomplete, indicates YAML parsing issue
- `carIdxPosition`, `carIdxLastLapTime`, etc.: Raw telemetry arrays
  - Verify these match position/car count expectations

### 6. Investigate Schema Mismatches

**Example Workflow**:

1. Get raw YAML from debug payload
2. Pretty-print it (unescape JSON escapes `\"` → `"`, `\n` → newlines)
3. Check actual property names and structure:
   - Are driver properties at `.SessionInfo.DriverInfo.Drivers[].UserName` or elsewhere?
   - Are there extra nesting levels or wrapped variants?
   - Do property names use different casing? (e.g., `driver_name` vs `driverName`)

4. If schema differs from models in Program.cs:
   - Update `DriverData` class properties
   - Update `SessionInfo`/`DriverInfo` models
   - Rebuild and retest

### 7. Disable Debug Mode (When Done)

Set `DEBUG_RAW_ENABLED=false` in `.env` to stop sending debug payloads. This reduces network traffic.

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "debug raw post failed 401" | INGEST_TOKEN is wrong or expired; verify Bearer token matches |
| "debug raw error: connection refused" | Check LIVE_API_DEBUG_RAW_URL is correct and worker is deployed |
| "parsedDriverCache" is empty | SessionInfo YAML structure differs from models; check schema via raw YAML |
| No data in `/api/live/debug/raw` | Ensure collector ran with `DEBUG_RAW_ENABLED=true` and posted successfully |
| "unauthorized" on debug query | Include `-H "Authorization: Bearer <token>"` in curl |

---

## API Reference

### POST /api/live/debug/raw
Ingest raw debug payloads (called automatically by collector in debug mode).

**Request**:
```
Authorization: Bearer {INGEST_TOKEN}
Content-Type: application/json

{
  "source": "irsdk-debug",
  "capturedAt": "2025-01-15T14:30:45Z",
  "payload": { ...full debug snapshot... }
}
```

**Response**:
```json
{
  "ok": true,
  "stored": true,
  "receivedAt": "2025-01-15T14:30:45Z"
}
```

### GET /api/live/debug/raw
Retrieve stored debug payloads.

**Query Params**:
- `limit=N` (1-25, default 1) - Number of recent snapshots to return

**Request**:
```
Authorization: Bearer {INGEST_TOKEN}
GET /api/live/debug/raw?limit=5
```

**Response**:
```json
{
  "ok": true,
  "rows": [...],
  "count": 5,
  "generatedAt": "2025-01-15T14:31:00Z"
}
```

---

## Expected Behavior

- **Every 10s** (configurable): Collector posts debug snapshot if `DEBUG_RAW_ENABLED=1`
- **Raw YAML stored**: Full SessionInfoString from iRSDK preserved in D1 for schema inspection
- **Telemetry arrays captured**: All CarIdx* arrays and parsed driver cache stored together
- **No impact on live ingest**: Debug payloads go to separate `/api/live/debug/raw` endpoint; regular ingest continues on `/api/live/ingest`

---

## Next Steps

Once raw data is captured and inspected:
1. Document exact YAML structure (property names, nesting, optional/required fields)
2. Update `SessionInfo`, `DriverInfo`, and `DriverData` models if needed
3. Update `TryPopulateDriverCacheFromYamlTree()` logic if schema changed
4. Rebuild collector and verify all 15+ drivers show with names and numbers
5. Disable debug mode and deploy final version
