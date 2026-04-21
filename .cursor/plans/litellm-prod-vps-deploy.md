# LiteLLM production deploy (iteration)

## Your infrastructure (given)

- Ubuntu 24.04 Vultr VPS, SSH access configured.
- **nginx + Certbot** terminate TLS; **`llm.ryanmarsh.net` → `127.0.0.1:4000`** (LiteLLM from docker-compose).
- No change required in-repo for nginx/Certbot unless we document optional tuning (streaming/SSE timeouts, `proxy_buffering off`, larger `client_max_body_size` if you upload large payloads).

Implications for Compose:

- LiteLLM can keep binding **`:4000`** on the host (or `127.0.0.1:4000:4000` if you want to ensure it is not reachable except via localhost + nginx).
- Set **`LITELLM_CORS_ORIGINS`** (or config) to your real browser origins (e.g. `https://llm.ryanmarsh.net`) instead of `*` in production if clients are browser-based.

## What can be implemented in this repo (nearly all of the plan)

| Deliverable | In repo? |
|-------------|----------|
| `docker-compose.yaml`: Redis 7+, remove Prometheus, mount `litellm_config.yaml`, `--config`, fix/remove broken `build`, internal DB/Redis, optional bind 4000 to localhost | Yes |
| `litellm_config.yaml`: Redis router + cache (host/port/password), `callbacks: ["otel"]`, Slack alerting, safe `allow_requests_on_db_unavailable`, no committed secrets | Yes |
| `infra/litellm/.env.example`: full key checklist | Yes |
| Remove or stop referencing `prometheus.yml` | Yes |
| `.github/workflows/deploy-litellm.yml`: SSH/rsync + write `.env` from GitHub Secret + `docker compose pull && up -d` | Yes |
| Optional: `infra/litellm/nginx-notes.md` or comments in workflow with suggested `proxy_read_timeout` / SSE — only if you want a doc snippet | Optional (you said avoid extra markdown unless needed; can be comments in workflow instead) |

## What stays on you (outside git)

- Create/update **GitHub Secrets** (`SSH_PRIVATE_KEY`, host, user, multiline `LITELLM_DOTENV` or equivalent).
- **New Relic** ingest key and correct OTLP endpoint (US vs EU).
- **Slack** webhook URL.
- One-time or rotated **`.env`** on the server matching `DATABASE_URL` / Postgres / Redis passwords.

## Execution trigger

When you say **execute / implement the plan**, the agent applies the file changes above; it does not SSH to your VPS automatically.
