FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends build-essential clang cmake ninja-build git curl unzip ca-certificates python3 python3-venv automake autoconf libtool pkg-config bison flex && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/build-env && /opt/build-env/bin/pip install --no-cache-dir conan==2.28.0 ninja==1.13.0
ENV PATH=/opt/build-env/bin:$PATH
WORKDIR /work
