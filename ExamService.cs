using ExamPortal.Api.Data;
using ExamPortal.Api.Dtos;
using ExamPortal.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ExamPortal.Api.Services;

/// <summary>
/// Structured event ids so Sentinel analytics rules can query
/// <c>AppTraces</c> without pattern-matching on free-text messages.
/// </summary>
internal static class ExamEventIds
{
    public static readonly EventId PaperDrawn = new(1001, "ExamPaperDrawn");
    public static readonly EventId PaperReplayed = new(1002, "ExamPaperReplayed");
    public static readonly EventId AnswerRecorded = new(1003, "ExamAnswerRecorded");
    public static readonly EventId ExamSubmitted = new(1004, "ExamSubmitted");
    public static readonly EventId OwnershipViolation = new(4001, "ExamSessionOwnershipViolation");
    public static readonly EventId InvalidOption = new(4002, "InvalidOptionSubmitted");
    public static readonly EventId ExpiredAccess = new(4003, "ExpiredSessionAccess");
    public static readonly EventId InsufficientBank = new(5001, "InsufficientQuestionBank");
}

public class ExamService(
    ExamDbContext db,
    ILogger<ExamService> logger,
    TimeProvider timeProvider) : IExamService
{
    private const int RequiredQuestionCount = 100;
    private const int OptionsPerQuestion = 4;
    private const QuestionCategory TargetCategory = QuestionCategory.Technology;

    private readonly ExamDbContext _db = db;
    private readonly ILogger<ExamService> _logger = logger;
    private readonly TimeProvider _clock = timeProvider;

    public async Task<ExamPaperDto> GetOrCreateExamPaperAsync(
        Guid examSessionId,
        Guid studentId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadOwnedSessionAsync(examSessionId, studentId, cancellationToken);

        // ---- Idempotency -----------------------------------------------------
        // If the paper already exists, replay it verbatim. Without this, a
        // refresh would hand the student a brand-new set of questions — both a
        // grading bug and an obvious way to farm for an easier paper.
        var alreadyDrawn = await _db.ExamSessionQuestions
            .AnyAsync(q => q.ExamSessionId == examSessionId, cancellationToken);

        if (alreadyDrawn)
        {
            _logger.LogInformation(ExamEventIds.PaperReplayed,
                "Replaying existing paper for session {ExamSessionId}", examSessionId);
            return await BuildPaperDtoAsync(session, cancellationToken);
        }

        if (session.Status is not (ExamSessionStatus.Created or ExamSessionStatus.InProgress))
        {
            throw new InvalidOperationException($"Session {examSessionId} is {session.Status} and cannot be started.");
        }

        // ---- 1. Draw 100 random technology questions, database-side ----------
        var drawnIds = await DrawRandomQuestionIdsAsync(RequiredQuestionCount, cancellationToken);

        if (drawnIds.Count < RequiredQuestionCount)
        {
            _logger.LogError(ExamEventIds.InsufficientBank,
                "Question bank returned {Actual} of {Required} eligible {Category} questions.",
                drawnIds.Count, RequiredQuestionCount, TargetCategory);

            throw new InvalidOperationException(
                $"Only {drawnIds.Count} eligible {TargetCategory} questions are available; {RequiredQuestionCount} are required.");
        }

        // ---- 2. Load the drawn questions with their options ------------------
        var questions = await _db.Questions
            .AsNoTracking()
            .Include(q => q.Options)
            .Where(q => drawnIds.Contains(q.Id))
            .ToListAsync(cancellationToken);

        // ---- 3. Shuffle question order and each question's option order ------
        // NEWID() already randomised the draw, but the Contains() re-read returns
        // rows in whatever order the index yields, so we shuffle explicitly with
        // a cryptographic RNG. System.Random is seeded from predictable state and
        // has no place in anti-cheat logic.
        ShuffleInPlace(questions);

        var now = _clock.GetUtcNow();
        var sessionQuestions = new List<ExamSessionQuestion>(questions.Count);

        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];

            var optionIds = question.Options
                .OrderBy(o => o.CanonicalOrder)
                .Select(o => o.Id)
                .ToList();

            if (optionIds.Count != OptionsPerQuestion)
            {
                // Defensive: the SQL HAVING clause should have excluded this already.
                throw new InvalidOperationException(
                    $"Question {question.Id} has {optionIds.Count} options; exactly {OptionsPerQuestion} are required.");
            }

            ShuffleInPlace(optionIds);

            sessionQuestions.Add(new ExamSessionQuestion
            {
                ExamSessionId = session.Id,
                QuestionId = question.Id,
                DisplayOrder = i + 1,
                OptionOrder = optionIds
            });
        }

        // ---- 4. Freeze the arrangement --------------------------------------
        session.Status = ExamSessionStatus.InProgress;
        session.StartedAtUtc ??= now;
        session.ExpiresAtUtc ??= session.StartedAtUtc.Value.AddMinutes(session.DurationMinutes);
        session.QuestionCount = sessionQuestions.Count;

        await _db.ExamSessionQuestions.AddRangeAsync(sessionQuestions, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent requests raced to start the same session. The unique
            // index on (ExamSessionId, DisplayOrder) means exactly one won —
            // discard our losing draw and serve the winner's paper.
            _db.ChangeTracker.Clear();
            var persisted = await LoadOwnedSessionAsync(examSessionId, studentId, cancellationToken);
            return await BuildPaperDtoAsync(persisted, cancellationToken);
        }

        _logger.LogInformation(ExamEventIds.PaperDrawn,
            "Drew {QuestionCount} {Category} questions for session {ExamSessionId} (student {StudentId})",
            sessionQuestions.Count, TargetCategory, session.Id, session.StudentId);

        return await BuildPaperDtoAsync(session, cancellationToken);
    }

    /// <summary>
    /// Random sampling is done in the database with <c>ORDER BY NEWID()</c>.
    /// Note the trap: <c>EF.Functions.Random()</c> maps to T-SQL <c>RAND()</c>,
    /// which is evaluated once per query, not once per row — ordering by it does
    /// nothing at all. <c>NEWID()</c> is re-evaluated per row and is the correct
    /// primitive here. (<c>Guid.NewGuid()</c> in a LINQ OrderBy simply won't
    /// translate and throws at runtime.)
    /// </summary>
    private async Task<List<Guid>> DrawRandomQuestionIdsAsync(int count, CancellationToken cancellationToken)
    {
        var category = (int)TargetCategory;

        // FromSqlInterpolated parameterises every hole — this is not string
        // concatenation and is not injectable.
        return await _db.Questions
            .FromSqlInterpolated($"""
                SELECT TOP({count}) q.*
                FROM dbo.Questions AS q
                WHERE q.IsActive = 1
                  AND q.Category = {category}
                  AND (SELECT COUNT(*) FROM dbo.AnswerOptions AS o WHERE o.QuestionId = q.Id) = {OptionsPerQuestion}
                  AND (SELECT COUNT(*) FROM dbo.AnswerOptions AS o WHERE o.QuestionId = q.Id AND o.IsCorrect = 1) = 1
                ORDER BY NEWID()
                """)
            .AsNoTracking()
            .Select(q => q.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task RecordAnswerAsync(
        Guid examSessionId,
        Guid studentId,
        AnswerSubmissionDto submission,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadOwnedSessionAsync(examSessionId, studentId, cancellationToken);
        EnsureWritable(session);

        var sessionQuestion = await _db.ExamSessionQuestions
            .FirstOrDefaultAsync(
                q => q.ExamSessionId == examSessionId && q.QuestionId == submission.QuestionId,
                cancellationToken)
            ?? throw new KeyNotFoundException($"Question {submission.QuestionId} is not part of session {examSessionId}.");

        // The selected option must belong to *this* question in *this* session.
        // Without this check a client could post an option id harvested elsewhere.
        if (!sessionQuestion.OptionOrder.Contains(submission.SelectedOptionId))
        {
            _logger.LogWarning(ExamEventIds.InvalidOption,
                "Option {OptionId} is not valid for question {QuestionId} in session {ExamSessionId}",
                submission.SelectedOptionId, submission.QuestionId, examSessionId);

            throw new InvalidOperationException("The selected option does not belong to this question.");
        }

        sessionQuestion.SelectedOptionId = submission.SelectedOptionId;
        sessionQuestion.AnsweredAtUtc = _clock.GetUtcNow();

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(ExamEventIds.AnswerRecorded,
            "Answer recorded for question {QuestionId} in session {ExamSessionId}",
            submission.QuestionId, examSessionId);
    }

    public async Task<ExamResultDto> SubmitExamAsync(
        Guid examSessionId,
        Guid studentId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadOwnedSessionAsync(examSessionId, studentId, cancellationToken);

        if (session.Status == ExamSessionStatus.Submitted)
        {
            return new ExamResultDto(session.Id, session.Score ?? 0, session.QuestionCount, session.SubmittedAtUtc!.Value);
        }

        var answers = await _db.ExamSessionQuestions
            .Where(q => q.ExamSessionId == examSessionId)
            .ToListAsync(cancellationToken);

        var selectedIds = answers
            .Where(a => a.SelectedOptionId.HasValue)
            .Select(a => a.SelectedOptionId!.Value)
            .ToList();

        var correctIds = (await _db.AnswerOptions
            .AsNoTracking()
            .Where(o => selectedIds.Contains(o.Id) && o.IsCorrect)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var score = 0;
        foreach (var answer in answers)
        {
            answer.IsCorrect = answer.SelectedOptionId.HasValue && correctIds.Contains(answer.SelectedOptionId.Value);
            if (answer.IsCorrect == true) score++;
        }

        session.Score = score;
        session.Status = ExamSessionStatus.Submitted;
        session.SubmittedAtUtc = _clock.GetUtcNow();

        // RowVersion turns a double-submit race into a DbUpdateConcurrencyException
        // instead of a silently overwritten grade.
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(ExamEventIds.ExamSubmitted,
            "Session {ExamSessionId} submitted with score {Score}/{QuestionCount}",
            session.Id, score, session.QuestionCount);

        return new ExamResultDto(session.Id, score, session.QuestionCount, session.SubmittedAtUtc.Value);
    }

    // ------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------

    /// <summary>
    /// Loads a session and asserts the caller owns it. Authorization is enforced
    /// here, at the data layer, not only in the controller — every path into the
    /// service goes through this method.
    /// </summary>
    private async Task<ExamSession> LoadOwnedSessionAsync(
        Guid examSessionId, Guid studentId, CancellationToken cancellationToken)
    {
        var session = await _db.ExamSessions
            .FirstOrDefaultAsync(s => s.Id == examSessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Exam session {examSessionId} was not found.");

        if (session.StudentId != studentId)
        {
            _logger.LogWarning(ExamEventIds.OwnershipViolation,
                "Student {StudentId} attempted to access session {ExamSessionId} owned by another student",
                studentId, examSessionId);

            // Same exception as "not found" — do not confirm the session exists.
            throw new KeyNotFoundException($"Exam session {examSessionId} was not found.");
        }

        return session;
    }

    private void EnsureWritable(ExamSession session)
    {
        if (session.Status != ExamSessionStatus.InProgress)
        {
            throw new InvalidOperationException($"Session {session.Id} is {session.Status} and no longer accepts answers.");
        }

        if (session.ExpiresAtUtc is { } expiry && _clock.GetUtcNow() > expiry)
        {
            _logger.LogWarning(ExamEventIds.ExpiredAccess,
                "Write attempted on expired session {ExamSessionId} (expired {ExpiresAtUtc})",
                session.Id, expiry);

            throw new InvalidOperationException($"Session {session.Id} expired at {expiry:O}.");
        }
    }

    /// <summary>
    /// Reads the frozen arrangement back and projects it into DTOs. The
    /// projection is explicit — <c>IsCorrect</c> is never selected, so the answer
    /// key cannot ride along on a navigation property.
    /// </summary>
    private async Task<ExamPaperDto> BuildPaperDtoAsync(ExamSession session, CancellationToken cancellationToken)
    {
        var rows = await _db.ExamSessionQuestions
            .AsNoTracking()
            .Where(q => q.ExamSessionId == session.Id)
            .OrderBy(q => q.DisplayOrder)
            .Select(q => new
            {
                q.QuestionId,
                q.DisplayOrder,
                q.OptionOrder,
                q.SelectedOptionId,
                QuestionText = q.Question.Text,
                Options = q.Question.Options.Select(o => new { o.Id, o.Text }).ToList()
            })
            .ToListAsync(cancellationToken);

        var questions = new List<ExamQuestionDto>(rows.Count);

        foreach (var row in rows)
        {
            var textById = row.Options.ToDictionary(o => o.Id, o => o.Text);

            var options = row.OptionOrder
                .Select((optionId, index) => new ExamOptionDto(optionId, index + 1, textById[optionId]))
                .ToList();

            questions.Add(new ExamQuestionDto(
                row.QuestionId,
                row.DisplayOrder,
                row.QuestionText,
                options,
                row.SelectedOptionId));
        }

        return new ExamPaperDto(
            session.Id,
            session.StartedAtUtc ?? session.CreatedAtUtc,
            session.ExpiresAtUtc ?? (session.StartedAtUtc ?? session.CreatedAtUtc).AddMinutes(session.DurationMinutes),
            questions.Count,
            questions);
    }

    /// <summary>
    /// Fisher–Yates using <see cref="RandomNumberGenerator"/>. Unbiased, and the
    /// sequence is not reproducible from a guessable seed.
    /// </summary>
    private static void ShuffleInPlace<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
