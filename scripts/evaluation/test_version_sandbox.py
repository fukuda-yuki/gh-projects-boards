"""Evaluation-specific safety contracts; fake only the GitHub operation boundary."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import version_sandbox as sandbox
from generate_version import generate


class InputExportTests(unittest.TestCase):
    def test_inputs_default_exports_the_whole_generated_wbs_offline(self):
        with tempfile.TemporaryDirectory() as root:
            manifest = Path(root, 'manifest.json')
            manifest.write_text(json.dumps(generate()), encoding='utf-8')
            output = Path(root, 'inputs')
            with patch('sys.argv', ['version_sandbox.py', 'inputs', '--manifest', str(manifest),
                                    '--evidence', str(output), '--offline']), contextlib.redirect_stdout(io.StringIO()):
                sandbox.main()
            self.assertEqual(len((output / 'first-pass.tsv').read_text(encoding='utf-8').splitlines()), 1040)

    def test_incomplete_dependency_range_refuses_before_writing(self):
        with tempfile.TemporaryDirectory() as root:
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
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as root:
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
        with tempfile.TemporaryDirectory() as root:
            items = [self.item('archived-other', archived=True), self.item('unavailable', content=False)]
            items += [self.item(row['key']) for row in plan['rows']]
            self.export_with_items(plan, items, root)
            lines = Path(root, 'first-pass.tsv').read_text(encoding='utf-8').splitlines()
            self.assertEqual(len(lines), 4)
            self.assertEqual(lines[3].split('\t')[7], '3')
            order = json.loads(Path(root, 'first-pass-order.json').read_text(encoding='utf-8'))
            self.assertEqual(order['issueIds'], [row['key'] for row in plan['rows']])


class RegistrationTests(unittest.TestCase):
    def test_known_conflicting_parent_preserves_remote_state(self):
        with tempfile.TemporaryDirectory() as root:
            manifest = dict(rows=[dict(key='R01', title='R01 x', parent=None), dict(key='R01-SA-001', title='R01 SA-001 y', parent='R01')])
            issues = {'R01': dict(id='parent', title='R01 x', parent=None),
                      'R01-SA-001': dict(id='child', title='R01 SA-001 y', parent=dict(id='different-parent'))}
            remote = dict(issues=issues.copy())
            before = json.dumps(remote)
            def write(*args, **kwargs):
                remote['changed'] = True
                return {}
            with patch.object(sandbox, 'inventory', return_value=(dict(id='project'), issues, {}, {})), patch.object(sandbox, 'api', write):
                with self.assertRaises(ValueError):
                    sandbox.register(manifest, Path(root), True, 39)
            self.assertEqual(json.dumps(remote), before)


if __name__ == '__main__':
    unittest.main()
