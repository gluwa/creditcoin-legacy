FROM ubuntu:bionic as base

RUN apt-get update && \
    apt-get install -y \
    curl gcc libssl-dev libzmq3-dev pkg-config unzip && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

RUN VERSION=3.5.1 && \
    curl -OLsS https://github.com/google/protobuf/releases/download/v$VERSION/protoc-$VERSION-linux-x86_64.zip && \
    unzip protoc-$VERSION-linux-x86_64.zip -d protoc3 && \
    mv protoc3/bin/* /usr/local/bin/ && \
    mv protoc3/include/* /usr/local/include/ && \
    rm protoc-$VERSION-linux-x86_64.zip

FROM base as build

RUN curl https://sh.rustup.rs -sSf | sh -s -- -y

ENV PATH=$PATH:/root/.cargo/bin \
    CARGO_INCREMENTAL=0

WORKDIR /usr/src/
RUN USER=root cargo new --bin ccconsensus
WORKDIR /usr/src/ccconsensus

COPY Cargo.toml Cargo.lock ./
RUN cargo build --release && \
    rm src/*.rs

COPY src ./src
RUN cargo build --release

FROM base

COPY --from=build \
    /usr/src/ccconsensus/target/release/ccconsensus \
    /usr/local/bin/ccconsensus

CMD ["ccconsensus", "-v"]
