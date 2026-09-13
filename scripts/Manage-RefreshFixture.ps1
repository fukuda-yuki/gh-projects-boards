# Opt-in manual fixture utility. Never invoked by the application or ordinary startup.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Start','Change','Cleanup')][string]$Action,
    [Parameter(Mandatory)][string]$Manifest,
    [string]$GhPath = 'C:\Program Files\GitHub CLI\gh.exe'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Manifest)) { throw 'Use an absolute manifest path in an isolated manual-check directory.' }
$project = 'PVT_kwHOBGPKL84BjFYc'
$repository = 'R_kgDOUVKgAw'
function Api([string]$Endpoint, [string]$Method = 'GET', $Body = $null, [switch]$AllowMissingNode) {
    $start = [Diagnostics.ProcessStartInfo]::new($GhPath)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($name in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','GH_DEBUG','GH_REPO','GH_HOST')) { [void]$start.Environment.Remove($name) }
    foreach ($arg in @('api','--hostname','github.com',$Endpoint,'--method',$Method)) { $start.ArgumentList.Add($arg) }
    if ($null -ne $Body) { $start.RedirectStandardInput = $true; $start.ArgumentList.Add('--input'); $start.ArgumentList.Add('-') }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync(); $errorOutput = $process.StandardError.ReadToEndAsync()
    if ($null -ne $Body) { $process.StandardInput.Write(($Body | ConvertTo-Json -Depth 30 -Compress)); $process.StandardInput.Close() }
    $process.WaitForExit(); $exitCode = $process.ExitCode; $process.Dispose()
    $result = $output.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($AllowMissingNode -and $result.errors -and @($result.errors | Where-Object type -ne 'NOT_FOUND').Count -eq 0 -and -not $result.data.node) { return $result }
    if ($exitCode -ne 0) { throw "gh request failed ($exitCode). Inspect the saved fixture identity; do not blindly retry a mutation." }
    if ($result.errors) { throw 'GraphQL result has errors. Reconcile fixture state before retrying.' }
    return $result
}
function Graph([string]$Query, $Variables = @{}, [switch]$AllowMissingNode) { Api 'graphql' 'POST' @{ query = $Query; variables = $Variables } -AllowMissingNode:$AllowMissingNode }
function Save { $script:fixture | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Manifest -Encoding utf8 }
function Snapshot {
    $result = Graph 'query { repository(owner:"fukuda-yuki",name:"codex-sandbox") { id issue(number:1) { id title body state } } user(login:"fukuda-yuki") { projectV2(number:3) { id fields(first:100) { pageInfo { hasNextPage } nodes { ... on ProjectV2FieldCommon { id name dataType } ... on ProjectV2SingleSelectField { options { id name } } } } items(first:100) { pageInfo { hasNextPage } nodes { id isArchived content { ... on Issue { id title state } } } } } } }'
    $p = $result.data.user.projectV2
    if ($result.data.repository.id -ne $repository -or $p.id -ne $project -or $p.items.pageInfo.hasNextPage -or $p.fields.pageInfo.hasNextPage) { throw 'Exact bounded sandbox identities/traversal were not verified.' }
    return $result.data
}
$scope = Api 'repos/fukuda-yuki/codex-sandbox/issues/1'
if ($scope.number -ne 1) { throw 'Sandbox scope Issue could not be verified.' }
if ($Action -eq 'Start') {
    if (Test-Path -LiteralPath $Manifest) { throw 'Manifest already exists; inspect it rather than creating another fixture.' }
    $directory = Split-Path $Manifest -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $baseline = Snapshot
    $fixture = @{ repository = $repository; project = $project; marker = ('ghpb-manual-refresh-' + [guid]::NewGuid().ToString('N')); baseline = $baseline; stage = 'before-create'; issue = $null; item = $null; number = $null }
    Save
    $issue = Api 'repos/fukuda-yuki/codex-sandbox/issues' 'POST' @{ title = ($fixture.marker + ' A'); body = 'Disposable manual refresh check. Only this fixture may be changed/cleaned by its manifest.' }
    $fixture.issue = $issue.node_id; $fixture.number = $issue.number; $fixture.stage = 'issue-created'; Save
    if ($fixture.number -le 1) { throw 'Refusing a protected Issue.' }
    $item = Graph 'mutation($p:ID!,$i:ID!) { addProjectV2ItemById(input:{projectId:$p,contentId:$i}) { item { id } } }' @{ p = $project; i = $fixture.issue }
    $fixture.item = $item.data.addProjectV2ItemById.item.id; $fixture.stage = 'ready-A'; Save
    Write-Host "Issue: https://github.com/fukuda-yuki/codex-sandbox/issues/$($fixture.number)"
    Write-Host "Project: https://github.com/users/fukuda-yuki/projects/3 / $project"
    Write-Host "Issue ID: $($fixture.issue) / item ID: $($fixture.item)"
    Write-Host "Expected baseline title: $($fixture.marker) A"
    Write-Host "Enter this local title in the app: $($fixture.marker) B"
    return
}
$fixture = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -AsHashtable
if ($fixture.repository -ne $repository -or $fixture.project -ne $project -or $fixture.number -le 1 -or $fixture.marker -notmatch '^ghpb-manual-refresh-[0-9a-f]{32}$') { throw 'Invalid fixture scope.' }
$owned = Api "repos/fukuda-yuki/codex-sandbox/issues/$($fixture.number)"
if ($owned.node_id -ne $fixture.issue -or -not $owned.title.StartsWith($fixture.marker + ' ')) { throw 'This is not the uniquely marked owned fixture.' }
if ($Action -eq 'Change') {
    if ($fixture.stage -ne 'ready-A') { throw 'External change is already attempted or completed; inspect first.' }
    $fixture.stage = 'change-attempted'; Save
    $null = Api "repos/fukuda-yuki/codex-sandbox/issues/$($fixture.number)" 'PATCH' @{ title = ($fixture.marker + ' C') }
    $readback = Api "repos/fukuda-yuki/codex-sandbox/issues/$($fixture.number)"
    if ($readback.title -ne ($fixture.marker + ' C')) { throw 'External C readback differs.' }
    $fixture.stage = 'remote-C'; Save
    Write-Host 'Remote C verified. In the app select 最新を取得, compare A/B/C, and resolve locally. GitHub should remain C.'
    return
}
if ($fixture.item) {
    if (@($fixture.baseline.user.projectV2.items.nodes | Where-Object id -eq $fixture.item).Count -ne 0) { throw 'Refusing to remove a pre-existing item.' }
    $item = Graph 'query($id:ID!) { node(id:$id) { ... on ProjectV2Item { id project { id } content { ... on Issue { id } } } } }' @{ id = $fixture.item }
    if ($item.data.node.project.id -ne $project -or $item.data.node.content.id -ne $fixture.issue) { throw 'Owned item identity is unverified.' }
    $fixture.stage = 'remove-item-attempted'; Save
    $null = Graph 'mutation($p:ID!,$i:ID!) { deleteProjectV2Item(input:{projectId:$p,itemId:$i}) { deletedItemId } }' @{ p = $project; i = $fixture.item }
    $fixture.item = $null; $fixture.stage = 'item-removed'; Save
}
$fixture.stage = 'delete-issue-attempted'; Save
$null = Graph 'mutation($i:ID!) { deleteIssue(input:{issueId:$i}) { clientMutationId } }' @{ i = $fixture.issue }
$absent = Graph 'query($id:ID!) { node(id:$id) { id } }' @{ id = $fixture.issue } -AllowMissingNode
if ($absent.data.node) { throw 'Fixture Issue still exists.' }
$after = Snapshot
if (($after | ConvertTo-Json -Depth 30 -Compress) -ne ($fixture.baseline | ConvertTo-Json -Depth 30 -Compress)) { throw 'Existing sandbox data changed; inspect independently.' }
$fixture.stage = 'cleanup-verified'; Save
Write-Host 'Owned fixture cleanup and preservation of existing sandbox data verified.'
