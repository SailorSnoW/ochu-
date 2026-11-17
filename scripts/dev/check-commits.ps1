<#
.SYNOPSIS
    Checks the commit messages of a range against the rules of CONTRIBUTING.md ("Commits"):
    a conventional subject of 72 characters at most, and no trailer or line that credits a
    language model or a coding assistant.

.DESCRIPTION
    -Range is a git revision range; by default the commits that origin/main does not have.
    -Title checks one more subject: the title of a pull request, which becomes the subject
    of the squashed commit.
    Merge commits are not read.

.EXAMPLE
    .\scripts\dev\check-commits.ps1
.EXAMPLE
    .\scripts\dev\check-commits.ps1 -Range 'v0.1.0..HEAD'
#>
[CmdletBinding()]
param(
    [string]$Range = 'origin/main..HEAD',
    [string]$Title
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = 0

$MaxSubject = 72
$Types = 'feat', 'fix', 'names', 'perf', 'refactor', 'style', 'docs', 'ci', 'build', 'chore', 'test', 'deps'
$subjectForm = '^(' + ($Types -join '|') + ')(\([a-z0-9][a-z0-9/._-]*\))?!?: \S'
# A credit to a tool that wrote the commit: the author of a commit is the person who answers for it.
$assistant = 'claude|anthropic|openai|chatgpt|\bgpt\b|copilot|gemini|codex|cursor|devin|aider|\bllm\b|\bai\b'
$credit = '(?i)^\s*((co-authored-by|generated-by|assisted-by)\s*:.*(' + $assistant + ')|.*generated with .*(' + $assistant + '))'

function Fail([string]$where, [string]$message) {
    $script:Failures++
    Write-Host ("FAIL  {0}: {1}" -f $where, $message)
}

function Test-Subject([string]$where, [string]$subject) {
    if ($subject -cnotmatch $subjectForm) {
        Fail $where ("'{0}' is not 'type(scope): text' with a type among {1}" -f $subject, ($Types -join ', '))
    }
    if ($subject.Length -gt $MaxSubject) {
        Fail $where ("subject of {0} characters, {1} at most" -f $subject.Length, $MaxSubject)
    }
}

$ids = @(& git -C $Root log --no-merges --format=%H $Range)
if ($LASTEXITCODE -ne 0) { throw ("git log {0} failed" -f $Range) }

foreach ($id in $ids) {
    $lines = @(& git -C $Root log -1 --format=%B $id)
    $where = $id.Substring(0, 7)
    Test-Subject $where ([string]$lines[0])
    foreach ($line in $lines) {
        if ($line -match $credit) { Fail $where ("'{0}': no credit to a language model or an assistant" -f $line.Trim()) }
    }
}
if ($Title) { Test-Subject 'pull request title' $Title }

if ($script:Failures -gt 0) {
    Write-Host ''
    Write-Host ("{0} problem(s). The rules are in CONTRIBUTING.md (""Commits"")." -f $script:Failures)
    exit 1
}
Write-Host ("ok    {0} commit(s) of {1} follow the rules" -f $ids.Count, $Range)
