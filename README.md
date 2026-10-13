# Gatekeeper

[![CI](https://github.com/AlexeyDavidenko/Gatekeeper/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/AlexeyDavidenko/Gatekeeper/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![PostgreSQL 16](https://img.shields.io/badge/PostgreSQL-16-336791)

**Gatekeeper** screens people who want to join a Telegram community. Each join request is
held until the applicant completes a short survey in a private chat with the bot. Moderators
then approve or reject the application, either from a Telegram admin group or from a web
dashboard.

---

## Contents

- [Features](#features)
- [How it works](#how-it-works)
- [Architecture](#architecture)
- [Tech stack](#tech-stack)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Testing](#testing)
- [Deployment](#deployment)
- [Project status](#project-status)

## Features

**Applicant flow (Telegram bot)**
- Every join request goes through a survey in a private chat with the bot.
- Supports free-text, single-choice, and multiple-choice questions. Prompts can use rich
  formatting.
- The applicant can pick Russian or English before the survey starts. Their choice is saved and
  used for all later messages.
- `/start` explains what to do next, and `/status` shows the state of the applicant's latest
  application.

**Moderation in Telegram**
- An application card goes to the admin group when the survey is complete. It shows the
  applicant's avatar and bio, and the answers can be shown or hidden.
- Moderators can approve or reject an application with one tap on the card.
- Admin commands: `/stats` (summary), `/pending` (review queue), `/find <query>` (fuzzy
  search), and `/cancel <id>`.

**Admin web dashboard**
- Moderators sign in with the Telegram Login Widget, which is verified on the server. Access
  depends on the user's role in the admin group: chat administrators are *Owners* and regular
  members are *Members*.
- The dashboard shows KPI cards, a 14-day approve/reject trend, and a breakdown by status.
- Review queue and decision history, with sortable and filterable data grids.
- Survey editor with a WYSIWYG prompt editor, choice options, and bilingual (RU/EN) content.
- Ban, mute, kick, and warn actions. Each one is recorded in the moderation log, and evidence
  attachments are stored in S3-compatible object storage.
- Owner-only tools: archiving the moderation log, viewing site visitor analytics (with bot
  traffic filtered out), and editing UI translations.
- Dark theme and responsive layout.

**Reliability and operations**
- Decisions made in Telegram always take precedence. Optimistic concurrency stops the web
  dashboard from overriding a decision that was already made in Telegram.
- Every Telegram side effect goes through a transactional outbox, with automatic retries and
  recovery of commands that got stuck.
- Nightly backups of the whole PostgreSQL cluster can be copied to off-site storage. CI checks
  on every push that a backup can be restored.

## How it works

```
 Applicant                Bot                     API                 Moderators
     │  join request       │                       │                        │
     ├────────────────────►│  create application   │                        │
     │                     ├──────────────────────►│                        │
     │◄──── survey (DM) ───┤                       │                        │
     ├──── answers ───────►├──────────────────────►│  card → admin group    │
     │                     │◄──── outbox ──────────┤  and web queue ───────►│
     │                     │                       │◄──── approve/reject ───┤
     │◄─ approved/declined ┤◄──── outbox ──────────┤                        │
```

1. A user sends a join request to the community.
2. The bot opens a private chat and asks the survey questions configured for that community.
3. When the survey is complete, the application goes into the review queue. A card is posted
   to the admin group, and the application also appears on the web dashboard.
4. A moderator approves or rejects it, either in Telegram or on the website. The bot then
   approves or declines the join request and tells the applicant the result.

Application lifecycle:
`JoinRequested → SurveyOffered → InSurvey → AwaitingReview → Approved | Rejected`
(and the terminal states `Abandoned` and `Cancelled`).

## Architecture

The solution follows Clean (onion) Architecture. All dependencies point inward, toward the
domain:

```
Domain  ←  Application  ←  Infrastructure  ←  Api
                ▲                              ▲
             Contracts  ←──────────────  Bot / Web
```

- **Api** is the only service that talks to the database and object storage. **Bot** and
  **Web** depend only on `Contracts` and call the API over a private network. Internal calls
  are authenticated with a shared key.
- **Database per tenant:** each community has its own PostgreSQL database. A shared *Catalog*
  database stores the tenant registry, chat bindings, and UI translations. The API runs
  migrations for the catalog and every tenant database on startup. Provisioning a new tenant
  is idempotent.
- **Transactional outbox:** a state change and the Telegram command that depends on it are
  saved in the same transaction. The bot drains the outbox, runs each command, and reports the
  result back. If a command stays in flight too long, it is reclaimed and retried.
- **Optimistic concurrency** uses PostgreSQL's `xmin` system column. The web dashboard sends
  `If-Match`, and a conflicting write gets `409`. When two moderators click in Telegram at the
  same time, the second click is treated as "already decided".
- **Localization:** bot and website texts are stored in the database and cached by each
  service. Owners can edit them at runtime. Survey content is stored in both languages for
  each question.

## Tech stack

| Area | Technology |
|---|---|
| Runtime | .NET 10, C# 14 |
| Orchestration | .NET Aspire 13: topology as code, Docker Compose publishing |
| API | ASP.NET Core Minimal APIs |
| Persistence | Entity Framework Core 10 + Npgsql, PostgreSQL 16 (`pg_trgm` for fuzzy search) |
| Web UI | Blazor Server (Interactive Server) + MudBlazor |
| Telegram | Telegram.Bot (long polling) |
| Object storage | MinIO (S3-compatible) |
| Observability | OpenTelemetry, health checks, `Microsoft.Extensions.Http.Resilience` |
| Testing | xUnit, Testcontainers (PostgreSQL, MinIO) |
| CI/CD | GitHub Actions → GitHub Container Registry → Docker Compose via Cloudflare Tunnel |

## Repository layout

```
src/
  Gatekeeper.Domain            Entities, aggregates, invariants, application state machine
  Gatekeeper.Application       Use-case handlers and ports
  Gatekeeper.Infrastructure    EF Core, multi-tenancy, MinIO storage, migrations
  Gatekeeper.Contracts         Wire DTOs and the API client interface (shared by Api/Bot/Web)
  Gatekeeper.Api               Minimal API; the only service with database access
  Gatekeeper.Bot               Worker: Telegram long polling + outbox execution
  Gatekeeper.Web               Blazor admin dashboard
  Gatekeeper.ServiceDefaults   Shared Aspire defaults (OpenTelemetry, health, resilience)
  Gatekeeper.AppHost           Deployment topology as code
tests/
  Gatekeeper.Tests             Unit and integration tests
deploy/                        Generated Docker Compose manifest
ops/                           Backup/restore scripts and systemd units
.github/workflows/ci.yml       Build, test, smoke tests, image publishing, deployment
```

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker (or another OCI runtime, such as OrbStack). Aspire runs PostgreSQL and MinIO in
  containers.
- A Telegram bot token from [@BotFather](https://t.me/BotFather), and an admin group where the
  bot is an administrator.

### Configuration

Secrets are never committed. Set the AppHost parameters with `dotnet user-secrets`:

```bash
cd src/Gatekeeper.AppHost

dotnet user-secrets set "Parameters:internal-api-key"        "<random string>"
dotnet user-secrets set "Parameters:telegram-bot-token"      "<bot token>"
dotnet user-secrets set "Parameters:telegram-bot-username"   "<bot username>"
dotnet user-secrets set "Parameters:telegram-admin-chat-id"  "<admin group chat id>"
dotnet user-secrets set "Parameters:tenant-id"               "<tenant id>"
dotnet user-secrets set "Parameters:minio-root-user"         "<user>"
dotnet user-secrets set "Parameters:minio-root-password"     "<password>"
dotnet user-secrets set "Parameters:cloudflare-tunnel-token" "<tunnel token>"
```

`tenant-id` is returned when you provision a tenant through the API
(`POST /internal/tenants/provision`).

### Run

```bash
dotnet build Gatekeeper.slnx
dotnet run --project src/Gatekeeper.AppHost
```

The Aspire dashboard starts the whole stack (PostgreSQL, MinIO, API, bot, and web) and shows
its logs, traces, and health.

## Testing

```bash
dotnet test Gatekeeper.slnx
```

The test suite has two parts:

- **Unit tests:** the domain state machine and invariants, use-case handlers (using in-memory
  fakes), Telegram Login Widget signature validation, and localization.
- **Integration tests:** real PostgreSQL and MinIO containers started with Testcontainers.
  They cover concurrent decisions, outbox recovery, cross-tenant endpoints, fuzzy search,
  dashboard aggregation, and evidence storage. Docker must be running.

On every push and pull request, CI also applies migrations to a fresh PostgreSQL instance,
smoke-tests the running API and web app, checks that tenant provisioning is idempotent, and
runs a backup → restore → row-count comparison.

## Deployment

Production runs as a Docker Compose stack generated from the Aspire AppHost:

```bash
aspire publish -o ./deploy
```

- After a green build on `main`, CI publishes the `api`, `bot`, and `web` container images
  to GHCR. It then redeploys only the services that changed.
- The web dashboard is the only public service. It is exposed through a Cloudflare Tunnel, and
  no ports are opened on the host. The bot uses outbound long polling, and the API and storage
  are reachable only on the private network.
- `ops/` contains the nightly `pg_dumpall` backup (with optional off-site copy through
  `rclone`), the matching restore script, and the systemd timers that schedule them.

## Project status

Gatekeeper is under active development and runs in production for a live community. The web
dashboard currently supports one tenant per deployment. Multi-tenant web hosting is planned.
