# AI Usage Declaration

## Tools used

| Tool | Purpose |
|---|---|
| **Claude Code** (Anthropic, model Claude Opus 5.5) running in VS Code | Database design, microservice architecture, implementation of the proxy service, and a separate code review and testing pass |

## How the work was done

1. **Template and coding conventions (human developer).** The developer prepared and designed the solution template. They taught me how they want the code written, including the DAL layer with a generic service and a generic repository that can run operations on any table in the database.
2. **Specification and focus points (human developer).** After the walkthrough of the microservice structure, the developer handed me the specification. They also pointed out the important points and emphases they wanted, so that I would stay focused on them.
3. **Design with AI.** Claude Code was used to design the database schema (`Database/002_Extensions.sql`, which extends the supplied `001_Initial.sql`) and the architecture of the microservice.
4. **Implementation.** We built our own service in the `App.MinimalApi.Service/Proxy` folder, on top of the template structure. The existing `ExternalService` (Refit client) was **not** used for the proxy.
5. **Review.**
   - When the work was finished, the developer did a code review.
   - After that, as I was asked, I opened a new chat in which an AI agent ran a separate, independent code review and testing pass.
   - Verification: `dotnet test` passes, 165 tests. A manual end-to-end run used the real proxy, SQL Server LocalDB, and the mock upstream.

## Responsibility

I reviewed the generated code and documentation, and I remain responsible for the complete submission. I can explain, trace, and modify any part of it, as required by the assessment.
