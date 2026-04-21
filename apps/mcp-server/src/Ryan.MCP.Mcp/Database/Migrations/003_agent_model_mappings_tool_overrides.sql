-- Adds optional per-tool model overrides for agent mappings.
-- Stored as JSON object string, e.g. {"opencode":"claude-sonnet-4-6","copilot":"gpt-5.3-codex"}
ALTER TABLE agent_model_mappings
ADD COLUMN IF NOT EXISTS tool_overrides_json TEXT;
