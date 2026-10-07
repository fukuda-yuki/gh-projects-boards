"""Inspect/register the shared WBS or simulate a week in the authorized sandbox.

Default is read-only. --apply explicitly enables writes; receipts make completed
weekly stages idempotent without overwriting subsequent PMO corrections.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import time
from datetime import date, datetime, timedelta, timezone

from generate_version import DEFAULT_PATH

REPOSITORY = "fukuda-yuki/codex-sandbox"
OWNER = "fukuda-yuki"
PROJECT_NUMBER = 3
GH = r"C:\Program Files\GitHub CLI\gh.exe"


def api(query, variables=None):
    environment = dict(os.environ)
    for key in ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG", "GH_HOST", "GH_REPO"):
        environment.pop(key, None)
    environment["GH_PROMPT_DISABLED"] = "1"
    result = subprocess.run([GH, "api", "--hostname", "github.com", "graphql", "--input", "-"],
                            input=json.dumps(dict(query=query, variables=variables or {})), text=True,
                            encoding="utf-8", capture_output=True, env=environment, timeout=90)
    if result.returncode:
        # No uncertain mutation is retried. A fresh inventory reconciles it on the next run.
        raise RuntimeError(f"gh failed ({result.returncode}): {result.stderr[:1000]}")
    response = json.loads(result.stdout)
    if response.get("errors"):
        raise RuntimeError(json.dumps(response["errors"], ensure_ascii=False)[:1500])
    return response["data"]


def wbs_key(title):
    match = re.match(r"^(R\d{2})(?: ([A-Z]{2})-(\d{3}))? ", title)
    if not match:
        return None
    return "-".join(part for part in match.groups() if part)


def index_issues(nodes, expected):
    result = {}
    for issue in nodes:
        key = wbs_key(issue["title"])
        if key not in expected:
            continue
        if key in result:
            raise ValueError(f"Duplicate WBS key {key}; no safe automatic adoption.")
        if issue["title"] != expected[key]["title"]:
            raise ValueError(f"Title differs for {key}; preserve it for review.")
        result[key] = issue
    return result


def pages(query, variables, select):
    nodes, cursor, seen, total = [], None, set(), None
    while True:
        connection = select(api(query, {**variables, "cursor": cursor}))
        if total is not None and total != connection["totalCount"]:
            raise ValueError("Connection count changed during traversal; retry a fresh read.")
        total = connection["totalCount"]
        for node in connection["nodes"]:
            if node is None or node["id"] in seen:
                raise ValueError("Null or repeated identity in traversal.")
            seen.add(node["id"])
            nodes.append(node)
        info = connection["pageInfo"]
        if not info["hasNextPage"]:
            return nodes, total
        if not info["endCursor"] or info["endCursor"] == cursor:
            raise ValueError("Invalid pagination cursor.")
        cursor = info["endCursor"]


def inventory(manifest):
    project = api('query{user(login:"fukuda-yuki"){projectV2(number:3){id number title owner{... on User{login}} fields(first:50){nodes{... on ProjectV2Field{id name dataType}} pageInfo{hasNextPage}}}}}')['user']['projectV2']
    if project["number"] != 3 or project["owner"]["login"] != OWNER or project["title"] != manifest["title"]:
        raise ValueError("The authorized Project identity/title does not match.")
    if project["fields"]["pageInfo"]["hasNextPage"]:
        raise ValueError("Field inventory exceeds this bounded evaluation script.")
    issues, issue_total = pages('''query($cursor:String){repository(owner:"fukuda-yuki",name:"codex-sandbox"){
      issues(first:100,after:$cursor,orderBy:{field:CREATED_AT,direction:ASC}){
        totalCount pageInfo{hasNextPage endCursor} nodes{id number title parent{id} }}}}''', {}, lambda d: d['repository']['issues'])
    if len(issues) != issue_total:
        raise ValueError("Incomplete repository Issue inventory; cannot infer missing WBS rows.")
    items, total = pages('''query($id:ID!,$cursor:String) {
      node(id:$id) { ... on ProjectV2 {
        items(first:100,after:$cursor) {
          totalCount pageInfo { hasNextPage endCursor }
          nodes {
            id isArchived content { ... on Issue { id number title repository { nameWithOwner } } }
            fieldValues(first:30) {
              pageInfo { hasNextPage }
              nodes { ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2Field { name } } } }
            }
          }
        }
      } }
    }''',
                         dict(id=project['id']), lambda d: d['node']['items'])
    for item in items:
        if item['fieldValues']['pageInfo']['hasNextPage']:
            raise ValueError("Incomplete item field values.")
    expected = {r['key']: r for r in manifest['rows']}
    matched = index_issues(issues, expected)
    by_issue = {i['content']['id']: i for i in items if i.get('content') and i['content'].get('repository', {}).get('nameWithOwner') == REPOSITORY}
    sheet_issue_ids = [i['content']['id'] for i in items
                       if not i['isArchived'] and i.get('content') and i['content'].get('repository')]
    return project, matched, by_issue, dict(reported=total, delivered=len(items),
        inaccessible=total-len(items)+sum(not i.get('content') for i in items), sheetIssueIds=sheet_issue_ids)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2)+"\n", encoding='utf-8')
    temporary.replace(path)


def export_inputs(manifest, evidence, through, offline=False):
    selected = [r for r in manifest['rows'] if int(r['key'][1:3]) <= through]
    if not offline:
        project, issues, _, counts = inventory(manifest)
        if any(row['key'] not in issues for row in selected):
            raise ValueError('The selected WBS is incomplete; no first-pass inputs were exported.')
        expected_order = [issues[row['key']]['id'] for row in selected]
        if counts['sheetIssueIds'] != expected_order:
            raise ValueError('The entire sheet Issue order must match the selected WBS; extra, archived or reordered Issues make row references unsafe.')
    positions = {r['key']: index+1 for index, r in enumerate(selected)}
    lines = []
    for row in selected:
        is_task = row['parent'] is not None
        assignee = 'person-'+row['person'] if offline and row['person'] else OWNER if row['person'] == 'U1' else ''
        lines.append('\t'.join([row['title'], assignee, str(row['estimate']) if is_task else '',
                                str(row['estimate']) if is_task else '', '0' if is_task else '', '', '',
                                ', '.join(str(positions[p]) for p in row['predecessors']), row['start'] or '']))
    evidence.mkdir(parents=True, exist_ok=True)
    if not offline:
        save(evidence / 'first-pass-order.json', dict(project=project['id'], repository=REPOSITORY,
             through=through, keys=[row['key'] for row in selected], issueIds=expected_order))
    (evidence/'first-pass.tsv').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    print(f"Prepared {len(lines)} rows for the ordinary sheet: title through 開始日指定. No GitHub writes.")


def register(manifest, evidence, apply, through, only_requirement=None):
    project, issues, items, counts = inventory(manifest)
    selected = [r for r in manifest['rows'] if int(r['key'][1:3]) <= through and
                (only_requirement is None or int(r['key'][1:3]) == only_requirement)]
    missing = [r['key'] for r in selected if r['key'] not in issues]
    additions = [r['key'] for r in selected if r['key'] in issues and issues[r['key']]['id'] not in items]
    parents = [r['key'] for r in selected if r['parent'] and
               (r['key'] not in issues or not issues[r['key']].get('parent') or
                issues[r['key']]['parent']['id'] != issues.get(r['parent'], {}).get('id'))]
    if len(missing) > 40:
        raise ValueError("Refusing mass recreation: more than 40 WBS Issues are absent.")
    for row in selected:
        existing = (issues.get(row['key'], {}).get('parent') or {}).get('id')
        if existing and existing != issues.get(row['parent'], {}).get('id'):
            raise ValueError(f"Existing different parent for {row['key']}; no writes performed.")
    report = dict(project=project['id'], repository=REPOSITORY, through=through, counts=counts,
                  matched=len(issues), missing=missing, additions=additions, parents=parents)
    save(evidence / 'registration-plan.json', report)
    print(json.dumps(dict(through=through, matched=len(issues), missing=len(missing), additions=len(additions), parents=len(parents),
                         counts={key: value for key, value in counts.items() if key != 'sheetIssueIds'})), flush=True)
    if not apply:
        return
    repository_id = api('query{repository(owner:"fukuda-yuki",name:"codex-sandbox"){id}}')['repository']['id']
    for row in selected:
        if row['key'] not in issues:
            issue = api('mutation($input:CreateIssueInput!){createIssue(input:$input){issue{id number title parent{id}}}}',
                        dict(input=dict(repositoryId=repository_id, title=row['title'], body=f"Synthetic version evaluation ({row['key']}); fukuda-yuki/gh-projects-boards#89.")))['createIssue']['issue']
            issues[row['key']] = issue
            save(evidence / 'created-issues.json', {k: issues[k] for k in missing if k in issues})
            print('created', row['key'], issue['number'], flush=True)
            time.sleep(2)
        issue = issues[row['key']]
        if issue['id'] not in items:
            item = api('mutation($input:AddProjectV2ItemByIdInput!){addProjectV2ItemById(input:$input){item{id}}}',
                       dict(input=dict(projectId=project['id'], contentId=issue['id'])))['addProjectV2ItemById']['item']
            items[issue['id']] = item
    links = []
    for row in selected:
        if not row['parent']:
            continue
        child, parent = issues[row['key']], issues[row['parent']]
        existing = (child.get('parent') or {}).get('id')
        if existing and existing != parent['id']:
            raise ValueError(f"Existing different parent for {row['key']}; never reparent silently.")
        if existing != parent['id']:
            links.append(dict(issueId=parent['id'], subIssueId=child['id']))
    for offset in range(0, len(links), 10):
        batch = links[offset:offset+10]
        declarations = ','.join(f'$v{i}:AddSubIssueInput!' for i in range(len(batch)))
        operations = ' '.join(f'a{i}:addSubIssue(input:$v{i}){{issue{{id}}}}' for i in range(len(batch)))
        api(f'mutation({declarations}){{{operations}}}', {f'v{i}': v for i,v in enumerate(batch)})
        print('hierarchy', min(offset+10, len(links)), '/', len(links), flush=True)
        time.sleep(2)
    # Explicitly place only WBS items. Other/inaccessible Project members stay untouched.
    current = [issue_id for issue_id in items if issue_id in {v['id'] for v in issues.values()}]
    desired = [issues[r['key']]['id'] for r in selected]
    selected_ids = set(desired)
    if only_requirement is not None:
        desired = [identity for identity in current if identity not in set(desired)] + desired
    previous = None
    moves = 0
    for index, issue_id in enumerate(desired):
        if only_requirement is not None and issue_id not in selected_ids:
            previous = items[issue_id]['id']
            continue
        if current.index(issue_id) != index:
            input_value = dict(projectId=project['id'], itemId=items[issue_id]['id'])
            if previous:
                input_value['afterId'] = previous
            api('mutation($input:UpdateProjectV2ItemPositionInput!){updateProjectV2ItemPosition(input:$input){clientMutationId}}', dict(input=input_value))
            current.remove(issue_id)
            current.insert(index, issue_id)
            moves += 1
            if moves % 25 == 0:
                print('positions', moves, flush=True)
            time.sleep(.5)
        previous = items[issue_id]['id']
    _, verified, verified_items, verified_counts = inventory(manifest)
    for row in selected:
        issue = verified[row['key']]
        if issue['id'] not in verified_items or row['parent'] and (issue.get('parent') or {}).get('id') != verified[row['parent']]['id']:
            raise ValueError('Registration readback mismatch: '+row['key'])
    if [identity for identity in verified_items if identity in set(desired)] != desired:
        raise ValueError('Project order readback differs from the registered order.')
    report.update(verified=True, writes=dict(created=len(missing), added=len(additions)+len(missing), hierarchy=len(links), positions=moves), counts=verified_counts)
    save(evidence / 'registration-result.json', report)
    print('verified registration', len(selected), flush=True)


def weekly_stage_ready(evidence, week):
    receipt = evidence / f'week-{week}.json'
    if receipt.exists() and json.loads(receipt.read_text(encoding='utf-8')).get('complete'):
        print(f'Week {week} already applied; preserving subsequent PMO edits.')
        return False
    if week > 1:
        previous = evidence / f'week-{week-1}.json'
        if not previous.exists() or not json.loads(previous.read_text(encoding='utf-8')).get('complete'):
            raise ValueError('Apply preceding weekly stage first.')
    return True


def weekly_progress(manifest, estimates, week):
    status_date = date.fromisoformat({1: '2026-10-21', 2: '2026-10-28', 3: '2026-11-04', 4: '2026-11-11'}[week])
    holiday_file = Path(__file__).resolve().parents[2] / 'tests/GhProjectsBoards.Tests/Fixtures/JapanHolidays.csv'
    holidays = set()
    for line in holiday_file.read_text(encoding='cp932').splitlines()[1:]:
        value = line.split(',')[0]
        try:
            holidays.add(datetime.strptime(value, '%Y/%m/%d').date())
        except ValueError:
            raise ValueError('Invalid bundled holiday date: '+value)
    updates = {}
    for requirement in range(1, 21):
        rows = [r for r in manifest['rows'] if r['parent'] == f'R{requirement:02d}']
        start = date.fromisoformat(rows[0]['start'])
        budget = sum(8 for offset in range(max(0, (status_date-start).days))
                     if (start+timedelta(days=offset)).weekday() < 5 and start+timedelta(days=offset) not in holidays)
        if requirement == 5:
            budget = max(0, budget-16)  # Two days of delayed progress; successors retain carryover.
        for row in rows:
            estimate = estimates[row['key']]
            work = estimate + (16 if row['key'] == 'R07-UI-001' else 0)
            actual = min(work, budget)
            budget -= actual
            updates[row['key']] = dict(Actual=actual, Remaining=work-actual)
    return status_date.isoformat(), updates


def simulate_week(manifest, evidence, apply, week):
    receipt = evidence / f'week-{week}.json'
    if not weekly_stage_ready(evidence, week):
        return
    project, issues, items, _ = inventory(manifest)
    field_ids = {f['name']: f['id'] for f in project['fields']['nodes'] if f.get('dataType') == 'NUMBER'}
    if not {'Estimate', 'Actual', 'Remaining'} <= field_ids.keys():
        raise ValueError('Required planning fields are absent.')
    estimates, task_items = {}, {}
    for row in manifest['rows']:
        if row['parent'] is None or int(row['key'][1:3]) > 20:
            continue
        issue = issues[row['key']]
        item = items[issue['id']]
        values = {v['field']['name']: v['number'] for v in item['fieldValues']['nodes'] if 'number' in v}
        estimate = values.get('Estimate')
        if estimate is None or estimate <= 0:
            raise ValueError(f"Publish the PMO's first pass before simulating {row['key']}.")
        estimates[row['key']], task_items[row['key']] = estimate, (item, values)
    status_date, progress = weekly_progress(manifest, estimates, week)
    updates = []
    for key, desired in progress.items():
        item, values = task_items[key]
        for field, value in desired.items():
            if values.get(field) != value:
                updates.append(dict(key=key, itemId=item['id'], field=field, before=values.get(field), after=value))
    if receipt.exists():
        saved = json.loads(receipt.read_text(encoding='utf-8'))
        if saved.get('project') != project['id']:
            raise ValueError('Receipt belongs to a different Project.')
        # Resume the captured operation, never recalculate intent from a partially changed week.
        updates = saved['updates']
        current_items = {v['id']: v for v in items.values()}
        for update in updates:
            values = {v['field']['name']: v['number'] for v in current_items[update['itemId']]['fieldValues']['nodes'] if 'number' in v}
            if values.get(update['field']) not in (update['before'], update['after']):
                raise ValueError('PMO changed a pending simulated field; preserve it for review.')
    result = dict(week=week, statusDate=status_date, project=project['id'], updates=updates, complete=False)
    print('week', week, 'field updates', len(updates), flush=True)
    if not apply:
        save(evidence / f'week-{week}-plan.json', result)
        return
    save(receipt, result)
    for offset in range(0, len(updates), 30):
        batch = updates[offset:offset+30]
        variables = {f'v{i}': dict(projectId=project['id'], itemId=u['itemId'], fieldId=field_ids[u['field']], value=dict(number=u['after'])) for i,u in enumerate(batch)}
        declarations = ','.join(f'$v{i}:UpdateProjectV2ItemFieldValueInput!' for i in range(len(batch)))
        operations = ' '.join(f'a{i}:updateProjectV2ItemFieldValue(input:$v{i}){{clientMutationId}}' for i in range(len(batch)))
        api(f'mutation({declarations}){{{operations}}}', variables)
        time.sleep(2)
    _, _, verified, _ = inventory(manifest)
    by_item = {v['id']: v for v in verified.values()}
    for update in updates:
        values = {v['field']['name']: v['number'] for v in by_item[update['itemId']]['fieldValues']['nodes'] if 'number' in v}
        if values.get(update['field']) != update['after']:
            raise ValueError('Weekly readback mismatch: '+update['key'])
    result.update(complete=True, completedAt=datetime.now(timezone.utc).isoformat())
    save(receipt, result)
    print('verified week', week, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['register', 'week', 'inputs'])
    parser.add_argument('--manifest', type=Path, default=DEFAULT_PATH)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--through', type=int, choices=[39, 40], default=39)
    parser.add_argument('--week', type=int, choices=[1, 2, 3, 4])
    parser.add_argument('--offline', action='store_true', help='Export inputs with the 20 synthetic assignee logins.')
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    if args.action == 'inputs':
        if args.apply:
            parser.error('inputs is read-only; --apply is not accepted')
        export_inputs(manifest, args.evidence, args.through, args.offline)
    elif args.action == 'register':
        register(manifest, args.evidence, args.apply, args.through)
    else:
        if args.week is None:
            parser.error('week requires --week 1..4')
        if not weekly_stage_ready(args.evidence, args.week):
            return
        if args.week == 3:
            register(manifest, args.evidence / 'r40', args.apply, 40, only_requirement=40)
        simulate_week(manifest, args.evidence, args.apply, args.week)


if __name__ == '__main__':
    main()
