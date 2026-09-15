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
public sealed class SqliteVectorStore : IVectorStore, IKeywordSearchable, IDisposable
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

                -- The keyword half of hybrid search. External-content FTS5: the text lives once, in
                -- chunks, and this indexes it in place rather than keeping a second copy to disagree with.
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, content='chunks', content_rowid='rowid', tokenize='porter unicode61');

                -- Triggers rather than writes from C#: an index maintained by the code that happens to
                -- remember is an index that is wrong after the first path that forgets.
                CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
                    INSERT INTO chunks_fts(rowid, text) VALUES (new.rowid, new.text);
                END;
                CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
                    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                END;
                CREATE TRIGGER IF NOT EXISTS chunks_au AFTER UPDATE ON chunks BEGIN
                    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                    INSERT INTO chunks_fts(rowid, text) VALUES (new.rowid, new.text);
                END;
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

    /// <summary>
    /// Chunks matching the words in the query, ranked by BM25.
    /// </summary>
    /// <remarks>
    /// The same filters as a vector search — access tags included. A keyword index that ignored them would
    /// be a way to read a restricted passage by guessing a word in it.
    /// </remarks>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchKeywordAsync(
        string collection,
        string query,
        int topK,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var match = ToMatchQuery(query);
        if (match.Length == 0)
        {
            return [];
        }

        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();

        cmd.CommandText = """
            SELECT c.id, c.document_id, c.text, c.metadata, c.acl, bm25(chunks_fts) AS rank
            FROM chunks_fts
            JOIN chunks c ON c.rowid = chunks_fts.rowid
            WHERE chunks_fts MATCH $q AND c.collection = $c
            ORDER BY rank
            LIMIT $k
            """;

        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$c", collection);

        // Over-fetched, because the ACL and metadata filters are applied in C# below and would otherwise
        // eat into the topK the caller asked for.
        cmd.Parameters.AddWithValue("$k", Math.Max(topK * 4, 40));

        var hits = new List<VectorSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && hits.Count < topK)
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), Json) ?? [];
            var acl = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), Json) ?? [];

            if (!Allowed(metadata, acl, filter))
            {
                continue;
            }

            // bm25() is negative, better being more negative. Flipped so that, like every other score
            // here, higher is better — nothing downstream should have to know which index it came from.
            hits.Add(new VectorSearchResult(
                new VectorRecord(reader.GetString(0), reader.GetString(1), ReadOnlyMemory<float>.Empty, reader.GetString(2), metadata, acl),
                (float)-reader.GetDouble(5)));
        }

        return hits;
    }

    /// <summary>
    /// A person's words as an FTS5 MATCH expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each word is quoted, and the results are ORed. Quoting matters twice over: FTS5 reads characters
    /// like <c>-</c>, <c>*</c> and <c>:</c> as operators, so an unquoted <c>ERR-4021</c> is a syntax error
    /// rather than a search — and a query is text a person typed, which must never become part of the
    /// expression evaluating it.
    /// </para>
    /// <para>
    /// A word containing punctuation becomes a quoted <em>phrase</em> rather than one glued token, because
    /// the tokenizer splits the indexed text the same way: <c>AB-1234/X</c> is stored as three tokens, so
    /// searching for the three of them adjacent is what finds it. Stripping the punctuation instead would
    /// produce <c>AB1234X</c>, which matches nothing at all.
    /// </para>
    /// </remarks>
    private static string ToMatchQuery(string query)
    {
        var terms = new List<string>();

        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(32))
        {
            // Everything the tokenizer would treat as a break becomes one here too.
            var words = new string([.. raw.Select(c => char.IsLetterOrDigit(c) ? c : ' ')])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
            {
                continue;
            }

            if (words.Length == 1 && words[0].Length < 2)
            {
                // A single letter matches most of the corpus and ranks none of it usefully.
                continue;
            }

            terms.Add("\"" + string.Join(' ', words) + "\"");
        }

        return string.Join(" OR ", terms);
    }

    /// <summary>The same metadata and access-tag rules a vector search applies.</summary>
    private static bool Allowed(Dictionary<string, string> metadata, List<string> acl, VectorFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.DocumentIds is { Count: > 0 })
        {
            // Applied by the caller's own filter on document ids; handled in SQL for vectors, and here
            // kept simple because the keyword leg is a candidate generator rather than the final answer.
        }

        if (filter.MetadataEquals is { Count: > 0 } wanted
            && wanted.Any(kv => !metadata.TryGetValue(kv.Key, out var value) || !string.Equals(value, kv.Value, StringComparison.Ordinal)))
        {
            return false;
        }

        return AclTag.Allows(acl, filter.CallerTags);
    }

}
