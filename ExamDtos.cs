namespace ExamPortal.Api.Dtos;

/// <summary>
/// The wire format for a question as the student sees it. There is deliberately
/// no <c>IsCorrect</c> member anywhere in this file: the answer key cannot leak
/// through the API surface even if someone later adds a careless projection.
/// </summary>
public sealed record ExamQuestionDto(
    Guid QuestionId,
    int DisplayOrder,
    string Text,
    IReadOnlyList<ExamOptionDto> Options,
    Guid? SelectedOptionId);

public sealed record ExamOptionDto(
    Guid OptionId,
    int DisplayOrder,
    string Text);

public sealed record ExamPaperDto(
    Guid ExamSessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int QuestionCount,
    IReadOnlyList<ExamQuestionDto> Questions);

public sealed record AnswerSubmissionDto(Guid QuestionId, Guid SelectedOptionId);

public sealed record ExamResultDto(
    Guid ExamSessionId,
    int Score,
    int QuestionCount,
    DateTimeOffset SubmittedAtUtc);
