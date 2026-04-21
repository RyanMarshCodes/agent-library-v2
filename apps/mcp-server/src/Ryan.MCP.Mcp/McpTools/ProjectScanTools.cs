using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class ProjectScanTools(ILogger<ProjectScanTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "scan_project")]
    [Description("""
        Scan a working directory to detect tech stack, frameworks, infrastructure, testing, quality tooling,
        and project structure. Returns a structured project brief. Use this as the first step when onboarding
        to a new project or codebase — it tells you what you're working with before you start reading code.
        """)]
    public async Task<string> ScanProject(
        [Description("Working directory to scan (defaults to current directory)")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var dir = ResolveDirectory(workingDirectory);
        if (dir is null)
            return JsonSerializer.Serialize(new { error = $"Directory not found: {workingDirectory}" }, JsonOptions);

        try
        {
            var scan = new ProjectScan(dir, logger);
            await scan.ExecuteAsync(cancellationToken);
            return JsonSerializer.Serialize(scan.ToResult(), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "scan_project failed for {Dir}", dir);
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private static string? ResolveDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            workingDirectory = Directory.GetCurrentDirectory();

        return Directory.Exists(workingDirectory) ? Path.GetFullPath(workingDirectory) : null;
    }

    // ─── Inner scan engine ───────────────────────────────────────────

    private sealed class ProjectScan(string rootDir, ILogger logger)
    {
        private string _projectName = "";
        private readonly List<string> _stacks = [];
        private readonly List<string> _frameworks = [];
        private readonly List<string> _packageManagers = [];
        private readonly List<string> _infrastructure = [];
        private readonly List<string> _testing = [];
        private readonly List<string> _qualityTools = [];
        private readonly List<string> _entryPoints = [];
        private readonly List<string> _topLevelDirs = [];
        private bool _isMonorepo;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            _projectName = Path.GetFileName(rootDir) ?? "unknown";

            DetectTopLevelDirs();
            await DetectDotNetAsync(ct);
            DetectNodeEcosystem();
            DetectPython();
            DetectGo();
            DetectRust();
            DetectJvm();
            DetectRuby();
            DetectInfrastructure();
            DetectQualityTools();
            DetectMonorepo();
        }

        public object ToResult()
        {
            var summary = BuildSummary();
            return new
            {
                status = "ok",
                projectName = _projectName,
                stacks = _stacks.Distinct().ToList(),
                frameworks = _frameworks.Distinct().ToList(),
                packageManagers = _packageManagers.Distinct().ToList(),
                infrastructure = _infrastructure.Distinct().ToList(),
                testing = _testing.Distinct().ToList(),
                qualityTools = _qualityTools.Distinct().ToList(),
                entryPoints = _entryPoints.Take(20).ToList(),
                isMonorepo = _isMonorepo,
                topLevelDirs = _topLevelDirs,
                summary
            };
        }

        // ─── Detection methods ───────────────────────────────────────

        private void DetectTopLevelDirs()
        {
            try
            {
                _topLevelDirs.AddRange(
                    Directory.EnumerateDirectories(rootDir)
                        .Select(Path.GetFileName)
                        .Where(n => n is not null && !n.StartsWith('.') && !IsIgnoredDir(n))
                        .Select(n => n!)
                        .OrderBy(n => n));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to enumerate top-level dirs");
            }
        }

        private async Task DetectDotNetAsync(CancellationToken ct)
        {
            // Solution files
            var slnFiles = SafeGlob(rootDir, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                _stacks.Add("dotnet");
                _packageManagers.Add("nuget");
                foreach (var sln in slnFiles)
                    _entryPoints.Add(RelPath(sln));
            }

            // Project files (search up to 3 levels deep to avoid traversing massive node_modules etc.)
            var csprojFiles = SafeGlob(rootDir, "*.csproj", SearchOption.AllDirectories, maxDepth: 4);
            var fsprojFiles = SafeGlob(rootDir, "*.fsproj", SearchOption.AllDirectories, maxDepth: 4);
            var projFiles = csprojFiles.Concat(fsprojFiles).ToArray();

            if (projFiles.Length > 0 && !_stacks.Contains("dotnet"))
            {
                _stacks.Add("dotnet");
                _packageManagers.Add("nuget");
            }

            if (slnFiles.Length == 0)
            {
                foreach (var proj in projFiles.Take(10))
                    _entryPoints.Add(RelPath(proj));
            }

            // Detect frameworks from csproj content
            foreach (var proj in projFiles.Take(20))
            {
                await DetectDotNetFrameworkFromProject(proj, ct);
            }

            // Aspire detection
            if (projFiles.Any(p => Path.GetFileName(p).Contains("AppHost", StringComparison.OrdinalIgnoreCase)))
                _frameworks.Add("aspire");

            // Directory.Build.props
            if (FileExists("Directory.Build.props") || FileExists("Directory.Packages.props"))
                _qualityTools.Add("central-package-management");

            // Test detection
            if (projFiles.Any(p => RelPath(p).Contains("test", StringComparison.OrdinalIgnoreCase)))
            {
                // Check for test framework references
                foreach (var testProj in projFiles.Where(p =>
                    RelPath(p).Contains("test", StringComparison.OrdinalIgnoreCase)).Take(5))
                {
                    await DetectDotNetTestFramework(testProj, ct);
                }
            }
        }

        private async Task DetectDotNetFrameworkFromProject(string projPath, CancellationToken ct)
        {
            try
            {
                var content = await File.ReadAllTextAsync(projPath, ct);

                if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("aspnetcore");
                if (content.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("dotnet-worker");
                if (content.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("blazor-wasm");
                if (content.Contains("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("blazor");
                if (content.Contains("Microsoft.Maui", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("maui");
                if (content.Contains("Aspire.Hosting", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("aspire");
                if (content.Contains("Microsoft.Azure.Functions", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("azure-functions");

                // Detect TFM
                var doc = XDocument.Parse(content);
                var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
                    ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;
                // Not adding to output directly, but useful for summary
            }
            catch
            {
                // Silently skip unparseable project files
            }
        }

        private async Task DetectDotNetTestFramework(string projPath, CancellationToken ct)
        {
            try
            {
                var content = await File.ReadAllTextAsync(projPath, ct);

                if (content.Contains("xunit", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("xunit");
                if (content.Contains("NUnit", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("nunit");
                if (content.Contains("MSTest", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("mstest");
                if (content.Contains("FluentAssertions", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("fluent-assertions");
                if (content.Contains("NSubstitute", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("nsubstitute");
                if (content.Contains("Moq", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("moq");
                if (content.Contains("Bogus", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("bogus");
                if (content.Contains("Verify", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("verify");
            }
            catch
            {
                // Skip
            }
        }

        private void DetectNodeEcosystem()
        {
            var rootPackageJson = Path.Combine(rootDir, "package.json");

            if (File.Exists(rootPackageJson))
            {
                // Root-level Node project
                ScanNodeProject(rootDir, "package.json");
            }

            // Also scan direct child directories for sub-projects with package.json
            // This catches monorepo layouts like LFOE where lfoe-admin/ and lfoe-components/ each have their own package.json
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(rootDir))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (dirName is null || dirName.StartsWith('.') || IsIgnoredDir(dirName)) continue;

                    var subPackageJson = Path.Combine(subDir, "package.json");
                    if (File.Exists(subPackageJson))
                    {
                        ScanNodeProject(subDir, $"{dirName}/package.json");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to scan sub-directories for package.json");
            }
        }

        private void ScanNodeProject(string projectDir, string entryPointRelPath)
        {
            if (!_stacks.Contains("node"))
                _stacks.Add("node");

            if (!_entryPoints.Contains(entryPointRelPath))
                _entryPoints.Add(entryPointRelPath);

            // Package manager detection (only from root or first found)
            if (_packageManagers.Count == 0 || !_packageManagers.Any(p => p is "npm" or "pnpm" or "yarn" or "bun"))
            {
                if (File.Exists(Path.Combine(projectDir, "pnpm-lock.yaml")) || File.Exists(Path.Combine(projectDir, "pnpm-workspace.yaml")))
                    _packageManagers.Add("pnpm");
                else if (File.Exists(Path.Combine(projectDir, "yarn.lock")))
                    _packageManagers.Add("yarn");
                else if (File.Exists(Path.Combine(projectDir, "bun.lockb")) || File.Exists(Path.Combine(projectDir, "bun.lock")))
                    _packageManagers.Add("bun");
                else if (File.Exists(Path.Combine(projectDir, "package-lock.json")))
                    _packageManagers.Add("npm");
                else
                    _packageManagers.Add("npm");
            }

            // Parse package.json for framework detection
            var packageJsonPath = Path.Combine(projectDir, "package.json");
            try
            {
                var json = File.ReadAllText(packageJsonPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                DetectNodeFrameworks(root);
                DetectNodeTesting(root);
                DetectNodeQualityTools(root);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse {Path}", packageJsonPath);
            }

            // Config file-based detection (check in the sub-project dir too)
            DetectNodeConfigFilesIn(projectDir);
        }

        private void DetectNodeFrameworks(JsonElement root)
        {
            var allDeps = MergeDeps(root);

            // Frontend frameworks
            if (allDeps.ContainsKey("react") || allDeps.ContainsKey("react-dom"))
                _frameworks.Add("react");
            if (allDeps.ContainsKey("@angular/core"))
                _frameworks.Add("angular");
            if (allDeps.ContainsKey("vue"))
                _frameworks.Add("vue");
            if (allDeps.ContainsKey("svelte") || allDeps.ContainsKey("@sveltejs/kit"))
                _frameworks.Add("svelte");
            if (allDeps.ContainsKey("solid-js"))
                _frameworks.Add("solid");
            if (allDeps.ContainsKey("lit") || allDeps.ContainsKey("lit-element") || allDeps.ContainsKey("lit-html"))
                _frameworks.Add("lit");

            // Meta-frameworks
            if (allDeps.ContainsKey("next"))
                _frameworks.Add("nextjs");
            if (allDeps.ContainsKey("nuxt") || allDeps.ContainsKey("nuxt3"))
                _frameworks.Add("nuxt");
            if (allDeps.ContainsKey("@analogjs/platform"))
                _frameworks.Add("analog");
            if (allDeps.ContainsKey("astro"))
                _frameworks.Add("astro");
            if (allDeps.ContainsKey("remix") || allDeps.ContainsKey("@remix-run/node"))
                _frameworks.Add("remix");

            // Build tools
            if (allDeps.ContainsKey("vite"))
                _frameworks.Add("vite");
            if (allDeps.ContainsKey("webpack") || allDeps.ContainsKey("webpack-cli"))
                _frameworks.Add("webpack");
            if (allDeps.ContainsKey("esbuild"))
                _frameworks.Add("esbuild");
            if (allDeps.ContainsKey("turbo"))
                _frameworks.Add("turborepo");

            // CSS / UI
            if (allDeps.ContainsKey("tailwindcss"))
                _frameworks.Add("tailwind");

            // TypeScript
            if (allDeps.ContainsKey("typescript") || FileExists("tsconfig.json"))
            {
                _stacks.Add("typescript");
            }
        }

        private void DetectNodeTesting(JsonElement root)
        {
            var allDeps = MergeDeps(root);

            if (allDeps.ContainsKey("vitest"))
                _testing.Add("vitest");
            if (allDeps.ContainsKey("jest"))
                _testing.Add("jest");
            if (allDeps.ContainsKey("mocha"))
                _testing.Add("mocha");
            if (allDeps.ContainsKey("@playwright/test") || allDeps.ContainsKey("playwright"))
                _testing.Add("playwright");
            if (allDeps.ContainsKey("cypress"))
                _testing.Add("cypress");
            if (allDeps.ContainsKey("@testing-library/react") ||
                allDeps.ContainsKey("@testing-library/vue") ||
                allDeps.ContainsKey("@testing-library/angular"))
                _testing.Add("testing-library");
            if (allDeps.ContainsKey("storybook") || allDeps.ContainsKey("@storybook/react"))
                _testing.Add("storybook");
        }

        private void DetectNodeQualityTools(JsonElement root)
        {
            var allDeps = MergeDeps(root);

            if (allDeps.ContainsKey("eslint"))
                _qualityTools.Add("eslint");
            if (allDeps.ContainsKey("prettier"))
                _qualityTools.Add("prettier");
            if (allDeps.ContainsKey("biome") || allDeps.ContainsKey("@biomejs/biome"))
                _qualityTools.Add("biome");
            if (allDeps.ContainsKey("husky"))
                _qualityTools.Add("husky");
            if (allDeps.ContainsKey("lint-staged"))
                _qualityTools.Add("lint-staged");
            if (allDeps.ContainsKey("commitlint") || allDeps.ContainsKey("@commitlint/cli"))
                _qualityTools.Add("commitlint");
        }

        private void DetectNodeConfigFilesIn(string dir)
        {
            bool exists(string name) => File.Exists(Path.Combine(dir, name));

            if (exists("angular.json") || exists("project.json"))
                _frameworks.Add("angular");
            if (exists("next.config.js") || exists("next.config.mjs") || exists("next.config.ts"))
                _frameworks.Add("nextjs");
            if (exists("nuxt.config.ts") || exists("nuxt.config.js"))
                _frameworks.Add("nuxt");
            if (exists("svelte.config.js") || exists("svelte.config.ts"))
                _frameworks.Add("svelte");
            if (exists("astro.config.mjs") || exists("astro.config.ts"))
                _frameworks.Add("astro");
            if (exists("vite.config.ts") || exists("vite.config.js") || exists("vite.config.mts"))
                _frameworks.Add("vite");
            if (exists("tailwind.config.js") || exists("tailwind.config.ts") || exists("tailwind.config.mjs"))
                _frameworks.Add("tailwind");
        }

        private void DetectPython()
        {
            var hasPyproject = FileExists("pyproject.toml");
            var hasRequirements = FileExists("requirements.txt");
            var hasSetupPy = FileExists("setup.py");
            var hasPipfile = FileExists("Pipfile");

            if (!hasPyproject && !hasRequirements && !hasSetupPy && !hasPipfile) return;

            _stacks.Add("python");
            if (hasPyproject) _entryPoints.Add("pyproject.toml");
            else if (hasRequirements) _entryPoints.Add("requirements.txt");

            // Package manager
            if (FileExists("poetry.lock") || hasPyproject)
                _packageManagers.Add("poetry");
            if (FileExists("uv.lock"))
                _packageManagers.Add("uv");
            if (hasPipfile)
                _packageManagers.Add("pipenv");
            if (hasRequirements && !FileExists("poetry.lock") && !hasPipfile)
                _packageManagers.Add("pip");

            // Framework detection from pyproject.toml
            if (hasPyproject)
            {
                try
                {
                    var content = File.ReadAllText(Path.Combine(rootDir, "pyproject.toml"));
                    if (content.Contains("django", StringComparison.OrdinalIgnoreCase))
                        _frameworks.Add("django");
                    if (content.Contains("fastapi", StringComparison.OrdinalIgnoreCase))
                        _frameworks.Add("fastapi");
                    if (content.Contains("flask", StringComparison.OrdinalIgnoreCase))
                        _frameworks.Add("flask");
                    if (content.Contains("pytest", StringComparison.OrdinalIgnoreCase))
                        _testing.Add("pytest");
                    if (content.Contains("ruff", StringComparison.OrdinalIgnoreCase))
                        _qualityTools.Add("ruff");
                    if (content.Contains("mypy", StringComparison.OrdinalIgnoreCase))
                        _qualityTools.Add("mypy");
                    if (content.Contains("black", StringComparison.OrdinalIgnoreCase))
                        _qualityTools.Add("black");
                }
                catch
                {
                    // Skip
                }
            }
        }

        private void DetectGo()
        {
            if (!FileExists("go.mod")) return;

            _stacks.Add("go");
            _packageManagers.Add("go-modules");
            _entryPoints.Add("go.mod");

            try
            {
                var content = File.ReadAllText(Path.Combine(rootDir, "go.mod"));
                if (content.Contains("gin-gonic", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("gin");
                if (content.Contains("gofiber", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("fiber");
                if (content.Contains("echo", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("echo");
            }
            catch
            {
                // Skip
            }
        }

        private void DetectRust()
        {
            if (!FileExists("Cargo.toml")) return;

            _stacks.Add("rust");
            _packageManagers.Add("cargo");
            _entryPoints.Add("Cargo.toml");

            try
            {
                var content = File.ReadAllText(Path.Combine(rootDir, "Cargo.toml"));
                if (content.Contains("actix-web", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("actix");
                if (content.Contains("axum", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("axum");
                if (content.Contains("tokio", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("tokio");
                if (content.Contains("[workspace]", StringComparison.Ordinal))
                    _isMonorepo = true;
            }
            catch
            {
                // Skip
            }
        }

        private void DetectJvm()
        {
            var hasPom = FileExists("pom.xml");
            var hasGradle = FileExists("build.gradle") || FileExists("build.gradle.kts");

            if (!hasPom && !hasGradle) return;

            if (hasPom) { _packageManagers.Add("maven"); _entryPoints.Add("pom.xml"); }
            if (hasGradle) { _packageManagers.Add("gradle"); _entryPoints.Add(FileExists("build.gradle.kts") ? "build.gradle.kts" : "build.gradle"); }

            // Detect Kotlin vs Java
            var ktFiles = SafeGlob(rootDir, "*.kt", SearchOption.AllDirectories, maxDepth: 3);
            var javaFiles = SafeGlob(rootDir, "*.java", SearchOption.AllDirectories, maxDepth: 3);

            if (ktFiles.Length > 0) _stacks.Add("kotlin");
            if (javaFiles.Length > 0) _stacks.Add("java");
            if (ktFiles.Length == 0 && javaFiles.Length == 0) _stacks.Add("java"); // default assumption

            // Framework detection from build files
            try
            {
                var buildContent = hasPom
                    ? File.ReadAllText(Path.Combine(rootDir, "pom.xml"))
                    : File.ReadAllText(Path.Combine(rootDir, FileExists("build.gradle.kts") ? "build.gradle.kts" : "build.gradle"));

                if (buildContent.Contains("spring-boot", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("spring-boot");
                if (buildContent.Contains("quarkus", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("quarkus");
                if (buildContent.Contains("micronaut", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("micronaut");
                if (buildContent.Contains("ktor", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("ktor");
            }
            catch
            {
                // Skip
            }
        }

        private void DetectRuby()
        {
            if (!FileExists("Gemfile")) return;

            _stacks.Add("ruby");
            _packageManagers.Add("bundler");
            _entryPoints.Add("Gemfile");

            try
            {
                var content = File.ReadAllText(Path.Combine(rootDir, "Gemfile"));
                if (content.Contains("rails", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("rails");
                if (content.Contains("sinatra", StringComparison.OrdinalIgnoreCase))
                    _frameworks.Add("sinatra");
                if (content.Contains("rspec", StringComparison.OrdinalIgnoreCase))
                    _testing.Add("rspec");
                if (content.Contains("rubocop", StringComparison.OrdinalIgnoreCase))
                    _qualityTools.Add("rubocop");
            }
            catch
            {
                // Skip
            }
        }

        private void DetectInfrastructure()
        {
            // Container
            if (FileExists("Dockerfile") || SafeGlob(rootDir, "Dockerfile*", SearchOption.TopDirectoryOnly).Length > 0)
                _infrastructure.Add("docker");
            if (FileExists("docker-compose.yml") || FileExists("docker-compose.yaml") || FileExists("compose.yml") || FileExists("compose.yaml"))
                _infrastructure.Add("docker-compose");
            if (FileExists(".dockerignore"))
                _infrastructure.Add("docker");

            // CI/CD
            if (Directory.Exists(Path.Combine(rootDir, ".github", "workflows")))
                _infrastructure.Add("github-actions");
            if (FileExists("azure-pipelines.yml") || FileExists(".azure-pipelines.yml"))
                _infrastructure.Add("azure-pipelines");
            if (FileExists(".gitlab-ci.yml"))
                _infrastructure.Add("gitlab-ci");
            if (FileExists("Jenkinsfile"))
                _infrastructure.Add("jenkins");
            if (FileExists("bitbucket-pipelines.yml"))
                _infrastructure.Add("bitbucket-pipelines");

            // IaC
            if (SafeGlob(rootDir, "*.bicep", SearchOption.AllDirectories, maxDepth: 3).Length > 0)
                _infrastructure.Add("bicep");
            if (SafeGlob(rootDir, "*.tf", SearchOption.AllDirectories, maxDepth: 3).Length > 0)
                _infrastructure.Add("terraform");
            if (FileExists("pulumi.yaml") || FileExists("Pulumi.yaml"))
                _infrastructure.Add("pulumi");
            if (FileExists("serverless.yml") || FileExists("serverless.yaml"))
                _infrastructure.Add("serverless-framework");
            if (SafeGlob(rootDir, "*.cdk.ts", SearchOption.AllDirectories, maxDepth: 3).Length > 0 ||
                FileExists("cdk.json"))
                _infrastructure.Add("aws-cdk");

            // Cloud config
            if (FileExists("fly.toml"))
                _infrastructure.Add("fly-io");
            if (FileExists("vercel.json"))
                _infrastructure.Add("vercel");
            if (FileExists("netlify.toml"))
                _infrastructure.Add("netlify");
            if (FileExists("railway.json") || FileExists("railway.toml"))
                _infrastructure.Add("railway");
        }

        private void DetectQualityTools()
        {
            if (FileExists(".editorconfig"))
                _qualityTools.Add("editorconfig");
            if (FileExists(".prettierrc") || FileExists(".prettierrc.json") || FileExists(".prettierrc.js") || FileExists(".prettierrc.yaml") || FileExists("prettier.config.js") || FileExists("prettier.config.mjs"))
                _qualityTools.Add("prettier");
            if (FileExists(".eslintrc.js") || FileExists(".eslintrc.json") || FileExists(".eslintrc.yaml") || FileExists("eslint.config.js") || FileExists("eslint.config.mjs") || FileExists("eslint.config.ts"))
                _qualityTools.Add("eslint");
            if (FileExists("biome.json") || FileExists("biome.jsonc"))
                _qualityTools.Add("biome");
            if (FileExists(".stylelintrc") || FileExists(".stylelintrc.json"))
                _qualityTools.Add("stylelint");
            if (FileExists("renovate.json") || FileExists("renovate.json5") || FileExists(".renovaterc"))
                _qualityTools.Add("renovate");
            if (FileExists("dependabot.yml") || FileExists(".github/dependabot.yml"))
                _qualityTools.Add("dependabot");
        }

        private void DetectMonorepo()
        {
            if (_isMonorepo) return; // Already detected (e.g., Rust workspace)

            // Node workspaces
            var packageJson = Path.Combine(rootDir, "package.json");
            if (File.Exists(packageJson))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                    if (doc.RootElement.TryGetProperty("workspaces", out _))
                    {
                        _isMonorepo = true;
                        return;
                    }
                }
                catch { }
            }

            if (FileExists("pnpm-workspace.yaml"))
            {
                _isMonorepo = true;
                return;
            }

            if (FileExists("lerna.json") || FileExists("nx.json") || FileExists("turbo.json"))
            {
                _isMonorepo = true;
                _frameworks.Add(FileExists("nx.json") ? "nx" : FileExists("turbo.json") ? "turborepo" : "lerna");
                return;
            }

            // Multiple .sln files or multiple package.json in direct subdirs
            var slnCount = SafeGlob(rootDir, "*.sln", SearchOption.TopDirectoryOnly).Length;
            if (slnCount > 1) _isMonorepo = true;

            // Multiple stacks detected (typescript is a qualifier of node, not a separate stack)
            var primaryStacks = _stacks.Distinct()
                .Where(s => s is not "typescript")
                .ToList();
            if (primaryStacks.Count >= 2)
                _isMonorepo = true;
        }

        private string BuildSummary()
        {
            var parts = new List<string>();

            if (_isMonorepo) parts.Add("Monorepo");

            var distinctStacks = _stacks.Distinct().ToList();
            if (distinctStacks.Count > 0)
                parts.Add(string.Join(" + ", distinctStacks.Select(Titleize)));

            var distinctFrameworks = _frameworks.Distinct().Take(5).ToList();
            if (distinctFrameworks.Count > 0)
                parts.Add("using " + string.Join(", ", distinctFrameworks));

            var distinctInfra = _infrastructure.Distinct().Take(4).ToList();
            if (distinctInfra.Count > 0)
                parts.Add("with " + string.Join(", ", distinctInfra));

            return parts.Count > 0
                ? string.Join(", ", parts)
                : "No recognized tech stack detected";
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private bool FileExists(string relativePath) =>
            File.Exists(Path.Combine(rootDir, relativePath));

        private string RelPath(string absolutePath) =>
            Path.GetRelativePath(rootDir, absolutePath).Replace('\\', '/');

        private static string Titleize(string s) => s switch
        {
            "dotnet" => ".NET",
            "node" => "Node.js",
            "typescript" => "TypeScript",
            "python" => "Python",
            "go" => "Go",
            "rust" => "Rust",
            "java" => "Java",
            "kotlin" => "Kotlin",
            "ruby" => "Ruby",
            _ => s
        };

        private static bool IsIgnoredDir(string name) =>
            name is "node_modules" or "bin" or "obj" or "dist" or "build" or "out"
                or ".git" or ".vs" or ".idea" or ".vscode" or "__pycache__"
                or "target" or "vendor" or ".next" or ".nuxt" or ".output"
                or "packages" or "artifacts" or "coverage" or "TestResults";

        private static Dictionary<string, string> MergeDeps(JsonElement root)
        {
            var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeSection(root, "dependencies", deps);
            MergeSection(root, "devDependencies", deps);
            MergeSection(root, "peerDependencies", deps);
            return deps;
        }

        private static void MergeSection(JsonElement root, string section, Dictionary<string, string> deps)
        {
            if (!root.TryGetProperty(section, out var el) || el.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in el.EnumerateObject())
            {
                deps.TryAdd(prop.Name, prop.Value.GetString() ?? "");
            }
        }

        /// <summary>
        /// Safe glob that respects maxDepth and silently skips inaccessible directories.
        /// </summary>
        private static string[] SafeGlob(string root, string pattern, SearchOption option, int maxDepth = int.MaxValue)
        {
            if (option == SearchOption.TopDirectoryOnly || maxDepth <= 1)
            {
                try { return Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
                catch { return []; }
            }

            var results = new List<string>();
            CollectFiles(root, pattern, 0, maxDepth, results);
            return [.. results];
        }

        private static void CollectFiles(string dir, string pattern, int currentDepth, int maxDepth, List<string> results)
        {
            if (currentDepth >= maxDepth) return;

            try
            {
                results.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            }
            catch
            {
                return;
            }

            if (currentDepth + 1 >= maxDepth) return;

            try
            {
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(subDir);
                    if (name is not null && !name.StartsWith('.') && !IsIgnoredDir(name))
                    {
                        CollectFiles(subDir, pattern, currentDepth + 1, maxDepth, results);
                    }
                }
            }
            catch
            {
                // Skip inaccessible directories
            }
        }
    }
}
