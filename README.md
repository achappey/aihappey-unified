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

## Protocol fidelity

The Responses, Chat Completions, Messages, and Interactions mappers retain native request/response envelope fields in protocol-prefixed unified metadata. Normalized controls remain authoritative; native-only settings and extension fields are restored when mapping back to their protocol. Native metadata is retained separately from the captured envelope. Existing provider-scoped usage/replay metadata is still retained. Request mapping continues to set `store=false`; it does not opt callers into server-side storage.

| Protocol | Covered fidelity improvements |
|---|---|
| Responses | Reasoning and inclusion options, tool-call limits, cache/continuation controls, response identity and incomplete/error details, usage extensions including nested token details, image URL/file-ID/detail preservation, text logprobs/cache breakpoints, refusals, and opaque native input replay. |
| Chat Completions | Native sampling/reasoning/audio/cache options, modern versus legacy token-limit fields, headers, message/audio/refusal/legacy-function fields, custom-tool text inputs, multimodal parts and extensions, response identity/multiple choices/logprobs, and native streaming usage details. |
| Messages | Structured/cache-controlled system prompts, native thinking/output/cache/container options, deferred-tool controls, response identity/stop details/extensions, ordered text/reasoning/tool blocks, and initial streaming reasoning signatures. |
| Interactions | Model versus agent identity, generation/thinking/multimodal configuration, continuation/environment/labels/safety options, response identity/extensions, normalized usage on export, and reverse-stream status/usage/signature mapping. |

These are native fidelity guarantees for the mapped fields, not a claim that different protocols have interchangeable semantics. Opaque Responses input items/content replay only to the original provider. Unknown Interactions steps/content and unknown streaming event types still have no general normalized semantic mapping. Interactions native usage remains available as its contract object; exporting `AIUsage` also maps its corresponding counters.

Existing tool approval events, client-side versus provider-executed tool behavior, and explicit per-stream state ownership are unchanged. Synthetic Chat Completions finishes retain their existing finish-reason spelling and null usage behavior; usage captured from a native Chat Completions chunk can be replayed. Vercel mappings are unchanged.

The protocol audit used the reusable sibling contract libraries and the latest official generated schemas available on 2026-11-03: OpenAI's [Responses request schema](https://github.com/openai/openai-python/blob/f6e8480f9a5b2a4d02b17a1da54a1e57964bc312/src/openai/types/responses/response_create_params.py) and [Chat Completions request schema](https://github.com/openai/openai-python/blob/f6e8480f9a5b2a4d02b17a1da54a1e57964bc312/src/openai/types/chat/completion_create_params.py), Anthropic's [Messages schema](https://github.com/anthropics/anthropic-sdk-python/blob/5752c132041af71055584632caab5ca2325cae5e/src/anthropic/types/message_create_params.py), and Google's [Interactions schema](https://github.com/googleapis/python-genai/blob/2eaf2d84e6cc3714eb5b5669fc26a2a1f5edf6c9/google/genai/_gaos/types/interactions/createmodelinteraction.py). SDK schemas were used because the documentation websites were unreachable from the audit environment.

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
