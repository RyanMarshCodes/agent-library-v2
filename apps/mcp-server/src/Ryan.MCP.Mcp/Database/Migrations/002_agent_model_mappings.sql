-- Agent-to-model mapping with tier, provider, and cost metadata.
-- Synced from agent frontmatter or set manually via MCP tools.
CREATE TABLE IF NOT EXISTS agent_model_mappings (
    agent_name       TEXT PRIMARY KEY,
    tier             TEXT NOT NULL,
    primary_model    TEXT NOT NULL,
    primary_provider TEXT,
    alt_model_1      TEXT,
    alt_provider_1   TEXT,
    alt_model_2      TEXT,
    alt_provider_2   TEXT,
    cost_per_1m_in   NUMERIC(10,4),
    cost_per_1m_out  NUMERIC(10,4),
    notes            TEXT,
    synced_from      TEXT NOT NULL DEFAULT 'frontmatter',
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_agent_model_mappings_tier
    ON agent_model_mappings(tier);
