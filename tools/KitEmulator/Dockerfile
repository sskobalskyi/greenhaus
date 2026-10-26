# Build context is the repository root, because the emulator reads the kit descriptions in data/.
#   docker build -f tools/KitEmulator/Dockerfile -t greenhaus-kit-emulator .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY tools/KitEmulator/KitEmulator.csproj tools/KitEmulator/
RUN dotnet restore tools/KitEmulator/KitEmulator.csproj
COPY tools/KitEmulator tools/KitEmulator
RUN dotnet publish tools/KitEmulator/KitEmulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
COPY data /data
ENV KITS_PATH=/data/kit-capabilities.json
ENTRYPOINT ["dotnet", "Greenhaus.KitEmulator.dll"]