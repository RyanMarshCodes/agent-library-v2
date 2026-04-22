using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services.Knowledge;
using Ryan.MCP.Mcp.Services.Observability;
using Ryan.MCP.Mcp.Services.WorkflowState;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class WorkflowTools(
    IWorkflowStateStore workflowStateStore,
    PromptPrefixCache promptPrefixCache,
    PlatformMetrics metrics,
    McpOptions options,
    ILogger<WorkflowTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly IReadOnlyDictionary<string, WorkflowDefinition> Workflows =
        new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["/feature-delivery"] = new(
                "/feature-delivery",
                "Feature Delivery",
                "Spec-driven feature implementation with quality gates.",
                [
                    new("scope-spec", "Scope and specification", "Feature statement, ACs, constraints", "Testable acceptance criteria approved"),
                    new("architecture-plan", "Architecture and plan", "Design, task DAG, risk notes", "Implementation plan approved"),
                    new("implementation-verify", "Implementation and verification", "Small diffs with tests", "Validation passes"),
                    new("review-release", "Review and release readiness", "Findings, release notes, rollback plan", "Merge/release recommendation ready")
                ],
                ["/commit", "/wrapup"]),
            ["/incident-triage"] = new(
                "/incident-triage",
                "Incident Triage",
                "Contain impact, identify root cause, and recover safely.",
                [
                    new("declare-contain", "Declare and contain", "Severity, owner, containment action", "Impact is bounded"),
                    new("diagnose", "Diagnose root cause", "Timeline and evidence set", "Likely root cause identified"),
                    new("mitigate-recover", "Mitigate and recover", "Fix or rollback with verification", "Service restored"),
                    new("post-incident", "Post-incident hardening", "RCA and prevention actions", "Follow-ups captured with owners")
                ],
                ["/review", "/wrapup"]),
            ["/spike-research"] = new(
                "/spike-research",
                "Spike Research",
                "Time-boxed exploration with decision-ready output.",
                [
                    new("frame", "Frame question and success criteria", "Decision question and constraints", "Scope is time-boxed"),
                    new("evidence", "Collect option evidence", "Comparative option matrix", "Evidence quality is comparable"),
                    new("validate", "Validate leading options", "Prototype/experiment outputs", "Top option is data-backed"),
                    new("decision", "Publish recommendation", "Decision memo and next steps", "Decision owner can approve/reject")
                ],
                ["/architect", "/feature-delivery"]),
            ["/multi-agent-delivery"] = new(
                "/multi-agent-delivery",
                "Multi-Agent Delivery",
                "Parallel specialist execution with explicit handoffs.",
                [
                    new("plan", "Plan orchestration", "Subtask DAG and routing", "Dependencies are correct"),
                    new("handoff", "Create handoff packets", "Inputs, constraints, deliverables", "Handoffs are unambiguous"),
                    new("parallel", "Execute in parallel", "Per-stream status and blockers", "Critical path complete"),
                    new("integrate", "Integrate and validate", "Integrated outputs + checks", "Unified recommendation ready")
                ],
                ["/review", "/commit"]),
            ["/eval-regression"] = new(
                "/eval-regression",
                "Eval Regression",
                "Detect quality/cost regressions before rollout.",
                [
                    new("readiness", "Prepare eval baseline", "Baseline metrics and thresholds", "Baseline approved"),
                    new("execute", "Run baseline and candidate", "Structured eval outputs", "Comparable runs complete"),
                    new("analyze", "Analyze drift", "Delta and failure clustering", "Regression severity categorized"),
                    new("decision", "Gate rollout", "Go/no-go and mitigation plan", "Decision owner sign-off")
                ],
                ["/status", "/wrapup"]),

            // Keep existing command compatibility.
            ["/context"] = SingleStep("/context", "Initialize project context", "Load standards and architecture context.", "/spec"),
            ["/init"] = SingleStep("/init", "Bootstrap workflow context", "Initialize or refresh workflow context and repository conventions.", "/spec"),
            ["/spec"] = SingleStep("/spec", "Write requirements from feature request", "Convert feature request into structured, testable requirements.", "/validate"),
            ["/validate"] = SingleStep("/validate", "Validate quality", "Validate spec quality or architecture/task quality before coding.", "/architect"),
            ["/architect"] = SingleStep("/architect", "Design ARD and tasks", "Design architecture decisions and break into implementation tasks.", "/implement"),
            ["/implement"] = SingleStep("/implement", "Implement tasks", "Generate and apply implementation changes task by task.", "/test"),
            ["/test"] = SingleStep("/test", "Generate tests and coverage", "Generate meaningful unit/integration tests and identify coverage gaps.", "/reflect"),
            ["/reflect"] = SingleStep("/reflect", "Self-review implementation", "Run self-review to catch requirement, correctness, and style misses.", "/review"),
            ["/review"] = SingleStep("/review", "Multi-agent review", "Run a final quality, correctness, and security review before merge.", "/commit"),
            ["/commit"] = SingleStep("/commit", "Commit and PR prep", "Generate conventional commit and PR description with risks and validation.", "/wrapup"),
            ["/wrapup"] = SingleStep("/wrapup", "Feature closeout", "Finalize feature artifacts and capture durable lessons/assumptions."),
            ["/proceed"] = SingleStep("/proceed", "Run full pipeline", "Run end-to-end spec-based pipeline with validation gates.", "/status"),
            ["/status"] = SingleStep("/status", "Workflow status", "Show current workflow state and recommended next command."),
            ["/bugfix"] = SingleStep("/bugfix", "Streamlined bug workflow", "Run report/analyze/fix/verify flow for defects.", "/test"),
            ["/sprint"] = SingleStep("/sprint", "Parallel proceed orchestration", "Run multiple feature pipelines in parallel with isolation.", "/status")
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
                w.Goal,
                steps = w.Steps.Count
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
            steps = workflow.Steps.Select((step, index) => new
            {
                index = index + 1,
                step.StepId,
                step.Title,
                step.RequiredArtifacts,
                step.ExitCriteria
            }),
            next = workflow.NextCommands
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
        var sequence = new[] { "/feature-delivery", "/test", "/review", "/commit" };
        var (cached, cacheKey, prefix) = GetOrCachePromptPrefix(
            scope: "start_feature_workflow",
            workflowKey: "/feature-delivery",
            stepKey: "sequence-v1",
            versionToken: "v1",
            templateFactory: () => """
Use this command sequence for spec-based delivery.

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
""");

        return JsonSerializer.Serialize(new
        {
            workflow = "feature-spec-based",
            stackHint = stack,
            featureRequest,
            promptCache = new
            {
                enabled = options.PromptCache.EnablePrefixCache,
                hit = cached,
                cacheKey,
                ttlMinutes = options.PromptCache.PrefixCacheTtlMinutes
            },
            sequence,
            promptPrefix = prefix,
            prompts = new Dictionary<string, string>
            {
                ["/context"] = $"/context - Analyze this repository for the feature and load standards. Feature: {featureRequest}",
                ["/spec"] = $"/spec - Write a structured, testable specification for this feature. Feature: {featureRequest}",
                ["/validate_spec"] = "/validate - Validate this spec for ambiguity, missing requirements, and testability gaps.",
                ["/architect"] = "/architect - Design an ARD/ADR and break implementation into tasks.",
                ["/validate_arch"] = "/validate - Validate the ARD and task plan for risks, sequencing, and missing work.",
                ["/feature-delivery"] = "/feature-delivery - Run the full gated delivery workflow from scoped specification to release readiness.",
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
    public async Task<string> RunWorkflowStep(
        [Description("Workflow command, e.g. '/spec', '/validate', '/review'.")] string command,
        [Description("Context payload for this step (feature text, diff summary, PR details, etc.).")] string context,
        [Description("Optional workflow id to persist progress.")] string? workflowId = null,
        [Description("Step index for multi-step workflows (default 1).")] int stepIndex = 1,
        [Description("Optional persisted workflow status when workflowId is supplied.")] string status = "in_progress",
        CancellationToken ct = default)
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

        var normalizedIndex = Math.Clamp(stepIndex, 1, workflow.Steps.Count);
        var step = workflow.Steps[normalizedIndex - 1];
        var (cached, cacheKey, prefix) = GetOrCachePromptPrefix(
            scope: "run_workflow_step",
            workflowKey: workflow.Command,
            stepKey: step.StepId,
            versionToken: $"step-{normalizedIndex}",
            templateFactory: () =>
                $"{workflow.Command} [{normalizedIndex}/{workflow.Steps.Count}] - {step.Title}\n" +
                $"Goal: {workflow.Goal}\n" +
                $"Required artifacts: {step.RequiredArtifacts}\n" +
                $"Exit criteria: {step.ExitCriteria}\n\n" +
                "Context:\n");

        WorkflowStateEntry? persisted = null;
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            persisted = await workflowStateStore.UpsertAsync(
                new WorkflowStateUpsertRequest(
                    workflowId.Trim(),
                    workflow.Command,
                    workflow.Title,
                    status,
                    normalizedIndex,
                    step.StepId,
                    step.Title,
                    context),
                ct).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            command = workflow.Command,
            title = workflow.Title,
            goal = workflow.Goal,
            step = new
            {
                index = normalizedIndex,
                step.StepId,
                step.Title,
                step.RequiredArtifacts,
                step.ExitCriteria
            },
            prompt = $"{prefix}{context}",
            promptCache = new
            {
                enabled = options.PromptCache.EnablePrefixCache,
                hit = cached,
                cacheKey,
                ttlMinutes = options.PromptCache.PrefixCacheTtlMinutes,
                prefixSha256 = Sha256(prefix)
            },
            next = normalizedIndex < workflow.Steps.Count
                ? new[] { $"{workflow.Command} step {normalizedIndex + 1}" }
                : workflow.NextCommands,
            persisted
        }, JsonOptions);
    }

    [McpServerTool(Name = "start_workflow")]
    [Description("Start a workflow and persist its initial state.")]
    public async Task<string> StartWorkflow(
        [Description("Workflow command (e.g. '/feature-delivery').")] string command,
        [Description("Optional stable workflow id. If omitted, generated automatically.")] string? workflowId = null,
        [Description("Optional title/objective for this run.")] string? title = null,
        [Description("Optional context payload.")] string? context = null,
        CancellationToken ct = default)
    {
        var key = NormalizeCommand(command);
        if (!Workflows.TryGetValue(key, out var workflow))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Unknown workflow '{command}'.",
                hint = "Use list_workflows() first."
            }, JsonOptions);
        }

        var initial = workflow.Steps[0];
        var id = string.IsNullOrWhiteSpace(workflowId)
            ? $"{workflow.Command.TrimStart('/').Replace('/', '-')}-{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : workflowId.Trim();

        var state = await workflowStateStore.UpsertAsync(
            new WorkflowStateUpsertRequest(
                id,
                workflow.Command,
                title ?? workflow.Title,
                "in_progress",
                1,
                initial.StepId,
                initial.Title,
                context),
            ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            workflow = workflow.Command,
            workflowId = id,
            currentStep = new
            {
                index = 1,
                initial.StepId,
                initial.Title
            },
            state
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
            _ when lower.Contains("incident") || lower.Contains("outage") || lower.Contains("sev") => "/incident-triage",
            _ when lower.Contains("spike") || lower.Contains("research") => "/spike-research",
            _ when lower.Contains("multi-agent") || lower.Contains("parallel") => "/multi-agent-delivery",
            _ when lower.Contains("regression") || lower.Contains("eval") => "/eval-regression",
            _ when lower.Contains("feature delivery") => "/feature-delivery",
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
            next = Workflows.TryGetValue(command, out var workflow) ? workflow.NextCommands : [],
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

    private static WorkflowDefinition SingleStep(
        string command,
        string title,
        string goal,
        params string[] nextCommands) =>
        new(
            command,
            title,
            goal,
            [new("default", "Execute workflow step", "Context relevant to this step", "Outcome and evidence captured")],
            nextCommands);

    private (bool Cached, string CacheKey, string Prefix) GetOrCachePromptPrefix(
        string scope,
        string workflowKey,
        string stepKey,
        string versionToken,
        Func<string> templateFactory)
    {
        var cacheKey = BuildPromptCacheKey(scope, workflowKey, stepKey, versionToken);
        if (promptPrefixCache.TryGet(cacheKey, out var cachedPrefix))
        {
            metrics.RecordPromptPrefixCacheHit();
            return (true, cacheKey, cachedPrefix);
        }

        metrics.RecordPromptPrefixCacheMiss();
        var prefix = templateFactory();
        promptPrefixCache.Set(cacheKey, prefix);
        return (false, cacheKey, prefix);
    }

    private string BuildPromptCacheKey(string scope, string workflowKey, string stepKey, string versionToken)
    {
        var project = options.Knowledge.ProjectSlug;
        return $"v1|{project}|{scope}|{workflowKey}|{stepKey}|{versionToken}";
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record WorkflowDefinition(
        string Command,
        string Title,
        string Goal,
        IReadOnlyList<WorkflowStep> Steps,
        IReadOnlyList<string> NextCommands);

    private sealed record WorkflowStep(
        string StepId,
        string Title,
        string RequiredArtifacts,
        string ExitCriteria);

    private static IReadOnlyList<string> GetSteps(string command) => command.ToLowerInvariant() switch
    {
        _ => ["Use get_workflow(command) to inspect structured steps."]
    };
}
