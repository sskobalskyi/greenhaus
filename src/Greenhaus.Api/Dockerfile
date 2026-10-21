# Build context is the repository root, because the service serves the species catalog.
#   docker build -f src/Greenhaus.Api/Dockerfile -t greenhaus-api .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Greenhaus.Domain/Greenhaus.Domain.csproj src/Greenhaus.Domain/
COPY src/Greenhaus.Api/Greenhaus.Api.csproj src/Greenhaus.Api/
RUN dotnet restore src/Greenhaus.Api/Greenhaus.Api.csproj
COPY src src
RUN dotnet publish src/Greenhaus.Api/Greenhaus.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
COPY data /data
ENV ASPNETCORE_URLS=http://+:8080 Species__Path=/data/species.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "Greenhaus.Api.dll"]