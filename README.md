# AIHappey.Unified

Unified AI models and conversions to and from the reusable Responses, Chat Completions, Messages, Interactions, and Vercel contracts. This library owns the existing request, response, content, usage, replay, and streaming mappings; it does not execute providers or tools.

## Boundary

- Backend/gateway → Unified → the five contract libraries. Contracts never reference Unified.
- Model namespaces remain `AIHappey.Unified.Models`; mapping namespaces remain `AIHappey.Responses.Mapping`, `AIHappey.ChatCompletions.Mapping`, `AIHappey.Messages.Mapping`, `AIHappey.Interactions.Mapping`, and `AIHappey.Vercel.Mapping` / `AIHappey.Vercel.Extensions`.
- Providers, HTTP/SSE transports, authentication, pricing, model catalogs, and gateway orchestration stay outside this library.
- Provider-specific fields and tool representations are preserved by the existing conversions. This is not a promise of lossless translation of every field between different protocols.
- Implementation helpers stay private/internal. There are no friend assemblies. The only additional public conversion introduced by extraction is normalized `AIUsage` → `ChatCompletionUsage`; legacy/raw adapters stay internal to the backend.
- MCP content/result types require `ModelContextProtocol.Core`. Some existing contract projects also depend on MCP ASP.NET Core packages transitively; extraction does not change those dependencies.

For Vercel file parts, string payloads (including JSON strings) are preserved verbatim, not inferred to be base64: ordinary text and relative references can also be valid base64. Supply a complete Data URL or use `byte[]`, `Memory<byte>`, or `ReadOnlyMemory<byte>` to explicitly encode binary content as a Data URL.

## Stateless streaming

There is no global or ambient request state, session store, provider lookup, or dependency-injection setup. DTO collections remain mutable; statelessness does not mean immutable DTOs or that streaming requires no accumulation.

Allocate one mapping-state object per logical stream and direction. Never reuse it concurrently for another stream, even if provider IDs or tool IDs differ. For Responses:

```csharp
var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
foreach (var part in responseParts)
foreach (var mapped in part.ToUnifiedStreamEvent(providerId, state))
    Consume(mapped);
```

The no-state single-event overloads are independent calls, **not** a multi-event stream session. Callers mapping a sequence must retain and pass explicit state. Existing sequence APIs allocate their state per enumeration, honor cancellation, and dispose their sources. Responses terminal events and abandoned/failed chunk enumerations clear only their own state. Discard caller-owned state when a source is canceled or abandoned before a terminal event; it is never retained by a static cache. Existing generated-ID and timestamp fallbacks are unchanged.

## Build and test

Use the .NET 11 SDK. For source development, clone the following as sibling directories: `aihappey-unified`, `aihappey-responses`, `aihappey-chatcompletions`, `aihappey-messages`, `aihappey-interactions`, and `aihappey-vercel`. The AI backend is not required.

```shell
dotnet test AIHappey.Unified.slnx -c Release
dotnet pack AIHappey.Unified.csproj -c Release -o artifacts
pwsh -File tests/Verify-PackageConsumption.ps1
```

The package check packs the six projects into a temporary local feed and restores/runs an isolated consumer with a private package cache, no project references, no linked source, and no backend checkout. It exercises all five protocol mapping surfaces and checks the resolved dependency graph. It does not publish packages.

The standalone test suite includes the protocol-only regressions moved from the backend, explicit stream-isolation tests, dependency/ownership checks, and a snapshot of the pre-extraction public surface. Provider/gateway integration regressions remain in `aihappey-ai` and must also pass when integrating changes.

The mapper types now live in the `AIHappey.Unified` assembly rather than `AIHappey.Core`; existing namespaces and signatures are retained, but precompiled assembly-qualified mapper references must be rebuilt. The model assembly name remains unchanged.
