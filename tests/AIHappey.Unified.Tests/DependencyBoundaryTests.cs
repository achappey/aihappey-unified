using System.Reflection;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Unified.Tests;

public sealed class DependencyBoundaryTests
{
    private static readonly Assembly Unified = typeof(AIRequest).Assembly;

    [Theory]
    [InlineData(typeof(ChatCompletionsUnifiedMapper))]
    [InlineData(typeof(MessagesUnifiedMapper))]
    [InlineData(typeof(MessagesUsageExtensions))]
    [InlineData(typeof(ResponsesUnifiedMapper))]
    [InlineData(typeof(InteractionsUnifiedMapper))]
    [InlineData(typeof(VercelUnifiedMapper))]
    [InlineData(typeof(VercelUnifiedRequestExtensions))]
    public void Models_and_mappers_have_one_assembly_owner(Type mapper)
        => Assert.Same(Unified, mapper.Assembly);

    [Fact]
    public void Only_the_five_reusable_contracts_are_internal_product_dependencies()
    {
        var references = Unified.GetReferencedAssemblies()
            .Where(reference => reference.Name!.StartsWith("AIHappey.", StringComparison.Ordinal))
            .Select(reference => reference.Name!).Order().ToArray();
        Assert.Equal(new[] { "AIHappey.ChatCompletions", "AIHappey.Interactions", "AIHappey.Messages",
            "AIHappey.Responses", "AIHappey.Vercel" }, references);
        Assert.Empty(Unified.GetCustomAttributes<InternalsVisibleToAttribute>());
        Assert.DoesNotContain(Unified.GetReferencedAssemblies(), reference =>
            reference.Name is "OpenAI" or "Anthropic.SDK" or "Mscc.GenerativeAI" or "ModelContextProtocol.AspNetCore");
    }

    [Theory]
    [InlineData(typeof(ChatCompletionOptions))]
    [InlineData(typeof(MessagesRequest))]
    [InlineData(typeof(ResponseRequest))]
    [InlineData(typeof(InteractionRequest))]
    [InlineData(typeof(ChatRequest))]
    public void Contracts_do_not_reference_unified_or_gateway_assemblies(Type contract)
        => Assert.DoesNotContain(contract.Assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("AIHappey.", StringComparison.Ordinal));

    [Fact]
    public void Mappers_do_not_store_ambient_or_global_stream_correlations()
    {
        var fields = Unified.GetTypes().SelectMany(type =>
            type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        Assert.DoesNotContain(fields, field => field.FieldType.IsGenericType
            && field.FieldType.GetGenericTypeDefinition() is var definition
            && (definition == typeof(AsyncLocal<>)
                || definition == typeof(ThreadLocal<>)
                || definition == typeof(Dictionary<,>)
                || definition == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>)));
    }

    [Fact]
    public void Existing_public_surface_is_preserved_with_only_the_intended_usage_conversion()
    {
        var baseline = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicApi.Baseline.txt"));
        var actual = PublicSurface(Unified.GetExportedTypes()).ToArray();
        Assert.Empty(baseline.Except(actual, StringComparer.Ordinal));
        var addition = Assert.Single(actual.Except(baseline, StringComparer.Ordinal));
        Assert.Equal("AIHappey.ChatCompletions.Mapping.ChatCompletionsUnifiedMapper | Method | "
            + "AIHappey.ChatCompletions.Models.ChatCompletionUsage ToChatCompletionUsage(AIHappey.Unified.Models.AIUsage) | usage", addition);
    }

    private static IEnumerable<string> PublicSurface(IEnumerable<Type> types)
        => types.SelectMany(type => new[] { "Type: " + type.FullName }
            .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => member is not Type)
                .Select(member => type.FullName + " | " + member.MemberType + " | " + member
                    + (member is MethodBase method
                        ? " | " + string.Join(", ", method.GetParameters().Select(parameter =>
                            parameter.Name + (parameter.IsOptional ? "=" + (parameter.DefaultValue ?? "null") : "")))
                        : ""))))
            .Order(StringComparer.Ordinal);
}
