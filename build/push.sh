#!/usr/bin/env bash
set -euo pipefail

for package in $(find -name "*.nupkg"); do
  echo "${0##*/}": Pushing $package...
  dotnet nuget push $package --source $1 --api-key $2
done