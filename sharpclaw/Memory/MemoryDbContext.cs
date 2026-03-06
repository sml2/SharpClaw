using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace sharpclaw.Memory;

/// <summary>
/// EF Core DbContext，映射 memories SQLite 表。
/// 使用 EnsureCreated 替代 Migrations，与现有 schema 完全兼容。
/// </summary>
internal sealed class MemoryDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();

    public MemoryDbContext(DbContextOptions<MemoryDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var keywordsConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>());

        // DateTimeOffset 以 ISO 8601 ("O") 格式存储，与原有 schema 保持兼容
        var dateTimeConverter = new ValueConverter<DateTimeOffset, string>(
            v => v.ToString("O"),
            v => DateTimeOffset.Parse(v));

        modelBuilder.Entity<MemoryRecord>(e =>
        {
            e.ToTable("memories");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.Category).HasColumnName("category").IsRequired();
            e.Property(m => m.Importance).HasColumnName("importance");
            e.Property(m => m.Content).HasColumnName("content").IsRequired();
            e.Property(m => m.Keywords)
                .HasColumnName("keywords")
                .IsRequired()
                .HasConversion(keywordsConverter);
            e.Property(m => m.CreatedAt)
                .HasColumnName("created_at")
                .HasConversion(dateTimeConverter);
            e.Property(m => m.Embedding)
                .HasColumnName("embedding")
                .IsRequired();
        });
    }

    /// <summary>
    /// 根据数据库文件路径构建 DbContextOptions。
    /// </summary>
    public static DbContextOptions<MemoryDbContext> BuildOptions(string dbPath)
    {
        return new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }
}
