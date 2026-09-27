FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# Bootstrap runs as non-root 'app' user provided by official .NET images.
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Gateway/RideHailing.Gateway/RideHailing.Gateway.csproj", "src/Gateway/RideHailing.Gateway/"]
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["global.json", "./"]
RUN dotnet restore "src/Gateway/RideHailing.Gateway/RideHailing.Gateway.csproj"
COPY . .
WORKDIR "/src/src/Gateway/RideHailing.Gateway"
RUN dotnet build "RideHailing.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RideHailing.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RideHailing.Gateway.dll"]
