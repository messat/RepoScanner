# Stage 1: compile with the .NET SDK. Nothing from this stage ships except the published app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The project files and shared build files first, so the restore step is reused until one of them changes
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/RepoScanner.Core/RepoScanner.Core.csproj src/RepoScanner.Core/
COPY src/RepoScanner.Data/RepoScanner.Data.csproj src/RepoScanner.Data/
COPY src/RepoScanner.Scanner/RepoScanner.Scanner.csproj src/RepoScanner.Scanner/
RUN dotnet restore src/RepoScanner.Scanner/RepoScanner.Scanner.csproj

COPY src/ src/
RUN dotnet publish src/RepoScanner.Scanner/RepoScanner.Scanner.csproj --configuration Release --no-restore --output /app -p:UseAppHost=false

# Stage 2: what ships. The .NET runtime, git for the clone (ADR-016) and the published app.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# The image's own non-root user, app (1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "RepoScanner.Scanner.dll"]
