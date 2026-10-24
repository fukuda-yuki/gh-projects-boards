"""Register/setup the authorized sandbox, export app inputs or verify the baseline.

Default is read-only. --apply explicitly enables writes.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time

from generate_version import DEFAULT_PATH

REPOSITORY = "fukuda-yuki/codex-sandbox"
OWNER = "fukuda-yuki"
PROJECT_NUMBER = 3
GH = r"C:\Program Files\GitHub CLI\gh.exe"
PHASES = ['SA', 'UI', 'SS', 'PS', 'PG', 'PT', 'IT', 'ST', 'OT']


def api(query, variables=None, *, method=None):
    environment = dict(os.environ)
    for key in ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG", "GH_HOST", "GH_REPO"):
        environment.pop(key, None)
    environment["GH_PROMPT_DISABLED"] = "1"
    command = [GH, "api", "--hostname", "github.com", query if method else "graphql", "--input", "-"]
    if method:
        command += ["--method", method]
    result = subprocess.run(command,
                            input=json.dumps(variables if method else dict(query=query, variables=variables or {})), text=True,
                            encoding="utf-8", capture_output=True, env=environment, timeout=90)
    if result.returncode:
        # No uncertain mutation is retried. A fresh inventory reconciles it on the next run.
        raise RuntimeError(f"gh failed ({result.returncode}): {result.stderr[:1000]}")
    response = json.loads(result.stdout)
    if response.get("errors"):
        raise RuntimeError(json.dumps(response["errors"], ensure_ascii=False)[:1500])
    return response if method else response["data"]


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
    project = api('query{user(login:"fukuda-yuki"){projectV2(number:3){id number title owner{... on User{login}} fields(first:50){nodes{... on ProjectV2FieldCommon{id name dataType} ... on ProjectV2SingleSelectField{options{id name}}} pageInfo{hasNextPage}}}}}')['user']['projectV2']
    if project["number"] != 3 or project["owner"]["login"] != OWNER or project["title"] != manifest["title"]:
        raise ValueError("The authorized Project identity/title does not match.")
    if project["fields"]["pageInfo"]["hasNextPage"]:
        raise ValueError("Field inventory exceeds this bounded evaluation script.")
    issues, issue_total = pages('''query($cursor:String){repository(owner:"fukuda-yuki",name:"codex-sandbox"){
      issues(first:100,after:$cursor,orderBy:{field:CREATED_AT,direction:ASC}){
        totalCount pageInfo{hasNextPage endCursor} nodes{id number title state parent{id} milestone{id title dueOn}
        assignees(first:100){totalCount nodes{id login} pageInfo{hasNextPage}}
        blockedBy(first:100){totalCount nodes{id} pageInfo{hasNextPage}} }}}}''', {}, lambda d: d['repository']['issues'])
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
              nodes {
                ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { name } } }
                ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2FieldCommon { name } } }
                ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { name } } }
              }
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
    positions = {r['key']: index+1 for index, r in enumerate(selected)}
    for row in selected:
        for predecessor in row['predecessors']:
            if predecessor not in positions:
                raise ValueError(f'Missing predecessor {predecessor}; use --through 40. No inputs were exported.')
    if not offline:
        project, issues, _, counts = inventory(manifest)
        if any(row['key'] not in issues for row in selected):
            raise ValueError('The selected WBS is incomplete; no first-pass inputs were exported.')
        expected_order = [issues[row['key']]['id'] for row in selected]
        if counts['sheetIssueIds'] != expected_order:
            raise ValueError('The entire sheet Issue order must match the selected WBS; extra, archived or reordered Issues make row references unsafe.')
    lines = []
    for row in selected:
        is_task = row['parent'] is not None
        assignee = 'person-'+row['person'] if offline and row['person'] else OWNER if row['person'] == 'U1' else ''
        lines.append('\t'.join([row['title'], row['phase'] or '', assignee, str(row['estimate']) if is_task else '',
                                str(row['estimate']) if is_task else '', '0' if is_task else '', row['start'] or '', '',
                                ', '.join(str(positions[p]) for p in row['predecessors'])]))
    evidence.mkdir(parents=True, exist_ok=True)
    if not offline:
        save(evidence / 'first-pass-order.json', dict(project=project['id'], repository=REPOSITORY,
             through=through, keys=[row['key'] for row in selected], issueIds=expected_order))
    (evidence/'first-pass.tsv').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    print(f"Prepared {len(lines)} rows for the ordinary sheet: title through 先行タスク. No GitHub writes.")


def milestone_inventory():
    nodes, total = pages('''query($cursor:String){repository(owner:"fukuda-yuki",name:"codex-sandbox"){
      milestones(first:100,after:$cursor,states:[OPEN,CLOSED]){
        totalCount pageInfo{hasNextPage endCursor} nodes{id title dueOn}}}}''',
        {}, lambda d: d['repository']['milestones'])
    if len(nodes) != total:
        raise ValueError('Incomplete milestone inventory.')
    return nodes


def utc_date(value):
    if not value:
        return None
    return datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone(timezone.utc).date().isoformat()


def evidence_identity(manifest):
    return dict(readAtUtc=datetime.now(timezone.utc).isoformat(),
                manifestSha256=hashlib.sha256(json.dumps(manifest, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest())


def setup_plan(manifest):
    project, issues, items, counts = inventory(manifest)
    milestones = milestone_inventory()
    fields = [f for f in project['fields']['nodes'] if f.get('name') == '工程']
    if len(fields) > 1 or fields and (fields[0]['dataType'] != 'SINGLE_SELECT' or
            [o['name'] for o in fields[0].get('options', [])] != PHASES):
        raise ValueError('Conflicting 工程 field type/options; no writes performed.')
    if manifest['phases'] != PHASES or [m['phase'] for m in manifest['milestones']] != PHASES:
        raise ValueError('Manifest must define exactly the nine ordered phases and milestones.')
    if len({m['title'] for m in manifest['milestones']}) != len(PHASES):
        raise ValueError('Duplicate manifest milestone title.')
    by_phase, missing = {}, []
    for expected in manifest['milestones']:
        # Validate every date before permitting any mutation.
        datetime.strptime(expected['due'], '%Y-%m-%d')
        matches = [m for m in milestones if m['title'] == expected['title']]
        if len(matches) > 1 or matches and utc_date(matches[0]['dueOn']) != expected['due']:
            raise ValueError('Conflicting milestone title/due date: '+expected['title'])
        if matches:
            by_phase[expected['phase']] = matches[0]
        else:
            missing.append(expected)
    manifest_ids = {m['id'] for m in by_phase.values()}
    memberships, observed = [], []
    for row in manifest['rows']:
        issue = issues.get(row['key'])
        item = items.get(issue['id']) if issue else None
        if not item or item['isArchived']:
            raise ValueError('Missing or archived WBS Issue/item: '+row['key'])
        phase = row['phase'] if row['parent'] else None
        if row['parent'] and phase not in PHASES:
            raise ValueError('Invalid task phase: '+row['key'])
        actual = (issue.get('milestone') or {}).get('id')
        if actual and actual not in manifest_ids:
            raise ValueError('Non-manifest milestone on '+row['key'])
        expected_id = by_phase.get(phase, {}).get('id')
        observed.append(dict(key=row['key'], issueId=issue['id'], itemId=item['id'], phase=phase,
                             milestoneId=actual, expectedMilestoneId=expected_id))
        if actual != expected_id or phase and phase not in by_phase:
            memberships.append(dict(key=row['key'], id=issue['id'], phase=phase,
                                    actual=actual, expected=expected_id))
    report = dict(project=project['id'], repository=REPOSITORY, counts=counts,
                  fieldMissing=not fields, options=PHASES, milestonesMissing=missing, memberships=memberships,
                  observed=dict(fields=fields, milestones=list(by_phase.values()), memberships=observed),
                  **evidence_identity(manifest))
    return report, by_phase, issues, items


def setup(manifest, evidence, apply=False):
    report, milestones, _, _ = setup_plan(manifest)
    save(evidence / 'setup-plan.json', report)
    print('setup plan', 'field missing:', report['fieldMissing'], 'milestones:',
          len(report['milestonesMissing']), 'memberships:', len(report['memberships']), flush=True)
    if not apply:
        return
    if report['fieldMissing']:
        api('mutation($input:CreateProjectV2FieldInput!){createProjectV2Field(input:$input){projectV2Field{... on ProjectV2FieldCommon{id}}}}',
            dict(input=dict(projectId=report['project'], dataType='SINGLE_SELECT', name='工程',
                 singleSelectOptions=[dict(name=p, color='GRAY', description='') for p in PHASES])))
        time.sleep(1)
    for milestone in report['milestonesMissing']:
        # GitHub keeps only the date of due_on as seen west of UTC (observed 2026-10-12: T00:00:00Z
        # read back as the previous day). Noon UTC lands on the same calendar date everywhere it truncates.
        api('repos/'+REPOSITORY+'/milestones', dict(title=milestone['title'],
            due_on=milestone['due']+'T12:00:00Z'), method='POST')
        time.sleep(1)
    # Resolve new node IDs and recheck all conflicts from fresh server observations.
    report, milestones, _, _ = setup_plan(manifest)
    if report['fieldMissing'] or report['milestonesMissing']:
        raise ValueError('Created field/milestones did not read back; reconcile with a fresh run.')
    links = [dict(id=m['id'], milestoneId=milestones[m['phase']]['id'] if m['phase'] else None)
             for m in report['memberships']]
    for offset in range(0, len(links), 10):
        batch = links[offset:offset+10]
        declarations = ','.join(f'$v{i}:UpdateIssueInput!' for i in range(len(batch)))
        operations = ' '.join(f'a{i}:updateIssue(input:$v{i}){{issue{{id}}}}' for i in range(len(batch)))
        api(f'mutation({declarations}){{{operations}}}', {f'v{i}': v for i, v in enumerate(batch)})
        # Account for every alias, not just HTTP requests, against content limits.
        time.sleep(len(batch))
    try:
        result, _, _, _ = setup_plan(manifest)
        result['verified'] = not (result['fieldMissing'] or result['milestonesMissing'] or result['memberships'])
    except ValueError as error:
        result = dict(verified=False, error=str(error))
    save(evidence / 'setup-result.json', result)
    if not result['verified']:
        raise ValueError('Setup independent readback mismatch; see setup-result.json.')
    print('verified setup', len(manifest['rows']), flush=True)


def verify(manifest, evidence):
    mismatches, observed = [], []
    resources = None
    try:
        resources, _, issues, items = setup_plan(manifest)
        for name in ('fieldMissing', 'milestonesMissing', 'memberships'):
            if resources[name]:
                mismatches.append(dict(field=name, expected=False if name == 'fieldMissing' else [], actual=resources[name]))
        for row in manifest['rows']:
            issue = issues[row['key']]
            values = {}
            for value in items[issue['id']]['fieldValues']['nodes']:
                name = value.get('field', {}).get('name')
                if name:
                    if name in values:
                        raise ValueError('Duplicate item field '+name+' on '+row['key'])
                    values[name] = next((value[k] for k in ('number', 'date', 'name') if k in value), None)
            task = row['parent'] is not None
            expected = {'工程': row['phase'], 'Estimate': row['estimate'], 'Remaining': row['estimate'],
                        'Actual': 0 if task else None, 'Start date': row['start'],
                        'predecessors': sorted(issues[p]['id'] for p in row['predecessors']),
                        'assignees': [OWNER] if row['person'] == 'U1' else [], 'state': 'OPEN'}
            for connection_name in ('blockedBy', 'assignees'):
                connection = issue[connection_name]
                if connection['pageInfo']['hasNextPage'] or len(connection['nodes']) != connection['totalCount']:
                    raise ValueError('Incomplete '+connection_name+' for '+row['key'])
            values.update(predecessors=sorted(p['id'] for p in issue['blockedBy']['nodes']),
                          assignees=sorted(p['login'] for p in issue['assignees']['nodes']), state=issue['state'])
            observed.append(dict(key=row['key'], issueId=issue['id'], itemId=items[issue['id']]['id'],
                                 expected=expected, actual={field: values.get(field) for field in expected}))
            for field, wanted in expected.items():
                actual = values.get(field)
                if actual != wanted:
                    mismatches.append(dict(key=row['key'], field=field, expected=wanted, actual=actual))
    except (ValueError, KeyError) as error:
        mismatches.append(dict(field='inventory', error=str(error)))
    report = dict(repository=REPOSITORY, projectNumber=PROJECT_NUMBER, statusDate=manifest['projectStart'],
                  rows=len(manifest['rows']), checkedRows=len(observed), verified=not mismatches, mismatches=mismatches,
                  resources=resources, observed=observed, **evidence_identity(manifest))
    save(evidence / 'verification.json', report)
    if mismatches:
        raise ValueError(f'{len(mismatches)} baseline mismatches; see verification.json.')
    print('verified baseline', len(manifest['rows']), flush=True)


def register(manifest, evidence, apply, through):
    project, issues, items, counts = inventory(manifest)
    selected = [r for r in manifest['rows'] if int(r['key'][1:3]) <= through]
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
    previous = None
    moves = 0
    for index, issue_id in enumerate(desired):
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['register', 'inputs', 'setup', 'verify'])
    parser.add_argument('--manifest', type=Path, default=DEFAULT_PATH)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--through', type=int, choices=[39, 40])
    parser.add_argument('--offline', action='store_true', help='Export inputs with the 20 synthetic assignee logins.')
    args = parser.parse_args()
    if args.through is None:
        args.through = 39 if args.action == 'register' else 40
    if args.offline and args.action != 'inputs':
        parser.error('--offline is only valid for inputs')
    if args.action in ('setup', 'verify') and args.through != 40:
        parser.error('setup and verify require the whole manifest (--through 40)')
    if args.action == 'verify' and args.apply:
        parser.error('verify is read-only; --apply is not accepted')
    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    if args.action == 'inputs':
        if args.apply:
            parser.error('inputs is read-only; --apply is not accepted')
        export_inputs(manifest, args.evidence, args.through, args.offline)
    elif args.action == 'setup':
        setup(manifest, args.evidence, args.apply)
    elif args.action == 'verify':
        verify(manifest, args.evidence)
    else:
        register(manifest, args.evidence, args.apply, args.through)


if __name__ == '__main__':
    main()
