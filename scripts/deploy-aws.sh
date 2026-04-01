#!/usr/bin/env bash
# =============================================================================
# MyTowerRegistration — AWS ECS Deployment Script
# =============================================================================
#
# Deploys the .NET API to AWS ECS (Fargate) with an RDS Postgres database.
# Reuses existing infrastructure from the Python MyTower project where possible.
#
# PREREQUISITES:
#   - AWS CLI installed and configured (aws configure)
#   - Docker installed and running
#   - jq installed (used for safe JSON construction)
#   - Run from the repo root: ./scripts/deploy-aws.sh
#
# WHAT THIS SCRIPT DOES (in order):
#   1.  Grants the ECS execution role permission to read Secrets Manager
#   2.  Creates ECR repositories for API + migrations images
#   3.  Builds and pushes Docker images to ECR
#   4.  Prompts for a DB password and stores it in Secrets Manager
#   5.  Creates a security group for RDS (locked to ECS traffic only)
#   6.  Opens port 8080 on the existing ECS security group (tightened in step 12)
#   7.  Creates an RDS subnet group and the RDS Postgres instance
#   8.  Updates the secret with the full connection string once RDS is up
#   9.  Creates a CloudWatch log group for container logs
#   10. Registers an ECS task definition for migrations
#   11. Runs migrations as a one-off ECS task and waits for it to finish
#   12. Creates an Application Load Balancer (ALB + target group + listener)
#       and tightens the ECS security group to only accept traffic from the ALB
#   13. Builds Blazor WASM, uploads to S3, creates a CloudFront distribution
#   14. Registers an ECS task definition for the API (with CORS origin set)
#   15. Creates an ECS service to keep the API running behind the ALB
#   16. Prints the stable ALB URL and CloudFront admin URL
#
# =============================================================================

# Exit immediately if any command fails, treat unset variables as errors,
# and propagate pipe failures (e.g. `cmd1 | cmd2` fails if cmd1 fails).
set -euo pipefail

# =============================================================================
# CONFIGURATION
# All resource names and IDs are here. Change these if you need to redeploy
# with different names, or move to a different account/region.
# =============================================================================

AWS_REGION="us-east-2"
AWS_ACCOUNT_ID="151935250464"

# Set this so all AWS CLI calls default to the right region without needing --region on every command.
# Commands that are truly global (e.g. IAM) ignore it; regional ones (ECR, ECS, RDS, etc.) use it.
export AWS_DEFAULT_REGION="${AWS_REGION}"

# Prevent Git Bash on Windows from converting leading slashes in arguments to Windows paths.
# Without this, paths like /ecs/mytower-registration become C:/Program Files/Git/ecs/...
export MSYS_NO_PATHCONV=1

# ECR base URL: <account>.dkr.ecr.<region>.amazonaws.com
# All images are pushed to and pulled from this registry.
ECR_BASE="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

# ECR repository names (created by this script)
ECR_API_REPO="mytower-registration-api"
ECR_MIGRATIONS_REPO="mytower-registration-migrations"

# ECS cluster — already exists from the Python project
ECS_CLUSTER="mytower-cluster"

# Security group assigned to ECS tasks — already exists, allows inbound 8000
# We'll add port 8080 for the .NET API
ECS_SG_ID="sg-05354e42eaaf4662d"

# Subnets in the default VPC — one per availability zone.
# All 3 are listed here for completeness; we use one for the single-task demo.
SUBNETS_ARRAY=(
    "subnet-0521349f1295ef589"   # us-east-2a
    "subnet-0b71feaecab5f9d97"   # us-east-2b
    "subnet-0c2e0b3fdb97e61f4"   # us-east-2c
)

# IAM role ECS uses to:
#   - Pull images from ECR
#   - Write logs to CloudWatch
#   - Read secrets from Secrets Manager (we grant this below)
EXECUTION_ROLE_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:role/ecsTaskExecutionRole"

# Resource names for things we create
RDS_INSTANCE_ID="mytower-registration-db"
RDS_SUBNET_GROUP="mytower-registration-subnet-group"
RDS_SG_NAME="mytower-registration-rds-sg"
SECRET_NAME="mytower-registration/db-connection-string"
MIGRATIONS_TASK_FAMILY="mytower-registration-migrations"
API_TASK_FAMILY="mytower-registration-api"
API_SERVICE_NAME="mytower-registration-api"

# Load balancer resource names
ALB_NAME="mytower-registration-alb"
ALB_SG_NAME="mytower-registration-alb-sg"
TG_NAME="mytower-registration-tg"

# S3 bucket for Blazor WASM static files (must be globally unique across all of AWS)
BLAZOR_BUCKET="mytower-registration-admin"

# Database settings
DB_NAME="mytower_registration"
DB_USERNAME="postgres"

# Port the .NET API listens on — must match Dockerfile EXPOSE and the default
# Kestrel binding (http://+:8080) set by the aspnet base image.
API_PORT=8080

# Health check path for the ALB target group. Stored as a variable rather than
# a literal string to prevent Git Bash on Windows from converting the leading
# slash to a Windows path (e.g. C:/Program Files/Git/api/graphql).
# MSYS_NO_PATHCONV=1 should prevent this, but variable expansion is more reliable.
HEALTH_CHECK_PATH="/api/graphql"

# CloudWatch log group — all container logs go here, prefixed by "api/" or "migrations/"
LOG_GROUP="/ecs/mytower-registration"

echo "=== MyTowerRegistration ECS Deployment ==="
echo "Region: ${AWS_REGION} | Account: ${AWS_ACCOUNT_ID}"
echo ""

# =============================================================================
# STEP 1: Grant ecsTaskExecutionRole access to Secrets Manager
# =============================================================================
# The standard ecsTaskExecutionRole policy only covers ECR and CloudWatch.
# To inject secrets into containers at startup, ECS needs permission to call
# secretsmanager:GetSecretValue. We attach a scoped inline policy here.
#
# Analogy: ECS is the delivery driver. Secrets Manager is the locked mailroom.
# This policy gives ECS the key — but only for our specific secrets, not all of them.
# The `Resource` ARN pattern uses a wildcard to cover the secret's random suffix.
echo "--- Step 1: Granting Secrets Manager read access to ecsTaskExecutionRole ---"

# Derive the role name from the ARN rather than hardcoding it, so this stays
# correct if the role is renamed or the ARN variable is changed above.
EXECUTION_ROLE_NAME="${EXECUTION_ROLE_ARN##*/}"

aws iam put-role-policy \
    --role-name "${EXECUTION_ROLE_NAME}" \
    --policy-name SecretsManagerReadForMyTowerRegistration \
    --policy-document "{
        \"Version\": \"2012-10-17\",
        \"Statement\": [{
            \"Effect\": \"Allow\",
            \"Action\": [\"secretsmanager:GetSecretValue\"],
            \"Resource\": \"arn:aws:secretsmanager:${AWS_REGION}:${AWS_ACCOUNT_ID}:secret:${SECRET_NAME}*\"
        }]
    }"

echo "OK"

# =============================================================================
# STEP 2: Create ECR repositories
# =============================================================================
# ECR (Elastic Container Registry) is AWS's private image registry — like
# Docker Hub, but private and integrated with IAM.
#
# We need two repositories:
#   mytower-registration-api        — the .NET runtime image (runs the API)
#   mytower-registration-migrations — the migration bundle image (runs once and exits)
#
# The `|| true` at the end of each command ignores errors if the repo
# already exists, making this step safe to re-run.
echo ""
echo "--- Step 2: Creating ECR repositories ---"

aws ecr create-repository \
    --repository-name "${ECR_API_REPO}" \
    --region "${AWS_REGION}" > /dev/null 2>&1 || true

aws ecr create-repository \
    --repository-name "${ECR_MIGRATIONS_REPO}" \
    --region "${AWS_REGION}" > /dev/null 2>&1 || true

echo "OK"

# =============================================================================
# STEP 3: Authenticate Docker to ECR and push images
# =============================================================================
# Docker needs a temporary token to push to ECR. The token is generated by
# the AWS CLI and piped directly into `docker login`. It's valid for 12 hours.
#
# We build each image using `--target` to select a specific stage from the
# multi-stage Dockerfile — `runtime` for the API, `dbMigrations` for migrations.
# Docker only builds up to and including that stage.
echo ""
echo "--- Step 3: Authenticating Docker to ECR ---"

aws ecr get-login-password --region "${AWS_REGION}" | \
    docker login --username AWS --password-stdin "${ECR_BASE}"

echo "--- Building and pushing API image (runtime stage) ---"
docker build --platform linux/amd64 --target runtime \
    -t "${ECR_BASE}/${ECR_API_REPO}:latest" \
    .
docker push "${ECR_BASE}/${ECR_API_REPO}:latest"

echo "--- Building and pushing migrations image (dbMigrations stage) ---"
docker build --platform linux/amd64 --target dbMigrations \
    -t "${ECR_BASE}/${ECR_MIGRATIONS_REPO}:latest" \
    .
docker push "${ECR_BASE}/${ECR_MIGRATIONS_REPO}:latest"

echo "OK"

# =============================================================================
# STEP 4: Prompt for DB password and store in Secrets Manager
# =============================================================================
# Secrets Manager stores sensitive values encrypted at rest. ECS injects them
# into containers as environment variables at startup — the value never appears
# in task definition config, CloudWatch logs, or the AWS console in plaintext.
#
# We prompt here so the password is never written to disk, shell history,
# or committed to source control.
#
# The secret is stored as JSON: {"connectionString": "Host=...;Password=..."}
# The JSON key lets us add more fields later (e.g. a read-replica URL) without
# changing the secret name.
echo ""
echo "--- Step 4: Storing DB password in Secrets Manager ---"

# If the secret already exists with a real connection string (not "placeholder"),
# skip the password prompt — the connection string is already correct from a
# previous successful run. Set SKIP_SECRET_UPDATE=true so Step 8 also skips.
SKIP_SECRET_UPDATE=false

EXISTING_CS=$(aws secretsmanager get-secret-value \
    --secret-id "${SECRET_NAME}" \
    --query 'SecretString' --output text 2>/dev/null \
    | jq -r '.connectionString // empty' 2>/dev/null || echo "")

if [ -n "${EXISTING_CS}" ] && [ "${EXISTING_CS}" != "placeholder" ]; then
    SECRET_ARN=$(aws secretsmanager describe-secret \
        --secret-id "${SECRET_NAME}" \
        --query 'ARN' --output text)
    SKIP_SECRET_UPDATE=true
    echo "Connection string already set in Secrets Manager, skipping password prompt."
    echo "Secret ARN: ${SECRET_ARN}"
else
    echo -n "Enter the RDS Postgres password: "
    read -r DB_PASSWORD
    echo ""

    # Store a placeholder now — Step 8 fills in the real connection string once
    # we know the RDS endpoint. The secret ARN is captured for use in task defs.
    if SECRET_ARN=$(aws secretsmanager describe-secret \
        --secret-id "${SECRET_NAME}" \
        --query 'ARN' --output text 2>/dev/null); then
        echo "Secret already exists, reusing: ${SECRET_ARN}"
        aws secretsmanager put-secret-value \
            --secret-id "${SECRET_NAME}" \
            --secret-string "{\"connectionString\":\"placeholder\"}" > /dev/null
    else
        SECRET_ARN=$(aws secretsmanager create-secret \
            --name "${SECRET_NAME}" \
            --description "DB connection string for MyTowerRegistration API" \
            --secret-string "{\"connectionString\":\"placeholder\"}" \
            --query 'ARN' --output text)
    fi
    echo "Secret ARN: ${SECRET_ARN}"
fi

# =============================================================================
# STEP 5: Create a security group for RDS
# =============================================================================
# Security groups are stateful firewalls attached to AWS resources. We create
# a dedicated one for RDS that allows port 5432 (Postgres) only from the ECS
# security group — not from the public internet.
#
# Using `--source-group` instead of `--cidr` is more robust: it matches any
# resource tagged with the ECS security group ID, regardless of its IP address.
# This means even if ECS task IPs change, the rule stays correct.
echo ""
echo "--- Step 5: Creating RDS security group ---"

# Determine which VPC the ECS security group belongs to, so we create the
# RDS security group in the same VPC rather than implicitly using the default.
VPC_ID=$(aws ec2 describe-security-groups \
    --group-ids "${ECS_SG_ID}" \
    --query 'SecurityGroups[0].VpcId' \
    --output text)

# Reuse existing RDS security group if it already exists (idempotent re-run).
EXISTING_RDS_SG=$(aws ec2 describe-security-groups \
    --filters "Name=group-name,Values=${RDS_SG_NAME}" "Name=vpc-id,Values=${VPC_ID}" \
    --query 'SecurityGroups[0].GroupId' \
    --output text 2>/dev/null || echo "None")

if [ "${EXISTING_RDS_SG}" = "None" ] || [ -z "${EXISTING_RDS_SG}" ]; then
    RDS_SG_ID=$(aws ec2 create-security-group \
        --group-name "${RDS_SG_NAME}" \
        --description "Allow Postgres from MyTowerRegistration ECS tasks only" \
        --vpc-id "${VPC_ID}" \
        --query 'GroupId' --output text)

    aws ec2 authorize-security-group-ingress \
        --group-id "${RDS_SG_ID}" \
        --protocol tcp \
        --port 5432 \
        --source-group "${ECS_SG_ID}"

    echo "RDS security group created: ${RDS_SG_ID}"
else
    RDS_SG_ID="${EXISTING_RDS_SG}"
    echo "Reusing existing RDS security group: ${RDS_SG_ID}"
fi

# =============================================================================
# STEP 6: Open port 8080 on the existing ECS security group
# =============================================================================
# The existing mytower-sg was created for the Python app (port 8000).
# Our .NET API runs on port 8080, so we add that ingress rule.
# The `|| true` ignores the error if this rule already exists.
echo ""
echo "--- Step 6: Opening port ${API_PORT} on ECS security group ---"

aws ec2 authorize-security-group-ingress \
    --group-id "${ECS_SG_ID}" \
    --protocol tcp \
    --port "${API_PORT}" \
    --cidr "0.0.0.0/0" > /dev/null 2>&1 || true

echo "OK"

# =============================================================================
# STEP 7: Create RDS subnet group and RDS instance
# =============================================================================
# An RDS subnet group tells RDS which subnets it can use. Including all 3 AZs
# is required by AWS even for a single-AZ instance — it allows a future
# Multi-AZ upgrade (automatic failover to a standby) without recreation.
#
# Free tier settings:
#   db.t3.micro = 2 vCPU burst, 1GB RAM, free for 750 hrs/month
#   allocated-storage 20 = 20GB gp2, free tier limit is 20GB
#   --no-publicly-accessible = RDS is only reachable from within the VPC
#   --no-multi-az = single availability zone (multi-AZ costs extra)
#
# After issuing the create command, RDS takes 5-10 minutes to initialize.
# `aws rds wait db-instance-available` polls every 30 seconds until it's ready.
echo ""
echo "--- Step 7: Creating RDS subnet group ---"

# Use array expansion to avoid word-splitting on subnet IDs with special chars.
# Idempotent: skip creation if the subnet group already exists.
if ! aws rds describe-db-subnet-groups \
    --db-subnet-group-name "${RDS_SUBNET_GROUP}" > /dev/null 2>&1; then
    aws rds create-db-subnet-group \
        --db-subnet-group-name "${RDS_SUBNET_GROUP}" \
        --db-subnet-group-description "Subnets for MyTowerRegistration RDS" \
        --subnet-ids "${SUBNETS_ARRAY[@]}"
else
    echo "RDS subnet group '${RDS_SUBNET_GROUP}' already exists, skipping."
fi

echo "--- Creating RDS Postgres instance (this takes 5-10 minutes) ---"

# Idempotent: skip creation if the instance already exists.
if ! aws rds describe-db-instances \
    --db-instance-identifier "${RDS_INSTANCE_ID}" > /dev/null 2>&1; then
    aws rds create-db-instance \
        --db-instance-identifier "${RDS_INSTANCE_ID}" \
        --db-instance-class db.t3.micro \
        --engine postgres \
        --engine-version 16 \
        --master-username "${DB_USERNAME}" \
        --master-user-password "${DB_PASSWORD}" \
        --db-name "${DB_NAME}" \
        --vpc-security-group-ids "${RDS_SG_ID}" \
        --db-subnet-group-name "${RDS_SUBNET_GROUP}" \
        --no-publicly-accessible \
        --allocated-storage 20 \
        --storage-type gp2 \
        --no-multi-az \
        --backup-retention-period 1 > /dev/null
else
    echo "RDS instance '${RDS_INSTANCE_ID}' already exists, skipping creation."
fi

echo "Waiting for RDS to become available..."
aws rds wait db-instance-available \
    --db-instance-identifier "${RDS_INSTANCE_ID}"

# Capture the endpoint hostname now that RDS is up
RDS_ENDPOINT=$(aws rds describe-db-instances \
    --db-instance-identifier "${RDS_INSTANCE_ID}" \
    --query 'DBInstances[0].Endpoint.Address' \
    --output text)

echo "RDS ready: ${RDS_ENDPOINT}"

# =============================================================================
# STEP 8: Update the secret with the full connection string
# =============================================================================
# Now that we know the RDS endpoint, we can build the full Npgsql connection
# string. We update the Secrets Manager secret we created in step 4.
#
# ASP.NET Core reads nested config keys via double underscore:
#   ConnectionStrings__DefaultConnection → ConnectionStrings.DefaultConnection
# This matches how appsettings.json is structured.
echo ""
echo "--- Step 8: Updating secret with full connection string ---"

if [ "${SKIP_SECRET_UPDATE}" = "true" ]; then
    echo "Skipping — connection string already set in Secrets Manager."
else
    # Trust Server Certificate=true skips RDS CA chain validation. Traffic is still
    # encrypted in transit, but the server certificate isn't verified against a
    # trusted CA — meaning a MITM inside the VPC could theoretically intercept it.
    # This is acceptable for a first deployment; the proper fix is to ship the AWS
    # RDS CA bundle in the container image and switch to SSL Mode=VerifyFull.
    # See: https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.SSL.html
    CONNECTION_STRING="Host=${RDS_ENDPOINT};Port=5432;Database=${DB_NAME};Username=${DB_USERNAME};Password=${DB_PASSWORD};SSL Mode=Require;Trust Server Certificate=true"

    # Use jq to build the JSON — direct string interpolation would break if the
    # password contains quotes, backslashes, or other special characters.
    SECRET_JSON=$(jq -n --arg cs "${CONNECTION_STRING}" '{connectionString:$cs}')

    aws secretsmanager update-secret \
        --secret-id "${SECRET_NAME}" \
        --secret-string "${SECRET_JSON}"

    # Clear sensitive values from memory
    unset DB_PASSWORD
    unset CONNECTION_STRING
    unset SECRET_JSON
fi

# Clear all sensitive values from memory — CONNECTION_STRING and SECRET_JSON
# both contain the plaintext password, so unset them along with DB_PASSWORD.
unset DB_PASSWORD
unset CONNECTION_STRING
unset SECRET_JSON

echo "OK"

# =============================================================================
# STEP 9: Create CloudWatch log group
# =============================================================================
# Container logs (stdout/stderr) are sent to CloudWatch via the awslogs driver.
# The standard ecsTaskExecutionRole can create log streams but not log groups,
# so we create the group explicitly here.
#
# Logs are organized by prefix: /ecs/mytower-registration/api/... and
# /ecs/mytower-registration/migrations/...
echo ""
echo "--- Step 9: Creating CloudWatch log group ---"

aws logs create-log-group \
    --log-group-name "${LOG_GROUP}" > /dev/null 2>&1 || true

echo "OK"

# =============================================================================
# STEP 10: Register the migrations ECS task definition
# =============================================================================
# A task definition is a blueprint describing how to run a container:
#   - Which image to use
#   - CPU + memory allocation (256 CPU units = 0.25 vCPU, minimum for Fargate)
#   - Network mode (awsvpc = each task gets its own private IP)
#   - Secrets to inject as environment variables
#   - Logging configuration
#
# For migrations, we override the entrypoint to use `sh -c` so the shell can
# expand the $CONNECTION_STRING environment variable and pass it to migrate-db.
# Without the shell, environment variables in the command string won't expand.
#
# The `secrets` block pulls the value from Secrets Manager at container startup.
# Format: "<SECRET_ARN>:<JSON_KEY>::"
#   - The JSON key extracts a specific field from the stored JSON object
#   - The trailing "::" means "latest version, AWSCURRENT stage"
echo ""
echo "--- Step 10: Registering migrations task definition ---"

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
# STEP 11: Run migrations as a one-off ECS task
# =============================================================================
# `run-task` launches a container that runs to completion — unlike a service,
# it does NOT restart. This is the ECS equivalent of `docker compose run`.
#
# assignPublicIp ENABLED is required when running in a public subnet without
# a NAT Gateway. It gives the task a public IP so it can reach:
#   - ECR (to pull the image)
#   - Secrets Manager (to read the connection string)
#   - CloudWatch (to send logs)
#
# We capture the task ARN, wait for it to stop, then check the exit code.
# Any non-zero exit code means migrations failed — we stop the script here
# rather than deploying an API against an incomplete schema.
echo ""
echo "--- Step 11: Running migrations ---"

# Capture the full output so we can check the failures array.
# run-task exits 0 even when it fails to place the task (e.g. image pull error,
# capacity issue) — the failure details are in .failures, not the exit code.
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
    echo "Check logs at: https://console.aws.amazon.com/cloudwatch/home?region=${AWS_REGION}#logsV2:log-groups/log-group/${LOG_GROUP//\//%2F}"
    exit 1
fi

echo "Migrations completed successfully (exit code 0)"

# =============================================================================
# STEP 12: Create Application Load Balancer
# =============================================================================
# An ALB gives the API a stable DNS hostname that doesn't change when tasks
# restart — unlike the raw public IP from the smoke-test phase.
#
# Three parts:
#   1. ALB itself          — receives traffic on port 80
#   2. Target group        — pool of ECS task IPs that get the traffic
#   3. Listener            — rule: "port 80 → forward to target group"
#
# Security model: we give the ALB its own security group that accepts port 80
# from the internet, then REMOVE the 0.0.0.0/0 rule on the ECS security group
# and replace it with "from ALB SG only". The ALB becomes the sole public entry
# point — ECS tasks are unreachable directly.
echo ""
echo "--- Step 12: Creating Application Load Balancer ---"

# VPC_ID was set in step 5, but recapture here so this step is re-runnable
# in isolation without step 5 having been run in the same shell session.
VPC_ID=$(aws ec2 describe-security-groups \
    --group-ids "${ECS_SG_ID}" \
    --query 'SecurityGroups[0].VpcId' \
    --output text)

# --- ALB security group: accept HTTP from internet -------------------------
EXISTING_ALB_SG=$(aws ec2 describe-security-groups \
    --filters "Name=group-name,Values=${ALB_SG_NAME}" "Name=vpc-id,Values=${VPC_ID}" \
    --query 'SecurityGroups[0].GroupId' \
    --output text 2>/dev/null || echo "None")

if [ "${EXISTING_ALB_SG}" = "None" ] || [ -z "${EXISTING_ALB_SG}" ]; then
    ALB_SG_ID=$(aws ec2 create-security-group \
        --group-name "${ALB_SG_NAME}" \
        --description "Allow HTTP from internet to MyTowerRegistration ALB" \
        --vpc-id "${VPC_ID}" \
        --query 'GroupId' --output text)

    aws ec2 authorize-security-group-ingress \
        --group-id "${ALB_SG_ID}" \
        --protocol tcp --port 80 --cidr "0.0.0.0/0"

    echo "ALB security group created: ${ALB_SG_ID}"
else
    ALB_SG_ID="${EXISTING_ALB_SG}"
    echo "Reusing existing ALB security group: ${ALB_SG_ID}"
fi

# --- Tighten ECS security group --------------------------------------------
# Step 6 opened port 8080 from 0.0.0.0/0 (fine for direct-access smoke testing).
# Now that the ALB is the sole entry point, revoke that open rule and replace it
# with one that only allows traffic originating from the ALB security group.
aws ec2 revoke-security-group-ingress \
    --group-id "${ECS_SG_ID}" \
    --protocol tcp --port "${API_PORT}" \
    --cidr "0.0.0.0/0" > /dev/null 2>&1 || true

aws ec2 authorize-security-group-ingress \
    --group-id "${ECS_SG_ID}" \
    --protocol tcp \
    --port "${API_PORT}" \
    --source-group "${ALB_SG_ID}" > /dev/null 2>&1 || true

echo "ECS security group updated: port ${API_PORT} now only reachable from ALB SG."

# --- Application Load Balancer ---------------------------------------------
EXISTING_ALB_ARN=$(aws elbv2 describe-load-balancers \
    --names "${ALB_NAME}" \
    --query 'LoadBalancers[0].LoadBalancerArn' \
    --output text 2>/dev/null || echo "None")

if [ "${EXISTING_ALB_ARN}" = "None" ] || [ -z "${EXISTING_ALB_ARN}" ]; then
    ALB_ARN=$(aws elbv2 create-load-balancer \
        --name "${ALB_NAME}" \
        --subnets "${SUBNETS_ARRAY[@]}" \
        --security-groups "${ALB_SG_ID}" \
        --scheme internet-facing \
        --type application \
        --ip-address-type ipv4 \
        --query 'LoadBalancers[0].LoadBalancerArn' --output text)
    echo "ALB created: ${ALB_ARN}"
else
    ALB_ARN="${EXISTING_ALB_ARN}"
    echo "Reusing existing ALB: ${ALB_ARN}"
fi

ALB_DNS=$(aws elbv2 describe-load-balancers \
    --load-balancer-arns "${ALB_ARN}" \
    --query 'LoadBalancers[0].DNSName' --output text)
echo "ALB DNS: ${ALB_DNS}"

# --- Target group ----------------------------------------------------------
# We use target-type=ip because Fargate tasks run in awsvpc mode — each task
# gets its own private IP. The ALB registers task IPs directly, unlike EC2
# where you'd register instance IDs.
#
# Health check: the ALB polls /api/graphql on each task to decide if it's
# healthy enough to receive traffic. Hot Chocolate returns 200 for GET requests
# (it serves the Banana Cake Pop UI), making it a reliable probe.
#
#   interval-seconds 30       — poll every 30s
#   healthy-threshold 2       — 2 consecutive 200s → task enters service (~60s)
#   unhealthy-threshold 3     — 3 consecutive failures → task pulled from rotation (~90s)
#   matcher "200"             — only HTTP 200 counts as healthy (GraphQL always returns 200)
EXISTING_TG_ARN=$(aws elbv2 describe-target-groups \
    --names "${TG_NAME}" \
    --query 'TargetGroups[0].TargetGroupArn' \
    --output text 2>/dev/null || echo "None")

if [ "${EXISTING_TG_ARN}" = "None" ] || [ -z "${EXISTING_TG_ARN}" ]; then
    # Use --cli-input-json + heredoc to avoid Git Bash path conversion.
    # Git Bash converts arguments that look like Unix paths (e.g. /api/graphql →
    # C:/Program Files/Git/api/graphql) even when the value comes from a variable.
    # JSON strings inside a heredoc are passed as a single string token and are
    # immune to this conversion — the same reason we use heredocs for task defs.
    TG_ARN=$(aws elbv2 create-target-group \
        --cli-input-json "$(cat <<EOF
{
    "Name": "${TG_NAME}",
    "Protocol": "HTTP",
    "Port": ${API_PORT},
    "VpcId": "${VPC_ID}",
    "TargetType": "ip",
    "HealthCheckProtocol": "HTTP",
    "HealthCheckPath": "${HEALTH_CHECK_PATH}",
    "HealthCheckIntervalSeconds": 30,
    "HealthyThresholdCount": 2,
    "UnhealthyThresholdCount": 3,
    "Matcher": { "HttpCode": "200" }
}
EOF
        )" \
        --query 'TargetGroups[0].TargetGroupArn' --output text)
    echo "Target group created: ${TG_ARN}"
else
    TG_ARN="${EXISTING_TG_ARN}"
    echo "Reusing existing target group: ${TG_ARN}"
fi

# --- Listener --------------------------------------------------------------
# The listener defines the routing rule attached to the ALB.
# One rule: all traffic arriving on port 80 is forwarded to our target group.
EXISTING_LISTENER=$(aws elbv2 describe-listeners \
    --load-balancer-arn "${ALB_ARN}" \
    --query 'Listeners[?Port==`80`].ListenerArn' \
    --output text 2>/dev/null || echo "")

if [ -z "${EXISTING_LISTENER}" ]; then
    aws elbv2 create-listener \
        --load-balancer-arn "${ALB_ARN}" \
        --protocol HTTP \
        --port 80 \
        --default-actions "Type=forward,TargetGroupArn=${TG_ARN}" > /dev/null
    echo "Listener created on port 80."
else
    echo "Listener on port 80 already exists, skipping."
fi

echo "OK"

# =============================================================================
# STEP 13: Build Blazor WASM and deploy to S3 + CloudFront
# =============================================================================
# Blazor WASM compiles to a bundle of static files: HTML, CSS, JS glue code,
# and .wasm (the compiled .NET runtime + app DLLs). There is no server —
# the browser downloads everything once, then runs the app locally in WASM.
#
# This means we can host it on S3 (pure object storage) and serve it via
# CloudFront (AWS's CDN). No Fargate task needed for the frontend.
#
# SPA routing problem: if a user bookmarks /admin/users, their browser requests
# that path from S3. S3 returns 404 — no file at that path exists. We configure
# a CloudFront custom error response: 404 → /index.html (HTTP 200). Blazor's
# client-side Router then picks up the URL and navigates to the right page.
# Without this, every deep link produces a blank error page.
#
# The Blazor app needs the API URL at publish time — it's baked into
# wwwroot/appsettings.json, which the browser downloads as a static file.
# We patch that file with the real ALB DNS before running dotnet publish.
#
# The API needs the CloudFront domain for CORS (AllowedOrigins). We get that
# domain immediately after create-distribution — no need to wait for the
# 15-minute global propagation — and pass it to the ECS task definition in step 14.
echo ""
echo "--- Step 13: Building and deploying Blazor Admin ---"

# --- Patch Blazor's API URL ------------------------------------------------
ADMIN_APPSETTINGS="./MyTowerRegistration.Admin/wwwroot/appsettings.json"

# Substitute the ALB DNS for the placeholder URL in wwwroot/appsettings.json.
# This file is downloaded by the browser at startup as plain text — it is NOT
# a secret. Using jq prevents breakage if the DNS name contains special chars.
jq --arg url "http://${ALB_DNS}" '.ApiBaseUrl = $url' "${ADMIN_APPSETTINGS}" \
    > "${ADMIN_APPSETTINGS}.tmp" && mv "${ADMIN_APPSETTINGS}.tmp" "${ADMIN_APPSETTINGS}"
echo "Patched ApiBaseUrl → http://${ALB_DNS}"

# --- Build and publish -----------------------------------------------------
# `dotnet publish` compiles the Blazor project in Release configuration.
# Output lands in ./publish/admin/wwwroot/ — that's the static site root.
dotnet publish ./MyTowerRegistration.Admin/MyTowerRegistration.Admin.csproj \
    --configuration Release \
    --output ./publish/admin
echo "Blazor publish complete."

# --- S3 bucket -------------------------------------------------------------
# We block all public access on the bucket — CloudFront fetches objects using
# an Origin Access Control (OAC), which signs requests with SigV4. The bucket
# is never exposed to the public internet directly.
if ! aws s3api head-bucket --bucket "${BLAZOR_BUCKET}" > /dev/null 2>&1; then
    aws s3api create-bucket \
        --bucket "${BLAZOR_BUCKET}" \
        --region "${AWS_REGION}" \
        --create-bucket-configuration LocationConstraint="${AWS_REGION}"

    aws s3api put-public-access-block \
        --bucket "${BLAZOR_BUCKET}" \
        --public-access-block-configuration \
        "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"

    echo "S3 bucket created: ${BLAZOR_BUCKET}"
else
    echo "Reusing existing S3 bucket: ${BLAZOR_BUCKET}"
fi

# Sync the published wwwroot to S3. --delete removes stale files from previous
# deploys (e.g. old versioned .wasm files that were renamed in the new build).
aws s3 sync ./publish/admin/wwwroot "s3://${BLAZOR_BUCKET}/" --delete
echo "Files uploaded to s3://${BLAZOR_BUCKET}/"

# --- CloudFront Origin Access Control (OAC) --------------------------------
# OAC is the modern replacement for the older Origin Access Identity (OAI).
# It uses IAM SigV4 to sign requests from CloudFront to S3, so S3 can verify
# that requests are from *our specific* CloudFront distribution — not any random
# one, and not direct browser requests.
EXISTING_OAC=$(aws cloudfront list-origin-access-controls \
    --query "OriginAccessControlList.Items[?Name=='mytower-admin-oac'].Id" \
    --output text 2>/dev/null || echo "")

if [ -z "${EXISTING_OAC}" ] || [ "${EXISTING_OAC}" = "None" ]; then
    OAC_ID=$(aws cloudfront create-origin-access-control \
        --origin-access-control-config \
        "Name=mytower-admin-oac,Description=OAC for MyTowerRegistration Admin S3 bucket,OriginAccessControlOriginType=s3,SigningBehavior=always,SigningProtocol=sigv4" \
        --query 'OriginAccessControl.Id' --output text)
    echo "OAC created: ${OAC_ID}"
else
    OAC_ID="${EXISTING_OAC}"
    echo "Reusing existing OAC: ${OAC_ID}"
fi

# --- CloudFront distribution -----------------------------------------------
# PriceClass_100 = US, Canada, Europe only — the cheapest tier.
# The CachePolicyId is AWS's managed "CachingOptimized" policy — it caches
# aggressively based on Cache-Control headers, which Blazor publish sets correctly.
EXISTING_CF_ID=$(aws cloudfront list-distributions \
    --query "DistributionList.Items[?Comment=='MyTowerRegistration Admin'].Id" \
    --output text 2>/dev/null || echo "")

if [ -z "${EXISTING_CF_ID}" ] || [ "${EXISTING_CF_ID}" = "None" ]; then
    CF_OUTPUT=$(aws cloudfront create-distribution --distribution-config "$(cat <<EOF
{
    "CallerReference": "mytower-admin-$(date +%s)",
    "Comment": "MyTowerRegistration Admin",
    "DefaultRootObject": "index.html",
    "Origins": {
        "Quantity": 1,
        "Items": [{
            "Id": "s3-mytower-admin",
            "DomainName": "${BLAZOR_BUCKET}.s3.${AWS_REGION}.amazonaws.com",
            "OriginAccessControlId": "${OAC_ID}",
            "S3OriginConfig": { "OriginAccessIdentity": "" }
        }]
    },
    "DefaultCacheBehavior": {
        "TargetOriginId": "s3-mytower-admin",
        "ViewerProtocolPolicy": "redirect-to-https",
        "CachePolicyId": "658327ea-f89d-4fab-a63d-7e88639e58f6",
        "AllowedMethods": {
            "Quantity": 2,
            "Items": ["HEAD", "GET"],
            "CachedMethods": { "Quantity": 2, "Items": ["HEAD", "GET"] }
        }
    },
    "CustomErrorResponses": {
        "Quantity": 1,
        "Items": [{
            "ErrorCode": 404,
            "ResponseCode": "200",
            "ResponsePagePath": "/index.html",
            "ErrorCachingMinTTL": 0
        }]
    },
    "Enabled": true,
    "PriceClass": "PriceClass_100",
    "HttpVersion": "http2"
}
EOF
    )")

    CF_ID=$(echo "${CF_OUTPUT}" | jq -r '.Distribution.Id')
    CF_DOMAIN=$(echo "${CF_OUTPUT}" | jq -r '.Distribution.DomainName')
    echo "CloudFront distribution created: ${CF_ID}"
else
    CF_ID="${EXISTING_CF_ID}"
    CF_DOMAIN=$(aws cloudfront get-distribution \
        --id "${CF_ID}" \
        --query 'Distribution.DomainName' --output text)
    echo "Reusing existing CloudFront distribution: ${CF_ID}"
fi

echo "CloudFront domain: ${CF_DOMAIN}"

# --- S3 bucket policy ------------------------------------------------------
# Allow CloudFront (authenticated via OAC/SigV4) to call s3:GetObject.
# The AWS:SourceArn condition locks this to our distribution specifically —
# without it, any CloudFront distribution could use this bucket as an origin.
CF_ARN="arn:aws:cloudfront::${AWS_ACCOUNT_ID}:distribution/${CF_ID}"

aws s3api put-bucket-policy --bucket "${BLAZOR_BUCKET}" --policy "$(jq -n \
    --arg bucket "${BLAZOR_BUCKET}" \
    --arg cf_arn "${CF_ARN}" \
    '{
        "Version": "2012-10-17",
        "Statement": [{
            "Sid": "AllowCloudFrontOAC",
            "Effect": "Allow",
            "Principal": {"Service": "cloudfront.amazonaws.com"},
            "Action": "s3:GetObject",
            "Resource": ("arn:aws:s3:::" + $bucket + "/*"),
            "Condition": {"StringEquals": {"AWS:SourceArn": $cf_arn}}
        }]
    }')"

echo "S3 bucket policy attached (CloudFront OAC only)."
echo "Note: the distribution takes ~15 min to propagate globally — CF_DOMAIN is"
echo "available immediately and used for CORS config in step 14."
echo "OK"

# =============================================================================
# STEP 14: Register the API ECS task definition
# =============================================================================
# The API task definition is similar to migrations but:
#   - Uses the runtime image (not migrations)
#   - Does NOT override the entrypoint (uses Dockerfile ENTRYPOINT as-is)
#   - Exposes port 8080
#   - Injects connection string as ConnectionStrings__DefaultConnection
#     (double underscore = nested config key in ASP.NET Core)
#
# The `secrets` block here works the same way as in step 10 — the value is
# fetched from Secrets Manager at container startup and injected as an env var.
echo ""
echo "--- Step 14: Registering API task definition ---"

# AllowedOrigins__0 injects the CORS allowed origin as an environment variable.
# ASP.NET Core reads environment variables as configuration using double underscore
# as a nesting separator: AllowedOrigins__0 → AllowedOrigins[0] in the config tree.
# This matches exactly what Program.cs reads via GetSection("AllowedOrigins").Get<string[]>().
# Using an env var instead of baking the value into appsettings.json means we can
# update CORS origins by redeploying the task definition — no Docker image rebuild needed.
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
            "value": "https://${CF_DOMAIN}"
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
# STEP 15: Create ECS service
# =============================================================================
# A service keeps desiredCount tasks running at all times. If a task crashes,
# ECS automatically launches a replacement. This is the key difference from
# `run-task` (step 11) which ran once and exited.
#
# With a load balancer attached, the service registers each task's private IP
# with the target group at startup and deregisters it on shutdown. The ALB
# health check (configured in step 12) gates whether a task receives traffic.
#
# IMPORTANT: ECS does not allow changing the load balancer config of an existing
# service — it must be set at creation. If the service was previously created
# without a load balancer (e.g. from an earlier smoke-test run of this script),
# you'll need to delete it manually before re-running:
#   aws ecs update-service --cluster mytower-cluster --service mytower-registration-api --desired-count 0
#   aws ecs delete-service --cluster mytower-cluster --service mytower-registration-api
#
# health-check-grace-period-seconds: gives the task time to start before the
# ALB starts evaluating health. Without this, ALB may mark the task unhealthy
# before ASP.NET Core has finished startup, causing an immediate replacement loop.
echo ""
echo "--- Step 15: Creating ECS service ---"

# Idempotent: update task definition if service already exists (with LB),
# otherwise create it fresh with the load balancer attached.
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
else
    echo "Service already exists, updating task definition..."
    aws ecs update-service \
        --cluster "${ECS_CLUSTER}" \
        --service "${API_SERVICE_NAME}" \
        --task-definition "${API_TASK_FAMILY}" \
        --desired-count 1 > /dev/null
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
echo "  API (via ALB):       http://${ALB_DNS}/api/graphql"
echo "  Admin UI:            https://${CF_DOMAIN}"
echo "  CloudWatch logs:     https://${AWS_REGION}.console.aws.amazon.com/cloudwatch/home?region=${AWS_REGION}#logsV2:log-groups/log-group/${LOG_GROUP//\//%2F}"
echo ""
echo "Notes:"
echo "  - ALB DNS is stable — it doesn't change when tasks restart."
echo "  - CloudFront is propagating globally (~15 min). The admin URL may return"
echo "    errors until propagation completes."
echo "  - CORS is configured: the API allows requests from https://${CF_DOMAIN}"
