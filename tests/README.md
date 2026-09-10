# Tests

No test project or test-framework dependency is included in the skeleton. Add the first test project when an implementing Issue introduces behavior that needs automated verification.

Derive cases from the relevant Issue's acceptance criteria. Keep local automated tests, ordinary Windows UI checks, and live GitHub tests distinct. Use only explicitly designated data for live mutation tests.

[Issue #12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12) owns cross-feature acceptance and performance measurements. An empty test suite or successful build does not establish feature acceptance.

## Test policy

### Workflow

Start with a short test list derived from the Issue's acceptance criteria.
Consider normal cases, boundaries, failures, and prohibited side effects.

Turn one item into a runnable test and confirm that it fails for the
intended reason. Unrelated build or environment failures are not evidence
that the test detects the missing behavior.

Make the smallest coherent production change, then refactor with the
relevant tests green. Extend the test list as new cases are discovered.
Run the affected suite before completion.

### Boundaries and assertions

A behavior test may exercise several collaborating classes.
Do not require one isolated test fixture per production class.

Use real application logic by default. Substitute GitHub access,
gh execution, time, or other difficult boundaries when the scenario
requires control or isolation.

When testing persistence, exercise the actual persistence implementation
against isolated test storage. A fake store does not prove durability
or recovery.

Assert observable results and contractual side effects.
Do not mock the behavior under test or compute expected values by
reusing the production logic being verified.

Validate real adapters separately. Passing against a test double does
not establish live GitHub or GHEC + EMU compatibility.

### Test levels

- Logic tests verify rules using real domain and application objects.
- Integration tests exercise connected components, including UI
  orchestration where relevant, without mocking every lower layer.
- UI E2E tests exercise the real Windows application through user actions.
  Controlled external boundaries are allowed; identify them in results.

Choose the lowest-cost level that can detect the relevant failure.
Do not duplicate every case at every level.
Live GitHub mutation tests remain separate and explicitly authorized.

### Exceptions

Documentation-only changes do not require new behavior tests.
Behavior-preserving refactoring normally uses existing tests.

Exploratory spikes and environments that cannot execute the required
tests must be reported explicitly. Record alternative checks and
unverified scope; do not claim an unobserved Red or Green result.