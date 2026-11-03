using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class ToolApprovalMappingTests
{
    [Fact]
    public void Client_executed_tool_call_does_not_imply_an_approval_request()
    {
        var envelope = new AIEventEnvelope
        {
            Type = "tool-input-available",
            Id = "call-1",
            Data = new AIToolInputAvailableEventData
            {
                ToolName = "lookup",
                Input = new { query = "weather" },
                ProviderExecuted = false
            }
        };

        var parts = envelope.ToUIMessagePart("test").ToList();

        Assert.IsType<ToolCallPart>(Assert.Single(parts));
    }

    [Fact]
    public void Explicit_approval_request_preserves_reason_and_descriptor()
    {
        var descriptor = JsonSerializer.SerializeToElement(new
        {
            scope = "account",
            permissions = new[] { "read" }
        });
        var envelope = new AIEventEnvelope
        {
            Type = "tool-approval-request",
            Data = new AIToolApprovalRequestEventData
            {
                ApprovalId = "approval-1",
                ToolCallId = "call-1",
                Reason = "Review access",
                ApprovalDescriptor = descriptor
            }
        };

        var part = Assert.IsType<ToolApprovalRequestUIPart>(
            Assert.Single(envelope.ToUIMessagePart("test")));

        Assert.Equal("approval-1", part.ApprovalId);
        Assert.Equal("call-1", part.ToolCallId);
        Assert.Equal("Review access", part.Reason);
        Assert.True(JsonElement.DeepEquals(
            descriptor,
            JsonSerializer.SerializeToElement(part.ApprovalDescriptor)));

        var roundTrip = Assert.IsType<ToolApprovalRequestUIPart>(
            Assert.Single(VercelUnifiedMapper.ToUnifiedEvent(part).ToUIMessagePart("test")));
        Assert.Equal(part.Reason, roundTrip.Reason);
        Assert.True(JsonElement.DeepEquals(
            descriptor,
            JsonSerializer.SerializeToElement(roundTrip.ApprovalDescriptor)));
    }
}
