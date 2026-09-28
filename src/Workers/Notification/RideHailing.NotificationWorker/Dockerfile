FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# No EXPOSE: worker serves no HTTP at bootstrap (compose publishes no host port).
# Bootstrap runs as non-root 'app' user provided by official .NET images.
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "src/Workers/Notification/RideHailing.NotificationWorker/RideHailing.NotificationWorker.csproj"
WORKDIR "/src/src/Workers/Notification/RideHailing.NotificationWorker"
RUN dotnet build "RideHailing.NotificationWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RideHailing.NotificationWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RideHailing.NotificationWorker.dll"]
