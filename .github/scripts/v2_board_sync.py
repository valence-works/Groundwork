#!/usr/bin/env python3
"""Recompute every Groundwork v2 board item's Status from GitHub state.

The board is derived, never hand-maintained. Status is a pure function of facts GitHub
already knows, so it cannot drift from reality and no contributor has to remember to
move a card:

    issue closed .................. Done
    open pull request that closes . In review
    has an assignee ............... In progress
    every blocker closed .......... Ready
    otherwise ..................... Blocked

Blockers come from the "## Dependencies" section of each issue body, which states them
as "Blocked by #N" and "Blocks #N" (the reverse edge is read too, so either phrasing
works). Phase-0 gate issues additionally block every non-gate, non-housekeeping item,
because nothing downstream may start until they report.

Run with no arguments to sync. Run with --dry-run to print the plan without writing.
Requires GH_TOKEN with `project` and `repo` scope.
"""
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

REPO_OWNER = "valence-works"
REPO_NAME = "Groundwork"
PROJECT_NUMBER = 5
LABEL = "v2"
GATES = {"G1", "G2", "G3"}

DRY_RUN = "--dry-run" in sys.argv
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
if not TOKEN:
    sys.exit(
        "No GH_TOKEN. In Actions this must be a PAT with `project` scope stored as a "
        "secret — the default GITHUB_TOKEN cannot write to organisation projects."
    )


def graphql(query, **variables):
    payload = json.dumps({"query": query, "variables": variables}).encode()
    request = urllib.request.Request(
        "https://api.github.com/graphql",
        data=payload,
        headers={
            "Authorization": f"bearer {TOKEN}",
            "Content-Type": "application/json",
            "User-Agent": "groundwork-v2-board-sync",
        },
    )
    try:
        with urllib.request.urlopen(request) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        sys.exit(f"GraphQL HTTP {error.code}: {error.read().decode()[:500]}")
    if "errors" in result:
        sys.exit(f"GraphQL errors: {json.dumps(result['errors'])[:800]}")
    return result["data"]


def issue_references(text):
    """Return issue numbers, expanding compact ranges such as #239–#243."""
    numbers = set()
    for match in re.finditer(r"#(\d+)(?:\s*[-–—]\s*#?(\d+))?", text):
        first = int(match.group(1))
        last = int(match.group(2) or first)
        if last < first or last - first > 100:
            numbers.add(first)
            continue
        numbers.update(range(first, last + 1))
    return numbers


def closing_issue_references(text):
    """Return only issue references on GitHub closing-keyword lines."""
    numbers = set()
    closing = re.compile(
        r"^\s*(?:[-*]\s*)?(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b",
        re.IGNORECASE,
    )
    for line in text.splitlines():
        if closing.match(line):
            numbers.update(issue_references(line))
    return numbers


# --------------------------------------------------------------------- read state

PROJECT_QUERY = """
query($org:String!, $number:Int!, $cursor:String) {
  organization(login:$org) {
    projectV2(number:$number) {
      id
      fields(first:50) {
        nodes {
          ... on ProjectV2SingleSelectField { id name options { id name } }
        }
      }
      items(first:100, after:$cursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          id
          content {
            ... on Issue { number state title body assignees(first:1) { totalCount }
                           labels(first:20) { nodes { name } } }
          }
        }
      }
    }
  }
}
"""

fields, items, cursor = None, [], None
while True:
    data = graphql(PROJECT_QUERY, org=REPO_OWNER, number=PROJECT_NUMBER, cursor=cursor)
    project = data["organization"]["projectV2"]
    project_id = project["id"]
    if fields is None:
        fields = {f["name"]: f for f in project["fields"]["nodes"] if f}
    items.extend(project["items"]["nodes"])
    page = project["items"]["pageInfo"]
    if not page["hasNextPage"]:
        break
    cursor = page["endCursor"]

status_field = fields["Status"]
status_option = {o["name"]: o["id"] for o in status_field["options"]}

# Open pull requests with closing-keyword links, so directly implemented items show as In review
# while parent and dependency references do not move unrelated cards.
PR_QUERY = """
query($owner:String!, $repo:String!) {
  repository(owner:$owner, name:$repo) {
    pullRequests(states:OPEN, first:100) { nodes { number title body } }
  }
}
"""
prs = graphql(PR_QUERY, owner=REPO_OWNER, repo=REPO_NAME)["repository"]["pullRequests"]["nodes"]
issues_with_open_pr = set()
for pr in prs:
    issues_with_open_pr.update(
        closing_issue_references(f"{pr['title']}\n{pr['body'] or ''}")
    )

# --------------------------------------------------------------- dependency graph

tracked = {}
for item in items:
    content = item.get("content") or {}
    number = content.get("number")
    if not number:
        continue
    labels = {n["name"] for n in (content.get("labels") or {}).get("nodes", [])}
    if LABEL not in labels:
        continue
    tracked[number] = {"item": item["id"], "content": content, "labels": labels}

work_id = {}
for number, info in tracked.items():
    match = re.match(r"^([A-Z]\d+)\b", info["content"]["title"] or "")
    if match:
        work_id[number] = match.group(1)

blockers = {n: set() for n in tracked}
for number, info in tracked.items():
    body = info["content"]["body"] or ""
    section = body.split("## Dependencies", 1)[1].split("\n---")[0] if "## Dependencies" in body else ""
    flat = " ".join(section.split())
    for match in re.finditer(r"Blocked by ([^.]*)\.", flat):
        for ref in issue_references(match.group(1)):
            if ref in tracked:
                blockers[number].add(ref)
    for match in re.finditer(r"(?:Blocks|[Mm]ust land before) ([^.]*)\.", flat):
        for ref in issue_references(match.group(1)):
            if ref in tracked:
                blockers[ref].add(number)

gate_numbers = {n for n, w in work_id.items() if w in GATES}
for number in tracked:
    work = work_id.get(number, "")
    # E1 is deliberately exempt: its own body says it should start during Phase 0.
    if work and work[0] not in ("G", "H") and work != "E1":
        blockers[number] |= gate_numbers

# ------------------------------------------------------------------ compute + write

def desired(number, info):
    if info["content"]["state"] != "OPEN":
        return "Done"
    # The epic has no work-item prefix; it tracks the programme, not a task.
    if number not in work_id:
        return "In progress"
    if number in issues_with_open_pr:
        return "In review"
    if info["content"]["assignees"]["totalCount"] > 0:
        return "In progress"
    open_blockers = [b for b in blockers[number] if tracked[b]["content"]["state"] == "OPEN"]
    return "Blocked" if open_blockers else "Ready"


MUTATION = """
mutation($project:ID!, $item:ID!, $field:ID!, $option:String!) {
  updateProjectV2ItemFieldValue(input:{
    projectId:$project, itemId:$item, fieldId:$field,
    value:{singleSelectOptionId:$option}}) { projectV2Item { id } }
}
"""

changes = 0
for number in sorted(tracked):
    info = tracked[number]
    want = desired(number, info)
    label = work_id.get(number, f"#{number}")
    if DRY_RUN:
        print(f"  {label:5} #{number:<4} -> {want}")
        continue
    graphql(MUTATION, project=project_id, item=info["item"],
            field=status_field["id"], option=status_option[want])
    changes += 1
    print(f"  {label:5} #{number:<4} -> {want}")

ready = sorted(work_id.get(n, str(n)) for n in tracked
               if desired(n, tracked[n]) == "Ready")
print(f"\n{len(tracked)} tracked, {changes} synced.")
print(f"Ready to start: {', '.join(ready) if ready else 'nothing — check the gates'}")
