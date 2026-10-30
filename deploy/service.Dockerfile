# Akış ve kayıt servisleri için ortak imaj. Kullanım:
#   docker build -f deploy/service.Dockerfile --build-arg SERVICE=Gozcu.RecordingService .
ARG SERVICE

FROM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build
ARG SERVICE
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Gozcu.Contracts/Gozcu.Contracts.csproj src/Gozcu.Contracts/
COPY src/Gozcu.Media/Gozcu.Media.csproj src/Gozcu.Media/
COPY src/Gozcu.Infrastructure/Gozcu.Infrastructure.csproj src/Gozcu.Infrastructure/
COPY src/${SERVICE}/${SERVICE}.csproj src/${SERVICE}/
RUN dotnet restore src/${SERVICE}/${SERVICE}.csproj
COPY src/ src/
RUN dotnet publish src/${SERVICE}/${SERVICE}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
ARG SERVICE
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC \
    SERVICE_DLL=${SERVICE}.dll
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
