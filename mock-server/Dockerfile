# Build context is the repository root, because the mock serves the species catalog and the contracts.
#   docker build -f mock-server/Dockerfile -t greenhaus-mock .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY mock-server/Greenhaus.MockServer/Greenhaus.MockServer.csproj mock-server/Greenhaus.MockServer/
RUN dotnet restore mock-server/Greenhaus.MockServer/Greenhaus.MockServer.csproj
COPY mock-server/Greenhaus.MockServer mock-server/Greenhaus.MockServer
COPY data data
COPY contracts contracts
RUN dotnet publish mock-server/Greenhaus.MockServer/Greenhaus.MockServer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Greenhaus.MockServer.dll"]
