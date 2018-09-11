FROM microsoft/dotnet:2.1.401-sdk AS build  
WORKDIR /app
ARG build=0-dev

COPY ./*.sln ./NuGet.config ./
COPY ./build/*.props ./build/

# Copy the main source project files
COPY src/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p src/${file%.*}/ && mv $file src/${file%.*}/; done

# Copy the sample project files
COPY samples/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p samples/${file%.*}/ && mv $file samples/${file%.*}/; done

# Copy the test project files
COPY test/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p test/${file%.*}/ && mv $file test/${file%.*}/; done

RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet build --version-suffix $build -c Release

# testrunner

FROM build AS testrunner
WORKDIR /app/test/Foundatio.Tests
ENTRYPOINT [ "dotnet", "test", "--verbosity", "minimal", "--logger:trx;LogFileName=/app/artifacts/test-results.trx" ]

# publish

FROM build AS publish
WORKDIR /app/

ARG build=0-dev

RUN dotnet pack --version-suffix $build -c Release -o /app/artifacts
ENTRYPOINT ["dotnet", "nuget", "push", "/app/artifacts/*.nupkg"]
CMD [ "--source", "https://api.nuget.org/v3/index.json", "--api-key", "$NUGET_KEY" ]

# docker build --target testrunner -t foundatio:testrunner --build-arg build=123-dev .
# docker run --rm -it foundatio:testrunner

# docker build --target publish -t foundatio:publish --build-arg build=123-dev .
# export NUGET_KEY=MY_SECRET_KEY
# docker run --rm -it foundatio:publish
