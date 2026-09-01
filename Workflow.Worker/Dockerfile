FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY ["Workflow.Worker/Workflow.Worker.csproj", "Workflow.Worker/"]

RUN dotnet restore "Workflow.Worker/Workflow.Worker.csproj"

COPY . .

WORKDIR /src/Workflow.Worker

RUN dotnet publish \
    "Workflow.Worker.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/runtime:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Workflow.Worker.dll"]