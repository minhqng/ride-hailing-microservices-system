FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# Bootstrap runs as non-root 'app' user provided by official .NET images.
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "src/Services/Driver/RideHailing.DriverService/RideHailing.DriverService.csproj"
WORKDIR "/src/src/Services/Driver/RideHailing.DriverService"
RUN dotnet build "RideHailing.DriverService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RideHailing.DriverService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RideHailing.DriverService.dll"]
