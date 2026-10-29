@echo off
REM Builds a single-file, self-contained GolfJoyBridge.exe into the "publish" folder.
REM Needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
dotnet publish src\GolfJoyBridge.App\GolfJoyBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the messages above and send them to whoever is helping you.
  exit /b 1
)
echo.
echo Done. Copy GRS.dll next to publish\GolfJoyBridge.exe, then run it.
