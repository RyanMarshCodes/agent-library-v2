# MCP Server: Build & Run Guidance

## After Making Changes

Whenever you make changes to the MCP server code (including agent logic, model mapping, or configuration), you must:

1. **Stop the running Aspire AppHost** (if active):
   ```powershell
   aspire stop
   ```
2. **Rebuild the MCP server:**
   ```powershell
   dotnet build -nologo -v:minimal
   ```
3. **Start the Aspire AppHost:**
   ```powershell
   aspire start
   ```

This ensures all changes are picked up and the MCP server is running the latest code.

## Quick Reference
- Use `aspire stop` before rebuilding to avoid file lock errors.
- Use `aspire start` to launch the full stack (AppHost, MCP, Postgres, etc.).
- The Aspire dashboard URL will be shown in the output after starting.

---

For more details, see the main repo README and `global-config/README.md`.
