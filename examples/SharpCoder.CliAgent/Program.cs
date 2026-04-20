using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OllamaSharp;

using SharpCoder;
using SharpCoder.CliAgent;

var modelOption = new Option<string>("--model", "-m")
{
    Description = "Ollama Cloud model id (e.g. gpt-oss:120b, qwen3-coder:480b).",
    DefaultValueFactory = _ => "gpt-oss:120b"
};

var reasoningOption = new Option<ReasoningLevel>("--reasoning", "-r")
{
    Description = "Reasoning effort: None, Low, Medium, or High.",
    DefaultValueFactory = _ => ReasoningLevel.Medium
};

var workDirOption = new Option<string>("--work-dir", "-w")
{
    Description = "Directory to create and use as the project root. Fails if it already exists.",
    Required = true
};

var assignmentOption = new Option<FileInfo>("--assignment", "-a")
{
    Description = "Path to a markdown file containing the assignment for the agent.",
    Required = true
};

var logDirOption = new Option<string>("--log-dir", "-l")
{
    Description = "Directory where per-run log files are written (created if missing).",
    DefaultValueFactory = _ => "logs"
};

var maxStepsOption = new Option<int>("--max-steps", "-s")
{
    Description = "Maximum number of agent iterations.",
    DefaultValueFactory = _ => 50
};

var contextWindowOption = new Option<int>("--context-window", "-c")
{
    Description = "Model context-window size in tokens. Used for auto-compaction decisions. Typical values: 131072 (128k, most modern models), 262144 (Qwen3-Coder), 1048576 (Gemini 1.5).",
    DefaultValueFactory = _ => 131_072
};

var rootCommand = new RootCommand("SharpCoder CLI Agent — run coding assignments against Ollama Cloud models and capture a full log per run for side-by-side comparison.")
{
    modelOption,
    reasoningOption,
    workDirOption,
    assignmentOption,
    logDirOption,
    maxStepsOption,
    contextWindowOption
};

rootCommand.SetAction((parseResult, ct) => RunAsync(
    model:         parseResult.GetValue(modelOption)!,
    reasoning:     parseResult.GetValue(reasoningOption),
    workDirArg:    parseResult.GetValue(workDirOption)!,
    assignment:    parseResult.GetValue(assignmentOption)!,
    logDirArg:     parseResult.GetValue(logDirOption)!,
    maxSteps:      parseResult.GetValue(maxStepsOption),
    contextWindow: parseResult.GetValue(contextWindowOption),
    ct:            ct));

return await rootCommand.Parse(args).InvokeAsync();

static async Task<int> RunAsync(
    string model,
    ReasoningLevel reasoning,
    string workDirArg,
    FileInfo assignment,
    string logDirArg,
    int maxSteps,
    int contextWindow,
    CancellationToken ct)
{
    // Ollama Cloud API key
    var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("Error: OLLAMA_API_KEY environment variable is not set.");
        Console.Error.WriteLine("Create an API key at https://ollama.com/settings/keys");
        return 2;
    }

    // Validate assignment file
    if (!assignment.Exists)
    {
        Console.Error.WriteLine($"Error: assignment file not found: {assignment.FullName}");
        return 2;
    }
    var assignmentText = await File.ReadAllTextAsync(assignment.FullName, ct);

    // Create (exclusive) work directory
    var workDir = Path.GetFullPath(workDirArg);
    if (Directory.Exists(workDir) || File.Exists(workDir))
    {
        Console.Error.WriteLine($"Error: work directory already exists: {workDir}");
        Console.Error.WriteLine("Choose a fresh --work-dir to avoid clobbering a previous run.");
        return 2;
    }
    Directory.CreateDirectory(workDir);

    // Prepare log directory + file
    var logDir = Path.GetFullPath(logDirArg);
    Directory.CreateDirectory(logDir);

    var startedAt = DateTime.Now;
    var logFileName = BuildLogFileName(startedAt, model, reasoning, workDir);
    var logFilePath = Path.Combine(logDir, logFileName);

    using var fileLogProvider = new FileLoggerProvider(logFilePath);
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.AddProvider(fileLogProvider);
        builder.SetMinimumLevel(LogLevel.Information);
    });
    var logger = loggerFactory.CreateLogger<CodingAgent>();

    // Write log header
    WriteHeader(fileLogProvider, startedAt, model, reasoning, workDir, assignment.FullName, assignmentText, maxSteps, contextWindow, logFilePath);

    Console.WriteLine($"Model:       {model}");
    Console.WriteLine($"Reasoning:   {reasoning}");
    Console.WriteLine($"Work dir:    {workDir}");
    Console.WriteLine($"Assignment:  {assignment.FullName}");
    Console.WriteLine($"Context:     {contextWindow:N0} tokens");
    Console.WriteLine($"Log file:    {logFilePath}");
    Console.WriteLine();

    // Build Ollama Cloud chat client
    using var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com") };
    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var ollamaClient = new OllamaApiClient(httpClient) { SelectedModel = model };
    IChatClient chatClient = ollamaClient;

    var agentOptions = new AgentOptions
    {
        WorkDirectory = workDir,
        EnableBash = true,
        EnableFileOps = true,
        EnableFileWrites = true,
        MaxSteps = maxSteps,
        Logger = logger,
        MaxContextTokens = contextWindow,
        ReasoningEffort = MapReasoning(reasoning),
        OnCompacting = () =>
        {
            fileLogProvider.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Compaction ] Compacting context…");
            Console.WriteLine("Compacting context…");
        },
        OnCompacted = r =>
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Compaction ] Compacted: "
                + $"{r.MessagesBefore}→{r.MessagesAfter} msgs, "
                + $"{r.TokensBefore}→{r.TokensAfter} tokens "
                + $"({r.ReductionPercent}% reduction)";
            fileLogProvider.WriteLine(line);
            Console.WriteLine($"Compacted {r.TokensBefore}→{r.TokensAfter} tokens ({r.ReductionPercent}% reduction)");
        }
    };

    var agent = new CodingAgent(chatClient, agentOptions);

    AgentResult? result = null;
    Exception? failure = null;
    try
    {
        await foreach (var update in agent.ExecuteStreamingAsync(null, assignmentText, ct))
        {
            if (update.Kind == StreamingUpdateKind.Completed)
            {
                result = update.Result;
            }
        }
    }
    catch (Exception ex)
    {
        failure = ex;
        logger.LogError(ex, "Agent run failed with an unhandled exception.");
    }
    finally
    {
        WriteFooter(fileLogProvider, startedAt, result, failure);
    }

    if (failure != null)
    {
        Console.Error.WriteLine($"\nAgent run failed: {failure.Message}");
        return 1;
    }

    Console.WriteLine($"\nStatus:        {result!.Status}");
    Console.WriteLine($"Finish reason: {result.FinishReason}");
    Console.WriteLine($"Tool calls:    {result.ToolCallCount}");
    Console.WriteLine($"Tokens:        {result.Usage?.TotalTokenCount}");
    Console.WriteLine($"\nFinal message:\n{result.Message}");
    return 0;
}

static ReasoningEffort? MapReasoning(ReasoningLevel level) => level switch
{
    ReasoningLevel.None   => null,
    ReasoningLevel.Low    => ReasoningEffort.Low,
    ReasoningLevel.Medium => ReasoningEffort.Medium,
    ReasoningLevel.High   => ReasoningEffort.High,
    _                     => null
};

static string BuildLogFileName(DateTime startedAt, string model, ReasoningLevel reasoning, string workDir)
{
    var timestamp = startedAt.ToString("yyyy-MM-ddTHH-mm-ss");
    var dirName = Path.GetFileName(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return $"{timestamp}_{Sanitize(model)}_{reasoning}_{Sanitize(dirName)}.log";
}

static string Sanitize(string value)
{
    var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', ' ', '/', '\\' }).ToArray();
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
    {
        sb.Append(invalid.Contains(c) ? '-' : c);
    }
    return sb.ToString();
}

static void WriteHeader(
    FileLoggerProvider log,
    DateTime startedAt,
    string model,
    ReasoningLevel reasoning,
    string workDir,
    string assignmentPath,
    string assignmentText,
    int maxSteps,
    int contextWindow,
    string logFilePath)
{
    log.WriteLine("============================================================");
    log.WriteLine($"  SharpCoder CliAgent run");
    log.WriteLine("============================================================");
    log.WriteLine($"Started at:   {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
    log.WriteLine($"Model:        {model}");
    log.WriteLine($"Reasoning:    {reasoning}");
    log.WriteLine($"Work dir:     {workDir}");
    log.WriteLine($"Max steps:    {maxSteps}");
    log.WriteLine($"Context:      {contextWindow:N0} tokens");
    log.WriteLine($"Assignment:   {assignmentPath}");
    log.WriteLine($"Log file:     {logFilePath}");
    log.WriteLine();
    log.WriteLine("------------------ Assignment ------------------------------");
    log.WriteLine(assignmentText);
    log.WriteLine("------------------ Agent log -------------------------------");
}

static void WriteFooter(FileLoggerProvider log, DateTime startedAt, AgentResult? result, Exception? failure)
{
    var finishedAt = DateTime.Now;
    var duration = finishedAt - startedAt;

    log.WriteLine("------------------ Run summary -----------------------------");
    log.WriteLine($"Finished at:  {finishedAt:yyyy-MM-dd HH:mm:ss zzz}");
    log.WriteLine($"Duration:     {duration}");

    if (failure != null)
    {
        log.WriteLine("Outcome:      FAILED");
        log.WriteLine("Exception:");
        log.WriteLine(failure.ToString());
    }

    if (result != null)
    {
        log.WriteLine($"Status:         {result.Status}");
        log.WriteLine($"Finish reason:  {result.FinishReason}");
        log.WriteLine($"Tool calls:     {result.ToolCallCount}");
        log.WriteLine($"Model id:       {result.ModelId}");
        if (result.Usage != null)
        {
            log.WriteLine($"Tokens in:      {result.Usage.InputTokenCount}");
            log.WriteLine($"Tokens out:     {result.Usage.OutputTokenCount}");
            log.WriteLine($"Tokens total:   {result.Usage.TotalTokenCount}");
        }

        if (result.Diagnostics is { } d)
        {
            log.WriteLine();
            log.WriteLine("------------------ Diagnostics -----------------------------");
            log.WriteLine($"Work dir:             {d.WorkDirectory}");
            log.WriteLine($"Bash enabled:         {d.EnableBash}");
            log.WriteLine($"File writes enabled:  {d.EnableFileWrites}");
            log.WriteLine($"Workspace docs:       {d.AutoLoadedWorkspaceInstructions}");
            log.WriteLine($"Skills enabled:       {d.SkillsEnabled}");
            log.WriteLine($"Reasoning effort:     {d.ReasoningEffort}");
            log.WriteLine($"Max steps:            {d.MaxSteps}");
            log.WriteLine($"Tool names:           {string.Join(", ", d.ToolNames)}");
            log.WriteLine($"Session history:      {d.SessionHistoryCount}");
            log.WriteLine($"Messages to LLM:      {d.TotalMessageCount}");
            log.WriteLine();
            log.WriteLine("System prompt:");
            log.WriteLine(d.SystemPrompt);
        }

        log.WriteLine();
        log.WriteLine("------------------ Final message ---------------------------");
        log.WriteLine(result.Message);

        log.WriteLine();
        log.WriteLine("------------------ Full message history --------------------");
        var i = 0;
        foreach (var msg in result.Messages)
        {
            log.WriteLine($"--- [{i++}] {msg.Role} ---");
            var text = msg.Text;
            if (!string.IsNullOrEmpty(text))
            {
                log.WriteLine(text);
            }

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        log.WriteLine($"[tool call] {call.Name} (callId={call.CallId})");
                        if (call.Arguments is { Count: > 0 })
                        {
                            log.WriteLine("  args: " + JsonSerializer.Serialize(call.Arguments));
                        }
                        break;
                    case FunctionResultContent fr:
                        log.WriteLine($"[tool result] callId={fr.CallId}");
                        log.WriteLine("  " + (fr.Result?.ToString() ?? "<null>"));
                        break;
                    case TextContent:
                        // already printed via msg.Text
                        break;
                    default:
                        log.WriteLine($"[content] {content.GetType().Name}");
                        break;
                }
            }
        }
    }
    log.WriteLine("============================================================");
}

internal enum ReasoningLevel
{
    None,
    Low,
    Medium,
    High
}
