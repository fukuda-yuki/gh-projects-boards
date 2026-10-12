const { execFileSync } = require('node:child_process');

// Local Git is real; only the GitHub API is substituted by the policy tests.
module.exports = async function validateRelease({ github, context, cwd = process.cwd() }) {
  const git = (...args) => execFileSync('git', args, {
    cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  if (context.eventName === 'pull_request') {
    return { sha: git('rev-parse', 'HEAD'), tag: '',
      version: `0.0.0-pr.${context.payload.number}`, prerelease: true };
  }
  let tag;
  if (context.eventName === 'workflow_dispatch') {
    if (context.ref !== 'refs/heads/main') throw new Error('Run workflow from main.');
    tag = context.payload.inputs?.tag;
  } else if (context.eventName === 'push' && context.ref.startsWith('refs/tags/')) {
    tag = context.ref.slice('refs/tags/'.length);
  }
  if (typeof tag !== 'string' || !/^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-rc\.[1-9]\d*)?$/.test(tag) || /\s/.test(tag)) {
    throw new Error('Use a version tag vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N.');
  }
  let sha;
  try { sha = git('rev-parse', '--verify', `refs/tags/${tag}^{commit}`); }
  catch { throw new Error(`An existing tag is required: ${tag}`); }
  if (context.eventName === 'push' && sha !== context.sha) {
    throw new Error('The tag moved since the triggering push. Create a new version.');
  }
  try { git('merge-base', '--is-ancestor', sha, 'refs/remotes/origin/main'); }
  catch { throw new Error('The release commit must be reachable from origin/main.'); }

  const { data } = await github.rest.actions.listWorkflowRuns({
    ...context.repo, workflow_id: 'ci.yml', event: 'push', branch: 'main', head_sha: sha, per_page: 100,
  });
  const latest = data.workflow_runs
    .filter(run => run.head_sha === sha && run.event === 'push' && run.head_branch === 'main')
    .sort((a, b) => b.id - a.id)[0];
  if (!latest || latest.status !== 'completed' || latest.conclusion !== 'success') {
    throw new Error(`Main CI must succeed for ${sha}; wait or repair CI, then retry this tag.`);
  }
  try {
    await github.rest.repos.getReleaseByTag({ ...context.repo, tag });
  } catch (error) {
    if (error.status !== 404) throw error;
    return { sha, tag, version: tag.slice(1), prerelease: tag.includes('-rc.') };
  }
  throw new Error(`Release ${tag} already exists; its assets will not be overwritten.`);
};
