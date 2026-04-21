CREATE TABLE IF NOT EXISTS memory_entities (
    name TEXT PRIMARY KEY,
    entity_type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS memory_observations (
    id BIGSERIAL PRIMARY KEY,
    entity_name TEXT NOT NULL REFERENCES memory_entities(name) ON DELETE CASCADE,
    content TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_relations (
    id BIGSERIAL PRIMARY KEY,
    from_entity_name TEXT NOT NULL REFERENCES memory_entities(name) ON DELETE CASCADE,
    to_entity_name TEXT NOT NULL REFERENCES memory_entities(name) ON DELETE CASCADE,
    relation_type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (from_entity_name, to_entity_name, relation_type)
);

CREATE INDEX IF NOT EXISTS ix_memory_entities_updated ON memory_entities(updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_memory_observations_entity ON memory_observations(entity_name);
CREATE INDEX IF NOT EXISTS ix_memory_relations_from ON memory_relations(from_entity_name);
CREATE INDEX IF NOT EXISTS ix_memory_relations_to ON memory_relations(to_entity_name);
