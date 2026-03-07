namespace sharpclaw.Memory;

/// <summary>
/// 记忆条目：记忆库中的单条记忆。
/// </summary>
public class MemoryEntry
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>类别：fact/preference/decision/todo/lesson</summary>
    public string Category { get; set; } = "fact";

    /// <summary>重要度 1-10</summary>
    public int Importance { get; set; } = 5;

    /// <summary>记忆内容</summary>
    public string Content { get; set; } = "";

    /// <summary>关键词列表</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>嵌入向量，以 BLOB 形式存储（float[] 按字节序列化）</summary>
    public byte[] Embedding { get; set; } = [];

    /// <summary>从 float[] 转换为字节数组</summary>
    public static byte[] FloatArrayToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>从字节数组转换为 float[]</summary>
    public static float[] BytesToFloatArray(byte[] bytes)
    {
        var result = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
}

/// <summary>
/// 记忆库统计信息。
/// </summary>
public class MemoryStats
{
    /// <summary>记忆总数</summary>
    public int TotalCount { get; set; }

    /// <summary>按类别统计</summary>
    public Dictionary<string, int> ByCategory { get; set; } = [];

    /// <summary>平均重要度</summary>
    public double AverageImportance { get; set; }

    /// <summary>最早记忆时间</summary>
    public DateTimeOffset? OldestMemory { get; set; }

    /// <summary>最新记忆时间</summary>
    public DateTimeOffset? NewestMemory { get; set; }
}
