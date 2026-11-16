using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.Vault;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.Lint;
using ObsidianGraphRag.Shared.Services.Retrieval;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.N8n;

namespace ObsidianGraphRag.Shared
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddObsidianGraphRag(this IServiceCollection services, IConfiguration? configuration = null,
            bool registerLocalWorkspace = true)
        {
            const string ollamaClientName = "Ollama";
            services.AddHttpClient(ollamaClientName, client =>
            {
                var timeoutSeconds = int.TryParse(configuration?["Ollama:RequestTimeoutSeconds"], out var configuredTimeout)
                    && configuredTimeout > 0 ? configuredTimeout : 180;
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });
            services.AddSingleton<ILlmClient>(sp => new LlmClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(ollamaClientName), configuration));
            if (!registerLocalWorkspace)
                return services;

            services.AddSingleton(_ => new VaultGraphEngine(configuration?["KnowledgeStorage:RootPath"]));
            services.AddSingleton<ObsidianIngestEngine>();
            services.AddHttpClient<DocumentImportService>(client =>
            {
                if (Uri.TryCreate(configuration?["Docling:BaseUrl"], UriKind.Absolute, out var address))
                    client.BaseAddress = address;
            });
            services.AddSingleton<WikiLintEngine>();
            services.AddSingleton<SemanticNoteIndex>();
            services.AddSingleton<GraphRagOrchestrator>();
            services.AddSingleton<SiteManagerService>();
            services.AddSingleton<N8nCrawlService>(sp => new N8nCrawlService(sp.GetRequiredService<HttpClient>(), configuration));

            return services;
        }
    }
}
