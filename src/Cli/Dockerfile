FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Cli/ ./
RUN dotnet publish Cli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# Npgsql requires libgssapi_krb5 for GSSAPI authentication support (Debian-based image)
RUN apt-get update && apt-get install -y --no-install-recommends libkrb5-3 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash appuser && chown -R appuser /app
USER appuser
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Cli.dll"]