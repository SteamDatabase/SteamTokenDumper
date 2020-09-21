@echo off

echo.
echo :: PREPARING
echo.

SET DOTNET_CLI_TELEMETRY_OPTOUT=1

set /p BuildToken=<apiclient_token.txt
move ApiClient.cs ApiClient_build.cs.tmp
powershell -Command "(gc ApiClient_build.cs.tmp) -replace '@STEAMDB_BUILD_TOKEN@', '%BuildToken%' | Out-File -encoding ASCII ApiClient.cs"

echo.
echo :: BUILDING WINDOWS
echo.

:: WINDOWS
del SteamTokenDumper.zip
rmdir /Q /S obj
rmdir /Q /S bin\Release
dotnet publish -c Release --runtime win-x64 --output bin/SteamTokenDumper /p:PublishSingleFile=true /p:PublishTrimmed=true
bash -c "cd bin && zip -9r ../SteamTokenDumper.zip SteamTokenDumper/"

echo.
echo :: BUILDING LINUX
echo.

:: LINUX
del SteamTokenDumper-linux.tar.gz
rmdir /Q /S obj
rmdir /Q /S bin\Release
dotnet publish -c Release --runtime linux-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true
bash -c "env GZIP=-9 tar cvzf SteamTokenDumper-linux.tar.gz --owner=0 --group=0 -C bin/Release/linux-x64/publish/ SteamTokenDumper"

echo.
echo :: FINALIZING
echo.

move ApiClient_build.cs.tmp ApiClient.cs
rmdir /Q /S obj
rmdir /Q /S bin\Release
