#!/usr/bin/env sh
# Regenerates docs/wizard-transcripts/ — the wizard's dialogue as golden files.
# Run after ANY change to the wizard (questions, probes, messages), then review
# and commit the diff. WizardTranscriptTests fails CI while these are stale.
#
# PowerShell equivalent:
#   $env:RG_UPDATE_TRANSCRIPTS='1'; dotnet test tests/RestoreGuard.Tests -c Release --filter WizardTranscript
set -eu
cd "$(dirname "$0")/.."

RG_UPDATE_TRANSCRIPTS=1 dotnet test tests/RestoreGuard.Tests -c Release \
  --filter "FullyQualifiedName~WizardTranscript"

echo
echo "Transcript changes to review:"
git --no-pager diff --stat -- docs/wizard-transcripts || true
