CREATE TABLE IF NOT EXISTS workflow_states (
    workflow_id TEXT PRIMARY KEY,
    command TEXT NOT NULL,
    title TEXT NOT NULL,
    status TEXT NOT NULL,
    step_index INTEGER NOT NULL,
    step_id TEXT NOT NULL,
    step_title TEXT NOT NULL,
    context TEXT NULL,
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_workflow_states_status_updated
    ON workflow_states(status, updated_utc DESC);

CREATE INDEX IF NOT EXISTS idx_workflow_states_command_updated
    ON workflow_states(command, updated_utc DESC);
