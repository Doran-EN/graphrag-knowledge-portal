using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.Ingestion;

public sealed class DocumentImportService(HttpClient client, VaultGraphEngine graph)
{
    public const string Collection = "documents";
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const string AcceptedExtensions = ".pdf,.docx,.pptx,.xlsx,.png,.jpg,.jpeg,.tiff";
    private const int SectionLength = 2600;

    public bool IsConfigured => client.BaseAddress is not null;

    public IReadOnlyList<UploadedDocument> GetUploadedDocuments()
    {
        var collection = Path.Combine(graph.RootPath, "raw", Collection);
        var documents = new List<UploadedDocument>();
        if (!Directory.Exists(collection))
            return documents;

        const string firstPartSuffix = " (part 1)";
        foreach (var folder in Directory.EnumerateDirectories(collection))
        {
            var firstPart = Path.Combine(folder, "part-0001.md");
            try
            {
                var note = graph.ParseNote(firstPart);
                var title = note.Title;
                if (title.EndsWith(firstPartSuffix, StringComparison.Ordinal))
                    title = title[..^firstPartSuffix.Length];
                if (AcceptedExtensions.Split(',').Contains(Path.GetExtension(title).ToLowerInvariant()))
                    documents.Add(new UploadedDocument(Path.GetFileName(folder), title));
            }
            catch (IOException)
            {
                // A document may have been removed after the directory was listed.
            }
        }

        return documents.OrderBy(document => document.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(document => document.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<(string Id, string FileName, int Sections, bool AlreadyImported)> ImportAsync(string suppliedName, Stream upload)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Document conversion is unavailable.");

        var fileName = Path.GetFileName(suppliedName.Replace('\\', '/'));
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AcceptedExtensions.Split(',').Contains(extension) || fileName.Length > 255)
            throw new InvalidOperationException("Choose a supported document or image file.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellation = timeout.Token;
        var staging = Path.Combine(graph.RootPath, ".imports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var originalPath = Path.Combine(staging, "original" + extension);
            byte[] hash;
            await using (var original = new FileStream(originalPath, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                await upload.CopyToAsync(original, cancellation);
                if (original.Length == 0 || original.Length > MaxFileBytes)
                    throw new InvalidOperationException("Choose a non-empty file no larger than 20 MB.");
                original.Position = 0;
                hash = await SHA256.HashDataAsync(original, cancellation);
            }

            var fileKey = Regex.Replace(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (fileKey.Length > 60)
                fileKey = fileKey[..60];
            var collection = Path.Combine(graph.RootPath, "raw", Collection);
            var id = $"{fileKey}-{Convert.ToHexStringLower(hash)}";
            var destination = Path.Combine(collection, id);
            if (Directory.Exists(destination))
                return (id, fileName, 0, true);

            var markdown = await ConvertAsync(originalPath, fileName, cancellation);
            var sections = SplitMarkdown(markdown).ToList();
            if (sections.Count == 0)
                throw new InvalidOperationException("Docling did not extract any Markdown from this file.");

            for (var index = 0; index < sections.Count; index++)
            {
                var number = index + 1;
                var title = sections.Count == 1 ? fileName : $"{fileName} (part {number})";
                var quotedTitle = JsonSerializer.Serialize(title).Replace("---", @"\u002d\u002d\u002d");
                var content = $"---\ntitle: {quotedTitle}\nsite: {Collection}\ndocument_part: {number}\n---\n\n{sections[index]}\n";
                await File.WriteAllTextAsync(Path.Combine(staging, $"part-{number:D4}.md"), content, cancellation);
            }

            Directory.CreateDirectory(collection);
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                return (id, fileName, 0, true);
            }

            return (id, fileName, sections.Count, false);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<string> ConvertAsync(string path, string fileName, CancellationToken cancellation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(File.OpenRead(path)), "files", fileName);
        form.Add(new StringContent("md"), "to_formats");

        using var submission = await client.PostAsync("/v1/convert/file/async", form, cancellation);
        submission.EnsureSuccessStatusCode();
        var task = await submission.Content.ReadFromJsonAsync<ConversionTask>(cancellation)
            ?? throw new InvalidOperationException("Docling did not return a conversion task.");
        var taskId = Uri.EscapeDataString(task.Id);

        while (task.Status is "pending" or "started")
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            task = await client.GetFromJsonAsync<ConversionTask>($"/v1/status/poll/{taskId}", cancellation)
                ?? throw new InvalidOperationException("Docling did not return a conversion status.");
        }

        if (task.Status != "success")
            throw new InvalidOperationException("Docling could not fully convert the file.");

        var result = await client.GetFromJsonAsync<ConversionResult>($"/v1/result/{taskId}", cancellation);
        if (result?.Status != "success" || string.IsNullOrWhiteSpace(result.Document?.Markdown))
            throw new InvalidOperationException("Docling did not return a complete Markdown document.");
        return result.Document.Markdown;
    }

    private static IEnumerable<string> SplitMarkdown(string markdown)
    {
        var start = 0;
        while (start < markdown.Length)
        {
            var end = Math.Min(start + SectionLength, markdown.Length);
            if (end < markdown.Length)
            {
                var lineBreak = markdown.LastIndexOf('\n', end - 1, end - start);
                if (lineBreak > start)
                    end = lineBreak + 1;
                else if (char.IsHighSurrogate(markdown[end - 1]))
                    end--;
            }

            var section = markdown[start..end].Trim();
            if (section.Length > 0)
                yield return section;
            start = end;
        }
    }

    private sealed record ConversionTask(
        [property: JsonPropertyName("task_id")] string Id,
        [property: JsonPropertyName("task_status")] string Status);

    private sealed record ConversionResult(string Status, ConvertedDocument? Document);

    private sealed record ConvertedDocument([property: JsonPropertyName("md_content")] string Markdown);

    public sealed record UploadedDocument(string Id, string FileName)
    {
        internal string RawPathPrefix => $"raw/{Collection}/{Id}/";
    }
}
