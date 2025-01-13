#!/bin/bash

# Load environment variables
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/.env" ]; then
	source "${SCRIPT_DIR}/.env"
else
	echo "Error: ${SCRIPT_DIR}/.env file not found"
	exit 1
fi

# Set SERVICE_USER and SERVICE_HOST to DEPLOY values if not specified
SERVICE_USER=${SERVICE_USER:-$DEPLOY_USER}
SERVICE_HOST=${SERVICE_HOST:-$DEPLOY_HOST}

if [ "$1" != "--restart-only" ]; then
	# Build and copy the DLL
	dotnet build ASFTimedPlay || exit 1
	scp ASFTimedPlay/bin/Debug/net9.0/ASFTimedPlay.dll "${DEPLOY_USER}@${DEPLOY_HOST}:${DEPLOY_PATH}"
fi

# If --restart or --restart-only parameter is provided, restart the service
if [ "$1" = "--restart" ] || [ "$1" = "--restart-only" ]; then
	if [ "${DOCKER_SWARM}" = "true" ]; then
		ssh "${SERVICE_USER}@${SERVICE_HOST}" "docker service update --force ${SERVICE_NAME}"
	else
		ssh "${SERVICE_USER}@${SERVICE_HOST}" "docker restart ${SERVICE_NAME}"
	fi
fi
