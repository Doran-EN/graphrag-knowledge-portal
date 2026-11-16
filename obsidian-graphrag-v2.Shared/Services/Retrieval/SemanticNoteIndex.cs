using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.Retrieval;

public sealed class SemanticNoteIndex(VaultGraphEngine graph, ILlmClient llm, ILogger<SemanticNoteIndex> logger)
{
    internal const int PassageLength = 1200;
    private const int PassageOverlap = 250;
    private const int BatchSize = 8;
    private const int SaveInterval = 64;
    private const float MinimumSimilarity = 0.62f;

    private readonly string _cachePath = Path.Combine(graph.RootPath, ".semantic", "nomic-embed-text-v1.json");
    private readonly SemaphoreSlim _gate = new(1);
    private Dictionary<string, float[]>? _vectors;
    private Task? _indexing;

    internal async Task<IReadOnlyList<SemanticMatch>> FindAsync(
        IReadOnlyList<ObsidianNote> notes, string question, bool fullCollection)
    {
        if (notes.Count == 0 || string.IsNullOrWhiteSpace(question) || !await _gate.WaitAsync(0))
            return [];

        try
        {
            _vectors ??= await LoadAsync();
            var passages = notes.SelectMany(CreatePassages).ToArray();
            var changed = false;
            if (fullCollection)
            {
                var activeKeys = passages.Select(passage => passage.Key).ToHashSet();
                foreach (var key in _vectors.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
                    changed |= _vectors.Remove(key);
            }

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var indexing = _indexing is { IsCompleted: false };
            float[]? queryVector = null;
            try
            {
                queryVector = (await llm.GenerateEmbeddingsAsync([$"search_query: {question}"], budget.Token))[0];
                if (!indexing)
                {
                    foreach (var batch in MissingPassages(passages, _vectors).Chunk(BatchSize))
                    {
                        var vectors = await EmbedPassagesAsync(batch, budget.Token);
                        StoreBatch(_vectors, batch, vectors);
                        changed = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Semantic search reached its time budget. Completed embeddings are available for later searches.");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Ollama embeddings were unavailable for semantic search.");
            }

            if (changed)
                await SaveAsync(_vectors);

            if (queryVector is null)
                return [];

            if (!indexing && budget.IsCancellationRequested)
            {
                var remaining = MissingPassages(passages, _vectors).ToArray();
                if (remaining.Length > 0)
                    _indexing = CompleteIndexAsync(remaining, _vectors);
            }

            return passages
                .Where(passage => _vectors.TryGetValue(passage.Key, out var vector) && vector?.Length == queryVector.Length)
                .Select(passage => new SemanticMatch(passage.Note, passage.Text, Similarity(queryVector, _vectors[passage.Key])))
                .Where(match => match.Similarity >= MinimumSimilarity)
                .OrderByDescending(match => match.Similarity)
                .DistinctBy(match => match.Note.RelativePath)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteIndexAsync(Passage[] remaining, Dictionary<string, float[]> cache)
    {
        var indexed = 0;
        try
        {
            foreach (var batch in remaining.Chunk(BatchSize))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var vectors = await EmbedPassagesAsync(batch, budget.Token);
                await _gate.WaitAsync();
                try
                {
                    StoreBatch(cache, batch, vectors);
                    indexed += batch.Length;
                    if (indexed % SaveInterval == 0)
                        await SaveAsync(cache);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Semantic indexing paused. A later search can resume it.");
        }
        finally
        {
            if (indexed % SaveInterval != 0)
            {
                await _gate.WaitAsync();
                try
                {
                    await SaveAsync(cache);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        logger.LogInformation("Background semantic indexing cached {Count} additional passages.", indexed);
    }

    private Task<float[][]> EmbedPassagesAsync(Passage[] batch, CancellationToken token) =>
        llm.GenerateEmbeddingsAsync(batch.Select(passage => $"search_document: {passage.Text}").ToArray(), token);

    private static IEnumerable<Passage> MissingPassages(Passage[] passages, Dictionary<string, float[]> cache) =>
        passages.Where(passage => !cache.ContainsKey(passage.Key)).DistinctBy(passage => passage.Key);

    private static void StoreBatch(Dictionary<string, float[]> cache, Passage[] batch, float[][] vectors)
    {
        for (var i = 0; i < batch.Length; i++)
            cache[batch[i].Key] = vectors[i];
    }

    private static IEnumerable<Passage> CreatePassages(ObsidianNote note)
    {
        var body = note.BodyContent;
        for (var start = 0; start < body.Length; start += PassageLength - PassageOverlap)
        {
            var length = Math.Min(PassageLength, body.Length - start);
            var text = body.Substring(start, length).Trim();
            if (text.Length > 0)
            {
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                yield return new Passage(note, text, key);
            }
            if (start + length == body.Length)
                yield break;
        }
    }

    // Ollama returns unit-length embeddings, so the dot product is cosine similarity.
    private static float Similarity(float[] left, float[] right)
    {
        var score = 0f;
        for (var i = 0; i < left.Length; i++)
            score += left[i] * right[i];
        return score;
    }

    private async Task<Dictionary<string, float[]>> LoadAsync()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                using var stream = File.OpenRead(_cachePath);
                return await JsonSerializer.DeserializeAsync<Dictionary<string, float[]>>(stream) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "The semantic index could not be read and will be rebuilt.");
        }
        return new();
    }

    private async Task SaveAsync(Dictionary<string, float[]> vectors)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporaryPath = _cachePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, vectors);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The semantic index could not be saved to disk.");
        }
    }

    private sealed record Passage(ObsidianNote Note, string Text, string Key);
    internal sealed record SemanticMatch(ObsidianNote Note, string Passage, float Similarity);
}
