@echo off
REM Build standalone executable
echo Building Ignium Live Collector (.NET)...
dotnet publish -c Release -o publish --self-contained -r win-x64

echo.
echo Build complete! Executable: publish\IgniumLiveCollector.exe
echo Copy the .env file to the publish folder to run.
pause
