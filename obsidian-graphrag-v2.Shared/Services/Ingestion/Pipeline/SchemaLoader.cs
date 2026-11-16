using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public class SchemaLoadResult
    {
        public string SchemaContent { get; set; } = string.Empty;
        public string SchemaHash { get; set; } = string.Empty;
        public bool Exists { get; set; }
    }

    public static class SchemaLoader
    {
        public static async Task<SchemaLoadResult> LoadAsync(string vaultPath)
        {
            var schemaPath = Path.Combine(vaultPath, "TheSchema.md");
            if (!File.Exists(schemaPath))
            {
                return new SchemaLoadResult
                {
                    Exists = false,
                    SchemaContent = "Default generic schema: Extract entities and concepts with valid Obsidian Properties YAML frontmatter.",
                    SchemaHash = "default"
                };
            }

            var content = await File.ReadAllTextAsync(schemaPath, Encoding.UTF8);
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
            var hash = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();

            return new SchemaLoadResult
            {
                Exists = true,
                SchemaContent = content,
                SchemaHash = hash
            };
        }
    }
}
