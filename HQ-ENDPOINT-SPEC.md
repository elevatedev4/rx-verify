# HQ endpoint spec: `/api/rxverify-reports`

Status: **not implemented yet.** This document specifies the contract the
`rx-verify` C# overlay (`overlay/RxVerifyOverlay`, see
`Reporting/RxReportSubmitter.cs` and `Integrated/ReportErrorWindow.xaml.cs`)
already codes against, so a manager-hq coder can implement the other side
next. This spec lives in the `rx-verify` repo (this worktree) because that's
where the client contract is defined; the actual route/DB work happens in
the separate `manager-hq` repo — **do not implement it here.**

## Why a new, separate endpoint + secret (design decision, already made)

rx-verify has no cloud of its own. The overlay needs to send pharmacist
"Report error…" corrections somewhere, and Manager HQ is the only server
Will already operates. But the overlay ships to a pharmacy workstation, and
**must never embed the full Manager HQ bearer secret** — that secret can
read/write the entire HQ surface (questions, messages, flags, other
projects' data). Embedding it in a client binary that leaves Will's own
machines is a real credential-scope violation, not a hypothetical one.

So: a **brand-new, dedicated, low-privilege secret**
(`RXVERIFY_REPORT_KEY`), authorizing **only** `POST /api/rxverify-reports`
(create). It must not work against any other HQ route. The manager-side GET
(listing/triage, section below) uses the existing full HQ secret instead —
that call only ever happens from Will's own manager session, never from the
overlay.

## POST /api/rxverify-reports — create a report

**Method:** `POST`

**Auth:** `Authorization: Bearer <RXVERIFY_REPORT_KEY>`
— a new env var, distinct from the existing Manager HQ secret
(`$HOME/claude/manager-app/.hq-secret` equivalent). Reject with `401` if
missing/wrong. This key must **only** authorize this one route — do not
reuse the existing bearer-check middleware if that middleware also grants
access to any other `/api/*` route.

**Headers:**
```
Authorization: Bearer <RXVERIFY_REPORT_KEY>
Content-Type: application/json
```

**Body (JSON) — mirrors `Reporting/RxReportPayload.cs` exactly, camelCase:**

```json
{
  "app": "rx-verify",
  "engineBuild": "a1b2c3d 2026-08-13T04:00:00Z",
  "commit": "deadbee",
  "field": "quantity",
  "source": "30",
  "entered": "60",
  "status": "red",
  "reasonCode": "qty_mismatch",
  "explanation": "Quantity mismatch between source and entered.",
  "correction": "Should be 30, matches the e-script. Entered was fat-fingered.",
  "createdAt": "2026-08-13T15:04:05Z"
}
```

| Field | Type | Notes |
|---|---|---|
| `app` | string | Always `"rx-verify"` — future-proofing if HQ ever accepts reports from another app. |
| `engineBuild` | string \| null | `"<sha> <builtAt>"` of the TypeScript engine subprocess, or `null` if the engine's handshake never ran. |
| `commit` | string \| null | The C# overlay's own git commit (8 chars), or `null` if it couldn't be resolved. |
| `field` | string | One of `rx-verify`'s 13 `FieldOrder.Fields` keys (`patientName`, `patientDOB`, `patientAddress`, `prescriberName`, `prescriberNpi`, `prescriberPhone`, `prescriberAddress`, `dateWritten`, `quantity`, `refills`, `daw`, `drug`, `sig`). |
| `source` | string \| null | The engine's source-side value for this field. **Never a real patient-identity value** — see PHI note below. |
| `entered` | string \| null | Same, entered side. |
| `status` | string | Lowercase verdict color: `"green"` \| `"yellow"` \| `"red"`. **Do not confuse with the report's own lifecycle status below** — this is the verdict color, not triage state. |
| `reasonCode` | string \| null | The engine's machine-readable reason code for the verdict. |
| `explanation` | string \| null | The engine's human-readable explanation line. |
| `correction` | string | Free text the pharmacist typed — never redacted client-side (see PHI note). |
| `createdAt` | string (ISO-8601, UTC) | Set client-side at submit time. |

### PHI note (already enforced client-side — trust but verify)

`field` is never one of the 3 patient-identity fields
(`patientName`/`patientDOB`/`patientAddress`) — the overlay hides the
"Report error…" affordance entirely for those (see
`Integrated/VerdictFieldInfo.cs` `IsPatientField` and
`Integrated/IntegratedBoxesWindow.cs` `BuildHotspotContextMenu`), and
`Reporting/RxReportPayload.cs` `RxReportBuilder.Build` redacts
`source`/`entered` to the literal string `"[redacted]"` as a second,
belt-and-suspenders layer even if that guard is ever bypassed. **The HQ
endpoint should still validate this defensively** (see "Server-side
validation" below) rather than assuming the client always behaves — a
future client bug or a hand-crafted request should not be able to smuggle
patient data into the reports table.

The `correction` free-text field is **not** scrubbed and could theoretically
contain PHI if a pharmacist types it in by hand for a non-patient field
report (e.g. quoting the patient's name while describing a drug mix-up).
Treat the reports table itself as **PHI-adjacent**: no public exposure, no
logging of full request bodies, standard HQ data-handling hygiene.

### Server-side validation (recommended)

- Reject (`400`) if `field` is missing, empty, or not one of the 13 known
  `FieldOrder` keys.
- Reject (`400`) if `field` is `patientName`, `patientDOB`, or
  `patientAddress` — belt-and-suspenders against the client-side guard above
  ever being bypassed (bug, hand-crafted request, future client version).
- Reject (`400`) if `status` isn't one of `green`/`yellow`/`red`.
- Reject (`400`) if `correction` is empty/whitespace-only — a report with no
  actual description isn't actionable.
- Cap `correction` length server-side (e.g. 4000 chars) — free text from an
  arbitrary bearer-holder should never be allowed to write an unbounded
  blob.

### Response

**201 Created** on success:
```json
{ "id": "rpt_abc123" }
```
`id` is whatever primary-key/identifier scheme HQ's storage layer already
uses (matches the existing `W-T20`/`E-Q4`-style convention loosely, or a
plain UUID/DB id — coder's choice, just needs to be stable and usable in the
GET response below).

**4xx on failure** — plain JSON error body, matching whatever shape HQ's
other endpoints already use for errors (e.g. `{ "error": "..." }`):
- `401` — missing/wrong bearer key.
- `400` — validation failure (see above).
- `429` — rate limited (see below).

### Rate limiting

This is a low-volume, pharmacist-driven action (a human clicking "Report
error…" and typing a sentence) — it will never legitimately fire in a tight
loop. A generous but real limit protects against a runaway client bug (e.g.
a retry loop with a bad backoff) hammering the endpoint:

**Suggested: 20 requests/minute per bearer key.** Return `429` with a
`Retry-After` header on exceeding it. This does not need to be
sophisticated (in-memory/edge-config counter is fine, matching whatever
lightweight approach HQ already uses elsewhere) — the goal is "stop a bug
from writing thousands of rows," not defending against a real attacker (the
key is low-privilege and create-only in the first place).

### Storage (the "store all those reports and the result" part)

Persist every accepted report as its own record: the full payload above,
plus server-added fields:

| Field | Type | Notes |
|---|---|---|
| `id` | string | Returned in the `201` response. |
| `receivedAt` | ISO-8601 timestamp | When HQ accepted it (server clock, not the client's `createdAt`). |
| `reportStatus` | string | Lifecycle state — see below. Defaults to `"new"`. **Distinct from the payload's own `status` field** (verdict color) — name it something unambiguous like `reportStatus` in storage/API responses so the two never get confused when both appear in the same JSON object. |
| `resolutionNote` | string \| null | Free text Will adds when triaging (e.g. "fixed in engine v1.4, was a normalize bug" or "not a bug, entered value was actually correct"). Null until triaged. |
| `resolvedAt` | ISO-8601 timestamp \| null | When `reportStatus` last changed away from `"new"`. |

`reportStatus` values: `"new"` (default, untriaged) → `"accepted"`
(confirmed real issue, not yet fixed) → `"fixed"` (shipped a fix) |
`"rejected"` (not actually a bug — false alarm, expected behavior, etc.).

This is the "we probably need to store all those reports and the result of
what came out of it so that we can modify that if there are errors found
later" part of the owner's original ask — the point is a durable audit
trail Will can mine later (which fields generate the most reports, which
ones turned into real engine fixes) even though there's no UI for that yet.

## GET (for the manager) — list/triage reports

**Method:** `GET /api/rxverify-reports`

**Auth:** the **existing, full** Manager HQ bearer secret (not
`RXVERIFY_REPORT_KEY` — this is a manager-only surface, read by Will's own
manager session, never by the overlay client). If it's simplest to reuse
whatever auth middleware already gates HQ's other manager-facing GET
routes, do that.

**Suggested query params** (all optional):
- `reportStatus` — filter to one lifecycle state (e.g. `?reportStatus=new`
  for an unread-reports queue).
- `field` — filter to one field key.
- `limit` — default/cap similar to HQ's existing list endpoints.

**Response:** array of the full stored record shape (payload fields +
`id`/`receivedAt`/`reportStatus`/`resolutionNote`/`resolvedAt`), newest
first.

**Suggested mutation** (needed to actually triage — add if not already
planned as a separate ticket): `PATCH /api/rxverify-reports/:id` with body
`{ "reportStatus": "...", "resolutionNote": "..." }`, same full-secret auth
as the GET above. Not required for v0 (a coder could ship GET-only first and
Will triages by hand-editing storage), but the whole point of storing
`reportStatus`/`resolutionNote` is to eventually support this — flagging it
now so the schema above doesn't need to change later to support it.

## What the overlay client already does (context, not a spec for HQ)

- `overlay/RxVerifyOverlay/Reporting/RxReportSubmitter.cs` — POSTs with an
  8s timeout; on **any** failure (missing key, network error, timeout,
  non-2xx) it fails soft: queues the payload to
  `%AppData%\RxVerifyOverlay\pending-reports.jsonl` instead of surfacing an
  error to the pharmacist. Retried once per app launch
  (`RetryPendingAsync`, called from `MainWindow.xaml.cs`'s constructor) —
  only when `RxVerifyReportKey` is actually configured.
- The overlay shows the pharmacist only two possible outcomes: "Sent." or
  "Saved — will send once connected." Both read as success. There is
  currently no way for the overlay to learn that HQ later rejected a queued
  report (e.g. failed validation) — a `4xx` on retry just gets silently
  re-queued forever by the current client logic. If a coder wants to close
  that gap later, the client would need to distinguish "retryable" (5xx,
  network) from "permanently invalid" (4xx) responses and drop the latter
  instead of re-queuing — **not built in v0**, flagging as a known gap.
