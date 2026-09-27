FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# Bootstrap runs as non-root 'app' user provided by official .NET images.
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Services/Payment/RideHailing.PaymentService/RideHailing.PaymentService.csproj", "src/Services/Payment/RideHailing.PaymentService/"]
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["global.json", "./"]
RUN dotnet restore "src/Services/Payment/RideHailing.PaymentService/RideHailing.PaymentService.csproj"
COPY . .
WORKDIR "/src/src/Services/Payment/RideHailing.PaymentService"
RUN dotnet build "RideHailing.PaymentService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RideHailing.PaymentService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RideHailing.PaymentService.dll"]
