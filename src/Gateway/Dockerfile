FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Gateway/ ./
RUN dotnet publish Gateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN useradd -m -s /bin/bash appuser && chown -R appuser /app
USER appuser
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Gateway.dll"]