# C4 Container Diagram

## System: ModuleDev Week-1 Action Gateway

### Containers

1. **Client** (External) - HTTP client
2. **Gateway** (C# ASP.NET Core, :8080) - route whitelist, proxy to Api (JWT is validated by Api, see ADR-001)
3. **Api** (C# ASP.NET Core, internal :8080) - Action runtime, calls api.invoke in PostgreSQL
4. **Cli** (C# Console) - Migration apply, action publish/list/activate/disable
5. **PostgreSQL** (:5432) - Authoritative state storage

### Interactions

    Client -> Gateway: POST /api/{module}/{action}
    Gateway -> Api: HTTP proxy (Compose DNS)
    Api -> PostgreSQL: SELECT api.invoke(...)
    PostgreSQL -> Api: JSON result
    Api -> Gateway: HTTP response
    Gateway -> Client: HTTP response
    Cli -> PostgreSQL: INSERT INTO autocheck.action_definitions