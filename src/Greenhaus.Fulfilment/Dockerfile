# Build context is the repository root, because the service reads the kit descriptions in data/.
#   docker build -f src/Greenhaus.Fulfilment/Dockerfile -t greenhaus-fulfilment .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Greenhaus.Fulfilment/Greenhaus.Fulfilment.csproj src/Greenhaus.Fulfilment/
RUN dotnet restore src/Greenhaus.Fulfilment/Greenhaus.Fulfilment.csproj
COPY src src
RUN dotnet publish src/Greenhaus.Fulfilment/Greenhaus.Fulfilment.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
COPY data /data
ENV ASPNETCORE_URLS=http://+:8080 Fulfilment__KitsPath=/data/kit-capabilities.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "Greenhaus.Fulfilment.dll"]