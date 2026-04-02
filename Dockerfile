# Grand Adventure Engine — Multi-stage Docker build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY Directory.Build.props .
COPY src/GAE.Core/GAE.Core.csproj src/GAE.Core/
COPY src/GAE.Engine/GAE.Engine.csproj src/GAE.Engine/
COPY src/GAE.Narrator/GAE.Narrator.csproj src/GAE.Narrator/
COPY src/GAE.WikiSync/GAE.WikiSync.csproj src/GAE.WikiSync/
COPY src/GAE.Discord/GAE.Discord.csproj src/GAE.Discord/
COPY src/GAE.Dashboard.Api/GAE.Dashboard.Api.csproj src/GAE.Dashboard.Api/

RUN dotnet restore src/GAE.Dashboard.Api/GAE.Dashboard.Api.csproj

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/GAE.Dashboard.Api/GAE.Dashboard.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r gae && useradd -r -g gae -s /bin/false gae

# Copy published app
COPY --from=build /app/publish .
COPY config/ /app/config/

# Create data directory for journal/checkpoints
RUN mkdir -p /app/data && chown -R gae:gae /app/data

USER gae

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "GAE.Dashboard.Api.dll"]
