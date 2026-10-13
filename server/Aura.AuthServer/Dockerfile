FROM mcr.microsoft.com/dotnet/sdk:8.0.425 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY ColorForge.Models/AuthSession.cs ColorForge.Models/AuthProtocol.cs ColorForge.Models/
COPY server/Aura.AuthServer/ server/Aura.AuthServer/
RUN dotnet restore server/Aura.AuthServer/Aura.AuthServer.csproj --locked-mode
RUN dotnet publish server/Aura.AuthServer/Aura.AuthServer.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:8.0.31
WORKDIR /app
COPY --from=build /app ./
ENV Aura__DataDirectory=/data
USER $APP_UID
ENTRYPOINT ["dotnet", "Aura.AuthServer.dll"]
