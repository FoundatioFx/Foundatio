#!/usr/bin/env bash
set -euo pipefail

for package in $(find -name "*.nupkg"); do
  echo "${0##*/}": Pushing $package...
  dotnet nuget push $package --source https://f.feedz.io/foundatio/foundatio/nuget --api-key $1
done