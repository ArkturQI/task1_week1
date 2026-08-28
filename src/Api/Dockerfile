FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Api/ ./
RUN dotnet publish Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# non-root user (useradd is available in Debian slim)
RUN useradd -m -s /bin/bash appuser && chown -R appuser /app
USER appuser
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Api.dll"]