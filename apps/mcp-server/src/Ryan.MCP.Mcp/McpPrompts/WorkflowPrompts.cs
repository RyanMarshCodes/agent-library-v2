using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpPrompts;

[McpServerPromptType]
public sealed class WorkflowPrompts
{
    [McpServerPrompt(Name = "new_feature_workflow")]
    [Description("Create a guided spec-based feature development sequence from a feature request.")]
    public ChatMessage NewFeatureWorkflow(
        [Description("Feature request or user story.")] string featureRequest,
        [Description("Optional stack hint, e.g. 'dotnet api + angular'.")] string? stackHint = null)
    {
        if (string.IsNullOrWhiteSpace(featureRequest))
        {
            return new ChatMessage(
                ChatRole.User,
                "Feature request is required. Provide a user story or feature description.");
        }

        var stack = string.IsNullOrWhiteSpace(stackHint) ? "not specified" : stackHint.Trim();
        var content = $"""
Use this command sequence for spec-based delivery.

Stack: {stack}
Feature: {featureRequest}

1) /context - Analyze repo context for this feature.
2) /spec - Write the feature specification with acceptance criteria.
3) /validate - Validate the specification quality.
4) /architect - Produce ARD/ADR and implementation tasks.
5) /validate - Validate architecture and task sequencing.
6) /implement - Implement tasks in small diffs.
7) /test - Add tests and coverage improvements.
8) /reflect - Self-review against spec and ARD.
9) /review - Perform multi-agent review.
10) /commit - Generate conventional commit + PR description.
""";

        return new ChatMessage(ChatRole.User, content);
    }

    [McpServerPrompt(Name = "proceed_workflow")]
    [Description("Generate an end-to-end /proceed style pipeline plan with validation gates.")]
    public ChatMessage ProceedWorkflow(
        [Description("Feature request, requirement id, or pipeline target.")] string target,
        [Description("Optional stack hint.")] string? stackHint = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ChatMessage(ChatRole.User, "Target is required for /proceed workflow.");
        }

        var stack = string.IsNullOrWhiteSpace(stackHint) ? "not specified" : stackHint.Trim();
        var content = $"""
Use /proceed for an end-to-end gated pipeline.

Stack: {stack}
Target: {target}

Pipeline gates:
1) /context
2) /spec
3) /validate (spec gate)
4) /architect
5) /validate (architecture/tasks gate)
6) /implement
7) /test
8) /reflect
9) /review
10) /commit
11) /wrapup

Rules:
- Do not advance gates when blockers exist.
- Fix blocker findings, then re-run /validate.
- Keep diffs small and verifiable.
""";

        return new ChatMessage(ChatRole.User, content);
    }

    [McpServerPrompt(Name = "workflow_step")]
    [Description("Generate a focused workflow-step prompt for commands like /spec, /validate, /architect, /review.")]
    public ChatMessage WorkflowStep(
        [Description("Workflow command, e.g. '/spec', '/review'.")] string command,
        [Description("Context to analyze for this step.")] string context)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ChatMessage(ChatRole.User, "Workflow command is required.");
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return new ChatMessage(ChatRole.User, "Workflow context is required.");
        }

        var normalized = command.Trim().StartsWith('/') ? command.Trim() : "/" + command.Trim();
        return new ChatMessage(
            ChatRole.User,
            $"{normalized} - Execute this workflow step against the context below. Return concise, actionable output.\n\nContext:\n{context}");
    }
}
