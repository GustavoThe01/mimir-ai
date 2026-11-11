using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mimir.Core.Knowledge;
using Mimir.Core.Llm;

namespace Mimir.Infrastructure.Knowledge;

/// <summary>
/// Índice de trechos num arquivo SQLite. O vetor fica numa coluna BLOB, como a sequência de floats de 32 bits.
/// </summary>
/// <remarks>
/// Guardar o vetor em binário ocupa 4 bytes por dimensão; em JSON seriam mais de 10. O SQLite não compara vetores,
/// então a similaridade é calculada em C# (<see cref="SemanticSearch"/>). Com a extensão sqlite-vec, a comparação
/// poderia ir para o banco, se a base crescer.
/// </remarks>
public sealed class SqliteChunkStore : IChunkStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS chunk (
            id          TEXT PRIMARY KEY,
            document_id TEXT NOT NULL,
            title       TEXT NOT NULL,
            section     TEXT NOT NULL,
            text        TEXT NOT NULL,
            start_line  INTEGER NOT NULL,
            end_line    INTEGER NOT NULL,
            department  TEXT NOT NULL,
            access      TEXT NOT NULL CHECK (json_valid(access)),
            hash        TEXT NOT NULL,
            model       TEXT NOT NULL,
            dimensions  INTEGER NOT NULL,
            embedding   BLOB NOT NULL CHECK (length(embedding) = dimensions * 4)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_chunk_document ON chunk (document_id);
        CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(
            id UNINDEXED, section, text, tokenize = 'unicode61 remove_diacritics 2');
        """;

    private readonly string _connectionString;

    public SqliteChunkStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Um índice criado antes da busca textual tem os trechos, mas não a tabela FTS: refaz a partir de chunk.
        command.CommandText = """
            INSERT INTO chunk_fts (id, section, text)
            SELECT id, section, text FROM chunk
            WHERE (SELECT count(*) FROM chunk_fts) <> (SELECT count(*) FROM chunk)
              AND id NOT IN (SELECT id FROM chunk_fts)
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task<IReadOnlyDictionary<string, ChunkFingerprint>> GetFingerprintsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, hash, model FROM chunk";
        var result = new Dictionary<string, ChunkFingerprint>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = new ChunkFingerprint(reader.GetString(1), reader.GetString(2));
        }

        return result;
    }

    public async Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chunk (id, document_id, title, section, text, start_line, end_line, department, access, hash, model, dimensions, embedding)
            VALUES ($id, $document, $title, $section, $text, $start, $end, $department, $access, $hash, $model, $dimensions, $embedding)
            ON CONFLICT (id) DO UPDATE SET
                document_id = excluded.document_id, title = excluded.title, section = excluded.section, text = excluded.text,
                start_line = excluded.start_line, end_line = excluded.end_line, department = excluded.department,
                access = excluded.access, hash = excluded.hash, model = excluded.model,
                dimensions = excluded.dimensions, embedding = excluded.embedding
            """;
        foreach (var item in chunks)
        {
            var chunk = item.Chunk;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", chunk.Id);
            command.Parameters.AddWithValue("$document", chunk.DocumentId);
            command.Parameters.AddWithValue("$title", chunk.Title);
            command.Parameters.AddWithValue("$section", chunk.Section);
            command.Parameters.AddWithValue("$text", chunk.Text);
            command.Parameters.AddWithValue("$start", chunk.StartLine);
            command.Parameters.AddWithValue("$end", chunk.EndLine);
            command.Parameters.AddWithValue("$department", chunk.Department.ToString());
            command.Parameters.AddWithValue("$access", JsonSerializer.Serialize(chunk.Access));
            command.Parameters.AddWithValue("$hash", chunk.Hash);
            command.Parameters.AddWithValue("$model", item.Model);
            command.Parameters.AddWithValue("$dimensions", item.Vector.Length);
            command.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes(item.Vector.Span).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var text = connection.CreateCommand();
            text.Transaction = transaction;
            text.CommandText = """
                DELETE FROM chunk_fts WHERE id = $id;
                INSERT INTO chunk_fts (id, section, text) VALUES ($id, $section, $text);
                """;
            text.Parameters.AddWithValue("$id", chunk.Id);
            text.Parameters.AddWithValue("$section", chunk.Section);
            text.Parameters.AddWithValue("$text", chunk.Text);
            await text.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM chunk WHERE id IN (SELECT value FROM json_each($ids));
            DELETE FROM chunk_fts WHERE id IN (SELECT value FROM json_each($ids));
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SearchTextAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var match = ToMatchExpression(query);
        if (match.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // bm25: quanto menor, mais relevante. A seção pesa o dobro do texto: o título já diz do que o trecho trata.
        command.CommandText = """
            SELECT id FROM chunk_fts WHERE chunk_fts MATCH $match
            ORDER BY bm25(chunk_fts, 0.0, 2.0, 1.0), id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Transforma a pergunta numa expressão FTS5 segura: cada palavra vira um termo entre aspas, ligado por OR.
    /// Sem isso, uma pergunta com aspas, hífen ou parênteses quebraria a sintaxe do MATCH.
    /// </summary>
    internal static string ToMatchExpression(string query)
    {
        var terms = new string(query.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length > 2 || term.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => $"\"{term}\"");
        return string.Join(" OR ", terms);
    }

    public async Task<IReadOnlyList<StoredChunk>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, document_id, title, section, text, start_line, end_line, department, access, hash, model, embedding
            FROM chunk ORDER BY id
            """;
        var result = new List<StoredChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunk = new KnowledgeChunk(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                Enum.Parse<Department>(reader.GetString(7)),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
                reader.GetString(9));
            var vector = MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(11)).ToArray();
            result.Add(new StoredChunk(chunk, vector, reader.GetString(10)));
        }

        return result;
    }
}
