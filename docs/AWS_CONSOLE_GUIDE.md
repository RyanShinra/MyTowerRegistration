# AWS Console Guide — Infrastructure Setup

This is a companion to [scripts/setup-infra.sh](../scripts/setup-infra.sh).
It describes how to do the same setup steps in the AWS Console, which is
often easier for one-time work: the wizards validate your inputs, error messages
are in plain English, and you can see what already exists before creating anything.

## When to use the script vs the Console

| Situation | Use |
|---|---|
| First-time environment setup | Either — Console is more visual; script is repeatable |
| Rebuilding from scratch (new account, new region) | Script — all 9 steps in one run |
| Checking on existing resources | Console — it's a much better browser |
| Diagnosing problems (unhealthy targets, task failures) | Console — logs, events, and health status are visual |
| Every code deploy | Script (`deploy.sh`) — automation is the whole point |

**The one place the script beats the Console even for one-time work:** steps that
chain outputs. The RDS endpoint (step 5) feeds the connection string (step 6),
which feeds the ECS task definition (in `deploy.sh`). In the Console you'd be
copying values between tabs. The script captures them in variables automatically.

---

## Step 1 — IAM: Grant ECS access to Secrets Manager

**Console path:** IAM → Roles → `ecsTaskExecutionRole` → Add permissions → Create inline policy

What to do:
1. Search for **IAM** in the AWS search bar.
2. In the left nav, choose **Roles**.
3. Search for and click **ecsTaskExecutionRole**.
4. Choose **Add permissions → Create inline policy**.
5. Switch to the **JSON** tab and paste:
   ```json
   {
     "Version": "2012-10-17",
     "Statement": [{
       "Effect": "Allow",
       "Action": ["secretsmanager:GetSecretValue"],
       "Resource": "arn:aws:secretsmanager:us-east-2:<YOUR_ACCOUNT_ID>:secret:mytower-registration/db-connection-string*"
     }]
   }
   ```
6. Name the policy `SecretsManagerReadForMyTowerRegistration`.

**Why this exists:** The default `ecsTaskExecutionRole` only covers ECR (pulling
images) and CloudWatch (writing logs). Secrets Manager access must be added
explicitly, scoped to just this secret.

---

## Step 2 — ECR: Create image repositories

**Console path:** Elastic Container Registry → Repositories → Create repository

Create two private repositories:
- `mytower-registration-api`
- `mytower-registration-migrations`

Settings: Private, all defaults are fine. Mutable tags (the default) lets
`deploy.sh` overwrite `:latest` on each push.

---

## Step 3 — Secrets Manager: Create the DB secret

**Console path:** Secrets Manager → Store a new secret

1. Choose **Other type of secret**.
2. Add a key/value pair: key = `connectionString`, value = `placeholder`
   (the real value is filled in after RDS is created in step 5).
3. Name the secret: `mytower-registration/db-connection-string`
4. Leave rotation disabled.

Save the **Secret ARN** — you'll need it when configuring the ECS task
definitions in `deploy.sh`.

---

## Step 4 — EC2: Create the RDS security group

**Console path:** EC2 → Security Groups → Create security group

1. Name: `mytower-registration-rds-sg`
2. VPC: choose the same VPC as the ECS security group (`sg-05354e42eaaf4662d`)
3. Inbound rule:
   - Type: **PostgreSQL** (port 5432)
   - Source: choose **Custom** and type the ECS security group ID (`sg-05354e42eaaf4662d`)

**Why source-group instead of an IP range:** ECS task IPs change on every
restart. Referencing the security group ID matches all resources tagged with
it, regardless of IP — so the rule stays correct automatically.

---

## Step 5 — RDS: Create the Postgres instance

**Console path:** RDS → Create database

Key settings:
- Engine: **PostgreSQL**, version 16
- Template: **Free tier** (auto-sets db.t3.micro, 20 GB, single-AZ, no backups)
- DB instance identifier: `mytower-registration-db`
- Master username: `postgres`
- Master password: choose something strong and save it
- DB name: `mytower_registration`
- VPC security group: select `mytower-registration-rds-sg` (created in step 4)
- Public access: **No**

The Console's free-tier template is particularly nice here — it pre-fills
the right instance class and storage settings and flags anything that would
incur charges.

RDS takes 5–10 minutes to initialize. Wait until the status shows **Available**
before proceeding. Then copy the **Endpoint** from the Connectivity tab —
you need it for step 6.

---

## Step 6 — Secrets Manager: Update with the real connection string

**Console path:** Secrets Manager → `mytower-registration/db-connection-string` → Retrieve secret value → Edit

Replace the `placeholder` value with:
```
Host=<RDS_ENDPOINT>;Port=5432;Database=mytower_registration;Username=postgres;Password=<YOUR_PASSWORD>;SSL Mode=Require;Trust Server Certificate=true
```

Then store it as JSON: `{"connectionString": "<the string above>"}`.

You can use **Edit → JSON** view in the Console to paste the whole JSON object.

---

## Step 7 — CloudWatch: Create the log group

**Console path:** CloudWatch → Log groups → Create log group

- Name: `/ecs/mytower-registration`
- Retention: 30 days is a sensible default (the free tier includes 5 GB/month).

All container logs flow here, prefixed by `api/` or `migrations/`.

---

## Step 8 — ALB: Create the load balancer

This step has three parts (security group, ALB, target group + listener) and
is the most involved Console workflow. The wizard guides you through them.

**Console path:** EC2 → Load Balancers → Create load balancer → Application Load Balancer

### 8a. ALB security group (create separately first)

**Console path:** EC2 → Security Groups → Create security group

- Name: `mytower-registration-alb-sg`
- VPC: same as ECS
- Inbound rules:
  - HTTP (port 80) from `0.0.0.0/0` (and `::/0` for IPv6 if desired)
  - HTTPS (port 443) from `0.0.0.0/0` ← add this even now; you'll use it for the domain

> **Note:** `setup-infra.sh` intentionally omits port 443 from the ALB security group
> because the HTTPS listener doesn't exist yet at infra-setup time. If you used the
> script instead of this Console guide, port 443 is added later in
> [DOMAIN_SETUP.md step 2b](DOMAIN_SETUP.md#step-2--alb-add-https-listener--http-redirect).

### 8b. Create the ALB

- Name: `mytower-registration-alb`
- Scheme: **Internet-facing**
- IP address type: IPv4
- VPC: select all three subnets (us-east-2a, 2b, 2c)
- Security group: `mytower-registration-alb-sg` (created above)

### 8c. Target group

In the ALB creation wizard, under **Listeners and routing**, create a new target group:
- Target type: **IP addresses** (required for Fargate)
- Name: `mytower-registration-tg`
- Protocol: HTTP, Port: 8080
- Health check path: `/api/graphql/`
- Healthy threshold: 2 checks
- Interval: 30 seconds

### 8d. Tighten the ECS security group

After the ALB is created, restrict ECS so it only accepts traffic from the ALB:

**Console path:** EC2 → Security Groups → `sg-05354e42eaaf4662d` (ECS SG) → Inbound rules → Edit

- Remove any existing rule for port 8080 from `0.0.0.0/0`
- Add: Custom TCP, port 8080, source = `mytower-registration-alb-sg`

---

## Step 9 — S3 + CloudFront: Host the Blazor admin frontend

### 9a. S3 bucket

**Console path:** S3 → Create bucket

- Name: `mytower-registration-admin`
- Region: us-east-2
- Block all public access: **Yes** (all four checkboxes on)
- All other defaults

### 9b. CloudFront Origin Access Control

**Console path:** CloudFront → Origin access → Create control setting

- Name: `mytower-admin-oac`
- Origin type: S3
- Signing behavior: Sign requests (recommended)
- Signing protocol: SigV4

### 9c. CloudFront distribution

**Console path:** CloudFront → Create distribution

Key settings:
- Origin domain: `mytower-registration-admin.s3.us-east-2.amazonaws.com`
- Origin access: choose the OAC created above
- Default root object: `index.html`
- Viewer protocol policy: **Redirect HTTP to HTTPS**
- Cache policy: **CachingOptimized** (AWS managed)
- Price class: **Use only North America and Europe** (cheapest)
- Custom error responses: add one — 404 → `/index.html`, response code 200
  (this enables Blazor client-side routing for deep links)

After creation, copy the **Distribution domain name** (e.g. `d1abc.cloudfront.net`).

### 9d. S3 bucket policy

After the CloudFront distribution is created, go back to the S3 bucket and
attach the bucket policy. CloudFront may offer to do this automatically after
step 9c — accept that if prompted, otherwise:

**Console path:** S3 → `mytower-registration-admin` → Permissions → Bucket policy

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "AllowCloudFrontOAC",
    "Effect": "Allow",
    "Principal": {"Service": "cloudfront.amazonaws.com"},
    "Action": "s3:GetObject",
    "Resource": "arn:aws:s3:::mytower-registration-admin/*",
    "Condition": {
      "StringEquals": {
        "AWS:SourceArn": "arn:aws:cloudfront::<YOUR_ACCOUNT_ID>:distribution/<YOUR_CF_DISTRIBUTION_ID>"
      }
    }
  }]
}
```

---

## Domain setup (mytower.dev) — Console only

These steps have no equivalent in `setup-infra.sh` because they are truly
one-time and involve external services (Namecheap, ACM).

See the separate [domain setup runbook](DOMAIN_SETUP.md) for:

1. ACM wildcard certificate (`*.mytower.dev`) — free, non-exportable, **must be in us-east-1 for CloudFront** and us-east-2 for ALB
2. ALB HTTPS listener + HTTP→HTTPS redirect + open port 443 in ALB security group
3. CloudFront alternate domain + certificate attachment
4. Namecheap CNAME records pointing `admin-api` → ALB and `admin` → CloudFront

Note: Route 53 is **not used**. CNAME records on Namecheap DNS point directly
to the ALB and CloudFront hostnames. Route 53 ALIAS records would only be
needed for the bare apex domain (`mytower.dev` with no subdomain), which is
not currently in use.

---

## Diagnosing problems in the Console

The Console is the right tool for troubleshooting. Key places to look:

| Problem | Where to look |
|---|---|
| API not responding | EC2 → Load Balancers → target group → **Targets** tab (health status) |
| Task crashing on startup | ECS → Cluster → Service → **Events** tab |
| Container logs | CloudWatch → Log groups → `/ecs/mytower-registration` |
| CORS errors | Network tab in browser DevTools; check `AllowedOrigins__0` in the ECS task definition |
| Blazor serving stale files | CloudFront → Distributions → **Invalidations** tab |
| RDS connection refused | Check RDS SG inbound rules and ECS SG outbound rules |
