#!/usr/bin/env bash
# =============================================================================
# MyTowerRegistration — AWS Infrastructure Setup
# =============================================================================
#
# Creates all AWS resources that exist independently of the application code:
# IAM, ECR, RDS, ALB, S3, CloudFront. Run this ONCE when setting up a new
# environment. It is safe to re-run — every step checks before creating.
#
# After this script succeeds, run ./scripts/deploy.sh to build and deploy
# the application for the first time (and on every subsequent code change).
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
#                   brew install --cask dotnet-sdk   (or download from Microsoft)
#   Configure AWS : aws configure   (sets access key, secret, default region)
#
# OTHER LINUX DISTROS
#   Alpine Linux uses musl libc, which is incompatible with the dotnet SDK
#   used here. Alpine is fine inside Docker containers but is not a supported
#   developer workstation for this project.
#   Arch Linux: pacman -S aws-cli docker jq dotnet-sdk
#
# =============================================================================

set -euo pipefail

# Prevent Git Bash on Windows from converting leading slashes in arguments
# to Windows paths (e.g. /ecs/foo → C:/Program Files/Git/ecs/foo).
# This is a no-op on WSL, Linux, and macOS — kept so the script stays
# runnable if someone accidentally uses Git Bash, at least for simple paths.
export MSYS_NO_PATHCONV=1

# =============================================================================
# CONFIGURATION
# All resource names and IDs live here. Change these to redeploy with
# different names or in a different account/region.
# =============================================================================

AWS_REGION="us-east-2"
AWS_ACCOUNT_ID="151935250464"

# Set so all AWS CLI calls default to the right region without needing
# --region on every command. Truly global services (IAM) ignore it.
export AWS_DEFAULT_REGION="${AWS_REGION}"

# ECR base URL: <account>.dkr.ecr.<region>.amazonaws.com
ECR_BASE="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

ECR_API_REPO="mytower-registration-api"
ECR_MIGRATIONS_REPO="mytower-registration-migrations"

# ECS cluster — already exists from the Python project
ECS_CLUSTER="mytower-cluster"

# Security group assigned to ECS tasks — already exists, allows inbound 8000
ECS_SG_ID="sg-05354e42eaaf4662d"

# Subnets in the default VPC — one per availability zone
SUBNETS_ARRAY=(
    "subnet-0521349f1295ef589"   # us-east-2a
    "subnet-0b71feaecab5f9d97"   # us-east-2b
    "subnet-0c2e0b3fdb97e61f4"   # us-east-2c
)

# IAM role ECS uses to pull images, write logs, and read secrets
EXECUTION_ROLE_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:role/ecsTaskExecutionRole"

# RDS
RDS_INSTANCE_ID="mytower-registration-db"
RDS_SUBNET_GROUP="mytower-registration-subnet-group"
RDS_SG_NAME="mytower-registration-rds-sg"
DB_NAME="mytower_registration"
DB_USERNAME="postgres"

# Secrets Manager
SECRET_NAME="mytower-registration/db-connection-string"

# CloudWatch
LOG_GROUP="/ecs/mytower-registration"

# Load balancer
ALB_NAME="mytower-registration-alb"
ALB_SG_NAME="mytower-registration-alb-sg"
TG_NAME="mytower-registration-tg"
API_PORT=8080

# Health check path stored as a variable rather than a literal string to prevent
# Git Bash from converting the leading slash to a Windows path.
HEALTH_CHECK_PATH="/api/graphql/"

# S3 + CloudFront (Blazor admin frontend)
BLAZOR_BUCKET="mytower-registration-admin"

echo "=== MyTowerRegistration Infrastructure Setup ==="
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
# This policy gives ECS the key — but only for our specific secrets.
# The Resource ARN pattern uses a wildcard to cover the secret's random suffix.
echo "--- Step 1: Granting Secrets Manager read access to ecsTaskExecutionRole ---"

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
# ECR is AWS's private image registry — like Docker Hub, but private and
# integrated with IAM.
#
# Two repositories:
#   mytower-registration-api        — the .NET runtime image (runs the API)
#   mytower-registration-migrations — the migration bundle image (runs once)
#
# The || true ignores errors if the repo already exists.
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
# STEP 3: Create the DB secret in Secrets Manager
# =============================================================================
# Secrets Manager stores sensitive values encrypted at rest. ECS injects them
# as environment variables at container startup — the value never appears in
# task definition config, CloudWatch logs, or the AWS console in plaintext.
#
# The secret is stored as JSON: {"connectionString": "Host=...;Password=..."}
# The JSON wrapper allows adding more fields later (e.g. a read-replica URL).
#
# We store a placeholder now and update it with the real connection string
# once we know the RDS endpoint (after step 5).
echo ""
echo "--- Step 3: Creating DB secret in Secrets Manager ---"

# If the secret already has a real connection string from a previous run,
# skip the password prompt and all subsequent secret-related work.
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
    echo "Connection string already set — skipping password prompt."
    echo "Secret ARN: ${SECRET_ARN}"
else
    echo -n "Enter the RDS Postgres password: "
    read -r DB_PASSWORD
    echo ""

    # Store a placeholder now; step 5 fills in the real connection string.
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
# STEP 4: Create the RDS security group
# =============================================================================
# Security groups are stateful firewalls. We create a dedicated one for RDS
# that allows port 5432 (Postgres) only from the ECS security group — not
# from the public internet.
#
# Using --source-group instead of --cidr is more robust: it matches any
# resource tagged with the ECS SG ID, regardless of IP address. ECS task IPs
# change on every restart; the security group ID stays the same.
echo ""
echo "--- Step 4: Creating RDS security group ---"

VPC_ID=$(aws ec2 describe-security-groups \
    --group-ids "${ECS_SG_ID}" \
    --query 'SecurityGroups[0].VpcId' \
    --output text)

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
# STEP 5: Create RDS subnet group and RDS instance
# =============================================================================
# An RDS subnet group tells RDS which subnets it can use. Including all 3 AZs
# is required by AWS even for a single-AZ instance — it enables a future
# Multi-AZ upgrade (automatic failover) without recreation.
#
# Free tier settings:
#   db.t3.micro = 2 vCPU burst, 1 GB RAM, free for 750 hrs/month
#   allocated-storage 20 = 20 GB gp2, free tier limit
#   --no-publicly-accessible = only reachable from within the VPC
#   --no-multi-az = single AZ (multi-AZ costs extra)
#
# RDS takes 5-10 minutes to initialize. The wait command polls every 30s.
echo ""
echo "--- Step 5: Creating RDS subnet group ---"

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

RDS_ENDPOINT=$(aws rds describe-db-instances \
    --db-instance-identifier "${RDS_INSTANCE_ID}" \
    --query 'DBInstances[0].Endpoint.Address' \
    --output text)

echo "RDS ready: ${RDS_ENDPOINT}"

# =============================================================================
# STEP 6: Update the secret with the full connection string
# =============================================================================
# Now that we know the RDS endpoint, build the full Npgsql connection string
# and store it. ECS will inject this as ConnectionStrings__DefaultConnection
# (double underscore = nested config key in ASP.NET Core).
#
# Trust Server Certificate=true skips CA chain validation. Traffic is still
# encrypted in transit, but the server cert isn't verified — a MITM inside
# the VPC could theoretically intercept it. Acceptable for a first deployment;
# the proper fix is to ship the AWS RDS CA bundle in the container image.
# See: https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.SSL.html
echo ""
echo "--- Step 6: Updating secret with full connection string ---"

if [ "${SKIP_SECRET_UPDATE}" = "true" ]; then
    echo "Skipping — connection string already set."
else
    CONNECTION_STRING="Host=${RDS_ENDPOINT};Port=5432;Database=${DB_NAME};Username=${DB_USERNAME};Password=${DB_PASSWORD};SSL Mode=Require;Trust Server Certificate=true"

    # Use jq to build the JSON — direct string interpolation breaks if the
    # password contains quotes, backslashes, or other special characters.
    SECRET_JSON=$(jq -n --arg cs "${CONNECTION_STRING}" '{connectionString:$cs}')

    aws secretsmanager update-secret \
        --secret-id "${SECRET_NAME}" \
        --secret-string "${SECRET_JSON}"

    # Clear plaintext password from memory
    unset DB_PASSWORD
    unset CONNECTION_STRING
    unset SECRET_JSON
fi

echo "OK"

# =============================================================================
# STEP 7: Create CloudWatch log group
# =============================================================================
# Container logs (stdout/stderr) are sent to CloudWatch via the awslogs driver.
# The standard ecsTaskExecutionRole can create log streams but not log groups,
# so we create the group explicitly here.
echo ""
echo "--- Step 7: Creating CloudWatch log group ---"

aws logs create-log-group \
    --log-group-name "${LOG_GROUP}" > /dev/null 2>&1 || true

echo "OK"

# =============================================================================
# STEP 8: Create Application Load Balancer
# =============================================================================
# An ALB gives the API a stable DNS hostname that doesn't change when tasks
# restart. Three parts:
#   1. ALB itself     — receives traffic on port 80
#   2. Target group   — pool of ECS task IPs that get the traffic
#   3. Listener       — rule: "port 80 → forward to target group"
#
# Security model: the ALB gets its own security group that accepts port 80
# from the internet. The ECS security group is locked to "from ALB SG only"
# — ECS tasks are unreachable directly from the internet.
#
# Note: the old deploy-aws.sh had a Step 6 that briefly opened port 8080 to
# 0.0.0.0/0 for smoke-testing before the ALB existed. That step is omitted
# here because setup-infra.sh creates the ALB in the same run — there is no
# smoke-test phase. The ECS SG goes directly to ALB-only access.
echo ""
echo "--- Step 8: Creating Application Load Balancer ---"

# Recapture VPC_ID in case this step is re-run in isolation
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

# --- Lock ECS security group to ALB-only access ----------------------------
# Revoke any open-internet rule on API_PORT that may exist from a prior run
# of the old deploy-aws.sh (which had the smoke-test step). No-op if absent.
aws ec2 revoke-security-group-ingress \
    --group-id "${ECS_SG_ID}" \
    --protocol tcp --port "${API_PORT}" \
    --cidr "0.0.0.0/0" > /dev/null 2>&1 || true

aws ec2 authorize-security-group-ingress \
    --group-id "${ECS_SG_ID}" \
    --protocol tcp \
    --port "${API_PORT}" \
    --source-group "${ALB_SG_ID}" > /dev/null 2>&1 || true

echo "ECS security group: port ${API_PORT} restricted to ALB SG only."

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
# target-type=ip is required for Fargate (awsvpc mode): each task gets its
# own private IP. Unlike EC2, you register IPs not instance IDs.
#
# Health check: ALB polls /api/graphql on each task.
# Hot Chocolate returns 200 for GET requests (serves Banana Cake Pop UI).
#   interval 30s     — poll every 30s
#   healthy 2        — 2 consecutive 200s → task enters service (~60s)
#   unhealthy 3      — 3 consecutive failures → task pulled (~90s)
EXISTING_TG_ARN=$(aws elbv2 describe-target-groups \
    --names "${TG_NAME}" \
    --query 'TargetGroups[0].TargetGroupArn' \
    --output text 2>/dev/null || echo "None")

if [ "${EXISTING_TG_ARN}" = "None" ] || [ -z "${EXISTING_TG_ARN}" ]; then
    # Use --cli-input-json + heredoc to avoid Git Bash path conversion on
    # the health check path. JSON strings inside heredocs are immune.
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
# One rule: all traffic on port 80 forwarded to the target group.
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
# STEP 9: Create S3 bucket and CloudFront distribution for Blazor Admin
# =============================================================================
# Blazor WASM compiles to a bundle of static files (HTML, CSS, JS, .wasm).
# There is no server — the browser downloads everything once and runs the
# app locally in WASM. We host on S3 and serve via CloudFront (AWS CDN).
#
# SPA routing: if a user bookmarks /admin/users, their browser requests that
# path from S3. S3 returns 404 — no file at that path exists. We configure
# a CloudFront custom error response: 404 → /index.html (HTTP 200). Blazor's
# client-side Router picks up the URL and navigates correctly.
#
# S3 public access is blocked — CloudFront fetches objects using an Origin
# Access Control (OAC), which signs requests with SigV4. The bucket is never
# exposed to the public internet directly.
echo ""
echo "--- Step 9: Creating S3 bucket and CloudFront distribution ---"

# --- S3 bucket -------------------------------------------------------------
if ! aws s3api head-bucket --bucket "${BLAZOR_BUCKET}" > /dev/null 2>&1; then
    # us-east-1 must omit --create-bucket-configuration; all other regions
    # require it. AWS rejects the parameter in us-east-1 with MalformedXML.
    if [ "${AWS_REGION}" = "us-east-1" ]; then
        aws s3api create-bucket \
            --bucket "${BLAZOR_BUCKET}" \
            --region "${AWS_REGION}"
    else
        aws s3api create-bucket \
            --bucket "${BLAZOR_BUCKET}" \
            --region "${AWS_REGION}" \
            --create-bucket-configuration LocationConstraint="${AWS_REGION}"
    fi

    aws s3api put-public-access-block \
        --bucket "${BLAZOR_BUCKET}" \
        --public-access-block-configuration \
        "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"

    echo "S3 bucket created: ${BLAZOR_BUCKET}"
else
    echo "Reusing existing S3 bucket: ${BLAZOR_BUCKET}"
fi

# --- CloudFront Origin Access Control (OAC) --------------------------------
# OAC is the modern replacement for Origin Access Identity (OAI). It uses
# IAM SigV4 to sign requests from CloudFront to S3, so S3 can verify that
# requests are from our specific distribution — not any random CloudFront
# distribution, and not direct browser requests.
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
# PriceClass_100 = US, Canada, Europe only — cheapest tier.
# CachePolicyId 658327ea... = AWS managed "CachingOptimized" policy.
# read returns space-separated IDs if the Comment matches multiple
# distributions (e.g. from a failed prior run). Take only the first.
EXISTING_CF_ID=$(aws cloudfront list-distributions \
    --query "DistributionList.Items[?Comment=='MyTowerRegistration Admin'].Id" \
    --output text 2>/dev/null | awk '{print $1}' || echo "")

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
echo "OK"

# =============================================================================
# DONE
# =============================================================================
echo ""
echo "=== Infrastructure setup complete! ==="
echo ""
echo "  ALB DNS:         http://${ALB_DNS}"
echo "  CloudFront:      https://${CF_DOMAIN}"
echo "  RDS endpoint:    ${RDS_ENDPOINT}"
echo ""
echo "Next step: run ./scripts/deploy.sh to build images and deploy the"
echo "application for the first time."
echo ""
echo "Note: CloudFront takes ~15 minutes to propagate globally."
echo "The admin URL will return errors until propagation completes."
