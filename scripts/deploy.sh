#!/usr/bin/env bash
# =============================================================================
# MyTowerRegistration — Application Deployment
# =============================================================================
#
# Builds and deploys the application code. Run this on every code change:
# new API features, schema migrations, Blazor UI changes, config updates.
#
# PREREQUISITE: run ./scripts/setup-infra.sh once first to create the AWS
# infrastructure this script deploys into (ALB, RDS, S3, CloudFront, etc.).
#
# WHAT THIS SCRIPT DOES (in order):
#   1. Looks up resource ARNs from existing infrastructure (fail-fast if
#      setup-infra.sh has not been run)
#   2. Builds and pushes Docker images to ECR
#   3. Registers an ECS task definition for migrations
#   4. Runs database migrations as a one-off ECS task
#   5. Builds Blazor WASM, uploads to S3, invalidates CloudFront cache
#   6. Registers an ECS task definition for the API
#   7. Creates the ECS service (first run) or updates it (subsequent runs)
#   8. Waits for the service to reach steady state
#
# PLATFORM COMPATIBILITY
#   Tested on:
#     - WSL2 (Ubuntu) on Windows   ← recommended Windows approach
#     - macOS (bash 3.2+ via /usr/bin/env bash, or Homebrew bash 4+)
#     - Ubuntu / Debian Linux
#     - RHEL / Fedora Linux
#
#   Git Bash on Windows is NOT supported. Even with MSYS_NO_PATHCONV=1, Git
#   Bash mangles Unix-style path arguments in ways that break AWS CLI calls.
#   Use WSL2 instead: it is a real Linux kernel and has no path mangling.
#
# PREREQUISITES (must be installed before running)
#   All platforms : aws-cli v2, docker, jq, dotnet SDK 8
#   Ubuntu/Debian : sudo apt  install awscli docker.io jq
#   RHEL/Fedora   : sudo dnf  install awscli docker   jq
#   macOS         : brew install awscli docker jq
#                   brew install --cask dotnet-sdk
#   Configure AWS : aws configure
#
# =============================================================================

set -euo pipefail

# Prevent Git Bash on Windows from converting leading slashes in arguments
# to Windows paths (e.g. /ecs/foo → C:/Program Files/Git/ecs/foo).
# This is a no-op on WSL, Linux, and macOS.
export MSYS_NO_PATHCONV=1

# =============================================================================
# CONFIGURATION
# Must match the values in setup-infra.sh — these names identify the existing
# infrastructure this script deploys into.
# =============================================================================

AWS_REGION="us-east-2"
AWS_ACCOUNT_ID="151935250464"

export AWS_DEFAULT_REGION="${AWS_REGION}"

ECR_BASE="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
ECR_API_REPO="mytower-registration-api"
ECR_MIGRATIONS_REPO="mytower-registration-migrations"

ECS_CLUSTER="mytower-cluster"
ECS_SG_ID="sg-05354e42eaaf4662d"

SUBNETS_ARRAY=(
    "subnet-0521349f1295ef589"   # us-east-2a
    "subnet-0b71feaecab5f9d97"   # us-east-2b
    "subnet-0c2e0b3fdb97e61f4"   # us-east-2c
)

EXECUTION_ROLE_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:role/ecsTaskExecutionRole"

SECRET_NAME="mytower-registration/db-connection-string"
MIGRATIONS_TASK_FAMILY="mytower-registration-migrations"
API_TASK_FAMILY="mytower-registration-api"
API_SERVICE_NAME="mytower-registration-api"
ALB_NAME="mytower-registration-alb"
TG_NAME="mytower-registration-tg"
BLAZOR_BUCKET="mytower-registration-admin"
API_PORT=8080
LOG_GROUP="/ecs/mytower-registration"
HEALTH_CHECK_PATH="/api/graphql/"

# CORS and API URL — stable constants now that mytower.dev DNS is configured.
# See: scripts/setup-infra.sh and the domain setup runbook.
ADMIN_ORIGIN="https://admin.mytower.dev"
API_BASE_URL="https://admin-api.mytower.dev"

echo "=== MyTowerRegistration Deployment ==="
echo "Region: ${AWS_REGION} | Account: ${AWS_ACCOUNT_ID}"
echo ""

# =============================================================================
# PREFLIGHT: Look up ARNs from existing infrastructure
# =============================================================================
# These values are outputs of setup-infra.sh. We look them up by name rather
# than hardcoding them so this script stays correct if resources are recreated
# (new ARNs, same names). If any lookup fails, setup-infra.sh hasn't been run
# and we exit immediately with a clear error rather than a cryptic later failure.
echo "--- Preflight: looking up infrastructure ARNs ---"

SECRET_ARN=$(aws secretsmanager describe-secret \
    --secret-id "${SECRET_NAME}" \
    --query 'ARN' --output text 2>/dev/null) \
    || { echo "ERROR: Secret '${SECRET_NAME}' not found. Run setup-infra.sh first."; exit 1; }

TG_ARN=$(aws elbv2 describe-target-groups \
    --names "${TG_NAME}" \
    --query 'TargetGroups[0].TargetGroupArn' \
    --output text 2>/dev/null) \
    || { echo "ERROR: Target group '${TG_NAME}' not found. Run setup-infra.sh first."; exit 1; }

CF_ID=$(aws cloudfront list-distributions \
    --query "DistributionList.Items[?Comment=='MyTowerRegistration Admin'].Id" \
    --output text 2>/dev/null | awk '{print $1}')
[ -z "${CF_ID}" ] && { echo "ERROR: CloudFront distribution not found. Run setup-infra.sh first."; exit 1; }

echo "  Secret ARN : ${SECRET_ARN}"
echo "  Target group: ${TG_ARN}"
echo "  CloudFront  : ${CF_ID}"
echo "  Admin origin: ${ADMIN_ORIGIN}"
echo "OK"

# =============================================================================
# STEP 1: Authenticate Docker to ECR and push images
# =============================================================================
# Docker needs a temporary token to push to ECR. The token is generated by
# the AWS CLI and piped directly into `docker login`. It is valid for 12 hours.
#
# We build each image using --target to select a specific stage from the
# multi-stage Dockerfile:
#   runtime      — the .NET API (runs the server)
#   dbMigrations — the migration bundle (runs once and exits)
echo ""
echo "--- Step 1: Building and pushing Docker images ---"

aws ecr get-login-password --region "${AWS_REGION}" | \
    docker login --username AWS --password-stdin "${ECR_BASE}"

docker build --platform linux/amd64 --target runtime \
    -t "${ECR_BASE}/${ECR_API_REPO}:latest" \
    .
docker push "${ECR_BASE}/${ECR_API_REPO}:latest"

docker build --platform linux/amd64 --target dbMigrations \
    -t "${ECR_BASE}/${ECR_MIGRATIONS_REPO}:latest" \
    .
docker push "${ECR_BASE}/${ECR_MIGRATIONS_REPO}:latest"

echo "OK"

# =============================================================================
# STEP 2: Register the migrations ECS task definition
# =============================================================================
# A task definition is a blueprint describing how to run a container:
#   - Which image to use
#   - CPU + memory (256 CPU units = 0.25 vCPU, minimum for Fargate)
#   - Network mode (awsvpc = each task gets its own private IP)
#   - Secrets to inject as environment variables
#   - Logging configuration
#
# For migrations, we override the entrypoint to use `sh -c` so the shell
# can expand $CONNECTION_STRING and pass it to migrate-db. Without the
# shell, environment variables in the command string won't expand.
#
# The `secrets` block pulls from Secrets Manager at container startup:
#   "<SECRET_ARN>:<JSON_KEY>::"
#   The JSON key extracts a specific field from the stored JSON object.
#   The trailing "::" means "latest version, AWSCURRENT stage".
#
# Every deploy registers a new task definition revision. ECS keeps the
# history — old revisions are never deleted automatically.
echo ""
echo "--- Step 2: Registering migrations task definition ---"

aws ecs register-task-definition --cli-input-json "$(cat <<EOF
{
    "family": "${MIGRATIONS_TASK_FAMILY}",
    "executionRoleArn": "${EXECUTION_ROLE_ARN}",
    "networkMode": "awsvpc",
    "requiresCompatibilities": ["FARGATE"],
    "cpu": "256",
    "memory": "512",
    "containerDefinitions": [{
        "name": "migrations",
        "image": "${ECR_BASE}/${ECR_MIGRATIONS_REPO}:latest",
        "essential": true,
        "entryPoint": ["sh", "-c"],
        "command": ["./migrate-db --connection \"\$CONNECTION_STRING\""],
        "secrets": [{
            "name": "CONNECTION_STRING",
            "valueFrom": "${SECRET_ARN}:connectionString::"
        }],
        "logConfiguration": {
            "logDriver": "awslogs",
            "options": {
                "awslogs-group": "${LOG_GROUP}",
                "awslogs-region": "${AWS_REGION}",
                "awslogs-stream-prefix": "migrations"
            }
        }
    }]
}
EOF
)" > /dev/null

echo "OK"

# =============================================================================
# STEP 3: Run database migrations
# =============================================================================
# `run-task` launches a container that runs to completion — unlike a service,
# it does NOT restart. This is the ECS equivalent of `docker compose run`.
#
# assignPublicIp ENABLED is required in a public subnet without a NAT Gateway.
# It gives the task a temporary public IP to reach ECR, Secrets Manager, and
# CloudWatch. The IP is not accessible from the internet (the ECS security
# group only allows inbound from the ALB).
#
# We check the .failures array explicitly: run-task exits 0 even when it
# fails to place the task (image pull error, capacity issue, etc.).
echo ""
echo "--- Step 3: Running database migrations ---"

RUN_TASK_OUTPUT=$(aws ecs run-task \
    --cluster "${ECS_CLUSTER}" \
    --task-definition "${MIGRATIONS_TASK_FAMILY}" \
    --launch-type FARGATE \
    --network-configuration "awsvpcConfiguration={subnets=[${SUBNETS_ARRAY[0]}],securityGroups=[${ECS_SG_ID}],assignPublicIp=ENABLED}" \
    --output json)

MIGRATION_TASK_ARN=$(echo "${RUN_TASK_OUTPUT}" | jq -r '.tasks[0].taskArn // empty')
FAILURES_LEN=$(echo "${RUN_TASK_OUTPUT}" | jq '.failures | length')

if [ "${FAILURES_LEN}" -ne 0 ] || [ -z "${MIGRATION_TASK_ARN}" ]; then
    echo "ERROR: Failed to start migration task."
    echo "${RUN_TASK_OUTPUT}" | jq '.failures'
    exit 1
fi

echo "Migration task: ${MIGRATION_TASK_ARN}"
echo "Waiting for migrations to complete..."

aws ecs wait tasks-stopped \
    --cluster "${ECS_CLUSTER}" \
    --tasks "${MIGRATION_TASK_ARN}"

EXIT_CODE=$(aws ecs describe-tasks \
    --cluster "${ECS_CLUSTER}" \
    --tasks "${MIGRATION_TASK_ARN}" \
    --query 'tasks[0].containers[0].exitCode' \
    --output text)

if [ "${EXIT_CODE}" != "0" ]; then
    echo "ERROR: Migrations failed with exit code ${EXIT_CODE}"
    echo "Logs: https://console.aws.amazon.com/cloudwatch/home?region=${AWS_REGION}#logsV2:log-groups/log-group/${LOG_GROUP//\//%2F}"
    exit 1
fi

echo "Migrations completed successfully."

# =============================================================================
# STEP 4: Build Blazor WASM, upload to S3, invalidate CloudFront cache
# =============================================================================
# `dotnet publish` compiles Blazor in Release configuration. Output lands in
# ./publish/admin/wwwroot/ — that is the static site root.
#
# appsettings.json is downloaded by the browser at startup — it is NOT a
# secret. We patch the PUBLISHED copy (not the source tree) so the committed
# file keeps its placeholder value and the repo working tree is never dirtied
# by a deploy. The Development override file handles local dev.
#
# HTTPS is required: Blazor is served over HTTPS by CloudFront, so the API
# URL must also be HTTPS or browsers block it (mixed-content policy).
#
# After syncing to S3, we create a CloudFront invalidation to evict stale
# cached files. Without this, users may receive old JS/WASM bundles until
# the cache TTL expires naturally (~24h for CachingOptimized policy).
# The invalidation is eventually consistent — it typically takes ~30 seconds
# to propagate to all edge nodes.
echo ""
echo "--- Step 4: Building and deploying Blazor Admin ---"

# Clean the output directory first so every file gets a fresh mtime.
# Without this, s3 sync can skip files whose local mtime is older than the
# S3 copy — notably appsettings.json, which can change content without
# changing size (same-length URL), causing the patched value to be silently
# skipped on cross-machine deploys (e.g. Mac vs WSL).
rm -rf ./publish/admin

dotnet publish ./MyTowerRegistration.Admin/MyTowerRegistration.Admin.csproj \
    --configuration Release \
    --output ./publish/admin
echo "Blazor publish complete."

PUBLISHED_APPSETTINGS="./publish/admin/wwwroot/appsettings.json"

# Guard: fail loudly if the key is missing rather than silently creating a
# new key that Blazor will deserialize as null. jq -e exits non-zero if the
# result is null or false — same class of bug as the silent-null
# deserialization trap in C# (see CLAUDE.md).
jq -e '.ApiBaseUrl' "${PUBLISHED_APPSETTINGS}" > /dev/null \
    || { echo "ERROR: ApiBaseUrl key missing from ${PUBLISHED_APPSETTINGS} — check the placeholder value"; exit 1; }

jq --arg url "${API_BASE_URL}" '.ApiBaseUrl = $url' "${PUBLISHED_APPSETTINGS}" \
    > "${PUBLISHED_APPSETTINGS}.tmp" && mv "${PUBLISHED_APPSETTINGS}.tmp" "${PUBLISHED_APPSETTINGS}"
echo "Patched published ApiBaseUrl → ${API_BASE_URL}"

# --delete removes stale files from previous deploys (e.g. old versioned
# .wasm files that were renamed in the new build).
aws s3 sync ./publish/admin/wwwroot "s3://${BLAZOR_BUCKET}/" --delete
echo "Files uploaded to s3://${BLAZOR_BUCKET}/"

aws cloudfront create-invalidation \
    --distribution-id "${CF_ID}" \
    --paths "/*" > /dev/null
echo "CloudFront cache invalidated."

echo "OK"

# =============================================================================
# STEP 5: Register the API ECS task definition
# =============================================================================
# Similar to the migrations task definition but:
#   - Uses the runtime image (not migrations)
#   - Does NOT override the entrypoint (uses Dockerfile ENTRYPOINT as-is)
#   - Exposes port 8080
#   - Injects the connection string as ConnectionStrings__DefaultConnection
#     (double underscore = nested config key in ASP.NET Core)
#
# AllowedOrigins__0 injects the CORS origin as an environment variable.
# ASP.NET Core maps double-underscore to config nesting:
#   AllowedOrigins__0 → AllowedOrigins[0]
# This matches what Program.cs reads via GetSection("AllowedOrigins").
# Using an env var means CORS origins can be updated by redeploying the task
# definition — no Docker image rebuild needed.
echo ""
echo "--- Step 5: Registering API task definition ---"

aws ecs register-task-definition --cli-input-json "$(cat <<EOF
{
    "family": "${API_TASK_FAMILY}",
    "executionRoleArn": "${EXECUTION_ROLE_ARN}",
    "networkMode": "awsvpc",
    "requiresCompatibilities": ["FARGATE"],
    "cpu": "256",
    "memory": "512",
    "containerDefinitions": [{
        "name": "api",
        "image": "${ECR_BASE}/${ECR_API_REPO}:latest",
        "essential": true,
        "portMappings": [{
            "containerPort": ${API_PORT},
            "protocol": "tcp"
        }],
        "environment": [{
            "name": "AllowedOrigins__0",
            "value": "${ADMIN_ORIGIN}"
        }],
        "secrets": [{
            "name": "ConnectionStrings__DefaultConnection",
            "valueFrom": "${SECRET_ARN}:connectionString::"
        }],
        "logConfiguration": {
            "logDriver": "awslogs",
            "options": {
                "awslogs-group": "${LOG_GROUP}",
                "awslogs-region": "${AWS_REGION}",
                "awslogs-stream-prefix": "api"
            }
        }
    }]
}
EOF
)" > /dev/null

echo "OK"

# =============================================================================
# STEP 6: Create or update the ECS service
# =============================================================================
# A service keeps desiredCount tasks running at all times. If a task crashes,
# ECS automatically launches a replacement. This is the key difference from
# `run-task` (step 3) which ran once and exited.
#
# With a load balancer attached, the service registers each task's private IP
# with the target group at startup and deregisters it on shutdown. The ALB
# health check gates whether a task receives traffic.
#
# IMPORTANT: ECS does not allow changing the load balancer config of an
# existing service — it must be set at creation. If the service was previously
# created without a load balancer (e.g. from an old smoke-test run), delete it
# manually first:
#   aws ecs update-service --cluster mytower-cluster \
#     --service mytower-registration-api --desired-count 0
#   aws ecs delete-service --cluster mytower-cluster \
#     --service mytower-registration-api
#
# health-check-grace-period-seconds: gives the task time to start before
# the ALB begins health checks. Without this, the ALB may mark the task
# unhealthy before ASP.NET Core finishes startup, causing an instant
# replacement loop.
echo ""
echo "--- Step 6: Creating or updating ECS service ---"

EXISTING_SERVICE=$(aws ecs describe-services \
    --cluster "${ECS_CLUSTER}" \
    --services "${API_SERVICE_NAME}" \
    --query 'services[?status!=`INACTIVE`].serviceName' \
    --output text 2>/dev/null || echo "")

if [ -z "${EXISTING_SERVICE}" ]; then
    aws ecs create-service \
        --cluster "${ECS_CLUSTER}" \
        --service-name "${API_SERVICE_NAME}" \
        --task-definition "${API_TASK_FAMILY}" \
        --desired-count 1 \
        --launch-type FARGATE \
        --network-configuration "awsvpcConfiguration={subnets=[${SUBNETS_ARRAY[0]}],securityGroups=[${ECS_SG_ID}],assignPublicIp=ENABLED}" \
        --load-balancers "targetGroupArn=${TG_ARN},containerName=api,containerPort=${API_PORT}" \
        --health-check-grace-period-seconds 60 > /dev/null
    echo "ECS service created."
else
    aws ecs update-service \
        --cluster "${ECS_CLUSTER}" \
        --service "${API_SERVICE_NAME}" \
        --task-definition "${API_TASK_FAMILY}" \
        --desired-count 1 \
        --force-new-deployment > /dev/null
    echo "ECS service updated."
fi

echo "Waiting for service to reach steady state..."

aws ecs wait services-stable \
    --cluster "${ECS_CLUSTER}" \
    --services "${API_SERVICE_NAME}"

echo "OK"

# =============================================================================
# DONE
# =============================================================================
echo ""
echo "=== Deployment complete! ==="
echo ""
echo "  API (via ALB):   https://admin-api.mytower.dev/api/graphql"
echo "  Admin UI:        https://admin.mytower.dev"
echo "  CloudWatch logs: https://${AWS_REGION}.console.aws.amazon.com/cloudwatch/home?region=${AWS_REGION}#logsV2:log-groups/log-group/${LOG_GROUP//\//%2F}"
echo ""
echo "Notes:"
echo "  - ALB DNS is stable — it does not change when tasks restart."
echo "  - CloudFront cache invalidation propagates in ~30 seconds."
echo "  - CORS configured: API allows requests from ${ADMIN_ORIGIN}"
