# SharpCoder.Providers

`SharpCoder.Providers` supplies the `IChatClient` factory used by SharpCoder. It
creates clients for GitHub Copilot, GitHub Models, and Ollama. Keep the
`SharpCoder` and `SharpCoder.Providers` package versions aligned when using them
together.

## Install

```bash
dotnet add package SharpCoder.Providers
```

## Create a client

Pass a provider-prefixed model string to select a provider explicitly. The
factory returns `Microsoft.Extensions.AI.IChatClient`:

```csharp
using SharpCoder.Providers;

using var client = ChatClientFactory.Create("copilot/<model>");
```

Replace `<model>` with the model identifier you want to use. Without a
recognized prefix, `LLM_PROVIDER` selects the provider (default: `copilot`),
and the remainder, if supplied, is treated as the model identifier.

## Providers and configuration

Supported prefixes are `copilot`, `github`, `ollama-cloud`, and `ollama-local`.
A provider prefix is case-insensitive; only these recognized prefixes before
`/` select a provider.

| Prefix | Credentials and environment variables | Default model when none is supplied |
| --- | --- | --- |
| `copilot` | A token from `SetTokenProvider`, `GH_TOKEN`, or `GITHUB_TOKEN` | `COPILOT_MODEL`, otherwise `claude-sonnet-4.6` |
| `github` (GitHub Models) | `GH_TOKEN`, then `GITHUB_TOKEN` | `GITHUB_MODEL`, otherwise `openai/gpt-4.1` |
| `ollama-cloud` | Required `OLLAMA_API_KEY` | `OLLAMA_MODEL`, otherwise `gpt-oss:120b` |
| `ollama-local` | `OLLAMA_URL` (otherwise `http://localhost:11434`) | `OLLAMA_MODEL`, otherwise `llama3` |

For Copilot, `ChatClientFactory.SetTokenProvider(Func<string?>)` takes
precedence when it returns a non-whitespace token; otherwise the factory checks
`GH_TOKEN`, then `GITHUB_TOKEN`. Whitespace values are treated as absent.
`ChatClientFactory.IsTokenAvailable()` reports whether a non-whitespace Copilot
token is available using that same precedence. The GitHub Models provider uses
`GH_TOKEN` and `GITHUB_TOKEN` only; it does not use the token provider.

## Copilot endpoint discovery

For Copilot requests, the factory can discover the account's advertised API
endpoint. Discovered endpoints are accepted only for HTTPS hosts at
`githubcopilot.com` or its subdomains; other values fall back to
`ChatClientFactory.DefaultCopilotApiEndpoint` (`https://api.githubcopilot.com`).
Discovery failures also fall back to that default. The public
`ChatClientFactory.GetCopilotApiEndpointAsync(string token, CancellationToken)`
exposes the lookup. GitHub Enterprise (`*.ghe.com`) is not supported.

## Built-in behavior

- Copilot and both Ollama providers use a resilience pipeline with up to three
  exponential-backoff retries and a 20-minute pipeline timeout; GitHub Models
  does not use this pipeline.
- Reasoning effort is mapped at the provider boundary: `extra_high` maps to
  `xhigh` for Copilot and `max` for Ollama; GitHub Models clamps it to `high`.
- Copilot request/response diagnostics are opt-in. Use
  `ChatClientFactory.SetDiagnosticsDirectory(path)` or set
  `SHARPCODER_DIAGNOSTICS_DIR`; an explicit method setting takes precedence.
  These diagnostic files are not produced by GitHub Models or either Ollama provider.

## License and source

Licensed under the [MIT License](https://github.com/robkaandorp/SharpCoder/blob/main/LICENSE).

[SharpCoder repository](https://github.com/robkaandorp/SharpCoder)
