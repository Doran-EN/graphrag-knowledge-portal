# GraphRAG Knowledge Portal

A self-hosted Blazor portal that turns documents and websites into a searchable knowledge graph. Chat combines keyword search, semantic search, and graph links to answer questions with citations. Each account has its own sources and graph.

- **Document ingestion** uses Docling to convert files to Markdown. Incremental ingestion skips unchanged sections and can pick up unfinished work.
- **Website ingestion** uses n8n and Crawl4AI to collect pages and save the resulting Markdown in the requesting account's workspace.
- **Graph and chat** let users explore entities and concepts, limit a search to a particular source, and inspect the cited excerpts.

## Run locally

Install Docker Desktop and Ollama. Start Ollama and download the chat and embedding models:

```powershell
ollama pull qwen3.5:4b
ollama pull nomic-embed-text
```

Run the remaining commands from the directory containing `compose.yaml`. Docker Desktop shows the services under the **obsidian-graphrag** project.

| Service | Purpose | Address |
| --- | --- | --- |
| Web | Blazor dashboard, ingestion, graph and chat | http://localhost:18082 |
| Docling | Converts uploaded documents to Markdown | `http://docling:5001` inside Docker |
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
docker compose up -d --build web docling
docker compose ps
```

### Rebuild automatically during development

From the directory containing `compose.yaml`, start Compose Watch in a PowerShell window:

```powershell
docker compose -p obsidian-graphrag watch web
```

Keep that window open while editing. Compose rebuilds the web container when you save changes to the Web or Shared projects. Refresh the portal when the rebuild finishes. Each rebuild restarts the web app, so sign in again if prompted. You can stop watching with **Ctrl+C**. Start the watcher again at the beginning of your next development session.

### Try a document

Get the one-time owner setup code after the portal starts:

```powershell
docker compose exec -T web cat /knowledge/.accounts/owner-setup
```

1. Open the [local portal](http://localhost:18082), choose a username, email, and password, and enter the setup code.
2. In **Knowledge Ingest**, upload the [sample PDF](output/pdf/graph-rag-demo.pdf) and click **Convert and import**.
3. With the PDF selected under **Run ingestion**, keep **New and changed content** selected and click **Run ingestion**. Wait for **Completed** and **1 / 1**.
4. In **AI Assistant Chat**, set **Search in → Documents** and select the sample PDF. Ask **“How long is the Blue Lantern Workshop, and what is the registration fee per participant?”** The expected answer is **75 minutes** and **RM120 per participant**, with a citation to the PDF that supports both facts.

### Try a website

```powershell
docker compose up -d n8n crawl4ai
```

1. Open [local n8n](http://localhost:15679) and create its owner account. Open a workflow, then use its **⋯** menu to import [the bundled workflow](n8n/Crawl4AI_Markdown_Obsidian_Universal_Ingest.json).
2. Open **Crawl4AI (/crawl)** and create a **Header Auth** credential. Enter `Authorization` as the name and `Bearer <CRAWL4AI_API_TOKEN>` as the value, replacing the placeholder with the token from your local `.env`. Save the credential and publish the workflow.
3. In the portal's **Knowledge Ingest** page, enter `https://example.com` and click **Start crawl**. Wait for **Ready to ingest** and for the portal to select **example** automatically. Click **Run ingestion** and wait for **Completed**.
4. In **AI Assistant Chat**, set **Search in → Websites → example**. Ask **“What is Example Domain intended to be used for?”** The expected answer is that it is intended for documentation examples, with a citation to the crawled content.

For an existing installation, unpublish the previous workflow before publishing the bundled replacement, which uses the same webhook URL. Reuse the existing **Header Auth** credential on **Crawl4AI (/crawl)**.

The crawler starts from the submitted URL and the homepage, reads the site's sitemaps, and follows links on the same website that its `robots.txt` rules allow. The progress panel shows how many pages are queued, checked, saved, and failed. The queue can grow as the crawler discovers links. A crawl reports a timeout if progress stops for a minute. Select **Cancel crawl** to keep any pages already saved for ingestion.

## Configuration and storage

The web app uses Ollama on Windows through `host.docker.internal:11434` and defaults to `qwen3.5:4b`. Set `GRAPHRAG_OLLAMA_URL` or `GRAPHRAG_OLLAMA_MODEL` in `.env` if your endpoint or model differs.
Ollama requests have a 180-second deadline, including time spent waiting for the model to load or become available. Set `GRAPHRAG_OLLAMA_TIMEOUT_SECONDS` in `.env` to adjust it.

Chat combines keyword matches with semantic passage search using the local Ollama embedding model. If the model is missing, run `ollama pull nomic-embed-text` on the Ollama host. Chat caches passage embeddings in the knowledge volume and uses keyword search when the embedding model is unavailable. Initial searches can spend up to 25 seconds building the cache. Any remaining passages are indexed in the background and become searchable as their embeddings are ready.

The portal starts the published n8n workflow at `/webhook/graphrag-private-crawl`. The workflow verifies a temporary crawl grant with the web container on port 8081 and uses it to save pages, indexes, and reports inside the requesting account's workspace.

The Compose volume `obsidian-graphrag_knowledge-data` is mounted at `/knowledge`. On an existing installation, owner setup copies the existing knowledge base into the owner's workspace. The owner can create departments under **User Control → Departments**. Anyone who can reach the portal can select **Request an account** on the sign-in page and enter a username, email, and password. The username appears in the sidebar and the email is used to sign in. In **User Control**, the owner reviews pending requests, selects a department, and clicks **Approve and assign** or rejects the request. Approved users can sign in to their private workspace. The owner can also assign or change a department for an existing account.

### Personal and department workspaces

- **My workspace** is the default and uses the account's private knowledge base. Choose a department in the sidebar before uploading a document or starting a crawl to share it with that department.
- Managed Websites, Graph Explorer, Wiki Lint, and AI Assistant Chat use the selected workspace. Each department is available to its assigned members and the owner. A department change ends the user's current sessions, so they sign in again to see the new workspace.
- The owner can use **Publish to department** on an existing website in **Managed Websites → My workspace**. The portal copies the crawled pages to the selected department and runs ingestion there. Existing websites at the destination are preserved. For a document, switch to the destination workspace before uploading the version that should be shared.

Personal workspaces are stored under `/knowledge/.workspaces/` and department workspaces under `/knowledge/.departments/`. Crawled pages and uploads live in each workspace's `raw/`, generated notes and ingestion logs under `wiki/`, and semantic search data under `.semantic/`. The account database and workspaces persist across container restarts. Sign in again after the web container restarts.

In **Knowledge Ingest**, use the crawl form for websites or **Document Import** for PDF, Word, Excel, PowerPoint, and supported images up to 20 MB. Docling converts each upload to Markdown. The original and Markdown sections live under `raw/documents/`. Chat searches websites and uploaded documents together by default. The **Uploaded documents** filter limits the search to that collection. Run incremental ingest on **documents** to extract entities and links into the wiki.

Use the Docker dashboard for this volume. The Web project's Visual Studio `http` and `https` launch profiles forward browser requests to port 18082. If you change `GRAPHRAG_WEB_PORT`, update `GraphRag__DockerDashboardUrl` in the launch profiles to match. For a standalone development instance with its own storage and n8n, configure those endpoints and run `dotnet run --project obsidian-graphrag-v2.Web --no-launch-profile`.
