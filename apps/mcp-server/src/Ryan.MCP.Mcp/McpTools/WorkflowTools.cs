using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class WorkflowTools(ILogger<WorkflowTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly IReadOnlyDictionary<string, WorkflowDefinition> Workflows =
        new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["/context"] = new("/context", "Initialize project context", "Load standards, architecture, and recent context before workflow execution."),
            ["/init"] = new("/init", "Bootstrap workflow context", "Initialize or refresh workflow context and repository conventions."),
            ["/spec"] = new("/spec", "Write requirements from feature request", "Convert feature request into structured, testable requirements."),
            ["/validate"] = new("/validate", "Validate quality", "Validate spec quality or architecture/task quality before coding."),
            ["/architect"] = new("/architect", "Design ARD and tasks", "Design architecture decisions and break into implementation tasks."),
            ["/implement"] = new("/implement", "Implement tasks", "Generate and apply implementation changes task by task."),
            ["/test"] = new("/test", "Generate tests and coverage", "Generate meaningful unit/integration tests and identify coverage gaps."),
            ["/reflect"] = new("/reflect", "Self-review implementation", "Run self-review to catch requirement, correctness, and style misses."),
            ["/review"] = new("/review", "Multi-agent review", "Run a final quality, correctness, and security review before merge."),
            ["/commit"] = new("/commit", "Commit and PR prep", "Generate conventional commit and PR description with risks and validation."),
            ["/wrapup"] = new("/wrapup", "Feature closeout", "Finalize feature artifacts and capture durable lessons/assumptions."),
            ["/proceed"] = new("/proceed", "Run full pipeline", "Run end-to-end spec-based pipeline with validation gates."),
            ["/status"] = new("/status", "Workflow status", "Show current workflow state and recommended next command."),
            ["/bugfix"] = new("/bugfix", "Streamlined bug workflow", "Run report/analyze/fix/verify flow for defects."),
            ["/sprint"] = new("/sprint", "Parallel proceed orchestration", "Run multiple feature pipelines in parallel with isolation.")
        };

    [McpServerTool(Name = "list_workflows")]
    [Description("List available spec-based development workflow commands and what each one does.")]
    public string ListWorkflows(
        [Description("Optional search term to filter commands or goals.")] string? filter = null)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowTools.ListWorkflows",
            ["Filter"] = filter
        });

        var items = Workflows.Values
            .Where(w => string.IsNullOrWhiteSpace(filter)
                || w.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || w.Goal.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Command)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            count = items.Count,
            commands = items.Select(w => new
            {
                w.Command,
                w.Title,
                w.Goal
            }),
            hint = "Use get_workflow(command) for step-by-step guidance."
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_workflow")]
    [Description("Get detailed, step-by-step guidance for a workflow command like /spec or /review.")]
    public string GetWorkflow(
        [Description("Workflow command (e.g. '/spec', '/architect', '/review').")] string command)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowTools.GetWorkflow",
            ["Command"] = command
        });

        var key = NormalizeCommand(command);
        if (!Workflows.TryGetValue(key, out var workflow))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Unknown workflow '{command}'.",
                hint = "Use list_workflows() to discover valid commands."
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            workflow.Command,
            workflow.Title,
            workflow.Goal,
            steps = GetSteps(workflow.Command),
            next = GetNextCommands(workflow.Command)
        }, JsonOptions);
    }

    [McpServerTool(Name = "start_feature_workflow")]
    [Description("Start the full spec-based feature workflow from a feature description. Returns a concrete command sequence and copy-paste prompts.")]
    public string StartFeatureWorkflow(
        [Description("Feature request or user story to implement.")] string featureRequest,
        [Description("Optional tech stack hint, e.g. 'dotnet api + react'.")] string? stackHint = null)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowTools.StartFeatureWorkflow",
            ["HasStackHint"] = !string.IsNullOrWhiteSpace(stackHint)
        });

        if (string.IsNullOrWhiteSpace(featureRequest))
        {
            return JsonSerializer.Serialize(new
            {
                error = "featureRequest is required",
                hint = "Pass a short feature description or user story."
            }, JsonOptions);
        }

        var stack = string.IsNullOrWhiteSpace(stackHint) ? "not specified" : stackHint.Trim();
        var sequence = new[]
        {
            "/context",
            "/spec",
            "/validate",
            "/architect",
            "/validate",
            "/implement",
            "/test",
            "/reflect",
            "/review",
            "/commit"
        };

        return JsonSerializer.Serialize(new
        {
            workflow = "feature-spec-based",
            stackHint = stack,
            featureRequest,
            sequence,
            prompts = new Dictionary<string, string>
            {
                ["/context"] = $"/context - Analyze this repository for the feature and load standards. Feature: {featureRequest}",
                ["/spec"] = $"/spec - Write a structured, testable specification for this feature. Feature: {featureRequest}",
                ["/validate_spec"] = "/validate - Validate this spec for ambiguity, missing requirements, and testability gaps.",
                ["/architect"] = "/architect - Design an ARD/ADR and break implementation into tasks.",
                ["/validate_arch"] = "/validate - Validate the ARD and task plan for risks, sequencing, and missing work.",
                ["/implement"] = "/implement - Implement tasks incrementally with small verifiable diffs.",
                ["/test"] = "/test - Generate unit/integration tests and identify coverage gaps for changed code.",
                ["/reflect"] = "/reflect - Self-review implementation for correctness, standards, and edge cases.",
                ["/review"] = "/review - Run a multi-agent quality/security/correctness review of the final diff.",
                ["/commit"] = "/commit - Generate a conventional commit message and PR description with risks and rollback."
            }
        }, JsonOptions);
    }

    [McpServerTool(Name = "run_workflow_step")]
    [Description("Build a copy-paste prompt for a specific workflow command using your current context.")]
    public string RunWorkflowStep(
        [Description("Workflow command, e.g. '/spec', '/validate', '/review'.")] string command,
        [Description("Context payload for this step (feature text, diff summary, PR details, etc.).")] string context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowTools.RunWorkflowStep",
            ["Command"] = command
        });

        var key = NormalizeCommand(command);
        if (!Workflows.TryGetValue(key, out var workflow))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Unknown workflow '{command}'.",
                hint = "Use list_workflows() first."
            }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return JsonSerializer.Serialize(new
            {
                error = "context is required",
                hint = "Pass the current artifact, request, or diff for the workflow step."
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            command = workflow.Command,
            title = workflow.Title,
            goal = workflow.Goal,
            prompt = $"{workflow.Command} - {workflow.Goal}\n\nContext:\n{context}",
            next = GetNextCommands(workflow.Command)
        }, JsonOptions);
    }

    [McpServerTool(Name = "resolve_workflow_trigger")]
    [Description("Resolve natural language into the best workflow command and suggested sequence.")]
    public string ResolveWorkflowTrigger(
        [Description("Natural request, e.g. 'let's build a new feature: ...' or 'review this PR'.")] string request)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowTools.ResolveWorkflowTrigger"
        });

        if (string.IsNullOrWhiteSpace(request))
        {
            return JsonSerializer.Serialize(new
            {
                error = "request is required",
                hint = "Provide the natural-language user request to classify."
            }, JsonOptions);
        }

        var text = request.Trim();
        var lower = text.ToLowerInvariant();

        var command = lower switch
        {
            _ when lower.Contains("let's build") || lower.Contains("new feature") || lower.Contains("feature request") => "/spec",
            _ when lower.Contains("validate") => "/validate",
            _ when lower.Contains("architect") || lower.Contains("design") => "/architect",
            _ when lower.Contains("implement") || lower.Contains("build this") => "/implement",
            _ when lower.Contains("test") || lower.Contains("coverage") => "/test",
            _ when lower.Contains("reflect") || lower.Contains("self-review") => "/reflect",
            _ when lower.Contains("review") || lower.Contains("pr") => "/review",
            _ when lower.Contains("commit") => "/commit",
            _ when lower.Contains("status") => "/status",
            _ when lower.Contains("bug") || lower.Contains("defect") || lower.Contains("incident") => "/bugfix",
            _ when lower.Contains("proceed") || lower.Contains("end-to-end") => "/proceed",
            _ => "/context"
        };

        return JsonSerializer.Serialize(new
        {
            request = text,
            recommendedCommand = command,
            next = GetNextCommands(command),
            hint = "Call get_workflow(recommendedCommand) or run_workflow_step(recommendedCommand, context)."
        }, JsonOptions);
    }

    private static string NormalizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        var trimmed = command.Trim();
        return trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
    }

    private static IReadOnlyList<string> GetSteps(string command) => command.ToLowerInvariant() switch
    {
        "/spec" =>
        [
            "Capture user problem, goals, and constraints.",
            "Define clear acceptance criteria and non-goals.",
            "List edge cases and measurable outcomes."
        ],
        "/validate" =>
        [
            "Check ambiguity and missing requirements.",
            "Check testability and measurable acceptance criteria.",
            "Flag risks, assumptions, and unknowns."
        ],
        "/architect" =>
        [
            "Define architecture decision and alternatives considered.",
            "Map components/data/contracts and quality attributes.",
            "Break into sequenced implementation tasks."
        ],
        "/implement" =>
        [
            "Implement one task at a time with small diffs.",
            "Run focused checks after each major task.",
            "Keep behavior aligned with approved spec and ARD."
        ],
        "/test" =>
        [
            "Cover happy path, edge cases, and error paths.",
            "Add/adjust tests for changed production behavior.",
            "Report remaining coverage or confidence gaps."
        ],
        "/reflect" =>
        [
            "Compare implementation against spec and ARD.",
            "Identify correctness, style, and maintainability risks.",
            "Propose fixes before formal review."
        ],
        "/review" =>
        [
            "Review for correctness and regressions.",
            "Review for security, performance, and maintainability.",
            "Return prioritized findings with concrete fixes."
        ],
        "/commit" =>
        [
            "Summarize changes and intent.",
            "Generate conventional commit message.",
            "Generate PR description with risk and rollback notes."
        ],
        "/wrapup" =>
        [
            "Summarize what shipped and what changed.",
            "Capture lessons and assumptions validated/invalidated.",
            "Recommend updates to conventions/architecture docs when needed."
        ],
        "/proceed" =>
        [
            "Run phase gates: spec validation, architecture, implementation, verification.",
            "Apply fix-and-revalidate loops for blocker findings.",
            "Advance only when current gate is approved."
        ],
        "/status" =>
        [
            "Inspect current artifacts and phase state.",
            "Report blockers and readiness.",
            "Recommend the next command."
        ],
        "/bugfix" =>
        [
            "Capture bug report and scope.",
            "Analyze root cause and implement minimal fix.",
            "Verify with targeted tests and review."
        ],
        "/sprint" =>
        [
            "Identify eligible requirements.",
            "Launch parallel pipelines with isolation.",
            "Monitor and report consolidated status."
        ],
        "/context" or "/init" =>
        [
            "Load project standards and architecture context.",
            "Identify relevant agents/docs/tools for current task.",
            "Provide recommended workflow entry points."
        ],
        _ =>
        [
            "Use list_workflows() to discover supported commands."
        ]
    };

    private static IReadOnlyList<string> GetNextCommands(string command) => command.ToLowerInvariant() switch
    {
        "/context" or "/init" => ["/spec"],
        "/spec" => ["/validate", "/architect"],
        "/architect" => ["/validate", "/implement"],
        "/implement" => ["/test", "/reflect"],
        "/reflect" => ["/review"],
        "/review" => ["/commit"],
        "/commit" => ["/wrapup"],
        "/proceed" => ["/status", "/wrapup"],
        "/bugfix" => ["/test", "/review", "/commit"],
        _ => []
    };

    private sealed record WorkflowDefinition(string Command, string Title, string Goal);
}
