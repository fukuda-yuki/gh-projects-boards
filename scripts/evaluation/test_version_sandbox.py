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
            self.assertEqual(lines[3].split('\t')[7], '2, 3')
            order = json.loads(Path(root, 'first-pass-order.json').read_text(encoding='utf-8'))
            self.assertEqual(order['issueIds'], [row['key'] for row in plan['rows']])


class WeeklyStageTests(unittest.TestCase):
    def test_progress_preserves_carryover_within_four_week_capacity(self):
        manifest = generate()
        estimates = {r['key']: r['estimate'] for r in manifest['rows'] if r['parent']}
        for week, capacity in [(1, 48), (2, 88), (3, 120), (4, 160)]:
            with self.subTest(week=week):
                _, progress = sandbox.weekly_progress(manifest, estimates, week)
                ordinary = {k: v for k, v in progress.items() if k.startswith('R02-')}
                self.assertEqual(sum(v['Actual'] for v in ordinary.values()), capacity)
                self.assertGreater(ordinary['R02-PS-001']['Remaining'], 0)
                for row in manifest['rows']:
                    if row['key'] in ordinary and ordinary[row['key']]['Actual'] > 0:
                        self.assertTrue(all(ordinary[p]['Remaining'] == 0 for p in row['predecessors']))

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

    def test_r40_arrival_keeps_prior_pmo_order(self):
        with tempfile.TemporaryDirectory() as root:
            manifest = dict(rows=[dict(key=k, title=k+' x', parent=None) for k in ['R01', 'R02', 'R40']])
            issues = {r['key']: dict(id=r['key'], number=i, title=r['title'], parent=None) for i, r in enumerate(manifest['rows'])}
            order = ['R40', 'R02', 'R01']
            def inventory(_):
                return dict(id='project'), issues, {k: dict(id='item-'+k) for k in order}, {}
            def api(query, variables=None):
                if 'repository(' in query:
                    return dict(repository=dict(id='repo'))
                value = variables['input']
                key, after = value['itemId'][5:], value.get('afterId', '')[5:]
                self.assertEqual(key, 'R40', 'Only the arriving requirement may be positioned.')
                order.remove(key)
                order.insert(order.index(after)+1 if after else 0, key)
                return {}
            with patch.object(sandbox, 'inventory', inventory), patch.object(sandbox, 'api', api), patch.object(sandbox.time, 'sleep'), contextlib.redirect_stdout(io.StringIO()):
                sandbox.register(manifest, Path(root), True, 40, only_requirement=40)
            self.assertEqual(order, ['R02', 'R01', 'R40'])

    def test_week_three_before_week_two_keeps_registered_remote_state(self):
        with tempfile.TemporaryDirectory() as root:
            remote = {"order": ["R02", "R01"], "r40": False}
            before = json.dumps(remote)
            def registration(*args, **kwargs):
                remote.update(order=["R01", "R02", "R40"], r40=True)
            with patch.object(sandbox, 'register', registration), patch('sys.argv', [
                    'version_sandbox.py', 'week', '--week', '3', '--apply', '--evidence', root]):
                with self.assertRaises(ValueError):
                    sandbox.main()
            self.assertEqual(json.dumps(remote), before)

    def test_completed_week_three_preserves_pmo_order_and_values(self):
        with tempfile.TemporaryDirectory() as root:
            Path(root, 'week-3.json').write_text(json.dumps(dict(complete=True)), encoding='utf-8')
            remote = {"order": ["R02", "R01", "R40"], "remaining": 17}
            before = json.dumps(remote)
            def registration(*args, **kwargs):
                remote['order'] = ['R01', 'R02', 'R40']
            with patch.object(sandbox, 'register', registration), patch('sys.argv', [
                    'version_sandbox.py', 'week', '--week', '3', '--apply', '--evidence', root]), contextlib.redirect_stdout(io.StringIO()):
                sandbox.main()
            self.assertEqual(json.dumps(remote), before)


if __name__ == '__main__':
    unittest.main()
