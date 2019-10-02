FROM mcr.microsoft.com/dotnet/core/sdk:3.0 AS build

# install dotnet sdk 2.2
ENV DOTNET_SDK_VERSION 2.2.402

RUN curl -SL --output dotnet.tar.gz https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$DOTNET_SDK_VERSION/dotnet-sdk-$DOTNET_SDK_VERSION-linux-x64.tar.gz \
    && dotnet_sha512='81937de0874ee837e3b42e36d1cf9e04bd9deff6ba60d0162ae7ca9336a78f733e624136d27f559728df3f681a72a669869bf91d02db47c5331398c0cfda9b44' \
    && echo "$dotnet_sha512 dotnet.tar.gz" | sha512sum -c - \
    && tar -zxf dotnet.tar.gz -C /usr/share/dotnet \
    && rm dotnet.tar.gz

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
