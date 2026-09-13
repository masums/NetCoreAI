using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace NetCoreAI.VectorStores.Sqlite;

/// <summary>
/// Zero-config vector store: one SQLite file, embeddings as float32 BLOBs, brute-force cosine search with
/// SIMD (<see cref="TensorPrimitives"/>). Correct and fast enough up to a few hundred thousand chunks;
/// larger sets should move to pgvector/Qdrant (Phase 4) or sqlite-vec acceleration.
/// </summary>
public sealed class SqliteVectorStore : IVectorStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteVectorStore(IOptions<NetCoreAIOptions> options)
        : this(Path.Combine(options.Value.DataDirectory, "vectors.db"))
    {
    }

    public SqliteVectorStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
    }

    public string Id => "sqlite";

    public async Task EnsureCollectionAsync(string collection, int dimensions, VectorDistance distance = VectorDistance.Cosine, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await Exec(db, "INSERT INTO collections(name, dimensions, distance) VALUES($n,$d,$m) ON CONFLICT(name) DO NOTHING", cancellationToken, ("$n", collection), ("$d", dimensions), ("$m", distance.ToString())).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await Exec(db, "DELETE FROM chunks WHERE collection=$n; DELETE FROM collections WHERE name=$n", cancellationToken, ("$n", collection)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM collections ORDER BY name";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (dimensions, _) = await GetCollectionAsync(db, collection, cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = db.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO chunks(id, collection, document_id, embedding, text, metadata, acl) VALUES($id,$c,$doc,$emb,$text,$meta,$acl) ON CONFLICT(id) DO UPDATE SET document_id=excluded.document_id, embedding=excluded.embedding, text=excluded.text, metadata=excluded.metadata, acl=excluded.acl";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pC = cmd.Parameters.Add("$c", SqliteType.Text);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);
            var pEmb = cmd.Parameters.Add("$emb", SqliteType.Blob);
            var pText = cmd.Parameters.Add("$text", SqliteType.Text);
            var pMeta = cmd.Parameters.Add("$meta", SqliteType.Text);
            var pAcl = cmd.Parameters.Add("$acl", SqliteType.Text);
            foreach (var r in records)
            {
                if (r.Embedding.Length != dimensions)
                {
                    throw new ArgumentException($"Record '{r.Id}' has {r.Embedding.Length} dimensions; collection '{collection}' expects {dimensions}.");
                }

                pId.Value = r.Id;
                pC.Value = collection;
                pDoc.Value = r.DocumentId;
                pEmb.Value = MemoryMarshal.AsBytes(r.Embedding.Span).ToArray();
                pText.Value = r.Text;
                pMeta.Value = JsonSerializer.Serialize(r.Metadata, Json);
                pAcl.Value = JsonSerializer.Serialize(r.AclTags, Json);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteByDocumentAsync(string collection, string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await Exec(db, "DELETE FROM chunks WHERE collection=$c AND document_id=$d", cancellationToken, ("$c", collection), ("$d", documentId)).ConfigureAwait(false);
    }

    public async Task<long> CountAsync(string collection, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE collection=$c";
        cmd.Parameters.AddWithValue("$c", collection);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string collection, ReadOnlyMemory<float> query, int topK, VectorFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (dimensions, distance) = await GetCollectionAsync(db, collection, cancellationToken).ConfigureAwait(false);
        if (query.Length != dimensions)
        {
            throw new ArgumentException($"Query has {query.Length} dimensions; collection '{collection}' expects {dimensions}.");
        }

        await using var cmd = db.CreateCommand();
        var sql = "SELECT id, document_id, embedding, text, metadata, acl FROM chunks WHERE collection=$c";
        cmd.Parameters.AddWithValue("$c", collection);
        if (filter?.DocumentIds is { Count: > 0 } docs)
        {
            var names = docs.Select((_, i) => $"$doc{i}").ToArray();
            sql += $" AND document_id IN ({string.Join(',', names)})";
            for (var i = 0; i < docs.Count; i++)
            {
                cmd.Parameters.AddWithValue(names[i], docs[i]);
            }
        }

        cmd.CommandText = sql;

        // Brute force: score every candidate (SIMD dot/cosine), keep the best topK with a bounded heap.
        var queryNorm = distance == VectorDistance.Cosine ? MathF.Sqrt(TensorPrimitives.Dot(query.Span, query.Span)) : 1f;
        var best = new PriorityQueue<VectorSearchResult, float>(topK + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), Json) ?? [];
            var acl = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), Json) ?? [];
            if (!Matches(filter, metadata, acl))
            {
                continue;
            }

            var bytes = (byte[])reader[2];
            var embedding = MemoryMarshal.Cast<byte, float>(bytes);
            var score = distance switch
            {
                VectorDistance.Cosine => TensorPrimitives.Dot(query.Span, embedding) / (queryNorm * MathF.Sqrt(TensorPrimitives.Dot(embedding, embedding)) + 1e-12f),
                VectorDistance.DotProduct => TensorPrimitives.Dot(query.Span, embedding),
                _ => -TensorPrimitives.Distance(query.Span, embedding),
            };
            if (filter?.MinScore is { } min && score < min)
            {
                continue;
            }

            var record = new VectorRecord(reader.GetString(0), reader.GetString(1), embedding.ToArray(), reader.GetString(3), metadata, acl);
            best.Enqueue(new VectorSearchResult(record, score), score);
            if (best.Count > topK)
            {
                best.Dequeue(); // lowest priority = lowest score
            }
        }

        var results = new List<VectorSearchResult>(best.Count);
        while (best.Count > 0)
        {
            results.Add(best.Dequeue());
        }

        results.Reverse();
        return results;
    }

    private static bool Matches(VectorFilter? filter, Dictionary<string, string> metadata, List<string> acl)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.MetadataEquals is { Count: > 0 } eq && eq.Any(kv => !metadata.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)))
        {
            return false;
        }

        // One implementation of the rule, shared with everything else that reasons about access: a record
        // with no tags at all is public, which is what the ingestion pipeline writes when neither the base
        // nor the document restricts it.
        return AclTag.Allows(acl, filter.CallerTags);
    }

    private static async Task<(int Dimensions, VectorDistance Distance)> GetCollectionAsync(SqliteConnection db, string collection, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT dimensions, distance FROM collections WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", collection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Vector collection '{collection}' does not exist. Call EnsureCollectionAsync first.");
        }

        return (reader.GetInt32(0), Enum.Parse<VectorDistance>(reader.GetString(1)));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct).ConfigureAwait(false);
        if (!_initialized)
        {
            await Exec(db, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS collections(name TEXT PRIMARY KEY, dimensions INTEGER NOT NULL, distance TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS chunks(id TEXT PRIMARY KEY, collection TEXT NOT NULL, document_id TEXT NOT NULL, embedding BLOB NOT NULL, text TEXT NOT NULL, metadata TEXT NOT NULL, acl TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_chunks_collection ON chunks(collection);
                CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks(collection, document_id);
                """, ct).ConfigureAwait(false);
            _initialized = true;
        }

        return db;
    }

    private static async Task Exec(SqliteConnection db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _writeLock.Dispose();
}
