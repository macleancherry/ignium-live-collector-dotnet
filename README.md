# Ignium Live Collector (.NET)

Standalone Windows executable for collecting live timing from iRSDK and pushing to ignium-live-worker.

## Quick Start

1. **Download** `IgniumLiveCollector.exe` from releases
2. Create `.env` file in same folder:
   ```
   LIVE_API_INGEST_URL=https://your-worker-url/api/live/ingest
   LIVE_API_INGEST_TOKEN=your_secret_token
   POLL_INTERVAL_SECONDS=10
   REQUEST_TIMEOUT_SECONDS=10
   ```
3. Double-click `IgniumLiveCollector.exe` (make sure iRacing is running)

## Automated Builds and Releases

- Every push to `main` automatically builds the Windows EXE and uploads it as a workflow artifact.
- Every pushed tag like `v1.0.0` automatically creates/updates a GitHub Release and attaches `IgniumLiveCollector.exe`.

To publish a new release build:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Requirements

- Windows 10 or later
- iRacing installed and running
- .NET Runtime 8.0 (included in standalone exe)

## Building from Source

### Prerequisites
- .NET 8.0 SDK: https://dotnet.microsoft.com/download

### Build

```powershell
dotnet build
```

### Run

```powershell
dotnet run
```

### Publish as Standalone EXE

```powershell
dotnet publish -c Release -o publish
```

This creates `publish\IgniumLiveCollector.exe` (~150MB standalone)

## Configuration

Create `.env` in the same directory as the exe:

```
LIVE_API_INGEST_URL=https://your-worker-url/api/live/ingest
LIVE_API_INGEST_TOKEN=your_secret_token
POLL_INTERVAL_SECONDS=10
REQUEST_TIMEOUT_SECONDS=10
```

**Never share** `.env` — it contains your secret token.

## What it does

- Polls iRSDK every 10 seconds (low CPU impact)
- Reads driver position, lap times, and session info
- Sends snapshots to live timing server
- Stops automatically when iRacing closes

## Troubleshooting

- **"error: GetSessionInfo() returned null"**: Make sure iRacing is running
- **"ingest failed 401"**: Check LIVE_API_INGEST_TOKEN is correct
- **"Connection refused"**: Check LIVE_API_INGEST_URL is correct and worker is running

## Data sent

Each update includes (per driver):
- Session and subsession ID
- Driver name, customer ID, car number
- Position, class position
- Current lap, last lap time, best lap time
- Time gap and interval to leader
