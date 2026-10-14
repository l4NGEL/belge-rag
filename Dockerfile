# syntax=docker/dockerfile:1
# Tek Dockerfile, iki hedef: --target api | worker. Ortak restore/build katmanı önbelleğe alınır.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BelgeRag.Core/BelgeRag.Core.csproj src/BelgeRag.Core/
COPY src/BelgeRag.Infrastructure/BelgeRag.Infrastructure.csproj src/BelgeRag.Infrastructure/
COPY src/BelgeRag.Api/BelgeRag.Api.csproj src/BelgeRag.Api/
COPY src/BelgeRag.Worker/BelgeRag.Worker.csproj src/BelgeRag.Worker/
RUN dotnet restore src/BelgeRag.Api/BelgeRag.Api.csproj && dotnet restore src/BelgeRag.Worker/BelgeRag.Worker.csproj
COPY src/ src/
RUN dotnet publish src/BelgeRag.Api/BelgeRag.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/BelgeRag.Worker/BelgeRag.Worker.csproj -c Release --no-restore -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BelgeRag.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "BelgeRag.Worker.dll"]
