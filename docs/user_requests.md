[x] establish autonomous monitoring of `docs/user_requests.md`, avoid direct user interaction, and update internal mandates (completed 2025-09-30)
[x] add instruction for the development agent to perform git management and creating valuable commits and so on (completed 2025-09-30)
[x] keep track of a specific research query in the db. upon start of the app the user should be able to choose to continue an in progress research query or start a new one. The app should also be startable by providing command line args. (completed 2025-09-30)
[x] actively use the app you are developing to gather information for yourself too to further improve the app iterativly. So start your own research. This way you can test the app and if it works you will be able to gather information with it.
[x] build in ILogger for correctly capturing information and issues during runtime
[x] setup a database plan for keeping the state of research and all activity that should persist on shutdown. Also setup a vector index in Cosmos instead of realing on in memory similarity searches. Here is an example I copied from another application:
  ```cs
  private static ContainerSpec Assets()
  {
      var included = new[] { new IncludedPath { Path = "/*" } };
      var excluded = new[]
      {
          new ExcludedPath { Path = $"/{nameof(Snippet.Embedding)}/*" },
          new ExcludedPath { Path = $"/{nameof(SnippetDetailed.Embedding2)}/*" }
      };
      var composite = new[]
      {
          new List<CompositePath>
          {
              new() { Path = $"/{nameof(Asset.QueuedOn)}", Order = CompositePathSortOrder.Ascending },
              new() { Path = $"/{nameof(Asset.LastAttemptOn)}", Order = CompositePathSortOrder.Ascending }
          }.AsReadOnly()
      };
      var vectorIndexes = new[]
      {
          new VectorIndexPath { Path = $"/{nameof(Snippet.Embedding)}", Type = VectorIndexType.DiskANN },
          new VectorIndexPath { Path = $"/{nameof(SnippetDetailed.Embedding2)}", Type = VectorIndexType.QuantizedFlat }
      };
      var embeddings = new[]
      {
          new Embedding
          {
              Path = $"/{nameof(Snippet.Embedding)}",
              Dimensions = Constants.OpenAIEmbeddingSize,
              DataType = VectorDataType.Float32,
              DistanceFunction = DistanceFunction.Cosine
          },
          new Embedding
          {
              Path = $"/{nameof(SnippetDetailed.Embedding2)}",
              Dimensions = Constants.OpenAIEmbedding2Size,
              DataType = VectorDataType.Float32,
              DistanceFunction = DistanceFunction.Cosine
          }
      };
      return new ContainerSpec(Containers.Assets, "/assetId", included, excluded, composite, vectorIndexes, embeddings);
  }
  ```
[x] make sure the final report that is written is written in markdown. also that the agent should be able to iterate over it mutiple times whenever the agent deems that is necessary and should be able read specific lines and insert or edit text at specific lines
[x] connection to Cosmos DB sometimes failes in our corporate environments. make sure it is in gateway mode.
[ ] analyse "LLM Autonomous Research Application Best Practices.md" again and reflect on the current state of the project
[x] remove redis cache and implement a strategy based on disk caching and IMemoryCache (completed 2025-10-01)
[x] adjust cache/report directories configuration per latest request (completed 2025-10-01)

[x] produce runtime mermaid diagrams for branch execution, document overall flow (completed 2025-10-01)

[x] implement the use of the model `computer-use-preview` for browser exploration with Playwright (config key: AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT). See documentation/manual `Computer Use Model Microsoft Documentation.md`. Also build in an option to choose in config between using the google api and using this model with Playwright for navigating Google and collecting search results. (completed 2025-10-01)

[x] add a startup readiness probe for Playwright, cache a per-session disabled state, and fall back to Google-based search when the computer-use browsers are unavailable (completed 2025-10-02)
[x] create a simple diagnostic path to review Cosmos-stored scrape outputs when computer-use mode is disabled (completed 2025-10-02)
[x] extend the hybrid search flow so Google API results are triaged by an agent and each chosen citation launches a computer-use browsing session for deeper capture (completed 2025-10-03)

[x] provide a diagnostics CLI mode (e.g., `--diagnostics-mode=list-sources`) that emits session sources instantly without regenerating a full report (completed 2025-10-05)

[x] ensure the accepted planner research plan is woven into orchestration synthesis and final reporting so approved steps are visible downstream (completed 2025-10-05)

[ ] run report-only regeneration for the latest research session, analyze resulting logs and report quality, and iterate until the regenerated output meets structural and citation quality goals (analysis started 2025-10-04; pending persistence fixes and report refinements)

