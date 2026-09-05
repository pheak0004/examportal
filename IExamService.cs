namespace ExamPortal.Api.Services;

public interface IExamService
{
    /// <summary>
    /// Returns the paper for a session. On first call it draws 100 random
    /// technology questions, shuffles their answer options, and persists that
    /// arrangement. Every later call replays the stored arrangement, so a
    /// student cannot reroll into an easier paper by refreshing.
    /// </summary>
    Task<ExamPaperDto> GetOrCreateExamPaperAsync(
        Guid examSessionId,
        Guid studentId,
        CancellationToken cancellationToken = default);

    Task RecordAnswerAsync(
        Guid examSessionId,
        Guid studentId,
        AnswerSubmissionDto submission,
        CancellationToken cancellationToken = default);

    Task<ExamResultDto> SubmitExamAsync(
        Guid examSessionId,
        Guid studentId,
        CancellationToken cancellationToken = default);
}
EOF

dotnet build --configuration Release
