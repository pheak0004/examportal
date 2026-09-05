using ExamPortal.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace ExamPortal.Api.Data;

public class ExamDbContext(DbContextOptions<ExamDbContext> options) : DbContext(options)
{
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<AnswerOption> AnswerOptions => Set<AnswerOption>();
    public DbSet<ExamSession> ExamSessions => Set<ExamSession>();
    public DbSet<ExamSessionQuestion> ExamSessionQuestions => Set<ExamSessionQuestion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Student>(entity =>
        {
            entity.HasIndex(s => s.ExternalObjectId).IsUnique();
            entity.HasIndex(s => s.MatriculationNumber).IsUnique();
        });

        modelBuilder.Entity<Question>(entity =>
        {
            entity.Property(q => q.Category).HasConversion<int>();
            entity.Property(q => q.Difficulty).HasConversion<int>();

            // Covering index for the sampling query in ExamService.
            entity.HasIndex(q => new { q.Category, q.IsActive });

            entity.HasMany(q => q.Options)
                  .WithOne(o => o.Question)
                  .HasForeignKey(o => o.QuestionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AnswerOption>(entity =>
        {
            entity.HasIndex(o => new { o.QuestionId, o.CanonicalOrder }).IsUnique();
        });

        modelBuilder.Entity<ExamSession>(entity =>
        {
            entity.Property(s => s.Status).HasConversion<int>();
            entity.HasIndex(s => new { s.StudentId, s.Status });

            entity.HasMany(s => s.Questions)
                  .WithOne(q => q.ExamSession)
                  .HasForeignKey(q => q.ExamSessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExamSessionQuestion>(entity =>
        {
            // A question may appear at most once per paper, at exactly one position.
            entity.HasIndex(q => new { q.ExamSessionId, q.QuestionId }).IsUnique();
            entity.HasIndex(q => new { q.ExamSessionId, q.DisplayOrder }).IsUnique();

            entity.HasOne(q => q.Question)
                  .WithMany()
                  .HasForeignKey(q => q.QuestionId)
                  .OnDelete(DeleteBehavior.Restrict); // never delete a question used in a sitting

            // Store the permutation as JSON. A ValueComparer is required so EF
            // change-tracks the list by value rather than by reference.
            var comparer = new ValueComparer<List<Guid>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                v => v.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.GetHashCode())),
                v => v.ToList());

            entity.Property(q => q.OptionOrder)
                  .HasConversion(
                      v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                      v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new List<Guid>())
                  .Metadata.SetValueComparer(comparer);

            entity.Property(q => q.OptionOrder).HasMaxLength(400);
        });
    }
}
