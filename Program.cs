using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using ExamPortal.Api.Data;
using ExamPortal.Api.Services;
using ExamPortal.Api.Telemetry;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Identity.Web;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. Configuration: Azure Key Vault via managed identity.
//    Nothing secret is ever stored in appsettings.json or in source control.
//    Locally, DefaultAzureCredential falls back to your `az login` / Visual
//    Studio identity, so the same code path works on a dev machine.
// ---------------------------------------------------------------------------
var keyVaultUri = builder.Configuration["KeyVault:Uri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        // Pin the user-assigned managed identity in Azure. Leave null to use the
        // system-assigned identity.
        ManagedIdentityClientId = builder.Configuration["KeyVault:ManagedIdentityClientId"],

        // Interactive/device-code flows have no place in a hosted API.
        ExcludeInteractiveBrowserCredential = true,
        ExcludeVisualStudioCodeCredential = !builder.Environment.IsDevelopment(),
        ExcludeAzureCliCredential = !builder.Environment.IsDevelopment()
    });

    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        credential,
        new AzureKeyVaultConfigurationOptions
        {
            // Rotated secrets are picked up without a redeploy.
            ReloadInterval = TimeSpan.FromHours(1)
        });
}
else if (!builder.Environment.IsDevelopment())
{
    // Fail fast rather than silently starting with no secrets.
    throw new InvalidOperationException("KeyVault:Uri is not configured.");
}

// ---------------------------------------------------------------------------
// 2. Telemetry: Application Insights.
//    Sentinel does not ingest from App Insights directly — App Insights is
//    workspace-based, so its tables (AppTraces, AppRequests, AppDependencies,
//    AppExceptions) land in a Log Analytics workspace, and Sentinel is enabled
//    on that workspace. See the notes in README-SECURITY.md.
// ---------------------------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // Secret name in Key Vault: ApplicationInsights--ConnectionString
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    options.EnableAdaptiveSampling = false; // never drop audit/security traces
});

// Stamps every telemetry item with the authenticated student id + exam session id
// so a Sentinel analytics rule can correlate suspicious activity to one sitting.
builder.Services.AddSingleton<ITelemetryInitializer, ExamContextTelemetryInitializer>();
builder.Services.AddHttpContextAccessor();

builder.Logging.AddApplicationInsights();

// ---------------------------------------------------------------------------
// 3. Data access
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("ExamDb")
    ?? throw new InvalidOperationException("Connection string 'ExamDb' was not resolved from Key Vault.");

builder.Services.AddDbContext<ExamDbContext>(options =>
{
    options.UseSqlServer(connectionString, sql =>
    {
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        sql.CommandTimeout(30);
    });

    // Detailed errors can echo parameter values into logs; dev only.
    options.EnableDetailedErrors(builder.Environment.IsDevelopment());
    options.EnableSensitiveDataLogging(false);
});

// ---------------------------------------------------------------------------
// 4. AuthN / AuthZ — Entra ID bearer tokens, no home-grown token handling.
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Student", policy => policy.RequireRole("Exam.Student"));
    options.AddPolicy("Invigilator", policy => policy.RequireRole("Exam.Invigilator"));

    // Everything requires an authenticated caller unless explicitly opted out.
    options.FallbackPolicy = options.DefaultPolicy;
});

// ---------------------------------------------------------------------------
// 5. Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System); // injected instead of DateTime.UtcNow, so exam timing is testable
builder.Services.AddScoped<IExamService, ExamService>();

// ---------------------------------------------------------------------------
// 6. Cross-cutting hardening
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("per-user", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("ExamClient", policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .WithMethods("GET", "POST")
        .AllowAnyHeader());
});

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ExamDbContext>("database", HealthStatus.Unhealthy);

var app = builder.Build();

// ---------------------------------------------------------------------------
// 7. Pipeline
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler();          // returns ProblemDetails, never a stack trace
    app.UseHsts();
}

app.UseStatusCodePages();
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseCors("ExamClient");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireRateLimiting("per-user");
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
