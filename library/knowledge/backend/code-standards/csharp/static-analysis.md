# Clean Code with Static Analysis

A checklist and templates for maintaining a high-quality .NET codebase using static analysis.

---

## The Checklist

| Step | Description | Purpose |
|------|-------------|---------|
| ✅ | `Directory.Build.props` with standard analysis settings | Enforce consistency across solution |
| ✅ | `Nullable enable` in csproj | Prevent null reference crashes |
| ✅ | `.editorconfig` defines style rules | Consistent formatting everywhere |
| ✅ | High-value analyzer packages added | Catch issues early |
| ✅ | `dotnet format --verify-no-changes` in CI | Prevent unformatted code |
| ✅ | Baseline legacy warnings | Gradual improvement without noise |
| ✅ | PR gates stop new problems | Automation prevents drift |

---

## Directory.Build.props Template

Place in solution root:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AnalysisLevel>latest</AnalysisLevel>
    <AnalysisMode>Recommended</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <!-- TreatWarningsAsErrors: enable once codebase is clean -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Meziantou.Analyzer" Version="*" PrivateAssets="all" />
    <PackageReference Include="Roslynator.Analyzers" Version="*" PrivateAssets="all" />
    <PackageReference Include="SonarAnalyzer.CSharp" Version="*" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Optional: conditional by project type -->
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Api.Analyzers" Version="*" 
      PrivateAssets="all" 
      Condition="'$(ProjectType)' == 'webapi'" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Analyzers" Version="*" 
      PrivateAssets="all" 
      Condition="'$(ProjectType)' == 'efcore'" />
  </ItemGroup>
</Project>
```

---

## .editorconfig Template

Place in solution root:

```ini
root = true

[*]
indent_style = space
indent_size = 4
end_of_line = crlf
insert_final_newline = true
charset = utf-8
trim_trailing_whitespace = true

[*.cs]
# Code style
csharp_new_line_before_open_brace = all
csharp_new_line_before_else = true
csharp_new_line_before_catch = true
csharp_new_line_before_finally = true
csharp_indent_case_contents = true
csharp_indent_switch_labels = true
csharp_space_after_cast = false
csharp_space_after_keywords_in_control_flow_statements = true
csharp_using_directive_placement = outside_namespace
csharp_prefer_braces = true
csharp_prefer_simple_using_statement = true
csharp_style_namespace_declarations = file_scoped
csharp_style_prefer_switch_expression = true
csharp_style_prefer_pattern_matching = true

# Naming
dotnet_naming_rule.interface.severity = error
dotnet_naming_rule.interface.style = IInterface
dotnet_naming_rule.types_sealed.severity = error
dotnet_naming_rule.types_sealed.style = sealed
dotnet_naming_rule.enums.flags.severity = error
dotnet_naming_rule.enums.flags.style = FlagsEnum

# Analysis rules (override props if needed)
dotnet_diagnostic.CA1054.severity = error
dotnet_diagnostic.CA1062.severity = error
dotnet_diagnostic.CA2007.severity = error
dotnet_diagnostic.CA5392.severity = error
dotnet_diagnostic.IDE0005.severity = error
dotnet_diagnostic.IDE0059.severity = none
dotnet_diagnostic.IDE0060.severity = warning

[*.{csproj,props,targets}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false

[*.{yaml,yml}]
indent_size = 2
```

---

## CI Pipeline Integration

### Baseline Legacy Warnings

```yaml
# GitHub Actions
- name: Build
  run: dotnet build --warnaserror
  
- name: Check formatting
  run: dotnet format --verify-no-changes
```

For existing projects with warnings, use a baseline:

```bash
# Generate baseline file
dotnet build /p:TreatWarningsAsErrors=false /p:GenerateWarningsFile=true
# Then add to Directory.Build.props:
<NoWarn>$(NoWarn);CA0000</NoWarn>
```

### PR Gate Example

```yaml
name: PR Check
on: [pull_request]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet format --verify-no-changes --verbosity diagnostic
      - run: dotnet test --no-restore --verbosity minimal
```

---

## Per-Project Type Recommendations

| Project Type | Additional Analyzers |
|--------------|---------------------|
| ASP.NET Core Web API | `Microsoft.AspNetCore.Mvc.Api.Analyzers` |
| Entity Framework Core | `Microsoft.EntityFrameworkCore.Analyzers` |
| Blazor | `Microsoft.AspNetCore.Components.Analyzers` |
| Source Generators | `Microsoft.CodeAnalysis.Analyzers` |
| Testing | `Xunit.Analyzers`, `NUnit.Analyzers` |

---

## Warnings Baseline Strategy

1. **Phase 1**: Build with `TreatWarningsAsErrors=false`, capture all warnings
2. **Phase 2**: Add `NoWarn` for existing warnings in Directory.Build.props
3. **Phase 3**: Enable `TreatWarningsAsErrors=true` — only new warnings fail
4. **Phase 4**: Periodically fix suppressed warnings and remove from NoWarn