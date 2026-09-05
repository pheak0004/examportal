using ExamPortal.Api.Data;
using ExamPortal.Api.Dtos;
using ExamPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ExamPortal.Api.Controllers;

[ApiController]
[Route("api/exam-sessions/{examSessionId:guid}")]
[Authorize(Policy = "Student")]
public class ExamController(IExamService examService, ExamDbContext db) : ControllerBase
{
    private readonly IExamService _examService = examService;
    private readonly ExamDbContext _db = db;

    [HttpGet("paper")]
    [ProducesResponseType<ExamPaperDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaper(Guid examSessionId, CancellationToken cancellationToken)
    {
        var studentId = await ResolveStudentIdAsync(cancellationToken);
        var paper = await _examService.GetOrCreateExamPaperAsync(examSessionId, studentId, cancellationToken);
        return Ok(paper);
    }

    [HttpPost("answers")]
    public async Task<IActionResult> SubmitAnswer(
        Guid examSessionId, [FromBody] AnswerSubmissionDto submission, CancellationToken cancellationToken)
    {
        var studentId = await ResolveStudentIdAsync(cancellationToken);
        await _examService.RecordAnswerAsync(examSessionId, studentId, submission, cancellationToken);
        return NoContent();
    }

    [HttpPost("submit")]
    [ProducesResponseType<ExamResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(Guid examSessionId, CancellationToken cancellationToken)
    {
        var studentId = await ResolveStudentIdAsync(cancellationToken);
        var result = await _examService.SubmitExamAsync(examSessionId, studentId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// The student id comes from the validated token, never from the route or
    /// body. This is the single most important line in the controller.
    /// </summary>
    private async Task<Guid> ResolveStudentIdAsync(CancellationToken cancellationToken)
    {
        var objectId = User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                       ?? User.FindFirstValue("oid")
                       ?? throw new UnauthorizedAccessException("Token is missing the object identifier claim.");

        var studentId = await _db.Students
            .Where(s => s.ExternalObjectId == objectId && s.IsActive)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (studentId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("No active student record is linked to this identity.");
        }

        return studentId;
    }
}
