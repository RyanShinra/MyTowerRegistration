# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first (layer caching for restore)
COPY MyTowerRegistration.sln .
COPY MyTowerRegistration.API/MyTowerRegistration_API.csproj MyTowerRegistration.API/
COPY MyTowerRegistration.Data/MyTowerRegistration_Data.csproj MyTowerRegistration.Data/
COPY MyTowerRegistration.Tests/MyTowerRegistration_Tests.csproj MyTowerRegistration.Tests/

RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish MyTowerRegistration.API/MyTowerRegistration_API.csproj \
    -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MyTowerRegistration.API.dll"]