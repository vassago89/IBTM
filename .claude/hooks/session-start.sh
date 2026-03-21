#!/bin/bash
set -euo pipefail

# 웹 환경에서만 실행
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_VERSION="10.0"
DOTNET_INSTALL_DIR="$HOME/.dotnet"

# dotnet이 이미 설치되어 있으면 스킵
if command -v dotnet &>/dev/null && dotnet --version 2>/dev/null | grep -q "^${DOTNET_VERSION}"; then
  exit 0
fi

# PATH에 .dotnet이 있는데 버전만 안 맞을 수도 있으므로 디렉토리 체크
if [ -x "$DOTNET_INSTALL_DIR/dotnet" ] && "$DOTNET_INSTALL_DIR/dotnet" --version 2>/dev/null | grep -q "^${DOTNET_VERSION}"; then
  echo "export DOTNET_ROOT=$DOTNET_INSTALL_DIR" >> "$CLAUDE_ENV_FILE"
  echo "export PATH=$DOTNET_INSTALL_DIR:\$PATH" >> "$CLAUDE_ENV_FILE"
  exit 0
fi

# .NET SDK 설치
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --install-dir "$DOTNET_INSTALL_DIR"

# 환경변수 설정
echo "export DOTNET_ROOT=$DOTNET_INSTALL_DIR" >> "$CLAUDE_ENV_FILE"
echo "export PATH=$DOTNET_INSTALL_DIR:\$PATH" >> "$CLAUDE_ENV_FILE"

# NuGet 패키지 복원
export PATH="$DOTNET_INSTALL_DIR:$PATH"
export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
cd "$CLAUDE_PROJECT_DIR"
dotnet restore
