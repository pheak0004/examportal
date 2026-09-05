using System.ComponentModel.DataAnnotations;

namespace ExamPortal.Api.Models;

public class Student
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Entra ID object id (the 'oid' claim). This is the join key between the
    /// identity provider and our data — never trust a client-supplied student id.
    /// </summary>
    [MaxLength(64)]
    public required string ExternalObjectId { get; set; }

    [MaxLength(32)]
    public required string MatriculationNumber { get; set; }

    [MaxLength(128)]
    public required string FullName { get; set; }

    [MaxLength(256)]
    public required string Email { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ExamSession> ExamSessions { get; set; } = [];
}

public enum QuestionCategory
{
    Technology = 1,
    Mathematics = 2,
    General = 3
}

public enum DifficultyLevel
{
    Easy = 1,
    Medium = 2,
    Hard = 3
}

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(2000)]
    public required string Text { get; set; }

    public QuestionCategory Category { get; set; } = QuestionCategory.Technology;
    public DifficultyLevel Difficulty { get; set; } = DifficultyLevel.Medium;

    /// <summary>Shown only after the exam is graded.</summary>
    [MaxLength(2000)]
    public string? Explanation { get; set; }

    /// <summary>Soft-delete / retire flag; retired questions are never drawn.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Exactly four options, exactly one of which is correct. SQL Server check
    /// constraints cannot count child rows, so this invariant is enforced in two
    /// places: at authoring time by validation, and at draw time by the
    /// HAVING clause in <c>ExamService</c> — a malformed question is simply
    /// never eligible for an exam.
    /// </summary>
    public ICollection<AnswerOption> Options { get; set; } = [];
}

public class AnswerOption
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    [MaxLength(1000)]
    public required string Text { get; set; }

    /// <summary>
    /// Never serialized to the client. See <c>Dtos</c> — the API projects into
    /// DTOs explicitly so this column cannot leak through a navigation property.
    /// </summary>
    public bool IsCorrect { get; set; }

    /// <summary>Canonical authoring order (A–D). Presentation order is per-session.</summary>
    public int CanonicalOrder { get; set; }
}

public enum ExamSessionStatus
{
    Created = 0,
    InProgress = 1,
    Submitted = 2,
    Expired = 3,
    Abandoned = 4
}

public class ExamSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StudentId { get; set; }
    public Student Student { get; set; } = null!;

    public ExamSessionStatus Status { get; set; } = ExamSessionStatus.Created;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }

    /// <summary>Server-authoritative deadline. The client clock is never trusted.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public int DurationMinutes { get; set; } = 120;
    public int QuestionCount { get; set; }

    public int? Score { get; set; }

    /// <summary>Recorded for audit trails / Sentinel correlation.</summary>
    [MaxLength(45)]
    public string? OriginIpAddress { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    /// <summary>Optimistic concurrency guard against double submission.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<ExamSessionQuestion> Questions { get; set; } = [];
}

/// <summary>
/// The frozen, per-session view of one question: which position it occupies and
/// the exact permutation of its four options as shown to this student.
/// Persisting the permutation is what makes the shuffle auditable and makes
/// grading possible after the fact.
/// </summary>
public class ExamSessionQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExamSessionId { get; set; }
    public ExamSession ExamSession { get; set; } = null!;

    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    /// <summary>1-based position within this student's paper.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Permutation of <see cref="AnswerOption.Id"/> values, in display order.</summary>
    public List<Guid> OptionOrder { get; set; } = [];

    public Guid? SelectedOptionId { get; set; }
    public DateTimeOffset? AnsweredAtUtc { get; set; }
    public bool? IsCorrect { get; set; }
}
