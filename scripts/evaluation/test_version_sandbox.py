"""Evaluation-specific safety contracts; fake only the GitHub operation boundary."""
import contextlib
import io
import json
from pathlib import Path
import shutil
import uuid
import unittest
from unittest.mock import patch
import version_sandbox as sandbox
from generate_version import generate


@contextlib.contextmanager
def temporary_directory():
    # Python 3.14's Windows mode-0700 temp ACL excludes the managed runner.
    root = Path(__file__).resolve().parents[2] / 'TestResults' / ('sandbox-unit-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    try:
        yield str(root)
    finally:
        shutil.rmtree(root)


class InputExportTests(unittest.TestCase):
    def test_inputs_default_exports_the_whole_generated_wbs_offline(self):
        with temporary_directory() as root:
            manifest = Path(root, 'manifest.json')
            manifest.write_text(json.dumps(generate()), encoding='utf-8')
            output = Path(root, 'inputs')
            with patch('sys.argv', ['version_sandbox.py', 'inputs', '--manifest', str(manifest),
                                    '--evidence', str(output), '--offline']), contextlib.redirect_stdout(io.StringIO()):
                sandbox.main()
            self.assertEqual(len((output / 'first-pass.tsv').read_text(encoding='utf-8').splitlines()), 1040)

    def test_incomplete_dependency_range_refuses_before_writing(self):
        with temporary_directory() as root:
            with self.assertRaisesRegex(ValueError, r'R40-SA-003.*--through 40'):
                sandbox.export_inputs(generate(), Path(root), 39, offline=True)
            self.assertEqual(list(Path(root).iterdir()), [])

    def export_with_items(self, manifest, items, root):
        issues = [dict(id=row['key'], title=row['title']) for row in manifest['rows']]
        def connection(nodes):
            return dict(nodes=nodes, totalCount=len(nodes), pageInfo=dict(hasNextPage=False, endCursor=None))
        def api(query, variables=None):
            if 'user(login:' in query:
                return dict(user=dict(projectV2=dict(id='project', number=3, title=manifest['title'],
                    owner=dict(login=sandbox.OWNER), fields=connection([]))))
            if 'repository(owner:' in query:
                return dict(repository=dict(issues=connection(issues)))
            return dict(node=dict(items=connection(items)))
        with patch.object(sandbox, 'api', api), contextlib.redirect_stdout(io.StringIO()):
            sandbox.export_inputs(manifest, Path(root), 39)

    @staticmethod
    def item(identity, repository=sandbox.REPOSITORY, archived=False, content=True):
        return dict(id='item-'+identity, isArchived=archived,
                    content=dict(id=identity, repository=dict(nameWithOwner=repository)) if content else None,
                    fieldValues=dict(nodes=[], pageInfo=dict(hasNextPage=False)))

    def test_extra_or_archived_sheet_issues_refuse_export_and_preserve_existing_inputs(self):
        plan = generate()
        plan['rows'] = plan['rows'][:4]
        for kind in ('non-WBS', 'other-repository', 'archived-WBS', 'later-requirement'):
            with self.subTest(kind=kind), temporary_directory() as root:
                items = [self.item(row['key']) for row in plan['rows']]
                if kind == 'archived-WBS':
                    items[1]['isArchived'] = True
                else:
                    items.insert(0, self.item('R40' if kind == 'later-requirement' else 'unrelated',
                                             'other/repository' if kind == 'other-repository' else sandbox.REPOSITORY))
                output = Path(root, 'first-pass.tsv')
                output.write_text('preserve existing inputs', encoding='utf-8')
                with self.assertRaises(ValueError):
                    self.export_with_items(plan, items, root)
                self.assertEqual(output.read_text(encoding='utf-8'), 'preserve existing inputs')
                self.assertFalse(Path(root, 'first-pass-order.json').exists())

    def test_exact_visible_issue_order_exports_matching_predecessors(self):
        plan = generate()
        plan['rows'] = plan['rows'][:4]
        with temporary_directory() as root:
            items = [self.item('archived-other', archived=True), self.item('unavailable', content=False)]
            items += [self.item(row['key']) for row in plan['rows']]
            self.export_with_items(plan, items, root)
            lines = Path(root, 'first-pass.tsv').read_text(encoding='utf-8').splitlines()
            self.assertEqual(len(lines), 4)
            self.assertEqual(lines[3].split('\t')[8], '3')
            order = json.loads(Path(root, 'first-pass-order.json').read_text(encoding='utf-8'))
            self.assertEqual(order['issueIds'], [row['key'] for row in plan['rows']])


class FakeSandbox:
    def __init__(self, whole=False):
        self.manifest = generate()
        self.manifest['rows'] = self.manifest['rows'] if whole else self.manifest['rows'][:12]
        keys = {r['key'] for r in self.manifest['rows']}
        for row in self.manifest['rows']:
            row['predecessors'] = [p for p in row['predecessors'] if p in keys]
        self.fields = [dict(id='phase', name='工程', dataType='SINGLE_SELECT',
                            options=[dict(name=p) for p in self.manifest['phases']])]
        self.milestones = [dict(id=m['phase'], title=m['title'], dueOn=m['due']+'T00:00:00Z')
                           for m in self.manifest['milestones']]
        self.issues = []
        self.items = []
        for row in self.manifest['rows']:
            issue = dict(id=row['key'], title=row['title'], state='OPEN', milestone=None,
                         assignees=self.connection([dict(id='user', login=sandbox.OWNER)] if row['person']=='U1' else []),
                         blockedBy=self.connection([dict(id=p) for p in row['predecessors']]))
            self.issues.append(issue)
            values = [('工程', 'name', row['phase']), ('Estimate', 'number', row['estimate']),
                      ('Remaining', 'number', row['estimate']), ('Actual', 'number', 0 if row['parent'] else None),
                      ('開始日指定', 'date', row['start'])]
            self.items.append(dict(id='item-'+row['key'], isArchived=False,
                content=dict(id=row['key'], repository=dict(nameWithOwner=sandbox.REPOSITORY)),
                fieldValues=self.connection([dict(field=dict(name=n), **{k:v}) for n,k,v in values if v is not None])))
        self.writes = []
        self.fail = False
        self.ignore_updates = False

    @staticmethod
    def connection(nodes):
        return dict(nodes=nodes, totalCount=len(nodes), pageInfo=dict(hasNextPage=False, endCursor=None))

    def api(self, query, variables=None, **kwargs):
        import time
        if query.startswith('mutation') or kwargs.get('method') == 'POST':
            self.writes.append((query, variables, time.monotonic()))
            if self.fail:
                raise RuntimeError('uncertain mutation')
            if 'createProjectV2Field' in query:
                self.fields.append(dict(id='phase', name='工程', dataType='SINGLE_SELECT',
                    options=variables['input']['singleSelectOptions']))
                return {}
            if kwargs.get('method') == 'POST':
                self.milestones.append(dict(id=variables['title'], title=variables['title'], dueOn=variables['due_on']))
                return {}
            if not self.ignore_updates:
                for value in variables.values():
                    issue = next(i for i in self.issues if i['id']==value['id'])
                    issue['milestone'] = next((m for m in self.milestones if m['id']==value['milestoneId']), None)
            return {}
        if 'user(login:' in query:
            return dict(user=dict(projectV2=dict(id='project', number=3, title=self.manifest['title'],
                owner=dict(login=sandbox.OWNER), fields=self.connection(self.fields))))
        def page(nodes):
            start = int((variables or {}).get('cursor') or 0)
            end = start + 100
            result = self.connection(nodes[start:end])
            result['totalCount'] = len(nodes)
            result['pageInfo'] = dict(hasNextPage=end < len(nodes), endCursor=str(end))
            return result
        if 'milestones(first:' in query:
            return dict(repository=dict(milestones=page(self.milestones)))
        if 'repository(owner:' in query:
            return dict(repository=dict(issues=page(self.issues)))
        return dict(node=dict(items=page(self.items)))

    def assign_baseline(self):
        for row, issue in zip(self.manifest['rows'], self.issues):
            issue['milestone'] = next((m for m in self.milestones if m['id']==row['phase']), None)


class SetupTests(unittest.TestCase):
    def test_dry_run_records_missing_resources_without_writes(self):
        fake = FakeSandbox(); fake.fields = []; fake.milestones = []
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.setup(fake.manifest, Path(root), False)
            report = json.loads(Path(root, 'setup-plan.json').read_text(encoding='utf-8'))
            self.assertTrue(report['fieldMissing'])
            self.assertEqual(len(report['milestonesMissing']), 9)
            self.assertEqual(len(report['memberships']), 11)
            self.assertEqual(fake.writes, [])

    def test_conflicts_refuse_before_any_write(self):
        for kind in ('type', 'options', 'due', 'duplicate', 'foreign', 'missing', 'archived'):
            with self.subTest(kind=kind):
                fake = FakeSandbox()
                if kind == 'type': fake.fields[0]['dataType'] = 'TEXT'
                if kind == 'options': fake.fields[0]['options'].reverse()
                if kind == 'due': fake.milestones[0]['dueOn'] = '2020-01-01T00:00:00Z'
                if kind == 'duplicate': fake.milestones.append(dict(fake.milestones[0], id='duplicate'))
                if kind == 'foreign': fake.issues[-1]['milestone'] = dict(id='foreign', title='foreign')
                if kind == 'missing': fake.issues.pop()
                if kind == 'archived': fake.items[-1]['isArchived'] = True
                with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
                    with self.assertRaises(ValueError): sandbox.setup(fake.manifest, Path(root), True)
                self.assertEqual(fake.writes, [])

    def test_alias_batches_are_paced_and_independently_read_back(self):
        fake = FakeSandbox()
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.setup(fake.manifest, Path(root), True)
            self.assertTrue(json.loads(Path(root, 'setup-result.json').read_text(encoding='utf-8'))['verified'])
            self.assertEqual([len(v) for _,v,_ in fake.writes], [10,1])
            self.assertGreaterEqual(fake.writes[1][2]-fake.writes[0][2], 10)
            before = len(fake.writes)
            sandbox.setup(fake.manifest, Path(root), True)
            self.assertEqual(len(fake.writes), before)

    def test_create_resources_and_clear_requirement_manifest_membership(self):
        fake = FakeSandbox(); fake.assign_baseline()
        fake.issues[0]['milestone'] = fake.milestones[0]
        fake.fields = []; fake.milestones.pop()
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.setup(fake.manifest, Path(root), True)
            self.assertIsNone(fake.issues[0]['milestone'])
            self.assertEqual(fake.writes[1][1]['due_on'], '2027-04-30T12:00:00Z')
            self.assertIn('repos/fukuda-yuki/codex-sandbox/milestones', fake.writes[1][0])

    def test_uncertain_write_stops_without_retry(self):
        fake = FakeSandbox(); fake.fail = True
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            with self.assertRaisesRegex(RuntimeError, 'uncertain'): sandbox.setup(fake.manifest, Path(root), True)
        self.assertEqual(len(fake.writes), 1)

    def test_readback_detects_unapplied_mutation(self):
        fake = FakeSandbox(); fake.assign_baseline(); fake.issues[1]['milestone'] = None; fake.ignore_updates = True
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            with self.assertRaises(ValueError): sandbox.setup(fake.manifest, Path(root), True)
            self.assertFalse(json.loads(Path(root, 'setup-result.json').read_text(encoding='utf-8'))['verified'])

    def test_verify_records_baseline_and_mismatches_without_writes(self):
        fake = FakeSandbox(); fake.assign_baseline()
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.verify(fake.manifest, Path(root))
            fake.items[1]['fieldValues']['nodes'][0]['name'] = 'OT'
            fake.items[1]['fieldValues']['nodes'][1]['number'] = 999
            fake.issues[2]['blockedBy'] = fake.connection([])
            with self.assertRaises(ValueError): sandbox.verify(fake.manifest, Path(root))
            report = json.loads(Path(root, 'verification.json').read_text(encoding='utf-8'))
            self.assertEqual({m['field'] for m in report['mismatches']}, {'工程', 'Estimate', 'predecessors'})
        self.assertEqual(fake.writes, [])

    def test_tsv_column_order_and_live_baseline(self):
        fake = FakeSandbox()
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.export_inputs(fake.manifest, Path(root), 40)
            lines = Path(root, 'first-pass.tsv').read_text(encoding='utf-8').splitlines()
            self.assertEqual(lines[0].split('\t'), [fake.manifest['rows'][0]['title']]+['']*9)
            self.assertEqual(lines[1].split('\t'), [fake.manifest['rows'][1]['title'], 'SA', sandbox.OWNER,
                '21', '21', '0', '', '', '', '2026-10-13'])
            self.assertEqual(lines[2].split('\t')[8], '2')


    def test_verify_checks_all_1040_rows_and_last_task(self):
        fake = FakeSandbox(whole=True); fake.assign_baseline()
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            sandbox.verify(fake.manifest, Path(root))
            fake.items[-1]['fieldValues']['nodes'][2]['number'] = 1
            fake.items[-1]['fieldValues']['nodes'][3]['number'] = 8
            fake.issues[-1]['assignees'] = fake.connection([dict(id='wrong', login='wrong')])
            fake.issues[-1]['state'] = 'CLOSED'
            fake.items[1]['fieldValues']['nodes'][-1]['date'] = '2020-01-01'
            with self.assertRaises(ValueError): sandbox.verify(fake.manifest, Path(root))
            report = json.loads(Path(root, 'verification.json').read_text(encoding='utf-8'))
            self.assertEqual(report['rows'], 1040)
            self.assertEqual(report['checkedRows'], 1040)
            self.assertEqual(len(report['observed']), 1040)
            self.assertEqual({m['field'] for m in report['mismatches']},
                             {'Remaining', 'Actual', 'assignees', 'state', '開始日指定'})
        self.assertEqual(fake.writes, [])

    def test_truncated_native_connection_refuses_verification(self):
        fake = FakeSandbox(); fake.assign_baseline()
        fake.issues[-1]['blockedBy']['pageInfo']['hasNextPage'] = True
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            with self.assertRaises(ValueError): sandbox.verify(fake.manifest, Path(root))
            self.assertIn('Incomplete blockedBy', Path(root, 'verification.json').read_text(encoding='utf-8'))
        self.assertEqual(fake.writes, [])

    def test_readonly_cli_rejects_apply_and_partial_setup_before_api(self):
        fake = FakeSandbox()
        for options in (['verify', '--apply'], ['inputs', '--apply'], ['setup', '--through', '39'], ['setup', '--offline']):
            with self.subTest(options=options), temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
                with patch('sys.argv', ['version_sandbox.py', *options, '--evidence', root]), contextlib.redirect_stderr(io.StringIO()):
                    with self.assertRaises(SystemExit): sandbox.main()
        self.assertEqual(fake.writes, [])


class ApiBoundaryTests(unittest.TestCase):
    def test_graphql_and_rest_use_explicit_host_and_remove_token_overrides(self):
        from types import SimpleNamespace
        names = ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN',
                 'GH_DEBUG', 'GH_HOST', 'GH_REPO')
        calls = []
        def run(command, **kwargs):
            calls.append((command, kwargs))
            return SimpleNamespace(returncode=0, stdout='{"data":{}}' if 'graphql' in command else '{}')
        with patch.dict(sandbox.os.environ, {n:'override' for n in names}), patch.object(sandbox.subprocess, 'run', run):
            sandbox.api('query{viewer{login}}')
            sandbox.api('repos/'+sandbox.REPOSITORY+'/milestones', dict(title='SA 完了', due_on='2026-10-30T00:00:00Z'), method='POST')
        for command, kwargs in calls:
            self.assertEqual(command[:4], [sandbox.GH, 'api', '--hostname', 'github.com'])
            self.assertTrue(all(n not in kwargs['env'] for n in names))
        self.assertEqual(json.loads(calls[1][1]['input'])['due_on'], '2026-10-30T00:00:00Z')

    def test_partial_graphql_errors_stop_without_retry(self):
        from types import SimpleNamespace
        calls = []
        def run(*args, **kwargs):
            calls.append(args)
            return SimpleNamespace(returncode=0, stdout='{"data":{"a0":{}},"errors":[{"message":"failed"}]}')
        with patch.object(sandbox.subprocess, 'run', run):
            with self.assertRaises(RuntimeError): sandbox.api('mutation{a0:updateIssue}')
        self.assertEqual(len(calls), 1)


class RegistrationTests(unittest.TestCase):
    def test_known_conflicting_parent_preserves_remote_state(self):
        fake = FakeSandbox()
        fake.manifest['rows'] = fake.manifest['rows'][:2]
        fake.issues[1]['parent'] = dict(id='different-parent')
        with temporary_directory() as root, patch.object(sandbox, 'api', fake.api):
            with self.assertRaises(ValueError):
                sandbox.register(fake.manifest, Path(root), True, 39)
        self.assertEqual(fake.writes, [])


if __name__ == '__main__':
    unittest.main()
