# Obsidian GraphRAG in Docker

Run these commands from the directory containing `compose.yaml`. Docker Desktop shows the services under the **obsidian-graphrag** project.

| Service | Purpose | Address |
| --- | --- | --- |
| Web | Blazor dashboard, ingestion, graph and chat | http://localhost:18082 |
| n8n | Runs the website ingestion workflow and stores it in a dedicated SQLite volume | http://localhost:15679 |
| Crawl4AI | Converts crawled pages to Markdown | `http://crawl4ai:11235` inside Docker |

Create a local `.env` once. Keep the generated token for the n8n crawler credential.

```powershell
if (!(Test-Path -LiteralPath .env)) {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $token = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    Set-Content -LiteralPath .env -Value "CRAWL4AI_API_TOKEN=$token" -Encoding ascii
}
docker compose up -d --build
docker compose ps
```

The web app uses Ollama on Windows through `host.docker.internal:11434` and defaults to `qwen3.5:4b`. Set `GRAPHRAG_OLLAMA_URL` or `GRAPHRAG_OLLAMA_MODEL` in `.env` if your endpoint or model differs.
Ollama requests have a 180-second deadline, including time spent waiting for the model to load or become available. Set `GRAPHRAG_OLLAMA_TIMEOUT_SECONDS` in `.env` to adjust it.

Chat combines keyword matches with semantic passage search using the local Ollama embedding model. If the model is missing, run `ollama pull nomic-embed-text` on the Ollama host. Chat caches passage embeddings in the knowledge volume and uses keyword search when the embedding model is unavailable. Initial searches can spend up to 25 seconds building the cache. Any remaining passages are indexed in the background and become searchable as their embeddings are ready.

On a fresh n8n instance, create the local owner account in the browser. If the workflow is absent, import [the bundled workflow](n8n/Crawl4AI_Markdown_Obsidian_Universal_Ingest.json). Set its Crawl4AI node to use a Header Auth credential with header name `Authorization` and value `Bearer <CRAWL4AI_API_TOKEN>` from `.env`.

Publish the bundled workflow after assigning the Crawl4AI credential. The portal starts it at `/webhook/graphrag-private-crawl`. The workflow verifies a temporary crawl grant with the web container on port 8081 and uses it to save pages, indexes, and reports inside the requesting account's workspace.

The Compose volume `obsidian-graphrag_knowledge-data` is mounted at `/knowledge`. On first startup with accounts enabled, run this command from the Compose directory to get the owner setup code:

```powershell
docker compose exec -T web cat /knowledge/.accounts/owner-setup
```

Open [the portal](http://localhost:18082), enter the code, and choose an owner email and password. The portal copies the existing knowledge base into the owner's workspace during setup. Use **Your account → Create invitation** to generate a single-use code that expires after 24 hours. The invited user enters it on the **Have an invitation?** page and starts with an empty workspace.

Each account uses its own directory under `/knowledge/.workspaces/`. Crawled pages and uploads live under that workspace's `raw/`, generated notes and ingestion logs under `wiki/`, and semantic search data under `.semantic/`. The account database and workspaces persist across container restarts. Sign in again after the web container restarts.

In **Knowledge Ingest**, use the crawl form for websites or **Document Import** for PDF, Word, Excel, PowerPoint, and supported images up to 20 MB. Docling converts each upload to Markdown. The original and Markdown sections live under `raw/documents/`. Chat searches websites and uploaded documents together by default. The **Uploaded documents** filter limits the search to that collection. Run incremental ingest on **documents** to extract entities and links into the wiki.

Use the Docker dashboard for this volume. The Web project's Visual Studio `http` and `https` launch profiles forward browser requests to port 18082. If you change `GRAPHRAG_WEB_PORT`, update `GraphRag__DockerDashboardUrl` in the launch profiles to match. For a standalone development instance with its own storage and n8n, configure those endpoints and run `dotnet run --project obsidian-graphrag-v2.Web --no-launch-profile`.
