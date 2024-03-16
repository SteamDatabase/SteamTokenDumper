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
rmdir /Q /S bin
dotnet publish -c Release --runtime win-x64 --self-contained true --output bin/SteamTokenDumper /p:PublishSingleFile=true /p:PublishTrimmed=true SteamTokenDumper.csproj
copy /b LICENSE+release_license.txt bin\SteamTokenDumper\LICENSE.txt
bash -c "unix2dos bin/SteamTokenDumper/SteamTokenDumper.config.ini"
bash -c "unix2dos bin/SteamTokenDumper/LICENSE.txt"
bash -c "cd bin && zip -9rj ../SteamTokenDumper.zip SteamTokenDumper/"

echo.
echo :: BUILDING LINUX
echo.

:: LINUX
del SteamTokenDumper-linux.tar.gz
rmdir /Q /S obj
rmdir /Q /S bin
dotnet publish -c Release --runtime linux-x64 --self-contained true --output bin/SteamTokenDumper /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true SteamTokenDumper.csproj
copy /b LICENSE+release_license.txt bin\SteamTokenDumper\LICENSE.txt
bash -c "dos2unix bin/SteamTokenDumper/SteamTokenDumper.config.ini"
bash -c "dos2unix bin/SteamTokenDumper/LICENSE.txt"
bash -c "tar -I 'gzip -9' -cvf SteamTokenDumper-linux.tar.gz --owner=0 --group=0 -C bin/SteamTokenDumper/ SteamTokenDumper SteamTokenDumper.config.ini LICENSE.txt"

echo.
echo :: FINALIZING
echo.

move ApiClient_build.cs.tmp ApiClient.cs
rmdir /Q /S obj
rmdir /Q /S bin
