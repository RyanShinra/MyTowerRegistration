# Domain Setup Runbook — mytower.dev

This document records the one-time steps to connect `mytower.dev` to the AWS infrastructure.
It is a companion to [AWS_CONSOLE_GUIDE.md](AWS_CONSOLE_GUIDE.md).

---

## Current State (as of 2026-04-09)

| Step | Status |
|---|---|
| ACM wildcard cert `*.mytower.dev` + `mytower.dev` (us-east-1, for CloudFront) | **Done — Issued (free, non-exportable)** |
| ACM wildcard cert `*.mytower.dev` + `mytower.dev` (us-east-2, for ALB) | **Done — Issued (free, non-exportable)** |
| Namecheap DNS validation CNAME | **Done** |
| ALB HTTPS listener + HTTP→HTTPS redirect | **Done** |
| CloudFront alternate domain + cert | **Done** |
| Namecheap: `admin-api.mytower.dev` CNAME → ALB | **Done** |
| Namecheap: `admin.mytower.dev` CNAME → CloudFront | **Done** |
| `deploy.sh` constants update | **Done** |

---

## Known Resource Values

| Resource | Value |
|---|---|
| ACM cert ARN | `arn:aws:acm:us-east-1:151935250464:certificate/07a46b81-bf79-4c1d-8319-92f135aa8d4f` |
| ALB DNS name | `mytower-registration-alb-90596354.us-east-2.elb.amazonaws.com` |
| CloudFront domain | `dlkzg304jfbpr.cloudfront.net` |
| CloudFront distribution ID | `E20OTOLXT2QXNM` |

---

## Step 1 — ACM Certificate ✅ Done

**Certificate:** `*.mytower.dev` + `mytower.dev`
**Region:** us-east-1 (required for CloudFront)
**Status:** Issued

Validation CNAME added to Namecheap:
- Host: `_b6768b9600bdb4e6874bc1d9ae18acff`
- Value: `_bbffb0456f34b2fa855f66462d5c74f5.jkddzztszm.acm-validations.aws`

---

## Step 2 — ALB: Add HTTPS Listener + HTTP Redirect

**Console path:** EC2 → Load Balancers → `mytower-registration-alb` → Listeners tab

### 2a. Add HTTPS listener (port 443)

1. Click **Add listener**
2. Protocol: **HTTPS**, Port: **443**
3. Default action: **Forward** → target group `mytower-registration-tg`
4. SSL/TLS certificate: choose **From ACM** → select `*.mytower.dev`
5. Click **Add**

### 2b. Open port 443 in the ALB security group

The ALB's security group needs an inbound rule for HTTPS traffic. This is safe — an
internet-facing ALB is meant to accept traffic from anywhere; the ECS tasks' security
group only allows traffic *from the ALB security group*, not from the internet directly.

**Console path:** EC2 → Security Groups → select the security group attached to `mytower-registration-alb` → **Inbound rules** → **Edit inbound rules**

Add a rule:
- Type: **HTTPS**
- Port: **443**
- Source: **0.0.0.0/0** (add `::/0` as a second source for IPv6)

Click **Save rules**. The "Listener port unreachable" warning on the ALB should clear.

### 2c. Change HTTP listener to redirect

The existing port 80 listener currently forwards traffic. Change it to redirect:

1. Click on the **HTTP:80** listener → **Edit**
2. Default action: remove the forward rule, add **Redirect**
3. Protocol: **HTTPS**, Port: **443**, Status code: **301**
4. Click **Save**

---

## Step 3 — CloudFront: Add Alternate Domain + Certificate

**Console path:** CloudFront → Distributions → `E20OTOLXT2QXNM` → General tab → Edit

1. Under **Alternate domain names (CNAMEs)**, click **Add item**
2. Enter: `admin.mytower.dev`
3. Under **Custom SSL certificate**, select `*.mytower.dev` (from ACM us-east-1)
4. Click **Save changes**

CloudFront will deploy the update — takes 5–10 minutes. Status changes from
"In Progress" back to "Deployed".

---

## Step 4 — Namecheap: Add DNS Records

**Console path:** Namecheap → Domain List → `mytower.dev` → Manage → Advanced DNS

Add two CNAME records:

| Type | Host | Value | TTL |
|---|---|---|---|
| CNAME | `admin-api` | `mytower-registration-alb-90596354.us-east-2.elb.amazonaws.com` | Automatic |
| CNAME | `admin` | `dlkzg304jfbpr.cloudfront.net` | Automatic |

DNS propagation is usually fast (seconds to minutes) but can take up to an hour.

**Test with:**
```bash
dig admin-api.mytower.dev
dig admin.mytower.dev
```

Or use [dnschecker.org](https://dnschecker.org) to check from multiple locations.

---

## Step 5 — Update deploy.sh

Once DNS is working and HTTPS is confirmed on both subdomains, update
`scripts/deploy.sh` to replace the dynamic CloudFront lookup with hardcoded constants.

Find the block that reads:
```bash
# Once mytower.dev DNS is configured, these become stable constants:
#   ADMIN_ORIGIN="https://admin.mytower.dev"
#   API_BASE_URL="https://admin-api.mytower.dev"
```

And replace the dynamic lookup lines above it with:
```bash
ADMIN_ORIGIN="https://admin.mytower.dev"
API_BASE_URL="https://admin-api.mytower.dev"
```

Remove the dynamic CF_DOMAIN lookup lines — they're no longer needed.

---

## Verification Checklist

After all steps:

- [ ] `https://admin-api.mytower.dev/api/graphql` loads the Nitro playground
- [ ] `https://admin.mytower.dev` loads the Blazor admin app
- [ ] `http://admin-api.mytower.dev` redirects to `https://` (ALB redirect)
- [ ] `http://admin.mytower.dev` redirects to `https://` (CloudFront enforces HTTPS)
- [ ] No mixed-content warnings in browser DevTools

---

## Context / Why These Choices

**Namecheap DNS instead of Route 53:**
Route 53 costs ~$0.50/month per hosted zone. Since we're only pointing subdomains
(not the zone apex), CNAME records work fine and Namecheap DNS is free. Route 53
ALIAS records are only required for bare `mytower.dev` (no subdomain), which we
don't need yet.

**Wildcard cert `*.mytower.dev`:**
Covers any single-level subdomain (`api`, `admin`, `io`, `game`, `www`, etc.).
Adding future subdomains requires only a Namecheap CNAME — no new cert needed.
The separate `mytower.dev` entry covers the bare domain if needed later.

**ACM cert must be in us-east-1:**
CloudFront is a global service that only reads ACM certs from us-east-1, regardless
of where your other AWS resources live. The ALB (in us-east-2) can use a cert from
us-east-2, but for simplicity the same us-east-1 cert covers both.
