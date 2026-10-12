const { test, beforeEach, afterEach } = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const validateRelease = require('./release-policy.cjs');

let cwd, sha, context, github, runs, existing;
function git(...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
beforeEach(() => {
  cwd = mkdtempSync(join(tmpdir(), 'ghpb-release-'));
  git('init', '-b', 'main');
  git('config', 'user.email', 'test@example.invalid');
  git('config', 'user.name', 'Release test');
  git('commit', '--allow-empty', '-m', 'main');
  sha = git('rev-parse', 'HEAD');
  git('update-ref', 'refs/remotes/origin/main', sha);
  git('tag', '-a', 'v1.2.3', '-m', 'candidate');
  context = { eventName: 'push', ref: 'refs/tags/v1.2.3', sha,
    repo: { owner: 'example', repo: 'app' }, payload: {} };
  runs = [{ id: 10, head_sha: sha, head_branch: 'main', event: 'push', status: 'completed', conclusion: 'success' }];
  existing = false;
  github = { rest: {
    actions: { listWorkflowRuns: async args => {
      assert.equal(args.workflow_id, 'ci.yml');
      assert.equal(args.head_sha, sha);
      assert.equal(args.event, 'push');
      assert.equal(args.branch, 'main');
      return { data: { workflow_runs: runs } };
    } },
    repos: { getReleaseByTag: async () => {
      if (existing) return { data: { draft: true } };
      throw Object.assign(new Error('not found'), { status: 404 });
    } },
  } };
});
afterEach(() => rmSync(cwd, { recursive: true, force: true }));
const validate = () => validateRelease({ github, context, cwd });

test('an annotated stable tag on checked main selects the exact commit and version', async () => {
  assert.deepEqual(await validate(), { sha, tag: 'v1.2.3', version: '1.2.3', prerelease: false });
});
test('a lightweight RC tag is a prerelease', async () => {
  git('tag', 'v2.0.0-rc.1');
  context.ref = 'refs/tags/v2.0.0-rc.1';
  assert.equal((await validate()).prerelease, true);
});
test('a checked older main commit remains releasable', async () => {
  git('commit', '--allow-empty', '-m', 'newer main');
  git('update-ref', 'refs/remotes/origin/main', 'HEAD');
  assert.equal((await validate()).sha, sha);
});
for (const tag of ['v1.2', 'v01.2.3', 'v1.2.3-rc.0', 'v1.2.3-rc.01', 'v1.2.3-beta.1', 'v1.2.3\n', '../main']) {
  test(`rejects unsupported tag ${JSON.stringify(tag)}`, async () => {
    context.ref = `refs/tags/${tag}`;
    await assert.rejects(validate(), /version tag/);
  });
}
test('rejects tags outside main even with successful CI', async () => {
  git('checkout', '-b', 'feature');
  git('commit', '--allow-empty', '-m', 'not merged');
  git('tag', 'v2.0.0');
  context.ref = 'refs/tags/v2.0.0';
  context.sha = git('rev-parse', 'HEAD');
  await assert.rejects(validate(), /origin\/main/);
});
test('rejects a tag moved after the push event', async () => {
  context.sha = '0'.repeat(40);
  await assert.rejects(validate(), /moved/);
});
test('manual retry resolves an existing tag from main', async () => {
  context.eventName = 'workflow_dispatch';
  context.ref = 'refs/heads/main';
  context.payload = { inputs: { tag: 'v1.2.3' } };
  assert.equal((await validate()).sha, sha);
});
test('manual release from a feature branch is rejected', async () => {
  context.eventName = 'workflow_dispatch';
  context.ref = 'refs/heads/feature';
  await assert.rejects(validate(), /Run workflow from main/);
});
test('manual retry never creates a missing tag', async () => {
  context.eventName = 'workflow_dispatch';
  context.ref = 'refs/heads/main';
  context.payload = { inputs: { tag: 'v9.9.9' } };
  await assert.rejects(validate(), /existing tag/);
  assert.equal(git('tag', '--list', 'v9.9.9'), '');
});
for (const conclusion of ['failure', 'cancelled', 'skipped', null]) {
  test(`blocks CI conclusion ${conclusion}`, async () => {
    runs[0].conclusion = conclusion;
    await assert.rejects(validate(), /CI/);
  });
}
test('an in-progress rerun cannot reuse its earlier success', async () => {
  runs[0].status = 'in_progress';
  await assert.rejects(validate(), /CI/);
});
test('missing CI is not a pass', async () => {
  runs = [];
  await assert.rejects(validate(), /CI/);
});
test('a newer failed run overrides an earlier success', async () => {
  runs.push({ ...runs[0], id: 11, conclusion: 'failure' });
  await assert.rejects(validate(), /CI/);
});
test('PR or wrong-commit success is insufficient', async () => {
  runs = [{ ...runs[0], event: 'pull_request' }, { ...runs[0], head_sha: 'wrong' }];
  await assert.rejects(validate(), /CI/);
});
test('an existing draft is never overwritten', async () => {
  existing = true;
  await assert.rejects(validate(), /already exists/);
});
test('GitHub lookup errors fail closed', async () => {
  github.rest.repos.getReleaseByTag = async () => { throw Object.assign(new Error('denied'), { status: 403 }); };
  await assert.rejects(validate(), /denied/);
});
test('PR validation needs no API access and creates only candidate metadata', async () => {
  context.eventName = 'pull_request';
  context.payload = { number: 123 };
  assert.deepEqual(await validateRelease({ github: {}, context, cwd }),
    { sha, tag: '', version: '0.0.0-pr.123', prerelease: true });
});
