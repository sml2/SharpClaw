namespace sharpclaw.Memory;

/// <summary>
/// EF Core 数据库实体，映射 memories 表。
/// 包含向量嵌入字段，与领域对象 <see cref="MemoryEntry"/> 分离。
/// </summary>
internal sealed class MemoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "fact";
    public int Importance { get; set; } = 5;
    public string Content { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>嵌入向量，以 BLOB 形式存储（float[] 按字节序列化）</summary>
    public byte[] Embedding { get; set; } = [];

    /// <summary>从领域对象转换</summary>
    public static MemoryRecord FromEntry(MemoryEntry entry, float[] embedding) => new()
    {
        Id = entry.Id,
        Category = entry.Category,
        Importance = entry.Importance,
        Content = entry.Content,
        Keywords = entry.Keywords,
        CreatedAt = entry.CreatedAt,
        Embedding = FloatArrayToBytes(embedding),
    };

    /// <summary>转换为领域对象</summary>
    public MemoryEntry ToEntry() => new()
    {
        Id = Id,
        Category = Category,
        Importance = Importance,
        Content = Content,
        Keywords = Keywords,
        CreatedAt = CreatedAt,
    };

    public static byte[] FloatArrayToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToFloatArray(byte[] bytes)
    {
        var result = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
}
