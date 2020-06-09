@echo off

set /p BuildToken=<apiclient_token.txt
move ApiClient.cs ApiClient_build.cs.tmp
powershell -Command "(gc ApiClient_build.cs.tmp) -replace '@STEAMDB_BUILD_TOKEN@', '%BuildToken%' | Out-File -encoding ASCII ApiClient.cs"

:: WINDOWS
del SteamTokenDumper.zip
rmdir /Q /S obj
rmdir /Q /S bin\Release
dotnet publish -c Release -p:PublishSingleFile=true --runtime win-x64
bash -c "zip -9j SteamTokenDumper.zip bin/Release/win-x64/publish/SteamTokenDumper.exe"

:: LINUX
del SteamTokenDumper-linux.tar.gz
rmdir /Q /S obj
rmdir /Q /S bin\Release
dotnet publish -c Release -p:PublishSingleFile=true --runtime linux-x64
bash -c "env GZIP=-9 tar cvzf SteamTokenDumper-linux.tar.gz --owner=0 --group=0 -C bin/Release/linux-x64/publish/ SteamTokenDumper"

move ApiClient_build.cs.tmp ApiClient.cs
rmdir /Q /S obj
rmdir /Q /S bin\Release
