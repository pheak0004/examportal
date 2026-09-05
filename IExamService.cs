using ExamPortal.Api.Dtos;

namespace ExamPortal.Api.Services;

public interface IExamService
{
    Task<ExamPaperDto> GetOrCreateExamPaperAsync(Guid examSessionId, Guid studentId, CancellationToken cancellationToken = default);

    Task RecordAnswerAsync(Guid examSessionId, Guid studentId, AnswerSubmissionDto submission, CancellationToken cancellationToken = default);

    Task<ExamResultDto> SubmitExamAsync(Guid examSessionId, Guid studentId, CancellationToken cancellationToken = default);
}
