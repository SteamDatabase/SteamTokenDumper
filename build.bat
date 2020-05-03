@echo off

dotnet publish -c Release --runtime win-x64
dotnet publish -c Release --runtime linux-x64

bash zip -9j SteamTokenDumper.zip bin/Release/win-x64/publish/SteamTokenDumper.exe
bash env GZIP=-9 tar cvzf SteamTokenDumper-linux.tar.gz -C bin/Release/linux-x64/publish/ SteamTokenDumper
