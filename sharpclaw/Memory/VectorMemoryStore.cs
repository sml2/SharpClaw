using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Sharc;
using Sharc.Vector;

using sharpclaw.Clients;

namespace sharpclaw.Memory;

/// <summary>
/// 基于 EF Core + SQLite 的向量记忆存储：
/// - EF Core 负责写操作（INSERT/UPDATE/DELETE）
/// - Sharc.Vector 负责向量相似度搜索（SIMD 加速）
/// - 嵌入向量以 BLOB 形式存储在 SQLite 中
/// </summary>
public class VectorMemoryStore : IMemoryStore
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly DashScopeRerankClient? _rerankClient;
    private readonly string _dbPath;
    private readonly DbContextOptions<MemoryDbContext> _dbOptions;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _dirty = true;

    public int MaxEntries { get; set; } = 200;
    public int RerankCandidateMultiplier { get; set; } = 3;

    /// <summary>
    /// 语义去重阈值：余弦距离小于此值时视为重复，合并而非新增。默认 0.15（对应相似度 0.85）。
    /// </summary>
    public float DeduplicationDistance { get; set; } = 0.15f;

    public VectorMemoryStore(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string filePath,
        DashScopeRerankClient? rerankClient = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _dbPath = filePath;
        _dbOptions = MemoryDbContext.BuildOptions(filePath);
        _rerankClient = rerankClient;

        InitializeDatabase();
        MigrateFromJson();
    }

    public async Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var embedding = await _embeddingGenerator.GenerateAsync(
                entry.Content, cancellationToken: cancellationToken);
            var vector = embedding.Vector.ToArray();

            // 语义去重：用 Sharc 搜索最相似的已有记忆
            var mostSimilar = FindMostSimilarWithinThreshold(vector);
            if (mostSimilar is not null)
            {
                // 合并：保留更高重要度的内容，合并关键词，刷新时间戳
                var existing = mostSimilar.Value.Entry;
                if (entry.Importance >= existing.Importance)
                {
                    existing.Content = entry.Content;
                    existing.Importance = entry.Importance;
                    existing.Category = entry.Category;
                }
                existing.Keywords = existing.Keywords
                    .Union(entry.Keywords, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                existing.CreatedAt = DateTimeOffset.UtcNow;

                var finalVector = entry.Importance >= mostSimilar.Value.Entry.Importance
                    ? vector : null;
                UpdateRecord(existing, finalVector);
                return;
            }

            InsertRecord(entry, vector);
            EvictIfNeeded();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var context = new MemoryDbContext(_dbOptions);
            var existing = context.Memories.Find(entry.Id);
            if (existing is null)
                return;

            float[]? newVector = null;
            if (existing.Content != entry.Content)
            {
                var embedding = await _embeddingGenerator.GenerateAsync(
                    entry.Content, cancellationToken: cancellationToken);
                newVector = embedding.Vector.ToArray();
            }

            UpdateRecord(entry, newVector);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(
        int count, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var context = new MemoryDbContext(_dbOptions);
            return await context.Memories
                .OrderByDescending(m => m.CreatedAt)
                .Take(count)
                .Select(m => m.ToEntry())
                .ToListAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query, int count, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var context = new MemoryDbContext(_dbOptions);
            var totalCount = await context.Memories.CountAsync(cancellationToken);
            if (totalCount == 0)
                return [];

            // Phase 1: 用 Sharc 向量搜索
            var queryEmbedding = await _embeddingGenerator.GenerateAsync(
                query, cancellationToken: cancellationToken);
            var queryVector = queryEmbedding.Vector.ToArray();

            var candidateCount = count * RerankCandidateMultiplier;
            var candidates = VectorSearch(queryVector, candidateCount);

            if (candidates.Count == 0)
                return [];

            var entries = LoadEntriesByRowIds(context, candidates.Select(c => c.RowId).ToList());

            // Phase 2: Rerank (optional)
            if (_rerankClient is not null && entries.Count > 0)
            {
                try
                {
                    var documents = entries.Select(e => e.Content).ToList();
                    var rerankResults = await _rerankClient.RerankAsync(
                        query, documents, count, cancellationToken);

                    return rerankResults
                        .Select(r => entries[r.Index])
                        .ToList()
                        .AsReadOnly();
                }
                catch
                {
                    // Rerank 失败，回退到向量搜索结果
                }
            }

            return entries.Take(count).ToList().AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var context = new MemoryDbContext(_dbOptions);
            await context.Memories
                .Where(m => m.Id == id)
                .ExecuteDeleteAsync(cancellationToken);
            _dirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var context = new MemoryDbContext(_dbOptions);
            return await context.Memories.CountAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private helpers ──

    private void InitializeDatabase()
    {
        using var context = new MemoryDbContext(_dbOptions);
        context.Database.EnsureCreated();
    }

    /// <summary>
    /// 从旧的 memories.json 迁移数据到 SQLite。
    /// </summary>
    private void MigrateFromJson()
    {
        var jsonPath = Path.ChangeExtension(_dbPath, ".json");
        if (!File.Exists(jsonPath))
            return;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var oldEntries = System.Text.Json.JsonSerializer.Deserialize<List<StoredMemoryEntryLegacy>>(json);
            if (oldEntries is null or [])
            {
                File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
                return;
            }

            using var context = new MemoryDbContext(_dbOptions);
            foreach (var old in oldEntries)
            {
                if (old.Vector is null || old.Entry is null)
                    continue;

                if (context.Memories.Find(old.Entry.Id) is not null)
                    continue;

                context.Memories.Add(MemoryRecord.FromEntry(old.Entry, old.Vector));
            }
            context.SaveChanges();
            _dirty = true;

            File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
        }
        catch
        {
            // 迁移失败不影响正常使用
        }
    }

    private void InsertRecord(MemoryEntry entry, float[] vector)
    {
        using var context = new MemoryDbContext(_dbOptions);
        context.Memories.Add(MemoryRecord.FromEntry(entry, vector));
        context.SaveChanges();
        _dirty = true;
    }

    private void UpdateRecord(MemoryEntry entry, float[]? newVector)
    {
        using var context = new MemoryDbContext(_dbOptions);
        var record = context.Memories.Find(entry.Id);
        if (record is null)
            return;

        record.Category = entry.Category;
        record.Importance = entry.Importance;
        record.Content = entry.Content;
        record.Keywords = entry.Keywords;
        record.CreatedAt = entry.CreatedAt;
        if (newVector is not null)
            record.Embedding = MemoryRecord.FloatArrayToBytes(newVector);

        context.SaveChanges();
        _dirty = true;
    }

    private void EvictIfNeeded()
    {
        using var context = new MemoryDbContext(_dbOptions);
        var count = context.Memories.Count();
        if (count <= MaxEntries)
            return;

        var excess = count - MaxEntries;
        var toEvict = context.Memories
            .OrderBy(m => m.Importance)
            .ThenBy(m => m.CreatedAt)
            .Take(excess)
            .Select(m => m.Id)
            .ToList();

        context.Memories
            .Where(m => toEvict.Contains(m.Id))
            .ExecuteDelete();
        _dirty = true;
    }

    /// <summary>
    /// 用 Sharc 执行向量相似度搜索。
    /// </summary>
    private IReadOnlyList<VectorMatch> VectorSearch(float[] queryVector, int k)
    {
        // 确保 WAL checkpoint 完成，Sharc 能读到最新数据
        if (_dirty)
        {
            using var context = new MemoryDbContext(_dbOptions);
            context.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
            _dirty = false;
        }

        if (!File.Exists(_dbPath) || new FileInfo(_dbPath).Length == 0)
            return [];

        try
        {
            var dbBytes = File.ReadAllBytes(_dbPath);
            using var db = SharcDatabase.OpenMemory(dbBytes);
            using var vq = db.Vector("memories", "embedding", DistanceMetric.Cosine);
            var result = vq.NearestTo(queryVector, k: k);
            return result.Matches;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 查找距离阈值内最相似的记忆（用于语义去重）。
    /// </summary>
    private (MemoryEntry Entry, float Distance)? FindMostSimilarWithinThreshold(float[] vector)
    {
        var matches = VectorSearch(vector, 1);
        if (matches.Count == 0)
            return null;

        var match = matches[0];
        // Cosine distance: 0 = identical, 2 = opposite. 阈值 0.15 ≈ 相似度 0.85
        if (match.Distance > DeduplicationDistance)
            return null;

        using var context = new MemoryDbContext(_dbOptions);
        var entry = LoadEntryByRowId(context, match.RowId);
        if (entry is null)
            return null;

        return (entry, match.Distance);
    }

    private static MemoryEntry? LoadEntryByRowId(MemoryDbContext context, long rowId)
    {
        return context.Memories
            .FromSqlRaw("SELECT id, category, importance, content, keywords, created_at, embedding FROM memories WHERE rowid = {0}", rowId)
            .AsNoTracking()
            .Select(r => r.ToEntry())
            .FirstOrDefault();
    }

    private static List<MemoryEntry> LoadEntriesByRowIds(MemoryDbContext context, List<long> rowIds)
    {
        if (rowIds.Count == 0)
            return [];

        // 逐行查询后按原始顺序重建，保持向量搜索的相关度顺序
        var map = new Dictionary<long, MemoryEntry>();
        foreach (var rowId in rowIds)
        {
            var entry = LoadEntryByRowId(context, rowId);
            if (entry is not null)
                map[rowId] = entry;
        }

        return rowIds
            .Where(map.ContainsKey)
            .Select(rid => map[rid])
            .ToList();
    }

    /// <summary>
    /// 旧版 JSON 格式的记忆条目，仅用于迁移。
    /// </summary>
    private sealed class StoredMemoryEntryLegacy
    {
        public MemoryEntry? Entry { get; set; }
        public float[]? Vector { get; set; }
    }
}
