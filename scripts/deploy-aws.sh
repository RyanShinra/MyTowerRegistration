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
#   - Run from the repo root: ./scripts/deploy-aws.sh
#
# WHAT THIS SCRIPT DOES (in order):
#   1.  Grants the ECS execution role permission to read Secrets Manager
#   2.  Creates ECR repositories for API + migrations images
#   3.  Builds and pushes Docker images to ECR
#   4.  Prompts for a DB password and stores it in Secrets Manager
#   5.  Creates a security group for RDS (locked to ECS traffic only)
#   6.  Opens port 8080 on the existing ECS security group
#   7.  Creates an RDS subnet group and the RDS Postgres instance
#   8.  Updates the secret with the full connection string once RDS is up
#   9.  Creates a CloudWatch log group for container logs
#   10. Registers an ECS task definition for migrations
#   11. Runs migrations as a one-off ECS task and waits for it to finish
#   12. Registers an ECS task definition for the API
#   13. Creates an ECS service to keep the API running
#   14. Prints the public IP of the running task
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
# Comma-separated for --network-configuration flags
SUBNETS_CSV=$(IFS=,; echo "${SUBNETS_ARRAY[*]}")
# Space-separated for --subnet-ids (RDS subnet group)
SUBNETS_SPACE="${SUBNETS_ARRAY[*]}"

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

# Database settings
DB_NAME="mytower_registration"
DB_USERNAME="postgres"

# Port the .NET API listens on (matches Dockerfile EXPOSE and Program.cs MapGraphQL)
API_PORT=8080

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

aws iam put-role-policy \
    --role-name ecsTaskExecutionRole \
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
docker build --target runtime \
    -t "${ECR_BASE}/${ECR_API_REPO}:latest" \
    .
docker push "${ECR_BASE}/${ECR_API_REPO}:latest"

echo "--- Building and pushing migrations image (dbMigrations stage) ---"
docker build --target dbMigrations \
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
echo -n "Choose a password for the RDS Postgres database: "
read -s DB_PASSWORD
echo ""

# We store a placeholder now and update with the full connection string in step 8,
# once we know the RDS endpoint. The secret ARN is captured for later use.
SECRET_ARN=$(aws secretsmanager create-secret \
    --name "${SECRET_NAME}" \
    --description "DB connection string for MyTowerRegistration API" \
    --secret-string "{\"connectionString\":\"placeholder\"}" \
    --query 'ARN' --output text)

echo "Secret ARN: ${SECRET_ARN}"

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

RDS_SG_ID=$(aws ec2 create-security-group \
    --group-name "${RDS_SG_NAME}" \
    --description "Allow Postgres from MyTowerRegistration ECS tasks only" \
    --query 'GroupId' --output text)

aws ec2 authorize-security-group-ingress \
    --group-id "${RDS_SG_ID}" \
    --protocol tcp \
    --port 5432 \
    --source-group "${ECS_SG_ID}"

echo "RDS security group created: ${RDS_SG_ID}"

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

aws rds create-db-subnet-group \
    --db-subnet-group-name "${RDS_SUBNET_GROUP}" \
    --db-subnet-group-description "Subnets for MyTowerRegistration RDS" \
    --subnet-ids ${SUBNETS_SPACE}

echo "--- Creating RDS Postgres instance (this takes 5-10 minutes) ---"

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

CONNECTION_STRING="Host=${RDS_ENDPOINT};Port=5432;Database=${DB_NAME};Username=${DB_USERNAME};Password=${DB_PASSWORD}"

aws secretsmanager update-secret \
    --secret-id "${SECRET_NAME}" \
    --secret-string "{\"connectionString\":\"${CONNECTION_STRING}\"}"

# Clear the password from memory — we no longer need it
unset DB_PASSWORD

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

MIGRATION_TASK_ARN=$(aws ecs run-task \
    --cluster "${ECS_CLUSTER}" \
    --task-definition "${MIGRATIONS_TASK_FAMILY}" \
    --launch-type FARGATE \
    --network-configuration "awsvpcConfiguration={subnets=[${SUBNETS_ARRAY[0]}],securityGroups=[${ECS_SG_ID}],assignPublicIp=ENABLED}" \
    --query 'tasks[0].taskArn' \
    --output text)

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
# STEP 12: Register the API ECS task definition
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
echo "--- Step 12: Registering API task definition ---"

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
# STEP 13: Create ECS service
# =============================================================================
# A service keeps desiredCount tasks running at all times. If a task crashes,
# ECS automatically launches a replacement. This is the key difference from
# `run-task` (step 11) which ran once and exited.
#
# For this first deployment we use a single task in a public subnet with a
# public IP — no load balancer. The IP will change if the task restarts,
# but it's sufficient for smoke testing.
#
# `aws ecs wait services-stable` polls until the service reaches steady state
# (at least 1 running task that passes health checks).
echo ""
echo "--- Step 13: Creating ECS service ---"

aws ecs create-service \
    --cluster "${ECS_CLUSTER}" \
    --service-name "${API_SERVICE_NAME}" \
    --task-definition "${API_TASK_FAMILY}" \
    --desired-count 1 \
    --launch-type FARGATE \
    --network-configuration "awsvpcConfiguration={subnets=[${SUBNETS_ARRAY[0]}],securityGroups=[${ECS_SG_ID}],assignPublicIp=ENABLED}" > /dev/null

echo "Waiting for service to reach steady state..."

aws ecs wait services-stable \
    --cluster "${ECS_CLUSTER}" \
    --services "${API_SERVICE_NAME}"

echo "OK"

# =============================================================================
# DONE — discover and print the public IP
# =============================================================================
# The running task's public IP is assigned to its elastic network interface (ENI).
# We chain three describe calls to find it:
#   list-tasks → task ARN
#   describe-tasks → ENI ID (from the task's network attachment)
#   describe-network-interfaces → public IP of the ENI
echo ""
echo "=== Deployment complete! ==="

TASK_ARN=$(aws ecs list-tasks \
    --cluster "${ECS_CLUSTER}" \
    --service-name "${API_SERVICE_NAME}" \
    --query 'taskArns[0]' --output text)

ENI_ID=$(aws ecs describe-tasks \
    --cluster "${ECS_CLUSTER}" \
    --tasks "${TASK_ARN}" \
    --query 'tasks[0].attachments[0].details[?name==`networkInterfaceId`].value' \
    --output text)

PUBLIC_IP=$(aws ec2 describe-network-interfaces \
    --network-interface-ids "${ENI_ID}" \
    --query 'NetworkInterfaces[0].Association.PublicIp' \
    --output text)

echo ""
echo "  GraphQL playground: http://${PUBLIC_IP}:${API_PORT}/api/graphql"
echo "  CloudWatch logs:    https://${AWS_REGION}.console.aws.amazon.com/cloudwatch/home?region=${AWS_REGION}#logsV2:log-groups/log-group/${LOG_GROUP//\//%2F}"
echo ""
echo "Note: The public IP changes if the task restarts. A load balancer gives a stable endpoint."
