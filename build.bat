@echo off

:: WINDOWS
rmdir /Q /S bin\Release
dotnet publish -c Release -p:PublishSingleFile=true --runtime win-x64
bash -c "zip -9j SteamTokenDumper.zip bin/Release/win-x64/publish/SteamTokenDumper.exe"

:: LINUX
rmdir /Q /S bin\Release
dotnet publish -c Release -p:PublishSingleFile=true --runtime linux-x64
bash -c "env GZIP=-9 tar cvzf SteamTokenDumper-linux.tar.gz -C bin/Release/linux-x64/publish/ SteamTokenDumper"

rmdir /Q /S bin\Release
