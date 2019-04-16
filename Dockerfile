FROM mcr.microsoft.com/dotnet/core/sdk:2.2.105 AS build
WORKDIR /app

ARG VERSION_SUFFIX=0-dev
ENV VERSION_SUFFIX=$VERSION_SUFFIX

COPY ./*.sln ./NuGet.config ./
COPY ./*/*.props ./
COPY ./LICENSE.txt ./LICENSE.txt

# Copy the main source project files
COPY src/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p src/${file%.*}/ && mv $file src/${file%.*}/; done

# Copy the sample project files
COPY samples/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p samples/${file%.*}/ && mv $file samples/${file%.*}/; done

# Copy the test project files
COPY tests/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p tests/${file%.*}/ && mv $file tests/${file%.*}/; done

RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet build --version-suffix $VERSION_SUFFIX -c Release

# testrunner

FROM build AS testrunner
WORKDIR /app/tests/Foundatio.Tests
ENTRYPOINT dotnet test --results-directory /app/artifacts --logger:trx

# pack

FROM build AS pack
WORKDIR /app/

ARG VERSION_SUFFIX=0-dev
ENV VERSION_SUFFIX=$VERSION_SUFFIX

ENTRYPOINT dotnet pack --version-suffix $VERSION_SUFFIX -c Release -o /app/artifacts

# publish

FROM pack AS publish
WORKDIR /app/

ENTRYPOINT [ "dotnet", "nuget", "push", "/app/artifacts/*.nupkg" ]

# docker build --target testrunner -t foundatio:testrunner --build-arg VERSION_SUFFIX=123-dev .
# docker run -it -v $(pwd)/artifacts:/app/artifacts foundatio:testrunner

# docker build --target publish -t foundatio:publish --build-arg VERSION_SUFFIX=123-dev .
# export NUGET_SOURCE=https://api.nuget.org/v3/index.json
# export NUGET_KEY=MY_SECRET_NUGET_KEY
# docker run -it foundatio:publish -k $NUGET_KEY -s ${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}
