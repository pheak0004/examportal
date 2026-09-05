# ExamPortal.Api — setup and security notes

## Key Vault secret names

The Key Vault configuration provider maps `--` to the configuration `:`
separator, so create these secrets:

| Secret name | Maps to |
|---|---|
| `ConnectionStrings--ExamDb` | `builder.Configuration.GetConnectionString("ExamDb")` |
| `ApplicationInsights--ConnectionString` | `Configuration["ApplicationInsights:ConnectionString"]` |

Nothing secret goes in `appsettings.json`. Locally, use user secrets:

```bash
dotnet user-secrets set "ConnectionStrings:ExamDb" "Server=(localdb)\\mssqllocaldb;Database=ExamPortal;Trusted_Connection=True;"
```

## Identity and access

Grant the App Service / Container App managed identity the **Key Vault Secrets
User** role on the vault. Use RBAC, not access policies — access policies are
legacy and cannot be scoped per-secret.

```bash
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee-object-id <managed-identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.KeyVault/vaults/<vault>
```

Better still, drop the SQL connection-string secret entirely and use a
[managed identity connection to Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview)
(`Authentication=Active Directory Default`). A password that does not exist
cannot be exfiltrated. The Key Vault path is kept here because your thesis
brief asked for it, and because it is the right pattern for third-party
credentials that have no managed-identity equivalent.

## Routing logs to Microsoft Sentinel

Application Insights does not feed Sentinel directly. The chain is:

```
App  →  Application Insights (workspace-based)  →  Log Analytics workspace  →  Sentinel
```

1. Create the App Insights resource as **workspace-based** and point it at your
   Log Analytics workspace. App telemetry then lands in the `AppTraces`,
   `AppRequests`, `AppExceptions` and `AppDependencies` tables of that
   workspace.
2. Enable Sentinel on the same workspace.
3. Write analytics rules against those tables. The `EventId` values in
   `ExamService` are emitted as structured properties, so rules can be precise
   rather than grepping message text:

```kusto
AppTraces
| where Properties.EventId in (4001, 4002, 4003)
| summarize Attempts = count() by tostring(Properties.ExamSessionId), UserId = tostring(Properties.AuthenticatedUserId), bin(TimeGenerated, 5m)
| where Attempts > 10
```

Adaptive sampling is disabled in `Program.cs` — sampling silently discards
telemetry, which is fine for performance metrics and unacceptable for an audit
trail.

## Migrations

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

## What still needs doing before this is truly production-ready

- A seeder or admin API for the question bank, with authoring-time validation
  that every question has exactly four options and exactly one correct one.
- An exception-handling middleware mapping `KeyNotFoundException` → 404,
  `InvalidOperationException` → 409, `UnauthorizedAccessException` → 403.
- A background job to expire abandoned sessions (`Status = Expired`).
- Integration tests against a real SQL Server (Testcontainers). The `NEWID()`
  sampling cannot be tested against the EF in-memory provider.
