SELECT 'CREATE DATABASE bifrost'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'bifrost')\gexec

SELECT 'CREATE DATABASE ryan_mcp'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'ryan_mcp')\gexec
