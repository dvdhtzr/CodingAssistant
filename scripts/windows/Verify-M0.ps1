$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '../..'))
py -3 -m unittest discover -s tests -v
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$model = if ($args.Count -gt 0) { $args[0] } else { 'qwen3.5:9b' }
py -3 tools/m0/preflight.py --model $model
exit $LASTEXITCODE
