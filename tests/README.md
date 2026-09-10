# Tests

No test project or test-framework dependency is included in the skeleton. Add the first test project when an implementing Issue introduces behavior that needs automated verification.

Derive cases from the relevant Issue's acceptance criteria. Keep local automated tests, ordinary Windows UI checks, and live GitHub tests distinct. Use only explicitly designated data for live mutation tests.

[Issue #12](https://github.com/fukuda-yuki/gh-projects-boards/issues/12) owns cross-feature acceptance and performance measurements. An empty test suite or successful build does not establish feature acceptance.
