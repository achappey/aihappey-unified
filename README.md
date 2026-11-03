# aihappey-unified

Reusable **.NET unified AI models** and **bidirectional protocol mappers** for Responses, Chat Completions, Messages, Interactions and Vercel.
Requests, responses, streaming, reasoning, tools, multimodal content and metadata.

## Quick start
Convert native protocol responses into Unified models and back.
```csharp
using AIHappey.Responses.Mapping;

var unified = response.ToUnifiedResponse("openai");
var native = unified.ToResponse("openai");
```
## Build and test
Requires .NET 11 and sibling protocol contract repositories.
```bash
dotnet test AIHappey.Unified.slnx -c Release
```
## Supported protocols
- OpenAI Responses
- OpenAI Chat Completions
- Anthropic Messages
- Google Interactions
- Vercel AI SDK UI Messages