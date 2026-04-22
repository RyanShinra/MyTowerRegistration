# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first (layer caching for restore)
# Adding dotnet-tools.json here means tool restore is also cached until
# tool versions change — same principle as caching NuGet packages separately.
COPY .config/dotnet-tools.json .config/
COPY MyTowerRegistration.sln .
COPY MyTowerRegistration.API/MyTowerRegistration.API.csproj MyTowerRegistration.API/
COPY MyTowerRegistration.Data/MyTowerRegistration.Data.csproj MyTowerRegistration.Data/
COPY MyTowerRegistration.Tests/MyTowerRegistration.Tests.csproj MyTowerRegistration.Tests/
COPY MyTowerRegistration.Admin/MyTowerRegistration.Admin.csproj MyTowerRegistration.Admin/

# dotnet tool restore installs local tools from dotnet-tools.json (dotnet-ef, husky).
# This replaces the old global install approach — local tools lock exact versions
# per-repo and are the idiomatic .NET way to share CLI tool dependencies.
RUN dotnet tool restore
ENV PATH="$PATH:/root/.dotnet/tools"

RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish MyTowerRegistration.API/MyTowerRegistration.API.csproj \
    -c Release -o /app/publish --no-restore
# Builds `migrate-db` which is used below
RUN dotnet ef migrations bundle \
    --project MyTowerRegistration.Data/MyTowerRegistration.Data.csproj \
    --startup-project MyTowerRegistration.API/MyTowerRegistration.API.csproj \
    --output /app/migrate-db \
    --configuration Release

# EF Migrations Bundle stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS dbMigrations
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
# The `migrate-db` tool is copied from the build stage and will be used to apply database migrations at runtime.
COPY --from=build /app/migrate-db .
ENTRYPOINT [ "./migrate-db", "--connection" ]

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MyTowerRegistration.API.dll"]