using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System.Security.Claims;

namespace ExamPortal.Api.Telemetry;

/// <summary>
/// Attaches the authenticated principal and the exam session id to every
/// telemetry item, so a Sentinel rule can pivot from one suspicious signal to
/// the whole sitting.
///
/// Note what is deliberately absent: no name, no email, no matriculation number.
/// The stable Entra object id is a pseudonymous key that an invigilator can
/// resolve when there is cause to; the telemetry store itself stays free of
/// directly identifying student data.
/// </summary>
public class ExamContextTelemetryInitializer(IHttpContextAccessor httpContextAccessor) : ITelemetryInitializer
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public void Initialize(ITelemetry telemetry)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return;

        var objectId = httpContext.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                       ?? httpContext.User.FindFirstValue("oid");

        if (!string.IsNullOrEmpty(objectId))
        {
            telemetry.Context.User.AuthenticatedUserId = objectId;
        }

        if (telemetry is ISupportProperties properties)
        {
            if (httpContext.Request.RouteValues.TryGetValue("examSessionId", out var sessionId) && sessionId is not null)
            {
                properties.Properties["ExamSessionId"] = sessionId.ToString()!;
            }

            properties.Properties["ClientIp"] = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }
}
