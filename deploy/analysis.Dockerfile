# Analiz servisi imajı: servis + ffmpeg (kod çözme) + YOLOX ONNX modeli.
#   docker build -f deploy/analysis.Dockerfile .
# Model imaja derleme sırasında indirilir ve SHA-256 ile doğrulanır; çalışırken ağ erişimi gerekmez.
ARG MODEL=yolox_tiny

FROM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Gozcu.Contracts/Gozcu.Contracts.csproj src/Gozcu.Contracts/
COPY src/Gozcu.Media/Gozcu.Media.csproj src/Gozcu.Media/
COPY src/Gozcu.Infrastructure/Gozcu.Infrastructure.csproj src/Gozcu.Infrastructure/
COPY src/Gozcu.Vision/Gozcu.Vision.csproj src/Gozcu.Vision/
COPY src/Gozcu.AnalysisService/Gozcu.AnalysisService.csproj src/Gozcu.AnalysisService/
RUN dotnet restore src/Gozcu.AnalysisService/Gozcu.AnalysisService.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/Gozcu.AnalysisService/Gozcu.AnalysisService.csproj -c Release -r linux-x64 --self-contained false \
    -o /app --no-restore

FROM mcr.microsoft.com/dotnet/sdk:8.0-noble AS model
ARG MODEL
WORKDIR /models
COPY scripts/download-model.sh /download-model.sh
RUN MODELS_DIR=/models bash /download-model.sh "$MODEL"

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
ARG MODEL
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=model /models/${MODEL}.onnx /app/models/${MODEL}.onnx
ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC \
    Analysis__ModelPath=/app/models/${MODEL}.onnx
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Gozcu.AnalysisService.dll"]
