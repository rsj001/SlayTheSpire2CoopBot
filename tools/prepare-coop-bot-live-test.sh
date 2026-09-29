#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --game-root PATH --ritsu-root PATH [--output-directory PATH] [--deploy-to-game]" >&2
  exit 2
}

game_root=""
ritsu_root=""
output_directory=""
deploy="false"
while (($#)); do
  case "$1" in
    --game-root) game_root="${2:-}"; shift 2 ;;
    --ritsu-root) ritsu_root="${2:-}"; shift 2 ;;
    --output-directory) output_directory="${2:-}"; shift 2 ;;
    --deploy-to-game) deploy="true"; shift ;;
    *) usage ;;
  esac
done
[[ -n "$game_root" && -n "$ritsu_root" ]] || usage

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
game_root="$(realpath "$game_root")"
ritsu_root="$(realpath "$ritsu_root")"
if [[ -z "$output_directory" ]]; then
  output_directory="$repository_root/.local/coopbot-live-kit"
fi
mkdir -p "$output_directory"
output_directory="$(realpath "$output_directory")"

dotnet build "$repository_root/CombatSolver.csproj" -c Release \
  -p:Sts2Dir="$game_root" -p:RitsuWorkshopRoot="$ritsu_root" -p:CopyModOnBuild=false
dotnet build "$repository_root/coopbot/CoopBot.csproj" -c Release \
  -p:Sts2Dir="$game_root" -p:RitsuWorkshopRoot="$ritsu_root" -p:CopyModOnBuild=false

arguments=("$repository_root" "$game_root" "$ritsu_root" "$output_directory")
if [[ "$deploy" == "true" ]]; then arguments+=(--deploy); fi
dotnet run --project "$repository_root/tools/CoopBot.LiveKit/CoopBot.LiveKit.csproj" \
  -c Release -- "${arguments[@]}"
