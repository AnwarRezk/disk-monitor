# Issue tracker: Linear

Issues and specs for diskdrift live in **Linear**, workspace `disk-monitor`, team **DIS** (https://linear.app/disk-monitor/team/DIS/active). Issue identifiers look like `DIS-123`.

Use the Linear MCP connector (tools such as `create_issue`, `get_issue`, `list_issues`, `update_issue`, `create_comment`) when it is available in the session. If no Linear tool is available, don't guess at an API: write the issue out in full (title, description, labels, parent, blockers) for the user to paste into Linear, and say so.

## Conventions

- **Create an issue**: create it in team `DIS` with a title and a Markdown description. New issues get the `needs-triage` label unless a skill says otherwise.
- **Read an issue**: fetch it by identifier (`DIS-123`), including description, labels, state, parent, relations and comments.
- **List issues**: list team `DIS` issues filtered by state and/or label (e.g. open issues labelled `ready-for-agent`).
- **Make an issue a sub-issue of a parent**: set the child's parent to the parent issue. Linear sub-issues are native and UI-visible.
- **Blocking**: use Linear's native **"blocked by" / "blocks"** issue relations. A ticket is unblocked when every blocker is in a completed or canceled state. If relations can't be set, put `Blocked by: DIS-12, DIS-34` at the top of the description.
- **Count open blockers**: read the issue's "blocked by" relations and count those not in a completed/canceled state (0 means startable).
- **Comment on an issue**: add a comment to the issue.
- **Apply / remove labels**: add or remove team labels on the issue. Labels must exist in the DIS team; create any that are missing (see `docs/agents/triage-labels.md`).
- **Close**: post the explanation as a comment first, then move the issue to a completed state (`Done`), or `Canceled` for `wontfix`.

File issues only into the `disk-monitor` workspace's DIS team. Never file into `mattpocock/skills` or any other tracker.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if external PRs should be triaged like issues; `/triage` reads this flag.)_

## When a skill says "publish to the issue tracker"

Create a Linear issue in team DIS.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single Linear issue with **sub-issues** as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog description.
- **Child ticket**: a sub-issue of the map. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: see **Blocking** under Conventions.
- **Frontier query**: list the map's open sub-issues, drop any with an open blocker or an assignee; first in map order (sub-issue sort order) wins.
- **Claim**: assign the issue to the current user (and move it to `In Progress`), the session's first write.
- **Resolve**: comment the answer, move the issue to `Done`, then append a context pointer (gist + link) to the map's Decisions-so-far.
