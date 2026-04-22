FROM maximhq/bifrost:latest

USER root

# Install common CLI tools needed by STDIO MCP servers.
# Supports Debian/Ubuntu and Alpine base images.
RUN if command -v apt-get >/dev/null 2>&1; then \
      apt-get update && \
      apt-get install -y --no-install-recommends \
        nodejs \
        npm \
        python3 \
        python3-pip \
        git \
        bash \
        curl \
        wget \
        jq \
        unzip \
        ca-certificates && \
      rm -rf /var/lib/apt/lists/*; \
    elif command -v apk >/dev/null 2>&1; then \
      apk add --no-cache \
        nodejs \
        npm \
        python3 \
        py3-pip \
        uv \
        git \
        bash \
        curl \
        wget \
        jq \
        unzip \
        ca-certificates; \
    else \
      echo "Unsupported base image package manager"; \
      exit 1; \
    fi

# Convenience symlink for tools that call `python`.
RUN ln -sf /usr/bin/python3 /usr/bin/python || true

# Install uv for Python MCP tooling on apt-based images.
# Alpine installs uv via apk above to avoid PEP 668 externally-managed errors.
RUN if command -v apt-get >/dev/null 2>&1; then \
      python3 -m pip install --no-cache-dir --upgrade pip uv; \
    fi
