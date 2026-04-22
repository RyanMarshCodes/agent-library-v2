# Cost Guardrails v1

## Routing Policy

- Default to small/efficient models.
- Escalate model tier only for risk/complexity triggers.
- De-escalate after high-risk step completion.

## Budget Controls

- Per-request token cap.
- Per-workflow spend ceiling.
- Retrieval caps (`top-k`, max chunk size, max context bytes).
- Stop-and-confirm when predicted spend exceeds threshold.

## Caching Policy

- Reuse stable prefixes and summaries.
- Prefer semantic cache for repeated retrieval.
- Invalidate cache after relevant code/doc changes.

## Monitoring Baseline

Track at minimum:

- tokens in/out,
- latency,
- cost,
- cache hit rate,
- retrieval volume.
