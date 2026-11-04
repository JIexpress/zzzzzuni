#!/usr/bin/env bash
# TASK-A-R9-16: rerun the URP variant counter / rule-hit report in -batchmode.
# Usage (Git Bash):  bash Tools/run_urp_variant_counter.sh
# Notes: this executes an Editor method, NOT a test run, so -quit is correct here.
#        Do NOT add -force-d3d12 on this machine (GUI Editor hangs).
#        Do NOT call powershell from Bash (blocked by policy).
#        Unity must be launched through Git Bash (licensing handshake).
set -u
unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY no_proxy NO_PROXY

UNITY="C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe"
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
PROJ="$ROOT/UnityProject"

"$UNITY" -batchmode -quit \
  -projectPath "$PROJ" \
  -logFile - \
  -executeMethod ZFrame.Editor.UrpVariantCounter.RunFromCommandLine
