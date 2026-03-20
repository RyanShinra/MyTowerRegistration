# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Install dotnet-ef tool early — rarely changes, so this layer stays cached across source changes
RUN dotnet tool install --global dotnet-ef --version 10.0.0
ENV PATH="$PATH:/root/.dotnet/tools"

# Copy solution and project files first (layer caching for restore)
COPY MyTowerRegistration.sln .
COPY MyTowerRegistration.API/MyTowerRegistration.API.csproj MyTowerRegistration.API/
COPY MyTowerRegistration.Data/MyTowerRegistration.Data.csproj MyTowerRegistration.Data/
COPY MyTowerRegistration.Tests/MyTowerRegistration.Tests.csproj MyTowerRegistration.Tests/

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