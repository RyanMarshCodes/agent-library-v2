#!/usr/bin/env node
import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const requiredFields = [
  "allowed_tools",
  "forbidden_actions",
  "max_parallelism",
  "budget_tier",
  "escalation_triggers"
];

function getChangedAgentFiles() {
  const cmd = "git diff --name-only --diff-filter=ACMRTUXB origin/main...HEAD";
  const output = execSync(cmd, { encoding: "utf8" });
  return output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .filter((path) =>
      path.startsWith("library/agents/") || path.startsWith("library/skills/"));
}

function parseFrontmatter(raw) {
  if (!raw.startsWith("---")) {
    return "";
  }

  const parts = raw.split("---");
  return parts.length >= 3 ? parts[1] : "";
}

function validate(path) {
  const fullPath = resolve(process.cwd(), path);
  const raw = readFileSync(fullPath, "utf8");
  const frontmatter = parseFrontmatter(raw);

  if (!frontmatter) {
    return { path, missing: ["frontmatter"] };
  }

  const missing = requiredFields.filter((field) => {
    const regex = new RegExp(`^${field}:`, "m");
    return !regex.test(frontmatter);
  });

  return { path, missing };
}

try {
  const files = getChangedAgentFiles();
  if (files.length === 0) {
    console.log("No changed agent/skill files to validate.");
    process.exit(0);
  }

  const failures = files
    .map(validate)
    .filter((item) => item.missing.length > 0);

  if (failures.length === 0) {
    console.log(`Validated ${files.length} changed agent/skill files.`);
    process.exit(0);
  }

  for (const failure of failures) {
    console.error(`Missing required guardrail fields in ${failure.path}: ${failure.missing.join(", ")}`);
  }

  process.exit(1);
} catch (error) {
  console.error(`Validation script failed: ${error.message}`);
  process.exit(1);
}
