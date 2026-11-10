# Embeds Build Buddy fix explanations with a local Ollama model and writes SQL for Tiger Data.
# Usage (from the build-buddy folder):
#   ollama pull nomic-embed-text
#   powershell -ExecutionPolicy Bypass -File db\embed-fix-notes.ps1
# Output: db\out\fix-notes-insert.sql and db\out\fix-notes-queries.sql

$ErrorActionPreference = 'Stop'
$model = 'nomic-embed-text'
$ollama = if ($env:OLLAMA_HOST) { $env:OLLAMA_HOST } else { 'http://localhost:11434' }
$inv = [Globalization.CultureInfo]::InvariantCulture

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$data = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'fix-notes-data.json') | ConvertFrom-Json
$outDir = Join-Path $root 'out'
New-Item -ItemType Directory -Force $outDir | Out-Null

function Get-Embedding([string]$text) {
    # nomic-embed-text expects a task prefix: search_document for stored text, search_query for questions
    $body = @{ model = $model; input = $text } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $resp = Invoke-RestMethod -Method Post -Uri "$ollama/api/embed" -ContentType 'application/json; charset=utf-8' -Body $bytes
    $vec = @($resp.embeddings[0])
    if ($vec.Count -ne 768) { throw "Expected 768 dimensions, got $($vec.Count)" }
    '[' + (($vec | ForEach-Object { ([double]$_).ToString('0.########', $inv) }) -join ',') + ']'
}

function Sql([string]$s) { "'" + $s.Replace("'", "''") + "'" }

# --- Inserts ---
$rows = @()
$i = 0
foreach ($n in $data.notes) {
    $i++
    Write-Host "Embedding note $i of $($data.notes.Count): $($n.model) $($n.error_code)"
    $doc = "search_document: $($n.error_code): $($n.compiler_message) Explanation: $($n.explanation)"
    $vec = Get-Embedding $doc
    # PowerShell 7 turns ISO date strings into DateTime; keep them as exact UTC text
    $at = if ($n.noted_at -is [datetime]) { $n.noted_at.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', $inv) } else { [string]$n.noted_at }
    $rows += "  ($(Sql $at),$(Sql $n.source), $(Sql $n.model), $(Sql $n.error_code), $(Sql $n.compiler_message), $(Sql $n.explanation), $(Sql $n.review), $(Sql $vec))"
}
$insert = "INSERT INTO fix_notes (noted_at, source, model, error_code, compiler_message, explanation, review, embedding) VALUES`n" + ($rows -join ",`n") + ";`n"
Set-Content -Encoding UTF8 -Path (Join-Path $outDir 'fix-notes-insert.sql') -Value $insert

# --- Queries ---
$queries = @()
foreach ($q in $data.queries) {
    Write-Host "Embedding query $($q.name)"
    $vec = Get-Embedding ("search_query: " + $q.text)
    $queries += @"
-- $($q.name): $($q.text) $($q.filter)
WITH q AS (SELECT $(Sql $vec)::vector(768) AS v)
SELECT to_char(noted_at, 'YYYY-MM-DD') AS day, model, error_code, review,
       round((embedding <=> q.v)::numeric, 3) AS distance,
       left(explanation, 70) AS explanation
FROM fix_notes, q
$($q.filter)
ORDER BY embedding <=> q.v
LIMIT 3;

"@
}
Set-Content -Encoding UTF8 -Path (Join-Path $outDir 'fix-notes-queries.sql') -Value ($queries -join "`n")

Write-Host "Done: $($data.notes.Count) notes and $($data.queries.Count) queries written to $outDir"
