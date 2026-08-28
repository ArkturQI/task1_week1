FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Cli/ ./
RUN dotnet publish Cli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# non-root user (useradd is available in Debian slim, adduser is not)
RUN useradd -m -s /bin/bash appuser && chown -R appuser /app
USER appuser
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Cli.dll"]