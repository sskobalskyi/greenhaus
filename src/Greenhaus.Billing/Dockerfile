# Build context is the repository root, because the service shares the domain rules.
#   docker build -f src/Greenhaus.Billing/Dockerfile -t greenhaus-billing .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Greenhaus.Domain/Greenhaus.Domain.csproj src/Greenhaus.Domain/
COPY src/Greenhaus.Billing/Greenhaus.Billing.csproj src/Greenhaus.Billing/
RUN dotnet restore src/Greenhaus.Billing/Greenhaus.Billing.csproj
COPY src src
RUN dotnet publish src/Greenhaus.Billing/Greenhaus.Billing.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Greenhaus.Billing.dll"]