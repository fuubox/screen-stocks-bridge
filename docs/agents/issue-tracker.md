# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a file for multiline bodies.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments`, with appropriate label and state filters.
- **Make an issue a sub-issue of a parent**: use GitHub sub-issues when available. Otherwise, put `Part of #<parent>` at the top of the child body.
- **Blocking**: use GitHub's native issue dependencies when available. Otherwise, put `Blocked by: #<n>` at the top of the child body.
- **Comment on an issue**: `gh issue comment <number> --body "..."`.
- **Apply or remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`.
- **Close**: explain the outcome in a comment, then close the issue.

Infer the repo from `git remote -v`. Before the first issue creation in a session, confirm the target with `gh repo view --json nameWithOwner`. File issues only into the user's repo.

## Pull requests as a triage surface

**PRs as a request surface: no.** Keep this off unless the repo decides to triage external PRs as feature requests.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## Wayfinding operations

The map is one issue labelled `wayfinder:map`; work items are child issues labelled `wayfinder:<type>` (`research`, `prototype`, `grilling`, or `task`). Use GitHub sub-issues where available; otherwise use task lists and `Part of #<map>`. Use native issue dependencies for blockers where available.
